using System;
using System.Collections.Generic;
using Sprocket.DamageModelling;
using UnityEngine;

namespace SprocketMP
{
    /// <summary>
    /// Fragments (spall cones, shell bursts, the shell's own body inside the tank) traced through the game's own armour
    /// model instead of one game penetration simulation each: armour plates are closed solids (front face carries the
    /// raw thickness, back face -1) and modules (crew, ammo, fuel, engine...) are surfaces with no thickness. A fragment
    /// goes through a plate if its penetration beats the steel along its path, damages every module it enters (through
    /// the game's own damage applier, so the usual per-module damage rules and our damage scales apply), and loses
    /// speed in it.
    /// </summary>
    public static partial class PenCapture
    {
        public static bool OwnFragmentsAvailable(VehicleRig rig) =>
            Shells.ShellConfig.OwnFragments.Value && rig != null && PenCamView.Instance != null && PenCamView.Instance.HasDm(rig);

        static IntPtr _idsFor; static int[] _objIds;
        static int ObjectId(CompoundStructure cs, short objIdx)
        {
            try
            {
                if (_idsFor != cs.Pointer || _objIds == null)
                {
                    var arr = cs.objects; int n = arr != null ? arr.Length : 0;
                    _objIds = new int[n]; for (int i = 0; i < n; i++) _objIds[i] = arr[i].ID;
                    _idsFor = cs.Pointer;
                }
                return objIdx >= 0 && objIdx < _objIds.Length ? _objIds[objIdx] : -1;
            }
            catch { return -1; }
        }

        static bool _dmgFallbackLogged;
        /// <summary>The game's damage for a fragment of this mass / speed / diameter (its own formula), else kinetic energy based.</summary>
        static float FragmentDamage(float mass, float speed, float dia)
        {
            try
            {
                var f = new Fragment(); f.mass = mass; f.speed = speed; f.diameter = dia;
                float d = f.Damage;
                if (float.IsFinite(d) && d > 0f) return d;
            }
            catch (Exception e) { if (!_dmgFallbackLogged) { _dmgFallbackLogged = true; MPLog.Warn("fragment damage formula: " + e.Message); } }
            return 0.5f * mass * speed * speed / 2000f;
        }

        static float pPen(float dia, float mass, float speed, ushort pc) { try { return PenetrationUtils.ComputePenetration(dia * 1000f, mass, speed, pc); } catch { return 0f; } }

        public struct FragStats { public int Fired, HitModules, Penetrated, Stopped, Bounced, Hidden; }
        public static int DebugFrags;   // log the next N fragments' path decisions
        public static FragStats Stats;

        /// <summary>One fragment. flags: record flags for the pen cam (64 burst, 128 spall cone, 2 inside). Returns where it ended.</summary>
        public static Vector3 Fire(CompoundStructure cs, VehicleRig rig, Vector3 p, Vector3 d, float speed, float mass, float dia, ushort penConst,
                                   int bounces, float moduleKeep, bool applyDamage, byte flags, System.Random rnd, float maxLen = 15f, bool record = true)
        {
            var view = PenCamView.Instance;
            d = d.normalized; Stats.Fired++;
            var hitObjs = new HashSet<int>();
            var applier = cs.DamageApplier;
            float left = maxLen;
            for (int leg = 0; leg < 12 && left > 0.01f && speed > 30f; leg++)
            {
                var hits = view.TraceDM(rig, p, d, Mathf.Min(15f, left));
                float travelled = Mathf.Min(15f, left); bool stop = false, bounce = false; Vector3 bnorm = Vector3.zero; byte segFlags = flags;
                float tCur = 0f, lastHit = -1f;
                var dbg = DebugFrags > 0 ? new System.Text.StringBuilder($"Fragment {Stats.Fired} leg {leg}: {pPen(dia, mass, speed, penConst):F0} mm pen, {speed:F0} m/s") : null;
                for (int i = 0; i < hits.Count; i++)
                {
                    var h = hits[i];
                    if (h.T < 0.0005f) continue;
                    dbg?.Append($" | {h.T * 100f:F0}cm {h.Raw:F0}mm m{h.Mat} o{h.Obj}");
                    if (SteelFace(h))
                    {
                        // armour (or a hard part): the steel along the path is the distance to its other face
                        int pj = SolidPartner(hits, i);
                        if (pj < 0 && h.Raw < -0.5f) { dbg?.Append(" far face, skipped"); continue; }   // the far face of a plate it is already in
                        float tOut = pj >= 0 ? hits[pj].T : h.T + Mathf.Max(0.002f, Math.Abs(h.Raw) * 0.001f / Mathf.Max(0.05f, h.Cos));
                        if (Math.Abs(h.Raw) <= 0.5f && h.Obj >= 0 && applyDamage && applier != null)
                        {
                            // a hard module (engine block, gun...): it is damaged too
                            int hid = ObjectId(cs, h.Obj);
                            if (hid >= 0 && hitObjs.Add(hid)) { try { applier.HealthDelta(hid, -FragmentDamage(mass, speed, dia), new DamageModelDamageModifiers(1f, 0f, 0f, 0f)); Stats.HitModules++; } catch { } }
                        }
                        float rha = _rha != null && h.Mat >= 0 && h.Mat < _rha.Length ? _rha[h.Mat] : 1f;
                        float steel = (tOut - h.T) * 1000f * rha;
                        float pen = 0f; try { pen = PenetrationUtils.ComputePenetration(dia * 1000f, mass, speed, penConst); } catch { }
                        dbg?.Append($" steel {steel:F0} (to {tOut * 100f:F0}cm)");
                        lastHit = tOut;
                        if (pen > steel && steel < 2000f)
                        {
                            speed *= Mathf.Sqrt(Mathf.Clamp01(1f - steel / pen));
                            segFlags |= 16; Stats.Penetrated++;
                            if ((flags & 64) != 0) flags |= 2;                                  // a burst fragment now inside the armour
                            if (pj > i) i = pj;                                                  // its other face is part of it
                            tCur = tOut;
                            if (speed < 30f) { travelled = tOut; stop = true; break; }
                            continue;
                        }
                        travelled = h.T; Stats.Stopped++;
                        if (bounces > 0) { bounce = true; bnorm = h.Normal; }
                        else { stop = true; segFlags |= 4; }
                        break;
                    }
                    // a module surface (no thickness): damage it once per fragment, then on through it, slower
                    int id = h.Obj >= 0 ? ObjectId(cs, h.Obj) : -1;
                    if (id < 0 || !hitObjs.Add(id)) continue;
                    lastHit = h.T; dbg?.Append(" module hit");
                    if (applyDamage && applier != null)
                    {
                        float dmg = FragmentDamage(mass, speed, dia);
                        try { applier.HealthDelta(id, -dmg, new DamageModelDamageModifiers(1f, 0f, 0f, 0f)); Stats.HitModules++; } catch (Exception e) { MPLog.Debug("fragment damage: " + e.Message); }
                    }
                    speed *= Mathf.Clamp01(moduleKeep);
                    // outside the armour (running gear, external tanks): its material along the path also counts, like in the
                    // game - a road wheel is soft, but wide. Inside, modules let spall through (SpallThroughModuleSpeed).
                    if ((flags & 64) != 0 && (flags & 2) == 0)
                    {
                        float tO = -1f;
                        for (int j = i + 1; j < hits.Count; j++) { var b2 = hits[j]; if (b2.T > h.T + 3f) break; if (b2.Shape == h.Shape && b2.Obj == h.Obj && Math.Abs(b2.Raw) <= 0.5f && b2.T > h.T + 0.0003f) { tO = b2.T; break; } }
                        if (tO > 0f)
                        {
                            float ms = (tO - h.T) * 1000f * Rha(h.Mat);
                            float mp = pPen(dia, mass, speed, penConst);
                            dbg?.Append($" ({ms:F0} mm of it)");
                            if (mp <= ms) { travelled = h.T + (tO - h.T) * 0.5f; stop = true; segFlags |= 4; dbg?.Append(" stuck in it"); break; }
                            speed *= Mathf.Sqrt(Mathf.Clamp01(1f - ms / Mathf.Max(0.01f, mp)));
                        }
                    }
                    if (speed < 60f) { travelled = h.T; stop = true; segFlags |= 4; break; }
                }
                if (dbg != null) { DebugFrags--; MPLog.Info(dbg + (stop ? " -> stopped" : bounce ? " -> bounces" : " -> flies on")); }
                if (stop || bounce) lastHit = travelled;
                var end = p + d * travelled;
                // the pen cam leaves out fragments that hit nothing at all, and cuts the flight into nowhere after the last hit
                float shown = lastHit < 0f ? 0f : stop || bounce ? travelled : Mathf.Min(travelled, lastHit + 0.3f);
                if (shown <= 0f) Stats.Hidden++;
                if (record && shown > 0f && _pending != null && rig.GO != null && _pending.Frags.Count < PenRecord.MaxFrags)
                {
                    var root = rig.GO.transform;
                    _pending.Frags.Add(new PenRecord.Frag { Start = root.InverseTransformPoint(p), Dir = root.InverseTransformDirection(d), Length = shown, Speed = speed, Diameter = dia, Parent = -1, Flags = segFlags, Original = false });
                }
                if (record && (flags & (64 | 128)) != 0) { _trackTests++; if (TrackDamage(rig, p, end, false)) _trackHits++; }
                left -= travelled;
                if (stop || !bounce) break;
                // bounce off the plate it couldn't get through: back off the surface, scattered, slower
                bounces--; Stats.Bounced++;
                var rd = Vector3.Reflect(d, bnorm);
                var r3 = new Vector3((float)rnd.NextDouble() * 2f - 1f, (float)rnd.NextDouble() * 2f - 1f, (float)rnd.NextDouble() * 2f - 1f);
                d = (rd + r3 * 0.6f).normalized; if (Vector3.Dot(d, bnorm) < 0.05f) d = (d + bnorm * 0.5f).normalized;
                speed = Mathf.Max(speed * 0.55f, 120f);
                p = end + bnorm * 0.01f;
                flags |= 8;
            }
            return p;
        }
    }
}
