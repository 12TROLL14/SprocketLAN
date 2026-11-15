using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace SprocketMP.Net
{
    /// <summary>A player reached through Steam (SteamNetworkingMessages, through Valve's relays when needed).</summary>
    public class SteamConnection : Connection
    {
        public readonly ulong Peer;
        public float LastHeard;
        public override string Kind => "Steam";
        public SteamConnection(ulong peer) { Peer = peer; LastHeard = Time.realtimeSinceStartup; try { Remote = "Steam: " + SteamApi.FriendName(peer); } catch { Remote = "Steam: " + peer; } }

        // Steam messages are at most 512 KB: bigger ones (designs) go in pieces. [0] whole, [1 total] first piece, [2] middle, [3] last
        const int Piece = 400 * 1024;
        public override void Send(byte[] data)
        {
            if (Closed || !SteamApi.Ready) return;
            if (data.Length + 1 <= Piece) { SteamNet.SendRaw(Peer, 0, data, 0, data.Length); return; }
            for (int off = 0; off < data.Length; off += Piece)
            {
                int n = Math.Min(Piece, data.Length - off);
                byte kind = off == 0 ? (byte)1 : off + n >= data.Length ? (byte)3 : (byte)2;
                SteamNet.SendRaw(Peer, kind, data, off, n, off == 0 ? data.Length : -1);
            }
        }
        public override void Close()
        {
            if (Closed) return;
            Closed = true;
            SteamNet.Forget(this);
            SteamNet.Inbox?.Enqueue(new Inbound { From = this, Disconnect = true });
        }

        internal MemoryStream Assembling; internal int AssemblingTotal;
        internal string LastDiag = "";
    }

    /// <summary>
    /// Steam side of the session: starts Steam (steam_api64.dll next to the mod, Sprocket's app id), pumps it on the main
    /// thread, turns peers into SteamConnections, handles invites (rich presence "connect" + join requests) and timeouts.
    /// </summary>
    public static class SteamNet
    {
        public const string ConnectArg = "+sprocketlan_steam";
        public static bool Available => SteamApi.Ready;
        public static string Problem = "not started";
        public static string MyName => SteamApi.Ready ? SteamApi.PersonaName : "";
        internal static ConcurrentQueue<Connection.Inbound> Inbox;
        static readonly Dictionary<ulong, SteamConnection> _conns = new Dictionary<ulong, SteamConnection>();
        public static bool Accepting;            // hosting: sessions from anyone are accepted
        public static ulong ExpectedHost;        // joining: only this peer
        static bool _tried; static float _startAt = -1f;
        static readonly Dictionary<ulong, float> _reqLogged = new Dictionary<ulong, float>();
        static float _nextDiag;
        /// <summary>Shown in F10 while a Steam connection is being set up.</summary>
        public static string JoinState = "";

        /// <summary>Every 3 s, for Steam connections not through the handshake yet: what Steam says about them.</summary>
        static void Diagnose()
        {
            if (Time.realtimeSinceStartup < _nextDiag) return; _nextDiag = Time.realtimeSinceStartup + 3f;
            JoinState = "";
            foreach (var c in _conns.Values)
            {
                bool joined = c.PlayerId != 255 || (Session.Current != null && !Session.Current.IsHost && Session.Current.LocalId != 255);
                if (joined) continue;
                var (st, ping, relayed, reason, why) = SteamApi.SessionDetail(c.Peer);
                string line = $"{SteamApi.StateText(st)}{(st == 3 ? $", ping {ping} ms, {(relayed ? "through Valve's relays" : "direct")}" : "")}{(reason != 0 ? $", end reason {reason}" : "")}{(why != "" ? ": " + why : "")}";
                JoinState = line;
                if (line != c.LastDiag) { c.LastDiag = line; MPLog.Info($"Steam link with {c.Remote}: {line}"); }
            }
        }
        /// <summary>Invite accepted in Steam (or the game was launched from one): the host's Steam id, picked up by the session.</summary>
        public static ulong PendingJoin;

        public static void Tick()
        {
            if (!Plugin.SteamEnabled.Value) return;
            if (!_tried)
            {
                if (_startAt < 0f) _startAt = Time.realtimeSinceStartup + 2f;
                if (Time.realtimeSinceStartup < _startAt) return;
                _tried = true; Start();
                return;
            }
            if (!SteamApi.Ready) return;
            SteamApi.Pump(null);
            while (SteamApi.DebugLines.TryDequeue(out var dl)) MPLog.Info(dl);
            Receive();
            LobbyTick();
            PendingLobbyTick();
            try { Diagnose(); } catch (Exception e) { MPLog.Debug("steam diag: " + e.Message); }
            float now = Time.realtimeSinceStartup;
            foreach (var c in _conns.Values.ToList()) if (now - c.LastHeard > Mathf.Max(5f, Plugin.SteamTimeout.Value)) { MPLog.Warn($"{c.Remote}: nothing heard for {Plugin.SteamTimeout.Value:0} s, disconnecting"); c.Close(); }
        }

        static void Start()
        {
            try
            {
                var dll = Path.Combine(Path.GetDirectoryName(typeof(SteamNet).Assembly.Location) ?? "", "steam_api64.dll");
                var err = SteamApi.Load(dll);
                if (err == null) err = SteamApi.Init(OnCallback);
                if (err != null) { Problem = err; MPLog.Info("Steam networking off: " + err); return; }
                Problem = null;
                MPLog.Info($"Steam networking on: {SteamApi.PersonaName} ({SteamApi.MySteamId}), app id {SteamApi.AppId}; steam_api64.dll {SteamApi.DllVersion} compatible");
                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i + 1 < args.Length; i++)
                {
                    if (args[i] == ConnectArg && ulong.TryParse(args[i + 1], out var host)) { PendingJoin = host; MPLog.Info("Started from a Steam invite"); }
                    else if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out var lob)) { MPLog.Info("Started from a Steam lobby invite"); JoinLobby(lob, 0); }
                }
            }
            catch (Exception e) { Problem = e.Message; MPLog.Warn("Steam networking failed to start: " + e.Message); }
        }

        /// <summary>Start Steam again (after F10's FIX IT put a steam_api64.dll in place). Only possible when no DLL
        /// was loaded the first time; false = the game has to be restarted.</summary>
        public static bool Retry()
        {
            if (SteamApi.Ready || SteamApi.Loaded) return false;
            _tried = false; _startAt = Time.realtimeSinceStartup + 0.5f; Problem = "starting";
            return true;
        }

        public static void Shutdown() { try { SteamApi.Shutdown(); } catch { } }

        static void OnCallback(int id, IntPtr p, int size)
        {
            switch (id)
            {
                case SteamApi.CB_SessionRequest:
                    unsafe
                    {
                        ulong who = SteamApi.AcceptSessionPeek(p);
                        bool ok = Accepting || who == ExpectedHost || _conns.ContainsKey(who);
                        if (ok) SteamApi.AcceptSession(p);
                        if (!_reqLogged.TryGetValue(who, out var at) || Time.realtimeSinceStartup - at > 10f)   // it can come twice (two callback routes)
                        {
                            _reqLogged[who] = Time.realtimeSinceStartup;
                            MPLog.Info(ok ? $"Steam session request from {SteamApi.FriendName(who)}: accepted" : $"Steam session request from {SteamApi.FriendName(who)}: ignored (not hosting - click HOST GAME first)");
                        }
                        break;
                    }
                case SteamApi.CB_SessionFailed:
                    {
                        ulong who = SteamApi.AcceptSessionPeek(p);   // SteamNetConnectionInfo_t starts with the remote identity
                        MPLog.Warn($"Steam session with {SteamApi.FriendName(who)} failed (left or lost connection)");
                        if (_conns.TryGetValue(who, out var c)) c.Close();
                        break;
                    }
                case SteamApi.CB_LobbyJoinRequested:
                    unsafe
                    {
                        // GameLobbyJoinRequested_t: lobby id, the friend who invited
                        ulong lobby = *(ulong*)p, friend = *(ulong*)((byte*)p + 8);
                        MPLog.Info($"Steam lobby invite from {SteamApi.FriendName(friend)} accepted");
                        JoinLobby(lobby, friend);
                        break;
                    }
                case SteamApi.CB_LobbyDataUpdate:
                    unsafe
                    {
                        ulong lobby = *(ulong*)p;
                        if (lobby == _pendingLobby) ResolvePendingLobby();
                        break;
                    }
                case SteamApi.CB_RichPresenceJoinRequested:
                    unsafe
                    {
                        ulong friend = *(ulong*)p; string connect = SteamApi.Utf8((byte*)p + 8);
                        var parts = connect.Split(' ');
                        if (parts.Length >= 2 && parts[0] == ConnectArg && ulong.TryParse(parts[1], out var host)) { PendingJoin = host; MPLog.Info($"Steam invite from {SteamApi.FriendName(friend)} accepted"); }
                        break;
                    }
            }
        }

        // ---- sending / receiving
        internal static void SendRaw(ulong peer, byte kind, byte[] data, int off, int n, int total = -1)
        {
            int head = kind == 1 ? 5 : 1;
            var buf = new byte[head + n];
            buf[0] = kind; if (kind == 1) BitConverter.TryWriteBytes(new Span<byte>(buf, 1, 4), total);
            Buffer.BlockCopy(data, off, buf, head, n);
            int r = SteamApi.Send(peer, buf);
            if (r != 1 && r != _lastSendErr) { _lastSendErr = r; MPLog.Warn($"Steam send to {SteamApi.FriendName(peer)} failed (EResult {r})"); }
            else if (r == 1) _lastSendErr = 1;
        }

        static int _lastSendErr = 1;
        const int MaxMessage = 1 << 20;
        static readonly List<(ulong from, byte[] data)> _in = new List<(ulong, byte[])>();
        static void Receive()
        {
            for (int loops = 0; loops < 16; loops++)
            {
                _in.Clear();
                if (SteamApi.Receive(_in, 128) == 0) break;
                foreach (var (from, raw) in _in)
                {
                    if (raw.Length < 1) continue;
                    if (!_conns.TryGetValue(from, out var c))
                    {
                        if (!(Accepting || from == ExpectedHost)) continue;   // a stranger, and we're not hosting
                        c = new SteamConnection(from); _conns[from] = c;
                        MPLog.Info("Incoming Steam connection from " + c.Remote);
                    }
                    c.LastHeard = Time.realtimeSinceStartup;
                    byte[] msg = null;
                    switch (raw[0])
                    {
                        case 0: msg = raw.AsSpan(1).ToArray(); break;
                        // pieces of a big message: never more than a TCP frame may hold (1 MB)
                        case 1:
                            {
                                int total = raw.Length >= 5 ? BitConverter.ToInt32(raw, 1) : -1;
                                if (total <= 0 || total > MaxMessage) { c.Assembling = null; break; }
                                c.Assembling = new MemoryStream(total); c.AssemblingTotal = total; c.Assembling.Write(raw, 5, raw.Length - 5);
                                if (c.Assembling.Length > total) c.Assembling = null;
                                break;
                            }
                        case 2:
                        case 3:
                            if (c.Assembling == null) break;
                            if (c.Assembling.Length + raw.Length - 1 > c.AssemblingTotal) { c.Assembling = null; break; }
                            c.Assembling.Write(raw, 1, raw.Length - 1);
                            if (raw[0] == 3) { msg = c.Assembling.ToArray(); c.Assembling = null; }
                            break;
                    }
                    if (msg == null || msg.Length == 0) continue;
                    // ping / pong straight away (as the TCP reader does), so the ping isn't held up by the game
                    if (msg.Length == 9 && msg[0] == (byte)Msg.Ping) { var pong = (byte[])msg.Clone(); pong[0] = (byte)Msg.Pong; c.Send(pong); continue; }
                    if (msg.Length == 9 && msg[0] == (byte)Msg.Pong)
                    {
                        long sent = BitConverter.ToInt64(msg, 1);
                        c.RttMs = (int)Math.Max(0, (System.Diagnostics.Stopwatch.GetTimestamp() - sent) * 1000L / System.Diagnostics.Stopwatch.Frequency);
                    }
                    if (!c.DeliverPublic(Inbox, msg)) c.Close();
                }
            }
        }

        /// <summary>The connection to a peer, created now if needed (joining a host).</summary>
        public static SteamConnection Open(ulong peer)
        {
            if (!_conns.TryGetValue(peer, out var c) || c.Closed) { c = new SteamConnection(peer); _conns[peer] = c; }
            return c;
        }
        internal static void Forget(SteamConnection c) { if (_conns.TryGetValue(c.Peer, out var x) && x == c) _conns.Remove(c.Peer); try { SteamApi.CloseSession(c.Peer); } catch { } }
        public static void CloseAll() { foreach (var c in _conns.Values.ToList()) c.Close(); _conns.Clear(); }

        // ---- lobby invites: the lobby says who hosts; until Steam has sent its data, the inviting friend is the best guess
        static ulong _pendingLobby, _pendingLobbyFriend; static float _pendingLobbyAt;
        static void JoinLobby(ulong lobby, ulong friend)
        {
            if (TryHostOf(lobby, out var host)) { PendingJoin = host; return; }
            _pendingLobby = lobby; _pendingLobbyFriend = friend; _pendingLobbyAt = Time.realtimeSinceStartup;
            SteamApi.RequestLobbyData(lobby);
        }
        static bool TryHostOf(ulong lobby, out ulong host)
        {
            host = 0;
            try { ulong.TryParse(SteamApi.GetLobbyData(lobby, "host"), out host); } catch { }
            return host != 0;
        }
        static void ResolvePendingLobby()
        {
            if (_pendingLobby == 0) return;
            ulong host = TryHostOf(_pendingLobby, out var h) ? h : _pendingLobbyFriend;
            MPLog.Info($"Joining the game of lobby {_pendingLobby}: host {SteamApi.FriendName(host)}");
            _pendingLobby = 0;
            if (host != 0) PendingJoin = host;
        }
        static void PendingLobbyTick()
        {
            if (_pendingLobby != 0 && Time.realtimeSinceStartup - _pendingLobbyAt > 5f) ResolvePendingLobby();   // no data: join whoever invited
        }

        // ---- invites
        public static void AdvertiseHost(bool on)
        {
            if (!SteamApi.Ready) return;
            if (on) { SteamApi.SetRichPresence("connect", $"{ConnectArg} {SteamApi.MySteamId}"); SteamApi.SetRichPresence("status", "Hosting a SprocketLAN game"); }
            else SteamApi.ClearRichPresence();
        }
        public static void OpenInviteDialog() { if (SteamApi.Ready) SteamApi.OpenInviteDialog($"{ConnectArg} {SteamApi.MySteamId}"); }
        public static int InviteFriendsInGame()
        {
            if (!SteamApi.Ready) return 0;
            var fs = SteamApi.FriendsInGame();
            foreach (var f in fs) SteamApi.Invite(f, $"{ConnectArg} {SteamApi.MySteamId}");
            return fs.Count;
        }
        // ---- lobby browser: a public host lists a Steam lobby carrying the game's details
        static ulong _lobby; static bool _creating; static float _nextLobbyUpdate;
        public static void PublishLobby()
        {
            if (!SteamApi.Ready || _lobby != 0 || _creating) return;
            _creating = true;
            SteamApi.CreateLobby(SteamApi.LobbyPublic, id =>
            {
                _creating = false;
                var s = Session.Current;
                if (id == 0) { MPLog.Warn("Steam lobby could not be created: the game won't show in the lobby browser (invites still work)"); return; }
                if (s == null || !s.IsHost || Session.IsPrivate) { SteamApi.LeaveLobby(id); return; }   // stopped meanwhile
                _lobby = id; _nextLobbyUpdate = 0f;
                MPLog.Info($"Listed in the Steam lobby browser (lobby {id})");
                UpdateLobby(true);
            });
        }
        public static void UnpublishLobby() { if (_lobby != 0) { SteamApi.LeaveLobby(_lobby); MPLog.Info("Removed from the Steam lobby browser"); } _lobby = 0; }
        static void UpdateLobby(bool all)
        {
            if (_lobby == 0) return;
            var s = Session.Current; if (s == null) return;
            if (all)
            {
                SteamApi.SetLobbyData(_lobby, "slan", "1");
                SteamApi.SetLobbyData(_lobby, "host", SteamApi.MySteamId.ToString());
                SteamApi.SetLobbyData(_lobby, "name", Plugin.EffectiveName);
                SteamApi.SetLobbyData(_lobby, "ver", Plugin.VERSION);
                SteamApi.SetLobbyData(_lobby, "proto", Plugin.PROTOCOL.ToString());
            }
            SteamApi.SetLobbyData(_lobby, "players", (s.Players.Count + 1).ToString());
            SteamApi.SetLobbyData(_lobby, "battle", (BattleManager.Instance?.InBattle ?? false) ? "1" : "0");
            SteamApi.SetLobbyData(_lobby, "pw", Session.HasPassword ? "1" : "0");
            SteamApi.SetLobbyData(_lobby, "rules", Lobby.Cur.Rules.Code());
            SteamApi.SetLobbyData(_lobby, "map", Lobby.Cur.MapTitle ?? "");
        }
        static void LobbyTick()
        {
            var s = Session.Current;
            if (s == null || !s.IsHost) { if (_lobby != 0) UnpublishLobby(); return; }
            if (Session.IsPrivate) { if (_lobby != 0) UnpublishLobby(); return; }   // switched to private while hosting
            if (_lobby == 0) { PublishLobby(); return; }
            if (Time.realtimeSinceStartup >= _nextLobbyUpdate) { _nextLobbyUpdate = Time.realtimeSinceStartup + 3f; UpdateLobby(false); }
        }

        public static bool SearchingLobbies { get; private set; }
        public static readonly List<Discovery.Found> Lobbies = new List<Discovery.Found>();
        public static void SearchLobbies()
        {
            if (!SteamApi.Ready || SearchingLobbies) return;
            SearchingLobbies = true; Lobbies.Clear();
            SteamApi.FindLobbies("slan", "1", list =>
            {
                SearchingLobbies = false;
                foreach (var id in list)
                {
                    ulong.TryParse(SteamApi.GetLobbyData(id, "host"), out var host);
                    if (host == 0 || host == SteamApi.MySteamId) continue;
                    var f = new Discovery.Found { SteamHost = host, Name = Safe.Text(SteamApi.GetLobbyData(id, "name"), 32), Version = Safe.Text(SteamApi.GetLobbyData(id, "ver"), 16) };
                    int.TryParse(SteamApi.GetLobbyData(id, "proto"), out f.Protocol); int.TryParse(SteamApi.GetLobbyData(id, "players"), out f.Players);
                    f.InBattle = SteamApi.GetLobbyData(id, "battle") == "1"; f.Password = SteamApi.GetLobbyData(id, "pw") == "1";
                    var rc = SteamApi.GetLobbyData(id, "rules"); if (!string.IsNullOrEmpty(rc)) f.Rules = LobbyRules.FromCode(Safe.Text(rc, 64));
                    f.Map = Safe.Text(SteamApi.GetLobbyData(id, "map"), 48);
                    Lobbies.Add(f);
                }
                MPLog.Info($"Steam lobby browser: {Lobbies.Count} game(s)");
            });
        }

        public static string RelayText()
        {
            if (!SteamApi.Ready) return Problem ?? "off";
            return SteamApi.RelayStatusText(SteamApi.RelayStatus());
        }
    }
}
