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
        public List<(Vector3 p, Vector3 d)> PlateHits = new List<(Vector3, Vector3)>();   // every plate the shell met (went through, stopped in, bounced off): point + direction, tank space
        public List<ushort> DeadCrew = new List<ushort>(), DeadMods = new List<ushort>();   // DeadMods: other modules already destroyed (tracks...)                         // victim's crew pools already dead before this hit
        public List<Quaternion> AimRots = new List<Quaternion>(); public List<Vector3> AimPos = new List<Vector3>();                  // victim's turret / gun local rotations at the hit
        public bool KillCam;
        public float Range = -1f, Angle = -1f, PlateLos = -1f;   // shot distance (m), impact angle from the normal (deg), first plate's line-of-sight thickness (mm)                                                       // local only: the victim's own replay of a fatal hit

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
            w.Write(Range); w.Write(Angle); w.Write(PlateLos);
            int na = Math.Min(Math.Min(AimRots.Count, AimPos.Count), 32);
            w.Write((byte)na); for (int i = 0; i < na; i++) { var q = AimRots[i]; w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); w.W(AimPos[i]); }
            w.Write((ushort)DeadMods.Count); foreach (var c in DeadMods) w.Write(c);
            int np = Math.Min(PlateHits.Count, 32); w.Write((byte)np); for (int i = 0; i < np; i++) { w.W(PlateHits[i].p); w.W(PlateHits[i].d); }
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
            p.Range = r.ReadSingle(); p.Angle = r.ReadSingle(); p.PlateLos = r.ReadSingle();
            int ac = r.ReadByte(); for (int i = 0; i < ac; i++) { p.AimRots.Add(new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle())); p.AimPos.Add(r.V3()); }
            try { if (r.BaseStream.Position + 2 <= r.BaseStream.Length) { int dm = r.ReadUInt16(); for (int i = 0; i < dm; i++) p.DeadMods.Add(r.ReadUInt16()); } } catch { }
            try { if (r.BaseStream.Position + 1 <= r.BaseStream.Length) { int np = r.ReadByte(); for (int i = 0; i < np; i++) p.PlateHits.Add((r.V3(), r.V3())); } } catch { }
            return p;
        }

        public static string ResultText(byte r) => r == 0 ? "PENETRATION" : r == 2 ? "RICOCHET" : r == 3 ? "PENETRATION - NO DAMAGE" : r == 4 ? "HE BLAST" : "NO PENETRATION";
    }

    /// <summary>Victim-side capture: hooks the armour simulation (CompoundStructure.Penetrate) on our own tank.</summary>
    public static partial class PenCapture
    {
        struct Shot { public byte Shooter; public float Time; public Vector3 Pos, Dir; public string Guid; public ushort Kind; public float Filler, FuseSens, FuseDist; public Shells.ShellParams P; public bool Scaled; }
        static readonly List<Shot> _recentShots = new List<Shot>();
        static PenRecord _pending;
        static float _pendingSince;
        static float[] _healthBefore;
        static IntPtr _lastSim; static float _shellDia; static bool _entrySet; static Vector3 _origDirSim; static int _lastSimFrame = -1;
        static bool _origPen, _bounce, _lockResult; static VehicleRig _victim;
        public static int Captured, Sent;

        // camera shake: the game shakes the camera for every simulated hit, including our invisible test rounds;
        // let one shake through per frame (the real hit's) and none while we probe
        static int _shakeFrame = -1, _quietUntilFrame = -1;
        static bool Shake_Prefix()
        {
            if (_trial || Time.frameCount <= _quietUntilFrame) return false;
            if (Time.frameCount == _shakeFrame) return false;
            _shakeFrame = Time.frameCount; return true;
        }

        public static void Patch(Harmony h)
        {
            try
            {
                var bfs = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
                var pre = new HarmonyMethod(typeof(PenCapture).GetMethod(nameof(Shake_Prefix), bfs));
                int n = 0;
                foreach (var t in new[] { typeof(Sprocket.Gameplay.VehicleControl.VehicleController), typeof(Sprocket.VehicleControl.VehicleControlPlayerState), typeof(Sprocket.CameraShakeController) })
                    foreach (var m in AccessTools.GetDeclaredMethods(t))
                        if (m.Name == "ApplyCameraShake" || m.Name == "Shake") { try { h.Patch(m, prefix: pre); n++; } catch (Exception e) { MPLog.Warn($"shake patch {t.Name}.{m.Name}: {e.Message}"); } }
                MPLog.Info($"Camera shake hooks: {n}");
            }
            catch (Exception e) { MPLog.Warn("shake patch: " + e.Message); }
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

        /// <summary>The sabot round of this shooter's latest shot really was sped up on this machine.</summary>
        public static void MarkLastShotScaled(byte shooter)
        {
            for (int i = _recentShots.Count - 1; i >= 0; i--)
                if (_recentShots[i].Shooter == shooter) { var s = _recentShots[i]; s.Scaled = true; _recentShots[i] = s; return; }
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

        static bool _trial; static float _boostV = 1f;

        // ============================================================== the mod's own shell model
        struct OwnShot { public bool Active; public Shells.ShellType Kind; public Shells.ShellParams P; public Vector3 Pos, Dir; public float Speed, PenMm, Mass, Diameter, Density, Filler; public ushort PenConst; }
        static OwnShot _own; static bool _skipShot;
        static readonly HashSet<(byte, float)> _resolved = new HashSet<(byte, float)>();

        static VehicleRig _probeRig;
        /// <summary>The game's own shell (the tap) does nothing: one fragment left, stopped dead where it touched the
        /// tank, so the game ends the shell there instead of letting it fly on through the tank.</summary>
        static unsafe void DropAll(IPenetrationSimulation simI)
        {
            try
            {
                if (simI == null || !InitOffsets()) return;
                byte* obj = (byte*)simI.Pointer;
                int count = *(int*)(obj + _offFragCount);
                IntPtr arr = *(IntPtr*)(obj + _offFragArr);
                if (arr == IntPtr.Zero || count <= 0) { *(int*)(obj + _offFragCount) = 0; return; }
                byte* e = (byte*)arr + 4 * IntPtr.Size;
                *(float*)(e + _fTravel) = 0f; *(float*)(e + _fSpeed) = 0f;
                *(ushort*)(e + _fFlags) = (ushort)((*(ushort*)(e + _fFlags) & 1) | 32);   // original, embedded
                *(int*)(obj + _offFragCount) = 1;
            }
            catch { }
        }

        static void AddSegF(PenRecord rec, Transform root, Vector3 a, Vector3 b, Vector3 dir, float speed, float dia, byte flags)
        {
            if (rec == null || rec.Frags.Count >= PenRecord.MaxFrags) return;
            rec.Frags.Add(new PenRecord.Frag { Start = root.InverseTransformPoint(a), Dir = root.InverseTransformDirection(dir), Length = Vector3.Distance(a, b), Speed = speed, Diameter = dia, Parent = -1, Flags = flags, Original = true });
        }

        static void AddSeg(PenRecord rec, Transform root, Vector3 a, Vector3 b, Vector3 dir, float speed, float dia, bool pen)
        {
            if (rec == null || rec.Frags.Count >= PenRecord.MaxFrags) return;
            rec.Frags.Add(new PenRecord.Frag { Start = root.InverseTransformPoint(a), Dir = root.InverseTransformDirection(dir), Length = Vector3.Distance(a, b), Speed = speed, Diameter = dia, Parent = -1, Flags = (byte)(1 | (pen ? 16 : 0)), Original = true });
        }

        /// <summary>The whole hit, decided by the mod: the shell flies straight; each armour layer on its line is
        /// rated by its line-of-sight thickness (after a little normalisation), or by its real thickness when the
        /// shell overmatches it; above the ricochet angle it glances off. Penetration left after each layer is what
        /// it had minus what the layer took. Inside the tank the shell body hits modules (the game's own fragment
        /// damage), a spall cone is thrown behind every plate it comes out of, APHE bursts after its fuse delay and
        /// HEAT's jet loses strength in every spaced-armour gap.</summary>
        /// <summary>HE on the game's armour model: it bursts on the first thing it touches (armour, a wheel, a track); it gets
        /// inside only if its penetration beats that plate's line of sight.</summary>
        static void ResolveHE(CompoundStructure cs, VehicleRig local, Transform root, Shot shot)
        {
            MPLog.Stage = "own HE model";
            var o = _own; var P = o.P; Shells.Explosives.P = P;
            ReadMaterials(cs); _probeRig = local; Shells.Explosives._victimRig = local;
            var view = PenCamView.Instance; var rec = _pending;
            float filler = Mathf.Min(P.Filler, Shells.ShellConfig.MaxFiller(Shells.ShellType.HE, o.Mass) + 0.001f);
            float hePen = P.HEPenMm > 0f ? P.HEPenMm : Shells.ShellConfig.HEPen(filler / 1000f);
            Vector3 pos = o.Pos - o.Dir * 1f, dir = o.Dir;
            var hits = view.TraceDM(local, pos, dir, 15f);
            int fi = -1; for (int i = 0; i < hits.Count; i++) if (hits[i].T > 0.002f && SteelFace(hits[i]) && !view.InWheel(local, pos + dir * hits[i].T)) { fi = i; break; }
            float tArm = fi >= 0 ? hits[fi].T : 15f, tC = tArm; string what = fi >= 0 ? "armour" : "nothing";
            // a wheel / the sprocket in front of the armour
            for (float t = 0f; t < tC; t += 0.02f) if (view.InWheel(local, pos + dir * t)) { tC = t; what = "a wheel"; break; }
            // a track band in front of that
            if (view.TrackHit(local, pos, pos + dir * tC) >= 0)
            {
                float lo = 0f, hi = tC;
                for (int k = 0; k < 12; k++) { float m = (lo + hi) * 0.5f; if (view.TrackHit(local, pos, pos + dir * m) >= 0) hi = m; else lo = m; }
                tC = hi; what = "a track";
            }
            if (tC >= 15f) { MPLog.Info("Own HE model: the shell's line misses the tank's armour"); _origPen = _bounce = false; _lockResult = true; return; }
            var contact = pos + dir * tC;
            bool inside = false; float los = 0f, ang = 0f; Vector3 nrm = -dir;
            if (what == "armour")
            {
                var h = hits[fi]; float cos = Mathf.Clamp(h.Cos, 0.05f, 1f);
                int pj = SolidPartner(hits, fi);
                float rawF = h.Raw > 0.5f ? h.Raw : pj >= 0 && hits[pj].Raw > 0.5f ? hits[pj].Raw : -1f;
                los = rawF > 0.5f ? rawF / cos * Rha(h.Mat) : pj >= 0 ? (hits[pj].T - h.T) * 1000f * Rha(h.Mat) : 1f;
                ang = Mathf.Acos(cos) * Mathf.Rad2Deg; nrm = h.Normal;
                inside = hePen >= los;
            }
            if (rec != null)
            {
                rec.Entry = root.InverseTransformPoint(contact); _entrySet = true;
                if (what == "armour") rec.PlateHits.Add((root.InverseTransformPoint(contact), root.InverseTransformDirection(dir)));
                if (los > 0f) { rec.PlateLos = los; rec.Angle = ang; }
                AddSeg(rec, root, o.Pos, contact, dir, o.Speed, o.Diameter, inside);
            }
            Vector3 pt;
            if (inside)
            {
                // through the plate: bursts just behind it (out of any steel)
                float tOut = tArm + los / 1000f;
                { int pj2 = SolidPartner(hits, fi); if (pj2 >= 0) tOut = hits[pj2].T; }
                pt = AirStart(cs, o.PenConst, pos + dir * (tOut + 0.03f), dir);
            }
            else
            {
                // on impact: back along the path and off the struck surface, never inside it
                pt = contact - dir * Mathf.Max(0f, Shells.ShellConfig.HEBurstBack.Value);
                pt = what == "armour" ? pt + nrm.normalized * Mathf.Max(0f, Shells.ShellConfig.HEBurstOffSurface.Value) : OffSurface(local, pt, dir);
                TrackDamage(local, pt - dir * 3f, contact + dir * 0.05f, true);
                pt = BackToAir(cs, o.PenConst, pt, dir);
            }
            MPLog.Info($"Own HE model: hits {what} {(los > 0f ? $"({los * Mathf.Cos(ang * Mathf.Deg2Rad):F0} mm at {ang:F0} deg, {los:F0} mm along the shell) " : "")}with {hePen:F0} mm: {(inside ? "goes through, bursts behind it" : "bursts outside")}");
            if (Plugin.VerboseLogging.Value && (what == "a wheel" || what == "a track")) DebugFrags = 8;    // log a few fragments' paths (fragments past the running gear)
            Shells.Explosives.Detonate(cs, local, pt, dir, o.Mass, filler, o.PenConst, inside, false, !TestFiring, o.Diameter * 1000f, false, hePen);
            DebugFrags = 0;
            MPLog.Info($"HE burst point {(pt - contact).magnitude * 100f:F0} cm from the impact, inside armour: {(view.HasDm(local) && view.DmSteelAround(local, pt, dir, out _, out _))}");
            MarkBlast(root);
            _heOutside = !inside; _origPen = inside; _simPen = inside; _bounce = false; _lockResult = true;
            if (rec != null) { rec.ShellSpeed = o.Speed; rec.ShellDir = root.InverseTransformDirection(o.Dir); }
        }

        static void ResolveOwn(CompoundStructure cs, VehicleRig local, Transform root, Shot shot)
        {
            if (_own.Kind == Shells.ShellType.HE) { ResolveHE(cs, local, root, shot); return; }
            _dmLastShape = -1; MPLog.Stage = "own hit model";
            var o = _own; var P = o.P; Shells.Explosives.P = P;
            ReadMaterials(cs); _probeRig = local; Shells.Explosives._victimRig = local;
            var view = PenCamView.Instance;
            var C = typeof(Shells.ShellConfig);
            bool heat = o.Kind == Shells.ShellType.HEAT, aphe = o.Kind == Shells.ShellType.APHE;
            float cal = o.Diameter * 1000f, p0 = Mathf.Max(1f, o.PenMm), pen = p0;
            float ricoDeg = P.RicoDeg > 1f ? P.RicoDeg : Shells.ShellConfig.RicochetAngle(o.Kind);
            float normScale = heat ? 0f : Mathf.Clamp01(P.Normalization);      // exit normalisation: shell type / cap reduce it, HEAT none
            float SpeedAt(float p) => o.Speed * Mathf.Pow(Mathf.Clamp01(p / p0), 1f / 1.43f);

            var rec = _pending;
            Vector3 pos = o.Pos - o.Dir * 1.0f, dir = o.Dir;
            Vector3 lastImpact = Vector3.zero; bool first = true;
            float crossed = 0f; bool penAny = false, rico = false;
            var exits = new List<(Vector3 p, Vector3 d, float pen)>();
            var bodySegs = new List<(Vector3 a, Vector3 b, Vector3 d, float pen)>();
            var bodyDraw = new List<(Vector3 a, Vector3 b, Vector3 d, float pen)>();
            Vector3 stopPt = pos + dir * 15f;
            var log = new System.Text.StringBuilder($"{Shells.ShellConfig.DisplayName(o.Kind, P.RicoDeg > 1f)}: {pen:F0} mm");   // RicoDeg is only sent for capped shells
            Vector3 flightFrom = o.Pos; bool ricoOut = false;   // ricoOut: bounced off the outside, flying on (shot traps)
            for (int guard = 0; guard < 40; guard++)
            {
                List<PenCamView.ArmourLayer> layers = null;
                if (view != null && Shells.ShellConfig.UseGameArmourModel.Value) layers = DmLayers(view, local, cs, pos, dir, o, log);
                if (layers == null) layers = view != null ? view.TraceArmour(local, pos, dir, 15f) : new List<PenCamView.ArmourLayer>();
                int li = -1; for (int k = 0; k < layers.Count; k++) if (layers[k].TIn > 0.002f) { li = k; break; }
                if (li < 0)
                {
                    // nothing more on this line: out the other side (or a miss)
                    if (ricoOut) { AddSegF(rec, root, flightFrom, pos + dir * 4f, dir, SpeedAt(pen), o.Diameter, 1 | 8); log.Append(" | bounces away"); stopPt = pos + dir * 4f; break; }   // the ricochet flies off
                    if (first) { log.Append(" | no armour on the line"); TrackDamage(local, o.Pos - o.Dir * 3f, pos + dir * 15f, true); }
                    var end = pos + dir * (first ? 15f : 4f);
                    if (!first && pen > 0f) bodySegs.Add((pos, end, dir, pen));
                    stopPt = end; break;
                }
                var L = layers[li];
                // is it armour? ask the damage model, probing from far outside along the line (a probe started inside the
                // previous plate - touching plates - sees everything inside-out)
                if (!L.Dm)
                {
                    const float far = 8f;
                    float span = L.TOut - L.TIn;
                    var from = pos + dir * (L.TIn - far);
                    float hs = 0f, he = 0f, hl = 0f;
                    bool probed = ProbeArmour(cs, o.PenConst, from, dir, far + span + 0.02f, out hs, out he, out hl, false, far - 0.003f)
                               || ProbeArmour(cs, o.PenConst, from, dir, far + span + 0.02f, out hs, out he, out hl, true, far - 0.003f);
                    if (!probed) log.Append(" [damage model could not be checked]");
                    // the game's own armour reading at this point (what the designer's armour view shows: raw / line of sight)
                    float myAng = Mathf.Acos(Mathf.Clamp01(L.Cos)) * Mathf.Rad2Deg;
                    bool gOk = SampleArmour(cs, pos + dir * Mathf.Max(0.001f, L.TIn - 0.05f), dir, 0.4f, o, out float gRaw, out float gLos, out float gAng)
                               && gRaw >= 0.5f && Math.Abs(gAng - myAng) < 12f && view != null && !view.InWheel(local, pos + dir * L.TIn);
                    // the damage model over a longer stretch: some parts (add-on armour, plates whose model is thinner than
                    // their armour value) are thicker there than their 3D model, or sit a little off it
                    float nextIn = li + 1 < layers.Count ? layers[li + 1].TIn : 99f;
                    float dmS = 0f, dmE = 0f, dmL = 0f;
                    float win = Mathf.Min(Mathf.Max(span * 4f, span + 0.25f), Mathf.Max(span + 0.02f, nextIn - L.TIn + 0.005f));
                    bool dmOk = probed && (ProbeArmour(cs, o.PenConst, from, dir, far + win, out dmS, out dmE, out dmL, false, far - 0.003f)
                                         || ProbeArmour(cs, o.PenConst, from, dir, far + win, out dmS, out dmE, out dmL, true, far - 0.003f));
                    dmS -= far; dmE -= far;
                    bool dmPlate = dmOk && dmL > 0.002f && dmS < span + 0.1f && dmE < win + 0.001f && L.TIn + dmE <= nextIn + 0.005f;
                    if (probed && hl < 0.002f && gOk) { hl = gLos / 1000f; dmPlate = false; log.Append($" [damage model probe missed it, the game's armour view has {gRaw:F0} mm]"); }
                    if (probed && hl < 0.002f && dmPlate)
                    {
                        // nothing hard inside the model's span, but the damage model's plate starts just behind it: that plate
                        log.Append($" [model {L.Nominal:F0} mm part is off its armour: damage model has {dmL * 1000f:F0} mm along the shell {dmS * 100f:F0} cm in]");
                        hl = dmL;
                        L.TIn += Mathf.Max(0f, dmS); L.Los = dmL * 1000f; L.Nominal = L.Los * L.Cos; L.TOut = L.TIn + dmL; L.FaceNominal = -1f;
                    }
                    if (probed && hl < 0.002f)
                    {
                        var b0 = pos + dir * L.TOut;
                        if (!first && pen > 0f) { bodySegs.Add((pos, b0, dir, pen)); }
                        else { AddSeg(rec, root, flightFrom, b0, dir, SpeedAt(pen), o.Diameter, false); flightFrom = b0; }
                        log.Append($" | {L.Nominal:F0} mm part is not armour, passes through");
                        pos = b0 + dir * 0.001f; continue;
                    }
                    // thickness: the plate's own thickness straight through from the struck face, over the cosine
                    if (gOk)
                    {
                        // the game's raw thickness decides; our own model measurements are only the fallback
                        float los = gRaw / Mathf.Max(0.05f, L.Cos);
                        if (Math.Abs(gRaw - L.Nominal) > 3f) log.Append($" [game armour view: {gRaw:F0} mm raw, {los:F0} along the shell (model said {L.Nominal:F0})]");
                        L.Los = los; L.Nominal = gRaw; L.TOut = L.TIn + los / 1000f; L.FaceNominal = -1f; dmPlate = false;
                    }
                    else if (_sampleLogs < 20 && !_sampleOk) { _sampleLogs++; log.Append(" [no game armour reading]"); }
                    float faceLos = L.FaceNominal / Mathf.Max(0.05f, L.Cos);
                    bool faceOk = !gOk && L.FaceNominal > 0.5f && L.FaceNominal < 1000f &&
                                  (L.FaceNominal < 2f * Mathf.Max(10f, L.Nominal)                                   // close to the trace
                                   || (L.FaceNominal < 3.5f * L.Nominal && L.FaceNominal <= 250f && L.TIn + faceLos / 1000f <= nextIn + 0.005f));   // thicker, but still clear of the next plate (the trace left the plate early through an inner face)
                    if (!faceOk && L.FaceNominal > 0.5f && Math.Abs(L.FaceNominal - L.Nominal) > 3f) log.Append($" [face reading {L.FaceNominal:F0} mm not used]");
                    if (dmOk && Math.Abs(dmL * 1000f - L.Los) > 5f) log.Append($" [damage model {dmL * 1000f:F0} mm along the shell from {dmS * 1000f:F0} mm, model {L.Los:F0}]");
                    if (faceOk)   // a reading far thicker than the trace: ray ran along the plate, ignore
                    {
                        float los = L.FaceNominal / Mathf.Max(0.05f, L.Cos);
                        if (Math.Abs(los - L.Los) > 3f) log.Append($" [plate {L.FaceNominal:F0} mm thick, {los:F0} along the shell (trace said {L.Los:F0})]");
                        L.Los = los; L.Nominal = L.FaceNominal; L.TOut = L.TIn + los / 1000f;
                    }
                    // armour thicker in the damage model than in the 3D model (add-on armour): the damage model decides
                    if (dmPlate && dmL * 1000f > L.Los * 1.25f && dmL * 1000f < L.Los * 3.5f && dmS < 0.02f)
                    {
                        log.Append($" [model {L.Nominal:F0} mm, damage model {dmL * 1000f * L.Cos:F0} mm ({dmL * 1000f:F0} along the shell): damage model used]");
                        L.Los = dmL * 1000f; L.Nominal = L.Los * L.Cos; L.TOut = L.TIn + Mathf.Max(0f, dmS) + dmL;
                    }
                }
                // the final-drive housing next to the sprocket is a thick lump in the model: count it as 10 mm
                if (L.Nominal > 150f && view != null && view.NearSprocket(local, pos + dir * L.TIn))
                {
                    log.Append($" [{L.Nominal:F0} mm part by the sprocket: counted as 10 mm]");
                    float k = 10f / L.Nominal; L.Nominal = 10f; L.Los *= k; L.TOut = L.TIn + L.Los / 1000f;
                }
                var entry = pos + dir * L.TIn; var exitP = pos + dir * L.TOut;
                if (first && !ricoOut) TrackDamage(local, o.Pos - o.Dir * 3f, entry, true);                    // the shell flying into a track on its way
                if (!first && pen > 0f) bodySegs.Add((pos, entry, dir, pen));            // inside the tank, up to this plate
                else AddSegF(rec, root, flightFrom, entry, dir, SpeedAt(pen), o.Diameter, ricoOut ? (byte)(1 | 8) : (byte)1);   // the approach (or the ricochet's flight into another plate: a shot trap)
                if (heat && !first)
                {
                    float dist = Vector3.Distance(lastImpact, entry);                     // jet travel since the last plate's impact point
                    float before = pen, keep = Mathf.Clamp01(1f - dist * Shells.ShellConfig.HEATJetLossPerMetre.Value);
                    pen *= keep;
                    log.Append($" | air gap {dist * 100f:F0} cm: jet {before:F0} -> {pen:F0} mm (-{(1f - keep) * 100f:F0}%)");
                }
                lastImpact = entry;
                if (rec != null && rec.PlateHits.Count < 32) rec.PlateHits.Add((root.InverseTransformPoint(entry), root.InverseTransformDirection(dir)));
                float ang = Mathf.Acos(Mathf.Clamp01(L.Cos)) * Mathf.Rad2Deg;
                bool over = cal >= Shells.ShellConfig.OvermatchRatio.Value * L.Nominal;
                if (first && heat && rec != null)
                {
                    // the warhead goes off on the first plate: a small flash (pen cam only)
                    rec.Blast = true; rec.BlastPoint = root.InverseTransformPoint(entry - dir * 0.05f);
                    rec.BlastRadius = 0.5f * Shells.Explosives.Radius(Mathf.Max(0.05f, o.Filler / 1000f));
                }
                if (first && !ricoOut && rec != null) { rec.Entry = root.InverseTransformPoint(entry); rec.PlateLos = L.Los; rec.Angle = ang; }
                if (!over && ang >= ricoDeg)
                {
                    rico |= first;
                    var refl = Vector3.Reflect(dir, L.Normal).normalized;
                    float pb = pen; pen *= 1f - Mathf.Clamp01(Shells.ShellConfig.RicoPenLoss.Value);
                    log.Append($" | {L.Nominal:F0} mm at {ang:F0} deg: RICOCHET (>= {ricoDeg:F0}), {pb:F0} -> {pen:F0} mm");
                    stopPt = entry;
                    if (first)
                    {
                        if (heat) { if (rec != null && rec.Frags.Count < PenRecord.MaxFrags) rec.Frags.Add(new PenRecord.Frag { Start = root.InverseTransformPoint(entry), Dir = root.InverseTransformDirection(refl), Length = 4f, Speed = SpeedAt(pen), Diameter = o.Diameter, Parent = -1, Flags = 1 | 8, Original = true }); break; }   // HEAT: fuse failure, gone
                        // off the outside: it flies on along the bounce and may hit another plate (a shot trap)
                        ricoOut = true; flightFrom = entry;
                        pos = entry + refl * 0.002f; dir = refl; continue;
                    }
                    // inside / between plates: carries on along the bounce and meets whatever is there
                    pos = entry + refl * 0.002f; dir = refl; continue;
                }
                float req = over ? L.Nominal : L.Los;                                   // line of sight (real thickness when overmatched)
                if (heat && !over) req *= 1f - Shells.ShellConfig.HeatLosReduction(ang);
                first = false;
                if (pen >= req)
                {
                    pen -= req; crossed += req; penAny = true;
                    AddSeg(rec, root, entry, exitP, dir, SpeedAt(pen), o.Diameter, true);
                    // leaving the plate the shell turns a little toward the plate's normal (not HEAT)
                    float n = L.Applique ? Mathf.Max(0f, Shells.ShellConfig.AppliqueExitTurn.Value) : normScale * Shells.ShellConfig.ExitNormalization(over ? 0f : ang);   // applique (fenders, track armour): its own turn (none by default)
                    // turns AWAY from the normal, toward the plate's surface (the plate-parallel direction)
                    var along = dir - Vector3.Dot(dir, L.Normal) * L.Normal;
                    var nd = along.sqrMagnitude > 1e-6f ? Vector3.RotateTowards(dir, along.normalized, n * Mathf.Deg2Rad, 0f).normalized : dir;
                    log.Append($" | {L.Nominal:F0} mm at {ang:F0} deg{(over ? " OVERMATCH" : "")}: has {pen + req:F0}, needs {req:F0}: through, {pen:F0} left{(n > 0.05f ? $", turns {n:F1} deg" : "")}");
                    exits.Add((exitP, nd, pen));
                    if (L.Dm) { _dmLastShape = L.Shape; _dmLastRaw = L.Nominal; _dmLastAt = exitP; }
                    pos = exitP + nd * 0.001f; dir = nd; flightFrom = pos;
                    if (pen <= 0.5f) { stopPt = exitP; break; }
                }
                else
                {
                    float into = Mathf.Clamp01(pen / Mathf.Max(1f, req));
                    stopPt = entry + dir * (L.TOut - L.TIn) * into;
                    AddSeg(rec, root, entry, stopPt, dir, 0f, o.Diameter, false);
                    log.Append($" | {L.Nominal:F0} mm at {ang:F0} deg: has {pen:F0}, needs {req:F0}: stopped");
                    pen = 0f; break;
                }
            }
            _origPen = penAny; _bounce = rico && !penAny; _simPen = penAny; _lockResult = true;   // a shot trap that got in counts as a penetration   // the body/spall sims below must not change the verdict
            _origFound = true; _origStart = o.Pos; _origDir = o.Dir; _origEnd = stopPt;

            // APHE: fuse armed by the armour it went through; bursts after the delay along its (bent) path,
            // or just in front of the next plate / where it stops
            Vector3 burst = Vector3.zero; bool bursts = false; float burstPen = 0f;
            if (aphe && penAny)
            {
                bool armed = crossed >= P.FuseSens;
                if (armed)
                {
                    float left = Mathf.Max(0.05f, P.FuseDist);
                    burst = exits[0].p; burstPen = exits[0].pen;
                    foreach (var sg in bodySegs)
                    {
                        float len = Vector3.Distance(sg.a, sg.b);
                        burstPen = sg.pen; if (left <= len) { burst = sg.a + sg.d * left; left = 0f; break; }
                        left -= len; burst = sg.b - sg.d * 0.03f;
                    }
                    bursts = true;
                    // the shell body ends at the burst
                    for (int k = 0; k < bodySegs.Count; k++)
                    {
                        var sg = bodySegs[k];
                        float along = Vector3.Dot(burst - sg.a, sg.d), len = Vector3.Distance(sg.a, sg.b);
                        if (along >= -0.01f && along <= len + 0.01f) { bodySegs[k] = (sg.a, burst, sg.d, sg.pen); bodySegs.RemoveRange(k + 1, bodySegs.Count - k - 1); break; }
                    }
                    for (int k = exits.Count - 1; k > 0; k--) if (Vector3.Dot(exits[k].p - burst, exits[k].d) > 0.01f) exits.RemoveAt(k);
                }
                log.Append($" | fuse {(armed ? $"armed ({crossed:F0} >= {P.FuseSens:F0} mm)" : $"not armed ({crossed:F0} < {P.FuseSens:F0} mm)")}");
            }
            MPLog.Info("Own model: " + log);

            // inside the tank: the shell body (not HEAT: the jet itself does nothing)
            foreach (var sg in bodySegs)
            {
                if (Vector3.Distance(sg.a, sg.b) < 0.02f) continue;
                if (heat) { AddSeg(rec, root, sg.a, sg.b, sg.d, SpeedAt(sg.pen), o.Diameter, false); continue; }
                if (OwnFragmentsAvailable(local))
                {
                    // the shell body through the modules along this stretch (our trace already knows there is no armour on it)
                    if (rec != null) bodyDraw.Add((sg.a, sg.b, sg.d, sg.pen));
                    Fire(cs, local, sg.a + sg.d * 0.002f, sg.d, Mathf.Max(30f, SpeedAt(sg.pen)), o.Mass, o.Diameter, o.PenConst, 0, 1f, !TestFiring, 0, new System.Random(1), Vector3.Distance(sg.a, sg.b), false);
                    continue;
                }
                var info = new PenetratorInfo(o.Mass, o.Diameter, o.Density, 0f, o.PenConst, sg.a + sg.d * 0.005f, sg.d * Mathf.Max(30f, SpeedAt(sg.pen)));
                IPenetrationSimulation sim;
                int recFrom = rec != null ? rec.Frags.Count : 0;
                _continuing = true;
                try { sim = cs.Penetrate(ref info, PenetrationSimulationOptions.Default); } finally { _continuing = false; }
                // the pen cam draws OUR path for this stretch, not the game's (which can run on straight through plates)
                if (rec != null)
                {
                    for (int k = recFrom; k < rec.Frags.Count; k++) if (rec.Frags[k].Original) { var f = rec.Frags[k]; f.Length = 0f; rec.Frags[k] = f; }
                    bodyDraw.Add((sg.a, sg.b, sg.d, sg.pen));
                }
                if (sim == null) continue;
                StopShellAt(sim, sg.b, sg.d, root);
                StraightOnly(sim, sg.d);
                // the body only flies through air and modules: if the game's path meets armour our trace missed, it ends there
                if (FirstArmour(sim, sg.a, sg.d, out var hardAt) && Vector3.Dot(hardAt - sg.a, sg.d) < Vector3.Distance(sg.a, sg.b) - 0.03f) { StopShellAt(sim, hardAt, sg.d, root); MPLog.Info($"Shell body met armour the trace missed, {Vector3.Distance(sg.a, hardAt) * 100f:F0} cm in: stopped there"); }
                KillNativeSpall(sim, _lastBase);
                if (rec != null) for (int k = recFrom; k < rec.Frags.Count; k++) if (!rec.Frags[k].Original && (rec.Frags[k].Flags & (64 | 128)) == 0) { var f = rec.Frags[k]; f.Length = 0f; rec.Frags[k] = f; }   // the game's spall from the body sim: not ours
                if (!TestFiring) try { sim.ApplyFragmentHits(); } catch { }
            }

            foreach (var bd in bodyDraw) AddSeg(rec, root, bd.a, bd.b, bd.d, SpeedAt(bd.pen), o.Diameter, false);
            // a spall cone behind every plate it came out of
            foreach (var (p, d, pn) in exits)
            {
                float v = SpeedAt(pn);
                v = Mathf.Max(v, o.Speed * (heat ? 0.5f : 0.4f));   // a shell that only just gets through still knocks a real cone of spall off the plate
                MPLog.Info($"Cone behind plate {exits.IndexOf((p, d, pn)) + 1} of {exits.Count}: {pn:F0} mm left, {v:F0} m/s");
                if (v < 20f) continue;
                var cp = AirStart(cs, o.PenConst, p + d * 0.005f, d);
                if (Vector3.Dot(cp - p, d) > 0.025f) { MPLog.Info("Cone skipped: another plate right behind this one (the spall goes into it)"); continue; }
                Shells.Explosives.SpallCone(cs, cp, d, v, o.Mass, cal, o.PenConst, P.ConeDeg, !TestFiring, P.SpallCountMult);
            }

            if (bursts) burst = BackToAir(cs, o.PenConst, burst, exits.Count > 0 ? exits[0].d : o.Dir);
            if (bursts) { Shells.Explosives.Detonate(cs, local, burst, exits.Count > 0 ? exits[0].d : o.Dir, o.Mass, o.Filler, o.PenConst, true, true, !TestFiring, cal, false, -1f, SpeedAt(burstPen)); MarkBlast(root); }
            if (rec != null) { rec.ShellSpeed = o.Speed; rec.ShellDir = root.InverseTransformDirection(o.Dir); }
        } static string _trialInfo = "";
        /// <summary>The mod's armour rules, applied before the game simulates the hit:
        ///  * overmatch (calibre >= OvermatchCalibreRatio x plate thickness): no ricochet, the slope is ignored
        ///    (the shell turns square onto the plate, so only its real thickness counts);
        ///  * otherwise the plate counts as its line-of-sight thickness: if the shell has that much penetration but the
        ///    game's own (steeper) slope penalty would stop it, the shell gets just enough extra to get through that
        ///    plate. A ricochet stays a ricochet.</summary>
        static bool ApplyArmourRules(CompoundStructure cs, VehicleRig rig, ref PenetratorInfo pi, float shellPenMm, float heatBonus)
        {
            try
            {
                var view = UI_PenCamViewInstance();
                if (view == null) { MPLog.Info("Armour rules: no pen cam view yet, game result kept"); return false; }
                if (!view.MeasurePlate(rig, pi.Position, pi.Velocity.normalized, out float nominal, out float los, out float ang)) { MPLog.Info("Armour rules: could not find the plate on the tank model, game result kept"); return false; }
                if (heatBonus > 0f)
                {
                    // HEAT: the slope counts less - for the rule, and for the shell itself
                    float hb = Shells.ShellConfig.HeatAngleFactor(heatBonus, ang);
                    los /= hb;
                    pi = new PenetratorInfo(pi.Mass, pi.Diameter, pi.Density, pi.MaxNormalizationAngle, pi.PenetratorConstant, pi.Position, pi.Velocity * Mathf.Pow(hb, 1f / 1.43f));
                }
                ReadMaterials(cs);
                float cal = pi.Diameter * 1000f;
                if (cal >= Shells.ShellConfig.OvermatchRatio.Value * nominal)
                {
                    pi = new PenetratorInfo(pi.Mass, pi.Diameter, pi.Density, 90f, pi.PenetratorConstant, pi.Position, pi.Velocity);
                    MPLog.Info($"Armour rules: overmatch ({cal:F0} mm shell vs {nominal:F0} mm plate at {ang:F0} deg): no ricochet, slope ignored");
                    return true;
                }
                if (!Shells.ShellConfig.LosArmour.Value) return true;   // line-of-sight boost off: the game decides (overmatch/HEAT bonus still applied)
                if (shellPenMm < los) { MPLog.Info($"Armour rules: {shellPenMm:F0} mm vs {los:F0} mm {(heatBonus > 0f ? "effective vs HEAT" : "line of sight")} ({nominal:F0} mm at {ang:F0} deg): not enough"); return true; }
                bool pen = Trial(cs, pi, out bool rico);
                MPLog.Info($"Armour rules: trial as fired: {_trialInfo}");
                if (pen || rico) { MPLog.Info($"Armour rules: {shellPenMm:F0} mm vs {los:F0} mm line of sight: game says {(pen ? "penetration" : "ricochet")}, kept"); return true; }
                foreach (float boost in new[] { 1.1f, 1.25f, 1.45f, 1.7f, 2.0f, 2.5f, 3.2f })
                {
                    var b = new PenetratorInfo(pi.Mass, pi.Diameter, pi.Density, pi.MaxNormalizationAngle, pi.PenetratorConstant, pi.Position, pi.Velocity * Mathf.Pow(boost, 1f / 1.43f));
                    if (Trial(cs, b, out bool r2) && !r2)
                    {
                        pi = b; _boostV = Mathf.Pow(boost, 1f / 1.43f);   // taken back off once the shell is through this plate
                        MPLog.Info($"Armour rules: {shellPenMm:F0} mm vs {los:F0} mm line of sight ({nominal:F0} mm at {ang:F0} deg): penetrates (game's slope penalty offset, x{boost:F2})");
                        return true;
                    }
                    MPLog.Info($"Armour rules: trial x{boost:F2}: {_trialInfo}");
                    if (r2) break;   // the extra speed made it glance off: leave the game's result
                }
                MPLog.Info($"Armour rules: {shellPenMm:F0} mm vs {los:F0} mm line of sight: could not get through, game result kept");
                return true;
            }
            catch (Exception e) { MPLog.Warn("Armour rules: " + e); _trial = false; return false; }
        }
        static PenCamView UI_PenCamViewInstance() => PenCamView.Instance;

        /// <summary>Simulates a hit without applying anything: did the shell get through a plate / ricochet?</summary>
        static unsafe bool Trial(CompoundStructure cs, PenetratorInfo info, out bool ricochet)
        {
            ricochet = false;
            IPenetrationSimulation sim;
            _trial = true;
            try { sim = cs.Penetrate(ref info, PenetrationSimulationOptions.Default); } finally { _trial = false; }
            if (sim == null || !InitOffsets()) return false;
            byte* obj = (byte*)sim.Pointer;
            int count = *(int*)(obj + _offFragCount);
            IntPtr arr = *(IntPtr*)(obj + _offFragArr);
            if (arr == IntPtr.Zero || count <= 0) return false;
            long maxLen = *(long*)((byte*)arr + 3 * IntPtr.Size); if (count > maxLen) count = (int)maxLen;
            byte* data = (byte*)arr + 4 * IntPtr.Size;
            // the shell got through a plate = along its own path an armour segment is followed by a non-armour one
            // (air or a module) that it actually travels through
            bool pen = false, prevHard = false; int segs = 0;
            for (int i = 0; i < count; i++)
            {
                byte* e = data + (long)i * _fragSize;
                int ff = *(ushort*)(e + _fFlags);
                if ((ff & 1) == 0) continue;
                segs++;
                short mat = _fMat >= 0 ? *(short*)(e + _fMat) : (short)-1;
                float travel = *(float*)(e + _fTravel);
                if ((ff & 16) != 0) ricochet = true;
                bool hard = IsHard(mat);
                if (prevHard && !hard && float.IsFinite(travel) && travel > 0.01f) pen = true;
                prevHard = hard;
            }
            if (pen) ricochet = false;
            _trialInfo = $"{segs} segs, pen={pen}, rico={ricochet}";
            return pen;
        }

        /// <summary>Asks the game's damage model what is really on the line: fires an unstoppable probe from 'from'
        /// and measures the hard (armour) material it crosses within 'len' metres. False when the model can't say.</summary>
        static int _dmLastShape = -1; static float _dmLastRaw; static Vector3 _dmLastAt; static float _dmUnit = 0f; static int _dmLogs;
        /// <summary>Armour layers along the line from the game's own armour model (its damage-model surfaces carry the
        /// raw thickness, like the designer's armour view). Null if that model isn't available for this tank.</summary>
        static List<PenCamView.ArmourLayer> DmLayers(PenCamView view, VehicleRig local, CompoundStructure cs, Vector3 pos, Vector3 dir, OwnShot o, System.Text.StringBuilder log)
        {
            var hits = view.TraceDM(local, pos, dir, 15f);
            if (hits.Count == 0) return null;
            bool anyT = false; foreach (var h in hits) if (h.Raw > 0.5f) { anyT = true; break; }
            if (!anyT) return null;
            // units: calibrate once against the game's armour sampler (expected: mm)
            if (_dmUnit <= 0f)
            {
                foreach (var h in hits)
                {
                    if (h.Raw <= 0.5f || h.T < 0.002f || !IsHard(h.Mat)) continue;
                    if (SampleArmour(cs, pos + dir * Mathf.Max(0.001f, h.T - 0.02f), dir, 0.2f, o, out float gr, out _, out _) && gr > 0.5f)
                    {
                        float r = gr / h.Raw;
                        _dmUnit = r > 0.8f && r < 1.25f ? 1f : r > 0.08f && r < 0.125f ? 0.1f : r > 8f && r < 12.5f ? 10f : 1f;
                        MPLog.Info($"Game armour model: surface thickness {h.Raw:F1} vs armour view {gr:F1} mm -> x{_dmUnit}");
                    }
                    break;
                }
                if (_dmUnit <= 0f) _dmUnit = -1f;   // not calibrated yet: assume mm this time
            }
            float unit = _dmUnit > 0f ? _dmUnit : 1f;
            var res = new List<PenCamView.ArmourLayer>();
            var sb = Plugin.VerboseLogging.Value && _dmLogs < 30 ? new System.Text.StringBuilder() : null;
            if (sb != null) foreach (var h in hits) sb.Append($" {h.T * 100f:F0}cm:{h.Raw * unit:F0}mm/m{h.Mat}");
            for (int hi = 0; hi < hits.Count; hi++)
            {
                var h = hits[hi];
                if (h.T < 0.0005f || !SteelFace(h)) continue;                                   // modules, crew, fuel... (soft materials)
                var at = pos + dir * h.T;
                if (PenCamView.Instance != null && PenCamView.Instance.InWheel(local, at)) continue;   // wheels / sprocket
                int j = SolidPartner(hits, hi);
                float tOut = j >= 0 ? hits[j].T : -1f;
                float cos = Mathf.Clamp(h.Cos, 0.05f, 1f), rha = Rha(h.Mat), raw, los;
                // the plate's raw thickness is on one of its two faces (the other is marked -1): outer or inner, as it was built
                float rawFace = h.Raw > 0.5f ? h.Raw : j >= 0 && hits[j].Raw > 0.5f ? hits[j].Raw : -1f;
                if (h.Raw < -0.5f && j < 0) continue;                                            // the far face of a plate we are already in
                if (rawFace > 0.5f) { raw = rawFace * unit * rha; los = raw / cos; if (tOut < 0f) tOut = h.T + rawFace * unit / cos / 1000f; }
                else
                {
                    // a solid part with no thickness value (applique like fenders, engine block, gun breech): its material along the path
                    if (tOut < 0f) continue;
                    los = (tOut - h.T) * 1000f * rha; raw = los * cos;
                }
                res.Add(new PenCamView.ArmourLayer { TIn = h.T, TOut = tOut, Cos = cos, Los = los, Nominal = raw, FaceNominal = -1f, Normal = h.Normal, Dm = true, Mat = h.Mat, Shape = h.Shape, Applique = Math.Abs(h.Raw) <= 0.5f });
                if (j > hi) hi = j;                                                              // its other face is part of it
            }
            if (sb != null) { _dmLogs++; MPLog.Info("Game armour model along the shell:" + sb); }
            return res;
        }

        static float Rha(short mat) => _rha != null && mat >= 0 && mat < _rha.Length ? _rha[mat] : 1f;
        /// <summary>A face of something solid that stops shells: a plate face (thickness value, or -1 on its other side), or
        /// a face of a part made of a hard material (rha >= ArmourRhaThreshold).</summary>
        internal static bool SteelFace(PenCamView.DmHit h) => Math.Abs(h.Raw) > 0.5f || (h.Mat >= 0 && IsHard(h.Mat));
        /// <summary>The other face of the solid entered at hits[i], or -1. A plate's two faces are one carrying its thickness
        /// and one marked -1 (either may be the outer one); a hard part without a thickness value has faces of 0. The other
        /// face must be of the matching kind, in the same mesh and part, and no further than the plate's thickness allows.</summary>
        internal static int SolidPartner(List<PenCamView.DmHit> hits, int i)
        {
            var h = hits[i];
            int kind = h.Raw > 0.5f ? 1 : h.Raw < -0.5f ? -1 : 0;
            float maxT = kind == 1 ? h.T + h.Raw / Mathf.Max(0.05f, h.Cos) * 0.0016f + 0.02f : kind == -1 ? h.T + 0.8f : h.T + 3f;
            int nn = -1;   // -1 face entered: a -1 face behind it, used only if no thickness face matches
            for (int j = i + 1; j < hits.Count; j++)
            {
                var b = hits[j];
                if (b.T > maxT) break;
                if (b.Shape != h.Shape || b.T <= h.T + 0.0003f || !SteelFace(b)) continue;
                if (h.Obj >= 0 && b.Obj >= 0 && b.Obj != h.Obj) continue;
                int bk = b.Raw > 0.5f ? 1 : b.Raw < -0.5f ? -1 : 0;
                if (kind == 1) { if (bk == -1) return j; continue; }
                if (kind == 0) { if (bk == 0) return j; continue; }
                // kind -1: the plate's thickness face, if it lies within that thickness
                if (bk == 1 && b.T <= h.T + b.Raw / Mathf.Max(0.05f, b.Cos) * 0.0016f + 0.02f) return j;
                if (bk == -1 && nn < 0 && b.T <= h.T + 0.35f) nn = j;
            }
            if (nn >= 0)
            {
                // a plate with -1 on both faces - unless a thickness face comes between (then that -1 face is another plate's)
                for (int j = i + 1; j < nn; j++) if (hits[j].Shape == h.Shape && hits[j].Raw > 0.5f) return -1;
                return nn;
            }
            return -1;
        }

        static int _sampleLogs; static bool _sampleOk; static int _sampleUnit;   // 0 unknown, 1 mm, 1000 metres
        /// <summary>The game's armour sampler (the designer's armour view): raw thickness and line of sight (mm) and
        /// the incidence angle (deg) of the first armour surface along the ray.</summary>
        static bool SampleArmour(CompoundStructure cs, Vector3 from, Vector3 dir, float maxDist, OwnShot o, out float raw, out float los, out float ang)
        {
            raw = los = 0f; ang = 0f;
            try
            {
                var st = new SampleSettings(o.Diameter, o.Mass, Mathf.Max(1f, o.Speed), 0f);
                SampleHit hit;
                bool ok;
                _trial = true;
                try { ok = cs.Sample(from, dir, maxDist, ref st, out hit); } finally { _trial = false; }
                if (!ok) return false;
                float r = hit.Thickness, l = hit.LOSThickness;
                if (!(r > 0f) || !float.IsFinite(r) || !float.IsFinite(l)) return false;
                if (_sampleUnit == 0) { _sampleUnit = r < 1.5f && l < 5f ? 1000 : 1; MPLog.Info($"Game armour sampler: raw {r} LOS {l} effective {hit.EffectiveThickness} angle {hit.SurfaceIncidenceAngle} -> units {(_sampleUnit == 1 ? "mm" : "metres")}"); }
                raw = r * _sampleUnit; los = Mathf.Max(raw, l * _sampleUnit);
                ang = Mathf.Acos(Mathf.Clamp01(raw / Mathf.Max(0.001f, los))) * Mathf.Rad2Deg;
                _sampleOk = true;
                return true;
            }
            catch (Exception e) { if (_sampleLogs++ < 5) MPLog.Warn("armour sampler: " + e.Message); _trial = false; return false; }
        }

        static unsafe bool ProbeArmour(CompoundStructure cs, ushort penConst, Vector3 from, Vector3 dir, float len, out float hardStart, out float hardEnd, out float hardLen, bool heavy = false, float skipBefore = 0f)
        { MPLog.Stage = "armour probe";
            hardStart = hardEnd = hardLen = 0f;
            try
            {
                var info = heavy ? new PenetratorInfo(5000f, 0.2f, 20000f, 0f, penConst, from, dir * 6000f) : Probe(penConst, from, dir);
                IPenetrationSimulation sim;
                _trial = true;
                try { sim = cs.Penetrate(ref info, PenetrationSimulationOptions.Default); } finally { _trial = false; }
                if (sim == null || !InitOffsets() || _fMat < 0) return false;
                byte* obj = (byte*)sim.Pointer;
                int count = *(int*)(obj + _offFragCount);
                IntPtr arr = *(IntPtr*)(obj + _offFragArr);
                if (arr == IntPtr.Zero || count <= 0) return false;
                long maxLen = *(long*)((byte*)arr + 3 * IntPtr.Size); if (count > maxLen) count = (int)maxLen;
                byte* data = (byte*)arr + 4 * IntPtr.Size;
                float t = 0f; bool any = false;
                for (int i = 0; i < count && t < len; i++)
                {
                    byte* e = data + (long)i * _fragSize;
                    int ff = *(ushort*)(e + _fFlags);
                    if ((ff & 1) == 0) continue;
                    if ((ff & 16) != 0) return false;                         // deflected: can't measure this way
                    float tr = *(float*)(e + _fTravel);
                    if (!float.IsFinite(tr) || tr < 0f) break;
                    short mat = *(short*)(e + _fMat);
                    if (HardAt(mat, from + dir * (t + tr * 0.5f)) && t + tr > skipBefore)   // steel of an earlier plate just before this one doesn't count
                    {
                        float a = Math.Max(t, skipBefore), b = Math.Min(t + tr, len);
                        if (b > a) { if (!any) hardStart = a; hardEnd = b; hardLen += b - a; any = true; }
                    }
                    else if (any && tr > 0.01f) break;                          // left the plate (air or a module)
                    t += tr;
                }
                return true;
            }
            catch (Exception ex) { MPLog.Debug("probe: " + ex.Message); _trial = false; return false; }
        }

        /// <summary>A point in the air: if 'p' lies inside a plate (the game then treats the whole tank inside-out for a
        /// fragment born there), moves it forward along 'd' to just past that plate's back face.</summary>
        /// <summary>A burst point in the air: if 'p' lies inside armour, moves it BACK along 'd' to just in front of that
        /// armour (a burst inside steel would send every fragment through the whole tank).</summary>
        /// <summary>Is this point inside any steel (wheels included), by the damage model?</summary>
        public static bool InSteel(CompoundStructure cs, ushort penConst, Vector3 p, Vector3 d)
        {
            var q = BackToAirQuiet(cs, penConst, p, d);
            return (q - p).sqrMagnitude > 1e-6f;
        }
        static bool _quietBack;
        static Vector3 BackToAirQuiet(CompoundStructure cs, ushort penConst, Vector3 p, Vector3 d) { _quietBack = true; try { return BackToAir(cs, penConst, p, d); } finally { _quietBack = false; } }

        static VehicleRig DmRig => _victim ?? _probeRig;

        /// <summary>The game's armour model: move 'p' back along 'd' out of any armour plate and out of the wheels.</summary>
        static bool DmBackToAir(Vector3 p, Vector3 d, out Vector3 q)
        {
            q = p;
            var view = PenCamView.Instance; var rig = DmRig;
            if (view == null || rig == null || !view.HasDm(rig)) return false;
            d = d.normalized;
            for (int k = 0; k < 4; k++)
            {
                if (!view.DmSteelAround(rig, q, d, out float back, out _)) break;
                q -= d * (back + 0.03f);
            }
            for (int k = 0; k < 75 && view.InWheel(rig, q); k++) q -= d * 0.02f;    // wheels / sprocket: no burst inside them either
            if (!_quietBack && (q - p).sqrMagnitude > 1e-6f) MPLog.Info($"Burst point was {(q - p).magnitude * 1000f:F0} mm inside armour or a wheel (game armour model): moved in front of it");
            return true;
        }

        public static unsafe Vector3 BackToAir(CompoundStructure cs, ushort penConst, Vector3 p, Vector3 d)
        {
            if (DmBackToAir(p, d, out var dq)) return dq;
            const float back = 8f;
            try
            {
                var info = Probe(penConst, p - d * back, d);
                IPenetrationSimulation sim;
                _trial = true;
                try { sim = cs.Penetrate(ref info, PenetrationSimulationOptions.Default); } finally { _trial = false; }
                if (sim == null || !InitOffsets() || _fMat < 0) return p;
                byte* obj = (byte*)sim.Pointer;
                int count = *(int*)(obj + _offFragCount);
                IntPtr arr = *(IntPtr*)(obj + _offFragArr);
                if (arr == IntPtr.Zero || count <= 0) return p;
                long maxLen = *(long*)((byte*)arr + 3 * IntPtr.Size); if (count > maxLen) count = (int)maxLen;
                byte* data = (byte*)arr + 4 * IntPtr.Size;
                float t = 0f, hardFrom = -1f;
                for (int i = 0; i < count; i++)
                {
                    byte* e = data + (long)i * _fragSize;
                    if ((*(ushort*)(e + _fFlags) & 1) == 0) continue;
                    float tr = *(float*)(e + _fTravel); if (!float.IsFinite(tr) || tr < 0f) break;
                    bool hard = !IsAir(*(short*)(e + _fMat));   // anything solid (armour, wheels, add-on track armour...): nothing may be born inside it
                    if (hard) { if (hardFrom < 0f) hardFrom = t; } else if (tr > 0.005f) hardFrom = -1f;
                    if (t + tr >= back) { if (hard && hardFrom >= 0f) { if (!_quietBack) MPLog.Info($"Burst point was {(back - hardFrom) * 1000f:F0} mm inside something solid: moved in front of it"); return p - d * back + d * Mathf.Max(0f, hardFrom - 0.03f); } return p; }
                    t += tr;
                }
            }
            catch (Exception ex) { MPLog.Debug("back to air: " + ex.Message); _trial = false; }
            return p;
        }

        public static unsafe Vector3 AirStart(CompoundStructure cs, ushort penConst, Vector3 p, Vector3 d)
        { MPLog.Stage = "spall start probe";
            {
                // the game's armour model: if p is inside a plate, just past that plate's back face
                var view = PenCamView.Instance; var rig = DmRig;
                if (view != null && rig != null && view.HasDm(rig))
                {
                    var dn = d.normalized;
                    if (!view.DmSteelAround(rig, p, dn, out _, out float on)) return p;
                    MPLog.Info($"Spall start was {on * 1000f:F0} mm inside armour (game armour model): moved behind it");
                    return p + dn * (on + 0.01f);
                }
            }
            // probe from far outside the tank (surely in the air) along d, and look at what is at p
            const float back = 8f;
            try
            {
                var info = Probe(penConst, p - d * back, d);
                IPenetrationSimulation sim;
                _trial = true;
                try { sim = cs.Penetrate(ref info, PenetrationSimulationOptions.Default); } finally { _trial = false; }
                if (sim == null || !InitOffsets() || _fMat < 0) return p;
                byte* obj = (byte*)sim.Pointer;
                int count = *(int*)(obj + _offFragCount);
                IntPtr arr = *(IntPtr*)(obj + _offFragArr);
                if (arr == IntPtr.Zero || count <= 0) return p;
                long maxLen = *(long*)((byte*)arr + 3 * IntPtr.Size); if (count > maxLen) count = (int)maxLen;
                byte* data = (byte*)arr + 4 * IntPtr.Size;
                float t = 0f; bool inHard = false;
                for (int i = 0; i < count; i++)
                {
                    byte* e = data + (long)i * _fragSize;
                    int ff = *(ushort*)(e + _fFlags);
                    if ((ff & 1) == 0) continue;
                    if ((ff & 16) != 0) { MPLog.Debug("air start: probe deflected"); return p; }
                    float tr = *(float*)(e + _fTravel);
                    if (!float.IsFinite(tr) || tr < 0f) break;
                    bool hard = IsHard(*(short*)(e + _fMat));   // any steel, wheels included: nothing may be born inside it
                    if (t + tr >= back - 0.002f && t <= back + 0.002f && hard) inHard = true;
                    if (inHard && !hard && t >= back - 0.002f)
                    {
                        MPLog.Info($"Spall start was {(t - back) * 1000f:F0} mm inside armour: moved behind it");
                        return p - d * back + d * (t + 0.01f);
                    }
                    t += tr;
                    if (t > back + 1.5f) break;
                }
            }
            catch (Exception ex) { MPLog.Debug("air start: " + ex.Message); _trial = false; }
            return p;
        }

        /// <summary>Cone fragments that got stuck in a module (crew, ammo, engine...): where they stopped, their direction
        /// and the speed they hit it with. Used to let spall carry on through soft modules.</summary>
        public static unsafe List<(Vector3 at, Vector3 dir, float speed, short mat)> StuckInModules(IPenetrationSimulation simI)
        {
            var res = new List<(Vector3, Vector3, float, short)>();
            try
            {
                if (simI == null || !InitOffsets() || _fMat < 0) return res;
                byte* obj = (byte*)simI.Pointer;
                int count = *(int*)(obj + _offFragCount);
                IntPtr arr = *(IntPtr*)(obj + _offFragArr);
                if (arr == IntPtr.Zero || count <= 0) return res;
                long maxLen = *(long*)((byte*)arr + 3 * IntPtr.Size); if (count > maxLen) count = (int)maxLen;
                byte* data = (byte*)arr + 4 * IntPtr.Size;
                var m = _lastSimToWorld;
                for (int i = 0; i < count; i++)
                {
                    byte* e = data + (long)i * _fragSize;
                    int ff = *(ushort*)(e + _fFlags);
                    if ((ff & 32) == 0) continue;                                  // only fragments that stopped (embedded)
                    short mat = *(short*)(e + _fMat);
                    if (IsAir(mat) || IsHard(mat)) continue;                       // stopped by armour: fine
                    float tr = *(float*)(e + _fTravel); if (!float.IsFinite(tr)) continue;
                    Vector3 sp = m.MultiplyPoint3x4(*(Vector3*)(e + _fSpawn)), dr = m.MultiplyVector(*(Vector3*)(e + _fDir)).normalized;
                    res.Add((sp + dr * tr, dr, *(float*)(e + _fSpeed), mat));
                }
            }
            catch (Exception ex) { MPLog.Debug("stuck: " + ex.Message); }
            return res;
        }

        /// <summary>Where each root fragment of a simulation stopped or glanced off (end of its first segment), with its
        /// direction and speed: used to let fragments carry on past the running gear.</summary>
        public static unsafe List<(Vector3 at, Vector3 dir, float speed)> FragEnds(IPenetrationSimulation simI)
        {
            var res = new List<(Vector3, Vector3, float)>();
            try
            {
                if (simI == null || !InitOffsets()) return res;
                byte* obj = (byte*)simI.Pointer;
                int count = *(int*)(obj + _offFragCount);
                IntPtr arr = *(IntPtr*)(obj + _offFragArr);
                if (arr == IntPtr.Zero || count <= 0) return res;
                long maxLen = *(long*)((byte*)arr + 3 * IntPtr.Size); if (count > maxLen) count = (int)maxLen;
                byte* data = (byte*)arr + 4 * IntPtr.Size;
                var m = _lastSimToWorld;
                for (int i = 0; i < count; i++)
                {
                    byte* e = data + (long)i * _fragSize;
                    if (*(short*)(e + _fParent) >= 0) continue;                    // root fragments only
                    int ff = *(ushort*)(e + _fFlags);
                    if ((ff & (16 | 32)) == 0) continue;                            // went on through: nothing to do
                    float tr = *(float*)(e + _fTravel); if (!float.IsFinite(tr)) continue;
                    Vector3 sp = m.MultiplyPoint3x4(*(Vector3*)(e + _fSpawn)), dr = m.MultiplyVector(*(Vector3*)(e + _fDir)).normalized;
                    res.Add((sp + dr * tr, dr, *(float*)(e + _fSpeed)));
                }
            }
            catch (Exception ex) { MPLog.Debug("frag ends: " + ex.Message); }
            return res;
        }

        /// <summary>How far along 'd' from 'p' the module material 'mat' continues (damage model probe), max 1.2 m.</summary>
        public static unsafe float ModuleDepth(CompoundStructure cs, Vector3 p, Vector3 d, short mat)
        {
            try
            {
                var info = Probe(1, p - d * 0.002f, d);
                IPenetrationSimulation sim;
                _trial = true;
                try { sim = cs.Penetrate(ref info, PenetrationSimulationOptions.Default); } finally { _trial = false; }
                if (sim == null || !InitOffsets()) return 0.4f;
                byte* obj = (byte*)sim.Pointer;
                int count = *(int*)(obj + _offFragCount);
                IntPtr arr = *(IntPtr*)(obj + _offFragArr);
                if (arr == IntPtr.Zero || count <= 0) return 0.4f;
                long maxLen = *(long*)((byte*)arr + 3 * IntPtr.Size); if (count > maxLen) count = (int)maxLen;
                byte* data = (byte*)arr + 4 * IntPtr.Size;
                float t = 0f;
                for (int i = 0; i < count; i++)
                {
                    byte* e = data + (long)i * _fragSize;
                    if ((*(ushort*)(e + _fFlags) & 1) == 0) continue;
                    float tr = *(float*)(e + _fTravel); if (!float.IsFinite(tr)) break;
                    short nm = *(short*)(e + _fMat); if (nm != mat && t > 0.005f) return IsHard(nm) ? -Mathf.Clamp(t, 0.05f, 1.2f) : Mathf.Clamp(t, 0.05f, 1.2f);   // negative: armour right behind it
                    t += tr; if (t > 1.2f) break;
                }
            }
            catch (Exception ex) { MPLog.Debug("module depth: " + ex.Message); _trial = false; }
            return 0.4f;
        }

        /// <summary>The mod's own track hitbox: a shell hitting it destroys the track, every fragment takes a fifth of it.</summary>
        public static int _trackTests, _trackHits;
        internal static bool TrackDamage(VehicleRig rig, Vector3 a, Vector3 b, bool shell)
        {
            try
            {
                if (rig == null || PenCamView.Instance == null) { if (shell) MPLog.Info("Track check: no victim / pen cam"); return false; }
                int mod = PenCamView.Instance.TrackHit(rig, a, b);
                if (shell && mod < 0) MPLog.Info("Track check: the shell's flight does not cross a track hitbox");
                if (mod < 0 || mod >= rig.Pools.Count || rig.Pools[mod] == null) return false;
                var pool = rig.Pools[mod];
                float max = 1f; try { max = pool.max; } catch { }
                pool.HealthDelta(-max * (shell ? 1f : Mathf.Clamp01(Shells.ShellConfig.TrackFragmentDamage.Value)));
                if (shell) MPLog.Info("Track hit by the shell");
                return true;
            }
            catch (Exception e) { MPLog.Info("track damage: " + e.Message); }
            return false;
        }

        static unsafe void SetRha<T>(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<T> arr, int idx, float v) where T : unmanaged
        {
            int off = (int)System.Runtime.InteropServices.Marshal.OffsetOf<T>("rhaFactor");
            byte* p = (byte*)arr.Pointer + 4 * IntPtr.Size + (long)idx * sizeof(T) + off;
            *(float*)p = v;
        }

        /// <summary>An invisible test round that goes through anything, with as little energy as possible (a huge one
        /// made the game shake the camera on every hit).</summary>
                static PenetratorInfo Probe(ushort penConst, Vector3 from, Vector3 dir)
        {
            return new PenetratorInfo(50f, 0.05f, 20000f, 0f, penConst, from, dir * 3000f);   // goes through any tank, ~1/1000 of the old round's energy
        }

        static readonly HashSet<IntPtr> _softWheels = new HashSet<IntPtr>();
        /// <summary>Wheels and sprocket count as (almost) no armour: finds their damage-model material by probing through
        /// them from the side and sets its armour factor to ~0, unless the hull armour uses the same material.</summary>
        static unsafe void SoftenWheels(CompoundStructure cs, VehicleRig rig)
        { MPLog.Stage = "wheel probing";
            if (cs == null || rig == null || PenCamView.Instance == null || _softWheels.Contains(cs.Pointer)) return;
            var probes = PenCamView.Instance.WheelProbes(rig);
            if (probes.Count == 0) return;
            _softWheels.Add(cs.Pointer);
            try
            {
                ReadMaterials(cs);
                var wheelMats = new HashSet<short>(); var hullMats = new HashSet<short>();
                foreach (var (a, b, w0, w1) in probes)
                {
                    var dir = (b - a).normalized; float len = (b - a).magnitude;
                    var info = Probe(1, a, dir);
                    IPenetrationSimulation sim;
                    _trial = true;
                    try { sim = cs.Penetrate(ref info, PenetrationSimulationOptions.Default); } finally { _trial = false; }
                    if (sim == null || !InitOffsets() || _fMat < 0) continue;
                    byte* obj = (byte*)sim.Pointer;
                    int count = *(int*)(obj + _offFragCount);
                    IntPtr arr = *(IntPtr*)(obj + _offFragArr);
                    if (arr == IntPtr.Zero || count <= 0) continue;
                    long maxLen = *(long*)((byte*)arr + 3 * IntPtr.Size); if (count > maxLen) count = (int)maxLen;
                    byte* data = (byte*)arr + 4 * IntPtr.Size;
                    float t = 0f;
                    for (int i = 0; i < count && t < len; i++)
                    {
                        byte* e = data + (long)i * _fragSize;
                        if ((*(ushort*)(e + _fFlags) & 1) == 0) continue;
                        float tr = *(float*)(e + _fTravel); if (!float.IsFinite(tr)) break;
                        short mat = *(short*)(e + _fMat);
                        if (!IsAir(mat)) { if (t + tr > w0 && t < w1) wheelMats.Add(mat); else if (t >= w1) hullMats.Add(mat); }
                        t += tr;
                    }
                }
                var mats = cs.materials; var done = new System.Text.StringBuilder();
                foreach (var wm in wheelMats)
                {
                    if (hullMats.Contains(wm) || wm < 0 || wm >= mats.Length) { done.Append($" {wm}(shared with hull, kept)"); continue; }
                    if (_rha != null && wm < _rha.Length && _rha[wm] >= Shells.ShellConfig.ArmourRhaThreshold.Value) { done.Append($" {wm}(armour steel, kept)"); continue; }
                    if (!Shells.ShellConfig.SoftWheels.Value) { done.Append($" {wm}(off in config)"); continue; }
                    SetRha(mats, wm, 0.005f);
                    done.Append($" {wm}");
                }
                _rha = null; ReadMaterials(cs);
                MPLog.Info($"Wheels/sprocket made (almost) armourless: materials{(done.Length > 0 ? done.ToString() : " none found")} (hull: {string.Join(",", hullMats)})");
            }
            catch (Exception e) { MPLog.Warn("soften wheels: " + e.Message); _trial = false; }
        }

        /// <summary>HE bursting on a surface: also step 5 cm off it along the struck surface's normal (thin parts like fenders:
        /// stepping back along the shell's path alone can leave the burst inside them).</summary>
        static Vector3 OffSurface(VehicleRig rig, Vector3 pt, Vector3 dir)
        {
            try
            {
                if (rig?.GO == null) return pt;
                var root = rig.GO.transform;
                var hits = Physics.RaycastAll(pt - dir * 1f, dir, 1.3f, ~0, QueryTriggerInteraction.Ignore);
                float best = float.MaxValue; Vector3 n = Vector3.zero;
                foreach (var h in hits) { var c = h.collider; if (c == null || !c.transform.IsChildOf(root)) continue; if (h.distance < best) { best = h.distance; n = h.normal; } }
                if (n.sqrMagnitude < 0.5f) return pt;
                if (Vector3.Dot(n, dir) > 0f) n = -n;   // towards the shell's side
                return pt + n.normalized * Mathf.Max(0f, Shells.ShellConfig.HEBurstOffSurface.Value);
            }
            catch { return pt; }
        }

        static float _impactAngle = -1f;
        /// <summary>Angle (deg) between the shell's path and the struck surface's normal, from the tank's colliders; -1 if unknown.</summary>
        static float ImpactAngle(VehicleRig rig, Vector3 pos, Vector3 dir)
        {
            try
            {
                if (rig?.GO == null || dir.sqrMagnitude < 0.5f) return -1f;
                var root = rig.GO.transform;
                var hits = Physics.RaycastAll(pos - dir * 2f, dir, 6f, ~0, QueryTriggerInteraction.Ignore);
                float best = float.MaxValue; Vector3 n = Vector3.zero;
                foreach (var h in hits)
                {
                    var c = h.collider; if (c == null || !c.transform.IsChildOf(root)) continue;
                    if (h.distance < best) { best = h.distance; n = h.normal; }
                }
                if (best == float.MaxValue || n.sqrMagnitude < 0.5f) return -1f;
                return Vector3.Angle(-dir, n);
            }
            catch (Exception e) { MPLog.Debug("impact angle: " + e.Message); return -1f; }
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
        { MPLog.Stage = "game hit (prefix)";
            if (_trial) return;
            _curShot = null; _skipShot = false;
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
                float vm = shot.Value.Scaled || TestFiring ? Mathf.Max(0.05f, sp.VelMult) : 1f;   // sabot rounds fly faster (if that worked for this shot), but their penetration is set by their multiplier only
                float speed = penetrator.Velocity.magnitude / vm;
                float apPen = PenetrationUtils.ComputePenetration(penetrator.Diameter * 1000f, penetrator.Mass, speed, penetrator.PenetratorConstant);   // diameter in mm
                float f = kind == Shells.ShellType.HE ? 0.02f : sp.PenFactor;   // shooter's penetration modifier; HE shell body does not penetrate
                _impactAngle = ImpactAngle(target, penetrator.Position, penetrator.Velocity.normalized);
                _boostV = 1f;
                MPLog.Info($"{Shells.ShellConfig.Name(kind)} hit: AP would be {apPen:F0} mm -> {apPen * f:F0} mm ({filler:F0} g filler)");
                float nf = sp.Normalization;
                if (Mathf.Abs(f - 1f) > 0.001f || Mathf.Abs(nf - 1f) > 0.001f || Mathf.Abs(vm - 1f) > 0.001f)
                    penetrator = new PenetratorInfo(penetrator.Mass, penetrator.Diameter, penetrator.Density, penetrator.MaxNormalizationAngle * nf, penetrator.PenetratorConstant, penetrator.Position, penetrator.Velocity / vm * Mathf.Pow(Mathf.Max(f, 0.01f), 1f / 1.43f));
                _own.Active = false;
                if (Shells.ShellConfig.OwnModel.Value && (kind != Shells.ShellType.HE || OwnFragmentsAvailable(target)) && penetrator.Velocity.sqrMagnitude > 1f)
                {
                    // the same shell touching us again (the game's tap carried on): nothing more
                    if (_resolved.Contains((shot.Value.Shooter, shot.Value.Time)))
                    {
                        penetrator = new PenetratorInfo(penetrator.Mass, penetrator.Diameter, penetrator.Density, 0f, penetrator.PenetratorConstant, penetrator.Position, penetrator.Velocity.normalized * 0.5f);
                        _curShot = null; _skipShot = true;
                        return;
                    }
                    // the mod decides this hit itself (see ResolveOwn): the game's own shell is reduced to a harmless tap
                    _own = new OwnShot
                    {
                        Active = true, Kind = kind, P = sp, Pos = penetrator.Position, Dir = penetrator.Velocity.normalized,
                        Speed = penetrator.Velocity.magnitude, PenMm = apPen * f, Mass = penetrator.Mass, Diameter = penetrator.Diameter,
                        Density = penetrator.Density, PenConst = penetrator.PenetratorConstant, Filler = filler,
                    };
                    penetrator = new PenetratorInfo(penetrator.Mass, penetrator.Diameter, penetrator.Density, 0f, penetrator.PenetratorConstant, penetrator.Position, _own.Dir * 2f);
                    return;
                }
                bool ruled = kind != Shells.ShellType.HE && ApplyArmourRules(__instance, target, ref penetrator, apPen * f, sp.HeatBonus);
                if (!ruled && sp.HeatBonus > 0f && _impactAngle >= 0f)
                {
                    // no plate measurement: HEAT's slope bonus from the collision angle
                    float hb = Shells.ShellConfig.HeatAngleFactor(sp.HeatBonus, _impactAngle);
                    penetrator = new PenetratorInfo(penetrator.Mass, penetrator.Diameter, penetrator.Density, penetrator.MaxNormalizationAngle, penetrator.PenetratorConstant, penetrator.Position, penetrator.Velocity * Mathf.Pow(hb, 1f / 1.43f));
                }
            }
            catch (Exception e) { MPLog.Warn("Shell prefix: " + e.Message); }
        }

        static void Penetrate_Postfix(CompoundStructure __instance, ref PenetratorInfo penetrator, IPenetrationSimulation __result)
        { MPLog.Stage = "game hit (postfix)";
            if (_trial) return;
            try
            {
                if (__result != null) _origIdx.Remove(__result.Pointer);   // the game reuses simulation objects
                var local = Target(out byte victimId);
                if (local == null || __result == null) return;
                _victim = local;
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
                if (_skipShot) { _skipShot = false; DropAll(__result); return; }
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
                    _pending.Entry = root.InverseTransformPoint(penetrator.Position); _entrySet = false;
                    _pending.Range = Vector3.Distance(shot.Value.Pos, penetrator.Position); _pending.Angle = _impactAngle;
                    _pending.ShellDir = root.InverseTransformDirection(penetrator.Velocity.normalized);
                    _pending.ShellSpeed = penetrator.Velocity.magnitude; _pending.ShellMass = penetrator.Mass; _pending.ShellDiameter = penetrator.Diameter;
                    _pendingSince = Time.time; _pendingFrame = Time.frameCount; _pendingSinceReal = -1f; _origPen = _bounce = false; _lockResult = false;
                    try { var rt = local.GO.transform; var rr = Quaternion.Inverse(rt.rotation); foreach (var xf in local.AimXforms) { _pending.AimRots.Add(xf != null ? rr * xf.rotation : Quaternion.identity); _pending.AimPos.Add(xf != null ? rt.InverseTransformPoint(xf.position) : Vector3.zero); } } catch { }
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
                _shellDia = penetrator.Diameter;
                if (_own.Active)
                {
                    _own.Active = false;
                    DropAll(__result);                                     // the game's tap: nothing at all
                    _resolved.Add((shot.Value.Shooter, shot.Value.Time)); if (_resolved.Count > 64) _resolved.Clear();
                    var swr = System.Diagnostics.Stopwatch.StartNew();
                    ResolveOwn(__instance, local, root, shot.Value);
                    MPLog.Info($"Hit worked out in {swr.ElapsedMilliseconds} ms");
                    Captured++;
                    return;
                }
                AddFragments(_pending, __result, root, local.Body != null ? local.Body.position : root.position);
                Captured++;
                int mainBase = _lastBase; var mainM = _lastSimToWorld;

                // ---- AP / APHE / APDS: our own spall cone instead of the game's spall
                var kindS = (Shells.ShellType)shot.Value.Kind;
                Shells.Explosives.P = shot.Value.P ?? Shells.ShellParams.ForLocal(kindS);
                _contSim = null;
                // (not HEAT: its jet is handled by HeatAirGaps; a continuation here applied full-power hits behind
                //  spaced armour before the gap penalty could cut them - invisible damage behind the second plate)
                if (kindS != Shells.ShellType.HE && kindS != Shells.ShellType.HEAT && _simPen && (Shells.Explosives.P.Normalization < 0.999f || _boostV > 1.001f))
                    ContinueShell(__instance, __result, penetrator, root, mainBase, mainM);
                if (kindS == Shells.ShellType.HEAT && _simPen)
                {
                    _heatSim = __result; _heatBase = mainBase; _heatM = mainM;
                    if (_contSim != null) { _heatSim = _contSim; _heatBase = _contBase; _heatM = _contM; }
                    _heatApply.Clear();
                    HeatAirGaps(__instance, _heatSim, penetrator, root, _heatBase, _heatM);
                    HeatStopInside(root);
                    if (!TestFiring) foreach (var hs in _heatApply) try { hs.ApplyFragmentHits(); } catch { }
                    _heatApply.Clear();
                }
                // one cone behind EVERY plate the shell came out of (armour -> air or a module along its path)
                var exits = new List<(Vector3 s, Vector3 d, float len, short mat, float speed)>();
                if (kindS != Shells.ShellType.HE && _simPen)
                    for (int i = 1; i < _origSegs.Count && exits.Count < 6; i++)
                    {
                        if (!(IsHard(_origSegs[i - 1].mat) && !IsHard(_origSegs[i].mat) && _origSegs[i].len > 0.01f)) continue;
                        var sg = _origSegs[i];
                        if (kindS == Shells.ShellType.HEAT && IsAir(sg.mat) && sg.len <= Shells.ShellConfig.HEATMaxGap.Value && i + 1 < _origSegs.Count && IsHard(_origSegs[i + 1].mat))
                        {
                            // HEAT cone into a spaced-armour gap: as weak as the jet is after that gap (else it punches
                            // through the next plate with the jet's full power)
                            float keep = Mathf.Clamp(1f - Shells.ShellConfig.HEATSpacedLoss.Value - sg.len * Shells.ShellConfig.HEATGapLossPerMetre.Value, 0.03f, 1f);
                            sg.speed *= Mathf.Pow(keep, 1f / 1.43f);
                        }
                        exits.Add(sg);
                    }
                if (kindS != Shells.ShellType.HE && _simPen && (_hasSpall || exits.Count > 0))
                {
                    KillNativeSpall(__result, mainBase);
                    if (exits.Count == 0) exits.Add(NearestSeg(_spallStart));
                    MPLog.Info($"Spall cones: {exits.Count} (shell path {_origSegs.Count} segments)");
                    foreach (var seg in exits)
                    {
                        float resid = seg.speed > 1f ? seg.speed : penetrator.Velocity.magnitude * 0.5f;
                        var axis = seg.d.sqrMagnitude > 0.5f ? Vector3.Slerp(_origDir, seg.d, 0.5f).normalized : _origDir;   // spall flies on mostly along the incoming line
                        Shells.Explosives.SpallCone(__instance, AirStart(__instance, penetrator.PenetratorConstant, !IsHard(seg.mat) && seg.len > 0.04f ? seg.s : _spallStart, axis), axis, resid, penetrator.Mass, penetrator.Diameter * 1000f, penetrator.PenetratorConstant, Shells.Explosives.P.ConeDeg, !TestFiring, Shells.Explosives.P.SpallCountMult);
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
                            pt = BackToAir(__instance, penetrator.PenetratorConstant, pt, _origDir);
                            Shells.Explosives.Detonate(__instance, local, pt, _origDir, penetrator.Mass, filler, penetrator.PenetratorConstant, true, true, !TestFiring, penetrator.Diameter * 1000f, false);   // fragments only
                            MarkBlast(root);
                        }
                    }
                    else
                    {
                        inside = _simPen;
                        // impact point = where the shell's path first enters armour; burst 5 cm in front of it, in the air
                        pt = inside ? (_hasSpall ? _spallStart : _origEnd) : FirstContact() - _origDir * Mathf.Max(0f, Shells.ShellConfig.HEBurstBack.Value);   // outside: on impact with the first thing it touched (a wheel included)
                        if (!inside) TrackDamage(local, pt - _origDir * 3f, pt + _origDir * 0.05f, true);   // HE striking the track itself
                        if (!inside) pt = OffSurface(local, pt, _origDir);
                        pt = BackToAir(__instance, penetrator.PenetratorConstant, pt, _origDir);
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
            LimitDepth(simI, maxDepth, Shells.Explosives.Mode != 2);   // spall cones: hard cut (no bounces at 0); bursts: keep shallow branches
        }
        public static unsafe void LimitDepth(IPenetrationSimulation simI, int maxDepth, bool compact)
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
                // drop every fragment deeper than maxDepth bounces, but KEEP the shallow ones that come after it in the
                // list (truncating at the first deep one used to throw away whole branches -> almost no bounces)
                var depth = new int[count]; var map = new int[count]; int kept = 0;
                for (int i = 0; i < count; i++)
                {
                    byte* e = data + (long)i * _fragSize;
                    short parent = *(short*)(e + _fParent);
                    bool hasParent = parent >= 0 && parent < i;
                    if (hasParent && map[parent] < 0) { map[i] = -1; depth[i] = int.MaxValue; continue; }   // parent dropped
                    int step = 1;
                    if (hasParent && _fDir >= 0)
                    {
                        var dc = *(Vector3*)(e + _fDir); var dp = *(Vector3*)(data + (long)map[parent] * _fragSize + _fDir);
                        float dot = dc.x * dp.x + dc.y * dp.y + dc.z * dp.z, ll = (float)Math.Sqrt((dc.x * dc.x + dc.y * dc.y + dc.z * dc.z) * (dp.x * dp.x + dp.y * dp.y + dp.z * dp.z));
                        if (ll > 1e-6f && dot / ll > 0.995f) step = 0;   // went straight through a module (crew, ammo...): not a bounce
                    }
                    depth[i] = hasParent ? depth[parent] + step : 0;
                    if (depth[i] > maxDepth)
                    {
                        if (!compact) { *(int*)(obj + _offFragCount) = kept; return; }
                        map[i] = -1; continue;
                    }
                    map[i] = kept;
                    if (kept != i)
                    {
                        byte* d = data + (long)kept * _fragSize;
                        for (int b = 0; b < _fragSize; b++) d[b] = e[b];
                    }
                    *(short*)(data + (long)kept * _fragSize + _fParent) = (short)(hasParent ? map[parent] : -1);
                    kept++;
                }
                if (kept < count) *(int*)(obj + _offFragCount) = kept;
            }
            catch (Exception e) { MPLog.Debug("LimitDepth: " + e.Message); }
        }

        static Matrix4x4 _lastSimToWorld = Matrix4x4.identity; static int _lastBase; static PenRecord _lastRec;

        /// <summary>APHE burst: the shell (and anything it would still do) ends at the burst point. Every shell-path
        /// segment that reaches past it, and every fragment born past it, gets zero mass/speed (= no damage) before the
        /// game applies the hits; the pen cam record is cut the same way.</summary>
        /// <summary>The shell body inside the tank goes straight (our model decides every plate): drop every part of the
        /// game's path that turned away (its own ricochets / deflections off plates and modules).</summary>
        static unsafe bool FirstArmour(IPenetrationSimulation simI, Vector3 from, Vector3 dir, out Vector3 at)
        {
            at = from;
            try
            {
                if (simI == null || !InitOffsets() || _fMat < 0) return false;
                byte* obj = (byte*)simI.Pointer;
                int count = *(int*)(obj + _offFragCount);
                IntPtr arr = *(IntPtr*)(obj + _offFragArr);
                if (arr == IntPtr.Zero || count <= 0) return false;
                long maxLen = *(long*)((byte*)arr + 3 * IntPtr.Size); if (count > maxLen) count = (int)maxLen;
                byte* data = (byte*)arr + 4 * IntPtr.Size;
                var m = _lastSimToWorld; float best = float.MaxValue;
                for (int i = 0; i < count; i++)
                {
                    byte* e = data + (long)i * _fragSize;
                    float tr = *(float*)(e + _fTravel);
                    if (!float.IsFinite(tr) || tr < 0.01f) continue;
                    var sp = m.MultiplyPoint3x4(*(Vector3*)(e + _fSpawn));
                    if (!HardAt(*(short*)(e + _fMat), sp + m.MultiplyVector(*(Vector3*)(e + _fDir)).normalized * (tr * 0.5f))) continue;
                    float along = Vector3.Dot(sp - from, dir);
                    if (along > 0.03f && along < best) { best = along; at = sp; }
                }
                return best < float.MaxValue;
            }
            catch { return false; }
        }

        static unsafe void StraightOnly(IPenetrationSimulation simI, Vector3 dir)
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
                var m = _lastSimToWorld; var drop = new bool[count]; int killed = 0;
                for (int i = 0; i < count; i++)
                {
                    byte* e = data + (long)i * _fragSize;
                    var dr = m.MultiplyVector(*(Vector3*)(e + _fDir)).normalized;
                    short par = *(short*)(e + _fParent);
                    bool parentDropped = par >= 0 && par < i && drop[par];
                    bool orig = (*(ushort*)(e + _fFlags) & 1) != 0;
                    if (parentDropped || !orig || Vector3.Dot(dr, dir) < 0.97f)   // also every fragment the game made (its own spall): our cones replace it
                    {
                        drop[i] = true; killed++;
                        int ri = _lastBase + Orig((IntPtr)obj, i);
                        if (_lastRec != null && ri >= 0 && ri < _lastRec.Frags.Count) { var f = _lastRec.Frags[ri]; f.Length = 0f; _lastRec.Frags[ri] = f; }
                    }
                }
                if (killed > 0) RemoveFrags(obj, data, count, drop);
            }
            catch (Exception e) { MPLog.Debug("StraightOnly: " + e.Message); }
        }

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
                int killed = 0; var drop = new bool[count];
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
                    killed++;
                    if (a0 > bAlong + 0.02f) drop[i] = true;                                   // entirely past the stop point
                    else { float keepLen = Mathf.Max(0f, (bAlong - a0) / Mathf.Max(0.05f, Vector3.Dot(dr, dir))); *(float*)(e + _fTravel) = keepLen; *(float*)(e + _fSpeed) = 0f; }   // the shell ends here
                    int ri = _lastBase + Orig(simPtr, i);
                    if (_lastRec != null && ri < _lastRec.Frags.Count)
                    {
                        var f = _lastRec.Frags[ri];
                        if (a0 > bAlong) f.Length = 0f; else f.Length = Mathf.Max(0f, (bAlong - a0) / Mathf.Max(0.05f, Vector3.Dot(dr, dir)));
                        _lastRec.Frags[ri] = f;
                    }
                }
                if (killed > 0) { RemoveFrags(obj, data, count, drop); MPLog.Info($"Shell path stopped, {killed} fragment segments past the stop point removed"); }
            }
            catch (Exception e) { MPLog.Debug("StopShellAt: " + e.Message); }
        }

        static readonly List<(Vector3 s, Vector3 d, float len, short mat, float speed)> _origSegs = new List<(Vector3, Vector3, float, short, float)>();
        static short _airMat = short.MinValue;
        // armour vs soft structure (crew, modules) - from the damage model's material table (rhaFactor)
        static float[] _rha;
        internal static bool IsHardMat(short m) => m >= 0 && IsHard(m);
        internal static void ReadMaterials(CompoundStructure cs)
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
        /// <summary>Armour, and not part of a wheel / the sprocket (steel wheels count as running gear, not armour).</summary>
        static bool HardAt(short mat, Vector3 world) => IsHard(mat) && !(_victim != null && PenCamView.Instance != null && PenCamView.Instance.InWheel(_victim, world));
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

        static Vector3 FirstContact()
        {
            foreach (var g in _origSegs) if (!IsAir(g.mat)) return g.s;
            return FirstImpact();
        }

        static Vector3 FirstImpact()
        {
            foreach (var g in _origSegs) if (!IsAir(g.mat) && !(_victim != null && PenCamView.Instance != null && PenCamView.Instance.InWheel(_victim, g.s + g.d * (g.len * 0.5f)))) return g.s;
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
        /// <summary>HEAT: the jet loses much of its penetration in every air gap between plates (spaced armour).
        /// The game's path is cut at each gap and the rest re-simulated with the reduced penetration.</summary>
        static void HeatAirGaps(CompoundStructure cs, IPenetrationSimulation main, PenetratorInfo pen, Transform root, int mainBase, Matrix4x4 mainM)
        {
            try
            {
                var sim = main; int bse = mainBase; var M = mainM; int from = 1;
                try {
                for (int gapN = 0; gapN < 3; gapN++)
                {
                    int e = -1;
                    for (int i = Math.Max(1, from); i < _origSegs.Count; i++)
                        if (IsHard(_origSegs[i - 1].mat) && IsAir(_origSegs[i].mat) && _origSegs[i].len > 0.05f
                            && _origSegs[i].len <= Shells.ShellConfig.HEATMaxGap.Value                        // spaced armour, not the crew compartment
                            && i + 1 < _origSegs.Count && IsHard(_origSegs[i + 1].mat)) { e = i; break; }      // a plate right behind the gap
                    if (e < 0) return;
                    var ex = _origSegs[e];
                    float gap = ex.len;
                    float keep = Mathf.Clamp(1f - Shells.ShellConfig.HEATSpacedLoss.Value - gap * Shells.ShellConfig.HEATGapLossPerMetre.Value, 0.03f, 1f);
                    float v = Mathf.Max(ex.speed, 1f) * Mathf.Pow(keep, 1f / 1.43f) / Mathf.Max(1f, gapN == 0 ? _boostV : 1f);   // penetration ~ v^1.43; armour-rule boost off after the first plate
                    _lastBase = bse; _lastRec = _pending; _lastSimToWorld = M;
                    StopShellAt(sim, ex.s + ex.d * 0.01f, ex.d, root);
                    _origSegs.RemoveRange(e, _origSegs.Count - e);
                    var info = new PenetratorInfo(pen.Mass, pen.Diameter, pen.Density, pen.MaxNormalizationAngle, pen.PenetratorConstant, ex.s + ex.d * 0.03f, ex.d * v);
                    int before = _origSegs.Count;
                    _continuing = true; _contSim = null;
                    IPenetrationSimulation ns;
                    try { ns = cs.Penetrate(ref info, PenetrationSimulationOptions.Default); }
                    finally { _continuing = false; }
                    MPLog.Info($"HEAT: {gap * 100f:F0} cm air gap -> {keep * 100f:F0}% of the jet's penetration left");
                    if (ns == null) return;
                    KillNativeSpall(ns, _contBase);
                    _heatApply.Add(ns);                                   // hits applied once the jet's end is known
                    sim = ns; bse = _contBase; M = _contM; from = before + 1;   // past the gap just handled (its plate is still the previous segment)
                    _heatSim = ns; _heatBase = _contBase; _heatM = _contM;
                }
                } finally { }
            }
            catch (Exception ex) { MPLog.Debug("HEAT gaps: " + ex.Message); _continuing = false; }
        }
        static readonly List<IPenetrationSimulation> _heatApply = new List<IPenetrationSimulation>();
        static IPenetrationSimulation _heatSim; static int _heatBase; static Matrix4x4 _heatM;
        /// <summary>HEAT: the jet is spent once it is through the armour into the tank: the shell itself damages nothing
        /// inside, only its spall cone does.</summary>
        static void HeatStopInside(Transform root)
        {
            try
            {
                for (int i = 1; i < _origSegs.Count; i++)
                {
                    if (!IsHard(_origSegs[i - 1].mat) || IsHard(_origSegs[i].mat)) continue;
                    bool spaced = IsAir(_origSegs[i].mat) && _origSegs[i].len <= Shells.ShellConfig.HEATMaxGap.Value && i + 1 < _origSegs.Count && IsHard(_origSegs[i + 1].mat);
                    if (spaced) continue;                                     // still in the armour array
                    var ex = _origSegs[i];
                    _lastBase = _heatBase; _lastRec = _pending; _lastSimToWorld = _heatM;
                    if (_heatSim != null) StopShellAt(_heatSim, ex.s + ex.d * 0.01f, ex.d, root);
                    if (i + 1 < _origSegs.Count) _origSegs.RemoveRange(i + 1, _origSegs.Count - i - 1);   // keep the exit for the jet's cone
                    MPLog.Info("HEAT: jet through the armour, shell stopped (only the jet's spall does damage inside)");
                    return;
                }
            }
            catch (Exception e) { MPLog.Debug("HEAT stop: " + e.Message); }
        }

        static bool HardAfter(int i) { for (int k = i + 1; k < _origSegs.Count; k++) if (IsHard(_origSegs[k].mat)) return true; return false; }

        static void ContinueShell(CompoundStructure cs, IPenetrationSimulation main, PenetratorInfo pen, Transform root, int mainBase, Matrix4x4 mainM)
        {
            try
            {
                int e = -1;
                for (int i = 1; i < _origSegs.Count; i++) if (IsHard(_origSegs[i - 1].mat) && IsAir(_origSegs[i].mat) && _origSegs[i].len > 0.02f) { e = i; break; }
                if (e < 0) return;
                var ex = _origSegs[e];
                var newDir = Vector3.Slerp(_origDir, ex.d, Mathf.Clamp01(Shells.Explosives.P.Normalization)).normalized;
                if ((Vector3.Angle(newDir, ex.d) < 0.5f && _boostV <= 1.001f) || ex.speed < 20f) return;
                // cancel the game's path after the plate
                _lastBase = mainBase; _lastRec = _pending; _lastSimToWorld = mainM;
                StopShellAt(main, ex.s + ex.d * 0.01f, ex.d, root);
                _origSegs.RemoveRange(e, _origSegs.Count - e);
                // re-simulate from the back face of the plate
                // the armour-rule boost only got it through that plate: the rest of the flight at its real speed
                var info = new PenetratorInfo(pen.Mass, pen.Diameter, pen.Density, pen.MaxNormalizationAngle * Shells.Explosives.P.Normalization, pen.PenetratorConstant, ex.s + newDir * 0.03f, newDir * (ex.speed / Mathf.Max(1f, _boostV)));
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
        /// <summary>Removes fragments (and everything spawned from them) from a simulation before its hits are applied.
        /// Zeroing speed/mass alone did not stop their damage.</summary>
        // simulations we compacted: current index -> index at capture time (the pen cam record is in capture order)
        static readonly Dictionary<IntPtr, int[]> _origIdx = new Dictionary<IntPtr, int[]>();
        static int Orig(IntPtr sim, int i) => _origIdx.TryGetValue(sim, out var m) && i < m.Length ? m[i] : i;
        static unsafe void RemoveFrags(byte* obj, byte* data, int count, bool[] drop)
        {
            var map = new int[count]; int kept = 0;
            var back = new int[count];
            for (int i = 0; i < count; i++)
            {
                byte* e = data + (long)i * _fragSize;
                short parent = *(short*)(e + _fParent);
                bool hasParent = parent >= 0 && parent < i;
                if (drop[i] || (hasParent && map[parent] < 0)) { map[i] = -1; continue; }
                map[i] = kept; back[kept] = Orig((IntPtr)obj, i);
                if (kept != i) { byte* d = data + (long)kept * _fragSize; for (int b = 0; b < _fragSize; b++) d[b] = e[b]; }
                *(short*)(data + (long)kept * _fragSize + _fParent) = (short)(hasParent ? map[parent] : -1);
                kept++;
            }
            if (kept < count) { *(int*)(obj + _offFragCount) = kept; Array.Resize(ref back, kept); _origIdx[(IntPtr)obj] = back; if (_origIdx.Count > 64) _origIdx.Clear(); }
        }

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
                int killed = 0; var drop = new bool[count];
                for (int i = 0; i < count; i++)
                {
                    byte* e = data + (long)i * _fragSize;
                    if ((*(ushort*)(e + _fFlags) & 1) != 0) continue;            // the shell itself
                    if (_shellDia > 0f && Math.Abs(*(float*)(e + _fDiameter) - _shellDia) < 0.15f * _shellDia)
                    {
                        var dv = *(Vector3*)(e + _fDir);
                        if (dv.sqrMagnitude > 1e-6f && Vector3.Dot(dv.normalized, _origDirSim) > 0.94f) continue;   // the shell carrying on after a thick plate
                    }
                    drop[i] = true; killed++;
                    { int ri = recBase + Orig((IntPtr)obj, i); if (_pending != null && ri < _pending.Frags.Count) { var f = _pending.Frags[ri]; f.Length = 0f; _pending.Frags[ri] = f; } }
                }
                if (killed > 0) RemoveFrags(obj, data, count, drop);
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

        internal static int FieldOff(IntPtr klass, string name, bool valueType)
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
        { MPLog.Stage = "recording fragments";
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
            var insideArr = new bool[count]; var penHard = new bool[count];   // burst fragments: through a plate yet?
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
                    if (!_entrySet && !_continuing && IsHard(mat)) { _entrySet = true; rec.Entry = root.InverseTransformPoint(start); if (float.IsFinite(travel) && travel > 0f && travel < 3f) rec.PlateLos = travel * 1000f; }   // where the shell meets the first plate
                    if (!_origFound) { _origFound = true; _origStart = start; _origDir = dir.normalized; _origDirSim = dr.sqrMagnitude > 1e-6f ? dr.normalized : dir.normalized; _origEnd = start + _origDir * (float.IsFinite(travel) ? Mathf.Clamp(travel, 0, 20f) : 0f); }
                    else { var e2 = start + dir.normalized * (float.IsFinite(travel) ? Mathf.Clamp(travel, 0, 20f) : 0f); if (Vector3.Dot(e2 - _origStart, _origDir) > Vector3.Dot(_origEnd - _origStart, _origDir)) _origEnd = e2; }
                    if (penetrated && HardAt(mat, start + dir.normalized * (Mathf.Min(travel, 3f) * 0.5f))) _simPen = true;   // through real armour (not wheels / sprocket)
                    float alongEnd = Vector3.Dot(start - _origStart, _origDir);
                    if (alongEnd >= _origEndAlong) { _origEndAlong = alongEnd; _origEndSpeed = speed; }
                }
                else if (!blastFrag && _origFound && _shellDia > 0f && Math.Abs(dia - _shellDia) < 0.15f * _shellDia && Vector3.Dot(dir.normalized, _origDir) > 0.94f)
                {
                    // the shell itself carrying on after a plate as a new fragment (happens behind thick plates):
                    // part of the shell path, so later plates of spaced armour still get their spall cone
                    _origSegs.Add((start, dir.normalized, float.IsFinite(travel) ? Mathf.Clamp(travel, 0f, 20f) : 0f, mat, speed));
                    if (penetrated && HardAt(mat, start + dir.normalized * (Mathf.Min(travel, 3f) * 0.5f))) _simPen = true;   // through real armour (not wheels / sprocket)
                }
                else if (!blastFrag && (!_hasSpall || Vector3.Dot(start - _origStart, _origDir) < Vector3.Dot(_spallStart - _origStart, _origDir))) { _hasSpall = true; _spallStart = start; }
                if (original && penetrated && HardAt(mat, start + dir.normalized * (Mathf.Min(travel, 3f) * 0.5f))) pen = true;
                if (original && deflected) bounce = true;
                                penHard[i] = penetrated && IsHard(mat);
                insideArr[i] = Shells.Explosives.Rebounding || (parent >= 0 && parent < i && (insideArr[parent] || penHard[parent]));
                byte flags = (byte)((original && !blastFrag ? 1 : 0) | (blastFrag && insideArr[i] ? 2 : 0) | (embedded ? 4 : 0) | (deflected ? 8 : 0) | (penetrated ? 16 : 0) | (blastFrag ? (Shells.Explosives.Mode == 2 ? 128 : 64) : 0));
                if (blastFrag) original = false;
                if (blastFrag && float.IsFinite(travel) && travel > 0.01f)
                { _trackTests++; if (TrackDamage(_victim, start, start + dir.normalized * Mathf.Min(travel, 20f), false)) _trackHits++; }
                rec.Frags.Add(new PenRecord.Frag
                {
                    Start = root.InverseTransformPoint(start), Dir = root.InverseTransformDirection(dir.normalized),
                    Length = float.IsFinite(travel) ? Mathf.Clamp(travel, 0, 20f) : 0f, Speed = speed, Diameter = dia,
                    Parent = (short)(parent < 0 ? -1 : parent + baseIndex), Flags = flags, Original = original,
                });
            }
            if (!Shells.Explosives.InExplosion && !_lockResult) { _origPen |= pen; _bounce |= bounce; }
            if (Plugin.VerboseLogging.Value && rec.Frags.Count > baseIndex)
                MPLog.Info($"Pen frag0 local start {rec.Frags[baseIndex].Start} dir {rec.Frags[baseIndex].Dir} len {rec.Frags[baseIndex].Length} (vehicle dist {(m.MultiplyPoint3x4(*(Vector3*)(data + _fSpawn)) - vehiclePos).magnitude:F1} m)");
        }

        /// <summary>Called every frame: a hit is sent PenCam.SendDelaySeconds after it was worked out (late damage gets in).</summary>
        
        static int _pendingFrame; static float _pendingSinceReal = -1f;
        public static void Update(BattleManager bm, Session s)
        { MPLog.Stage = "pen cam capture update";
            if (_pending != null && _pendingSinceReal < 0f) _pendingSinceReal = Time.realtimeSinceStartup;   // counted from the frame after the hit was worked out
            if (bm.Local != null && bm.Local.GO != null && Time.frameCount % 30 == 0)
            {
                // do the wheel probing up front (not on the first hit: that caused a freeze)
                try
                {
                    var cs0 = bm.Local.GO.GetComponentInChildren<CompoundStructure>();
                    if (cs0 != null && !_softWheels.Contains(cs0.Pointer) && PenCamView.Instance != null && PenCamView.Instance.WheelProbes(bm.Local).Count > 0) { SoftenWheels(cs0, bm.Local); _quietUntilFrame = Time.frameCount + 3; }
                }
                catch { }
            }
            if (_pending == null) return;
            if (Time.realtimeSinceStartup - _pendingSinceReal < Mathf.Clamp(Plugin.PenCamSendDelay.Value, 0f, 2f) || Time.frameCount <= _pendingFrame) return;
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
                if (local != null)
                {
                    var crewP = new HashSet<IntPtr>(); foreach (var cp in local.CrewPools) if (cp != null) crewP.Add(cp.Pointer);
                    var hitNow = new HashSet<int>(); foreach (var d in rec.Damage) hitNow.Add(d.Pool);
                    for (int i = 0; i < local.Pools.Count; i++)
                    {
                        var pl = local.Pools[i]; if (pl == null || hitNow.Contains(i) || crewP.Contains(pl.Pointer)) continue;
                        try { if (pl.Current <= 0.01f * Mathf.Max(pl.max, 0.001f)) rec.DeadMods.Add((ushort)i); } catch { }
                    }
                }
                rec.Result = rec.Damage.Count > 0 ? (byte)0 : _bounce ? (byte)2 : _origPen ? (byte)3 : (byte)1;
                if (_heOutside) rec.Result = rec.Damage.Count > 0 ? (byte)4 : (byte)1;
                _heOutside = false;
                s.Broadcast(rec.Serialize());
                Sent++;
                LastSent = rec; LastSentAt = Time.time;
                if (bm.LocalDestroyedAt > 0f && Time.time - bm.LocalDestroyedAt < 2f) bm.ShowKillCam(rec);
                MPLog.Info($"Pen cam sent to player {rec.Shooter}: {PenRecord.ResultText(rec.Result)}, {rec.Frags.Count} fragments, {rec.Damage.Count} modules damaged{(_trackTests > 0 ? $", fragments vs track hitbox: {_trackHits} of {_trackTests} hit" : "")}");
                _trackTests = _trackHits = 0;
            }
            catch (Exception e) { MPLog.Warn("PenCam send: " + e.Message); }
        }

        /// <summary>End points (world space) of fragments of this simulation that got INSIDE (through a plate) and
        /// then stopped in something: where an HE fragment would bounce off.</summary>
        public static unsafe List<(Vector3 end, Vector3 dir, float speed)> StoppedInside(IPenetrationSimulation simI, bool includeRoots = false)
        {
            var res = new List<(Vector3, Vector3, float)>();
            try
            {
                if (simI == null || !InitOffsets()) return res;
                byte* obj = (byte*)simI.Pointer;
                int count = *(int*)(obj + _offFragCount);
                IntPtr arr = *(IntPtr*)(obj + _offFragArr);
                if (arr == IntPtr.Zero || count <= 0) return res;
                long maxLen = *(long*)((byte*)arr + 3 * IntPtr.Size); if (count > maxLen) count = (int)maxLen;
                byte* data = (byte*)arr + 4 * IntPtr.Size;
                var m = _lastSimToWorld;
                for (int i = 0; i < count; i++)
                {
                    byte* e = data + (long)i * _fragSize;
                    short parent = *(short*)(e + _fParent);
                    if (parent < 0 && !includeRoots) continue;                  // still the outside part of the flight
                    int ff = *(ushort*)(e + _fFlags);
                    if ((ff & 64) != 0) continue;                               // went through: not stopped here
                    float travel = *(float*)(e + _fTravel), speed = *(float*)(e + _fSpeed);
                    if (!float.IsFinite(travel) || travel <= 0.02f || travel > 20f) continue;
                    short mat = _fMat >= 0 ? *(short*)(e + _fMat) : (short)-1;
                    if (!IsHard(mat)) continue;                                 // stopped in a module / crew: absorbed, no bounce
                    Vector3 sp = *(Vector3*)(e + _fSpawn), dr = *(Vector3*)(e + _fDir);
                    if (dr.sqrMagnitude < 1e-6f) continue;
                    var start = m.MultiplyPoint3x4(sp); var dir = m.MultiplyVector(dr).normalized;
                    // the segment starts where it met the plate: bounce from just in FRONT of that surface (in air),
                    // a fragment born inside the plate would ignore all armour
                    res.Add((start - dir * 0.02f, dir, speed));
                }
            }
            catch (Exception ex) { MPLog.Debug("StoppedInside: " + ex.Message); }
            return res;
        }

        public static PenRecord LastSent; public static float LastSentAt = -10f;
        public static void Reset() { _pending = null; _recentShots.Clear(); _lastSim = IntPtr.Zero; }
    }
}
