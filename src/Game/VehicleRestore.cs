using System;
using System.Collections.Generic;
using Sprocket;
using Sprocket.Vehicles;
using Sprocket.Vehicles.CrewSystems;
using UnityEngine;

namespace SprocketMP
{
    /// <summary>
    /// Brings a vehicle's damage state back to new. Used for field repairs (includeCrew = false) and for reviving a
    /// puppet whose owner respawned while our scene kept the old wreck (includeCrew = true).
    /// Healing a pool alone is not enough: components cache their own state (broken tracks, fires, ammo flameouts,
    /// blown-off parts), so each of those is reset explicitly.
    /// </summary>
    public static class VehicleRestore
    {
        /// <summary>The bottom-left module status display only redraws on a health-change event; poke it.</summary>
        public static void RefreshDamageHud()
        {
            try
            {
                var displays = UnityEngine.Object.FindObjectsOfType<Sprocket.Vehicles.UI.VehicleComponentHealthDisplayer>();
                if (displays == null) return;
                foreach (var d in displays)
                {
                    try { d?.OnComponentHealthChange(null, Il2CppSystem.EventArgs.Empty); } catch (Exception e) { MPLog.Warn("hud refresh: " + e.Message); }
                }
            }
            catch (Exception e) { MPLog.Warn("hud refresh: " + e.Message); }
        }

        /// <summary>
        /// Put blown-off parts (turret after an ammo detonation) back where they were. Decoupled parts get their own
        /// rigidbody and are re-parented to the scene root, so the game's own decoupled list is not enough to find them:
        /// we use the snapshot taken while the vehicle was intact.
        /// </summary>
        static int RecoupleParts(VehicleRig rig)
        {
            int fixedCount = 0;
            // 1. the game's own path
            try
            {
                var dec = rig.PartRegister?.decoupledParts;
                if (dec != null)
                {
                    for (int i = dec.Count - 1; i >= 0; i--)
                    {
                        try { var part = dec[i]; if (part != null) { part.Recouple(); part.RecoupleTransform(); } } catch (Exception e) { MPLog.Warn("recouple: " + e.Message); }
                    }
                    dec.Clear();
                }
            }
            catch (Exception e) { MPLog.Warn("decoupled list: " + e.Message); }

            // 2. snapshot path: force every part back to its original parent, local pose and state
            foreach (var snap in rig.Parts)
            {
                try
                {
                    var d = snap.Part;
                    if (d == null || d.WasCollected) { MPLog.Warn($"part '{snap.Name}' on {rig.RawName} no longer exists"); continue; }
                    if (snap.Parent == null || snap.Parent.WasCollected) { MPLog.Warn($"part '{snap.Name}': original parent gone"); continue; }
                    var t = d.transform;
                    bool wasDecoupled = false; try { wasDecoupled = d.decoupled; } catch { }
                    bool moved = t.parent == null || t.parent.Pointer != snap.Parent.Pointer;
                    float poseErr = (t.localPosition - snap.LocalPos).magnitude + Quaternion.Angle(t.localRotation, snap.LocalRot) * 0.01f;
                    bool hasBody = false; try { hasBody = d.partRigidbody != null; } catch { }
                    MPLog.Info($"part '{snap.Name}' on {rig.RawName}: decoupled={wasDecoupled} moved={moved} poseErr={poseErr:F3} active={d.gameObject.activeSelf}/{d.gameObject.activeInHierarchy} body={hasBody}(had {snap.HadBody}) parent={(t.parent != null ? t.parent.name : "<root>")}");
                    if (wasDecoupled) { try { d.Recouple(); } catch { } }
                    try { d.decoupled = false; d.decoupleRequested = false; d.distanceCulled = false; } catch { }
                    try
                    {
                        var rb = d.partRigidbody;
                        if (rb != null && !rb.WasCollected)
                        {
                            if (!rb.isKinematic) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                            if (snap.HadBody) rb.isKinematic = snap.BodyKinematic;
                            else { rb.isKinematic = true; UnityEngine.Object.Destroy(rb); d.partRigidbody = null; }
                        }
                    }
                    catch { }
                    if (t.parent == null || t.parent.Pointer != snap.Parent.Pointer) t.SetParent(snap.Parent, false);
                    t.localPosition = snap.LocalPos; t.localRotation = snap.LocalRot;
                    if (!d.gameObject.activeSelf) d.gameObject.SetActive(true);
                    if (wasDecoupled || moved || poseErr > 0.01f) fixedCount++;
                }
                catch (Exception e) { MPLog.Warn($"recouple '{snap.Name}': {e.Message}"); }
            }

            // 3. anything switched off since the vehicle was intact (hidden turret meshes, culled parts)
            int reactivated = 0, rendered = 0;
            foreach (var go in rig.ActiveObjects) { try { if (go != null && !go.WasCollected && !go.activeSelf) { go.SetActive(true); reactivated++; } } catch { } }
            foreach (var r in rig.EnabledRenderers) { try { if (r != null && !r.WasCollected && !r.enabled) { r.enabled = true; rendered++; } } catch { } }
            if (reactivated + rendered > 0) MPLog.Info($"{rig.RawName}: re-enabled {reactivated} objects and {rendered} renderers");
            fixedCount += reactivated + rendered;
            return fixedCount;
        }

        static bool IsBurnEffect(string name)
        {
            var l = name.ToLowerInvariant();
            return l.Contains("fire") || l.Contains("flame") || l.Contains("burn") || l.Contains("cookoff") || l.Contains("smoke") || l.Contains("explos");
        }

        /// <summary>
        /// Effects spawned by the cook-off event itself (turret pop explosion, the long flame out of the empty turret
        /// ring). They are fresh clones per event, so switching them off is safe; the per-hatch "Flameout Effect"
        /// objects are reused by the game and are only stopped.
        /// </summary>
        static bool IsOneShotEffect(string name)
        {
            var l = name.ToLowerInvariant();
            return l.Contains("blown") || l.Contains("turretpop") || l.Contains("explosion");
        }

        /// <summary>Fire / cook-off / smoke effects left running on a wreck (they don't stop when the fire state is cleared).</summary>
        static int StopBurnEffects(VehicleRig rig)
        {
            int stopped = 0;
            var names = new List<string>();
            try
            {
                foreach (var v in rig.GO.GetComponentsInChildren<UnityEngine.VFX.VisualEffect>(true))
                {
                    if (v == null || !IsBurnEffect(v.gameObject.name)) continue;
                    try { v.Stop(); v.Reinit(); } catch { }
                    if (IsOneShotEffect(v.gameObject.name)) { try { v.gameObject.SetActive(false); } catch { } }
                    stopped++; names.Add(v.gameObject.name);
                }
                foreach (var ps in rig.GO.GetComponentsInChildren<ParticleSystem>(true))
                {
                    if (ps == null || !IsBurnEffect(ps.gameObject.name)) continue;
                    try { ps.Stop(true); ps.Clear(true); } catch { }
                    if (IsOneShotEffect(ps.gameObject.name)) { try { ps.gameObject.SetActive(false); } catch { } }
                    stopped++; names.Add(ps.gameObject.name);
                }
            }
            catch (Exception e) { MPLog.Warn("StopBurnEffects: " + e.Message); }
            if (stopped > 0) MPLog.Info($"{rig.RawName}: stopped {stopped} fire/smoke effects ({string.Join(", ", names)})");
            return stopped;
        }

        /// <summary>
        /// Ammo cook-off vents flames through "exits" (turret ring, hatches). Each exit keeps its own long-term fire
        /// effect and a 'Blown' flag (open hatch), independent of the fire/flameout state - clear both.
        /// </summary>
        static int ResetCookoffExits(VehicleRig rig)
        {
            int n = 0;
            try
            {
                try { rig.FlameoutModule?.receiver?.SetFlameoutVisible(false); } catch { }
                var exits = rig.FlameoutModule?.exits;
                if (exits != null)
                {
                    for (int i = 0; i < exits.Length; i++)
                    {
                        var ex = exits[i]; if (ex == null) continue;
                        try
                        {
                            var eff = ex.CookoffEffect;
                            if (eff != null)
                            {
                                try { eff.MarkBlownOff(); } catch { }
                                StopFireEffect(eff.TryCast<Sprocket.Vehicles.Flameouts.FlameoutAssemblyStage.AmmoCookoffLongtermEffectWrapper>()?.Effect);
                                StopFireEffect(eff.TryCast<Sprocket.Vehicles.Flameouts.FlameoutAssemblyStage.AmmoCookoffEffectWrapper>()?.Effect);
                                ex.CookoffEffect = null;
                                n++;
                            }
                            ex.AmmoCookoffFlags = Sprocket.Vehicles.Events.Flameouts.AmmoCookoffFlags.None;
                        }
                        catch (Exception e) { MPLog.Warn("cookoff exit: " + e.Message); }
                    }
                }
                // hatches blown open by the cook-off: close them again
                foreach (var hs in rig.Hatches)
                {
                    try
                    {
                        var h = hs.Hatch; if (h == null || h.WasCollected) continue;
                        h.cookoffFlags = Sprocket.Vehicles.Events.Flameouts.AmmoCookoffFlags.None;
                        var m = h.DoorModel;
                        if (m != null) { m.SetVisible(true); m.ModelLocalPosition = hs.LocalPos; m.ModelLocalRotation = hs.LocalRot; }
                    }
                    catch (Exception e) { MPLog.Warn("hatch reset: " + e.Message); }
                }
                foreach (var tr in rig.GO.GetComponentsInChildren<Sprocket.Vehicles.Turrets.TurretRing>(true))
                {
                    try { if (tr != null) tr.cookoffFlags = Sprocket.Vehicles.Events.Flameouts.AmmoCookoffFlags.None; } catch { }
                }
            }
            catch (Exception e) { MPLog.Warn("ResetCookoffExits: " + e.Message); }
            if (n > 0) MPLog.Info($"{rig.RawName}: cleared {n} cook-off vent effects, reset {rig.Hatches.Count} hatches");
            return n;
        }

        static void StopFireEffect(Sprocket.Vehicles.AmmunitionStorage.FireEffect fx)
        {
            if (fx == null || fx.WasCollected) return;
            try { var v = fx.visualEffect; if (v != null) { v.Stop(); v.Reinit(false); } } catch { }
            try { fx.audioSource?.Stop(); } catch { }
            try { var l = fx.fireLight; if (l != null) l.enabled = false; } catch { }
        }

        public static int Restore(VehicleRig rig, bool includeCrew)
        {
            if (rig == null || !rig.Alive) return 0;
            if (rig.Pools.Count == 0 || rig.FireModule == null) { rig.CrewResolved = false; rig.ResolveCrew(); }
            int n = 0;
            Patches.AllowDamage = true;
            try
            {
                // 1. health pools (set directly: a delta on a dead pool is not always accepted)
                foreach (var p in rig.Pools)
                {
                    if (p == null) continue;
                    if (!includeCrew && rig.CrewPoolPtrs.Contains(p.Pointer)) continue;
                    try
                    {
                        if (p.current < p.max - 0.001f)
                        {
                            p.current = p.max;
                            p.MarkHealthModified();
                            p.NotifyHealthChanges();
                            n++;
                        }
                    }
                    catch (Exception e) { MPLog.Warn("restore pool: " + e.Message); }
                }
                try { rig.HealthReg?.NotifyHealthChanges(); } catch { }

                // 2. component-level health fractions (cannons, breeches, engines, transmissions, motors, fuel tanks...)
                try
                {
                    var comps = rig.GO.GetComponentsInChildren<VehicleComponent>(true);
                    foreach (var c in comps)
                    {
                        if (c == null) continue;
                        if (!includeCrew && c.TryCast<CrewSeat>() != null) continue;
                        try { if (c.HealthFraction < 0.999f) c.HealthFraction = 1f; } catch { }
                    }
                }
                catch (Exception e) { MPLog.Warn("restore components: " + e.Message); }

                // 3. tracks keep a separate 'broken' latch in the track simulation
                try
                {
                    var tracks = rig.GO.GetComponentsInChildren<Sprocket.Vehicles.Tracks.TrackAssembly>(true);
                    foreach (var t in tracks)
                    {
                        if (t == null) continue;
                        try { t.Broken = false; } catch { }
                        try
                        {
                            var tb = t.Controller?.TryCast<Sprocket.ContinuousTracks.TrackBehaviour>();
                            if (tb != null)
                            {
                                if (tb.Broken || tb.broken) { tb.Repair(); n++; }
                                tb.broken = false; tb.BeltMovementEnabled = true; tb.TransformRotationsEnabled = true;
                                // broken links are scattered by the belt simulation; snap them back onto the wheels
                                try { tb.segmentController?.ResetPositions(); } catch (Exception e) { MPLog.Warn("belt reset (sim): " + e.Message); }
                            }
                        }
                        catch (Exception e) { MPLog.Warn("track repair: " + e.Message); }
                        try
                        {
                            var belt = t.Belt;
                            if (belt != null)
                            {
                                MPLog.Info($"belt {t.name}: controller={VehicleRig.TypeName(belt.beltController)} visible={belt.segmentsVisible} segments={belt.SegmentCount}");
                                try { belt.beltController?.ResetPositions(); } catch (Exception e) { MPLog.Warn("belt reset: " + e.Message); }
                                belt.segmentsVisible = true;
                                belt.UpdateVisibility();
                            }
                        }
                        catch (Exception e) { MPLog.Warn("belt visuals: " + e.Message); }
                    }
                }
                catch (Exception e) { MPLog.Warn("tracks: " + e.Message); }

                // 4. fire and ammunition flameout
                try { rig.FireModule?.fireControl?.Extinguish(); } catch { }
                try
                {
                    var fm = rig.FlameoutModule;
                    if (fm != null) { fm.sim = new Sprocket.Vehicles.AmmunitionStorage.FlameoutSimulation(0f, 0f); fm.flameoutVisible = false; }
                }
                catch (Exception e) { MPLog.Warn("flameout reset: " + e.Message); }
                try { rig.VB.Flags &= ~VehicleFlags.OnFire; } catch { }

                // 5. field repair: a destroyed engine / transmission clears the vehicle's "can move" flags and stalls the
                //    engine; health alone doesn't bring them back (the tank then can't start or drive)
                if (!includeCrew)
                {
                    try { rig.VB.Flags |= VehicleFlags.Mobile | VehicleFlags.SelfPropelled | VehicleFlags.Armed; } catch (Exception e) { MPLog.Warn("repair flags: " + e.Message); }
                    try { EngineRevive.Revive(rig, true); } catch (Exception e) { MPLog.Warn("repair engine revive: " + e.Message); }
                    try { rig.Driver?.StartupInstant(); } catch (Exception e) { MPLog.Warn("repair engine start: " + e.Message); }
                    foreach (var w in rig.Weapons) { try { if (w != null) w.Functioning = true; } catch { } }
                }

                // 6. full revive only: blown-off parts, crew, vehicle flags, weapons
                if (includeCrew)
                {
                    n += RecoupleParts(rig);
                    n += StopBurnEffects(rig);
                    n += ResetCookoffExits(rig);
                    try { rig.Driver?.StartupInstant(); rig.Driver?.ResetControls(); } catch { }
                    try { rig.VB.Flags |= VehicleFlags.Enabled | VehicleFlags.Mobile | VehicleFlags.Armed | VehicleFlags.SelfPropelled; } catch { }
                    foreach (var w in rig.Weapons) { try { if (w != null) { w.Functioning = true; w.NeedsReload = false; } } catch { } }
                }
            }
            finally { Patches.AllowDamage = false; }
            RefreshDamageHud();
            MPLog.Info($"Restored {rig.RawName} (crew={includeCrew}): {n} items");
            return n;
        }
    }
}
