using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.BACnet;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Yabe;

namespace YabeMac
{
    /// <summary>
    /// Port of MainDialog.UserCmd.cs: the user menu built from YabeMenuCmd.txt and its commands.
    /// File lookup: ~/Library/Application Support/YabeMac/YabeMenuCmd.txt first, else the one next to the executable.
    /// </summary>
    public partial class MainWindow
    {
        static readonly string[] CommandList = { "none", "launch", "send_iam", "send_whois", "leave_devices", "subscribe_files",
                                                 "snapshot", "write_recipe", "settings", "exec_batch" };
        Func<string, Task>[] UserCmdCommands;

        /// <summary>
        /// Hook for the Subscriptions area: called by the Subscribe_Files user command with a (local) .cov file path
        /// (equivalent of dropping the file on the Subscription view). Left null = command logs "not available".
        /// </summary>
        public Action<string> SubscribeFileHook;

        (Key key, KeyModifiers mods)? ShortCut(string s)
        {
            var ctrl = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? KeyModifiers.Meta : KeyModifiers.Control;   // Cmd on macOS
            Key ToKey(char c) => char.IsDigit(c) ? (Key)Enum.Parse(typeof(Key), "D" + c) : (Key)Enum.Parse(typeof(Key), char.ToUpper(c).ToString());
            try
            {
                if (s.Length == 1) return (ToKey(s[0]), ctrl);
                int ret = s[1];
                int.TryParse(s[0].ToString(), out int ModifierInt);
                // Yabe reserved shortcuts are not accepted with their modifiers (as in the original)
                if (ModifierInt == 3 && (ret == 'N' || ret == 'S' || (ret >= '0' && ret <= '9')))
                    return (ToKey((char)ret), KeyModifiers.None);
                var m = KeyModifiers.None;
                if (((ModifierInt & 1) == 1) || ((ModifierInt & 3) == 0)) m |= ctrl;
                if ((ModifierInt & 2) == 2) m |= KeyModifiers.Alt;
                if ((ModifierInt & 4) == 4) m |= KeyModifiers.Shift;
                return (ToKey((char)ret), m);
            }
            catch { return null; }
        }

        Tuple<int, string> MenuLineToCmd(string line, out string[] MenuCommand)
        {
            Tuple<int, string> Cmd = null;
            // Change escaped \; and \, with unusable symbols (restored later)
            line = line.Replace("\\,", new string((char)4, 1)).Replace("\\;", new string((char)3, 1));
            MenuCommand = line.Split(';');
            if (MenuCommand.Length == 3) Cmd = new Tuple<int, string>(Array.IndexOf(CommandList, MenuCommand[2].ToLower()), null);
            else if (MenuCommand.Length == 4) Cmd = new Tuple<int, string>(Array.IndexOf(CommandList, MenuCommand[2].ToLower()), MenuCommand[3]);
            return Cmd;
        }

        MenuItem FindMenu(string header, ItemsControl parent = null)
        {
            foreach (var o in (parent ?? MainMenu).Items)
                if (o is MenuItem mi)
                {
                    if ((mi.Header as string) == header) return mi;
                    var r = FindMenu(header, mi);
                    if (r != null) return r;
                }
            return null;
        }

        static string UserCmdFile()
        {
            string user = Path.Combine(AppSettings.Dir, "YabeMenuCmd.txt");
            return File.Exists(user) ? user : Path.Combine(Startup.AppDir, "YabeMenuCmd.txt");
        }

        void InitUserCmd()
        {
            var userMenu = FindMenu("User Commands");
            if (userMenu == null) return;
            string filepath = UserCmdFile();

            if (File.Exists(filepath))
            {
                UserCmdCommands = new Func<string, Task>[] { UserCmd_None, UserCmd_Launch, UserCmd_Iam, UserCmd_WhoIs,
                    UserCmd_RemoveDevices, UserCmd_SubscribeFiles, UserCmd_SnapShot, UserCmd_WriteRecipe, UserCmd_Settings, UserCmd_ExecBatch };

                int LineCount = 0;
                try
                {
                    var parents = new Stack<MenuItem>();
                    MenuItem current = userMenu;
                    var items = new List<object>();
                    // build into a fresh item list then swap in (parent items hold their own children)
                    var itemLists = new Dictionary<MenuItem, List<object>> { [userMenu] = items };
                    foreach (string l in File.ReadLines(filepath))
                    {
                        LineCount++;
                        if (string.IsNullOrWhiteSpace(l) || l.StartsWith("#")) continue;

                        Tuple<int, string> Cmd = MenuLineToCmd(l, out string[] MenuCommand);
                        if (Cmd == null) continue;

                        if (MenuCommand[0].StartsWith("SubStart"))
                        {
                            var sub = new MenuItem { Header = MenuCommand[0].Substring(Math.Min(9, MenuCommand[0].Length)) };
                            itemLists[current].Add(sub);
                            itemLists[sub] = new List<object>();
                            parents.Push(current);
                            current = sub;
                        }
                        else if (MenuCommand[0].StartsWith("SubClose"))
                        {
                            if (parents.Count > 0) current = parents.Pop();
                        }
                        else if (MenuCommand[0] != "Sep" && Cmd.Item1 >= 0)
                        {
                            var mi = new MenuItem { Header = MenuCommand[0], Tag = Cmd };
                            if (MenuCommand[1].Length >= 1)
                            {
                                var sc = ShortCut(MenuCommand[1]);
                                if (sc != null) mi.HotKey = new KeyGesture(sc.Value.key, sc.Value.mods);
                            }
                            if (Cmd.Item1 > 0) mi.Click += UserMenuItem_Click;
                            itemLists[current].Add(mi);
                        }
                        else
                            itemLists[current].Add(new Separator());
                    }
                    foreach (var kv in itemLists)
                        foreach (var o in kv.Value) kv.Key.Items.Add(o);
                }
                catch (Exception ex) { Log("Error file YabeMenuCmd.txt line " + LineCount + ": " + ex.Message); }
            }

            if (userMenu.Items.Count == 0) userMenu.IsVisible = false;
        }

        /// <summary>Menu "User Commands" itself was clicked (only reachable when it has no sub items).</summary>
        async void OnUserCommands(object s, RoutedEventArgs e)
        {
            if (s is MenuItem mi && mi.Items.Count > 0) return;
            await Dialogs.Message("No user commands defined.\nCreate a YabeMenuCmd.txt in\n" + AppSettings.Dir + "\n(see the sample next to the application) and restart Yabe.", "User Commands");
        }

        static string ChangeDirectoryLocalPath(string OriginalPath)
        {
            string LocalPath = Startup.AppDir;
            if (!LocalPath.EndsWith("/") && !LocalPath.EndsWith("\\")) LocalPath += Path.DirectorySeparatorChar;
            OriginalPath = OriginalPath.Replace("./", LocalPath);
            return OriginalPath.Replace(".\\", LocalPath);
        }

        async void UserMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var Cmd = (Tuple<int, string>)(sender as MenuItem).Tag;
                await UserCmdCommands[Cmd.Item1](Cmd.Item2);
            }
            catch { }
        }

        // ---------------------------------------------------------------------------------------------
        async Task UserCmd_None(string Parameters)  // used with a delay in batches
        {
            if (Parameters == null) return;
            uint.TryParse(Parameters, out uint delay);
            await Task.Delay((int)delay);
        }

        async Task UserCmd_Launch(string Parameters)
        {
            if (Parameters == null) return;
            string[] P = Parameters.Split(',');
            if (P.Length < 2) return;
            try
            {
                string program = ChangeDirectoryLocalPath(P[1]);
                string args = P.Length > 2 ? ChangeDirectoryLocalPath(P[2]) : null;
                // macOS has no notepad: open the file with the default text editor instead
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Path.GetFileNameWithoutExtension(program).ToLower() == "notepad")
                {
                    args = "-t \"" + args + "\"";
                    program = "/usr/bin/open";
                }
                var ps = new ProcessStartInfo(program) { WorkingDirectory = ChangeDirectoryLocalPath(P[0]) };
                if (args != null) ps.Arguments = args;
                Process.Start(ps);
            }
            catch
            {
                await Dialogs.Message("Cannot launch " + ChangeDirectoryLocalPath(P[1]) + "\r\nCheck the program name", "Launch Error");
            }
        }

        async Task UserCmd_Settings(string Parameters)
        {
            if (Parameters == null) return;
            string[] P = Parameters.Split(',');
            var allProp = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            string Norm(string n) => new string(n.Where(char.IsLetterOrDigit).ToArray()).ToLower();

            for (int i = 0; i < P.Length; i++)
            {
                string[] Setting = P[i].Split('=');
                if (Setting.Length == 1)    // End: save (and restart)
                {
                    AppSettings.Default.Save();
                    if (P[i].Trim().ToLower() == "restart") RestartApplication();
                    continue;
                }
                var prop = allProp.FirstOrDefault(p => Norm(p.Name) == Norm(Setting[0]));
                if (prop == null) { Log("Settings: unknown setting \"" + Setting[0].Trim() + "\""); continue; }
                string ValToSet = Setting[1].Replace((char)4, ',').Replace((char)3, ';');
                try
                {
                    if (prop.PropertyType.IsEnum) prop.SetValue(AppSettings.Default, Enum.Parse(prop.PropertyType, ValToSet.Trim(), true));
                    else prop.SetValue(AppSettings.Default, Convert.ChangeType(ValToSet.Trim(), prop.PropertyType, System.Globalization.CultureInfo.InvariantCulture));
                }
                catch { }
            }
        }

        void RestartApplication()
        {
            try
            {
                AppSettings.Default.Save();
                string exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
            }
            catch { }
            Close();
        }

        async Task UserCmd_Iam(string Parameters)
        {
            if (AppSettings.Default.YabeDeviceId >= 0)
            {
                foreach (var cli in Channels.ToArray())
                    try { YabeServer?.Iam(cli); } catch { }
            }
            else
                Log("Cannot send Iam without Yabe device Id");
        }

        async Task UserCmd_WhoIs(string Parameters)
        {
            if (Parameters == null) return;
            string[] Param = Parameters.Split(',');
            var Id = new List<Tuple<int, int>>();
            try
            {
                for (int i = 0; i < Param.Length; i++)
                {
                    int Min, Max;
                    if (Param[i].Contains(".."))
                    {
                        string[] Param2 = Param[i].Split('.');
                        Min = Convert.ToInt32(Param2[0]);
                        Max = Convert.ToInt32(Param2[2]);
                    }
                    else
                        Min = Max = Convert.ToInt32(Param[i]);
                    Id.Add(new Tuple<int, int>(Min, Max));
                }
            }
            catch { }

            foreach (var cli in Channels.ToArray())
                foreach (var devId in Id)
                    try { cli.WhoIs(devId.Item1, devId.Item2); } catch { }
        }

        async Task UserCmd_RemoveDevices(string Parameters)
        {
            if (Parameters == null) return;
            string[] Param = Parameters.Split(',');
            var Id = new uint[Param.Length];
            try { for (int i = 0; i < Param.Length; i++) Id[i] = Convert.ToUInt32(Param[i]); }
            catch { return; }

            // If a router is to be removed all routed nodes have to be in the removed list before
            var toRemove = new List<(DeviceNode parent, DeviceNode node)>();
            var roots = DeviceTree.ItemsSource as IEnumerable<DeviceNode>;
            if (roots == null) return;
            foreach (var chan in roots.Where(r => r.Channel != null).ToList())
            {
                foreach (var Tn in chan.Children.ToList())
                {
                    int count = 0;
                    if (Tn.Children.Count != 0)
                    {
                        foreach (var Tn2 in Tn.Children.ToList())
                            if (Tn2.Device != null && !Id.Contains(Tn2.Device.deviceId)) { toRemove.Add((Tn, Tn2)); count++; }
                    }
                    if (count == Tn.Children.Count)   // a simple device or a router now with all devices removed
                        if (Tn.Device != null && !Id.Contains(Tn.Device.deviceId)) toRemove.Add((chan, Tn));
                }
            }
            foreach (var (parent, node) in toRemove) parent.Children.Remove(node);

            _objectRoots.Clear();     // clear address space
            _propertyRows.Clear();    // clear property grid
        }

        async Task UserCmd_SubscribeFiles(string Parameters)
        {
            if (Parameters == null) return;
            foreach (string ParamStr in Parameters.Split(','))
            {
                string f = ChangeDirectoryLocalPath(ParamStr);
                if (!File.Exists(f)) continue;
                if (SubscribeFileHook == null) { Log("Subscribe_Files: subscriptions not available"); return; }
                try { SubscribeFileHook(f); } catch { }
            }
        }

        List<string> UserCmd_WriteRecipeSnapShotCommon(string Parameters, bool IsOnlySnap)
        {
            BacnetValue GetBacnetValue(BACnetDevice device, BacnetObjectId obj, BacnetPropertyIds prop)
            {
                device.ReadPropertyRequest(obj, prop, out IList<BacnetValue> out_value);
                if (out_value != null) return out_value[0];
                return new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL, null);
            }

            var Status = new List<string>();
            Parameters = ChangeDirectoryLocalPath(Parameters);
            if (File.Exists(Parameters))
            {
                string ReadWriteStatus = null;
                foreach (string WriteCmd in File.ReadAllLines(Parameters))
                {
                    string[] Commande = WriteCmd.Split(';');
                    if (Commande.Length < 7 || Commande[0].StartsWith("#")) continue;
                    try
                    {
                        if (uint.TryParse(Commande[0], out uint device_id))   // fails on the header line
                        {
                            BACnetDevice device = FetchDeviceFromDeviceId(device_id);
                            if (device == null)
                            {
                                ReadWriteStatus = Commande[0] + ";" + Commande[1] + ";" + Commande[2] + ";" + Commande[3] + ";" + Commande[4] + ";;Device not Found";
                                Status.Add(ReadWriteStatus);
                                continue;
                            }
                            if (device.deviceName != null) Commande[1] = device.deviceName;

                            ReadWriteStatus = Commande[0] + ";" + Commande[1] + ";" + Commande[2] + ";" + Commande[3] + ";" + Commande[4] + ";";

                            BacnetObjectId boid = BacnetObjectId.Parse("OBJECT_" + Commande[2].Trim().Replace(' ', '_').ToUpper());
                            BacnetPropertyIds prop = (BacnetPropertyIds)Enum.Parse(typeof(BacnetPropertyIds), "PROP_" + Commande[4].Trim().Replace(' ', '_').ToUpper());
                            uint.TryParse(Commande[5], out uint WritePriority);

                            string ValStrSatus = Commande[6];
                            string ValStr = Commande[6];
                            for (int i = 7; i < Commande.Length; i++)   // restore the possible ; from a string
                            {
                                ValStr = ValStr + ";" + Commande[i];
                                ValStrSatus = ValStrSatus + "," + Commande[i];
                            }

                            BacnetValue valRead = GetBacnetValue(device, boid, prop);

                            if (valRead.Tag != BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL && !IsOnlySnap)
                            {
                                var valtoWrite = new BacnetValue(valRead.Tag, ValStr);
                                uint oldpriority = device.channel.WritePriority;
                                device.channel.WritePriority = WritePriority;
                                try
                                {
                                    bool ret = device.WritePropertyRequest(boid, prop, new List<BacnetValue> { valtoWrite });
                                    if (ret == false) ReadWriteStatus += ";Write Fail";
                                    else ReadWriteStatus += ValStrSatus + ";Write Ok";
                                }
                                catch { ReadWriteStatus += ";Write Fail"; }
                                device.channel.WritePriority = oldpriority;
                            }
                            if (IsOnlySnap)
                            {
                                if (valRead.Value != null) ReadWriteStatus += valRead.Value.ToString();
                                else ReadWriteStatus += ";Read Fail";
                            }
                        }
                        else continue;
                    }
                    catch { ReadWriteStatus += ";Syntax Error"; }

                    Status.Add(ReadWriteStatus);
                }
            }
            return Status;
        }

        async Task UserCmd_SnapShot(string Parameters)
        {
            if (Parameters == null) return;
            string[] Param = Parameters.Split(',');
            if (Param.Length >= 1)
            {
                List<string> status = await Task.Run(() => UserCmd_WriteRecipeSnapShotCommon(Param[0], true));
                if (Param.Length >= 2)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("DeviceId;DeviceName;ObjectId;ObjectName;Property;Value;Status");
                    foreach (string s in status) sb.AppendLine(s);
                    string FileName = ChangeDirectoryLocalPath(Param[1]).Replace("%d", DateTime.Now.ToString().Replace('/', '_').Replace(':', '_'));
                    try { File.WriteAllText(FileName, sb.ToString()); }
                    catch { Log("Fail to write the snapshot File"); }
                }
                if (Param.Length == 1 || Param.Length > 2)
                    await new UserCommandStatus(status).ShowDialog(this);
            }
        }

        async Task UserCmd_WriteRecipe(string Parameters)
        {
            if (Parameters == null) return;
            List<string> status = await Task.Run(() => UserCmd_WriteRecipeSnapShotCommon(Parameters, false));
            await new UserCommandStatus(status).ShowDialog(this);
        }

        async Task UserCmd_ExecBatch(string Parameters)
        {
            if (Parameters == null) return;
            if (!File.Exists(ChangeDirectoryLocalPath(Parameters))) return;

            // The file is the same as YabeMenuCmd.txt without the 2 first columns
            string[] lines = File.ReadAllLines(ChangeDirectoryLocalPath(Parameters));
            int LineCount = 0;
            string LineError = "", Sep = "";
            foreach (string line in lines)
            {
                LineCount++;
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;

                Tuple<int, string> Cmd = MenuLineToCmd(";;" + line, out _);
                if (Cmd == null) { LineError = LineError + Sep + LineCount; Sep = ", "; }
                else
                    try { await UserCmdCommands[Cmd.Item1](Cmd.Item2); }
                    catch { LineError = LineError + Sep + LineCount; Sep = ", "; }
            }
            if (LineError != "") Log("Script error line(s) : " + LineError);
        }
    }
}
