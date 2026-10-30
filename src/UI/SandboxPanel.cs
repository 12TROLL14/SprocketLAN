using System;
using System.Collections.Generic;
using HarmonyLib;
using Sprocket.VehicleDesigner.ArmourEditing;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketMP.UI
{
    /// <summary>
    /// The designer's armour inspector in "Sample" mode fires the game's own penetration simulation on click. With the
    /// mod's shells on, a click fires the mod's shell instead (type, calibre, penetration, filler from our panel next to
    /// the inspector): the result shows in the pen cam and its path is drawn on the tank in place of the game's line.
    /// </summary>
    public static class SandboxShells
    {
        public static bool Enabled = true;
        public static int Kind = 0;                       // index into Kinds
        public static float Calibre = 75f, Penetration = 150f, ApheFiller = 100f, HeFiller = 650f, FuseSens = -1f, FuseDist = -1f;   // fuse -1: the config default
        public static readonly (string name, Shells.ShellType t, bool cap)[] Kinds =
        {
            ("AP", Shells.ShellType.AP, false), ("APC", Shells.ShellType.AP, true), ("APHE", Shells.ShellType.APHE, false), ("APHEC", Shells.ShellType.APHE, true),
            ("HE", Shells.ShellType.HE, false), ("APDS", Shells.ShellType.APDS, false), ("APFSDS", Shells.ShellType.APFSDS, false), ("HEAT", Shells.ShellType.HEAT, false),
        };

        static float _opSeen = -10f; static bool _sampling; public static ushort PenConst = 1;
        public static Ray? PendingRay;
        public static Sprocket.Vehicles.IDamageModelSource Source;   // the inspector's own damage model (hull + turret)
        public static Ray LastRay; static int _rayAt = -1;
        /// <summary>The ray under the mouse: the inspector's own if it updated this frame or the last, else the camera's.</summary>
        public static Ray MouseRay()
        {
            if (Time.frameCount - _rayAt <= 1) return LastRay;
            Camera cam = null;
            foreach (var c in Camera.allCameras) if (c != null && c.enabled && c.targetTexture == null && c.gameObject.activeInHierarchy && (cam == null || c.depth > cam.depth)) cam = c;
            if (cam == null) cam = Camera.main;
            var m = Mouse.current;
            return cam != null && m != null ? cam.ScreenPointToRay(m.position.ReadValue()) : LastRay;
        }
        public static bool OverOurPanel;
        public static readonly List<(Vector3 a, Vector3 b, Color c)> Lines = new List<(Vector3, Vector3, Color)>();

        /// <summary>The armour inspector is open in Sample mode (seen this moment).</summary>
        public static bool Sampling => _sampling && Time.realtimeSinceStartup - _opSeen < 0.4f;

        public static void Patch(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(ArmourOverlayPointerOperator), nameof(ArmourOverlayPointerOperator.Update)), postfix: new HarmonyMethod(typeof(SandboxShells), nameof(Update_Postfix)));
                h.Patch(AccessTools.Method(typeof(ArmourOverlayPointerOperator), nameof(ArmourOverlayPointerOperator.DrawGizmos)), prefix: new HarmonyMethod(typeof(SandboxShells), nameof(DrawGizmos_Prefix)));
                MPLog.Info("Sandbox armour inspector hooks: ok");
            }
            catch (Exception e) { MPLog.Warn("Sandbox armour inspector hooks: " + e.Message); }
        }

        static void Update_Postfix(ArmourOverlayPointerOperator __instance)
        {
            try
            {
                _opSeen = Time.realtimeSinceStartup;
                var ov = __instance.overlay?.TryCast<ArmourOverlay>();
                _sampling = ov != null && ov.Sampling;
                if (ov != null) PenConst = ov.PenetratorConstant > 0 ? ov.PenetratorConstant : PenConst;
                Source = __instance.structure;
                LastRay = __instance.sampleRay; _rayAt = Time.frameCount;
            }
            catch (Exception e) { MPLog.Debug("sandbox op: " + e.Message); }
        }

        static bool DrawGizmos_Prefix(Sprocket.GizmoRendering.IGizmoDrawer gizmos)
        {
            if (!Enabled || !_sampling) return true;
            try
            {
                var ed = gizmos?.TryCast<Sprocket.GizmoRendering.IEdgeDrawer>();
                if (ed == null) return true;
                foreach (var (a, b, c) in Lines)
                    Sprocket.GizmoRendering.GizmoDrawerExtentions.DrawSplitLine(ed, a, b, Vector3.Distance(a, b) + 1f, c, c);
                return false;   // the game's own simulation line would show the native shell, not ours
            }
            catch (Exception e) { MPLog.Debug("sandbox gizmos: " + e.Message); return true; }
        }

        public static Shells.ShellStore.Design SandboxDesign()
        {
            var d = Shells.ShellStore.Defaults();
            var k = Kinds[Kind];
            d.ApheFiller = ApheFiller; d.HeFiller = HeFiller;
            if (FuseSens >= 0f) d.FuseSens = FuseSens; if (FuseDist >= 0f) d.FuseDist = FuseDist;
            d.CapAP = k.cap && k.t == Shells.ShellType.AP; d.CapAPHE = k.cap && k.t == Shells.ShellType.APHE;
            return d;
        }
        public static float Mass() { try { return new Sprocket.Vehicles.ShellProfile((ushort)Mathf.Clamp(Calibre * 5f, 10f, 5000f), (ushort)Mathf.Clamp(Calibre, 5f, 500f)).ProjectileMass; } catch { return 7850f * Mathf.PI / 4f * Mathf.Pow(Calibre / 1000f, 2) * Calibre / 1000f * 3.5f * 0.6f; } }
    }

    public partial class MenuUI
    {
        GameObject _sbPanel; TextMeshProUGUI _sbInfo; Button _sbToggle; readonly List<Button> _sbKinds = new List<Button>();
        Field _sbCal, _sbPen, _sbFill, _sbFuseS, _sbFuseD; TextMeshProUGUI _sbFillLbl, _sbFuseSL, _sbFuseDL; float _nextSbUi;

        void BuildSandboxPanel()
        {
            _sbPanel = Panel(_root.transform, "SandboxShells", Vector2.zero, new Vector2(440, 268), PanelCol);
            var rt = _sbPanel.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1, 0);
            rt.anchoredPosition = new Vector2(-14, 400);
            var t = _sbPanel.transform; float y = -10;
            _sbToggle = MakeButton(t, new Vector2(12, y), new Vector2(416, 30), "", BtnCol, () => { SandboxShells.Enabled = !SandboxShells.Enabled; _nextSbUi = 0; });
            y -= 38;
            for (int i = 0; i < SandboxShells.Kinds.Length; i++)
            {
                int k = i;
                var b = MakeButton(t, new Vector2(12 + (i % 4) * 105, y - (i / 4) * 32), new Vector2(100, 28), SandboxShells.Kinds[i].name, FieldCol, () => { SandboxShells.Kind = k; _nextSbUi = 0; });
                b.Label.fontSize = 15; _sbKinds.Add(b);
            }
            y -= 72;
            Label(t, new Vector2(12, y), new Vector2(150, 28), "Calibre (mm)", 16);
            _sbCal = MakeField(t, new Vector2(160, y), new Vector2(90, 28), SandboxShells.Calibre.ToString("0"), 5, false, v => { if (TryF(v, out var x)) SandboxShells.Calibre = Mathf.Clamp(x, 5f, 500f); _nextSbUi = 0; });
            y -= 32;
            Label(t, new Vector2(12, y), new Vector2(150, 28), "AP penetration (mm)", 16);
            _sbPen = MakeField(t, new Vector2(160, y), new Vector2(90, 28), SandboxShells.Penetration.ToString("0"), 5, false, v => { if (TryF(v, out var x)) SandboxShells.Penetration = Mathf.Clamp(x, 1f, 3000f); _nextSbUi = 0; });
            y -= 32;
            _sbFuseSL = Label(t, new Vector2(262, y + 64), new Vector2(100, 28), "Fuse (mm)", 15);
            _sbFuseS = MakeField(t, new Vector2(352, y + 64), new Vector2(76, 28), "", 5, false, v => { if (TryF(v, out var x)) SandboxShells.FuseSens = Mathf.Clamp(x, 0f, 500f); _nextSbUi = 0; });
            _sbFuseDL = Label(t, new Vector2(262, y + 32), new Vector2(100, 28), "Delay (m)", 15);
            _sbFuseD = MakeField(t, new Vector2(352, y + 32), new Vector2(76, 28), "", 5, false, v => { if (TryF(v, out var x)) SandboxShells.FuseDist = Mathf.Clamp(x, 0.05f, 10f); _nextSbUi = 0; });
            _sbFillLbl = Label(t, new Vector2(12, y), new Vector2(150, 28), "Filler (g)", 16);
            _sbFill = MakeField(t, new Vector2(160, y), new Vector2(90, 28), "", 6, false, v =>
            {
                if (!TryF(v, out var x)) return;
                var k = SandboxShells.Kinds[SandboxShells.Kind].t;
                if (k == Shells.ShellType.HE) SandboxShells.HeFiller = Mathf.Clamp(x, 0f, 20000f); else SandboxShells.ApheFiller = Mathf.Clamp(x, 0f, 5000f);
                _nextSbUi = 0;
            });
            y -= 34;
            var go = new GameObject("Info"); go.transform.SetParent(t, false);
            var irt = go.AddComponent<RectTransform>(); irt.anchorMin = irt.anchorMax = irt.pivot = new Vector2(0, 1);
            irt.anchoredPosition = new Vector2(12, y); irt.sizeDelta = new Vector2(416, 268 + y - 6);
            _sbInfo = go.AddComponent<TextMeshProUGUI>(); StyleText(_sbInfo, 14, TextAlignmentOptions.TopLeft); _sbInfo.enableWordWrapping = true;
            _sbPanel.SetActive(false);
        }

        bool _sbOn;
        void UpdateSandboxPanel()
        {
            if (_sbPanel == null) return;
            bool on = SandboxShells.Sampling && !Runner.Battle.InBattle;
            if (on != _sbOn) { _sbOn = on; _sbPanel.SetActive(on); if (!on) { Commit(); SandboxShells.Lines.Clear(); } }
            if (!on) { SandboxShells.OverOurPanel = false; SandboxShells.PendingRay = null; return; }
            var m = Mouse.current;
            SandboxShells.OverOurPanel = m != null && RectTransformUtility.RectangleContainsScreenPoint(_sbPanel.GetComponent<RectTransform>(), m.position.ReadValue(), null);

            // a click on the tank (the shot is taken if the ray meets its damage model; our panel is excluded)
            if (SandboxShells.Enabled && m != null && m.leftButton.wasPressedThisFrame && !SandboxShells.OverOurPanel && !AnyFieldFocused())
            {
                var ray = SandboxShells.MouseRay();
                Commit();
                try { FireSandboxShell(ray); } catch (Exception e) { MPLog.Warn("Sandbox shell: " + e); }
            }

            if (Time.realtimeSinceStartup < _nextSbUi) return;
            _nextSbUi = Time.realtimeSinceStartup + 0.25f;
            var kind = SandboxShells.Kinds[SandboxShells.Kind];
            _sbToggle.Label.text = SandboxShells.Enabled ? "<b>MOD SHELLS: ON</b> <size=13>(click the tank)</size>" : "<b>MOD SHELLS: OFF</b> <size=13>(the game's own simulation)</size>";
            _sbToggle.Normal = SandboxShells.Enabled ? new Color(0.2f, 0.5f, 0.25f, 1f) : new Color(0.3f, 0.3f, 0.3f, 1f);
            for (int i = 0; i < _sbKinds.Count; i++) _sbKinds[i].Normal = i == SandboxShells.Kind ? new Color(0.75f, 0.55f, 0.15f, 1f) : FieldCol;
            bool he = kind.t == Shells.ShellType.HE, aphe = kind.t == Shells.ShellType.APHE;
            _sbFill.Rect.gameObject.SetActive(he || aphe); _sbFillLbl.gameObject.SetActive(he || aphe);
            _sbFuseS.Rect.gameObject.SetActive(aphe); _sbFuseSL.gameObject.SetActive(aphe); _sbFuseD.Rect.gameObject.SetActive(aphe); _sbFuseDL.gameObject.SetActive(aphe);
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (!_sbFuseS.Input.isFocused) _sbFuseS.Input.text = (SandboxShells.FuseSens >= 0f ? SandboxShells.FuseSens : Shells.ShellConfig.FuseSensitivity.Value).ToString("0.#", ci);
            if (!_sbFuseD.Input.isFocused) _sbFuseD.Input.text = (SandboxShells.FuseDist >= 0f ? SandboxShells.FuseDist : Shells.ShellConfig.FuseDistance.Value).ToString("0.##", ci);
            bool foc = false; try { foc = _sbFill.Input.isFocused; } catch { }
            if (!foc) _sbFill.Input.text = (he ? SandboxShells.HeFiller : SandboxShells.ApheFiller).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            _sbInfo.text = SandboxInfo();
        }

        static string SandboxInfo()
        {
            var kind = SandboxShells.Kinds[SandboxShells.Kind];
            float mass = SandboxShells.Mass();
            float filler = kind.t == Shells.ShellType.HE ? SandboxShells.HeFiller : kind.t == Shells.ShellType.APHE ? SandboxShells.ApheFiller : 0f;
            float max = kind.t == Shells.ShellType.HE || kind.t == Shells.ShellType.APHE ? Shells.ShellConfig.MaxFiller(kind.t, mass) : 0f;
            float used = Mathf.Min(filler, max);
            Shells.ShellStore.Override = SandboxShells.SandboxDesign();
            float pen;
            try { pen = kind.t == Shells.ShellType.HE ? Shells.ShellConfig.HEPen(used / 1000f) : Shells.ShellConfig.Penetration(kind.t, SandboxShells.Penetration, used); }
            finally { Shells.ShellStore.Override = null; }
            string s = $"<color=#9aa>{SandboxShells.Calibre:0} mm shell, {mass:0.##} kg</color>\n<b>{kind.name}</b>: <color=#fd8>{pen:0} mm</color> {(kind.t == Shells.ShellType.HE ? "fragment penetration" : "penetration")}";
            if (max > 0f) s += $", {used:0} g filler{(filler > max + 0.5f ? $" <color=#f96>(max {max:0} g for this shell)</color>" : "")}";
            if (kind.t == Shells.ShellType.APHE) s += $"\n<color=#9aa>{Shells.ShellConfig.APHEFragments(used)} burst fragments; fuse arms after {(SandboxShells.FuseSens >= 0f ? SandboxShells.FuseSens : Shells.ShellConfig.FuseSensitivity.Value):0} mm of armour, bursts {(SandboxShells.FuseDist >= 0f ? SandboxShells.FuseDist : Shells.ShellConfig.FuseDistance.Value):0.##} m later</color>";
            if (kind.t == Shells.ShellType.HE) s += $"\n<color=#9aa>{Shells.ShellConfig.HEFragments(used)} burst fragments</color>";
            return s;
        }

        /// <summary>Fires the panel's shell along the inspector's sample ray at the tank being edited.</summary>
        void FireSandboxShell(Ray ray)
        {
            var kind = SandboxShells.Kinds[SandboxShells.Kind];
            float cal = Mathf.Clamp(SandboxShells.Calibre, 5f, 500f), mass = SandboxShells.Mass();
            ushort k = SandboxShells.PenConst;
            float speed = Shells.Explosives.SpeedForPen(cal / 1000f, mass, k, SandboxShells.Penetration);
            var root = FindDesignerVehicle(out _);
            if (root == null) { MPLog.Warn("Sandbox shell: no vehicle in the scene"); return; }
            Sprocket.DamageModelling.CompoundStructure cs = null;
            try { cs = SandboxShells.Source?.DamageModel?.TryCast<Sprocket.DamageModelling.CompoundStructure>(); } catch { }
            if (cs == null) cs = root.GetComponentInChildren<Sprocket.DamageModelling.CompoundStructure>(true);
            if (cs == null) { MPLog.Warn("Sandbox shell: the inspector's damage model isn't available"); return; }
            if (_testDM != null) { UnityEngine.Object.Destroy(_testDM); _testDM = null; }   // an old copy built for the earlier test row (its crew showed in T pose)
            // where the ray meets the tank's armour model: the shell starts 3 m before it, so always outside
            var dir = ray.direction.normalized;
            float dist = -1f;
            try { if (SandboxShells.Source != null && SandboxShells.Source.RaycastDamageModel(ray.origin, dir, 2000f, out float d0)) dist = d0; } catch { }
            if (dist < 0f) { MPLog.Info("Sandbox shell: the click missed the tank"); return; }
            var start = ray.origin + dir * Mathf.Max(0f, dist - 3f);
            PenCamView.ExtraDm = cs;
            var vbh = root.GetComponent<Sprocket.Vehicles.VehicleBehaviour>();
            var rig = vbh != null ? new VehicleRig(vbh) : new VehicleRig(root);
            if (vbh != null) try { rig.ResolveCrew(); } catch { }
            PenCamView.Invalidate(rig);
            // sabot rounds (APDS / APFSDS) leave the barrel faster: the hit code takes that multiplier back out before working out
            // the gun's AP penetration, so it must be in the launch speed (it was missing: they penetrated far less than AP)
            float vMult = Mathf.Max(0.05f, Shells.ShellConfig.VelocityMult(kind.t));
            var info = new Sprocket.DamageModelling.PenetratorInfo(mass, cal / 1000f, 7850f, 0f, k, start, dir * speed * vMult);
            MPLog.Info($"Sandbox {kind.name}: {cal:0} mm, {mass:0.##} kg at {speed:0} m/s ({SandboxShells.Penetration:0} mm AP), penetrator constant {k}");
            Shells.ShellStore.Override = SandboxShells.SandboxDesign();
            try { PenCapture.TestFire(cs, rig, info, kind.t); }
            finally { Shells.ShellStore.Override = null; }
            // the path on the tank, in place of the game's line
            SandboxShells.Lines.Clear();
            var rec = Runner.Battle.LatestPenCam;
            if (rec == null || rec.Victim != PenCapture.TestVictimId || rig.GO == null || Time.time - rec.ReceivedAt > 1f) return;   // this shot's record only
            Runner.Battle.LatestPenCam = null;   // shown on the tank itself, no pen cam
            var tr = rig.GO.transform;
            foreach (var f in rec.Frags)
            {
                float len = Mathf.Min(f.Length, 10f); if (len < 0.01f) continue;
                if (!f.Original && (f.Flags & (64 | 128)) == 0) continue;
                var col = f.Original ? new Color(0.35f, 1f, 0.35f, 1f) : (f.Flags & 128) != 0 ? new Color(1f, 0.9f, 0.2f, 1f) : kind.t == Shells.ShellType.HE && (f.Flags & 2) == 0 ? new Color(1f, 0.5f, 0.1f, 1f) : new Color(0.8f, 0.3f, 1f, 1f);
                var a = tr.TransformPoint(f.Start);
                SandboxShells.Lines.Add((a, a + tr.TransformDirection(f.Dir).normalized * len, col));
                if (SandboxShells.Lines.Count > 400) break;
            }
        }
    }
}
