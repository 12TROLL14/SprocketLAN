using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SprocketMP
{
    /// <summary>Send / receive vehicle designs (.blueprint + profile picture) between players over TCP.
    /// Independent from the game session: works in menus, to any IP running the mod. The receiver writes the
    /// design into its configured folder and keeps a timestamped backup of any design it replaces.</summary>
    public static class DesignShare
    {
        const uint Magic = 0x53444C53;   // "SLDS"
        const int Version = 2;   // 2: the design's shell settings (fillers, fuse, caps, autocannon per gun) travel with it
        const int MaxBytes = 64 << 20;

        public class Design { public string Name, Faction, Path; public DateTime Modified; public long Size; }

        public static readonly ConcurrentQueue<string> Notices = new ConcurrentQueue<string>();   // shown in the HUD
        public static volatile string SendStatus = "";
        public static volatile bool Sending;
        static TcpListener _listener;

        static string FactionsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Sprocket", "Factions");

        /// <summary>All vehicle designs of every faction, newest first.</summary>
        public static List<Design> ListDesigns()
        {
            var list = new List<Design>();
            try
            {
                if (!Directory.Exists(FactionsRoot)) return list;
                foreach (var fac in Directory.GetDirectories(FactionsRoot))
                {
                    var veh = Path.Combine(fac, "Blueprints", "Vehicles");
                    if (!Directory.Exists(veh)) continue;
                    foreach (var f in Directory.GetFiles(veh, "*.blueprint"))
                    {
                        var fi = new FileInfo(f);
                        list.Add(new Design { Name = Path.GetFileNameWithoutExtension(f), Faction = Path.GetFileName(fac), Path = f, Modified = fi.LastWriteTime, Size = fi.Length });
                    }
                }
            }
            catch (Exception e) { MPLog.Warn("Design list: " + e.Message); }
            return list.OrderByDescending(d => d.Modified).ToList();
        }

        /// <summary>Folder received designs go to: the configured one, or else the vehicles folder of the faction
        /// with the most recently saved design.</summary>
        static string _recvCache; static DateTime _recvAt;
        public static string ReceiveFolder()
        {
            // cached: the automatic folder needs a directory scan
            if (_recvCache != null && (DateTime.UtcNow - _recvAt).TotalSeconds < 10) return _recvCache;
            _recvCache = ReceiveFolderNow(); _recvAt = DateTime.UtcNow;
            return _recvCache;
        }
        static string ReceiveFolderNow()
        {
            var cfg = Plugin.DesignReceiveFolder.Value;
            if (!string.IsNullOrWhiteSpace(cfg)) return Environment.ExpandEnvironmentVariables(cfg.Trim());
            var newest = ListDesigns().FirstOrDefault(d => !d.Name.Equals("Autosave", StringComparison.OrdinalIgnoreCase));
            if (newest != null) return Path.GetDirectoryName(newest.Path);
            return Path.Combine(FactionsRoot, "Default", "Blueprints", "Vehicles");
        }

        static string BackupFolder()
        {
            var cfg = Plugin.DesignBackupFolder.Value;
            if (!string.IsNullOrWhiteSpace(cfg)) return Environment.ExpandEnvironmentVariables(cfg.Trim());
            return Path.Combine(BepInEx.Paths.ConfigPath, "SprocketLAN_DesignBackups");
        }

        public static string SafeName(string n)
        {
            n = (n ?? "").Trim();
            foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            n = n.Replace("..", "_").Trim('.', ' ');
            if (n.Length > 80) n = n.Substring(0, 80);
            return n.Length == 0 ? "Received design" : n;
        }

        // =========================================================================================== receive
        public static void StartListener()
        {
            if (_listener != null || !Plugin.DesignAccept.Value) return;
            // several game copies on one PC: the first takes the port, the others the next free ones
            for (int port = Plugin.DesignPort.Value; port < Plugin.DesignPort.Value + 8; port++)
            {
                try
                {
                    var l = new TcpListener(IPAddress.Any, port); l.Start();
                    _listener = l; ListenPort = port;
                    new Thread(AcceptLoop) { IsBackground = true, Name = "SprocketLAN-designs" }.Start();
                    MPLog.Info($"Design sharing: listening on TCP {port}, saving to {ReceiveFolder()}");
                    return;
                }
                catch (Exception) { }   // port in use: try the next
            }
            MPLog.Warn($"Design sharing: no free port from {Plugin.DesignPort.Value}");
        }
        public static int ListenPort;
        static readonly List<(string name, List<string> lines)> _shellImports = new List<(string, List<string>)>();
        /// <summary>Main thread: apply shell settings received with designs.</summary>
        public static void ApplyShellImports()
        {
            List<(string, List<string>)> todo;
            lock (_shellImports) { if (_shellImports.Count == 0) return; todo = new List<(string, List<string>)>(_shellImports); _shellImports.Clear(); }
            foreach (var (n, l) in todo) { int k = Shells.ShellStore.Import(n, l); MPLog.Info($"Shell settings for '{n}' received with the design: {k} entries"); }
        }

        static void AcceptLoop()
        {
            while (true)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); } catch { return; }
                new Thread(() => Receive(c)) { IsBackground = true, Name = "SprocketLAN-design-rx" }.Start();
            }
        }

        static void Receive(TcpClient c)
        {
            string from = "?";
            try
            {
                using (c)
                {
                    c.ReceiveTimeout = c.SendTimeout = 20000;
                    try { from = ((IPEndPoint)c.Client.RemoteEndPoint).Address.ToString(); } catch { }
                    var s = c.GetStream();
                    var r = new BinaryReader(s, Encoding.UTF8); var w = new BinaryWriter(s, Encoding.UTF8);
                    uint magic = r.ReadUInt32();
                    if (magic == SettingsMagic) { ReceiveSettings(r, w, from); return; }
                    int ver = magic == Magic ? r.ReadInt32() : -1;
                    if (magic != Magic || (ver != 1 && ver != 2)) { w.Write("ERROR: not a SprocketLAN design transfer (or different mod version)"); return; }
                    string sender = r.ReadString(); string name = SafeName(r.ReadString());
                    int len = r.ReadInt32(); if (len <= 0 || len > MaxBytes) { w.Write("ERROR: bad size"); return; }
                    var bp = r.ReadBytes(len); if (bp.Length != len) throw new IOException("truncated design");
                    int plen = r.ReadInt32(); byte[] png = null;
                    if (plen > 0 && plen <= MaxBytes) { png = r.ReadBytes(plen); if (png.Length != plen) png = null; }
                    List<string> shellLines = null;
                    if (ver >= 2) { int nl = r.ReadInt32(); if (nl >= 0 && nl < 1000) { shellLines = new List<string>(); for (int i = 0; i < nl; i++) shellLines.Add(r.ReadString()); } }

                    string msg = Save(name, bp, png);
                    if (shellLines != null && shellLines.Count > 0) { lock (_shellImports) _shellImports.Add((name, shellLines)); msg += $" + {shellLines.Count} shell settings"; }
                    w.Write("OK: " + msg); w.Flush();
                    string who = string.IsNullOrWhiteSpace(sender) ? from : $"{sender} ({from})";
                    MPLog.Info($"Design '{name}' received from {who}: {msg}");
                    Notices.Enqueue($"Received design <b>{name}</b> from {who}{(msg.Contains("backup") ? " (old version backed up)" : "")}");
                }
            }
            catch (Exception e) { MPLog.Warn($"Design receive from {from}: {e.Message}"); Notices.Enqueue($"<color=#f88>Design transfer from {from} failed: {e.Message}</color>"); }
        }

        static string Save(string name, byte[] bp, byte[] png)
        {
            var dir = ReceiveFolder(); Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, name + ".blueprint");
            var profDir = Path.Combine(dir, "Profiles"); var prof = Path.Combine(profDir, name + ".png");
            string backup = null;
            if (File.Exists(target))
            {
                var bdir = BackupFolder(); Directory.CreateDirectory(bdir);
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                backup = Path.Combine(bdir, $"{name}_{stamp}.blueprint");
                File.Copy(target, backup, true);
                if (File.Exists(prof)) File.Copy(prof, Path.Combine(bdir, $"{name}_{stamp}.png"), true);
            }
            var tmp = target + ".part";
            File.WriteAllBytes(tmp, bp);
            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
            if (png != null) { Directory.CreateDirectory(profDir); File.WriteAllBytes(prof, png); }
            return backup != null ? $"updated {target} (backup: {backup})" : $"saved {target}";
        }

        // =========================================================================================== settings
        const uint SettingsMagic = 0x53434C53;   // "SLCS"
        const int SettingsVersion = 1;
        static readonly string[] SharedSections = { "Armour", "Shells", "Combat", "Repair", "Stabiliser" };

        /// <summary>Settings that decide how hits and gameplay work (sent to other players). Key bindings, folders,
        /// names, network and pen cam display settings stay personal.</summary>
        public static List<BepInEx.Configuration.ConfigEntryBase> SharedSettings()
        {
            var l = new List<BepInEx.Configuration.ConfigEntryBase>();
            var cfg = Plugin.Instance?.Config; if (cfg == null) return l;
            foreach (var kv in cfg)
            {
                if (Array.IndexOf(SharedSections, kv.Key.Section) < 0) continue;
                if (kv.Key.Key.Contains("Key")) continue;   // key bindings
                l.Add(kv.Value);
            }
            return l;
        }

        public class SettingsOffer { public string From; public List<(string sec, string key, string val)> Values; public List<string> Changes; }
        public static volatile SettingsOffer PendingSettings;

        static List<string> Diff(List<(string sec, string key, string val)> vals)
        {
            var ch = new List<string>(); var cfg = Plugin.Instance?.Config; if (cfg == null) return ch;
            var mine = SharedSettings();
            foreach (var (sec, key, val) in vals)
            {
                var e = mine.FirstOrDefault(x => x.Definition.Section == sec && x.Definition.Key == key);
                if (e == null) continue;   // unknown or not shared here
                string cur = BepInEx.Configuration.TomlTypeConverter.ConvertToString(e.BoxedValue, e.SettingType);
                if (cur != val) ch.Add($"{key}: {cur} -> {val}");
            }
            return ch;
        }

        static void ReceiveSettings(BinaryReader r, BinaryWriter w, string from)
        {
            if (r.ReadInt32() != SettingsVersion) { w.Write("ERROR: different settings format (update the mod)"); return; }
            string sender = r.ReadString(); int n = r.ReadInt32();
            if (n < 0 || n > 2000) { w.Write("ERROR: bad settings"); return; }
            var vals = new List<(string, string, string)>();
            for (int i = 0; i < n; i++) vals.Add((r.ReadString(), r.ReadString(), r.ReadString()));
            string who = string.IsNullOrWhiteSpace(sender) ? from : $"{sender} ({from})";
            var ch = Diff(vals);
            if (ch.Count == 0) { w.Write("OK: their settings are already the same"); w.Flush(); Notices.Enqueue($"Settings from <b>{who}</b>: already the same as yours"); return; }
            PendingSettings = new SettingsOffer { From = who, Values = vals, Changes = ch };
            w.Write($"OK: {ch.Count} differences, waiting for them to apply"); w.Flush();
            MPLog.Info($"Settings received from {who}: {ch.Count} differences ({string.Join("; ", ch)})");
            Notices.Enqueue($"<b>{who}</b> sent you their settings ({ch.Count} differences): open [{Plugin.DesignKey.Value}] to apply");
        }

        /// <summary>Main thread: apply the pending offer (the current config file is backed up first).</summary>
        public static string ApplyPendingSettings()
        {
            var o = PendingSettings; if (o == null) return "nothing to apply";
            PendingSettings = null;
            var cfg = Plugin.Instance.Config;
            try
            {
                var bdir = BackupFolder(); Directory.CreateDirectory(bdir);
                var bk = Path.Combine(bdir, $"SprocketLAN_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.cfg");
                try { cfg.Save(); } catch { }
                File.Copy(cfg.ConfigFilePath, bk, true);
                var mine = SharedSettings(); int n = 0;
                bool save = cfg.SaveOnConfigSet; cfg.SaveOnConfigSet = false;
                try
                {
                    foreach (var (sec, key, val) in o.Values)
                    {
                        var e = mine.FirstOrDefault(x => x.Definition.Section == sec && x.Definition.Key == key); if (e == null) continue;
                        try { var v = BepInEx.Configuration.TomlTypeConverter.ConvertToValue(val, e.SettingType); if (!Equals(v, e.BoxedValue)) { e.BoxedValue = v; n++; } }
                        catch (Exception ex) { MPLog.Warn($"setting {key}: {ex.Message}"); }
                    }
                }
                finally { cfg.SaveOnConfigSet = save; }
                cfg.Save();
                MPLog.Info($"Applied {n} settings from {o.From} (previous config backed up to {bk})");
                return $"<color=#8f8>Applied {n} settings from {o.From}</color> <color=#9aa>(your old config: {bk})</color>";
            }
            catch (Exception e) { MPLog.Warn("apply settings: " + e.Message); return "<color=#f88>Applying settings failed: " + e.Message + "</color>"; }
        }

        public static void SendSettings(string target)
        {
            if (Sending) return;
            target = (target ?? "").Trim();
            if (target.Length == 0) { SendStatus = "<color=#f88>Enter an IP address</color>"; return; }
            string host = target; int port = Plugin.DesignPort.Value;
            int colon = target.LastIndexOf(':');
            if (colon > 0 && target.IndexOf(':') == colon && int.TryParse(target.Substring(colon + 1), out var p)) { host = target.Substring(0, colon); port = p; }
            // read the values on the main thread
            var vals = SharedSettings().Select(e => (e.Definition.Section, e.Definition.Key, BepInEx.Configuration.TomlTypeConverter.ConvertToString(e.BoxedValue, e.SettingType))).ToList();
            Sending = true; SendStatus = $"Sending your settings to {host}:{port}...";
            string sender = Plugin.EffectiveName;
            new Thread(() =>
            {
                try
                {
                    using var c = new TcpClient();
                    var ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(5000)) throw new Exception("no answer (is the mod running there, and the port open?)");
                    c.EndConnect(ar);
                    c.ReceiveTimeout = c.SendTimeout = 20000;
                    var s = c.GetStream();
                    var w = new BinaryWriter(s, Encoding.UTF8); var r = new BinaryReader(s, Encoding.UTF8);
                    w.Write(SettingsMagic); w.Write(SettingsVersion); w.Write(sender ?? ""); w.Write(vals.Count);
                    foreach (var (a, b, v) in vals) { w.Write(a); w.Write(b); w.Write(v ?? ""); }
                    w.Flush();
                    string reply = r.ReadString();
                    SendStatus = reply.StartsWith("OK") ? $"<color=#8f8>Settings sent to {host}: {reply.Substring(3).Trim()}</color>" : $"<color=#f88>{host}: {reply}</color>";
                    MPLog.Info($"Settings sent to {host}:{port}: {reply}");
                }
                catch (Exception e) { SendStatus = $"<color=#f88>Send failed: {e.Message}</color>"; MPLog.Warn($"Settings send to {target}: {e.Message}"); }
                finally { Sending = false; }
            }) { IsBackground = true, Name = "SprocketLAN-settings-tx" }.Start();
        }

        // =========================================================================================== send
        /// <summary>Sends a design in the background; progress/result in SendStatus. target = "ip" or "ip:port".</summary>
        public static void Send(Design d, string target)
        {
            if (Sending || d == null) return;
            target = (target ?? "").Trim();
            if (target.Length == 0) { SendStatus = "<color=#f88>Enter an IP address</color>"; return; }
            string host = target; int port = Plugin.DesignPort.Value;
            int colon = target.LastIndexOf(':');
            if (colon > 0 && target.IndexOf(':') == colon && int.TryParse(target.Substring(colon + 1), out var p)) { host = target.Substring(0, colon); port = p; }
            Sending = true; SendStatus = $"Sending '{d.Name}' to {host}:{port}...";
            string sender = Plugin.EffectiveName;
            var shells = Shells.ShellStore.Export(d.Name);   // read on the main thread
            new Thread(() =>
            {
                try
                {
                    var bp = File.ReadAllBytes(d.Path);
                    var profPath = Path.Combine(Path.GetDirectoryName(d.Path), "Profiles", d.Name + ".png");
                    byte[] png = File.Exists(profPath) ? File.ReadAllBytes(profPath) : null;
                    using var c = new TcpClient();
                    var ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(5000)) throw new Exception("no answer (is the mod running there, and the port open?)");
                    c.EndConnect(ar);
                    c.ReceiveTimeout = c.SendTimeout = 20000;
                    var s = c.GetStream();
                    var w = new BinaryWriter(s, Encoding.UTF8); var r = new BinaryReader(s, Encoding.UTF8);
                    w.Write(Magic); w.Write(Version); w.Write(sender ?? ""); w.Write(d.Name);
                    w.Write(bp.Length); w.Write(bp);
                    w.Write(png?.Length ?? 0); if (png != null) w.Write(png);
                    w.Write(shells.Count); foreach (var l in shells) w.Write(l);
                    w.Flush();
                    string reply = r.ReadString();
                    bool ok = reply.StartsWith("OK");
                    SendStatus = ok ? $"<color=#8f8>Sent '{d.Name}' to {host} ({bp.Length / 1024} KB)</color>" : $"<color=#f88>{host}: {reply}</color>";
                    MPLog.Info($"Design '{d.Name}' sent to {host}:{port}: {reply}");
                }
                catch (Exception e) { SendStatus = $"<color=#f88>Send failed: {e.Message}</color>"; MPLog.Warn($"Design send to {target}: {e.Message}"); }
                finally { Sending = false; }
            }) { IsBackground = true, Name = "SprocketLAN-design-tx" }.Start();
        }
    }
}
