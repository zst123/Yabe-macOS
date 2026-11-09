using System;
using System.Collections.Generic;
using System.IO;
using System.IO.BACnet;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Mstp.BacnetCapture;

namespace Yabe.MstpCapture
{
    /// <summary>Snapshot of one MS/TP station's statistics (original BacnetNode).</summary>
    public sealed class MstpNodeStat
    {
        public byte Num;
        public int TotalFrames;
        public int MeanTimeTokenRotation;   // ms
        public int[] FrameTypeStatistic;    // 10 entries, see SnifferDisplayBacnetMstpFrameTypes
    }

    /// <summary>
    /// Non-UI port of Mstp.BacnetCapture/BacnetCapture.cs: runs the MS/TP transport in sniffer mode, keeps the
    /// per-station statistics (BacnetNode) and forwards the raw frames to Wireshark (pcap, DLT 165).
    /// </summary>
    public sealed class MstpCaptureSession : IDisposable
    {
        public const string DefaultPipePath = "/tmp/bacnet";

        // Same numbering as SnifferDisplayBacnetMstpFrameTypes of the original
        public static readonly string[] FrameTypeNames =
        {
            "TOKEN", "POLL_FOR_MASTER", "REPLY_TO_POLL_FOR_MASTER", "TEST_REQUEST", "TEST_RESPONSE",
            "DATA_EXPECTING_REPLY", "DATA_NOT_EXPECTING_REPLY", "REPLY_POSTPONED", "UNKNOW_FRAME_TYPE", "PROPRIETARY_FRAME"
        };

        /// <summary>"POLL_FOR_MASTER" -> "Poll For Master" (original GetNiceName).</summary>
        public static string GetNiceName(int frameType)
        {
            string name = FrameTypeNames[frameType].Replace('_', ' ');
            return System.Threading.Thread.CurrentThread.CurrentCulture.TextInfo.ToTitleCase(name.ToLower());
        }

        readonly object _lock = new object();
        readonly List<BacnetNode> _nodes = new List<BacnetNode>();
        readonly MstpPcapPipeSender _wireshark;
        BacnetMstpProtocolTransport _serial;
        bool _ownsSerial;

        /// <summary>Frame types that must NOT be sent to Wireshark (original FiltreMessages).</summary>
        public readonly bool[] FiltreMessages = new bool[8];

        public MstpPcapPipeSender Wireshark => _wireshark;
        public string PipePath => _wireshark.PipePath;
        public bool Running { get; private set; }
        public int FrameCount { get { lock (_lock) return _nodes.Sum(n => n.TotalFrames); } }

        /// <summary>Raised (on a capture thread) for each frame: frame type, source address.</summary>
        public event Action<byte, byte> FrameReceived;

        public MstpCaptureSession(string pipePath = null)
        {
            _wireshark = new MstpPcapPipeSender(pipePath ?? DefaultPipePath, 165);   // 165 = bacnet mstp
        }

        /// <summary>Serial ports: /dev/tty.* and /dev/cu.* (what SerialPort.GetPortNames returns on macOS) plus Linux ttyUSB/ttyACM.</summary>
        public static string[] GetSerialPorts()
        {
            var list = new List<string>();
            try
            {
                if (Directory.Exists("/dev"))
                    foreach (string f in Directory.GetFiles("/dev"))
                    {
                        string n = Path.GetFileName(f);
                        if (n.StartsWith("tty.") || n.StartsWith("cu.") || n.StartsWith("ttyUSB") || n.StartsWith("ttyACM") || n.StartsWith("ttyS"))
                            list.Add(f);
                    }
            }
            catch { }
            try { foreach (string p in SerialPort.GetPortNames()) if (!list.Contains(p)) list.Add(p); } catch { }
            list.Sort(StringComparer.Ordinal);
            return list.ToArray();
        }

        /// <summary>Port selected by default: the last one, preferring USB adapters (original: the last in the list).</summary>
        public static string DefaultPort(string[] ports)
        {
            if (ports == null || ports.Length == 0) return "";
            var usb = ports.LastOrDefault(p => p.Contains("usb", StringComparison.OrdinalIgnoreCase));
            return usb ?? ports[ports.Length - 1];
        }

        /// <summary>Standalone: open the serial port in spy mode (port cannot be shared).</summary>
        public void Start(string portName, int baudRate)
        {
            if (Running) return;
            var t = new BacnetMstpProtocolTransport(portName, baudRate, -1);
            t.RawMessageRecieved += OnRawMessage;
            try { t.Start_SpyMode(); }
            catch { t.RawMessageRecieved -= OnRawMessage; t.Dispose(); throw; }
            _serial = t; _ownsSerial = true;
            _wireshark.Start();
            Running = true;
        }

        /// <summary>Launched from Yabe: listen to the frames of an already running transport (port stays open).</summary>
        public void Start(BacnetMstpProtocolTransport shared)
        {
            if (Running) return;
            _serial = shared; _ownsSerial = false;
            shared.RawMessageRecieved += OnRawMessage;
            _wireshark.Start();
            Running = true;
        }

        public void Stop()
        {
            if (!Running) return;
            Running = false;
            if (_serial != null)
            {
                _serial.RawMessageRecieved -= OnRawMessage;
                if (_ownsSerial) { try { _serial.Dispose(); } catch { } }   // shared transports are left alone, as in the original
                _serial = null;
            }
            _wireshark.Disconnect();
        }

        public void Dispose() => Stop();

        void OnRawMessage(byte[] buffer, int offset, int length) => ProcessFrame(buffer, offset, length);

        /// <summary>One raw MS/TP frame (55 FF type dst src lenH lenL crc [data crc16]). Also the entry point of the tests.</summary>
        public void ProcessFrame(byte[] buffer, int offset, int length)
        {
            if (buffer == null || length < 8 || buffer.Length < offset + length) return;
            byte sourceAddress = buffer[offset + 4];
            byte frameType = buffer[offset + 2];

            // If filter not set, send it to Wireshark
            if (frameType > 7 || !FiltreMessages[frameType])
                _wireshark.SendToWireshark(buffer, offset, length);

            lock (_lock)
            {
                BacnetNode node = _nodes.Find(n => n.Num == sourceAddress);
                if (node == null) { node = new BacnetNode(sourceAddress); _nodes.Add(node); }
                node.NewFrameSend(frameType);
            }
            FrameReceived?.Invoke(frameType, sourceAddress);
        }

        /// <summary>Stations sorted by address.</summary>
        public List<MstpNodeStat> Snapshot()
        {
            lock (_lock)
                return _nodes.OrderBy(n => n.Num).Select(n => new MstpNodeStat
                {
                    Num = n.Num,
                    TotalFrames = n.TotalFrames,
                    MeanTimeTokenRotation = n.MeanTimeTokenRotation,
                    FrameTypeStatistic = (int[])n.FrameTypeStatistic.Clone(),
                }).ToList();
        }
    }

    /// <summary>
    /// Port of Wireshark/WiresharkSender.cs. The Windows named pipe \\.\pipe\bacnet becomes a FIFO
    /// (default /tmp/bacnet) that Wireshark reads with:  wireshark -ni /tmp/bacnet
    /// Writes a pcap stream: global header (network = 165) then one record per MS/TP frame.
    /// </summary>
    public sealed class MstpPcapPipeSender : IDisposable
    {
        [DllImport("libc", SetLastError = true)] static extern int mkfifo(string path, uint mode);
        [DllImport("libc", SetLastError = true)] static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)] static extern int fcntl(int fd, int cmd, int arg);
        [DllImport("libc", SetLastError = true)] static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] static extern int write(int fd, byte[] buf, IntPtr count);

        const int F_GETFL = 3, F_SETFL = 4;
        static int O_NONBLOCK => RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? 0x0004 : 0x0800;
        const int O_WRONLY = 1;

        readonly uint _netId;
        readonly object _writeLock = new object();
        volatile bool _connected, _stop;
        int _fd = -1;
        Thread _thread;

        public string PipePath { get; }
        public bool IsConnected => _connected;
        /// <summary>Set when the FIFO could not be created.</summary>
        public string Error { get; private set; }

        public MstpPcapPipeSender(string pipePath, uint pcapNetId) { PipePath = pipePath; _netId = pcapNetId; }

        public void Start()
        {
            _stop = false;
            try
            {
                if (File.Exists(PipePath) || Directory.Exists(PipePath))
                {
                    // reuse an existing fifo, never delete anything else
                    if (!IsFifo(PipePath))
                    { Error = PipePath + " exists and is not a pipe"; return; }
                }
                else if (mkfifo(PipePath, Convert.ToUInt32("666", 8)) != 0)
                { Error = "mkfifo " + PipePath + " failed (errno " + Marshal.GetLastWin32Error() + ")"; return; }
            }
            catch (Exception ex) { Error = ex.Message; return; }
            Error = null;
            StartThread();
        }

        static bool IsFifo(string path)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("/bin/test", "-p \"" + path + "\"") { UseShellExecute = false };
                using (var p = System.Diagnostics.Process.Start(psi)) { p.WaitForExit(2000); return p.ExitCode == 0; }
            }
            catch { return false; }
        }

        void StartThread()
        {
            _thread = new Thread(PipeCreate) { IsBackground = true, Name = "MstpPcapPipe" };
            _thread.Start();
        }

        // wait for Wireshark (a reader) on the fifo
        void PipeCreate()
        {
            try
            {
                while (!_stop)
                {
                    int fd = open(PipePath, O_WRONLY | O_NONBLOCK);   // fails with ENXIO until a reader exists
                    if (fd < 0) { Thread.Sleep(200); continue; }
                    int fl = fcntl(fd, F_GETFL, 0);
                    fcntl(fd, F_SETFL, fl & ~O_NONBLOCK);
                    lock (_writeLock)
                    {
                        _fd = fd;
                        // pcap global header
                        var h = new byte[24];
                        BitConverter.GetBytes(0xa1b2c3d4u).CopyTo(h, 0);
                        BitConverter.GetBytes((ushort)2).CopyTo(h, 4);
                        BitConverter.GetBytes((ushort)4).CopyTo(h, 6);
                        BitConverter.GetBytes(0).CopyTo(h, 8);
                        BitConverter.GetBytes(0u).CopyTo(h, 12);
                        BitConverter.GetBytes(65535u).CopyTo(h, 16);
                        BitConverter.GetBytes(_netId).CopyTo(h, 20);
                        if (!WriteAll(h)) { CloseFd(); continue; }
                        _connected = true;
                    }
                    return;
                }
            }
            catch { }
        }

        bool WriteAll(byte[] b)
        {
            int off = 0;
            while (off < b.Length)
            {
                byte[] part = off == 0 ? b : b.AsSpan(off).ToArray();
                int n = write(_fd, part, (IntPtr)part.Length);
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        void CloseFd() { if (_fd >= 0) { close(_fd); _fd = -1; } }

        public void Disconnect()
        {
            _stop = true;
            _connected = false;
            try { _thread?.Join(300); } catch { }
            lock (_writeLock) CloseFd();
        }

        public void Dispose() => Disconnect();

        static uint ToUnix(DateTime d) => (uint)(d - new DateTime(1970, 1, 1).ToLocalTime()).TotalSeconds;

        public bool SendToWireshark(byte[] buffer, int offset, int length) => SendToWireshark(buffer, offset, length, DateTime.Now);

        public bool SendToWireshark(byte[] buffer, int offset, int length, DateTime date)
        {
            if (!_connected || buffer == null || buffer.Length < offset + length) return false;
            DateTime d2 = new DateTime((date.Ticks / 10000000L) * 10000000L);
            var rec = new byte[16 + length];
            BitConverter.GetBytes(ToUnix(date)).CopyTo(rec, 0);
            BitConverter.GetBytes((uint)((date.Ticks - d2.Ticks) / 10)).CopyTo(rec, 4);
            BitConverter.GetBytes((uint)length).CopyTo(rec, 8);
            BitConverter.GetBytes((uint)length).CopyTo(rec, 12);
            Array.Copy(buffer, offset, rec, 16, length);
            lock (_writeLock)
            {
                if (_fd < 0) return false;
                if (!WriteAll(rec))
                {
                    // broken pipe, each time wireshark is closing, try to restart
                    _connected = false;
                    CloseFd();
                    if (!_stop) StartThread();
                    return false;
                }
            }
            return true;
        }
    }
}
