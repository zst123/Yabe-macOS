using System;
using System.Collections.Generic;
using System.IO.BACnet;
using System.IO.BACnet.Storage;
using System.Reflection;

namespace YabeMac
{
    /// <summary>Yabe acting as a BACnet server (port of Utils.cs/YabeDevice.cs; uses AppSettings.YabeDeviceId).</summary>
    public class YabeServerDevice
    {
        readonly DeviceStorage m_storage;
        readonly uint myId;

        public YabeServerDevice(uint DeviceId)
        {
            myId = DeviceId;
            m_storage = DeviceStorage.Load("Yabe.Common_Files.YabeDeviceDescriptor.xml", (uint)AppSettings.Default.YabeDeviceId);
            string version = (Assembly.GetEntryAssembly() ?? GetType().Assembly).GetName().Version.ToString();
            Property Prop = Array.Find(m_storage.Objects[0].Properties, p => p.Id == BacnetPropertyIds.PROP_OBJECT_LIST);
            Prop.Value[0] = "OBJECT_DEVICE:" + AppSettings.Default.YabeDeviceId.ToString();
            Prop = Array.Find(m_storage.Objects[0].Properties, p => p.Id == BacnetPropertyIds.PROP_FIRMWARE_REVISION);
            Prop.Value[0] = version;
            Prop = Array.Find(m_storage.Objects[0].Properties, p => p.Id == BacnetPropertyIds.PROP_APPLICATION_SOFTWARE_VERSION);
            Prop.Value[0] = version;
        }

        static bool Log(Exception ex) { System.Diagnostics.Trace.WriteLine("YabeDevice: " + ex); return true; }

        public void Iam(BacnetClient client)
        {
            // MS/TP transport (not available in this port yet): no I-Am without a source address
            var t = client.Transport;
            if (t != null && t.GetType().Name == "BacnetMstpProtocolTransport" &&
                t.GetType().GetProperty("SourceAddress")?.GetValue(t) is short sa && sa == -1)
                return;
            client.Iam(myId, BacnetSegmentations.SEGMENTATION_BOTH, 61440);
        }

        public void AddCom(BacnetClient client)
        {
            client.OnWhoIs += OnWhoIs;
            client.OnReadPropertyRequest += OnReadPropertyRequest;
            client.OnReadPropertyMultipleRequest += OnReadPropertyMultipleRequest;
        }

        public void RemoveCom(BacnetClient client)
        {
            client.OnWhoIs -= OnWhoIs;
            client.OnReadPropertyRequest -= OnReadPropertyRequest;
            client.OnReadPropertyMultipleRequest -= OnReadPropertyMultipleRequest;
        }

        void OnWhoIs(BacnetClient sender, BacnetAddress adr, int low_limit, int high_limit)
        {
            if (low_limit != -1 && myId < low_limit) return;
            else if (high_limit != -1 && myId > high_limit) return;
            sender.Iam(myId, BacnetSegmentations.SEGMENTATION_BOTH, 61440);
        }

        void OnReadPropertyRequest(BacnetClient sender, BacnetAddress adr, byte invoke_id, BacnetObjectId object_id, BacnetPropertyReference property, BacnetMaxSegments max_segments)
        {
            lock (m_storage)
            {
                try
                {
                    IList<BacnetValue> value;
                    DeviceStorage.ErrorCodes code = m_storage.ReadProperty(object_id, (BacnetPropertyIds)property.propertyIdentifier, property.propertyArrayIndex, out value);
                    if (code == DeviceStorage.ErrorCodes.Good)
                        sender.ReadPropertyResponse(adr, invoke_id, sender.GetSegmentBuffer(max_segments), object_id, property, value);
                    else
                        sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROPERTY, invoke_id, BacnetErrorClasses.ERROR_CLASS_DEVICE, BacnetErrorCodes.ERROR_CODE_OTHER);
                }
                catch (Exception ex) when (Log(ex))
                {
                    sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROPERTY, invoke_id, BacnetErrorClasses.ERROR_CLASS_DEVICE, BacnetErrorCodes.ERROR_CODE_OTHER);
                }
            }
        }

        void OnReadPropertyMultipleRequest(BacnetClient sender, BacnetAddress adr, byte invoke_id, IList<BacnetReadAccessSpecification> properties, BacnetMaxSegments max_segments)
        {
            lock (m_storage)
            {
                try
                {
                    IList<BacnetPropertyValue> value;
                    var values = new List<BacnetReadAccessResult>();
                    foreach (BacnetReadAccessSpecification p in properties)
                    {
                        if (p.propertyReferences.Count == 1 && p.propertyReferences[0].propertyIdentifier == (uint)BacnetPropertyIds.PROP_ALL)
                        {
                            if (!m_storage.ReadPropertyAll(p.objectIdentifier, out value))
                            {
                                sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROP_MULTIPLE, invoke_id, BacnetErrorClasses.ERROR_CLASS_OBJECT, BacnetErrorCodes.ERROR_CODE_UNKNOWN_OBJECT);
                                return;
                            }
                        }
                        else
                            m_storage.ReadPropertyMultiple(p.objectIdentifier, p.propertyReferences, out value);
                        values.Add(new BacnetReadAccessResult(p.objectIdentifier, value));
                    }
                    sender.ReadPropertyMultipleResponse(adr, invoke_id, sender.GetSegmentBuffer(max_segments), values);
                }
                catch (Exception ex) when (Log(ex))
                {
                    sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROP_MULTIPLE, invoke_id, BacnetErrorClasses.ERROR_CLASS_DEVICE, BacnetErrorCodes.ERROR_CODE_OTHER);
                }
            }
        }
    }

    public partial class MainWindow
    {
        /// <summary>The Yabe BACnet server (original m_Server). Null until a channel is started with YabeDeviceId >= 0.</summary>
        public YabeServerDevice YabeServer { get; private set; }

        /// <summary>
        /// Hook for channel start-up (original MainDialog.Menus.cs, just BEFORE comm.Start()):
        /// if AppSettings.YabeDeviceId >= 0 the channel is registered with the Yabe server (answers Who-Is,
        /// ReadProperty, ReadPropertyMultiple); otherwise incoming Who-Is are ignored.
        /// Call it instead of "comm.OnWhoIs += (_,__,___,____) => {}".
        /// </summary>
        public void InitYabeServer(BacnetClient comm)
        {
            try
            {
                if (AppSettings.Default.YabeDeviceId >= 0)
                {
                    if (YabeServer == null) YabeServer = new YabeServerDevice((uint)AppSettings.Default.YabeDeviceId);
                    YabeServer.AddCom(comm);
                    return;
                }
            }
            catch (Exception ex) { Log("Yabe device not available: " + ex.Message); }
            comm.OnWhoIs += (_, __, ___, ____) => { };
        }

        /// <summary>Call AFTER comm.Start(): sends an I-Am for the Yabe device (no-op when YabeDeviceId &lt; 0).</summary>
        public void YabeServerIam(BacnetClient comm)
        {
            try { YabeServer?.Iam(comm); } catch { }
        }

        /// <summary>Call when a channel is removed/disposed (original: m_Server.RemoveCom).</summary>
        public void YabeServerRemove(BacnetClient comm) => YabeServer?.RemoveCom(comm);
    }
}
