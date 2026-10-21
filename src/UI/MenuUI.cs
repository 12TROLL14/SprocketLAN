using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SprocketMP.Net;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SprocketMP.UI
{
    /// <summary>
    /// uGUI/TextMeshPro overlay. No IMGUI (stripped in this build), no injected classes and no IL2CPP delegates:
    /// buttons and text fields are hit-tested manually with the Input System mouse, text is typed by polling keys.
    /// </summary>
    public class MenuUI
    {
        class Button { public RectTransform Rect; public Image Img; public Color Normal; public Action OnClick; public TextMeshProUGUI Label; }
        class Field { public RectTransform Rect; public TMP_InputField Input; public Action<string> Commit; public bool WasFocused; public string Value => Input != null ? Input.text : ""; }

        GameObject _root, _panel, _hud, _feed, _death;
        TextMeshProUGUI _status, _hudText, _feedText, _deathText;
        TMP_FontAsset _font;
        PenCamView _penCam;
        readonly List<Button> _buttons = new List<Button>();
        readonly List<Field> _fields = new List<Field>();
        Button _hostBtn, _joinBtn, _stopBtn;
        bool _visible = true, _built;
        float _nextBuildTry, _nextText;
        bool _wasInBattle;

        static readonly Color PanelCol = new Color(0.08f, 0.09f, 0.10f, 0.92f);
        static readonly Color BtnCol = new Color(0.22f, 0.36f, 0.22f, 1f);
        static readonly Color BtnRed = new Color(0.45f, 0.18f, 0.16f, 1f);
        static readonly Color FieldCol = new Color(0.16f, 0.17f, 0.19f, 1f);
        static readonly Color FieldFocus = new Color(0.25f, 0.30f, 0.40f, 1f);

        public bool Visible => _visible;

        public void Update()
        {
            if (!_built)
            {
                if (Time.realtimeSinceStartup < _nextBuildTry) return;
                _nextBuildTry = Time.realtimeSinceStartup + 2f;
                TryBuild();
                if (!_built) return;
            }
            if (_root == null || _root.WasCollected) { _built = false; return; }

            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.f10Key.wasPressedThisFrame && !AnyFieldFocused()) { _visible = !_visible; if (!_visible) Commit(); }
                if (kb.f11Key.wasPressedThisFrame) Runner.Battle.DumpDiagnostics("F11");
            }

            bool inBattle = Runner.Battle.InBattle;
            if (inBattle && !_wasInBattle) _visible = false;   // get out of the way when a battle starts
            _wasInBattle = inBattle;

            _panel.SetActive(_visible);
            _hud.SetActive(Runner.Session.Active || !_visible || Runner.Battle.Repairing);

            UpdateShellPanel(inBattle);
            if (_visible)
            {
                if (inBattle) { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; }
                HandleMouse();
                PollFields();
                RefreshButtons();
            }
            else if (_shellPanelOn) { HandleMouse(); PollFields(); }
            try { Shells.ShellSelect.Update(AnyFieldFocused()); } catch (Exception e) { MPLog.Debug("shell select: " + e.Message); }
            if (Time.realtimeSinceStartup >= _nextText) { _nextText = Time.realtimeSinceStartup + 0.25f; RefreshText(); }
            RefreshFeed();
            RefreshDeath();
            _penCam?.Update();
        }

        // ------------------------------------------------------------------------------------- build
        void TryBuild()
        {
            try
            {
                _font = FindFont();
                if (_font == null) { MPLog.Debug("UI: font not available yet"); return; }

                _root = new GameObject("SprocketLAN_UI");
                UnityEngine.Object.DontDestroyOnLoad(_root);
                var canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 32000;
                _root.AddComponent<GraphicRaycaster>();   // lets the game's EventSystem focus our input fields
                var scaler = _root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 0.5f;

                // HUD line (top right)
                _hud = new GameObject("HUD"); _hud.transform.SetParent(_root.transform, false);
                var hrt = _hud.AddComponent<RectTransform>();
                hrt.anchorMin = hrt.anchorMax = hrt.pivot = new Vector2(1, 1);
                hrt.anchoredPosition = new Vector2(-12, -8); hrt.sizeDelta = new Vector2(860, 80);
                _hudText = _hud.AddComponent<TextMeshProUGUI>();
                StyleText(_hudText, 17, TextAlignmentOptions.TopRight);

                // kill feed (top left, under the HUD area)
                _feed = new GameObject("KillFeed"); _feed.transform.SetParent(_root.transform, false);
                var frt = _feed.AddComponent<RectTransform>();
                frt.anchorMin = frt.anchorMax = frt.pivot = new Vector2(0.5f, 1);
                frt.anchoredPosition = new Vector2(0, -50); frt.sizeDelta = new Vector2(900, 220);
                _feedText = _feed.AddComponent<TextMeshProUGUI>();
                StyleText(_feedText, 24, TextAlignmentOptions.Top);

                // "you were destroyed" banner (screen centre)
                _death = new GameObject("DeathBanner"); _death.transform.SetParent(_root.transform, false);
                var drt = _death.AddComponent<RectTransform>();
                drt.anchorMin = drt.anchorMax = drt.pivot = new Vector2(0.5f, 0.5f);
                drt.anchoredPosition = new Vector2(0, 120); drt.sizeDelta = new Vector2(1400, 260);
                _deathText = _death.AddComponent<TextMeshProUGUI>();
                StyleText(_deathText, 64, TextAlignmentOptions.Center);
                _death.SetActive(false);
                _penCam = new PenCamView(_root.transform, _font);
                BuildShellPanel();

                // main panel (top left)
                _panel = Panel(_root.transform, "Panel", new Vector2(18, -60), new Vector2(560, 640), PanelCol);
                float y = -12;
                Label(_panel.transform, new Vector2(16, y), new Vector2(530, 34), "<b>SPROCKET LAN</b>  v" + Plugin.VERSION + "   <size=15><color=#9aa>[F10] hide</color></size>", 24); y -= 44;

                Label(_panel.transform, new Vector2(16, y), new Vector2(140, 30), "Player name", 18);
                MakeField(_panel.transform, new Vector2(160, y), new Vector2(380, 30), Plugin.EffectiveName, 24, false, v => { if (!string.IsNullOrWhiteSpace(v)) { Plugin.PlayerName.Value = v.Trim(); Plugin.CmdName = null; } });
                y -= 40;

                Label(_panel.transform, new Vector2(16, y), new Vector2(140, 30), "Host port", 18);
                var portField = MakeField(_panel.transform, new Vector2(160, y), new Vector2(120, 30), Plugin.Port.Value.ToString(), 5, true, v => { if (int.TryParse(v, out var p) && p > 0 && p < 65536) Plugin.Port.Value = p; });
                _hostBtn = MakeButton(_panel.transform, new Vector2(290, y), new Vector2(250, 30), "HOST GAME", BtnCol, () =>
                {
                    Commit(); Runner.Session.StartHost(Plugin.Port.Value);
                });
                y -= 40;

                Label(_panel.transform, new Vector2(16, y), new Vector2(140, 30), "Join ip:port", 18);
                var addrField = MakeField(_panel.transform, new Vector2(160, y), new Vector2(240, 30), Plugin.JoinAddress.Value, 64, false, v => Plugin.JoinAddress.Value = v.Trim());
                _joinBtn = MakeButton(_panel.transform, new Vector2(410, y), new Vector2(130, 30), "JOIN", BtnCol, () =>
                {
                    Commit(); Runner.Session.StartClient(Plugin.JoinAddress.Value);
                });
                y -= 40;

                _stopBtn = MakeButton(_panel.transform, new Vector2(16, y), new Vector2(260, 30), "DISCONNECT", BtnRed, () => Runner.Session.Stop());
                MakeButton(_panel.transform, new Vector2(286, y), new Vector2(254, 30), "DIAGNOSTICS (F11)", new Color(0.25f, 0.25f, 0.3f, 1f), () => Runner.Battle.DumpDiagnostics("button"));
                y -= 44;

                var srt = new GameObject("Status"); srt.transform.SetParent(_panel.transform, false);
                var rt = srt.AddComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
                rt.anchoredPosition = new Vector2(16, y); rt.sizeDelta = new Vector2(528, 640 + y - 12);
                _status = srt.AddComponent<TextMeshProUGUI>();
                StyleText(_status, 17, TextAlignmentOptions.TopLeft);

                _built = true;
                MPLog.Info("Menu UI built (font " + _font.name + ")");
            }
            catch (Exception e)
            {
                MPLog.Warn("UI build failed (will retry): " + e.Message);
                try { if (_root != null) UnityEngine.Object.Destroy(_root); } catch { }
                _root = null; _buttons.Clear(); _fields.Clear();
            }
        }

        // ------------------------------------------------------------------------------------- shell settings (vehicle designer)
        GameObject _shellPanel; TextMeshProUGUI _shellInfo; bool _shellPanelOn; float _nextAmmoScan; RectTransform _ammoTitle;

        void BuildShellPanel()
        {
            _shellPanel = Panel(_root.transform, "ShellPanel", Vector2.zero, new Vector2(430, 330), PanelCol);
            var rt = _shellPanel.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1, 1);
            rt.anchoredPosition = new Vector2(-14, -250);
            float y = -10;
            _shellTitle = Label(_shellPanel.transform, new Vector2(14, y), new Vector2(405, 28), "", 19); y -= 36;
            _shellFields[0] = ShellRow(ref y, "APHE filler (grams)", (d, f) => d.ApheFiller = Mathf.Clamp(f, 0f, 2000f));
            _shellFields[1] = ShellRow(ref y, "HE filler (grams)", (d, f) => d.HeFiller = Mathf.Clamp(f, 0f, 20000f));
            _shellFields[2] = ShellRow(ref y, "APHE fuse sensitivity (mm)", (d, f) => d.FuseSens = Mathf.Clamp(f, 0f, 500f));
            _shellFields[3] = ShellRow(ref y, "APHE fuse distance (m)", (d, f) => d.FuseDist = Mathf.Clamp(f, 0.05f, 10f));
            y -= 4;
            var go = new GameObject("Info"); go.transform.SetParent(_shellPanel.transform, false);
            var irt = go.AddComponent<RectTransform>(); irt.anchorMin = irt.anchorMax = irt.pivot = new Vector2(0, 1);
            irt.anchoredPosition = new Vector2(14, y); irt.sizeDelta = new Vector2(405, 330 + y - 8);
            _shellInfo = go.AddComponent<TextMeshProUGUI>(); StyleText(_shellInfo, 15, TextAlignmentOptions.TopLeft);

            _shellPanel.SetActive(false);
        }

        TextMeshProUGUI _shellTitle; readonly Field[] _shellFields = new Field[4]; string _shellDesign = null;

        Field ShellRow(ref float y, string label, Action<Shells.ShellStore.Design, float> set)
        {
            Label(_shellPanel.transform, new Vector2(14, y), new Vector2(230, 28), label, 17);
            var f = MakeField(_shellPanel.transform, new Vector2(250, y), new Vector2(130, 28), "", 7, false, v =>
            {
                if (!TryF(v, out var x) || string.IsNullOrEmpty(_shellDesign)) return;
                var d = Shells.ShellStore.Get(_shellDesign);
                var copy = new Shells.ShellStore.Design { ApheFiller = d.ApheFiller, HeFiller = d.HeFiller, FuseSens = d.FuseSens, FuseDist = d.FuseDist };
                set(copy, x);
                Shells.ShellStore.Set(_shellDesign, copy);
            });
            y -= 34;
            return f;
        }

        /// <summary>The design being edited: its name selects the saved shell settings shown in the panel.</summary>
        void RefreshShellDesign()
        {
            string name = Shells.ShellStore.DesignNameInScene();
            if (name == _shellDesign) return;
            _shellDesign = name; Shells.ShellStore.ActiveName = name;
            var d = Shells.ShellStore.Get(name);
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            float[] vals = { d.ApheFiller, d.HeFiller, d.FuseSens, d.FuseDist };
            for (int i = 0; i < 4; i++) if (_shellFields[i]?.Input != null) _shellFields[i].Input.text = vals[i].ToString("0.##", ci);
            _shellTitle.text = $"<b>Shells</b>  <color=#fd8>{(string.IsNullOrEmpty(name) ? "?" : name)}</color> <size=13><color=#9aa>(keys 1/2/3 in battle)</color></size>";
            MPLog.Info($"Shell panel: design '{name}'");
        }

        /// <summary>Root object of the vehicle in the scene (designer has no VehicleBehaviour).</summary>
        static GameObject FindDesignerVehicle(out int comps)
        {
            comps = 0;
            foreach (var v in UnityEngine.Object.FindObjectsOfType<Sprocket.Vehicles.VehicleBehaviour>()) if (v != null) return v.gameObject;
            var all = UnityEngine.Object.FindObjectsOfType<Sprocket.Vehicles.VehicleComponent>();
            comps = all.Length;
            Transform from = null;
            foreach (var vc in all) if (vc != null && vc.TryCast<Sprocket.Vehicles.Cannons.Cannon>() != null) { from = vc.transform; break; }
            if (from == null && comps > 0) from = all[0].transform;
            for (var t = from; t != null; t = t.parent)
                if (t.GetComponentsInChildren<Sprocket.Vehicles.VehicleComponent>(true).Length >= comps) return t.gameObject;
            return null;
        }

        static bool TryF(string v, out float f) => float.TryParse((v ?? "").Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f);

        /// <summary>Shows the shell settings next to the designer's "Ammo Rack" panel.</summary>
        void UpdateShellPanel(bool inBattle)
        {
            if (_shellPanel == null) return;
            if (inBattle) { if (_shellPanelOn) { _shellPanel.SetActive(false); _shellPanelOn = false; } return; }
            if (Time.realtimeSinceStartup >= _nextAmmoScan)
            {
                _nextAmmoScan = Time.realtimeSinceStartup + 0.5f;
                bool found = false;
                try
                {
                    if (_ammoTitle != null && _ammoTitle.gameObject.activeInHierarchy) found = true;
                    else
                    {
                        _ammoTitle = null;
                        foreach (var t in UnityEngine.Object.FindObjectsOfType<TMP_Text>())
                            if (t != null && t.gameObject.activeInHierarchy && t.text == "Ammo Rack" && !t.transform.IsChildOf(_root.transform)) { _ammoTitle = t.rectTransform; found = true; break; }
                    }
                }
                catch { }
                if (found != _shellPanelOn) { _shellPanelOn = found; _shellPanel.SetActive(found); }
            }
            if (_shellPanelOn) { UpdateShellTest(); if (Time.realtimeSinceStartup >= _nextShellInfo) try { RefreshShellDesign(); } catch (Exception e) { MPLog.Debug("shell design: " + e.Message); } } else { _testArmed = false; _shellDesign = null; }
            if (_shellPanelOn && _shellInfo != null && Time.realtimeSinceStartup >= _nextShellInfo)
            {
                _nextShellInfo = Time.realtimeSinceStartup + 0.5f;
                _shellInfo.text = ShellInfoText();
            }
        }
        float _nextShellInfo;
        GameObject _testRow;
        readonly Button[] _testBtns = new Button[3]; Button _testArmBtn; Shells.ShellType _testKind = Shells.ShellType.AP; bool _testArmed;

        void UpdateShellTest()
        {
            if (_testRow == null) return;     // sandbox shell test removed
            var kb = Keyboard.current;
            if (kb != null && !AnyFieldFocused())
                try { if (Enum.TryParse<Key>(Plugin.ShellTestKey.Value, true, out var tk) && tk != Key.None && kb[tk].wasPressedThisFrame) { _testRow.SetActive(!_testRow.activeSelf); if (!_testRow.activeSelf) _testArmed = false; } } catch { }
            if (_testRow == null || !_testRow.activeSelf) return;
            for (int i = 0; i < 3; i++) if (_testBtns[i] != null) _testBtns[i].Normal = (int)_testKind == i ? new Color(0.75f, 0.55f, 0.15f, 1f) : new Color(0.25f, 0.27f, 0.3f, 1f);
            _testArmBtn.Normal = _testArmed ? new Color(0.7f, 0.2f, 0.15f, 1f) : new Color(0.3f, 0.3f, 0.45f, 1f);
            _testArmBtn.Label.text = _testArmed ? $"<b>[{Plugin.ShellTestFireKey.Value}] FIRE</b>" : "<b>TEST SHELL</b>";
            if (!_testArmed || AnyFieldFocused() || kb == null) return;
            bool fire = false;
            try { fire = Enum.TryParse<Key>(Plugin.ShellTestFireKey.Value, true, out var fk) && fk != Key.None && kb[fk].wasPressedThisFrame; } catch { }
            if (!fire) return;
            try { FireTestShell(_testKind); } catch (Exception e) { MPLog.Warn("Test shell: " + e); }
        }

        GameObject _testDM;

        /// <summary>The designer has no armour simulation model: build one from the vehicle the same way battles do.</summary>
        Sprocket.DamageModelling.CompoundStructure BuildTestDamageModel(GameObject root)
        {
            try
            {
                if (_testDM != null) UnityEngine.Object.Destroy(_testDM);
                _testDM = new GameObject("SprocketLAN_TestDamageModel");
                _testDM.transform.SetParent(root.transform, false);
                var list = new Il2CppSystem.Collections.Generic.List<Sprocket.Vehicles.VehicleObject>();
                foreach (var o in root.GetComponentsInChildren<Sprocket.Vehicles.VehicleObject>()) if (o != null) list.Add(o);
                var builder = new Sprocket.DamageModelling.CompoundStructureBuilder();
                Sprocket.Vehicles.DamageModels.VehicleDamageModelGeneration.CreateStructures(new Sprocket.DamageModelling.IDamageModelConstructor(builder.Pointer), new Il2CppSystem.Collections.Generic.IReadOnlyList<Sprocket.Vehicles.VehicleObject>(list.Pointer));
                var built = builder.Construct(_testDM);
                var cs = built?.TryCast<Sprocket.DamageModelling.CompoundStructure>() ?? _testDM.GetComponent<Sprocket.DamageModelling.CompoundStructure>();
                MPLog.Info($"Test shell: built a damage model from {list.Count} vehicle objects -> {(cs != null ? "ok" : "failed")}");
                return cs;
            }
            catch (Exception e) { MPLog.Warn("Test damage model: " + e); return null; }
        }

        static bool MainGun(out float mass, out int cal, out float speed, out ushort k)
        {
            mass = 0; cal = 0; speed = 0; k = 0;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Sprocket.Vehicles.Cannons.Cannon>())
            {
                var bp = c?.Blueprint; if (bp == null || bp.Caliber <= cal) continue;
                var prof = new Sprocket.Vehicles.ShellProfile(bp.PropellantLength, bp.Caliber);
                cal = bp.Caliber; mass = prof.ProjectileMass; speed = c.muzzleVelocity; k = bp.PenetratorConstant;
            }
            return cal > 0 && mass > 0 && speed > 1f;
        }

        /// <summary>Sandbox: fires a shell from the camera through the mouse cursor at the tank being edited.</summary>
        void FireTestShell(Shells.ShellType kind)
        {
            if (!MainGun(out float mass, out int cal, out float speed, out ushort k)) { MPLog.Warn("Test shell: no gun on this vehicle"); return; }
            // the edited tank: the root of the scene's vehicle components (the designer has no VehicleBehaviour)
            GameObject root = null; int comps = 0;
            foreach (var v in UnityEngine.Object.FindObjectsOfType<Sprocket.Vehicles.VehicleBehaviour>()) if (v != null) { root = v.gameObject; break; }
            if (root == null)
            {
                var all = UnityEngine.Object.FindObjectsOfType<Sprocket.Vehicles.VehicleComponent>();
                comps = all.Length;
                Transform from = null;
                foreach (var vc in all) if (vc != null && vc.TryCast<Sprocket.Vehicles.Cannons.Cannon>() != null) { from = vc.transform; break; }
                if (from == null && comps > 0) from = all[0].transform;
                // lowest ancestor that contains every vehicle component = the vehicle's root object
                for (var t = from; t != null; t = t.parent)
                    if (t.GetComponentsInChildren<Sprocket.Vehicles.VehicleComponent>(true).Length >= comps) { root = t.gameObject; break; }
            }
            var allCs = UnityEngine.Object.FindObjectsOfType<Sprocket.DamageModelling.CompoundStructure>();
            MPLog.Info($"Test shell: vehicle {(root != null ? root.name : "none")}, {comps} components seen, {allCs.Length} damage models in scene");
            if (root == null) { MPLog.Warn("Test shell: no vehicle in the scene"); return; }
            var cs = root.GetComponentInChildren<Sprocket.DamageModelling.CompoundStructure>(true);
            if (cs == null && allCs.Length > 0) cs = allCs[0];
            if (cs == null) cs = BuildTestDamageModel(root);
            if (cs == null) { MPLog.Warn("Test shell: the vehicle has no damage model here"); return; }
            // the camera actually drawing the designer view (highest depth on screen), not necessarily Camera.main
            Camera cam = null;
            foreach (var c in Camera.allCameras) if (c != null && c.enabled && c.targetTexture == null && c.gameObject.activeInHierarchy && (cam == null || c.depth > cam.depth)) cam = c;
            if (cam == null) cam = Camera.main;
            var mouse = Mouse.current;
            if (cam == null || mouse == null) return;
            var ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            MPLog.Info($"Test shell: camera '{cam.name}' ray {ray.origin} -> {ray.direction}");
            // start well outside the hull (10 m before whatever the cursor is on) so the shell meets the exterior armour first
            var start = (Physics.Raycast(ray, out var hit, 1000f) ? hit.point : ray.origin + ray.direction * 10f) - ray.direction * 10f;
            var vbh = root.GetComponent<Sprocket.Vehicles.VehicleBehaviour>();
            var rig = vbh != null ? new VehicleRig(vbh) : new VehicleRig(root);
            if (vbh != null) try { rig.ResolveCrew(); } catch (Exception e) { MPLog.Debug("test rig: " + e.Message); }
            PenCamView.Invalidate(rig);    // the design may have changed since the last test
            var info = new Sprocket.DamageModelling.PenetratorInfo(mass, cal / 1000f, 7850f, 0f, k, start, ray.direction * speed);
            MPLog.Info($"Test {Shells.ShellConfig.Name(kind)}: {cal} mm, {mass:F2} kg at {speed:F0} m/s");
            PenCapture.TestFire(cs, rig, info, kind);
        }

        /// <summary>Penetration of each shell type for the vehicle's main gun (largest calibre cannon in the designer).</summary>
        static string ShellInfoText()
        {
            float ap = -1f, mass = -1f; int cal = 0;
            try
            {
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Sprocket.Vehicles.Cannons.Cannon>())
                {
                    var bp = c?.Blueprint; if (bp == null || bp.Caliber <= cal) continue;
                    var prof = new Sprocket.Vehicles.ShellProfile(bp.PropellantLength, bp.Caliber);
                    float v = c.muzzleVelocity;
                    cal = bp.Caliber; mass = prof.ProjectileMass; ap = v > 1f ? prof.GetPenetration(v, bp.PenetratorConstant) : -1f;
                }
            }
            catch (Exception e) { MPLog.Debug("shell info: " + e.Message); }
            var A = Shells.ShellType.APHE; var H = Shells.ShellType.HE;
            float fa = Shells.ShellConfig.Filler(A), fh = Shells.ShellConfig.Filler(H);
            string capA = "", capH = "";
            if (mass > 0)
            {
                float ma = Shells.ShellConfig.MaxFiller(A, mass), mh = Shells.ShellConfig.MaxFiller(H, mass);
                if (fa > ma) { capA = $" <color=#f96>(max {ma:F0} g)</color>"; fa = ma; }
                if (fh > mh) { capH = $" <color=#f96>(max {mh:F0} g)</color>"; fh = mh; }
            }
            string Pen(Shells.ShellType t, float g) => ap > 0 ? $"{Shells.ShellConfig.Penetration(t, ap, g):F0} mm" : $"{Shells.ShellConfig.PenFactor(t, 100f, g) * 100f:F0}% of AP";
            string head = cal > 0 ? $"<color=#9aa>{cal} mm gun, {mass:F1} kg shell, max filler APHE {Shells.ShellConfig.MaxFiller(A, mass):F0} g / HE {Shells.ShellConfig.MaxFiller(H, mass):F0} g</color>\n" : "<color=#9aa>no gun found</color>\n";
            return head +
                $"<b>AP</b>  {Pen(Shells.ShellType.AP, 0)}\n" +
                $"<b>APHE</b>  {Pen(A, fa)}, {fa:F0} g{capA}, fuse {Shells.ShellStore.Active.FuseSens:F0} mm / {Shells.ShellStore.Active.FuseDist:F2} m\n" +
                $"<b>HE</b>  frags {Pen(H, fh)}, {fh:F0} g{capH}, explodes on impact\n" +
                $"<b>APDS</b>  {Pen(Shells.ShellType.APDS, 0)}, narrower spall cone";
        }

        void Commit() { foreach (var f in _fields) f.Commit?.Invoke(f.Value); }

        bool AnyFieldFocused() { foreach (var f in _fields) { try { if (f.Input != null && f.Input.isFocused) return true; } catch { } } return false; }

        GameObject Panel(Transform parent, string name, Vector2 pos, Vector2 size, Color c)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            var img = go.AddComponent<Image>(); img.color = c;
            return go;
        }

        TextMeshProUGUI Label(Transform parent, Vector2 pos, Vector2 size, string text, float fs)
        {
            var go = new GameObject("Label"); go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            var t = go.AddComponent<TextMeshProUGUI>();
            StyleText(t, fs, TextAlignmentOptions.Left);
            t.text = text;
            return t;
        }

        void StyleText(TextMeshProUGUI t, float fs, TextAlignmentOptions align)
        {
            t.font = _font; t.fontSize = fs; t.alignment = align; t.color = Color.white; t.richText = true;
            t.raycastTarget = false;
            t.text = "";
        }

        Button MakeButton(Transform parent, Vector2 pos, Vector2 size, string text, Color c, Action onClick)
        {
            var go = Panel(parent, "Btn", pos, size, c);
            var lbl = new GameObject("Text"); lbl.transform.SetParent(go.transform, false);
            var rt = lbl.AddComponent<RectTransform>(); rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.sizeDelta = Vector2.zero;
            var t = lbl.AddComponent<TextMeshProUGUI>(); StyleText(t, 17, TextAlignmentOptions.Center); t.text = "<b>" + text + "</b>";
            var b = new Button { Rect = go.GetComponent<RectTransform>(), Img = go.GetComponent<Image>(), Normal = c, OnClick = onClick, Label = t };
            _buttons.Add(b);
            return b;
        }

        Field MakeField(Transform parent, Vector2 pos, Vector2 size, string value, int maxLen, bool numbers, Action<string> commit)
        {
            // a real TMP_InputField: native text entry (any keyboard layout, IME, paste) with no managed delegates
            var go = Panel(parent, "Field", pos, size, FieldCol);
            var area = new GameObject("TextArea"); area.transform.SetParent(go.transform, false);
            var art = area.AddComponent<RectTransform>(); art.anchorMin = Vector2.zero; art.anchorMax = Vector2.one; art.offsetMin = new Vector2(8, 2); art.offsetMax = new Vector2(-8, -2);
            area.AddComponent<RectMask2D>();
            var txtGo = new GameObject("Text"); txtGo.transform.SetParent(area.transform, false);
            var trt = txtGo.AddComponent<RectTransform>(); trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
            var t = txtGo.AddComponent<TextMeshProUGUI>(); StyleText(t, 18, TextAlignmentOptions.Left);
            t.raycastTarget = false;
            var input = go.AddComponent<TMP_InputField>();
            input.textViewport = art;
            input.textComponent = t;
            input.fontAsset = _font;
            input.pointSize = 18;
            input.characterLimit = maxLen;
            input.richText = false;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.contentType = numbers ? TMP_InputField.ContentType.IntegerNumber : TMP_InputField.ContentType.Standard;
            input.customCaretColor = true; input.caretColor = Color.white;
            input.selectionColor = new Color(0.4f, 0.6f, 1f, 0.5f);
            input.text = value ?? "";
            var f = new Field { Rect = go.GetComponent<RectTransform>(), Input = input, Commit = commit };
            _fields.Add(f);
            return f;
        }

        // ------------------------------------------------------------------------------------- input
        void HandleMouse()
        {
            var mouse = Mouse.current; if (mouse == null) return;
            Vector2 mp = mouse.position.ReadValue();
            bool click = mouse.leftButton.wasPressedThisFrame;
            foreach (var b in _buttons)
            {
                if (b.Rect == null || !b.Rect.gameObject.activeInHierarchy) continue;
                bool over = RectTransformUtility.RectangleContainsScreenPoint(b.Rect, mp, null);
                b.Img.color = over ? Color.Lerp(b.Normal, Color.white, 0.25f) : b.Normal;
                if (over && click) { try { b.OnClick?.Invoke(); } catch (Exception e) { MPLog.Error("Button: " + e); } return; }
            }
            if (!click) return;
            foreach (var f in _fields)
            {
                try
                {
                    if (f.Rect != null && f.Rect.gameObject.activeInHierarchy && RectTransformUtility.RectangleContainsScreenPoint(f.Rect, mp, null) && !f.Input.isFocused)
                        f.Input.ActivateInputField();
                }
                catch (Exception e) { MPLog.Warn("field focus: " + e.Message); }
            }
        }

        void PollFields()
        {
            foreach (var f in _fields)
            {
                bool foc = false;
                try { foc = f.Input.isFocused; } catch { }
                if (f.WasFocused && !foc) f.Commit?.Invoke(f.Value);
                f.WasFocused = foc;
            }
        }

        void RefreshButtons()
        {
            bool active = Runner.Session.Active;
            _hostBtn.Rect.gameObject.SetActive(!active);
            _joinBtn.Rect.gameObject.SetActive(!active);
            _stopBtn.Label.text = Runner.Session.IsHost ? "<b>STOP HOSTING</b>" : "<b>DISCONNECT</b>";
            _stopBtn.Rect.gameObject.SetActive(active);
        }

        // ------------------------------------------------------------------------------------- text
        void RefreshText()
        {
            var s = Runner.Session; var bm = Runner.Battle;
            // HUD
            if (s.Active)
            {
                string h = $"<b>LAN {(s.IsHost ? "HOST" : "CLIENT")}</b>  {s.Players.Count + 1} players";
                if (s.IsHost && s.Players.Count > 0) h += "  |  ping " + string.Join(", ", s.Players.Values.OrderBy(p => p.Id).Select(p => $"{p.Name} {(p.PingMs >= 0 ? PingColor(p.PingMs) + p.PingMs + "ms</color>" : "--")}"));
                if (!s.IsHost) h += s.LocalPingMs >= 0 ? $"  |  ping <b>{PingColor(s.LocalPingMs)}{s.LocalPingMs} ms</color></b>" : "  |  ping --";
                if (bm.InBattle) h += $"  |  puppets {bm.PuppetByPlayer.Count}/{s.Players.Values.Count(p => p.InBattle)}  |  shots out {bm.ShotsSent} / in {bm.ShotsReceived}";
                if (!_visible) h += "   <color=#9aa>[F10] menu</color>";
                _hudText.text = h;
            }
            else _hudText.text = _visible ? "" : "<color=#9aa>Multiplayer: [F10]</color>";
            if (bm.InBattle) _hudText.text += "\n" + Shells.ShellSelect.HudText();
            if (bm.Repairing) _hudText.text += $"\n<color=#9fd><b>REPAIRING {bm.RepairRemaining:F1}s</b> - hold position ([{Plugin.RepairKey.Value}] cancel)</color>";

            if (!_visible) return;
            var sb = new StringBuilder();
            sb.AppendLine("<b>Status:</b> " + s.Status);
            sb.AppendLine(bm.InBattle ? $"<b>Battle:</b> driving '{bm.LocalVehicleName}'{(bm.SpawnApplied ? " (spawn aligned)" : "")}" : "<b>Battle:</b> not in a battle");
            if (s.Active)
            {
                sb.AppendLine();
                sb.AppendLine("<b>Players</b>");
                sb.AppendLine($" [{(s.IsHost ? 0 : s.LocalId)}] {Plugin.EffectiveName} <color=#8c8>(you)</color> - {(bm.InBattle ? bm.LocalVehicleName : "in menus")}");
                foreach (var p in s.Players.Values.OrderBy(p => p.Id))
                {
                    string where = p.InBattle ? $"'{p.Vehicle}'" : "in menus";
                    string puppet = "";
                    if (bm.InBattle && p.InBattle)
                    {
                        if (bm.PuppetByPlayer.TryGetValue(p.Id, out var r)) puppet = Time.time - p.LastStateAt < 3 ? " <color=#8c8>linked</color>" : " <color=#cc8>linked, no data</color>";
                        else puppet = " <color=#e77>NO MATCHING AI TANK</color>";
                    }
                    string ping = p.PingMs >= 0 ? $" {PingColor(p.PingMs)}{p.PingMs}ms</color>" : "";
                    sb.AppendLine($" [{p.Id}] {p.Name}{(p.Id == 0 ? " (host)" : "")}{ping} - {where}{puppet}");
                }
            }
            sb.AppendLine();
            sb.AppendLine("<color=#bbb><b>How to play</b>");
            sb.AppendLine("1. One player clicks HOST GAME, the others type the host's IP and click JOIN.");
            sb.AppendLine("2. Each player opens Custom Battle, puts THEIR tank on their own team and every other player's tank (same design file) on the AI team, then starts.");
            sb.AppendLine($"3. Enemy AI tanks become the other players. [F10] menu, [F11] diagnostics, [{Plugin.RepairKey.Value}] field repair.</color>");
            if (!string.IsNullOrEmpty(bm.LastEvent)) sb.AppendLine("\n<color=#9aa>Last: " + bm.LastEvent + "</color>");
            _status.text = sb.ToString();
        }

        static string PingColor(int ms) => ms < 60 ? "<color=#8c8>" : ms < 150 ? "<color=#dd8>" : "<color=#e77>";

        void RefreshDeath()
        {
            var bm = Runner.Battle;
            bool show = bm.LocalDestroyed;
            if (_death.activeSelf != show) _death.SetActive(show);
            if (!show) return;
            string t = $"<color=#FF2A2A><b>{bm.DeathReason}</b></color>";
            if (!string.IsNullOrEmpty(bm.DeathKiller)) t += $"\n<size=50%><color=#FFB0A8>destroyed by {bm.DeathKiller}</color></size>";
            t += "\n<size=35%><color=#ddd>Esc > Reset to respawn</color></size>";
            _deathText.text = t;
        }

        void RefreshFeed()
        {
            var feed = Runner.Battle.KillFeed;
            _feed.SetActive(!_visible && feed.Count > 0);
            if (_visible || feed.Count == 0) return;
            float now = Time.time;
            var sb = new StringBuilder();
            foreach (var e in feed)
            {
                float age = now - e.Time;
                if (age > 10f || age < 0) continue;
                int alpha = (int)(255 * Mathf.Clamp01(10f - age));
                sb.Append($"<alpha=#{alpha:X2}>").AppendLine(e.Text);
            }
            _feedText.text = sb.ToString();
        }

        static TMP_FontAsset FindFont()
        {
            try { var f = TMP_Settings.defaultFontAsset; if (f != null) return f; } catch { }
            try
            {
                var all = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                if (all != null) foreach (var f in all) if (f != null) return f;
            }
            catch { }
            return null;
        }
    }
}
