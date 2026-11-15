using System;
using System.Collections.Generic;
using Sprocket;
using UnityEngine;

namespace SprocketMP
{
    /// <summary>
    /// Third-person camera on multi-turret tanks: the game centres it on one turret of its choice (the vehicle's
    /// tracking information). With F7 › Battle › CameraFollowsFireGroup, the camera's close target (the one it orbits
    /// when zoomed in) becomes the turret of the gun of the selected fire group; a gun not in a turret gives the
    /// game's own choice back. Both the vehicle's tracking information and the camera's are set (the game copies the
    /// vehicle's into the camera when it starts tracking it again, e.g. after the gunner sight).
    /// The by-value properties are used: the game's ref-returning ones fail through the interop.
    /// </summary>
    public static class CameraFollow
    {
        static OrbitalMovementController _cam; static Sprocket.Vehicles.VehicleBehaviour _vb;
        static Transform _origClose, _origFar; static Vector3 _origCloseOffset, _origFarOffset; static bool _haveOrig;
        static IntPtr _applied; static float _next, _nextFind; static IntPtr _tank; static int _errors; static float _origMax = -1f, _origMin = -1f; static bool _loggedMax;

        public static void Tick()
        {
            var bm = BattleManager.Instance;
            if (bm == null || !bm.InBattle || bm.Local?.GO == null || FreeCam.Active) { if (bm == null || !bm.InBattle) Reset(); return; }
            KeepZoomRange();   // every frame: the game puts its own limit back
            if (!Plugin.CamFollowFireGroup.Value) { if (_applied != IntPtr.Zero) Restore(); if (_cam == null && Time.unscaledTime >= _nextFind) { _nextFind = Time.unscaledTime + 3f; try { _cam = FindCamera(bm.Local.GO.transform); } catch { } } return; }
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.25f;
            if (bm.Local.GO.Pointer != _tank) { Reset(); _tank = bm.Local.GO.Pointer; }
            try
            {
                var root = bm.Local.GO.transform;
                if (_vb == null)
                {
                    _vb = bm.Local.GO.GetComponentInChildren<Sprocket.Vehicles.VehicleBehaviour>(true);
                    if (_vb == null) _vb = bm.Local.GO.GetComponentInParent<Sprocket.Vehicles.VehicleBehaviour>();
                    if (_vb == null) return;
                }
                if (_cam == null && Time.unscaledTime >= _nextFind)   // (not searched again once found: the search is a whole-scene scan)
                {
                    _nextFind = Time.unscaledTime + 3f;
                    _cam = FindCamera(root);
                }
                // the zoom range the game gave the camera for the whole tank: the camera works it out again from its
                // target when it's told to track one, and a turret is much smaller than the tank
                var vinfo = _vb.trackingInformation;
                if (!_haveOrig)
                {
                    _origClose = vinfo.CloseTrackTarget; _origCloseOffset = vinfo.CloseTrackOffset; _origFar = vinfo.FarTrackTarget; _origFarOffset = vinfo.FarTrackOffset; _haveOrig = true;
                    MPLog.Info($"Camera: the game's offsets: far {_origFarOffset}, close {_origCloseOffset}");
                    MPLog.Info($"Camera: the game tracks '{Name(vinfo.FarTrackTarget)}' / '{Name(_origClose)}' (camera {(_cam != null ? "found" : "not found")})");
                }
                // the selected fire group's gun -> its turret
                Transform turret = null;
                Transform gun = null;
                try { gun = Shells.ShellSelect.ActiveWeapon != null ? Shells.ShellSelect.ActiveWeapon.transform : null; } catch { }
                if (gun == null) { var c = Shells.ShellSelect.ActiveCannon; if (c != null) gun = c.transform; }
                if (gun != null && gun.IsChildOf(root)) turret = TurretOf(gun, root, bm.Local);
                if (gun != null && gun.Pointer != _lastGun) { _lastGun = gun.Pointer; MPLog.Info($"Camera: selected gun '{gun.name}' -> {(turret != null ? "turret '" + turret.name + "'" : "no turret (the game's choice)")}"); }
                Transform want = turret != null ? turret : _origClose;
                if (want == null) return;
                // both targets: the camera orbits the far one when zoomed out and the close one when zoomed in (it blends
                // between them with the zoom), so the far one (the hull) is moved to the turret too
                Vector3 off = want == _origClose ? _origCloseOffset : Vector3.zero;
                Transform far = turret != null ? turret : _origFar;
                Vector3 farOff = _origFarOffset;   // zoomed out: the game's height above the target (zoom range and clipping as the game has them)
                var ni = new TrackingInformation(far, want, farOff, off);
                bool vehOk = vinfo.CloseTrackTarget != null && vinfo.CloseTrackTarget.Pointer == want.Pointer && vinfo.FarTrackTarget != null && vinfo.FarTrackTarget.Pointer == far.Pointer;
                if (!vehOk) _vb.trackingInformation = ni;
                bool camOk = true;
                if (_cam != null)
                {
                    var cinfo = _cam.trackingInformation;
                    var cc = cinfo.CloseTrackTarget; var cf = cinfo.FarTrackTarget;
                    bool ours = cf != null && cf.IsChildOf(root) || cc != null && cc.IsChildOf(root);   // the camera is on our tank (not the gunner sight / spectating)
                    camOk = !ours || cc != null && cc.Pointer == want.Pointer && cf != null && cf.Pointer == far.Pointer;
                    if (!camOk)
                    {
                        if (_applied == want.Pointer && ++_resets == 5) MPLog.Info("Camera: the game keeps moving the camera back to its own target; set again each time");
                        var ci = new TrackingInformation(far, want, farOff, off);
                        _cam.Track(ref ci);
                        KeepZoomRange();
                    }
                }
                if (_applied != want.Pointer) MPLog.Info($"Camera: vehicle target {(vehOk ? "already" : "set")}, camera {(_cam == null ? "not found" : camOk ? "already on it" : "set")}");
                if (_applied != want.Pointer && !_loggedTurrets)
                {
                    _loggedTurrets = true;
                    var sb = new System.Text.StringBuilder();
                    foreach (var x in bm.Local.TurretXforms) if (x != null) sb.Append($" '{x.name}' at {root.InverseTransformPoint(x.position)};");
                    MPLog.Info($"Camera: this tank's turrets:{sb}");
                }
                if (_applied != want.Pointer) MPLog.Info($"Camera: target at {root.InverseTransformPoint(want.position)} (tank space)");
                if (_applied != want.Pointer) MPLog.Info($"Camera: following {(turret != null ? "the turret '" + turret.name + "' of the selected fire group" : "the game's choice")}");
                _applied = want.Pointer;
            }
            catch (Exception e) { if (_errors++ < 5) MPLog.Warn("camera follow: " + e.Message); _cam = null; _vb = null; }
        }

        /// <summary>The turret ring the gun sits on (the nearest one up its parents: a gun in a turret on a turret
        /// gives the upper one); a turret behaviour as a fallback; null for a hull gun.</summary>
        static IntPtr _lastGun; static int _resets; static bool _loggedTurrets;
        static Transform TurretOf(Transform gun, Transform root, VehicleRig rig)
        {
            // what the turrets really rotate: the nearest one above the gun (a turret on a turret gives the upper one)
            var turrets = new HashSet<IntPtr>();
            foreach (var x in rig.TurretXforms) if (x != null) turrets.Add(x.Pointer);
            for (var t = gun; t != null && t != root; t = t.parent) if (turrets.Contains(t.Pointer)) return t;
            for (var t = gun; t != null && t != root; t = t.parent)
            {
                try { if (t.GetComponent<Sprocket.Vehicles.Turrets.TurretRing>() != null) return t; } catch { }
                try { if (t.GetComponent<Sprocket.Vehicles.Weapons.TurretBehaviour>() != null) return t; } catch { }
            }
            if (!_loggedChain)
            {
                _loggedChain = true;
                var sb = new System.Text.StringBuilder();
                for (var t = gun; t != null && t != root.parent; t = t.parent)
                {
                    sb.Append(t.name).Append(" [");
                    foreach (var co in t.GetComponents<Component>()) { try { sb.Append(co.GetIl2CppType().Name).Append(' '); } catch { } }
                    sb.Append("] < ");
                }
                MPLog.Info("Camera: no turret ring above the selected gun: " + sb);
            }
            return null;
        }
        static bool _loggedChain;

        static void KeepZoomRange()
        {
            if (_cam == null) return;
            try
            {
                if (_origMax < 0f) { _origMax = _cam.maxDistance; _origMin = _cam.minDistance; MPLog.Info($"Camera: the game's zoom range {_origMin:0.#}-{_origMax:0.#} m"); }
                float want = Math.Max(_origMax, Plugin.CamMaxZoom.Value);
                if (want <= 0f) return;
                float mx = _cam.maxDistance;
                if (mx < want - 0.01f)
                {
                    if (!_loggedMax) { _loggedMax = true; MPLog.Info($"Camera: zoom out limit {mx:0.#} m -> {want:0.#} m (F7 › Battle › CameraMaxZoomOut)"); }
                    _cam.maxDistance = want;
                }
            }
            catch { }
        }

        static OrbitalMovementController FindCamera(Transform root)
        {
            // the battle's player state holds the third-person camera
            try
            {
                foreach (var ps in UnityEngine.Object.FindObjectsOfType<Sprocket.VehicleControl.VehicleControlPlayerState>())
                {
                    var cm = ps != null ? ps.cameraMovementController : null;
                    if (cm != null) return cm;
                }
            }
            catch (Exception e) { MPLog.Debug("camera player state: " + e.Message); }
            OrbitalMovementController best = null;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<OrbitalMovementController>())
            {
                if (c == null) continue;
                try
                {
                    var i = c.trackingInformation;
                    if (i.FarTrackTarget != null && i.FarTrackTarget.IsChildOf(root) || i.CloseTrackTarget != null && i.CloseTrackTarget.IsChildOf(root)) return c;
                }
                catch { }
                if (best == null) best = c;
            }
            return best;
        }
        static string Name(Transform t) => t != null ? t.name : "-";

        static void Restore()
        {
            try
            {
                if (_vb != null && _haveOrig && _origClose != null)
                {
                    var info = _vb.trackingInformation;
                    var ni = new TrackingInformation(_origFar != null ? _origFar : info.FarTrackTarget, _origClose, _origFar != null ? _origFarOffset : info.FarTrackOffset, _origCloseOffset);
                    _vb.trackingInformation = ni;
                    if (_cam != null) _cam.Track(ref ni);
                }
            }
            catch { }
            _applied = IntPtr.Zero;
        }
        static void Reset() { _cam = null; _vb = null; _haveOrig = false; _origClose = null; _origFar = null; _applied = IntPtr.Zero; _tank = IntPtr.Zero; _errors = 0; _origMax = _origMin = -1f; _loggedMax = false; _lastGun = IntPtr.Zero; _resets = 0; _loggedTurrets = false; }
    }
}
