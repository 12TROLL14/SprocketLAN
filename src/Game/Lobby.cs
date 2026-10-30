using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using SprocketMP.Net;
using UnityEngine;

namespace SprocketMP
{
    /// <summary>
    /// Multiplayer lobby: every player picks only their own tank and team and presses READY; the host picks the map
    /// and where each team spawns, then STARTs. Each player's designs travel over the game connection into a separate
    /// multiplayer folder, and on START every machine writes its own custom battle setup (its own tank on the player
    /// team, every other player's tank on the AI team), opens Custom Battle and confirms it. In battle each tank is
    /// moved to its team's spawn (teammates side by side).
    /// </summary>
    public static class Lobby
    {
        // ============================================================================================ state
        public const int MaxTeams = 4;
        public const byte SpawnAttacker = 0, SpawnDefender = 1, SpawnCustom = 2;

        public class Spawn { public byte Kind; public Vector3 Pos; }
        public class Slot { public byte Id; public string Name = ""; public byte Team; public string Design = "", Hash = ""; public bool Ready, HaveAll, InBattle; }
        public class State
        {
            public string Map = "", MapTitle = "", Env = "";
            public byte Teams = 2;
            public Spawn[] Spawns = NewSpawns();
            public int Token;
            public List<Slot> Slots = new List<Slot>();

            public void Write(BinaryWriter w)
            {
                w.Write(Map ?? ""); w.Write(MapTitle ?? ""); w.Write(Env ?? ""); w.Write(Teams); w.Write(Token);
                for (int i = 0; i < MaxTeams; i++) { w.Write(Spawns[i].Kind); w.W(Spawns[i].Pos); }
                w.Write((byte)Slots.Count);
                foreach (var s in Slots)
                {
                    w.Write(s.Id); w.Write(s.Name ?? ""); w.Write(s.Team); w.Write(s.Design ?? ""); w.Write(s.Hash ?? "");
                    w.Write((byte)((s.Ready ? 1 : 0) | (s.HaveAll ? 2 : 0) | (s.InBattle ? 4 : 0)));
                }
            }
            public static State Read(BinaryReader r)
            {
                var st = new State { Map = r.ReadString(), MapTitle = r.ReadString(), Env = r.ReadString(), Teams = r.ReadByte(), Token = r.ReadInt32() };
                for (int i = 0; i < MaxTeams; i++) st.Spawns[i] = new Spawn { Kind = r.ReadByte(), Pos = r.V3() };
                int n = r.ReadByte();
                for (int i = 0; i < n; i++)
                {
                    var s = new Slot { Id = r.ReadByte(), Name = r.ReadString(), Team = r.ReadByte(), Design = r.ReadString(), Hash = r.ReadString() };
                    byte f = r.ReadByte(); s.Ready = (f & 1) != 0; s.HaveAll = (f & 2) != 0; s.InBattle = (f & 4) != 0;
                    st.Slots.Add(s);
                }
                return st;
            }
            public byte[] Bytes() { using var ms = new MemoryStream(); using var w = new BinaryWriter(ms, Encoding.UTF8); Write(w); w.Flush(); return ms.ToArray(); }
        }
        static Spawn[] NewSpawns() => new[] { new Spawn { Kind = SpawnAttacker }, new Spawn { Kind = SpawnDefender }, new Spawn { Kind = SpawnAttacker }, new Spawn { Kind = SpawnDefender } };

        /// <summary>The lobby as the host has it (host: authoritative; clients: the last one received).</summary>
        public static State Cur = new State();
        public static string Notice = ""; public static float NoticeAt;
        static void Say(string s) { Notice = s; NoticeAt = Time.realtimeSinceStartup; MPLog.Info("Lobby: " + StripTags(s)); }

        public static string StripTags(string s) { var sb = new StringBuilder(); bool tag = false; foreach (var c in s ?? "") { if (c == '<') tag = true; else if (c == '>') tag = false; else if (!tag) sb.Append(c); } return sb.ToString(); }

        // ---- this player's own choices
        public static byte MyTeam = 255;           // 255 = not chosen yet (default by id)
        public static string MyDesign = "", MyDesignPath = "", MyHash = "";
        public static bool MyReady;
        static float _nextHashCheck, _nextPick, _nextHostSend;
        static byte[] _lastSentState;

        static Session S => Session.Current;
        static byte MyId => S != null && S.Active ? (S.IsHost ? (byte)0 : S.LocalId) : (byte)0;
        public static bool Connected => S != null && S.Active && (S.IsHost || S.LocalId != 255);

        public static byte DefaultTeam(byte id) => (byte)(id % Math.Max(2, (int)Cur.Teams));
        public static byte TeamOf(byte id) { var s = Cur.Slots.FirstOrDefault(x => x.Id == id); return s != null ? s.Team : DefaultTeam(id); }
        public static byte EffectiveMyTeam => MyTeam != 255 && MyTeam < Cur.Teams ? MyTeam : DefaultTeam(MyId);

        // ============================================================================================ folders / designs
        public static string MpFolder
        {
            get
            {
                var cfg = Plugin.LobbyDesignFolder.Value;
                if (!string.IsNullOrWhiteSpace(cfg)) return Environment.ExpandEnvironmentVariables(cfg.Trim());
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Sprocket", "SprocketLAN Multiplayer");
            }
        }
        static string MpPath(string design) => Path.Combine(MpFolder, DesignShare.SafeName(design) + ".blueprint");

        static readonly Dictionary<string, (DateTime t, long len, string h)> _hashCache = new Dictionary<string, (DateTime, long, string)>();
        public static string HashFile(string path)
        {
            try
            {
                var fi = new FileInfo(path); if (!fi.Exists) return "";
                if (_hashCache.TryGetValue(path, out var c) && c.t == fi.LastWriteTimeUtc && c.len == fi.Length) return c.h;
                using var sha = System.Security.Cryptography.SHA1.Create();
                var h = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").Substring(0, 16);
                _hashCache[path] = (fi.LastWriteTimeUtc, fi.Length, h);
                return h;
            }
            catch (Exception e) { MPLog.Debug("hash: " + e.Message); return ""; }
        }
        public static bool HaveDesign(string design, string hash) => !string.IsNullOrEmpty(design) && HashFile(MpPath(design)) == hash;

        /// <summary>Do we have every other player's tank (right version) in the multiplayer folder?</summary>
        static bool HaveAll()
        {
            foreach (var s in Cur.Slots)
            {
                if (s.Id == MyId || string.IsNullOrEmpty(s.Design)) continue;
                if (!HaveDesign(s.Design, s.Hash)) return false;
            }
            return true;
        }

        public static void PickDesign(DesignShare.Design d)
        {
            if (d == null) return;
            MyDesign = d.Name; MyDesignPath = d.Path; MyHash = HashFile(d.Path); MyReady = false;
            Plugin.LobbyLastTank.Value = d.Path;
            Say($"Your tank: <b>{d.Name}</b> - sending it to the other players");
            SendMyDesign(true);
            Changed();
        }
        public static void SetTeam(byte t) { MyTeam = (byte)(t % Math.Max(2, (int)Cur.Teams)); Changed(); }
        public static void ToggleReady()
        {
            if (!MyReady && string.IsNullOrEmpty(MyDesign)) { Say("<color=#f88>Pick your tank first</color>"); return; }
            MyReady = !MyReady; Changed();
        }
        static void Changed() { _nextPick = 0f; _nextHostSend = 0f; _dirty = true; }
        static bool _dirty, _restored; static float _nextTick;

        // ============================================================================================ host settings
        public static void HostSetMap(string key, string title) { if (!S.IsHost) return; Cur.Map = key; Cur.MapTitle = title; Changed(); }
        public static void HostSetTeams(int n) { if (!S.IsHost) return; Cur.Teams = (byte)Mathf.Clamp(n, 2, MaxTeams); Changed(); }
        public static void HostCycleSpawn(int team)
        {
            if (!S.IsHost || team < 0 || team >= MaxTeams) return;
            var sp = Cur.Spawns[team];
            bool hasCustom = sp.Pos != Vector3.zero;
            sp.Kind = (byte)((sp.Kind + 1) % (hasCustom ? 3 : 2));
            Changed();
        }
        public static void HostPlaceSpawn(int team, Vector3 pos)
        {
            if (!S.IsHost || team < 0 || team >= MaxTeams) return;
            Cur.Spawns[team].Kind = SpawnCustom; Cur.Spawns[team].Pos = pos; Changed();
        }
        public static string SpawnText(Spawn s) => s.Kind == SpawnAttacker ? "map spawn A" : s.Kind == SpawnDefender ? "map spawn B" : $"placed ({s.Pos.x:F0}, {s.Pos.z:F0})";

        // ---- maps
        public class MapEntry { public string Key, Title; }
        static List<MapEntry> _maps; static float _nextMapTry;
        public static List<MapEntry> Maps()
        {
            if (_maps != null && _maps.Count > 0) return _maps;
            if (Time.realtimeSinceStartup < _nextMapTry) return _maps ?? new List<MapEntry>();
            _nextMapTry = Time.realtimeSinceStartup + 3f;
            var l = new List<MapEntry>();
            try
            {
                var ld = Sprocket.IScenarioLoader.Instance;
                var list = ld?.LoadedScenarioDefinitions;
                if (list != null)
                {
                    int n = list.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<Sprocket.MapInfo>>().Count;
                    for (int i = 0; i < n; i++)
                    {
                        var mi = list[i];
                        var cfg = mi?.Config; if (cfg == null) continue;
                        string id = cfg.Identifier ?? "", title = mi.Localization?.Name ?? id;
                        var modes = cfg.GameModes;
                        MPLog.Info($"Lobby map: '{id}' ({title}) modes={modes} teams={cfg.TeamCount} splash='{cfg.CustomBattleSplashPath}'");
                        if (string.IsNullOrEmpty(id) || (modes & Sprocket.SupportedGameModes.Sandbox) == 0) continue;
                        l.Add(new MapEntry { Key = id, Title = string.IsNullOrEmpty(title) ? id : title });
                    }
                }
            }
            catch (Exception e) { MPLog.Warn("Lobby map list: " + e.Message); }
            if (l.Count == 0)
            {
                // the last custom battle's map at least
                var last = JsonString(ReadAutosave(), "MapName");
                if (!string.IsNullOrEmpty(last)) l.Add(new MapEntry { Key = last, Title = last });
            }
            _maps = l;
            return l;
        }

        // ============================================================================================ per frame
        public static void Update()
        {
            DriveLaunch();
            if (!_restored)
            {
                _restored = true;
                var last = Plugin.LobbyLastTank.Value;
                if (!string.IsNullOrEmpty(last) && File.Exists(last)) { MyDesignPath = last; MyDesign = Path.GetFileNameWithoutExtension(last); MyHash = HashFile(last); }
            }
            if (!Connected) { Cur.Slots.Clear(); return; }
            float now = Time.realtimeSinceStartup;
            if (now < _nextTick && !_dirty) return;
            _nextTick = now + 0.25f; _dirty = false;
            if (now >= _nextHashCheck && !string.IsNullOrEmpty(MyDesignPath))
            {
                _nextHashCheck = now + 2f;
                var h = HashFile(MyDesignPath);
                if (h != "" && h != MyHash) { MyHash = h; MyReady = false; Say($"<b>{MyDesign}</b> was saved again - sending the new version"); SendMyDesign(true); Changed(); }
            }
            RequestMissing();
            if (S.IsHost) HostTick(now); else ClientTick(now);
        }

        static Dictionary<byte, (byte team, string design, string hash, bool ready, bool have)> _picks = new Dictionary<byte, (byte, string, string, bool, bool)>();

        static void HostTick(float now)
        {
            if (string.IsNullOrEmpty(Cur.Map)) { var ms = Maps(); var last = JsonString(ReadAutosave(), "MapName"); var m = ms.FirstOrDefault(x => x.Key == last) ?? ms.FirstOrDefault(); if (m != null) { Cur.Map = m.Key; Cur.MapTitle = m.Title; } }
            var bm = BattleManager.Instance;
            var slots = new List<Slot> { new Slot { Id = 0, Name = Plugin.EffectiveName, Team = EffectiveMyTeam, Design = MyDesign, Hash = MyHash, Ready = MyReady, HaveAll = HaveAll(), InBattle = bm?.InBattle ?? false } };
            foreach (var p in S.Players.Values.OrderBy(p => p.Id))
            {
                _picks.TryGetValue(p.Id, out var pk);
                byte team = pk.design == null ? DefaultTeam(p.Id) : (pk.team < Cur.Teams ? pk.team : DefaultTeam(p.Id));
                slots.Add(new Slot { Id = p.Id, Name = p.Name, Team = team, Design = pk.design ?? "", Hash = pk.hash ?? "", Ready = pk.ready, HaveAll = pk.have, InBattle = p.InBattle });
            }
            foreach (var k in _picks.Keys.Where(k => !S.Players.ContainsKey(k)).ToList()) _picks.Remove(k);
            Cur.Slots = slots;
            if (now < _nextHostSend) return;
            _nextHostSend = now + 0.25f;
            var bytes = Cur.Bytes();
            bool same = _lastSentState != null && bytes.AsSpan().SequenceEqual(_lastSentState);
            if (same && now - _lastFullSend < 2f) return;
            _lastFullSend = now; _lastSentState = bytes;
            S.Broadcast(Wire.Build(Msg.LobbyState, w => w.Write(bytes)));
        }
        static float _lastFullSend;

        static void ClientTick(float now)
        {
            if (now < _nextPick) return;
            _nextPick = now + 2f;
            byte team = EffectiveMyTeam, id = S.LocalId; string d = MyDesign ?? "", h = MyHash ?? ""; bool rd = MyReady, have = HaveAll();
            S.Broadcast(Wire.Build(Msg.LobbyPick, w => { w.Write(id); w.Write(team); w.Write(d); w.Write(h); w.Write(rd); w.Write(have); }));
        }

        // ============================================================================================ network in
        public static void OnPick(byte sender, BinaryReader r)
        {
            r.ReadByte(); byte team = r.ReadByte(); string d = r.ReadString(), h = r.ReadString(); bool rd = r.ReadBoolean(), have = r.ReadBoolean();
            _picks.TryGetValue(sender, out var old);
            _picks[sender] = (team, d, h, rd, have);
            if (old.design != d || old.ready != rd || old.team != team || old.have != have) Changed();
        }

        public static void OnState(BinaryReader r)
        {
            var st = State.Read(r);
            Cur = st;
            if (MyTeam != 255 && MyTeam >= st.Teams) MyTeam = 255;
        }

        public static void OnStart(BinaryReader r)
        {
            var st = State.Read(r);
            Cur = st;
            Launch(st);
        }

        // ---- designs over the game connection (chunked: frames are limited to 1 MB)
        const int Chunk = 200 * 1024;
        static float _lastDesignSend = -99f;
        static readonly Dictionary<string, byte[][]> _rx = new Dictionary<string, byte[][]>();
        static readonly Dictionary<byte, float> _requested = new Dictionary<byte, float>();

        static void SendMyDesign(bool force)
        {
            if (!Connected || string.IsNullOrEmpty(MyDesignPath)) return;
            float now = Time.realtimeSinceStartup;
            if (!force && now - _lastDesignSend < 5f) return;
            _lastDesignSend = now;
            try
            {
                var bp = File.ReadAllBytes(MyDesignPath);
                var prof = Path.Combine(Path.GetDirectoryName(MyDesignPath), "Profiles", MyDesign + ".png");
                var png = File.Exists(prof) ? File.ReadAllBytes(prof) : Array.Empty<byte>();
                var shells = Shells.ShellStore.Export(MyDesign);
                byte[] payload;
                using (var ms = new MemoryStream())
                {
                    using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
                    {
                        var gz = Gzip(bp); w.Write(gz.Length); w.Write(gz);
                        w.Write(png.Length); w.Write(png);
                        w.Write(shells.Count); foreach (var l in shells) w.Write(l);
                    }
                    payload = ms.ToArray();
                }
                int count = (payload.Length + Chunk - 1) / Chunk; byte id = MyId; string name = MyDesign, hash = MyHash;
                for (int i = 0; i < count; i++)
                {
                    int off = i * Chunk, len = Math.Min(Chunk, payload.Length - off); int idx = i;
                    S.Broadcast(Wire.Build(Msg.LobbyDesign, w => { w.Write(id); w.Write(name); w.Write(hash); w.Write((ushort)idx); w.Write((ushort)count); w.Write(len); w.Write(payload, off, len); }));
                }
                MPLog.Info($"Lobby: sent design '{name}' ({bp.Length / 1024} KB, {payload.Length / 1024} KB packed, {count} parts, {shells.Count} shell settings)");
            }
            catch (Exception e) { Say($"<color=#f88>Could not send {MyDesign}: {e.Message}</color>"); }
        }

        public static void OnDesign(byte sender, BinaryReader r)
        {
            r.ReadByte(); string name = r.ReadString(), hash = r.ReadString(); int idx = r.ReadUInt16(), count = r.ReadUInt16(); int len = r.ReadInt32(); var data = r.ReadBytes(len);
            if (count <= 0 || count > 400 || idx >= count) return;
            string key = sender + "|" + hash;
            if (!_rx.TryGetValue(key, out var parts) || parts.Length != count) { parts = new byte[count][]; _rx[key] = parts; }
            parts[idx] = data;
            if (parts.Any(p => p == null)) return;
            _rx.Remove(key);
            try
            {
                var all = parts.SelectMany(p => p).ToArray();
                using var rr = new BinaryReader(new MemoryStream(all), Encoding.UTF8);
                var bp = Gunzip(rr.ReadBytes(rr.ReadInt32()));
                var png = rr.ReadBytes(rr.ReadInt32());
                int nl = rr.ReadInt32(); var lines = new List<string>(); for (int i = 0; i < nl && i < 1000; i++) lines.Add(rr.ReadString());
                string safe = DesignShare.SafeName(name);
                Directory.CreateDirectory(MpFolder);
                var target = MpPath(name);
                File.WriteAllBytes(target + ".part", bp);
                if (File.Exists(target)) File.Delete(target);
                File.Move(target + ".part", target);
                if (png.Length > 0) { var pd = Path.Combine(MpFolder, "Profiles"); Directory.CreateDirectory(pd); File.WriteAllBytes(Path.Combine(pd, safe + ".png"), png); }
                if (lines.Count > 0) Shells.ShellStore.Import(name, lines);
                string got = HashFile(target);
                string who = BattleManager.Instance?.NameOf(sender) ?? ("player " + sender);
                MPLog.Info($"Lobby: design '{name}' from {who} saved to {target} ({bp.Length / 1024} KB, {lines.Count} shell settings){(got != hash ? $" - HASH MISMATCH {got} vs {hash}" : "")}");
                Say($"Got <b>{name}</b> from {who}");
                _requested.Remove(sender);
                Changed();
            }
            catch (Exception e) { MPLog.Warn($"Lobby design '{name}' from {sender}: {e.Message}"); }
        }

        public static void OnRequest(byte sender, BinaryReader r)
        {
            r.ReadByte(); byte want = r.ReadByte();
            if (want == MyId) SendMyDesign(false);
        }

        static void RequestMissing()
        {
            float now = Time.realtimeSinceStartup;
            foreach (var s in Cur.Slots)
            {
                if (s.Id == MyId || string.IsNullOrEmpty(s.Design) || HaveDesign(s.Design, s.Hash)) continue;
                if (_requested.TryGetValue(s.Id, out var t) && now - t < 8f) continue;
                _requested[s.Id] = now; byte me = MyId, want = s.Id;
                S.Broadcast(Wire.Build(Msg.LobbyRequest, w => { w.Write(me); w.Write(want); }));
            }
        }

        static byte[] Gzip(byte[] b) { using var ms = new MemoryStream(); using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, true)) gz.Write(b, 0, b.Length); return ms.ToArray(); }
        static byte[] Gunzip(byte[] b) { using var src = new GZipStream(new MemoryStream(b), CompressionMode.Decompress); using var ms = new MemoryStream(); src.CopyTo(ms); return ms.ToArray(); }

        // ============================================================================================ start
        /// <summary>Why the host can't start yet ("" = can start).</summary>
        public static string StartBlocker()
        {
            if (!Connected || !S.IsHost) return "only the host starts";
            if (string.IsNullOrEmpty(Cur.Map)) return "pick a map";
            if (Cur.Slots.Count < 2) return "waiting for players";
            foreach (var s in Cur.Slots)
            {
                if (string.IsNullOrEmpty(s.Design)) return $"{s.Name} has no tank";
                if (!s.Ready) return $"{s.Name} is not ready";
                if (!s.HaveAll) return $"{s.Name} is still receiving designs";
                if (s.InBattle) return $"{s.Name} is in a battle (go back to the main menu)";
            }
            return "";
        }

        public static void HostStart(bool force)
        {
            if (!S.IsHost) return;
            string why = StartBlocker();
            if (why != "" && !force) { Say("<color=#f88>Can't start: " + why + "</color>"); return; }
            if (string.IsNullOrEmpty(Cur.Env)) Cur.Env = JsonObject(ReadAutosave(), "Environment");
            Cur.Token++;
            var bytes = Cur.Bytes();
            S.Broadcast(Wire.Build(Msg.LobbyStart, w => w.Write(bytes)));
            MPLog.Info($"Lobby: START (token {Cur.Token}, map {Cur.Map}, {Cur.Slots.Count} players)");
            Launch(Cur);
        }

        // ---- launching the battle on this machine
        public static bool InLobbyBattle;            // set from launch until we're back in the main menu after the battle
        public static State Launched;
        static string _launchJson; static int _phase; static float _phaseAt, _launchStarted; static bool _sawBattle; static int _openTries;

        static void Launch(State st)
        {
            var me = st.Slots.FirstOrDefault(s => s.Id == MyId);
            if (me == null || string.IsNullOrEmpty(MyDesignPath) || !File.Exists(MyDesignPath)) { Say("<color=#f88>START received, but you have no tank picked</color>"); return; }
            if (BattleManager.Instance?.InBattle == true) { Say("<color=#f88>START received while in a battle: go back to the main menu</color>"); return; }
            try
            {
                var others = st.Slots.Where(s => s.Id != MyId && !string.IsNullOrEmpty(s.Design)).GroupBy(s => s.Design, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var g in others) if (!File.Exists(MpPath(g.Key))) { Say($"<color=#f88>Missing the design {g.Key} - can't start</color>"); return; }
                var sb = new StringBuilder();
                sb.Append("{\n  \"MapName\": ").Append(Q(st.Map)).Append(",\n  \"Environment\": ").Append(string.IsNullOrEmpty(st.Env) ? DefaultEnv : st.Env).Append(",\n  \"Teams\": [\n");
                TeamJson(sb, new[] { (MyDesignPath, 1) }, 11);
                sb.Append(",\n");
                TeamJson(sb, others.Select(g => (MpPath(g.Key), g.Count())).ToArray(), 4);
                sb.Append("\n  ]\n}\n");
                _launchJson = sb.ToString();
                var auto = AutosavePath;
                try { var bk = Path.Combine(Path.GetDirectoryName(auto), "CustomBattleSetup.before-lobby.json"); if (File.Exists(auto) && !File.Exists(bk)) File.Copy(auto, bk); } catch { }
                File.WriteAllText(auto, _launchJson);
                MPLog.Info("Lobby: battle setup written to " + auto + "\n" + _launchJson);
            }
            catch (Exception e) { Say("<color=#f88>Could not write the battle setup: " + e.Message + "</color>"); return; }
            Launched = st; InLobbyBattle = true; _sawBattle = false; _natAtt = null; _natDef = null; _spawnEpoch = -1;
            _phase = 1; _phaseAt = _launchStarted = Time.realtimeSinceStartup; _openTries = 0;
            Say($"Starting on <b>{(string.IsNullOrEmpty(st.MapTitle) ? st.Map : st.MapTitle)}</b>...");
            Runner.Menu?.HideLobby();
        }

        const string DefaultEnv = "{ \"TimeCycleFraction\": 0.35, \"CloudMapR\": 0.0, \"CloudMapG\": 1.0, \"CloudMapB\": 0.0, \"CloudMapA\": 0.0, \"FogDistance\": 1500.0 }";
        static void TeamJson(StringBuilder sb, (string path, int count)[] units, int flags)
        {
            sb.Append("    {\n      \"unitDefs\": [");
            for (int i = 0; i < units.Length; i++)
            {
                var p = units[i].path; var icon = Path.Combine(Path.GetDirectoryName(p), "Profiles", Path.GetFileNameWithoutExtension(p) + ".png");
                sb.Append(i > 0 ? "," : "").Append("\n        { \"path\": ").Append(Q(p)).Append(", \"iconPath\": ").Append(Q(icon)).Append(" }");
            }
            sb.Append("\n      ],\n      \"unitInstances\": [");
            for (int i = 0; i < units.Length; i++) sb.Append(i > 0 ? ", " : "").Append("{ \"Count\": ").Append(units[i].count).Append(" }");
            sb.Append($"],\n      \"flags\": {flags},\n      \"paint\": \"\",\n      \"condition\": 0.5,\n      \"dirt\": 0.1,\n      \"budget\": [100000000]\n    }}");
        }
        static string Q(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s ?? "") { if (c == '\\' || c == '"') sb.Append('\\').Append(c); else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); }
            return sb.Append('"').ToString();
        }

        public static string AutosavePath => Path.Combine(Application.persistentDataPath, "CustomBattleSetup.json");
        static string ReadAutosave() { try { return File.Exists(AutosavePath) ? File.ReadAllText(AutosavePath) : ""; } catch { return ""; } }
        /// <summary>"key": "value" from a JSON text (first match).</summary>
        public static string JsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return "";
            int i = json.IndexOf("\"" + key + "\""); if (i < 0) return "";
            i = json.IndexOf(':', i); if (i < 0) return "";
            int a = json.IndexOf('"', i + 1); if (a < 0) return "";
            var sb = new StringBuilder();
            for (int k = a + 1; k < json.Length; k++) { char c = json[k]; if (c == '\\' && k + 1 < json.Length) { sb.Append(json[++k]); continue; } if (c == '"') break; sb.Append(c); }
            return sb.ToString();
        }
        /// <summary>"key": { ... } from a JSON text, braces included.</summary>
        static string JsonObject(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return "";
            int i = json.IndexOf("\"" + key + "\""); if (i < 0) return "";
            int a = json.IndexOf('{', i); if (a < 0) return "";
            int depth = 0;
            for (int k = a; k < json.Length; k++) { if (json[k] == '{') depth++; else if (json[k] == '}' && --depth == 0) return json.Substring(a, k - a + 1); }
            return "";
        }

        /// <summary>Opens Custom Battle from the main menu, loads the written setup and confirms it.</summary>
        static void DriveLaunch()
        {
            if (_phase == 0) return;
            float now = Time.realtimeSinceStartup;
            if (now < _phaseAt) return;
            if (now - _launchStarted > 180f) { _phase = 0; Say("<color=#f88>Battle didn't start: open Custom Battle yourself (the setup is already filled in) and confirm</color>"); return; }
            try
            {
                Sprocket.CustomBattles.CustomBattleCreation cbc = null;
                try { cbc = UnityEngine.Object.FindObjectOfType<Sprocket.CustomBattles.CustomBattleCreation>(); } catch { }
                switch (_phase)
                {
                    case 1:
                        if (cbc != null) { _phase = 2; _phaseAt = now + 0.8f; MPLog.Info("Lobby: Custom Battle screen found"); return; }
                        _phaseAt = now + 1.5f;
                        if (_openTries < 4)
                        {
                            _openTries++;
                            var mm = UnityEngine.Object.FindObjectOfType<Sprocket.MainMenu>();
                            if (mm == null) { MPLog.Info("Lobby: no main menu yet"); return; }
                            Sprocket.MainMenuScreen screen = null;
                            foreach (var s in mm.screens) if (s != null && s.TryCast<Sprocket.CustomBattles.MainMenuCustomBattle>() != null) { screen = s; break; }
                            if (screen == null) { MPLog.Warn("Lobby: the main menu has no Custom Battle screen"); return; }
                            mm.SetActiveMenu(screen);
                            MPLog.Info($"Lobby: opening Custom Battle (try {_openTries})");
                        }
                        else if (_openTries == 4) { _openTries++; Say("Open <b>Custom Battle</b> - the lobby's setup is already filled in and starts by itself"); }
                        return;
                    case 2:
                        if (cbc == null) { _phase = 1; return; }
                        cbc.LoadConfig(_launchJson);
                        MPLog.Info("Lobby: setup loaded into the Custom Battle screen");
                        _phase = 3; _phaseAt = now + 1.0f; return;
                    case 3:
                        if (cbc == null) { _phase = 0; return; }   // gone: the battle is loading
                        var v = cbc.ConfigEdit.Valid;
                        if (!v.Valid) { _phase = 0; Say($"<color=#f88>The game refused the setup: {v.FirstError}</color> - check it in Custom Battle"); return; }
                        cbc.ConfirmButton.Click();
                        MPLog.Info("Lobby: confirmed - battle loading");
                        _phase = 0; return;
                }
            }
            catch (Exception e) { MPLog.Warn($"Lobby launch (phase {_phase}): {e.Message}"); _phaseAt = now + 2f; }
        }

        // ============================================================================================ in battle: spawns
        static (Vector3 p, Quaternion r)? _natAtt, _natDef;
        static int _spawnEpoch = -1; static float _nextMenuCheck;

        public static void BattleTick(BattleManager bm)
        {
            if (!InLobbyBattle || Launched == null) return;
            float now = Time.realtimeSinceStartup;
            if (bm.InBattle) _sawBattle = true;
            else
            {
                if (_sawBattle && now >= _nextMenuCheck)
                {
                    _nextMenuCheck = now + 1f;
                    bool menu = false; try { menu = UnityEngine.Object.FindObjectOfType<Sprocket.MainMenu>() != null; } catch { }
                    if (menu) { InLobbyBattle = false; MPLog.Info("Lobby: back in the main menu"); }
                }
                return;
            }
            if (_spawnEpoch == bm.Epoch || Time.time - bm.EpochStartedAt < 0.6f) return;
            var L = bm.Local; if (L == null || L.Body == null) return;
            // the map's own spawns, seen once per battle: ours (player team = attacker) and the AI team's (defender)
            if (_natAtt == null) _natAtt = (L.InitialPos, L.InitialRot);
            if (_natDef == null)
            {
                var ai = bm.Rigs.Where(r => r != L && r.Team != L.Team).ToList();
                if (ai.Count > 0)
                {
                    var c = Vector3.zero; foreach (var r in ai) c += r.InitialPos; c /= ai.Count;
                    _natDef = (c, ai.OrderBy(r => r.RawName, StringComparer.Ordinal).First().InitialRot);
                }
                else if (Time.time - bm.EpochStartedAt < 4f) return;   // wait for them
            }
            _spawnEpoch = bm.Epoch;
            var st = Launched; var me = st.Slots.FirstOrDefault(s => s.Id == MyId); if (me == null) return;
            var pos = new Vector3[MaxTeams]; var rot = new Quaternion[MaxTeams];
            for (int t = 0; t < MaxTeams; t++)
            {
                var sp = st.Spawns[t];
                if (sp.Kind == SpawnCustom) { pos[t] = sp.Pos; rot[t] = Quaternion.identity; }
                else if (sp.Kind == SpawnDefender && _natDef != null) { pos[t] = _natDef.Value.p; rot[t] = _natDef.Value.r; }
                else { pos[t] = _natAtt.Value.p; rot[t] = _natAtt.Value.r; }
            }
            int team = Mathf.Clamp(me.Team, 0, st.Teams - 1);
            var P = pos[team]; var R = rot[team];
            if (st.Spawns[team].Kind == SpawnCustom)
            {
                // face the other teams (their spawns' centre), else the map's spawns
                var c = Vector3.zero; int n = 0;
                for (int t = 0; t < st.Teams; t++) if (t != team && (pos[t] - P).sqrMagnitude > 25f) { c += pos[t]; n++; }
                if (n == 0 && _natDef != null) { c = (_natAtt.Value.p + _natDef.Value.p) * 0.5f; n = 1; }
                var d = n > 0 ? c / n - P : Vector3.forward; d.y = 0;
                R = d.sqrMagnitude > 1f ? Quaternion.LookRotation(d.normalized, Vector3.up) : Quaternion.identity;
            }
            // teammates side by side (same order on every machine)
            var mates = st.Slots.Where(s => Mathf.Clamp(s.Team, 0, st.Teams - 1) == team).OrderBy(s => s.Id).ToList();
            int k = Math.Max(0, mates.FindIndex(s => s.Id == MyId));
            float gap = Mathf.Max(10f, Plugin.LobbySpawnSpacing.Value);
            float side = ((k + 1) / 2) * gap * (k % 2 == 1 ? 1f : -1f);
            var fwd = R * Vector3.forward; fwd.y = 0; if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward; fwd.Normalize();
            var right = new Vector3(fwd.z, 0, -fwd.x);
            var at = P + right * side;
            MPLog.Info($"Lobby spawn: team {team + 1} ({SpawnText(st.Spawns[team])}) slot {k} -> {at}");
            bm.PlaceLocal(at, Quaternion.LookRotation(fwd, Vector3.up), "Moved to your team's spawn");
            MapCapture.NoteSpawns(st.Map, _natAtt?.p, _natDef?.p);
        }

        /// <summary>Map of the battle being played (for the map overview), "" if unknown.</summary>
        public static string CurrentMap() => InLobbyBattle && Launched != null ? Launched.Map : JsonString(ReadAutosave(), "MapName");
    }
}
