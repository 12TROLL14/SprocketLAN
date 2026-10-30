using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace SprocketMP.Net
{
    public enum Msg : byte
    {
        Hello = 1,        // C->H  name, protocol
        Welcome = 2,      // H->C  yourId
        Roster = 3,       // H->*  players
        BattleState = 4,  // *     id, inBattle, epoch, vehicleName
        State = 5,        // *     id, transform, inputs, aims  (relayed by host)
        Fire = 6,         // *     id, weapon, shell launch data (relayed)
        Health = 7,       // *     id, pool values (relayed)
        SpawnPose = 8,    // H->C  target id, epoch, pose
        Reject = 9,       // H->C  reason
        Chat = 10,        // *     id, text
        Kill = 11,        // *     victim id (sender), killer id, epoch, reason
        Repair = 12,      // *     id (sender) finished a field repair
        Ping = 13,        // H<->C timestamp (never relayed)
        Pong = 14,        // H<->C echoed timestamp (never relayed)
        PenCam = 15,      // *     victim (sender) -> everyone; shown by the shooter (see Game/PenCam.cs)
        LobbyState = 16,  // H->C  the whole lobby (Game/Lobby.cs)
        LobbyPick = 17,   // C->H  sender, team, design, hash, ready, has every design
        LobbyDesign = 18, // *     sender's design, in parts (relayed)
        LobbyRequest = 19,// *     please (re)send your design (relayed)
        LobbyStart = 20,  // H->C  start now, with the final lobby
        ModData = 21,     // *     other mods' messages (Api/Lan.cs): sender, target (255 = all), channel, bytes
    }

    public struct PlayerEntry
    {
        public byte Id; public string Name; public string Vehicle; public bool InBattle; public int Epoch; public ushort PingMs;
    }

    public class VehicleStateMsg
    {
        public byte Id;
        public uint Seq;
        public Vector3 Pos; public Quaternion Rot; public Vector3 Vel; public Vector3 AngVel;
        public float Steer, Throttle;
        public byte Flags;               // bit0 ignition held, bit1 alive
        /// <summary>Local rotation of each aimed transform (turret ring / gun trunnions), index-aligned with the vehicle's GunLayer tasks.</summary>
        public Quaternion[] AimRots = Array.Empty<Quaternion>();
        public float ReceivedAt;         // local Time.time when received (not serialized)
    }

    public class FireMsg
    {
        public byte Id;
        public byte Weapon;              // index into the vehicle's weapon list (blueprint order)
        public string ProjectileGuid = "";
        public Vector3 Pos, Dir, Vel;
        public float BarrelLength; public uint MaxPressure;
        public ushort ShellKind;         // 0 = native game shell; >0 = custom shell handler (see Shells/ShellRegistry)
        public byte[] Payload = Array.Empty<byte>();
        /// <summary>Other mods' data attached to the shot (Api.ShotInfo.Attach), null if none.</summary>
        public System.Collections.Generic.Dictionary<string, byte[]> Extras;
    }

    public class KillMsg
    {
        public byte Victim; public byte Killer = 255; public int Epoch; public byte Reason; // 0 = crew lost, 1 = ammunition destroyed
        public static string ReasonText(byte r) => r == 1 ? "ammo detonation" : "crew knocked out";
    }

    public class HealthMsg
    {
        public byte Id;
        public int Layout;                       // VehicleRig.PoolLayoutHash of the sender's tank
        public float[] Values = Array.Empty<float>();
    }

    public static class Wire
    {
        public static byte[] Build(Msg type, Action<BinaryWriter> body)
        {
            using var ms = new MemoryStream(64);
            using var w = new BinaryWriter(ms, Encoding.UTF8);
            w.Write((byte)type);
            body?.Invoke(w);
            w.Flush();
            return ms.ToArray();
        }

        public static void W(this BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        public static void W(this BinaryWriter w, Quaternion q) { w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); }
        public static Vector3 V3(this BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        public static Quaternion Q(this BinaryReader r) => new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        // ---- State ----
        public static byte[] State(VehicleStateMsg s) => Build(Msg.State, w =>
        {
            w.Write(s.Id); w.Write(s.Seq);
            w.W(s.Pos); w.W(s.Rot); w.W(s.Vel); w.W(s.AngVel);
            w.Write(s.Steer); w.Write(s.Throttle); w.Write(s.Flags);
            w.Write((byte)s.AimRots.Length);
            foreach (var a in s.AimRots) w.W(a);
        });
        public static VehicleStateMsg ReadState(BinaryReader r)
        {
            var s = new VehicleStateMsg();
            s.Id = r.ReadByte(); s.Seq = r.ReadUInt32();
            s.Pos = r.V3(); s.Rot = r.Q(); s.Vel = r.V3(); s.AngVel = r.V3();
            s.Steer = r.ReadSingle(); s.Throttle = r.ReadSingle(); s.Flags = r.ReadByte();
            int n = r.ReadByte(); s.AimRots = new Quaternion[n];
            for (int i = 0; i < n; i++) s.AimRots[i] = r.Q();
            return s;
        }

        // ---- Fire ----
        public static byte[] Fire(FireMsg f) => Build(Msg.Fire, w =>
        {
            w.Write(f.Id); w.Write(f.Weapon); w.Write(f.ProjectileGuid ?? "");
            w.W(f.Pos); w.W(f.Dir); w.W(f.Vel); w.Write(f.BarrelLength); w.Write(f.MaxPressure);
            w.Write(f.ShellKind); w.Write((ushort)f.Payload.Length); w.Write(f.Payload);
            int nx = f.Extras?.Count ?? 0; w.Write((byte)Math.Min(nx, 255));
            if (nx > 0) { int k = 0; foreach (var kv in f.Extras) { if (k++ >= 255) break; w.Write(kv.Key); w.Write(kv.Value.Length); w.Write(kv.Value); } }
        });
        public static FireMsg ReadFire(BinaryReader r)
        {
            var f = new FireMsg();
            f.Id = r.ReadByte(); f.Weapon = r.ReadByte(); f.ProjectileGuid = r.ReadString();
            f.Pos = r.V3(); f.Dir = r.V3(); f.Vel = r.V3(); f.BarrelLength = r.ReadSingle(); f.MaxPressure = r.ReadUInt32();
            f.ShellKind = r.ReadUInt16(); int n = r.ReadUInt16(); f.Payload = r.ReadBytes(n);
            if (r.BaseStream.Position < r.BaseStream.Length)
            {
                int nx = r.ReadByte();
                for (int i = 0; i < nx; i++) { f.Extras ??= new System.Collections.Generic.Dictionary<string, byte[]>(); string key = r.ReadString(); int len = r.ReadInt32(); f.Extras[key] = r.ReadBytes(len); }
            }
            return f;
        }

        // ---- Kill ----
        public static byte[] Kill(KillMsg k) => Build(Msg.Kill, w => { w.Write(k.Victim); w.Write(k.Killer); w.Write(k.Epoch); w.Write(k.Reason); });
        public static KillMsg ReadKill(BinaryReader r) => new KillMsg { Victim = r.ReadByte(), Killer = r.ReadByte(), Epoch = r.ReadInt32(), Reason = r.ReadByte() };

        // ---- Health ----
        public static byte[] Health(HealthMsg h) => Build(Msg.Health, w =>
        {
            w.Write(h.Id); w.Write(h.Layout); w.Write((ushort)h.Values.Length);
            foreach (var v in h.Values) w.Write(v);
        });
        public static HealthMsg ReadHealth(BinaryReader r)
        {
            var h = new HealthMsg { Id = r.ReadByte(), Layout = r.ReadInt32() };
            int n = r.ReadUInt16(); h.Values = new float[n];
            for (int i = 0; i < n; i++) h.Values[i] = r.ReadSingle();
            return h;
        }
    }
}
