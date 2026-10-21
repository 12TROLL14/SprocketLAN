using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Sprocket.DamageModelling;
using SprocketMP.Net;
using UnityEngine;

namespace SprocketMP
{
    /// <summary>One penetration event, recorded by the VICTIM (where damage is computed) and sent to the shooter.
    /// All positions/directions are in the victim vehicle's local space, so they map 1:1 onto the shooter's puppet
    /// (same design file).</summary>
    public class PenRecord
    {
        public byte Victim, Shooter = 255;
        public int Layout;                  // VehicleRig.PoolLayoutHash of the victim's tank
        public ushort ShellKind;            // 0 = native AP; room for HE/HEAT/... later (see Shells/ShellRegistry)
        public string ShellGuid = "";
        public Vector3 Entry, ShellDir;     // where the shell met the armour, direction of travel
        public float ShellSpeed, ShellMass, ShellDiameter;
        public byte Result;                 // 0 = penetration, 1 = non-penetration (absorbed), 2 = ricochet
        public List<Frag> Frags = new List<Frag>();
        public List<Dmg> Damage = new List<Dmg>();
        public float ReceivedAt;
        public bool Blast; public Vector3 BlastPoint; public float BlastRadius;    // explosive shells
        public List<ushort> DeadCrew = new List<ushort>();                         // victim's crew pools already dead before this hit

        public struct Frag { public Vector3 Start, Dir; public float Length, Speed, Diameter; public short Parent; public byte Flags; public bool Original; }
        public struct Dmg { public ushort Pool; public float Before, After, Max; }

        public const int MaxFrags = 2000;   // several spall cones + burst fragments, each with their bounces

        public byte[] Serialize() => Wire.Build(Msg.PenCam, w =>
        {
            w.Write(Victim); w.Write(Shooter); w.Write(Layout); w.Write(ShellKind); w.Write(ShellGuid ?? "");
            w.W(Entry); w.W(ShellDir); w.Write(ShellSpeed); w.Write(ShellMass); w.Write(ShellDiameter); w.Write(Result);
            int n = Math.Min(Frags.Count, MaxFrags); w.Write((ushort)n);
            for (int i = 0; i < n; i++)
            {
                var f = Frags[i];
                w.W(f.Start); w.W(f.Dir); w.Write(f.Length); w.Write(f.Speed); w.Write(f.Diameter); w.Write(f.Parent); w.Write(f.Flags); w.Write(f.Original);
            }
            w.Write((ushort)Damage.Count);
            foreach (var d in Damage) { w.Write(d.Pool); w.Write(d.Before); w.Write(d.After); w.Write(d.Max); }
            w.Write(Blast); w.W(BlastPoint); w.Write(BlastRadius);
            w.Write((ushort)DeadCrew.Count); foreach (var c in DeadCrew) w.Write(c);
        });

        public static PenRecord Read(BinaryReader r)
        {
            var p = new PenRecord { Victim = r.ReadByte(), Shooter = r.ReadByte(), Layout = r.ReadInt32(), ShellKind = r.ReadUInt16(), ShellGuid = r.ReadString() };
            p.Entry = r.V3(); p.ShellDir = r.V3(); p.ShellSpeed = r.ReadSingle(); p.ShellMass = r.ReadSingle(); p.ShellDiameter = r.ReadSingle(); p.Result = r.ReadByte();
            int n = r.ReadUInt16();
            for (int i = 0; i < n; i++)
                p.Frags.Add(new Frag { Start = r.V3(), Dir = r.V3(), Length = r.ReadSingle(), Speed = r.ReadSingle(), Diameter = r.ReadSingle(), Parent = r.ReadInt16(), Flags = r.ReadByte(), Original = r.ReadBoolean() });
            int m = r.ReadUInt16();
            for (int i = 0; i < m; i++) p.Damage.Add(new Dmg { Pool = r.ReadUInt16(), Before = r.ReadSingle(), After = r.ReadSingle(), Max = r.ReadSingle() });
            p.Blast = r.ReadBoolean(); p.BlastPoint = r.V3(); p.BlastRadius = r.ReadSingle();
            int dc = r.ReadUInt16(); for (int i = 0; i < dc; i++) p.DeadCrew.Add(r.ReadUInt16());
            return p;
        }

        public static string ResultText(byte r) => r == 0 ? "PENETRATION" : r == 2 ? "RICOCHET" : r == 3 ? "PENETRATION - NO DAMAGE" : r == 4 ? "HE BLAST" : "NO PENETRATION";
    }

    /// <summary>Victim-side capture: hooks the armour simulation (CompoundStructure.Penetrate) on our own tank.</summary>
    public static class PenCapture
    {
        struct Shot { public byte Shooter; public float Time; public Vector3 Pos, Dir; public string Guid; public ushort Kind; public float Filler, FuseSens, FuseDist; public Shells.ShellParams P; }
        static readonly List<Shot> _recentShots = new List<Shot>();
        static PenRecord _pending;
        static float _pendingSince;
        static float[] _healthBefore;
        static IntPtr _lastSim; static int _lastSimFrame = -1;
        static bool _origPen, _bounce;
        public static int Captured, Sent;

        public static void Patch(Harmony h)
        {
            int ok = 0;
            // only the real method: patching the explicit-interface stubs recurses forever (they share native code)
            foreach (var name in new[] { nameof(CompoundStructure.Penetrate) })
            {
                try
                {
                    var m = AccessTools.Method(typeof(CompoundStructure), name);
                    if (m == null) continue;
                    var bf = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
                    h.Patch(m, prefix: new HarmonyMethod(typeof(PenCapture).GetMethod(nameof(Penetrate_Prefix), bf)), postfix: new HarmonyMethod(typeof(PenCapture).GetMethod(nameof(Penetrate_Postfix), bf)));
                    ok++;
                }
                catch (Exception e) { MPLog.Warn($"PenCam patch {name}: {e.Message}"); }
            }
            MPLog.Info($"Pen cam capture hooks: {ok}/1");
        }

        /// <summary>Every replicated shot launched on this machine (used to work out who hit us).</summary>
        public static void RecordRemoteShot(FireMsg f)
        {
            _recentShots.Add(new Shot { Shooter = f.Id, Time = Time.time, Pos = f.Pos, Dir = f.Dir.normalized, Guid = f.ProjectileGuid, Kind = f.ShellKind, P = ShotParams(f), Filler = ShotParams(f).Filler, FuseSens = ShotParams(f).FuseSens, FuseDist = ShotParams(f).FuseDist });
            if (_recentShots.Count > 64) _recentShots.RemoveAt(0);
        }

        /// <summary>The shooter's shell parameters sent with the shot (older clients without them: our own values as a fallback).</summary>
        static Shells.ShellParams ShotParams(FireMsg f)
        {
            var p = Shells.ShellParams.Parse(f.Payload);
            if (p != null) return p;
            MPLog.Debug("shot without shell parameters - using local defaults");
            return Shells.ShellParams.ForLocal((Shells.ShellType)f.ShellKind);
        }

        static bool HasFreshShot()
        {
            float now = Time.time;
            foreach (var s in _recentShots) if (now - s.Time <= 6f) return true;
            return false;
        }

        static Shot? FindShot(Vector3 pos, Vector3 vel)
        {
            Shot? best = null; float bestScore = float.MaxValue;
            float now = Time.time;
            foreach (var s in _recentShots)
            {
                if (now - s.Time > 6f) continue;
                var toHit = pos - s.Pos;
                float along = Vector3.Dot(toHit, s.Dir);
                if (along < 0) continue;
                float off = (toHit - s.Dir * along).magnitude;                 // distance of the hit from the shot's line
                float score = off + Mathf.Abs(Vector3.Angle(s.Dir, vel)) * 0.2f;  // gravity bends it a little; direction must roughly agree
                if (score < bestScore) { bestScore = score; best = s; }
            }
            return bestScore < 25f ? best : null;
        }

        static bool IsMine(CompoundStructure cs, VehicleRig local)
        {
            if (local == null) return false;
            if (TestFiring) return true;
            try { if ((int)cs.ID == (int)local.VB.ID) return true; } catch { }
            try { return cs.damageApplier != null && local.VehicleHealthReg != null && cs.damageApplier.Pointer == local.VehicleHealthReg.Pointer; } catch { }
            return false;
        }

        static Shot? _curShot;

        // ---- sandbox shell test (vehicle designer): the shot is ours, the target is the tank being edited
        public static VehicleRig TestRig; static Shot? _testShot; public static bool TestFiring;
        public const byte TestVictimId = 254;

        static VehicleRig Target(out byte victimId)
        {
            victimId = 0;
            if (TestFiring) { victimId = TestVictimId; return TestRig; }
            var bm = BattleManager.Instance; var s = Session.Current;
            if (bm == null || !bm.InBattle || s == null || !s.Active) return null;
            victimId = s.LocalId; return bm.Local;
        }

        /// <summary>Fires a test shell of the given type straight at the edited tank (sandbox).</summary>
        public static void TestFire(CompoundStructure cs, VehicleRig rig, PenetratorInfo info, Shells.ShellType kind)
        {
            TestRig = rig; TestFiring = true; _pending = null;
            _testShot = new Shot { Shooter = 255, Time = Time.time, Pos = info.Position, Dir = info.Velocity.normalized, Guid = "", Kind = (ushort)kind, P = Shells.ShellParams.ForLocal(kind), Filler = Shells.ShellConfig.Filler(kind), FuseSens = Shells.ShellStore.Active.FuseSens, FuseDist = Shells.ShellStore.Active.FuseDist };
            try
            {
                var sim = cs.Penetrate(ref info, PenetrationSimulationOptions.Default);
                MPLog.Info($"Test {Shells.ShellConfig.Name(kind)} fired: hit={(sim != null && sim.Hit)}");
            }
            catch (Exception e) { MPLog.Warn("Test shell: " + e); }
            finally { TestFiring = false; _testShot = null; }
            if (_pending != null) { _pendingSince = Time.time - 1f; FinalizeTest(); }
        }

        static void FinalizeTest()
        {
            var rec = _pending; _pending = null;
            if (rec == null || rec.Frags.Count == 0) { MPLog.Info("Test shell: no armour hit"); return; }
            rec.Result = _bounce ? (byte)2 : _origPen ? (byte)3 : (byte)1;
            if (_origPen && rec.Frags.Count > 2) rec.Result = 0;
            _heOutside = false;
            rec.Shooter = Session.Current != null ? Session.Current.LocalId : (byte)0;
            rec.ReceivedAt = Time.time;
            Runner.Battle.LatestPenCam = rec;
        }

        /// <summary>APHE/HE penetrate less than AP: scale the striking velocity (penetration ~ v^1.43).</summary>
        static void Penetrate_Prefix(CompoundStructure __instance, ref PenetratorInfo penetrator)
        {
            _curShot = null;
            try
            {
                if (Shells.Explosives.InExplosion || _continuing) return;
                if (!TestFiring && !HasFreshShot()) return;     // cheap early-out (ammo cook-offs fire hundreds of these)
                var target = Target(out _);
                if (target == null || !IsMine(__instance, target)) return;
                var shot = TestFiring ? _testShot : FindShot(penetrator.Position, penetrator.Velocity);
                _curShot = shot;
                if (shot == null) return;
                var kind = (Shells.ShellType)shot.Value.Kind;
                var sp = shot.Value.P ?? Shells.ShellParams.ForLocal(kind);
                Shells.Explosives.P = sp;
                float filler = Mathf.Min(sp.Filler, Shells.ShellConfig.MaxFiller(kind, penetrator.Mass) + 0.001f);
                float speed = penetrator.Velocity.magnitude;
                float apPen = PenetrationUtils.ComputePenetration(penetrator.Diameter * 1000f, penetrator.Mass, speed, penetrator.PenetratorConstant);   // diameter in mm
                float f = kind == Shells.ShellType.HE ? 0.02f : sp.PenFactor;   // shooter's penetration modifier; HE shell body does not penetrate
                MPLog.Info($"{Shells.ShellConfig.Name(kind)} hit: AP would be {apPen:F0} mm -> {apPen * f:F0} mm ({filler:F0} g filler)");
                float nf = sp.Normalization;
                if (Mathf.Abs(f - 1f) > 0.001f || Mathf.Abs(nf - 1f) > 0.001f)
                    penetrator = new PenetratorInfo(penetrator.Mass, penetrator.Diameter, penetrator.Density, penetrator.MaxNormalizationAngle * nf, penetrator.PenetratorConstant, penetrator.Position, penetrator.Velocity * Mathf.Pow(Mathf.Max(f, 0.01f), 1f / 1.43f));
            }
            catch (Exception e) { MPLog.Warn("Shell prefix: " + e.Message); }
        }

        static void Penetrate_Postfix(CompoundStructure __instance, ref PenetratorInfo penetrator, IPenetrationSimulation __result)
        {
            try
            {
                var local = Target(out byte victimId);
                if (local == null || __result == null) return;
                if (_continuing)
                {
                    // our re-simulated shell path after the first plate: part of the same hit
                    _contSim = __result;
                    if (_pending != null) AddFragments(_pending, __result, local.GO.transform, local.Body != null ? local.Body.position : local.GO.transform.position);
                    _contBase = _lastBase; _contM = _lastSimToWorld;
                    return;
                }
                if (Shells.Explosives.InExplosion)
                {
                    // fragments of our own detonation: cut their bounces by calibre, then add them to the pen cam record
                    LimitDepth(__result, Shells.Explosives.MaxDepth);
                    if (_pending != null && IsMine(__instance, local)) AddFragments(_pending, __result, local.GO.transform, local.Body != null ? local.Body.position : local.GO.transform.position);
                    return;
                }
                if (!TestFiring && _curShot == null && !HasFreshShot()) return;
                if (__result.Pointer == _lastSim && Time.frameCount == _lastSimFrame) return;   // the game reuses simulation objects between shots            // explicit-interface wrappers call the real method
                if (!IsMine(__instance, local)) return;
                _lastSim = __result.Pointer; _lastSimFrame = Time.frameCount;

                var shot = _curShot ?? (TestFiring ? _testShot : FindShot(penetrator.Position, penetrator.Velocity));
                _curShot = null;
                if (shot == null) return;    // not a replicated player shot (e.g. our own ricochet, environment)

                var root = local.GO.transform;
                // the first capture of a burst snapshots health; damage is applied right after this call
                if (_pending == null)
                {
                    _pending = new PenRecord { Victim = victimId, Shooter = shot.Value.Shooter, Layout = local.PoolLayoutHash, ShellGuid = shot.Value.Guid, ShellKind = shot.Value.Kind };
                    _pending.Entry = root.InverseTransformPoint(penetrator.Position);
                    _pending.ShellDir = root.InverseTransformDirection(penetrator.Velocity.normalized);
                    _pending.ShellSpeed = penetrator.Velocity.magnitude; _pending.ShellMass = penetrator.Mass; _pending.ShellDiameter = penetrator.Diameter;
                    _pendingSince = Time.time; _origPen = _bounce = false;
                    _healthBefore = new float[local.Pools.Count];
                    for (int i = 0; i < _healthBefore.Length; i++) { try { _healthBefore[i] = local.Pools[i].Current; } catch { _healthBefore[i] = -1; } }
                    // which of our crew are already dead (before this hit): the shooter's pen cam leaves them out
                    for (int i = 0; i < local.PoolOwners.Count && i < local.Pools.Count; i++)
                    {
                        var seat = local.PoolOwners[i]?.TryCast<Sprocket.Vehicles.CrewSystems.CrewSeat>();
                        if (seat == null) continue;
                        bool dead = false;
                        try { dead |= seat.HealthFraction <= 0.01f; } catch { }
                        try { var op = seat.Operator; var ab = op?.ActiveBehaviour; if (op != null && ab == null) dead = true; else if (ab != null) dead |= ab.HealthFraction <= 0.01f; } catch { }   // no operator behaviour left = crew member gone
                        try { float mx = Mathf.Max(local.Pools[i].max, 0.001f); dead |= _healthBefore[i] >= 0f && _healthBefore[i] <= 0.01f * mx; } catch { }
                        if (dead) _pending.DeadCrew.Add((ushort)i);
                    }
                }
                _origSegs.Clear();
                ReadMaterials(__instance);
                _simPen = false; _hasSpall = false; _origFound = false; _origEndSpeed = 0f; _origEndAlong = float.MinValue;
                AddFragments(_pending, __result, root, local.Body != null ? local.Body.position : root.position);
                Captured++;
                int mainBase = _lastBase; var mainM = _lastSimToWorld;

                // ---- AP / APHE / APDS: our own spall cone instead of the game's spall
                var kindS = (Shells.ShellType)shot.Value.Kind;
                Shells.Explosives.P = shot.Value.P ?? Shells.ShellParams.ForLocal(kindS);
                _contSim = null;
                if (kindS != Shells.ShellType.HE && _simPen && Shells.Explosives.P.Normalization < 0.999f)
                    ContinueShell(__instance, __result, penetrator, root, mainBase, mainM);
                if (kindS != Shells.ShellType.HE && _simPen && _hasSpall)
                {
                    KillNativeSpall(__result, mainBase);
                    // one cone behind EVERY plate the shell came out of (armour -> air along its path)
                    var exits = new List<(Vector3 s, Vector3 d, float len, short mat, float speed)>();
                    for (int i = 1; i < _origSegs.Count && exits.Count < 6; i++)
                        if (IsHard(_origSegs[i - 1].mat) && IsAir(_origSegs[i].mat) && _origSegs[i].len > 0.06f) exits.Add(_origSegs[i]);   // armour plate -> air (not modules)
                    if (exits.Count == 0) exits.Add(NearestSeg(_spallStart));
                    foreach (var seg in exits)
                    {
                        float resid = seg.speed > 1f ? seg.speed : penetrator.Velocity.magnitude * 0.5f;
                        var axis = seg.d.sqrMagnitude > 0.5f ? Vector3.Slerp(_origDir, seg.d, 0.5f).normalized : _origDir;   // spall flies on mostly along the incoming line
                        Shells.Explosives.SpallCone(__instance, IsAir(seg.mat) && seg.len > 0.06f ? seg.s : _spallStart, axis, resid, penetrator.Mass, penetrator.Diameter * 1000f, penetrator.PenetratorConstant, Shells.Explosives.P.ConeDeg, !TestFiring, Shells.Explosives.P.SpallCountMult);
                    }
                    _lastBase = mainBase; _lastRec = _pending; _lastSimToWorld = mainM;
                }

                // ---- explosive shells
                var kind = (Shells.ShellType)shot.Value.Kind;
                if ((kind == Shells.ShellType.APHE || kind == Shells.ShellType.HE) && _origFound)
                {
                    float filler = Mathf.Min(Shells.Explosives.P.Filler, Shells.ShellConfig.MaxFiller(kind, penetrator.Mass) + 0.001f);
                    Vector3 pt; bool inside;
                    if (kind == Shells.ShellType.APHE)
                    {
                        // fuse sensitivity: the fuse only arms when the shell lost enough energy going through armour,
                        // i.e. it went through at least FuseSensitivityMm of (line-of-sight) steel
                        float v0 = penetrator.Velocity.magnitude, v1 = _origEndSpeed > 0f ? _origEndSpeed : v0;
                        float armour = 0f;
                        try { armour = PenetrationUtils.ComputePenetration(penetrator.Diameter * 1000f, penetrator.Mass, v0, penetrator.PenetratorConstant) - PenetrationUtils.ComputePenetration(penetrator.Diameter * 1000f, penetrator.Mass, Mathf.Max(1f, v1), penetrator.PenetratorConstant); } catch { }
                        // the speed read-out can miss the loss (e.g. shell stopped/continued sim): also measure the steel along the path
                        float geo = 0f;
                        foreach (var sg in _origSegs)
                            if (IsHard(sg.mat) && sg.len < 1.5f) geo += sg.len * 1000f * (_rha != null && sg.mat >= 0 && sg.mat < _rha.Length ? _rha[sg.mat] : 1f);
                        armour = Mathf.Max(armour, geo);
                        bool armed = armour >= Shells.Explosives.P.FuseSens;
                        MPLog.Info($"APHE fuse: ~{armour:F0} mm of armour crossed (needs {Shells.Explosives.P.FuseSens:F0} mm) -> {(armed ? "armed" : "not armed")}");
                        if (!_simPen) { MPLog.Info("APHE did not penetrate: no detonation"); }
                        else if (!armed) { }
                        else
                        {
                            var start = _hasSpall ? _spallStart : _origStart;
                            float fuse = Mathf.Max(0.02f, Shells.Explosives.P.FuseDist * 0.8f);   // calibrated: the configured distance is what you see in game
                            // follow the shell's REAL path from where it armed; if it stops first (e.g. fails the next plate
                            // of spaced armour, or gets deflected and stops) it bursts where it stopped, not further on
                            pt = AlongShellPath(start, fuse, out bool stoppedEarly);
                            // (the burst point stays ON the shell's simulated path - its turning is already reduced at the
                            //  source via the penetrator's normalisation, so spaced armour and stops are always respected)
                            if (stoppedEarly) MPLog.Info("APHE: shell stopped before its fuse delay ran out - bursts where it stopped");
                            _lastBase = mainBase; _lastRec = _pending; _lastSimToWorld = mainM;
                            StopShellAt(__result, pt, _origDir, root);     // the shell body is gone once it bursts
                            if (_contSim != null) { _lastBase = _contBase; _lastRec = _pending; _lastSimToWorld = _contM; StopShellAt(_contSim, pt, _origDir, root); }
                            Shells.Explosives.Detonate(__instance, local, pt, _origDir, penetrator.Mass, filler, penetrator.PenetratorConstant, true, true, !TestFiring, penetrator.Diameter * 1000f, false);   // fragments only
                            MarkBlast(root);
                        }
                    }
                    else
                    {
                        inside = _simPen;
                        // impact point = where the shell's path first enters armour; burst 5 cm in front of it, in the air
                        pt = inside ? (_hasSpall ? _spallStart : _origEnd) : FirstImpact() - _origDir * 0.05f;
                        Shells.Explosives.Detonate(__instance, local, pt, _origDir, penetrator.Mass, filler, penetrator.PenetratorConstant, inside, false, !TestFiring, penetrator.Diameter * 1000f, false,
                            Shells.Explosives.P.HEPenMm > 0f ? Shells.Explosives.P.HEPenMm : Shells.ShellConfig.HEPen(filler / 1000f));   // HE: fragments only (carrying the shell's penetration), no pressure damage
                        MarkBlast(root);
                        _heOutside = !inside;
                    }
                }
            }
            catch (Exception e) { MPLog.Warn("PenCam capture: " + e.Message); }
        }

        static bool _simPen, _hasSpall, _origFound, _heOutside;
        static float _origEndSpeed, _origEndAlong;
        static Vector3 _spallStart, _origStart, _origDir, _origEnd;

        /// <summary>Truncates a simulation's fragment list at the first fragment deeper than maxDepth bounces
        /// (children are appended after their parents), so ApplyFragmentHits only applies the allowed part.</summary>
        public static unsafe void LimitDepth(IPenetrationSimulation simI, int maxDepth)
        {
            try
            {
                if (simI == null || !InitOffsets()) return;
                IntPtr simPtr = simI.Pointer;
                if (Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(simPtr) != Il2CppInterop.Runtime.Il2CppClassPointerStore<PenetrationSimulation>.NativeClassPtr) return;
                byte* obj = (byte*)simPtr;
                int count = *(int*)(obj + _offFragCount);
                IntPtr arr = *(IntPtr*)(obj + _offFragArr);
                if (arr == IntPtr.Zero || count <= 1) return;
                long maxLen = *(long*)((byte*)arr + 3 * IntPtr.Size); if (count > maxLen) count = (int)maxLen;
                byte* data = (byte*)arr + 4 * IntPtr.Size;
                var depth = new int[count];
                for (int i = 0; i < count; i++)
                {
                    short parent = *(short*)(data + (long)i * _fragSize + _fParent);
                    depth[i] = parent >= 0 && parent < i ? depth[parent] + 1 : 0;
                    if (depth[i] > maxDepth) { *(int*)(obj + _offFragCount) = i; return; }
                }
            }
            catch (Exception e) { MPLog.Debug("LimitDepth: " + e.Message); }
        }

        static Matrix4x4 _lastSimToWorld = Matrix4x4.identity; static int _lastBase; static PenRecord _lastRec;

        /// <summary>APHE burst: the shell (and anything it would still do) ends at the burst point. Every shell-path
        /// segment that reaches past it, and every fragment born past it, gets zero mass/speed (= no damage) before the
        /// game applies the hits; the pen cam record is cut the same way.</summary>
        static unsafe void StopShellAt(IPenetrationSimulation simI, Vector3 burst, Vector3 dir, Transform root)
        {
            try
            {
                if (simI == null || !InitOffsets()) return;
                IntPtr simPtr = simI.Pointer;
                byte* obj = (byte*)simPtr;
                int count = *(int*)(obj + _offFragCount);
                IntPtr arr = *(IntPtr*)(obj + _offFragArr);
                if (arr == IntPtr.Zero || count <= 0) return;
                long maxLen = *(long*)((byte*)arr + 3 * IntPtr.Size); if (count > maxLen) count = (int)maxLen;
                byte* data = (byte*)arr + 4 * IntPtr.Size;
                var m = _lastSimToWorld;
                float bAlong = Vector3.Dot(burst, dir);
                int killed = 0;
                for (int i = 0; i < count; i++)
                {
                    byte* e = data + (long)i * _fragSize;
                    Vector3 sp = m.MultiplyPoint3x4(*(Vector3*)(e + _fSpawn)), dr = m.MultiplyVector(*(Vector3*)(e + _fDir)).normalized;
                    float travel = *(float*)(e + _fTravel); if (!float.IsFinite(travel)) travel = 0f;
                    int ff = *(ushort*)(e + _fFlags);
                    bool original = (ff & 1) != 0;
                    float a0 = Vector3.Dot(sp, dir), a1 = Vector3.Dot(sp + dr * travel, dir);
                    bool kill = a0 > bAlong + 0.02f || (original && a1 > bAlong + 0.02f);
                    if (!kill) continue;
                    *(float*)(e + _fSpeed) = 0f; *(float*)(e + _fMass) = 0f; killed++;
                    if (_lastRec != null && _lastBase + i < _lastRec.Frags.Count)
                    {
                        var f = _lastRec.Frags[_lastBase + i];
                        if (a0 > bAlong) f.Length = 0f; else f.Length = Mathf.Max(0f, (bAlong - a0) / Mathf.Max(0.05f, Vector3.Dot(dr, dir)));
                        _lastRec.Frags[_lastBase + i] = f;
                    }
                }
                if (killed > 0) MPLog.Info($"APHE burst: shell path stopped, {killed} fragment segments past the burst removed");
            }
            catch (Exception e) { MPLog.Debug("StopShellAt: " + e.Message); }
        }

        static readonly List<(Vector3 s, Vector3 d, float len, short mat, float speed)> _origSegs = new List<(Vector3, Vector3, float, short, float)>();
        static short _airMat = short.MinValue;
        // armour vs soft structure (crew, modules) - from the damage model's material table (rhaFactor)
        static float[] _rha;
        static void ReadMaterials(CompoundStructure cs)
        {
            try
            {
                var m = cs.materials; if (m == null) return;
                if (_rha != null && _rha.Length == m.Length) return;
                _rha = new float[m.Length];
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < m.Length; i++) { _rha[i] = m[i].rhaFactor; sb.Append($" {i}:{_rha[i]:F2}"); }
                MPLog.Info("Damage model materials (rha):" + sb);
            }
            catch (Exception e) { MPLog.Debug("materials: " + e.Message); }
        }
        static bool IsHard(short mat) => !IsAir(mat) && (_rha == null || mat < 0 || mat >= _rha.Length || _rha[mat] >= Shells.ShellConfig.ArmourRhaThreshold.Value);

        static bool IsAir(short mat)
        {
            if (_airMat == short.MinValue) { try { _airMat = DamageModelRules.AirMaterialIndex; } catch { _airMat = -1; } MPLog.Info($"Damage model air material index: {_airMat}"); }
            return mat == _airMat || mat < 0;
        }

        /// <summary>Point "dist" metres further along the shell's simulated path (segment by segment, so deflections in
        /// angled armour are followed) from the path point nearest to "from". stoppedEarly = the path ended first.</summary>
        static Vector3 AlongShellPath(Vector3 from, float dist, out bool stoppedEarly)
        {
            stoppedEarly = false;
            if (_origSegs.Count == 0) return from + _origDir * dist;
            var dbg = new System.Text.StringBuilder();
            foreach (var g in _origSegs) dbg.Append($" {g.len:F2}m/{(IsAir(g.mat) ? "air" : IsHard(g.mat) ? "armour" : "module")}");
            MPLog.Info($"APHE path (fuse {dist / 0.8f:F2} m):" + dbg);
            // where on the path is the arming point?
            int si = 0; float su = 0f, best = float.MaxValue;
            for (int i = 0; i < _origSegs.Count; i++)
            {
                var g = _origSegs[i];
                float u = Mathf.Clamp(Vector3.Dot(from - g.s, g.d), 0f, g.len);
                float d = (g.s + g.d * u - from).sqrMagnitude;
                if (d < best) { best = d; si = i; su = u; }
            }
            // the burst must happen in AIR: fragments born inside a plate do not interact with it (they would ignore armour)
            Vector3 lastAir = from; bool haveAir = false;
            float left = dist;
            for (int i = si; i < _origSegs.Count; i++)
            {
                var g = _origSegs[i];
                float u0 = i == si ? su : 0f, avail = g.len - u0;
                if (IsHard(g.mat))
                {
                    // entering armour before the delay ran out: if the shell does not come out the other side, it stops here
                    if (i == _origSegs.Count - 1 || left > 0f && !HasAirAfter(i)) { stoppedEarly = true; return haveAir ? lastAir : from; }
                    continue;                    // went through this plate; the delay keeps running on the other side
                }
                if (!IsAir(g.mat))
                {
                    // a module / crew member: the delay keeps running through it, but the burst must not happen INSIDE it
                    // (fragments born inside a structure ignore armour) -> if the delay ends here, burst just before it
                    if (left <= avail || i == _origSegs.Count - 1) { stoppedEarly = left > avail; return haveAir ? lastAir : from; }
                    left -= avail;
                    continue;
                }
                if (left <= avail) return g.s + g.d * Mathf.Clamp(u0 + left, Mathf.Min(0.05f, g.len * 0.5f), Mathf.Max(g.len * 0.5f, g.len - 0.05f));   // keep 5 cm clear of plates
                left -= avail;
                lastAir = g.s + g.d * Mathf.Max(u0, g.len - 0.04f); haveAir = true;   // just before whatever ends this air gap
            }
            stoppedEarly = true;
            return haveAir ? lastAir : from;
        }

        static Vector3 FirstImpact()
        {
            foreach (var g in _origSegs) if (!IsAir(g.mat)) return g.s;
            if (_origSegs.Count > 0) { var f = _origSegs[0]; return f.s + f.d * f.len; }
            return _origEnd;
        }

        /// <summary>True if the straight line a->b stays in the air gaps of the shell's path (no plate crossed).</summary>
        static bool PathClear(Vector3 a, Vector3 b)
        {
            // cheap check: b must be closer to an air segment of the path than to any armour segment
            float bestAir = float.MaxValue, bestMat = float.MaxValue;
            foreach (var g in _origSegs)
            {
                float u = Mathf.Clamp(Vector3.Dot(b - g.s, g.d), 0f, g.len);
                float d = (g.s + g.d * u - b).sqrMagnitude;
                if (IsAir(g.mat)) bestAir = Mathf.Min(bestAir, d); else bestMat = Mathf.Min(bestMat, d);
            }
            return bestAir <= bestMat && bestMat > 0.05f * 0.05f;
        }

        static bool HasAirAfter(int i) { for (int k = i + 1; k < _origSegs.Count; k++) if (IsAir(_origSegs[k].mat)) return true; return false; }

        static bool _continuing; static IPenetrationSimulation _contSim; static int _contBase; static Matrix4x4 _contM;

        /// <summary>The game turns a penetrating shell towards the plate normal by its own fixed amount. For shells that turn
        /// less (APHE, APDS), the game's path after the first plate is cancelled and the shell is re-simulated from the
        /// plate's back face along a direction only partly turned (shooter's normalisation factor).</summary>
        static void ContinueShell(CompoundStructure cs, IPenetrationSimulation main, PenetratorInfo pen, Transform root, int mainBase, Matrix4x4 mainM)
        {
            try
            {
                int e = -1;
                for (int i = 1; i < _origSegs.Count; i++) if (IsHard(_origSegs[i - 1].mat) && IsAir(_origSegs[i].mat) && _origSegs[i].len > 0.02f) { e = i; break; }
                if (e < 0) return;
                var ex = _origSegs[e];
                var newDir = Vector3.Slerp(_origDir, ex.d, Mathf.Clamp01(Shells.Explosives.P.Normalization)).normalized;
                if (Vector3.Angle(newDir, ex.d) < 0.5f || ex.speed < 20f) return;
                // cancel the game's path after the plate
                _lastBase = mainBase; _lastRec = _pending; _lastSimToWorld = mainM;
                StopShellAt(main, ex.s + ex.d * 0.01f, ex.d, root);
                _origSegs.RemoveRange(e, _origSegs.Count - e);
                // re-simulate from the back face of the plate
                var info = new PenetratorInfo(pen.Mass, pen.Diameter, pen.Density, pen.MaxNormalizationAngle * Shells.Explosives.P.Normalization, pen.PenetratorConstant, ex.s + newDir * 0.03f, newDir * ex.speed);
                _continuing = true;
                IPenetrationSimulation sim;
                try { sim = cs.Penetrate(ref info, PenetrationSimulationOptions.Default); }
                finally { _continuing = false; }
                if (sim == null) return;
                KillNativeSpall(sim, _contBase);
                if (!TestFiring) try { sim.ApplyFragmentHits(); } catch { }
                MPLog.Debug($"Shell path re-simulated after the plate: turned {Vector3.Angle(_origDir, newDir):F1} deg instead of {Vector3.Angle(_origDir, ex.d):F1}");
            }
            catch (Exception e) { MPLog.Debug("ContinueShell: " + e.Message); _continuing = false; }
        }

        static (Vector3 s, Vector3 d, float len, short mat, float speed) NearestSeg(Vector3 p)
        {
            (Vector3, Vector3, float, short, float) best = default; float bd = float.MaxValue;
            foreach (var g in _origSegs)
            {
                float u = Mathf.Clamp(Vector3.Dot(p - g.s, g.d), 0f, g.len);
                float d = (g.s + g.d * u - p).sqrMagnitude;
                // prefer the air segment right after the plate (its speed is the residual speed)
                if (IsAir(g.mat)) d *= 0.5f;
                if (d < bd) { bd = d; best = g; }
            }
            return best;
        }

        /// <summary>The game's own spall from the main shell does no damage and is not shown (replaced by our cone).</summary>
        static unsafe void KillNativeSpall(IPenetrationSimulation simI, int recBase)
        {
            try
            {
                if (simI == null || !InitOffsets()) return;
                byte* obj = (byte*)simI.Pointer;
                int count = *(int*)(obj + _offFragCount);
                IntPtr arr = *(IntPtr*)(obj + _offFragArr);
                if (arr == IntPtr.Zero || count <= 0) return;
                long maxLen = *(long*)((byte*)arr + 3 * IntPtr.Size); if (count > maxLen) count = (int)maxLen;
                byte* data = (byte*)arr + 4 * IntPtr.Size;
                int killed = 0;
                for (int i = 0; i < count; i++)
                {
                    byte* e = data + (long)i * _fragSize;
                    if ((*(ushort*)(e + _fFlags) & 1) != 0) continue;            // the shell itself
                    *(float*)(e + _fSpeed) = 0f; *(float*)(e + _fMass) = 0f; killed++;
                    if (_pending != null && recBase + i < _pending.Frags.Count) { var f = _pending.Frags[recBase + i]; f.Length = 0f; _pending.Frags[recBase + i] = f; }
                }
                if (killed > 0) MPLog.Debug($"native spall removed: {killed}");
            }
            catch (Exception e) { MPLog.Debug("KillNativeSpall: " + e.Message); }
        }

        static void MarkBlast(Transform root)
        {
            if (_pending == null) return;
            _pending.Blast = true;
            _pending.BlastPoint = root.InverseTransformPoint(Shells.Explosives.LastPoint);
            _pending.BlastRadius = Shells.Explosives.LastRadius;
        }

        // ---- raw IL2CPP memory access to PenetrationSimulation.fragments (Fragment is a non-blittable struct, so the
        //      interop wrappers (GetFragment / Il2CppReferenceArray<Fragment>) corrupt memory; we read the array directly)
        static bool _offsetsOk, _offsetsTried;
        static int _offFragArr, _offFragCount, _fragSize;
        static int _fDiameter, _fMass, _fSpeed, _fTravel, _fSpawn, _fDir, _fParent, _fFlags, _fMat = -1;

        static int FieldOff(IntPtr klass, string name, bool valueType)
        {
            var f = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(klass, name);
            if (f == IntPtr.Zero) throw new Exception("field not found: " + name);
            int off = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(f);
            return valueType ? off - 2 * IntPtr.Size : off;   // value-type field offsets include the object header
        }

        static bool InitOffsets()
        {
            if (_offsetsTried) return _offsetsOk;
            _offsetsTried = true;
            try
            {
                var simK = Il2CppInterop.Runtime.Il2CppClassPointerStore<PenetrationSimulation>.NativeClassPtr;
                var fragK = Il2CppInterop.Runtime.Il2CppClassPointerStore<Fragment>.NativeClassPtr;
                _offFragArr = FieldOff(simK, "fragments", false);
                _offFragCount = FieldOff(simK, "fragCount", false);
                uint align = 0;
                _fragSize = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_value_size(fragK, ref align);
                _fDiameter = FieldOff(fragK, "diameter", true); _fMass = FieldOff(fragK, "mass", true);
                _fSpeed = FieldOff(fragK, "speed", true); _fTravel = FieldOff(fragK, "travelDistance", true);
                _fSpawn = FieldOff(fragK, "spawnPoint", true); _fDir = FieldOff(fragK, "direction", true);
                _fParent = FieldOff(fragK, "parentIndex", true); _fFlags = FieldOff(fragK, "flags", true);
                try { _fMat = FieldOff(fragK, "materialIndex", true); } catch { _fMat = -1; }
                _offsetsOk = _fragSize > 0 && _fragSize < 512;
                MPLog.Info($"Pen cam offsets: arr={_offFragArr} count={_offFragCount} size={_fragSize} spawn={_fSpawn} dir={_fDir} flags={_fFlags} ok={_offsetsOk}");
            }
            catch (Exception e) { MPLog.Warn("Pen cam offsets: " + e.Message); _offsetsOk = false; }
            return _offsetsOk;
        }

        static unsafe void AddFragments(PenRecord rec, IPenetrationSimulation simI, Transform root, Vector3 vehiclePos)
        {
            if (!InitOffsets()) return;
            IntPtr simPtr = simI.Pointer;
            if (Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(simPtr) != Il2CppInterop.Runtime.Il2CppClassPointerStore<PenetrationSimulation>.NativeClassPtr)
            { MPLog.Warn("Pen cam: unexpected simulation type, skipped"); return; }
            var sim = new PenetrationSimulation(simPtr);
            byte* obj = (byte*)simPtr;
            int count = *(int*)(obj + _offFragCount);
            IntPtr arr = *(IntPtr*)(obj + _offFragArr);
            if (arr == IntPtr.Zero || count <= 0) return;
            long maxLen = *(long*)((byte*)arr + 3 * IntPtr.Size);
            if (count > maxLen) count = (int)maxLen;
            byte* data = (byte*)arr + 4 * IntPtr.Size;
            var m = sim.SimToWorldSpace;
            // fragments may already be in world space; use whichever interpretation puts the first one near our tank
            {
                Vector3 sp0 = *(Vector3*)(data + _fSpawn);
                float dRaw = (sp0 - vehiclePos).sqrMagnitude, dConv = (m.MultiplyPoint3x4(sp0) - vehiclePos).sqrMagnitude;
                if (dRaw < dConv) m = Matrix4x4.identity;
                _lastSimToWorld = m;
                if (Plugin.VerboseLogging.Value) MPLog.Info($"Pen frag space: raw {Mathf.Sqrt(dRaw):F1} m, converted {Mathf.Sqrt(dConv):F1} m from tank -> {(dRaw < dConv ? "raw" : "converted")}");
            }

            int baseIndex = rec.Frags.Count;
            _lastBase = baseIndex; _lastRec = rec;
            bool pen = false, bounce = false;
            for (int i = 0; i < count && rec.Frags.Count < PenRecord.MaxFrags; i++)
            {
                byte* e = data + (long)i * _fragSize;
                Vector3 sp = *(Vector3*)(e + _fSpawn), dr = *(Vector3*)(e + _fDir);
                float travel = *(float*)(e + _fTravel), speed = *(float*)(e + _fSpeed), dia = *(float*)(e + _fDiameter);
                short parent = *(short*)(e + _fParent);
                short mat = _fMat >= 0 ? *(short*)(e + _fMat) : (short)-1;
                int ff = *(ushort*)(e + _fFlags);
                Vector3 start = m.MultiplyPoint3x4(sp), dir = m.MultiplyVector(dr);
                bool original = (ff & 1) != 0, deflected = (ff & 16) != 0, embedded = (ff & 32) != 0, penetrated = (ff & 64) != 0;
                bool blastFrag = Shells.Explosives.InExplosion;
                if (blastFrag) { }
                else if (original)
                {
                    if (!blastFrag) _origSegs.Add((start, dir.normalized, float.IsFinite(travel) ? Mathf.Clamp(travel, 0f, 20f) : 0f, mat, speed));
                    if (!_origFound) { _origFound = true; _origStart = start; _origDir = dir.normalized; _origEnd = start + _origDir * (float.IsFinite(travel) ? Mathf.Clamp(travel, 0, 20f) : 0f); }
                    else { var e2 = start + dir.normalized * (float.IsFinite(travel) ? Mathf.Clamp(travel, 0, 20f) : 0f); if (Vector3.Dot(e2 - _origStart, _origDir) > Vector3.Dot(_origEnd - _origStart, _origDir)) _origEnd = e2; }
                    if (penetrated) _simPen = true;
                    float alongEnd = Vector3.Dot(start - _origStart, _origDir);
                    if (alongEnd >= _origEndAlong) { _origEndAlong = alongEnd; _origEndSpeed = speed; }
                }
                else if (!blastFrag && (!_hasSpall || Vector3.Dot(start - _origStart, _origDir) < Vector3.Dot(_spallStart - _origStart, _origDir))) { _hasSpall = true; _spallStart = start; }
                if (original && penetrated) pen = true;
                if (original && deflected) bounce = true;
                                byte flags = (byte)((original && !blastFrag ? 1 : 0) | (embedded ? 4 : 0) | (deflected ? 8 : 0) | (penetrated ? 16 : 0) | (blastFrag ? (Shells.Explosives.Mode == 2 ? 128 : 64) : 0));
                if (blastFrag) original = false;
                rec.Frags.Add(new PenRecord.Frag
                {
                    Start = root.InverseTransformPoint(start), Dir = root.InverseTransformDirection(dir.normalized),
                    Length = float.IsFinite(travel) ? Mathf.Clamp(travel, 0, 20f) : 0f, Speed = speed, Diameter = dia,
                    Parent = (short)(parent < 0 ? -1 : parent + baseIndex), Flags = flags, Original = original,
                });
            }
            if (!Shells.Explosives.InExplosion) { _origPen |= pen; _bounce |= bounce; }
            if (Plugin.VerboseLogging.Value && rec.Frags.Count > baseIndex)
                MPLog.Info($"Pen frag0 local start {rec.Frags[baseIndex].Start} dir {rec.Frags[baseIndex].Dir} len {rec.Frags[baseIndex].Length} (vehicle dist {(m.MultiplyPoint3x4(*(Vector3*)(data + _fSpawn)) - vehiclePos).magnitude:F1} m)");
        }

        /// <summary>Called every frame: a burst is closed 0.4 s after its first hit (damage has been applied by then).</summary>
        public static void Update(BattleManager bm, Session s)
        {
            if (_pending == null) return;
            if (Time.time - _pendingSince < 0.4f) return;
            var rec = _pending; _pending = null;
            try
            {
                var local = bm.Local;
                if (local != null && _healthBefore != null && _healthBefore.Length == local.Pools.Count)
                    for (int i = 0; i < local.Pools.Count; i++)
                    {
                        float after; try { after = local.Pools[i].Current; } catch { continue; }
                        float before = _healthBefore[i];
                        if (before < 0 || after >= before - 0.01f) continue;
                        float max = 1f; try { max = local.Pools[i].max; } catch { }
                        rec.Damage.Add(new PenRecord.Dmg { Pool = (ushort)i, Before = before, After = after, Max = max });
                    }
                if (rec.Frags.Count == 0) return;
                rec.Result = rec.Damage.Count > 0 ? (byte)0 : _bounce ? (byte)2 : _origPen ? (byte)3 : (byte)1;
                if (_heOutside) rec.Result = rec.Damage.Count > 0 ? (byte)4 : (byte)1;
                _heOutside = false;
                s.Broadcast(rec.Serialize());
                Sent++;
                MPLog.Info($"Pen cam sent to player {rec.Shooter}: {PenRecord.ResultText(rec.Result)}, {rec.Frags.Count} fragments, {rec.Damage.Count} modules damaged");
            }
            catch (Exception e) { MPLog.Warn("PenCam send: " + e.Message); }
        }

        public static void Reset() { _pending = null; _recentShots.Clear(); _lastSim = IntPtr.Zero; }
    }
}
