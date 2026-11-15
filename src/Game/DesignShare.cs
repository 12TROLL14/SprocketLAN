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
        const int MaxBytes = 16 << 20;        // a design (blueprints are a few MB at most)
        const int MaxPicture = 8 << 20;
        static int _receiving;                  // TCP transfers being read right now

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
            if (n.Length > 80) n = n.Substring(0, 80).Trim('.', ' ');
            if (n.Length == 0) return "Received design";
            // Windows device names (CON, NUL, COM1...) can't be file names: "NUL.blueprint" is the null device
            string stem = n.Split('.')[0].Trim().ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "CONIN$" || stem == "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
                n = "_" + n;
            return n;
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
                if (Interlocked.Increment(ref _receiving) > 3) { Interlocked.Decrement(ref _receiving); try { c.Close(); } catch { } continue; }   // a few at a time
                new Thread(() => { try { Receive(c); } finally { Interlocked.Decrement(ref _receiving); } }) { IsBackground = true, Name = "SprocketLAN-design-rx" }.Start();
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
                    // one transfer at most (design + picture + shell or settings lines); names, keys and lines are short
                    var r = new Net.SafeReader(new Net.LimitedStream(s, MaxBytes + MaxPicture + (2 << 20)), MaxBytes, 4096); var w = new BinaryWriter(s, Encoding.UTF8);
                    w.Write(Process(r, from, 0)); w.Flush();   // by IP: no Steam identity, so never counted as a friend
                }
            }
            catch (Exception e) { MPLog.Warn($"Design receive from {from}: {e.Message}"); Notices.Enqueue($"<color=#f88>Design transfer from {from} failed: {e.Message}</color>"); }
        }

        // =========================================================================================== accept rules
        public class Incoming { public string From, Name; public byte[] Bp, Png; public List<string> Shells; public float At; public bool Settings; public SettingsOffer Offer; }
        static readonly List<Incoming> _pending = new List<Incoming>();
        public static List<Incoming> Pending { get { lock (_pending) return new List<Incoming>(_pending); } }

        /// <summary>Auto-accepted? (AutoAccept: Friends = Steam friends only, Always, Never)</summary>
        static bool AutoAccepts(ulong steamId)
        {
            var mode = Plugin.DesignAutoAccept.Value ?? "Friends";
            if (mode.Equals("Always", StringComparison.OrdinalIgnoreCase)) return true;
            if (mode.Equals("Never", StringComparison.OrdinalIgnoreCase)) return false;
            return steamId != 0 && Net.SteamApi.IsFriend(steamId);
        }

        public static void Accept(Incoming x)
        {
            lock (_pending) _pending.Remove(x);
            try
            {
                if (x.Settings) { PendingSettings = x.Offer; Notices.Enqueue($"Settings from <b>{x.From}</b>: open [{Plugin.DesignKey.Value}] to apply"); return; }
                string msg = Save(x.Name, x.Bp, x.Png);
                if (x.Shells != null && x.Shells.Count > 0) lock (_shellImports) _shellImports.Add((x.Name, x.Shells));
                MPLog.Info($"Design '{x.Name}' from {x.From} accepted: {msg}");
                Notices.Enqueue($"Saved design <b>{x.Name}</b> from {x.From}{(msg.Contains("backup") ? " (old version backed up)" : "")}");
            }
            catch (Exception e) { Notices.Enqueue($"<color=#f88>Saving '{x.Name}' failed: {e.Message}</color>"); }
        }
        public static void Deny(Incoming x) { lock (_pending) _pending.Remove(x); MPLog.Info($"{(x.Settings ? "Settings" : $"Design '{x.Name}'")} from {x.From} refused"); }

        /// <summary>Keep it for the player to decide, or refuse it quietly (AskForOthers off). Returns the reply for the sender.</summary>
        static string Hold(Incoming x)
        {
            if (!Plugin.DesignAskOthers.Value)
            {
                MPLog.Info($"{(x.Settings ? "Settings" : $"Design '{x.Name}'")} from {x.From} refused (not auto-accepted, prompts off)");
                return "ERROR: they don't accept transfers from you";
            }
            lock (_pending)
            {
                _pending.RemoveAll(p => p.From == x.From && p.Name == x.Name && p.Settings == x.Settings);   // a re-send replaces the old one
                if (_pending.Count >= 8) _pending.RemoveAt(0);                                             // spam: oldest dropped
                _pending.Add(x);
            }
            Notices.Enqueue($"<b>{x.From}</b> wants to send you {(x.Settings ? "their settings" : $"the design <b>{x.Name}</b>")}: open [{Plugin.DesignKey.Value}] to accept or deny");
            return "OK: waiting for them to accept";
        }

        /// <summary>Reads one transfer (design or settings) and applies the accept rules. Returns the reply for the sender.</summary>
        static string Process(BinaryReader r, string from, ulong steamId)
        {
            {
                {
                    uint magic = r.ReadUInt32();
                    if (magic == SettingsMagic) return ReceiveSettings(r, from, steamId);
                    int ver = magic == Magic ? r.ReadInt32() : -1;
                    if (magic != Magic || (ver != 1 && ver != 2)) return "ERROR: not a SprocketLAN design transfer (or different mod version)";
                    string sender = Net.Safe.Text(r.ReadString(), 32); string name = SafeName(r.ReadString());
                    int len = r.ReadInt32(); if (len <= 0 || len > MaxBytes) return "ERROR: bad size";
                    var bp = r.ReadBytes(len); if (bp.Length != len) throw new IOException("truncated design");
                    int plen = r.ReadInt32(); byte[] png = null;
                    if (plen < 0 || plen > MaxPicture) return "ERROR: bad picture size";
                    if (plen > 0) { png = r.ReadBytes(plen); if (png.Length != plen) png = null; }
                    List<string> shellLines = null;
                    if (ver >= 2) { int nl = r.ReadInt32(); if (nl >= 0 && nl < 1000) { shellLines = new List<string>(); for (int i = 0; i < nl; i++) shellLines.Add(r.ReadString()); } }

                    string who = string.IsNullOrWhiteSpace(sender) ? from : (from == sender ? sender : $"{sender} ({from})");
                    var x = new Incoming { From = who, Name = name, Bp = bp, Png = png, Shells = shellLines, At = 0f };
                    if (!AutoAccepts(steamId)) return Hold(x);
                    string msg = Save(name, bp, png);
                    if (shellLines != null && shellLines.Count > 0) { lock (_shellImports) _shellImports.Add((name, shellLines)); msg += $" + {shellLines.Count} shell settings"; }
                    MPLog.Info($"Design '{name}' received from {who}: {msg}");
                    Notices.Enqueue($"Received design <b>{name}</b> from {who}{(msg.Contains("backup") ? " (old version backed up)" : "")}");
                    return "OK: " + msg;
                }
            }
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
                if (cur != val) ch.Add($"{key}: {cur} -> {Net.Safe.Text(val, 60)}");
            }
            return ch;
        }

        static string ReceiveSettings(BinaryReader r, string from, ulong steamId)
        {
            if (r.ReadInt32() != SettingsVersion) return "ERROR: different settings format (update the mod)";
            string sender = Net.Safe.Text(r.ReadString(), 32); int n = r.ReadInt32();
            if (n < 0 || n > 2000) return "ERROR: bad settings";
            var vals = new List<(string, string, string)>();
            for (int i = 0; i < n; i++) vals.Add((r.ReadString(), r.ReadString(), r.ReadString()));
            string who = string.IsNullOrWhiteSpace(sender) ? from : (from == sender ? sender : $"{sender} ({from})");
            var ch = Diff(vals);
            if (ch.Count == 0) { Notices.Enqueue($"Settings from <b>{who}</b>: already the same as yours"); return "OK: their settings are already the same"; }
            var offer = new SettingsOffer { From = who, Values = vals, Changes = ch };
            MPLog.Info($"Settings received from {who}: {ch.Count} differences ({string.Join("; ", ch)})");
            if (!AutoAccepts(steamId)) return Hold(new Incoming { From = who, Name = "settings", Settings = true, Offer = offer });
            PendingSettings = offer;   // still applied only by the player (APPLY / DISCARD)
            Notices.Enqueue($"<b>{who}</b> sent you their settings ({ch.Count} differences): open [{Plugin.DesignKey.Value}] to apply");
            return $"OK: {ch.Count} differences, waiting for them to apply";
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

        // =========================================================================================== through the game session
        // To a player in the session (any connection: TCP, Steam, mixed): the same transfer as by IP, compressed, in
        // pieces through the host. Channel messages carry: [int id][ushort part][ushort parts][ulong sender Steam id][bytes].
        const string XferChannel = "SprocketLAN.xfer", ReplyChannel = "SprocketLAN.xferReply";
        const int Piece = 200 * 1024;
        static int _nextXfer = 1;
        sealed class Assembly { public byte[][] Parts; public int Got; public ulong Steam; public float At; }
        static readonly Dictionary<(byte, int), Assembly> _assembling = new Dictionary<(byte, int), Assembly>();

        public static void InitSession()
        {
            SprocketLAN.Api.Lan.RegisterChannel(XferChannel, OnPiece);
            SprocketLAN.Api.Lan.RegisterChannel(ReplyChannel, (from, data) =>
            {
                try { var r = new Net.SafeReader(data); r.ReadInt32(); string reply = Net.Safe.Text(r.ReadString(), 300); string who = Runner.Battle?.NameOf(from) ?? ("player " + from);
                      SendStatus = reply.StartsWith("OK") ? $"<color=#8f8>{who}: {reply.Substring(3).Trim()}</color>" : $"<color=#f88>{who}: {reply.Replace("ERROR:", "").Trim()}</color>"; MPLog.Info($"Transfer to {who}: {reply}"); }
                catch { }
            });
        }

        static void SendBlob(byte target, byte[] blob, string what)
        {
            if (!SprocketLAN.Api.Lan.Connected) { SendStatus = "<color=#f88>Not connected</color>"; return; }
            byte[] packed; using (var ms = new MemoryStream()) { using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, true)) gz.Write(blob, 0, blob.Length); packed = ms.ToArray(); }
            int id = _nextXfer++, parts = Math.Max(1, (packed.Length + Piece - 1) / Piece);
            ulong me = Net.SteamNet.Available ? Net.SteamApi.MySteamId : 0;
            for (int i = 0; i < parts; i++)
            {
                int off = i * Piece, n = Math.Min(Piece, packed.Length - off);
                var ms = new MemoryStream(); var w = new BinaryWriter(ms);
                w.Write(id); w.Write((ushort)i); w.Write((ushort)parts); w.Write(me); w.Write(packed, off, n);
                if (!SprocketLAN.Api.Lan.SendTo(target, XferChannel, ms.ToArray())) { SendStatus = "<color=#f88>Send failed (not connected?)</color>"; return; }
            }
            string who = Runner.Battle?.NameOf(target) ?? ("player " + target);
            SendStatus = $"Sent {what} to {who} ({packed.Length / 1024} KB packed), waiting for their answer...";
            MPLog.Info($"{what} sent to {who} through the game connection ({blob.Length / 1024} KB, {packed.Length / 1024} KB packed, {parts} parts)");
        }

        static void OnPiece(byte from, byte[] data)
        {
            try
            {
                var r = new Net.SafeReader(data);
                int id = r.ReadInt32(); int part = r.ReadUInt16(), parts = r.ReadUInt16(); ulong steam = r.ReadUInt64();
                if (parts == 0 || parts > (MaxBytes + MaxPicture) / Piece + 2 || part >= parts || data.Length - 16 > Piece) return;
                // the Steam id is the one the host saw (Steam players), not what the sender wrote: a player can't pass for someone's friend
                steam = Runner.Session?.SteamIdOf(from) ?? 0;
                var key = (from, id);
                // forget transfers that never finished
                foreach (var k in _assembling.Where(kv => UnityEngine.Time.realtimeSinceStartup - kv.Value.At > 120f).Select(kv => kv.Key).ToList()) _assembling.Remove(k);
                if (!_assembling.TryGetValue(key, out var a))
                {
                    if (_assembling.Count > 16 || _assembling.Keys.Count(k => k.Item1 == from) >= 2) return;   // a few transfers at a time, 2 per sender
                    _assembling[key] = a = new Assembly { Parts = new byte[parts][], Steam = steam };
                }
                a.At = UnityEngine.Time.realtimeSinceStartup;
                if (a.Parts.Length != parts || a.Parts[part] != null) return;
                a.Parts[part] = r.ReadBytes(data.Length - 16); a.Got++;
                if (a.Got < parts) return;
                _assembling.Remove(key);
                var packed = a.Parts.SelectMany(x => x).ToArray();
                byte[] blob = Net.Safe.Gunzip(packed, MaxBytes + MaxPicture + (1 << 20));
                string who = Runner.Battle?.NameOf(from) ?? ("player " + from);
                string reply;
                try { reply = Process(new Net.SafeReader(blob), who, a.Steam); }
                catch (Exception e) { reply = "ERROR: " + e.Message; MPLog.Warn($"Transfer from {who}: {e.Message}"); }
                var rm = new MemoryStream(); var rw = new BinaryWriter(rm, Encoding.UTF8); rw.Write(id); rw.Write(reply ?? "");
                SprocketLAN.Api.Lan.SendTo(from, ReplyChannel, rm.ToArray());
            }
            catch (Exception e) { MPLog.Warn("Transfer piece: " + e.Message); }
        }

        static byte[] DesignBlob(Design d, string sender, List<string> shells)
        {
            var bp = File.ReadAllBytes(d.Path);
            var profPath = Path.Combine(Path.GetDirectoryName(d.Path), "Profiles", d.Name + ".png");
            byte[] png = File.Exists(profPath) ? File.ReadAllBytes(profPath) : null;
            var ms = new MemoryStream(); var w = new BinaryWriter(ms, Encoding.UTF8);
            w.Write(Magic); w.Write(Version); w.Write(sender ?? ""); w.Write(d.Name);
            w.Write(bp.Length); w.Write(bp);
            w.Write(png?.Length ?? 0); if (png != null) w.Write(png);
            w.Write(shells.Count); foreach (var l in shells) w.Write(l);
            w.Flush(); return ms.ToArray();
        }

        /// <summary>A design to a player in the session (through the host, any connection type).</summary>
        public static void SendToPlayer(Design d, byte player)
        {
            if (d == null) return;
            try { SendBlob(player, DesignBlob(d, Plugin.EffectiveName, Shells.ShellStore.Export(d.Name)), $"design '{d.Name}'"); }
            catch (Exception e) { SendStatus = $"<color=#f88>Send failed: {e.Message}</color>"; }
        }
        /// <summary>Our shared settings to a player in the session.</summary>
        public static void SendSettingsToPlayer(byte player)
        {
            var vals = SharedSettings().Select(e => (e.Definition.Section, e.Definition.Key, BepInEx.Configuration.TomlTypeConverter.ConvertToString(e.BoxedValue, e.SettingType))).ToList();
            var ms = new MemoryStream(); var w = new BinaryWriter(ms, Encoding.UTF8);
            w.Write(SettingsMagic); w.Write(SettingsVersion); w.Write(Plugin.EffectiveName ?? ""); w.Write(vals.Count);
            foreach (var (a, b, v) in vals) { w.Write(a); w.Write(b); w.Write(v ?? ""); }
            w.Flush();
            SendBlob(player, ms.ToArray(), "your settings");
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
