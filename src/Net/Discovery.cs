using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SprocketMP.Net
{
    /// <summary>
    /// LAN game discovery over UDP broadcast. A host answers "who is hosting?" questions on the discovery port;
    /// the F10 menu's FIND GAMES button broadcasts the question on every network adapter and lists the answers.
    /// Answers are plain text: SPLAN!|protocol|version|tcpPort|hostName|players|inBattle
    /// </summary>
    public static class Discovery
    {
        const string Ask = "SPLAN?1", Answer = "SPLAN!";

        public sealed class Found
        {
            public string Ip; public int Port; public string Name = ""; public string Version = ""; public int Protocol; public int Players; public bool InBattle;
            public string Address => Ip + ":" + Port;
            public bool Compatible => Protocol == Plugin.PROTOCOL;
        }

        // ------------------------------------------------------------------ host side
        static UdpClient _responder; static Thread _respThread; static volatile string _info; static int _respPort;
        static float _nextInfo;

        /// <summary>Main thread, every frame: answer searches while we host.</summary>
        public static void Tick(Session s)
        {
            bool hosting = s != null && s.IsHost && Plugin.DiscoveryAnswer.Value;
            int port = Plugin.DiscoveryPort.Value;
            if (!hosting || port != _respPort) StopResponder();
            if (!hosting) return;
            if (UnityEngine.Time.unscaledTime >= _nextInfo)
            {
                _nextInfo = UnityEngine.Time.unscaledTime + 1f;
                string name = Clean(Plugin.EffectiveName);
                bool inBattle = BattleManager.Instance?.InBattle ?? false;
                _info = $"{Answer}|{Plugin.PROTOCOL}|{Plugin.VERSION}|{Plugin.Port.Value}|{name}|{s.Players.Count + 1}|{(inBattle ? 1 : 0)}";
            }
            if (_responder == null && UnityEngine.Time.unscaledTime >= _retryAt) StartResponder(port);
        }
        static float _retryAt;
        static string Clean(string s) => (s ?? "").Replace("|", "/").Replace("\n", " ");

        static void StartResponder(int port)
        {
            try
            {
                var u = new UdpClient(AddressFamily.InterNetwork);
                u.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                u.Client.Bind(new IPEndPoint(IPAddress.Any, port));
                _responder = u; _respPort = port;
                _respThread = new Thread(() => Respond(u)) { IsBackground = true, Name = "SprocketLAN discovery" };
                _respThread.Start();
                MPLog.Info($"LAN discovery: answering on UDP {port}");
            }
            catch (Exception e) { MPLog.Warn($"LAN discovery: can't listen on UDP {port} ({e.Message}) - others can still join by IP"); _retryAt = UnityEngine.Time.unscaledTime + 30f; }
        }
        static void Respond(UdpClient u)
        {
            var reply = new byte[0];
            while (true)
            {
                try
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    var data = u.Receive(ref from);
                    if (data.Length < Ask.Length || Encoding.UTF8.GetString(data) != Ask) continue;
                    var info = _info; if (info == null) continue;
                    var b = Encoding.UTF8.GetBytes(info);
                    u.Send(b, b.Length, from);
                }
                catch (ObjectDisposedException) { return; }
                catch { if (_responder != u) return; Thread.Sleep(50); }
            }
        }
        static void StopResponder()
        {
            if (_responder == null) return;
            var u = _responder; _responder = null; _respPort = 0;
            try { u.Close(); } catch { }
            MPLog.Info("LAN discovery: stopped answering");
        }
        public static void Shutdown() => StopResponder();

        // ------------------------------------------------------------------ searching
        static readonly object _lock = new object();
        static readonly List<Found> _found = new List<Found>();
        static volatile bool _searching;
        static DateTime _searchedAt = DateTime.MinValue;
        public static bool Searching => _searching;
        public static bool SearchedOnce => _searchedAt != DateTime.MinValue;
        public static List<Found> Results { get { lock (_lock) return _found.ToList(); } }

        /// <summary>Broadcast the question on every adapter and collect answers for a moment (in the background).</summary>
        public static void Search(float seconds = 1.5f)
        {
            if (_searching) return;
            _searching = true;
            lock (_lock) _found.Clear();
            int port = Plugin.DiscoveryPort.Value;
            var t = new Thread(() => SearchThread(port, seconds)) { IsBackground = true, Name = "SprocketLAN search" };
            t.Start();
        }

        static void SearchThread(int port, float seconds)
        {
            try
            {
                using var u = new UdpClient(AddressFamily.InterNetwork);
                u.EnableBroadcast = true;
                u.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                u.Client.ReceiveTimeout = 250;
                var ask = Encoding.UTF8.GetBytes(Ask);
                var targets = BroadcastAddresses();
                var mine = LocalAddresses();
                var until = DateTime.UtcNow.AddSeconds(seconds);
                int sent = 0;
                while (DateTime.UtcNow < until)
                {
                    if (sent < 2 && (sent == 0 || DateTime.UtcNow > until.AddSeconds(-seconds * 0.5)))   // ask twice (UDP can drop one)
                    {
                        foreach (var a in targets) { try { u.Send(ask, ask.Length, new IPEndPoint(a, port)); } catch { } }
                        sent++;
                    }
                    try
                    {
                        var from = new IPEndPoint(IPAddress.Any, 0);
                        var data = u.Receive(ref from);
                        var f = Parse(Encoding.UTF8.GetString(data), from.Address);
                        if (f == null) continue;
                        if (mine.Contains(from.Address.ToString())) f.Ip = "127.0.0.1";   // our own host
                        lock (_lock) if (!_found.Any(x => x.Address == f.Address)) _found.Add(f);
                    }
                    catch (ObjectDisposedException) { break; }
                    catch { }   // receive timeout
                }
                MPLog.Info($"LAN discovery: searched {targets.Count} network(s), found {_found.Count} game(s)");
            }
            catch (Exception e) { MPLog.Warn("LAN discovery search failed: " + e.Message); }
            finally { _searchedAt = DateTime.UtcNow; _searching = false; }
        }

        static Found Parse(string s, IPAddress from)
        {
            var p = s.Split('|');
            if (p.Length < 7 || p[0] != Answer) return null;
            var f = new Found { Ip = from.ToString(), Version = p[2], Name = p[4] };
            int.TryParse(p[1], out f.Protocol); int.TryParse(p[3], out f.Port); int.TryParse(p[5], out f.Players); f.InBattle = p[6] == "1";
            return f.Port > 0 ? f : null;
        }

        /// <summary>255.255.255.255 plus each IPv4 adapter's subnet broadcast (Windows only sends the global one on one adapter).</summary>
        static List<IPAddress> BroadcastAddresses()
        {
            var l = new List<IPAddress> { IPAddress.Broadcast };
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        var ip = ua.Address.GetAddressBytes(); var mask = ua.IPv4Mask.GetAddressBytes();
                        if (mask.All(b => b == 0)) continue;
                        var bc = new byte[4]; for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                        var a = new IPAddress(bc); if (!l.Contains(a)) l.Add(a);
                    }
                }
            }
            catch (Exception e) { MPLog.Debug("LAN discovery adapters: " + e.Message); }
            l.Add(IPAddress.Loopback);   // a host on this PC
            return l;
        }
        static HashSet<string> LocalAddresses()
        {
            var h = new HashSet<string> { "127.0.0.1" };
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork) h.Add(ua.Address.ToString());
            }
            catch { }
            return h;
        }
    }
}
