using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace SprocketMP.Net
{
    public class RemotePlayer
    {
        public byte Id;
        public string Name = "";
        public string Vehicle = "";
        public double[] Tokens, TokenAt; public int Flood; public HashSet<Msg> FloodLogged;   // host: per message kind rate limit
        public bool InBattle;
        public int Epoch;
        public Connection Conn;            // host only
        public int SpawnSentEpoch = -1;    // host only: epoch for which SpawnPose was sent
        public float LastStateAt = -999f;
        public int PingMs = -1;            // round trip to the host (host measures clients; clients learn others' via the roster)
        public string Address = "";        // clients: another client's IP as the host sees it (for sending designs / settings)
        public ulong SteamId;              // clients: another client's Steam id as the host's Steam connection to it says (0 = not on Steam)
    }

    /// <summary>
    /// Star topology: host (id 0) + clients (1..n). Clients only talk to the host; the host relays
    /// State/Fire/Health/BattleState from one client to every other client. All game work happens on the main thread
    /// (Pump() is called from MPBehaviour.Update).
    /// </summary>
    public class Session
    {
        public static Session Current;

        // ---- password / visibility (F10). The join password travels as a hash in Hello (an optional field at the end,
        //      so older versions can still join games without a password).
        public static bool IsPrivate => string.Equals(Plugin.HostVisibility.Value, "Private", StringComparison.OrdinalIgnoreCase);
        public static bool HasPassword => !string.IsNullOrEmpty(Plugin.HostPassword.Value);
        public static string PwHash(string pw)
        {
            if (string.IsNullOrEmpty(pw)) return "";
            using var sha = System.Security.Cryptography.SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes("sprocketlan:" + pw))).Replace("-", "");
        }
        static bool FixedTimeEquals(string a, string b)
        {
            var x = Encoding.UTF8.GetBytes(a ?? ""); var y = Encoding.UTF8.GetBytes(b ?? "");
            return x.Length == y.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(x, y);
        }
        // The password never travels, not even as a hash: a host with a password answers the first Hello with a one-time
        // random challenge, and the client proves it knows the password with an HMAC of that challenge (keyed by the
        // password's hash). A sniffed proof can't be replayed (a new challenge each time), and a host without the
        // password learns nothing it could reuse to join a game with that password.
        static byte[] HelloPacket(string proof = "") => Wire.Build(Msg.Hello, w => { w.Write(Plugin.PROTOCOL); w.Write(Plugin.EffectiveName); w.Write(proof ?? ""); });
        static string Proof(string pw, byte[] nonce)
        {
            if (string.IsNullOrEmpty(pw) || nonce == null) return "";
            using var h = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(PwHash(pw)));
            return "p1:" + BitConverter.ToString(h.ComputeHash(nonce)).Replace("-", "");
        }
        readonly Dictionary<Connection, byte[]> _challenges = new Dictionary<Connection, byte[]>();

        public enum Role { None, Host, Client }
        public Role Mode { get; private set; } = Role.None;
        public bool Active => Mode != Role.None;
        public bool IsHost => Mode == Role.Host;
        public byte LocalId { get; private set; } = 255;
        public string Status = "Offline";
        public readonly Dictionary<byte, RemotePlayer> Players = new Dictionary<byte, RemotePlayer>(); // excludes local

        readonly ConcurrentQueue<Connection.Inbound> _inbox = new ConcurrentQueue<Connection.Inbound>();
        HostListener _listener;
        Connection _hostConn;                 // client only
        Task<TcpClient> _connecting;          // client only
        string _connectTarget;
        public string HostAddress => _connectTarget;

        public event Action<RemotePlayer> PlayerLeft;

        /// <summary>Round-trip time to the host in ms (client), -1 if unknown. Host = 0.</summary>
        public int LocalPingMs = -1;
        static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        double _nextPing, _nextRoster;
        static byte[] PingPacket(Msg type, long ticks) => Wire.Build(type, w => w.Write(ticks));

        // ------------------------------------------------------------------ lifecycle
        public void StartHost(int port)
        {
            Stop(); Stats.Reset();
            try
            {
                _listener = new HostListener(_inbox);
                _listener.Start(port);
                Mode = Role.Host; LocalId = 0;
                SteamNet.Inbox = _inbox; SteamNet.Accepting = SteamNet.Available; SteamNet.AdvertiseHost(SteamNet.Available);   // Steam friends can join too
                if (SteamNet.Available && !IsPrivate) SteamNet.PublishLobby();   // listed in the lobby browser
                Status = $"Hosting on port {port}" + (SteamNet.Available ? " and on Steam" : "");
                MPLog.Info(Status);
            }
            catch (Exception e)
            {
                Status = "Host failed: " + e.Message; MPLog.Error(Status);
                _listener = null; Mode = Role.None;
            }
        }

        public void StartClient(string address)
        {
            Stop(); Stats.Reset();
            string host = address.Trim(); int port = Plugin.Port.Value;
            int colon = host.LastIndexOf(':');
            if (colon > 0 && int.TryParse(host.Substring(colon + 1), out var p)) { port = p; host = host.Substring(0, colon); }
            _connectTarget = $"{host}:{port}";
            Status = "Connecting to " + _connectTarget + "...";
            MPLog.Info(Status);
            Mode = Role.Client; LocalId = 255;
            var tcp = new TcpClient();
            _connecting = tcp.ConnectAsync(host, port).ContinueWith(t => { if (t.IsFaulted) throw t.Exception.GetBaseException(); return tcp; });
        }

        /// <summary>Join a host through Steam (from an invite): same protocol, carried by SteamNetworkingMessages.</summary>
        public void StartSteamClient(ulong hostSteamId)
        {
            Stop(); Stats.Reset();
            if (!SteamNet.Available) { Status = "Steam isn't available: " + SteamNet.Problem; MPLog.Warn(Status); return; }
            string name = SteamApi.FriendName(hostSteamId);
            _connectTarget = "Steam: " + name;
            Status = $"Joining {name} through Steam...";
            MPLog.Info(Status);
            Mode = Role.Client; LocalId = 255;
            SteamNet.Inbox = _inbox; SteamNet.ExpectedHost = hostSteamId; SteamNet.Accepting = false;
            _hostConn = SteamNet.Open(hostSteamId);
            _hostConn.Send(HelloPacket());
        }

        public void Stop()
        {
            if (Mode == Role.None) return;
            MPLog.Info("Session stopped");
            SteamNet.Accepting = false; SteamNet.ExpectedHost = 0; SteamNet.AdvertiseHost(false); SteamNet.UnpublishLobby();
            _listener?.Stop(); _listener = null;
            _hostConn?.Close(); _hostConn = null;
            foreach (var p in Players.Values) p.Conn?.Close();
            Players.Clear();
            foreach (var c in _strangers.Keys.ToList()) c.Close();
            _strangers.Clear(); _closeLater.Clear();
            _connecting = null;
            SteamNet.CloseAll();
            Mode = Role.None; LocalId = 255; Status = "Offline";
            while (_inbox.TryDequeue(out _)) { }
        }

        // ------------------------------------------------------------------ sending
        /// <summary>Send a message originating from the local player to everybody else.</summary>
        public void Broadcast(byte[] data)
        {
            if (Mode == Role.Host) { foreach (var p in Players.Values) p.Conn?.Send(data); }
            else if (Mode == Role.Client && LocalId != 255) _hostConn?.Send(data);
        }

        void RelayExcept(byte[] data, Connection except)
        {
            foreach (var p in Players.Values) if (p.Conn != null && p.Conn != except) p.Conn.Send(data);
        }

        /// <summary>A player's Steam id as far as we can trust it: our own Steam connection to them, or (another client)
        /// what the host's Steam connection to them says. 0 = not known to be on Steam.</summary>
        public ulong SteamIdOf(byte id)
        {
            if (Mode == Role.Host) return Players.TryGetValue(id, out var p) && p.Conn is SteamConnection sc ? sc.Peer : 0;
            if (Mode != Role.Client) return 0;
            if (id == 0) return _hostConn is SteamConnection hc ? hc.Peer : 0;
            return Players.TryGetValue(id, out var q) ? q.SteamId : 0;
        }

        public void SendTo(byte playerId, byte[] data)
        {
            if (Mode == Role.Host && Players.TryGetValue(playerId, out var p)) p.Conn?.Send(data);
        }

        // ------------------------------------------------------------------ pump (main thread)
        public void Pump()
        {
            try { SteamNet.Tick(); } catch (Exception e) { MPLog.Warn("Steam: " + e.Message); }
            if (SteamNet.PendingJoin != 0) { var h = SteamNet.PendingJoin; SteamNet.PendingJoin = 0; StartSteamClient(h); }
            double now = Clock.Elapsed.TotalSeconds;
            if (now >= _nextPing)
            {
                _nextPing = now + 1.0;
                var ping = PingPacket(Msg.Ping, System.Diagnostics.Stopwatch.GetTimestamp());
                if (Mode == Role.Host) foreach (var p in Players.Values) p.Conn?.Send(ping);
                else if (Mode == Role.Client && LocalId != 255) _hostConn?.Send(ping);
            }
            if (Mode == Role.Host && Players.Count > 0 && now >= _nextRoster) { _nextRoster = now + 2.0; SendRoster(); }

            if (Mode == Role.Client && _connecting != null && _connecting.IsCompleted)
            {
                var t = _connecting; _connecting = null;
                if (t.IsFaulted || t.IsCanceled)
                {
                    Status = "Connect failed: " + (t.Exception?.GetBaseException().Message ?? "cancelled");
                    MPLog.Warn(Status); Mode = Role.None;
                }
                else
                {
                    _hostConn = new TcpConnection(t.Result, _inbox);
                    Status = "Connected, handshaking...";
                    _hostConn.Send(HelloPacket());
                }
            }

            if (_listener != null)
                while (_listener.Accepted.TryDequeue(out var c)) { MPLog.Info("Incoming connection from " + c.Remote); Stranger(c); }
            if (Mode == Role.Host && now >= _nextStrangerCheck) { _nextStrangerCheck = now + 1.0; CheckStrangers(now); }

            int budget = 2000;
            while (budget-- > 0 && _inbox.TryDequeue(out var msg))
            {
                if (!msg.Disconnect && msg.From != null) System.Threading.Interlocked.Decrement(ref msg.From.Queued);
                if (msg.From != null && msg.From.Closed && !msg.Disconnect) continue;   // cut off already: ignore what it still had queued
                try
                {
                    if (msg.Disconnect) OnDisconnect(msg.From);
                    else Handle(msg.From, msg.Data);
                }
                catch (Exception e)
                {
                    // broken or forged data: logged a few times, then that connection is closed
                    int n = msg.From != null ? ++msg.From.BadMessages : 0;
                    if (n <= 5) MPLog.Error($"Handle message from {msg.From?.Remote}: " + e);
                    if (n >= 50 && msg.From != null && !msg.From.Closed) { MPLog.Warn($"{msg.From.Remote} keeps sending broken messages: disconnected"); msg.From.Close(); }
                }
            }
        }

        // ---- connections that haven't joined (no valid Hello yet): limited in number and time
        readonly Dictionary<Connection, double> _strangers = new Dictionary<Connection, double>();
        readonly List<(Connection c, double at)> _closeLater = new List<(Connection, double)>();
        readonly Dictionary<string, (int fails, double until)> _pwFails = new Dictionary<string, (int, double)>();
        double _nextStrangerCheck;
        const double HelloTimeout = 10.0;
        const int MaxStrangers = 16;
        /// <summary>Most players a host accepts (the host not counted).</summary>
        public static int MaxPlayers => Math.Max(1, Math.Min(64, Plugin.MaxPlayers.Value));

        void Stranger(Connection c)
        {
            if (c == null || c.Closed || _strangers.ContainsKey(c) || Players.Values.Any(p => p.Conn == c)) return;
            if (_strangers.Count >= MaxStrangers) { MPLog.Warn($"Too many connections waiting to join: {c.Remote} refused"); c.Close(); return; }
            _strangers[c] = Clock.Elapsed.TotalSeconds;
        }
        void CheckStrangers(double now)
        {
            foreach (var kv in _strangers.ToList())
                if (kv.Key.Closed) _strangers.Remove(kv.Key);
                else if (now - kv.Value > HelloTimeout) { MPLog.Info($"{kv.Key.Remote} didn't join within {HelloTimeout:0} s: disconnected"); _strangers.Remove(kv.Key); kv.Key.Close(); }
            for (int i = _closeLater.Count - 1; i >= 0; i--)
                if (now >= _closeLater[i].at) { _closeLater[i].c.Close(); _strangers.Remove(_closeLater[i].c); _closeLater.RemoveAt(i); }
        }
        /// <summary>Refuse a joining player: the reason is sent, then the connection is closed (a moment later, so it arrives).</summary>
        void Refuse(Connection c, string reason)
        {
            c.Send(Wire.Build(Msg.Reject, w => w.Write(reason)));
            if (!_closeLater.Any(x => x.c == c)) _closeLater.Add((c, Clock.Elapsed.TotalSeconds + 1.5));
        }
        static string AddressKey(Connection c)
        {
            if (c is SteamConnection sc) return "steam:" + sc.Peer;
            var r = c.Remote ?? "?";
            if (c is TcpConnection) { int i = r.LastIndexOf(':'); if (i > 0) r = r.Substring(0, i); }   // the IP, not the port
            return r;
        }

        void OnDisconnect(Connection c)
        {
            _strangers.Remove(c);
            _challenges.Remove(c);
            if (Mode == Role.Host)
            {
                var p = Players.Values.FirstOrDefault(x => x.Conn == c);
                if (p != null)
                {
                    Players.Remove(p.Id);
                    MPLog.Info($"Player {p.Id} ({p.Name}) left");
                    PlayerLeft?.Invoke(p);
                    SendRoster();
                }
            }
            else if (Mode == Role.Client && c == _hostConn)
            {
                MPLog.Warn("Lost connection to host");
                foreach (var p in Players.Values.ToList()) PlayerLeft?.Invoke(p);
                Players.Clear();
                _hostConn = null; Mode = Role.None; LocalId = 255;
                Status = "Disconnected from host";
            }
        }

        void Handle(Connection from, byte[] data)
        {
            using var r = new SafeReader(data);
            var type = (Msg)r.ReadByte();
            var bm = BattleManager.Instance;

            if (Mode == Role.Host)
            {
                var sender = Players.Values.FirstOrDefault(x => x.Conn == from);
                if (sender == null) Stranger(from);
                if (type == Msg.Hello)
                {
                    if (sender != null) return;                                  // already joined: a second Hello is ignored
                    if (_closeLater.Any(x => x.c == from)) return;               // already refused
                    int proto = r.ReadInt32(); string name = Safe.Text(r.ReadString(), 32);
                    if (name.Length == 0) name = "Player";
                    string pwHash = r.BaseStream.Position < r.BaseStream.Length ? r.ReadString() : "";
                    string addr = AddressKey(from); double now = Clock.Elapsed.TotalSeconds;
                    if (_pwFails.TryGetValue(addr, out var pf) && pf.until > now)
                    {
                        Refuse(from, $"Too many wrong passwords: try again in {Math.Ceiling(pf.until - now)} s");
                        MPLog.Warn($"Rejected {name} ({from.Remote}): locked out after wrong passwords");
                        return;
                    }
                    if (HasPassword && !_challenges.ContainsKey(from) && string.IsNullOrEmpty(pwHash))
                    {   // first Hello of a password game: send the challenge (not a failed try)
                        var nonce = new byte[32]; System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
                        if (_challenges.Count > 64) _challenges.Clear();
                        _challenges[from] = nonce;
                        from.Send(Wire.Build(Msg.Challenge, w => { w.Write((byte)nonce.Length); w.Write(nonce); }));
                        return;
                    }
                    bool pwOk = true;
                    if (HasPassword)
                    {
                        _challenges.TryGetValue(from, out var nonce); _challenges.Remove(from);   // each challenge is used once
                        pwOk = nonce != null && FixedTimeEquals(pwHash, Proof(Plugin.HostPassword.Value, nonce));
                    }
                    if (!pwOk)
                    {
                        if (_pwFails.Count > 2000) _pwFails.Clear();
                        int fails = (_pwFails.TryGetValue(addr, out var f0) ? f0.fails : 0) + 1;
                        _pwFails[addr] = (fails, fails >= 5 ? now + 30.0 * (fails - 4) : 0);   // from the 5th try on: 30 s, 60 s, 90 s... lockout
                        Refuse(from, string.IsNullOrEmpty(pwHash) ? "This game has a password: type it in the Password field, then join" : "Wrong password");
                        MPLog.Warn($"Rejected {name} ({from.Remote}): wrong or missing password ({fails})");
                        return;
                    }
                    if (proto != Plugin.PROTOCOL)
                    {
                        Refuse(from, $"Protocol mismatch (host {Plugin.PROTOCOL}, you {proto}). Use the same mod version.");
                        MPLog.Warn($"Rejected {name}: protocol {proto}");
                        return;
                    }
                    if (Players.Count >= MaxPlayers)
                    {
                        Refuse(from, $"The game is full ({MaxPlayers + 1} players)");
                        MPLog.Warn($"Rejected {name}: game full");
                        return;
                    }
                    _pwFails.Remove(addr);
                    _strangers.Remove(from);
                    byte id = 1; while (Players.ContainsKey(id) && id < 250) id++;
                    var p = new RemotePlayer { Id = id, Name = name, Conn = from };
                    from.PlayerId = id;
                    Players[id] = p;
                    from.Send(Wire.Build(Msg.Welcome, w => w.Write(id)));
                    MPLog.Info($"Player {id} '{name}' joined from {from.Remote}");
                    SendRoster();
                    bm?.OnPlayerJoined(p);
                    return;
                }
                if (sender == null) return; // not handshaked
                // a joined player flooding a message type: the extra ones are neither relayed (to every other player)
                // nor used; a player that keeps flooding far past the limits is disconnected
                if (!RateOk(sender, type))
                {
                    if (++sender.Flood >= 20000) { MPLog.Warn($"Player {sender.Id} '{sender.Name}' floods the host: disconnected"); from.Close(); }
                    return;
                }
                if (type == Msg.Ping) { from.Send(PingPacket(Msg.Pong, r.ReadInt64())); return; }
                if (type == Msg.Pong) { sender.PingMs = from.RttMs; return; }
                // force the id in relayed messages to the sender's real id (no spoofing)
                if (type == Msg.ModData && data.Length > 2)
                {
                    // other mods' messages: to everyone, or to one player (the host forwards it)
                    data[1] = sender.Id; byte target = data[2];
                    if (target == 255) RelayExcept(data, from);
                    else if (target != 0) { SendTo(target, data); return; }
                    r.BaseStream.Position = 1;
                    SprocketLAN.Api.Lan.OnModData(sender.Id, r);
                    return;
                }
                if (data.Length > 1 && (type == Msg.State || type == Msg.Fire || type == Msg.Health || type == Msg.BattleState || type == Msg.Chat || type == Msg.Kill || type == Msg.Repair || type == Msg.PenCam || type == Msg.LobbyDesign || type == Msg.LobbyRequest))
                {
                    data[1] = sender.Id;
                    r.BaseStream.Position = 1;
                    RelayExcept(data, from);
                }
                Dispatch(type, r, sender.Id);
                if (type == Msg.BattleState) SendRoster();
            }
            else if (Mode == Role.Client)
            {
                switch (type)
                {
                    case Msg.Ping:
                        _hostConn?.Send(PingPacket(Msg.Pong, r.ReadInt64())); return;
                    case Msg.Pong:
                        LocalPingMs = _hostConn != null ? _hostConn.RttMs : -1;
                        if (Players.TryGetValue(0, out var hostP)) hostP.PingMs = LocalPingMs;
                        return;
                    case Msg.Welcome:
                        LocalId = r.ReadByte();
                        Status = $"Connected to {_connectTarget} as player {LocalId}";
                        MPLog.Info(Status);
                        bm?.OnConnected();
                        return;
                    case Msg.Challenge:
                        {
                            int nl = r.ReadByte(); var nonce = r.ReadBytes(nl);
                            if (nl < 16) return;
                            if (from != _hostConn) return;
                            _hostConn.Send(HelloPacket(Proof(Plugin.HostPassword.Value, nonce)));   // empty if we have no password: the host says so
                            return;
                        }
                    case Msg.Reject:
                        Status = "Rejected: " + Safe.Text(r.ReadString(), 160); MPLog.Warn(Status);
                        Stop(); Status = "Rejected"; return;
                    case Msg.Roster:
                        ReadRoster(r); return;
                    case Msg.LobbyState:
                        Lobby.OnState(r); return;
                    case Msg.LobbyStart:
                        Lobby.OnStart(r); return;
                    case Msg.SpawnPose:
                        {
                            byte target = r.ReadByte(); int epoch = r.ReadInt32();
                            var pos = r.V3(); var rot = r.Q();
                            if (target == LocalId) bm?.OnSpawnPose(epoch, pos, rot);
                            return;
                        }
                }
                byte senderId = data.Length > 1 ? data[1] : (byte)255;
                if (senderId == LocalId) return;
                Dispatch(type, r, senderId);
            }
        }

        void Dispatch(Msg type, BinaryReader r, byte senderId)
        {
            switch (type)
            {
                case Msg.LobbyPick: if (Mode == Role.Host) Lobby.OnPick(senderId, r); return;
                case Msg.LobbyDesign: Lobby.OnDesign(senderId, r); return;
                case Msg.LobbyRequest: Lobby.OnRequest(senderId, r); return;
                case Msg.ModData: SprocketLAN.Api.Lan.OnModData(senderId, r); return;
            }
            var bm = BattleManager.Instance;
            if (bm == null) return;
            switch (type)
            {
                case Msg.State:
                    {
                        var s = Wire.ReadState(r);
                        s.Id = senderId;
                        bm.OnRemoteState(s);
                        break;
                    }
                case Msg.Fire:
                    {
                        var f = Wire.ReadFire(r); f.Id = senderId;
                        bm.OnRemoteFire(f);
                        break;
                    }
                case Msg.Health:
                    {
                        var h = Wire.ReadHealth(r); h.Id = senderId;
                        bm.OnRemoteHealth(h);
                        break;
                    }
                case Msg.BattleState:
                    {
                        r.ReadByte();
                        bool inBattle = r.ReadBoolean(); int epoch = r.ReadInt32(); string veh = r.ReadString();
                        if (veh.Length > 200) veh = veh.Substring(0, 200);
                        veh = Safe.Text(veh, 200);   // shown in the scoreboard: no rich text / control characters
                        var p = GetOrCreate(senderId);
                        p.InBattle = inBattle; p.Epoch = epoch; p.Vehicle = veh;
                        MPLog.Info($"Player {senderId} ({p.Name}) battle state: inBattle={inBattle} epoch={epoch} vehicle='{veh}'");
                        bm.OnRemoteBattleState(p);
                        break;
                    }
                case Msg.PenCam:
                    {
                        var pr = PenRecord.Read(r); pr.Victim = senderId;
                        bm.OnPenCam(pr);
                        break;
                    }
                case Msg.Repair:
                    {
                        bm.OnRemoteRepair(senderId);
                        break;
                    }
                case Msg.Kill:
                    {
                        var k = Wire.ReadKill(r); k.Victim = senderId;
                        bm.OnRemoteKill(k);
                        break;
                    }
                case Msg.Chat:
                    {
                        r.ReadByte(); var text = Safe.Text(r.ReadString(), 200);
                        bm.OnChat(senderId, text);
                        break;
                    }
            }
        }

        // messages a second (and twice that in a burst) per player and kind; real play is far below these
        static int RateOf(Msg t)
        {
            switch (t)
            {
                case Msg.State: return 90;
                case Msg.Fire: return 160;
                case Msg.Health: return 40;
                case Msg.BattleState: return 10;
                case Msg.PenCam: return 6;
                case Msg.Repair: case Msg.Kill: case Msg.LobbyRequest: return 4;
                case Msg.Chat: return 4;
                case Msg.LobbyPick: return 10;
                case Msg.LobbyDesign: return 40;    // a design's parts (200 KB each)
                case Msg.ModData: return 200;
                default: return 50;
            }
        }
        static bool RateOk(RemotePlayer p, Msg t)
        {
            if (t == Msg.Ping || t == Msg.Pong) return true;
            double now = Clock.Elapsed.TotalSeconds;
            int k = (int)t & 0xff;
            if (p.Tokens == null) { p.Tokens = new double[256]; p.TokenAt = new double[256]; }
            double rate = RateOf(t), cap = rate * 2;
            if (p.TokenAt[k] == 0) { p.Tokens[k] = cap; p.TokenAt[k] = now; }
            p.Tokens[k] = Math.Min(cap, p.Tokens[k] + (now - p.TokenAt[k]) * rate); p.TokenAt[k] = now;
            if (p.Tokens[k] >= 1) { p.Tokens[k] -= 1; return true; }
            if (p.FloodLogged == null) p.FloodLogged = new HashSet<Msg>();
            if (p.FloodLogged.Add(t)) MPLog.Warn($"Player {p.Id} '{p.Name}' sends more than {RateOf(t)} {t} messages a second: extra ones dropped");
            return false;
        }

        RemotePlayer GetOrCreate(byte id)
        {
            if (!Players.TryGetValue(id, out var p)) { p = new RemotePlayer { Id = id, Name = "Player " + id }; Players[id] = p; }
            return p;
        }

        // ------------------------------------------------------------------ roster
        static int RttMs(long sentTicks)
        {
            long dt = Clock.ElapsedTicks - sentTicks;
            return (int)Math.Max(0, Math.Min(65535, dt * 1000.0 / System.Diagnostics.Stopwatch.Frequency));
        }

        public void SendRoster()
        {
            if (Mode != Role.Host) return;
            var bm = BattleManager.Instance;
            var all = new List<PlayerEntry>
            {
                new PlayerEntry { Id = 0, Name = Plugin.EffectiveName, Vehicle = bm?.LocalVehicleName ?? "", InBattle = bm?.InBattle ?? false, Epoch = bm?.Epoch ?? 0 }
            };
            foreach (var p in Players.Values) all.Add(new PlayerEntry { Id = p.Id, Name = p.Name, Vehicle = p.Vehicle, InBattle = p.InBattle, Epoch = p.Epoch, PingMs = (ushort)Math.Max(0, p.PingMs) });
            var data = Wire.Build(Msg.Roster, w =>
            {
                w.Write((byte)all.Count);
                foreach (var e in all) { w.Write(e.Id); w.Write(e.Name ?? ""); w.Write(e.Vehicle ?? ""); w.Write(e.InBattle); w.Write(e.Epoch); w.Write(e.PingMs); }
                // after the entries (older versions stop reading before this): each client's address as seen by the host
                w.Write((byte)Players.Count);
                foreach (var p in Players.Values) { w.Write(p.Id); w.Write(p.Conn?.Remote ?? ""); }
                // then each client's Steam id, from the host's own Steam connection to it (not what the player claims)
                w.Write((byte)Players.Count);
                foreach (var p in Players.Values) { w.Write(p.Id); w.Write(p.Conn is SteamConnection sc ? sc.Peer : 0UL); }
            });
            foreach (var p in Players.Values) p.Conn?.Send(data);
        }

        void ReadRoster(BinaryReader r)
        {
            int n = r.ReadByte();
            var seen = new HashSet<byte>();
            for (int i = 0; i < n; i++)
            {
                var e = new PlayerEntry { Id = r.ReadByte(), Name = Safe.Text(r.ReadString(), 32), Vehicle = r.ReadString(), InBattle = r.ReadBoolean(), Epoch = r.ReadInt32(), PingMs = r.ReadUInt16() };
                if (e.Id == LocalId) continue;
                seen.Add(e.Id);
                var p = GetOrCreate(e.Id);
                bool changed = p.Vehicle != e.Vehicle || p.InBattle != e.InBattle || p.Epoch != e.Epoch;
                p.Name = e.Name; p.Vehicle = e.Vehicle; p.InBattle = e.InBattle; p.Epoch = e.Epoch;
                p.PingMs = e.Id == 0 ? LocalPingMs : e.PingMs;
                if (changed) BattleManager.Instance?.OnRemoteBattleState(p);
            }
            try
            {
                if (r.BaseStream.Position < r.BaseStream.Length)
                {
                    int na = r.ReadByte();
                    for (int i = 0; i < na; i++) { byte id = r.ReadByte(); string addr = Safe.Text(r.ReadString(), 80); if (id != LocalId && Players.TryGetValue(id, out var pa)) pa.Address = addr; }
                    if (r.BaseStream.Position < r.BaseStream.Length)
                    {
                        int ns = r.ReadByte();
                        for (int i = 0; i < ns; i++) { byte id = r.ReadByte(); ulong sid = r.ReadUInt64(); if (id != LocalId && Players.TryGetValue(id, out var ps)) ps.SteamId = sid; }
                    }
                }
            }
            catch { }
            foreach (var gone in Players.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                var p = Players[gone]; Players.Remove(gone);
                MPLog.Info($"Player {gone} ({p.Name}) left");
                PlayerLeft?.Invoke(p);
            }
        }
    }
}
