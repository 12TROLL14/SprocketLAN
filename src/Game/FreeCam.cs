using System;
using System.Collections.Generic;
using HarmonyLib;
using Sprocket.PlayerControl;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketMP
{
    /// <summary>
    /// Free camera in battle, alive or not. While it is on:
    /// - the game's camera update (PlayerCameraController.CameraUpdate) is skipped and we place the camera ourselves;
    /// - every enabled game input action is disabled, so WASD / the mouse move the camera and not the tank (the tank
    ///   coasts with no input). They are enabled again when the free cam is turned off.
    /// The mod reads the keyboard and mouse directly, so its own keys keep working.
    /// </summary>
    public static class FreeCam
    {
        public static bool Active { get; private set; }
        static Camera _cam;
        static Vector3 _pos, _savedPos; static Quaternion _savedRot;
        static float _yaw, _pitch, _speedMul = 1f, _savedFov;
        static readonly List<UnityEngine.InputSystem.InputAction> _disabled = new List<UnityEngine.InputSystem.InputAction>();
        static float _nextBlock, _menuAt; static bool _fightWarned, _gameMenu, _weLocked;
        static string _keyName; static Key _key = Key.None;
        static CursorLockMode _savedLock; static bool _savedVisible;

        public static void Patch(Harmony h)
        {
            try
            {
                var m = AccessTools.Method(typeof(PlayerCameraController), nameof(PlayerCameraController.CameraUpdate));
                h.Patch(m, prefix: new HarmonyMethod(typeof(FreeCam), nameof(CamPrefix)), postfix: new HarmonyMethod(typeof(FreeCam), nameof(CamPostfix)));
                MPLog.Info("Free cam: hooked the game camera");
            }
            catch (Exception e) { MPLog.Warn("Free cam hook failed (the free cam may fight the game camera): " + e.Message); }
        }
        static bool CamPrefix() => !Active;
        static void CamPostfix() { if (Active) Apply(); }

        public static string KeyName => Plugin.FreeCamKey.Value;

        /// <summary>Every frame (main thread). `typing`: a text field has focus; `uiCursor`: a mod panel uses the mouse.</summary>
        public static void Update(bool typing, bool uiCursor)
        {
            var kb = Keyboard.current;
            if (_keyName != Plugin.FreeCamKey.Value) { _keyName = Plugin.FreeCamKey.Value; if (!Enum.TryParse(_keyName, true, out _key)) _key = Key.None; }
            var bm = BattleManager.Instance;
            bool can = bm != null && bm.Local != null && bm.Local.GO != null && Plugin.FreeCamEnabled.Value;
            if (Active && (!can || _cam == null)) { Exit("battle ended"); return; }
            if (kb != null && !typing)
            {
                bool toggle = false;
                try { toggle = _key != Key.None && kb[_key].wasPressedThisFrame; } catch { }
                if (toggle) { if (Active) Exit("key"); else if (can) Enter(); }
            }
            if (!Active) return;

            if (!_fightWarned && _cam != null && (_cam.transform.position - _pos).sqrMagnitude > 0.05f)
            { _fightWarned = true; MPLog.Warn($"Free cam: something else also moves the camera ({_cam.transform.position} vs {_pos})"); }
            if (Time.unscaledTime >= _nextBlock) { _nextBlock = Time.unscaledTime + 0.5f; BlockInputs(); }   // the game may enable some again

            // Esc opens the game's pause menu: leave the cursor alone until the menu is closed again
            // (Esc again, or the game locking the cursor itself when it resumes)
            if (kb != null && kb.escapeKey.wasPressedThisFrame) { _gameMenu = !_gameMenu; _menuAt = Time.unscaledTime; }
            if (_gameMenu && Time.unscaledTime - _menuAt > 0.3f && Cursor.lockState == CursorLockMode.Locked && !_weLocked) _gameMenu = false;
            if (_gameMenu || Time.timeScale == 0f) uiCursor = true;
            _weLocked = false;

            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            var mouse = Mouse.current;
            if (!uiCursor)
            {
                if (Cursor.lockState != CursorLockMode.Locked) { Cursor.lockState = CursorLockMode.Locked; _weLocked = true; }
                Cursor.visible = false;
                if (mouse != null)
                {
                    var d = mouse.delta.ReadValue() * (0.08f * Mathf.Max(0.05f, Plugin.FreeCamSensitivity.Value));
                    _yaw += d.x; _pitch = Mathf.Clamp(_pitch - d.y, -89f, 89f);
                }
            }
            if (mouse != null && !uiCursor)
            {
                float sc = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(sc) > 0.01f) _speedMul = Mathf.Clamp(_speedMul * (sc > 0 ? 1.25f : 0.8f), 0.05f, 20f);
            }
            if (kb != null && !typing && !_gameMenu)
            {
                var rot = Quaternion.Euler(_pitch, _yaw, 0f);
                Vector3 mv = Vector3.zero;
                if (kb.wKey.isPressed) mv += Vector3.forward;
                if (kb.sKey.isPressed) mv += Vector3.back;
                if (kb.dKey.isPressed) mv += Vector3.right;
                if (kb.aKey.isPressed) mv += Vector3.left;
                Vector3 world = rot * mv;
                if (kb.eKey.isPressed || kb.spaceKey.isPressed) world += Vector3.up;
                if (kb.qKey.isPressed || kb.leftCtrlKey.isPressed) world += Vector3.down;
                float speed = Mathf.Max(0.1f, Plugin.FreeCamSpeed.Value) * _speedMul;
                if (kb.leftShiftKey.isPressed) speed *= 4f;
                if (kb.leftAltKey.isPressed) speed *= 0.25f;
                _pos += world * speed * dt;
            }
            Apply();
        }

        public static string Help => $"<b>FREE CAM</b>  <color=#bbb>WASD move · Q/E down/up · Shift fast · Alt slow · wheel speed x{_speedMul:0.##} · [{KeyName}] exit</color>";

        static void Apply()
        {
            if (_cam == null) return;
            try { _cam.transform.SetPositionAndRotation(_pos, Quaternion.Euler(_pitch, _yaw, 0f)); } catch { }
        }

        static void Enter()
        {
            try
            {
                var pcc = UnityEngine.Object.FindObjectOfType<PlayerCameraController>();
                Camera cam = null;
                if (pcc != null)
                {
                    try { if (pcc.thirdPersonCamera != null && pcc.ActiveCamera != pcc.thirdPersonCamera) pcc.SetThirdPersonCameraActive(true); } catch (Exception e) { MPLog.Debug("free cam third person: " + e.Message); }
                    try { cam = pcc.thirdPersonCamera != null ? pcc.thirdPersonCamera : pcc.ActiveCamera; } catch { }
                }
                if (cam == null) cam = Camera.main;
                if (cam == null) { MPLog.Warn("Free cam: no camera found"); return; }
                _cam = cam;
                _savedPos = cam.transform.position; _savedRot = cam.transform.rotation; _savedFov = cam.fieldOfView;
                _pos = _savedPos;
                var e2 = _savedRot.eulerAngles; _yaw = e2.y; _pitch = e2.x > 180f ? e2.x - 360f : e2.x;
                _savedLock = Cursor.lockState; _savedVisible = Cursor.visible;
                Active = true; _fightWarned = false; _gameMenu = false;
                BlockInputs();
                MPLog.Info($"Free cam on (camera '{cam.name}', {_disabled.Count} game input actions paused)");
            }
            catch (Exception e) { MPLog.Warn("Free cam: " + e.Message); Exit("error"); }
        }

        static void BlockInputs()
        {
            try
            {
                var list = UnityEngine.InputSystem.InputSystem.ListEnabledActions();
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    var a = list[i]; if (a == null) continue;
                    if (UsesEscape(a)) continue;   // the game's pause menu stays usable
                    try { a.Disable(); _disabled.Add(a); } catch { }
                }
            }
            catch (Exception e) { MPLog.Debug("free cam input block: " + e.Message); }
        }

        static bool UsesEscape(UnityEngine.InputSystem.InputAction a)
        {
            try
            {
                var b = a.bindings;
                for (int i = 0; i < b.Count; i++)
                {
                    var path = (b[i].effectivePath ?? b[i].path ?? "").ToLowerInvariant();
                    if (path.Contains("escape")) return true;
                }
            }
            catch { }
            return false;
        }

        public static void Exit(string why)
        {
            if (!Active) return;
            Active = false;
            int n = 0;
            foreach (var a in _disabled) { try { if (a != null) { a.Enable(); n++; } } catch { } }
            _disabled.Clear();
            try { if (_cam != null) { _cam.transform.SetPositionAndRotation(_savedPos, _savedRot); _cam.fieldOfView = _savedFov; } } catch { }
            _cam = null; _speedMul = 1f;
            try { Cursor.lockState = _savedLock; Cursor.visible = _savedVisible; } catch { }
            MPLog.Info($"Free cam off ({why}), {n} game input actions resumed");
        }
    }
}
