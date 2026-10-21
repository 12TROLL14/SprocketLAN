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
        public bool InBattle;
        public int Epoch;
        public Connection Conn;            // host only
        public int SpawnSentEpoch = -1;    // host only: epoch for which SpawnPose was sent
        public float LastStateAt = -999f;
        public int PingMs = -1;            // round trip to the host (host measures clients; clients learn others' via the roster)
    }

    /// <summary>
    /// Star topology: host (id 0) + clients (1..n). Clients only talk to the host; the host relays
    /// State/Fire/Health/BattleState from one client to every other client. All game work happens on the main thread
    /// (Pump() is called from MPBehaviour.Update).
    /// </summary>
    public class Session
    {
        public static Session Current;

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

        public event Action<RemotePlayer> PlayerLeft;

        /// <summary>Round-trip time to the host in ms (client), -1 if unknown. Host = 0.</summary>
        public int LocalPingMs = -1;
        static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        double _nextPing, _nextRoster;
        static byte[] PingPacket(Msg type, long ticks) => Wire.Build(type, w => w.Write(ticks));

        // ------------------------------------------------------------------ lifecycle
        public void StartHost(int port)
        {
            Stop();
            try
            {
                _listener = new HostListener(_inbox);
                _listener.Start(port);
                Mode = Role.Host; LocalId = 0;
                Status = $"Hosting on port {port}";
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
            Stop();
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

        public void Stop()
        {
            if (Mode == Role.None) return;
            MPLog.Info("Session stopped");
            _listener?.Stop(); _listener = null;
            _hostConn?.Close(); _hostConn = null;
            foreach (var p in Players.Values) p.Conn?.Close();
            Players.Clear();
            _connecting = null;
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

        public void SendTo(byte playerId, byte[] data)
        {
            if (Mode == Role.Host && Players.TryGetValue(playerId, out var p)) p.Conn?.Send(data);
        }

        // ------------------------------------------------------------------ pump (main thread)
        public void Pump()
        {
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
                    _hostConn = new Connection(t.Result, _inbox);
                    Status = "Connected, handshaking...";
                    _hostConn.Send(Wire.Build(Msg.Hello, w => { w.Write(Plugin.PROTOCOL); w.Write(Plugin.EffectiveName); }));
                }
            }

            if (_listener != null)
                while (_listener.Accepted.TryDequeue(out var c)) MPLog.Info("Incoming connection from " + c.Remote);

            int budget = 2000;
            while (budget-- > 0 && _inbox.TryDequeue(out var msg))
            {
                try
                {
                    if (msg.Disconnect) OnDisconnect(msg.From);
                    else Handle(msg.From, msg.Data);
                }
                catch (Exception e) { MPLog.Error("Handle message: " + e); }
            }
        }

        void OnDisconnect(Connection c)
        {
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
            using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
            var type = (Msg)r.ReadByte();
            var bm = BattleManager.Instance;

            if (Mode == Role.Host)
            {
                var sender = Players.Values.FirstOrDefault(x => x.Conn == from);
                if (type == Msg.Hello)
                {
                    int proto = r.ReadInt32(); string name = r.ReadString();
                    if (proto != Plugin.PROTOCOL)
                    {
                        from.Send(Wire.Build(Msg.Reject, w => w.Write($"Protocol mismatch (host {Plugin.PROTOCOL}, you {proto}). Use the same mod version.")));
                        MPLog.Warn($"Rejected {name}: protocol {proto}");
                        return;
                    }
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
                if (type == Msg.Ping) { from.Send(PingPacket(Msg.Pong, r.ReadInt64())); return; }
                if (type == Msg.Pong) { sender.PingMs = from.RttMs; return; }
                // force the id in relayed messages to the sender's real id (no spoofing)
                if (data.Length > 1 && (type == Msg.State || type == Msg.Fire || type == Msg.Health || type == Msg.BattleState || type == Msg.Chat || type == Msg.Kill || type == Msg.Repair || type == Msg.PenCam))
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
                    case Msg.Reject:
                        Status = "Rejected: " + r.ReadString(); MPLog.Warn(Status);
                        Stop(); Status = "Rejected"; return;
                    case Msg.Roster:
                        ReadRoster(r); return;
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
                        r.ReadByte(); var text = r.ReadString();
                        bm.OnChat(senderId, text);
                        break;
                    }
            }
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
            });
            foreach (var p in Players.Values) p.Conn?.Send(data);
        }

        void ReadRoster(BinaryReader r)
        {
            int n = r.ReadByte();
            var seen = new HashSet<byte>();
            for (int i = 0; i < n; i++)
            {
                var e = new PlayerEntry { Id = r.ReadByte(), Name = r.ReadString(), Vehicle = r.ReadString(), InBattle = r.ReadBoolean(), Epoch = r.ReadInt32(), PingMs = r.ReadUInt16() };
                if (e.Id == LocalId) continue;
                seen.Add(e.Id);
                var p = GetOrCreate(e.Id);
                bool changed = p.Vehicle != e.Vehicle || p.InBattle != e.InBattle || p.Epoch != e.Epoch;
                p.Name = e.Name; p.Vehicle = e.Vehicle; p.InBattle = e.InBattle; p.Epoch = e.Epoch;
                p.PingMs = e.Id == 0 ? LocalPingMs : e.PingMs;
                if (changed) BattleManager.Instance?.OnRemoteBattleState(p);
            }
            foreach (var gone in Players.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                var p = Players[gone]; Players.Remove(gone);
                MPLog.Info($"Player {gone} ({p.Name}) left");
                PlayerLeft?.Invoke(p);
            }
        }
    }
}
