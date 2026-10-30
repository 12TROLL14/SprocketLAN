using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SprocketMP;
using SprocketMP.Net;
using UnityEngine;

namespace SprocketLAN.Api
{
    /// <summary>A player in the current session (see <see cref="Lan.Players"/>).</summary>
    public sealed class PlayerInfo
    {
        public byte Id { get; internal set; }
        public string Name { get; internal set; } = "";
        public bool IsLocal { get; internal set; }
        public bool IsHost { get; internal set; }
        public bool InBattle { get; internal set; }
        /// <summary>Design name of the tank they are driving ("" in menus).</summary>
        public string Vehicle { get; internal set; } = "";
        /// <summary>Their tank in OUR scene (the puppet), or our own tank for the local player; null if none.</summary>
        public GameObject Tank { get; internal set; }
        public int PingMs { get; internal set; } = -1;
        public override string ToString() => $"{Name} ({Id})";
    }

    /// <summary>One shot: fired by us (<see cref="Lan.LocalShot"/>) or replayed on a puppet (<see cref="Lan.RemoteShot"/>).</summary>
    public sealed class ShotInfo
    {
        internal FireMsg Msg;
        /// <summary>Player id of the shooter.</summary>
        public byte ShooterId => Msg.Id;
        /// <summary>Index of the weapon in the shooter's tank (blueprint order).</summary>
        public int Weapon => Msg.Weapon;
        public Vector3 Position => Msg.Pos;
        public Vector3 Direction => Msg.Dir;
        public Vector3 Velocity => Msg.Vel;
        /// <summary>The game's projectile type GUID of the round.</summary>
        public string ProjectileGuid => Msg.ProjectileGuid;
        /// <summary>SprocketLAN shell kind (0 = the game's own round, 1+ = a registered IShellHandler).</summary>
        public ushort ShellKind => Msg.ShellKind;
        /// <summary>Shooter's tank in this scene: ours for LocalShot, the shooter's puppet for RemoteShot.</summary>
        public GameObject Tank { get; internal set; }
        /// <summary>The game's projectile id (ProjectileRegister) of the round launched on THIS machine, 0 if unknown
        /// (or if a shell handler launched it its own way).</summary>
        public int ProjectileId { get; internal set; }
        /// <summary>True for a shot replayed from another player.</summary>
        public bool IsRemote { get; internal set; }

        /// <summary>LocalShot only: attach data under a key of your own (e.g. "MyMod.fuse"); it arrives with the shot
        /// on every other machine (<see cref="TryGet"/> in RemoteShot). Keep it small: it travels with every shot.</summary>
        public void Attach(string key, byte[] data)
        {
            if (IsRemote) throw new InvalidOperationException("Attach only works on your own shots (LocalShot)");
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("key");
            if (data == null) data = Array.Empty<byte>();
            if (data.Length > 60000) throw new ArgumentException("shot attachments are limited to 60000 bytes each");
            Msg.Extras ??= new Dictionary<string, byte[]>();
            Msg.Extras[key] = data;
        }
        /// <summary>Data attached to this shot by the shooter's copy of a mod.</summary>
        public bool TryGet(string key, out byte[] data)
        {
            data = null;
            return Msg.Extras != null && Msg.Extras.TryGetValue(key, out data);
        }
        public IReadOnlyDictionary<string, byte[]> Attachments => Msg.Extras ?? (IReadOnlyDictionary<string, byte[]>)new Dictionary<string, byte[]>();
    }

    /// <summary>Receives a message on a channel: who sent it and the bytes.</summary>
    public delegate void MessageHandler(byte senderId, byte[] data);

    /// <summary>
    /// SprocketLAN's public API: lets other mods use the multiplayer session (see API.md next to the DLL).
    /// Reference SprocketLAN.dll and add [BepInDependency("SprocketLAN", BepInDependency.DependencyFlags.SoftDependency)].
    /// Everything here is main-thread only (call it from Unity's Update / your patches, never from another thread).
    /// </summary>
    public static class Lan
    {
        /// <summary>Bumped when this API changes in a way that matters to callers.</summary>
        public const int ApiVersion = 1;
        public static string ModVersion => Plugin.VERSION;

        // ------------------------------------------------------------------ session
        static Session S => Session.Current;
        /// <summary>In a session (host, or a client that finished joining).</summary>
        public static bool Connected => S != null && S.Active && (S.IsHost || S.LocalId != 255);
        public static bool IsHost => S != null && S.IsHost;
        /// <summary>Our player id (host = 0), 255 when not connected.</summary>
        public static byte LocalPlayerId => Connected ? (S.IsHost ? (byte)0 : S.LocalId) : (byte)255;
        /// <summary>We are in a battle (driving a tank).</summary>
        public static bool InBattle => BattleManager.Instance?.InBattle ?? false;
        /// <summary>Our own tank in the battle, null in menus.</summary>
        public static GameObject LocalTank => BattleManager.Instance?.Local?.GO;

        /// <summary>Everyone in the session, us included (empty when not connected).</summary>
        public static IReadOnlyList<PlayerInfo> Players
        {
            get
            {
                var l = new List<PlayerInfo>(); if (!Connected) return l;
                var bm = BattleManager.Instance;
                l.Add(new PlayerInfo { Id = LocalPlayerId, Name = Plugin.EffectiveName, IsLocal = true, IsHost = S.IsHost, InBattle = bm?.InBattle ?? false, Vehicle = bm?.LocalVehicleName ?? "", Tank = bm?.Local?.GO, PingMs = S.IsHost ? 0 : S.LocalPingMs });
                foreach (var p in S.Players.Values.OrderBy(p => p.Id))
                {
                    GameObject tank = null; if (bm != null && bm.PuppetByPlayer.TryGetValue(p.Id, out var r)) tank = r.GO;
                    l.Add(new PlayerInfo { Id = p.Id, Name = p.Name, IsHost = p.Id == 0, InBattle = p.InBattle, Vehicle = p.Vehicle, Tank = tank, PingMs = p.PingMs });
                }
                return l;
            }
        }
        /// <summary>The tank in our scene driven by this player (their puppet; ours for our own id), null if none.</summary>
        public static GameObject TankOf(byte playerId)
        {
            var bm = BattleManager.Instance; if (bm == null) return null;
            if (playerId == LocalPlayerId) return bm.Local?.GO;
            return bm.PuppetByPlayer.TryGetValue(playerId, out var r) ? r.GO : null;
        }
        /// <summary>Which player drives this tank (any object inside it works): their id, ours for our tank, 255 for none (AI / scenery).</summary>
        public static byte PlayerOf(GameObject obj)
        {
            var bm = BattleManager.Instance; if (bm == null || obj == null) return 255;
            try
            {
                var vb = obj.GetComponentInParent<Sprocket.Vehicles.VehicleBehaviour>(); if (vb == null) return 255;
                if (bm.Local != null && vb.Pointer == bm.Local.Ptr) return Connected ? LocalPlayerId : (byte)0;
                foreach (var kv in bm.PuppetByPlayer) if (kv.Value.Ptr == vb.Pointer) return kv.Key;
            }
            catch { }
            return 255;
        }

        /// <summary>A player joined / left (raised on the main thread, at most once per frame per player).</summary>
        public static event Action<PlayerInfo> PlayerJoined, PlayerLeft;

        // ------------------------------------------------------------------ features
        /// <summary>The mod's shells and armour model are loaded this session.</summary>
        public static bool ShellsEnabled => Plugin.ShellsOn;
        /// <summary>The pen cam is loaded this session.</summary>
        public static bool PenCamEnabled => Plugin.PenCamOn;
        /// <summary>The modelling tools are loaded this session.</summary>
        public static bool ModellingToolsEnabled => Plugin.ModelToolsOn;

        /// <summary>Register a custom shell type (see SprocketMP.Shells.IShellHandler): it is told about our launches
        /// (to claim them and write a payload) and replays them on puppets. Call from your plugin's Load().</summary>
        public static void RegisterShell(SprocketMP.Shells.IShellHandler handler) => SprocketMP.Shells.ShellRegistry.Register(handler);

        // ------------------------------------------------------------------ messages
        static readonly Dictionary<string, MessageHandler> _channels = new Dictionary<string, MessageHandler>(StringComparer.Ordinal);
        /// <summary>Largest message (bytes) <see cref="Send"/> accepts.</summary>
        public const int MaxMessageBytes = 900_000;

        /// <summary>Receive messages sent on this channel by other players' copies of your mod. Use a unique name,
        /// e.g. "MyMod.sync". One handler per channel (registering again replaces it).</summary>
        public static void RegisterChannel(string channel, MessageHandler handler)
        {
            if (string.IsNullOrEmpty(channel) || handler == null) throw new ArgumentException("channel and handler are required");
            _channels[channel] = handler;
        }
        public static void UnregisterChannel(string channel) { if (channel != null) _channels.Remove(channel); }

        /// <summary>Send to every other player (reliable and in order, like everything on SprocketLAN's TCP link).
        /// Returns false when not connected.</summary>
        public static bool Send(string channel, byte[] data) => SendInternal(255, channel, data);
        /// <summary>Send to one player. Returns false when not connected or the id is ours.</summary>
        public static bool SendTo(byte playerId, string channel, byte[] data) => playerId != LocalPlayerId && playerId != 255 && SendInternal(playerId, channel, data);

        static bool SendInternal(byte target, string channel, byte[] data)
        {
            if (!Connected) return false;
            if (string.IsNullOrEmpty(channel)) throw new ArgumentException("channel");
            data ??= Array.Empty<byte>();
            if (data.Length > MaxMessageBytes) throw new ArgumentException($"message too big ({data.Length} bytes, max {MaxMessageBytes}): split it");
            byte me = LocalPlayerId;
            var pkt = Wire.Build(Msg.ModData, w => { w.Write(me); w.Write(target); w.Write(channel); w.Write(data.Length); w.Write(data); });
            if (S.IsHost && target != 255) S.SendTo(target, pkt); else S.Broadcast(pkt);
            return true;
        }

        internal static void OnModData(byte sender, BinaryReader r)
        {
            r.ReadByte(); r.ReadByte(); string ch = r.ReadString(); int n = r.ReadInt32(); var data = r.ReadBytes(n);
            if (!_channels.TryGetValue(ch, out var h)) return;
            try { h(sender, data); } catch (Exception e) { MPLog.Warn($"API channel '{ch}' handler: {e}"); }
        }

        // ------------------------------------------------------------------ shots
        /// <summary>We fired (a battle shot, offline too): attach data with <see cref="ShotInfo.Attach"/>.</summary>
        public static event Action<ShotInfo> LocalShot;
        /// <summary>Another player's shot was just replayed on their puppet in our scene, with what they attached.</summary>
        public static event Action<ShotInfo> RemoteShot;

        internal static void RaiseLocalShot(FireMsg f, VehicleRig tank, int weapon)
        {
            if (LocalShot == null) return;
            var si = new ShotInfo { Msg = f, Tank = tank?.GO, ProjectileId = Patches.LastLocalProjectileId, IsRemote = false };
            foreach (Action<ShotInfo> h in LocalShot.GetInvocationList()) { try { h(si); } catch (Exception e) { MPLog.Warn("API LocalShot handler: " + e); } }
        }
        internal static void RaiseRemoteShot(FireMsg f, VehicleRig puppet, int projectileId)
        {
            if (RemoteShot == null) return;
            var si = new ShotInfo { Msg = f, Tank = puppet?.GO, ProjectileId = projectileId, IsRemote = true };
            foreach (Action<ShotInfo> h in RemoteShot.GetInvocationList()) { try { h(si); } catch (Exception e) { MPLog.Warn("API RemoteShot handler: " + e); } }
        }

        // ------------------------------------------------------------------ per frame
        static readonly HashSet<byte> _known = new HashSet<byte>(); static float _nextTick;
        internal static void Tick()
        {
            if (PlayerJoined == null && PlayerLeft == null) return;
            if (Time.unscaledTime < _nextTick) return; _nextTick = Time.unscaledTime + 0.5f;   // twice a second is plenty
            var now = Connected ? Players.Where(p => !p.IsLocal).ToList() : new List<PlayerInfo>();
            var ids = new HashSet<byte>(now.Select(p => p.Id));
            foreach (var p in now) if (_known.Add(p.Id)) { try { PlayerJoined?.Invoke(p); } catch (Exception e) { MPLog.Warn("API PlayerJoined handler: " + e); } }
            foreach (var id in _known.Where(i => !ids.Contains(i)).ToList())
            {
                _known.Remove(id);
                try { PlayerLeft?.Invoke(new PlayerInfo { Id = id, Name = "Player " + id }); } catch (Exception e) { MPLog.Warn("API PlayerLeft handler: " + e); }
            }
        }
    }
}
