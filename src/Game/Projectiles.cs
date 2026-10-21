using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using Sprocket.DamageModelling;
using Sprocket.Vehicles;
using Sprocket.WeaponFramework;
using SprocketMP.Net;
using UnityEngine;

namespace SprocketMP
{
    /// <summary>Projectile type lookup and remote shot spawning.</summary>
    public static class Projectiles
    {
        static readonly Dictionary<string, ProjectileTypeID> _guidToId = new Dictionary<string, ProjectileTypeID>();
        static readonly Dictionary<int, string> _idToGuid = new Dictionary<int, string>();

        // NOTE: projectile type ids are registered per spawned design and change when vehicles respawn,
        // so nothing here is cached - ids are always resolved against the live register.
        public static string GuidOf(ProjectileTypeID id)
        {
            try
            {
                var reg = IProjectileTypeRegister.Instance;
                if (reg != null && reg.TryGet(id, out IProjectileType t) && t != null) return t.Guid.ToString();
            }
            catch (Exception e) { MPLog.Warn("GuidOf: " + e.Message); }
            return "";
        }

        public static bool TryResolve(string guid, out ProjectileTypeID id)
        {
            id = default;
            if (string.IsNullOrEmpty(guid)) return false;
            try
            {
                var reg = IProjectileTypeRegister.Instance;
                if (reg == null) return false;
                var g = Il2CppSystem.Guid.Parse(guid);
                if (!reg.IsRegistered(g)) return false;
                id = reg.GetID(g);
                return reg.TryGet(id, out IProjectileType _);
            }
            catch (Exception e) { MPLog.Warn("Resolve projectile guid: " + e.Message); return false; }
        }

        static bool IsRegistered(ProjectileTypeID id)
        {
            try { var reg = IProjectileTypeRegister.Instance; return reg != null && reg.TryGet(id, out IProjectileType _); } catch { return false; }
        }

        public static void ClearCache() { _guidToId.Clear(); _idToGuid.Clear(); }

        public static Transform TransformOf(Il2CppSystem.Object source)
        {
            if (source == null) return null;
            try
            {
                var wb = source.TryCast<WeaponBehaviour>(); if (wb != null) return wb.transform;
                var c = source.TryCast<Component>(); if (c != null) return c.transform;
                var go = source.TryCast<GameObject>(); if (go != null) return go.transform;
                var t = source.TryCast<Transform>(); if (t != null) return t;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Fire a replicated shot from a puppet. Preferred path: let the puppet's own cannon fire (recoil, muzzle flash,
        /// sound) while the launch hook swaps in the shooter's exact launch state. Fallback: launch the projectile directly.
        /// </summary>
        public static void FireRemote(VehicleRig rig, FireMsg f)
        {
            Patches.CurrentRemoteShooter = f.Id;
            try { FireRemoteInner(rig, f); } finally { Patches.CurrentRemoteShooter = 255; }
        }

        static void FireRemoteInner(VehicleRig rig, FireMsg f)
        {
            var w = PickWeapon(rig, f.Weapon, out int wIndex);
            bool haveNative = rig.NativeShellTypes.TryGetValue(wIndex, out var nativeType) && IsRegistered(nativeType);
            if (!TryResolve(f.ProjectileGuid, out var typeId))
            {
                // projectile types are registered per design; fall back to what this puppet's gun natively uses
                if (haveNative) typeId = nativeType;
                else if (w != null && w.LoadedProjectileTypeID.Value > 0 && IsRegistered(w.LoadedProjectileTypeID)) typeId = w.LoadedProjectileTypeID;
                else { MPLog.Warn($"Remote shot dropped: projectile {f.ProjectileGuid} unknown here and puppet gun {wIndex} has no shell type"); return; }
            }

            if (Patches.LaunchPatched && w != null)
            {
                Patches.BeginRemoteShot(f, typeId);
                try
                {
                    // puppets have no AI loader: keep the breech primed so FireInternal always has a round
                    w.LoadedProjectileTypeID = typeId; w.Functioning = true; w.NeedsReload = false;
                    w.FireInternal(typeId);
                }
                catch (Exception e) { MPLog.Warn("Puppet FireInternal: " + e.Message); }
                bool consumed = Patches.EndRemoteShot();
                try { w.LoadedProjectileTypeID = haveNative ? nativeType : typeId; w.NeedsReload = false; } catch { }
                if (consumed) { MPLog.Info($"Remote shot from player {f.Id} fired through puppet weapon {wIndex}"); return; }
                MPLog.Warn("Puppet weapon did not launch; using direct launch");
            }
            DirectLaunch(w != null ? (Il2CppSystem.Object)w : rig.VB, f, typeId);
        }

        static WeaponBehaviour PickWeapon(VehicleRig rig, int index, out int used)
        {
            used = index;
            if (index >= 0 && index < rig.Weapons.Count && rig.Weapons[index] != null) return rig.Weapons[index];
            for (int i = 0; i < rig.Weapons.Count; i++) if (rig.Weapons[i] != null) { used = i; return rig.Weapons[i]; }
            used = -1; return null;
        }

        public static void DirectLaunch(Il2CppSystem.Object source, FireMsg f, ProjectileTypeID typeId)
        {
            IProjectileFactory factory = Patches.CapturedFactory;
            if (factory == null) { try { factory = DamageModel.ProjectileFactory; } catch { } }
            if (factory == null) { MPLog.Warn("No projectile factory available yet; shot dropped"); return; }
            var state = new ProjectileLaunchState(f.Pos, f.Dir, f.Vel);
            var info = new ProjectileLauncherInfo(f.BarrelLength, f.MaxPressure);
            Patches.InDirectLaunch = true;
            try
            {
                factory.Launch(source, ref state, ref info, typeId, out ProjectileFireInfo fi);
                MPLog.Info($"Remote shot from player {f.Id} launched directly (projectile {fi.ProjectileID})");
            }
            catch (Exception e) { MPLog.Error("DirectLaunch: " + e); }
            finally { Patches.InDirectLaunch = false; }
        }
    }
}
