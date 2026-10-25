using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sprocket;
using Sprocket.ArtificialIntelligence;
using Sprocket.DamageModelling;
using Sprocket.Vehicles;
using Sprocket.WeaponFramework;
using SprocketMP.Net;
using UnityEngine;

namespace SprocketMP
{
    /// <summary>
    /// Harmony hooks. Each patch is applied individually so one failure (e.g. an IL2CPP signature Harmony can't handle)
    /// doesn't take the others down; the diagnostics dump shows which ones are live.
    /// </summary>
    public static class Patches
    {
        public static bool LaunchPatched, LaunchExplicitPatched, WeaponFirePatched;
        public static int HealthPatchCount, AimPatchCount, EndScreenPatchCount;
        public static IProjectileFactory CapturedFactory;

        // ---- damage immunity bookkeeping (pointers of puppet-owned health objects)
        static readonly HashSet<IntPtr> PuppetPools = new HashSet<IntPtr>();
        static readonly HashSet<IntPtr> PuppetRegisters = new HashSet<IntPtr>();
        static readonly HashSet<IntPtr> PuppetVehicleRegisters = new HashSet<IntPtr>();
        static readonly HashSet<IntPtr> RegisteredRigs = new HashSet<IntPtr>();
        static readonly HashSet<IntPtr> PuppetAimables = new HashSet<IntPtr>();
        public static bool AllowDamage;
        /// <summary>Replicated projectiles launched on this machine: projectile id -> (shooter player id, time).</summary>
        public static readonly Dictionary<int, (byte shooter, float time)> RemoteProjectiles = new Dictionary<int, (byte, float)>();
        public static int BlockedDamageEvents;

        // ---- remote shot state
        static FireMsg _pendingShot; static ProjectileTypeID _pendingType; static bool _pendingConsumed; static bool _inRemoteShot;
        public static bool InDirectLaunch;
        static int _launchDepth;

        public static void Apply(Harmony h)
        {
            var t = typeof(Patches);
            // projectile launch (exact launch state for outgoing shots; override for replicated shots)
            LaunchPatched = TryPatch(h, AccessTools.Method(typeof(ProjectileRegister), nameof(ProjectileRegister.Launch)), t, nameof(Launch_Prefix), nameof(Launch_Postfix));
            LaunchExplicitPatched = TryPatch(h, AccessTools.Method(typeof(ProjectileRegister), nameof(ProjectileRegister.Sprocket_DamageModelling_IProjectileFactory_Launch)), t, nameof(Launch_Prefix), nameof(Launch_Postfix));
            LaunchPatched |= LaunchExplicitPatched;
            // fallback detection of local shots if the launch hook failed
            if (!LaunchPatched)
                WeaponFirePatched = TryPatch(h, AccessTools.Method(typeof(WeaponBehaviour), nameof(WeaponBehaviour.Fire)), t, null, nameof(WeaponFire_Postfix));

            // damage immunity for puppets
            if (TryPatch(h, AccessTools.Method(typeof(HealthPool), nameof(HealthPool.HealthDelta), new[] { typeof(float), typeof(DamageModifiers) }), t, nameof(Pool_Prefix), null)) HealthPatchCount++;
            if (TryPatch(h, AccessTools.Method(typeof(HealthPool), nameof(HealthPool.HealthDelta), new[] { typeof(float) }), t, nameof(Pool_Prefix), null)) HealthPatchCount++;
            if (TryPatch(h, AccessTools.Method(typeof(HealthRegister), nameof(HealthRegister.HealthDelta), new[] { typeof(int), typeof(float), typeof(DamageModifiers) }), t, nameof(Register_Prefix), null)) HealthPatchCount++;
            if (TryPatch(h, AccessTools.Method(typeof(HealthRegister), nameof(HealthRegister.HealthDelta), new[] { typeof(float), typeof(DamageModifiers) }), t, nameof(Register_Prefix), null)) HealthPatchCount++;
            if (TryPatch(h, AccessTools.Method(typeof(VehicleHealthRegister), nameof(VehicleHealthRegister.Sprocket_DamageModelling_IDamageApplier_HealthDelta), new[] { typeof(int), typeof(float), typeof(DamageModelDamageModifiers) }), t, nameof(VRegister_Prefix), null)) HealthPatchCount++;
            if (TryPatch(h, AccessTools.Method(typeof(VehicleHealthRegister), nameof(VehicleHealthRegister.Sprocket_DamageModelling_IDamageApplier_HealthDelta), new[] { typeof(float), typeof(DamageModelDamageModifiers) }), t, nameof(VRegister_Prefix), null)) HealthPatchCount++;

            // puppet turrets/mantlets: freeze the native motors, the network drives the transforms
            AimPatchCount += TryPatch(h, AccessTools.Method(typeof(Sprocket.Vehicles.Weapons.TurretBehaviour), "MoveToTarget"), t, nameof(Aimable_Prefix), null) ? 1 : 0;
            AimPatchCount += TryPatch(h, AccessTools.Method(typeof(Sprocket.Vehicles.Weapons.TurretBehaviour), "Apply"), t, nameof(Aimable_Prefix), null) ? 1 : 0;
            AimPatchCount += TryPatch(h, AccessTools.Method(typeof(Sprocket.Vehicles.Weapons.LayingDriveBehaviour), "MoveToTarget"), t, nameof(Aimable_Prefix), null) ? 1 : 0;
            AimPatchCount += TryPatch(h, AccessTools.Method(typeof(Sprocket.Vehicles.Weapons.LayingDriveBehaviour), "Apply"), t, nameof(Aimable_Prefix), null) ? 1 : 0;

            // no victory / defeat screens in multiplayer (kill feed instead)
            EndScreenPatchCount += TryPatch(h, AccessTools.Method(typeof(DeathmatchGameMode), nameof(DeathmatchGameMode.CompleteMission)), t, nameof(MissionVoid_Prefix), null) ? 1 : 0;
            EndScreenPatchCount += TryPatch(h, AccessTools.Method(typeof(DeathmatchGameMode), nameof(DeathmatchGameMode.FailMission)), t, nameof(MissionVoid_Prefix), null) ? 1 : 0;
            EndScreenPatchCount += TryPatch(h, AccessTools.Method(typeof(DeathmatchGameMode), nameof(DeathmatchGameMode.ShowSuccessScreen)), t, nameof(MissionTask_Prefix), null) ? 1 : 0;
            EndScreenPatchCount += TryPatch(h, AccessTools.Method(typeof(DeathmatchGameMode), nameof(DeathmatchGameMode.ShowFailScreen)), t, nameof(MissionTask_Prefix), null) ? 1 : 0;
            EndScreenPatchCount += TryPatch(h, AccessTools.Method(typeof(DeathmatchGameMode), nameof(DeathmatchGameMode.ShowEndScreen)), t, nameof(MissionTask_Prefix), null) ? 1 : 0;

            MPLog.Info($"Patches: aim={AimPatchCount}/4 endScreens={EndScreenPatchCount}/5");
            MPLog.Info($"Patches: launch={LaunchPatched} (explicit={LaunchExplicitPatched}) weaponFireFallback={WeaponFirePatched} healthHooks={HealthPatchCount}/6");
        }

        static bool TryPatch(Harmony h, MethodBase target, Type host, string prefix, string postfix)
        {
            if (target == null) { MPLog.Warn($"Patch target not found ({prefix ?? postfix})"); return false; }
            try
            {
                h.Patch(target,
                    prefix: prefix != null ? new HarmonyMethod(host.GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)) : null,
                    postfix: postfix != null ? new HarmonyMethod(host.GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)) : null);
                MPLog.Debug($"Patched {target.DeclaringType?.Name}.{target.Name}");
                return true;
            }
            catch (Exception e)
            {
                MPLog.Warn($"Failed to patch {target.DeclaringType?.Name}.{target.Name}: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        // =============================================================================== puppet registry
        public static void RegisterPuppet(VehicleRig rig)
        {
            foreach (var p in rig.AimBehaviourPtrs) if (p != IntPtr.Zero) PuppetAimables.Add(p);
            if (!Plugin.PuppetsImmuneToLocalDamage.Value) return;
            if (rig.Pools.Count == 0 && rig.HealthReg == null) rig.ResolveCrew();
            bool first = RegisteredRigs.Add(rig.Ptr);
            foreach (var p in rig.Pools) if (p != null) PuppetPools.Add(p.Pointer);
            if (rig.HealthReg != null) PuppetRegisters.Add(rig.HealthReg.Pointer);
            if (rig.VehicleHealthReg != null) PuppetVehicleRegisters.Add(rig.VehicleHealthReg.Pointer);
            if (first) MPLog.Info($"Damage immunity on puppet {rig.RawName}: {rig.Pools.Count} pools");
        }

        public static void UnregisterPuppet(VehicleRig rig)
        {
            foreach (var p in rig.AimBehaviourPtrs) PuppetAimables.Remove(p);
            RegisteredRigs.Remove(rig.Ptr);
            foreach (var p in rig.Pools) if (p != null) PuppetPools.Remove(p.Pointer);
            if (rig.HealthReg != null) PuppetRegisters.Remove(rig.HealthReg.Pointer);
            if (rig.VehicleHealthReg != null) PuppetVehicleRegisters.Remove(rig.VehicleHealthReg.Pointer);
        }

        public static void ClearPuppets()
        {
            PuppetPools.Clear(); PuppetRegisters.Clear(); PuppetVehicleRegisters.Clear(); RegisteredRigs.Clear(); PuppetAimables.Clear();
            RemoteProjectiles.Clear();
        }

        // =============================================================================== aim / mission hooks
        static bool Aimable_Prefix(Il2CppSystem.Object __instance)
        {
            if (PuppetAimables.Count == 0 || __instance == null) return true;
            return !PuppetAimables.Contains(__instance.Pointer);
        }

        static float _nextSuppressLog;
        static bool MpActive => Session.Current != null && Session.Current.Active;

        static bool MissionVoid_Prefix()
        {
            if (!MpActive) return true;
            if (Time.unscaledTime > _nextSuppressLog) { _nextSuppressLog = Time.unscaledTime + 10f; MPLog.Info("Suppressed native mission end (multiplayer)"); }
            return false;
        }

        static bool MissionTask_Prefix(ref Il2CppSystem.Threading.Tasks.Task __result)
        {
            if (!MpActive) return true;
            __result = Il2CppSystem.Threading.Tasks.Task.CompletedTask;
            MPLog.Info("Suppressed native end screen (multiplayer)");
            return false;
        }

        // =============================================================================== damage hooks
        static bool Pool_Prefix(HealthPool __instance, ref float __result, ref float value)
        {
            // our spall cones / shell bursts: each fragment's damage is scaled (the game's damage per hit is tuned for its
            // own few spall pieces, not for dozens of simulated fragments)
            if (Shells.Explosives.InExplosion && value < 0f)
                value *= Shells.Explosives.Mode == 2 ? Shells.Explosives.P.SpallDamage : Shells.Explosives.P.BurstDamage;   // the shooter's values
            if (AllowDamage || PuppetPools.Count == 0) return true;
            if (!PuppetPools.Contains(__instance.Pointer)) return true;
            BlockedDamageEvents++; __result = 0f; return false;
        }

        static bool Register_Prefix(HealthRegister __instance)
        {
            if (AllowDamage || PuppetRegisters.Count == 0) return true;
            if (!PuppetRegisters.Contains(__instance.Pointer)) return true;
            BlockedDamageEvents++; return false;
        }

        static bool VRegister_Prefix(VehicleHealthRegister __instance)
        {
            if (AllowDamage || PuppetVehicleRegisters.Count == 0) return true;
            if (!PuppetVehicleRegisters.Contains(__instance.Pointer)) return true;
            BlockedDamageEvents++; return false;
        }

        // =============================================================================== projectile hooks
        /// <summary>Called once per frame: recovers the re-entrancy counter if a launch threw inside the game.</summary>
        public static void ResetFrame() { _launchDepth = 0; }

        public static void BeginRemoteShot(FireMsg f, ProjectileTypeID type)
        {
            _pendingShot = f; _pendingType = type; _pendingConsumed = false; _inRemoteShot = true;
        }

        public static bool EndRemoteShot()
        {
            bool c = _pendingConsumed;
            _pendingShot = null; _inRemoteShot = false; _pendingConsumed = false;
            return c;
        }

        static bool Launch_Prefix(ProjectileRegister __instance, ref ProjectileLaunchState launchState, ref ProjectileLauncherInfo launcherInfo, ref ProjectileTypeID projectileTypeID)
        {
            _launchDepth++;
            if (_launchDepth > 1) return true; // explicit-interface wrapper calling the real method
            try
            {
                if (CapturedFactory == null && __instance != null) CapturedFactory = __instance.Cast<IProjectileFactory>();
            }
            catch { }
            if (!_inRemoteShot) return true;
            if (_pendingConsumed)
            {
                // a multi-barrel weapon tried to launch again during one replicated shot; the shooter sends each barrel separately
                return false;
            }
            var f = _pendingShot;
            launchState = new ProjectileLaunchState(f.Pos, f.Dir, f.Vel);
            launcherInfo = new ProjectileLauncherInfo(f.BarrelLength, f.MaxPressure);
            projectileTypeID = _pendingType;
            _pendingConsumed = true;
            return true;
        }

        public static byte CurrentRemoteShooter = 255;

        static void Launch_Postfix(ProjectileRegister __instance, Il2CppSystem.Object source, ref ProjectileLaunchState launchState, ref ProjectileLauncherInfo launcherInfo, ProjectileTypeID projectileTypeID, ref ProjectileFireInfo fireInfo)
        {
            int depth = _launchDepth;
            if (_launchDepth > 0) _launchDepth--;
            if (depth != 1) return;
            if (_inRemoteShot || InDirectLaunch)
            {
                try
                {
                    if (_inRemoteShot && fireInfo.ProjectileID != 0)
                    {
                        // sabot rounds fly faster: the shooter's multiplier travels with the shot
                        var kind = (Shells.ShellType)_pendingShot.ShellKind;
                        if (Shells.ShellConfig.IsSabot(kind))
                        {
                            var sp = Shells.ShellParams.Parse(_pendingShot.Payload);
                            if (ShellVelocity.Scale(__instance.Pointer, fireInfo.ProjectileID, sp != null ? sp.VelMult : Shells.ShellConfig.VelocityMult(kind))) PenCapture.MarkLastShotScaled(_pendingShot.Id);
                        }
                    }
                    if (CurrentRemoteShooter != 255 && fireInfo.ProjectileID != 0)
                    {
                        RemoteProjectiles[fireInfo.ProjectileID] = (CurrentRemoteShooter, Time.time);
                        if (RemoteProjectiles.Count > 256)
                        {
                            var cutoff = Time.time - 30f; var old = new List<int>();
                            foreach (var kv in RemoteProjectiles) if (kv.Value.time < cutoff) old.Add(kv.Key);
                            foreach (var k in old) RemoteProjectiles.Remove(k);
                        }
                    }
                }
                catch { }
                return;
            }
            try
            {
                var bm = BattleManager.Instance;
                if (bm == null || !bm.InBattle) return;
                var s = Session.Current; if (s == null || !s.Active) return;
                if (!bm.IsLocalSource(source, launchState.Position)) return;
                if (Shells.ShellConfig.IsSabot(Shells.ShellSelect.Current) && fireInfo.ProjectileID != 0)
                    ShellVelocity.Scale(__instance.Pointer, fireInfo.ProjectileID, Shells.ShellConfig.VelocityMult(Shells.ShellSelect.Current));
                bm.OnLocalLaunch(source, launchState.Position, launchState.Direction, launchState.InitialVelocity,
                    launcherInfo.BarrelLength, launcherInfo.MaxPressure, projectileTypeID);
            }
            catch (Exception e) { MPLog.Warn("Launch postfix: " + e.Message); }
        }

        // fallback path when the launch hook can't be installed: read the projectile back out of the register
        static void WeaponFire_Postfix(WeaponBehaviour __instance, WeaponFireInfo __result)
        {
            if (_inRemoteShot || InDirectLaunch) return;
            try
            {
                var bm = BattleManager.Instance;
                if (bm == null || !bm.InBattle) return;
                var s = Session.Current; if (s == null || !s.Active) return;
                if (!bm.IsLocalSource(__instance, __instance.ShellSpawnPosition)) return;
                var reg = DamageModel.ProjectileRegister?.TryCast<ProjectileRegister>();
                if (reg == null) return;
                if (!reg.activeIdMap.TryGetValue(__result.ProjectileID, out int idx)) return;
                var inst = reg.pool[idx];
                var vel = inst.velocity;
                bm.OnLocalLaunch(__instance, inst.spawnPoint, vel.normalized, vel, 0f, 0u, inst.def.ID);
            }
            catch (Exception e) { MPLog.Warn("WeaponFire postfix: " + e.Message); }
        }
    }
}
