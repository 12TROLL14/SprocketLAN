using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using SprocketMP.Shells;

namespace SprocketMP.UI
{
    /// <summary>
    /// In-game settings panel (F7): every entry of SprocketLAN.cfg, grouped by section, with sliders / toggles / text
    /// fields and a live diagram for the settings that have a geometric meaning (spall cones, exit turn, ricochet
    /// angles, overmatch, HE burst offsets, APHE fuse, HEAT jet loss). Changes apply at once and are saved.
    /// </summary>
    public partial class MenuUI
    {
        class CfgRow
        {
            public GameObject Root; public Button Name, Toggle, Reset; public Field Text;
            public RectTransform Track, Fill, Handle; public ConfigEntryBase Entry; public float Min, Max; public bool HasSlider;
        }

        GameObject _cfgPanel; bool _cfgOn; string _cfgKeyName; Key _cfgKey = Key.None;
        readonly List<string> _cfgSections = new List<string>();
        readonly Dictionary<string, List<ConfigEntryBase>> _cfgBySection = new Dictionary<string, List<ConfigEntryBase>>();
        readonly List<Button> _cfgTabs = new List<Button>();
        readonly List<CfgRow> _cfgRows = new List<CfgRow>();
        string _cfgSection; int _cfgPage; ConfigEntryBase _cfgShown, _cfgSelected; CfgRow _cfgDrag;
        TextMeshProUGUI _cfgDesc, _cfgPageLbl, _cfgTitle;
        RawImage _cfgImg; Texture2D _cfgTex; Color32[] _px; string _drawnSig;
        readonly List<TextMeshProUGUI> _cfgLabels = new List<TextMeshProUGUI>(); int _cfgLabelUsed;
        float _cfgNextSave = -1f, _cfgNextText;
        const int CfgRowsPerPage = 15, S = 384;

        ConfigFile Cfg => Plugin.Instance?.Config;

        void BuildConfigPanel()
        {
            var cfg = Cfg; if (cfg == null) return;
            _cfgSections.Clear(); _cfgBySection.Clear();
            foreach (var kv in cfg)
            {
                var sec = kv.Key.Section;
                if (!_cfgBySection.TryGetValue(sec, out var l)) { l = new List<ConfigEntryBase>(); _cfgBySection[sec] = l; _cfgSections.Add(sec); }
                l.Add(kv.Value);
            }
            // most-used sections first
            string[] pref = { "Armour", "Shells", "PenCam", "Stabiliser", "Repair", "Battle", "Combat", "Puppets" };
            _cfgSections.Sort((a, b) => { int ia = Array.IndexOf(pref, a), ib = Array.IndexOf(pref, b); if (ia < 0) ia = 99; if (ib < 0) ib = 99; return ia != ib ? ia.CompareTo(ib) : string.Compare(a, b, StringComparison.Ordinal); });

            _cfgPanel = Panel(_root.transform, "Settings", Vector2.zero, new Vector2(1300, 780), new Color(0.07f, 0.08f, 0.09f, 0.96f));
            var prt = _cfgPanel.GetComponent<RectTransform>(); prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f); prt.anchoredPosition = Vector2.zero;
            var t = _cfgPanel.transform;
            _cfgTitle = Label(t, new Vector2(16, -10), new Vector2(1000, 34), "", 24);
            MakeButton(t, new Vector2(1190, -12), new Vector2(96, 30), "CLOSE", BtnRed, ToggleConfigPanel);

            float y = -56;
            foreach (var sec in _cfgSections)
            {
                string s = sec;
                var b = MakeButton(t, new Vector2(16, y), new Vector2(190, 32), s, FieldCol, () => { _cfgSection = s; _cfgPage = 0; FillCfgRows(); });
                b.Label.fontSize = 16; _cfgTabs.Add(b); y -= 36;
            }

            y = -56;
            for (int i = 0; i < CfgRowsPerPage; i++)
            {
                var r = new CfgRow();
                r.Root = new GameObject("Row"); r.Root.transform.SetParent(t, false);
                var rrt = r.Root.AddComponent<RectTransform>(); rrt.anchorMin = rrt.anchorMax = rrt.pivot = new Vector2(0, 1); rrt.anchoredPosition = new Vector2(220, y); rrt.sizeDelta = new Vector2(660, 40);
                var row = r;
                r.Name = MakeButton(r.Root.transform, new Vector2(0, -2), new Vector2(270, 34), "", new Color(0.12f, 0.13f, 0.15f, 1f), () => { _cfgSelected = row.Entry; });
                r.Name.Label.alignment = TextAlignmentOptions.Left; r.Name.Label.margin = new Vector4(8, 0, 4, 0); r.Name.Label.fontSize = 14; r.Name.Label.enableWordWrapping = false; r.Name.Label.overflowMode = TextOverflowModes.Ellipsis;
                // slider
                var track = Panel(r.Root.transform, "Track", new Vector2(282, -15), new Vector2(200, 8), new Color(0.2f, 0.21f, 0.24f, 1f));
                r.Track = track.GetComponent<RectTransform>();
                var fill = Panel(track.transform, "Fill", Vector2.zero, new Vector2(0, 8), new Color(0.35f, 0.6f, 0.35f, 1f)); r.Fill = fill.GetComponent<RectTransform>();
                var handle = Panel(track.transform, "Handle", Vector2.zero, new Vector2(10, 22), new Color(0.85f, 0.88f, 0.9f, 1f)); r.Handle = handle.GetComponent<RectTransform>();
                r.Handle.pivot = new Vector2(0.5f, 0.5f);
                r.Text = MakeField(r.Root.transform, new Vector2(492, -4), new Vector2(110, 30), "", 120, false, v => CommitCfgText(row, v));
                r.Toggle = MakeButton(r.Root.transform, new Vector2(282, -4), new Vector2(200, 30), "", FieldCol, () => { if (row.Entry is ConfigEntry<bool> be) { be.Value = !be.Value; _cfgSelected = row.Entry; CfgChanged(); } });
                r.Reset = MakeButton(r.Root.transform, new Vector2(608, -4), new Vector2(50, 30), "<size=13>RESET</size>", new Color(0.28f, 0.24f, 0.18f, 1f), () => { if (row.Entry != null) { row.Entry.BoxedValue = row.Entry.DefaultValue; _cfgSelected = row.Entry; CfgChanged(); } });
                _cfgRows.Add(r); y -= 42;
            }
            MakeButton(t, new Vector2(220, y - 6), new Vector2(110, 30), "< PREV", BtnCol, () => { if (_cfgPage > 0) { _cfgPage--; FillCfgRows(); } });
            _cfgPageLbl = Label(t, new Vector2(340, y - 6), new Vector2(300, 30), "", 16);
            MakeButton(t, new Vector2(650, y - 6), new Vector2(110, 30), "NEXT >", BtnCol, () => { if ((_cfgPage + 1) * CfgRowsPerPage < CurList().Count) { _cfgPage++; FillCfgRows(); } });

            // diagram + description
            var imgGo = new GameObject("Diagram"); imgGo.transform.SetParent(t, false);
            var irt = imgGo.AddComponent<RectTransform>(); irt.anchorMin = irt.anchorMax = irt.pivot = new Vector2(0, 1); irt.anchoredPosition = new Vector2(896, -56); irt.sizeDelta = new Vector2(S, S);
            _cfgImg = imgGo.AddComponent<RawImage>(); _cfgImg.raycastTarget = false;
            _cfgTex = new Texture2D(S, S, TextureFormat.RGBA32, false); _cfgTex.filterMode = FilterMode.Bilinear; _cfgTex.wrapMode = TextureWrapMode.Clamp;
            UnityEngine.Object.DontDestroyOnLoad(_cfgTex);
            _cfgImg.texture = _cfgTex; _px = new Color32[S * S];
            for (int i = 0; i < 14; i++)
            {
                var lg = new GameObject("L"); lg.transform.SetParent(imgGo.transform, false);
                var lrt = lg.AddComponent<RectTransform>(); lrt.anchorMin = lrt.anchorMax = Vector2.zero; lrt.sizeDelta = new Vector2(240, 22);
                var tl = lg.AddComponent<TextMeshProUGUI>(); StyleText(tl, 14, TextAlignmentOptions.BottomLeft); tl.enableWordWrapping = false;
                _cfgLabels.Add(tl);
            }
            _cfgDesc = Label(t, new Vector2(896, -56 - S - 10), new Vector2(S, 300), "", 15);
            _cfgDesc.enableWordWrapping = true; _cfgDesc.alignment = TextAlignmentOptions.TopLeft;
            Label(t, new Vector2(16, -744), new Vector2(1270, 28), "<color=#9aa><size=14>Changes apply at once and are saved. Hits are worked out on the tank that is hit, so in multiplayer each player's own armour/shell settings decide the hits their tank takes. Key bindings and network settings apply the next time they are used.</size></color>", 14);

            _cfgSection = _cfgSections.Count > 0 ? _cfgSections[0] : null;
            _cfgPanel.SetActive(false);
        }

        List<ConfigEntryBase> CurList() => _cfgSection != null && _cfgBySection.TryGetValue(_cfgSection, out var l) ? l : new List<ConfigEntryBase>();

        void ToggleConfigPanel()
        {
            if (_cfgPanel == null) return;
            _cfgOn = !_cfgOn;
            _cfgPanel.SetActive(_cfgOn);
            var cfg = Cfg;
            if (_cfgOn) { _visible = false; if (_designOn) ToggleDesignPanel(); _cfgPanel.transform.SetAsLastSibling(); try { if (cfg != null) cfg.SaveOnConfigSet = false; } catch { } FillCfgRows(); _drawnSig = null; }
            else { Commit(); _cfgDrag = null; try { if (cfg != null) { cfg.Save(); cfg.SaveOnConfigSet = true; } } catch (Exception e) { MPLog.Warn("config save: " + e.Message); } _cfgNextSave = -1f; }
        }

        void CfgChanged() { _cfgNextSave = Time.realtimeSinceStartup + 1.5f; _cfgNextText = 0f; }

        void FillCfgRows()
        {
            var list = CurList();
            if (_cfgPage * CfgRowsPerPage >= list.Count) _cfgPage = 0;
            for (int i = 0; i < _cfgRows.Count; i++)
            {
                var r = _cfgRows[i]; int idx = _cfgPage * CfgRowsPerPage + i;
                if (idx >= list.Count) { r.Entry = null; r.Root.SetActive(false); continue; }
                var e = list[idx]; r.Entry = e; r.Root.SetActive(true);
                r.Name.Label.text = e.Definition.Key;
                bool isBool = e.SettingType == typeof(bool);
                bool num = e.SettingType == typeof(float) || e.SettingType == typeof(int);
                r.HasSlider = num && CfgRange(e, out r.Min, out r.Max);
                r.Toggle.Rect.gameObject.SetActive(isBool);
                r.Track.gameObject.SetActive(r.HasSlider);
                r.Text.Rect.gameObject.SetActive(!isBool);
                var frt = r.Text.Rect;
                if (num) { frt.anchoredPosition = new Vector2(492, -4); frt.sizeDelta = new Vector2(110, 30); }
                else { frt.anchoredPosition = new Vector2(282, -4); frt.sizeDelta = new Vector2(320, 30); }
                try { r.Text.Input.text = Fmt(e); } catch { }
            }
            int pages = Math.Max(1, (list.Count + CfgRowsPerPage - 1) / CfgRowsPerPage);
            _cfgPageLbl.text = $"<color=#9aa>page {_cfgPage + 1}/{pages}  ({list.Count} settings)</color>";
            foreach (var b in _cfgTabs) b.Normal = b.Label.text.Contains(">" + _cfgSection + "<") ? new Color(0.25f, 0.42f, 0.28f, 1f) : FieldCol;
            _cfgNextText = 0f;
        }

        static string Fmt(ConfigEntryBase e)
        {
            var v = e.BoxedValue;
            if (v is float f) return f.ToString("0.###", CultureInfo.InvariantCulture);
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        void CommitCfgText(CfgRow r, string v)
        {
            var e = r.Entry; if (e == null) return;
            try
            {
                object nv = null;
                if (e.SettingType == typeof(float)) { if (TryF(v, out var f)) nv = f; }
                else if (e.SettingType == typeof(int)) { if (int.TryParse((v ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) nv = n; else if (TryF(v, out var f2)) nv = Mathf.RoundToInt(f2); }
                else if (e.SettingType == typeof(string)) nv = v ?? "";
                else nv = TomlTypeConverter.ConvertToValue(v, e.SettingType);
                if (nv != null && !Equals(nv, e.BoxedValue)) { e.BoxedValue = nv; _cfgSelected = e; CfgChanged(); }
            }
            catch (Exception ex) { MPLog.Warn($"setting {e.Definition.Key}: {ex.Message}"); }
            try { r.Text.Input.text = Fmt(e); } catch { }
        }

        /// <summary>Slider range: the entry's own acceptable range, else a sensible one from its name and default.</summary>
        static bool CfgRange(ConfigEntryBase e, out float min, out float max)
        {
            min = 0f; max = 1f;
            var av = e.Description?.AcceptableValues;
            if (av != null)
            {
                var tp = av.GetType();
                var pmin = tp.GetProperty("MinValue"); var pmax = tp.GetProperty("MaxValue");
                if (pmin != null && pmax != null) { min = Convert.ToSingle(pmin.GetValue(av)); max = Convert.ToSingle(pmax.GetValue(av)); return max > min; }
                return false;
            }
            float d = Convert.ToSingle(e.DefaultValue, CultureInfo.InvariantCulture);
            string k = e.Definition.Key;
            if (k.Contains("Port") || k.Contains("SendRate")) return false;
            if (k.StartsWith("RicochetAngle") || k.Contains("HalfAngle") || k.Contains("SnapAngle")) { max = 90f; return true; }
            if (k.StartsWith("ExitNormalization")) { max = 45f; return true; }
            if (k == "OvermatchCalibreRatio") { min = 1f; max = 10f; return true; }
            if (d < 0f) { min = d * 3f; max = -d * 3f; return true; }
            if (e.SettingType == typeof(int)) { max = Mathf.Max(10f, d * 3f); return true; }
            if (d <= 1f) { max = 1f; return true; }
            max = d * 3f; return true;
        }

        void UpdateConfigPanel()
        {
            var kb = Keyboard.current;
            try
            {
                if (_cfgKeyName != Plugin.ConfigKey.Value) { _cfgKeyName = Plugin.ConfigKey.Value; if (!Enum.TryParse(_cfgKeyName, true, out _cfgKey)) _cfgKey = Key.None; }
                if (kb != null && _cfgKey != Key.None && kb[_cfgKey].wasPressedThisFrame && !AnyFieldFocused()) ToggleConfigPanel();
            }
            catch { }
            if (!_cfgOn || _cfgPanel == null) return;
            if (Runner.Battle.InBattle) { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; }
            _cfgTitle.text = $"<b>SETTINGS</b>  <color=#fd8>{_cfgSection}</color>   <size=15><color=#9aa>[{Plugin.ConfigKey.Value}] close - click a setting's name to see it explained</color></size>";

            var mouse = Mouse.current; Vector2 mp = mouse != null ? mouse.position.ReadValue() : Vector2.zero;
            // hover shows a setting's diagram; the clicked one stays when the mouse leaves
            ConfigEntryBase hover = null;
            foreach (var r in _cfgRows) if (r.Entry != null && r.Root.activeSelf && RectTransformUtility.RectangleContainsScreenPoint(r.Root.GetComponent<RectTransform>(), mp, null)) { hover = r.Entry; break; }
            // slider dragging
            if (mouse != null)
            {
                if (mouse.leftButton.wasPressedThisFrame)
                    foreach (var r in _cfgRows)
                        if (r.Entry != null && r.HasSlider && r.Track.gameObject.activeInHierarchy && Grab(r.Track, mp)) { _cfgDrag = r; _cfgSelected = r.Entry; break; }
                if (!mouse.leftButton.isPressed) _cfgDrag = null;
                if (_cfgDrag != null && _cfgDrag.Entry != null)
                {
                    RectTransformUtility.ScreenPointToLocalPointInRectangle(_cfgDrag.Track, mp, null, out var lp);
                    var rect = _cfgDrag.Track.rect;
                    float u = Mathf.Clamp01((lp.x - rect.xMin) / Mathf.Max(1f, rect.width));
                    float v = _cfgDrag.Min + (_cfgDrag.Max - _cfgDrag.Min) * u;
                    var e = _cfgDrag.Entry;
                    object nv;
                    if (e.SettingType == typeof(int)) nv = Mathf.RoundToInt(v);
                    else { float span = _cfgDrag.Max - _cfgDrag.Min, step = span <= 2f ? 0.01f : span <= 20f ? 0.05f : span <= 200f ? 0.5f : 1f; nv = Mathf.Round(v / step) * step; }
                    if (!Equals(nv, e.BoxedValue)) { e.BoxedValue = nv; CfgChanged(); }
                    hover = e;
                }
            }
            _cfgShown = hover ?? _cfgSelected ?? (CurList().Count > 0 ? CurList()[0] : null);

            if (Time.realtimeSinceStartup >= _cfgNextText)
            {
                _cfgNextText = Time.realtimeSinceStartup + 0.2f;
                foreach (var r in _cfgRows)
                {
                    var e = r.Entry; if (e == null) continue;
                    bool changed = !Equals(e.BoxedValue, e.DefaultValue);
                    bool sel = e == _cfgShown;
                    r.Name.Normal = sel ? new Color(0.2f, 0.27f, 0.36f, 1f) : new Color(0.12f, 0.13f, 0.15f, 1f);
                    r.Name.Label.text = (changed ? "<color=#fd8>" : "") + e.Definition.Key + (changed ? "</color>" : "");
                    r.Reset.Rect.gameObject.SetActive(changed);
                    if (e is ConfigEntry<bool> be) { r.Toggle.Label.text = be.Value ? "<b><color=#8f8>ON</color></b>" : "<b><color=#aaa>OFF</color></b>"; r.Toggle.Normal = be.Value ? new Color(0.22f, 0.36f, 0.22f, 1f) : FieldCol; }
                    bool foc = false; try { foc = r.Text.Input.isFocused; } catch { }
                    if (!foc) { var s = Fmt(e); try { if (r.Text.Input.text != s) r.Text.Input.text = s; } catch { } }
                    if (r.HasSlider)
                    {
                        float v = Convert.ToSingle(e.BoxedValue, CultureInfo.InvariantCulture), u = Mathf.Clamp01((v - r.Min) / (r.Max - r.Min));
                        r.Fill.sizeDelta = new Vector2(200f * u, 8f); r.Handle.anchoredPosition = new Vector2(200f * u, -4f);
                    }
                }
                foreach (var b in _cfgTabs) b.Normal = b.Label.text.Contains(">" + _cfgSection + "<") ? new Color(0.25f, 0.42f, 0.28f, 1f) : FieldCol;
                if (_cfgShown != null)
                {
                    var e = _cfgShown;
                    string range = "";
                    foreach (var r in _cfgRows) if (r.Entry == e && r.HasSlider) range = $"  <color=#9aa>slider {r.Min:0.##} - {r.Max:0.##} (type any value in the box)</color>";
                    _cfgDesc.text = $"<b><color=#fd8>{e.Definition.Key}</color></b>\n<size=14>{e.Description?.Description}</size>\n\n<size=13><color=#9aa>value</color> {Fmt(e)}   <color=#9aa>default</color> {Convert.ToString(e.DefaultValue, CultureInfo.InvariantCulture)}{range}</size>";
                }
            }
            if (_cfgShown != null) DrawDiagram(_cfgShown);
            if (_cfgNextSave > 0f && Time.realtimeSinceStartup >= _cfgNextSave) { _cfgNextSave = -1f; try { Cfg?.Save(); } catch { } }
        }

        static bool Grab(RectTransform track, Vector2 mp)
        {
            // a taller hit area than the 8 px bar
            RectTransformUtility.ScreenPointToLocalPointInRectangle(track, mp, null, out var lp);
            var r = track.rect;
            return lp.x >= r.xMin - 8 && lp.x <= r.xMax + 8 && lp.y >= r.yMin - 12 && lp.y <= r.yMax + 12;
        }

        // ======================================================================================== diagrams
        static readonly string[] ConeKeys = { "SpallConeHalfAngleDegrees", "APDSSpallConeNarrowing", "APFSDSConeNarrowing", "HEATConeNarrowing", "SpallFragmentsPer10mmCal", "APHESpallReduction", "APFSDSSpallIncrease" };
        static readonly string[] ExitKeys = { "ExitNormalizationAt60", "ExitNormalizationAt70", "APDSNormalizationReduction", "APFSDSNormalizationReduction", "APCapNormalizationReduction", "APHENormalisation" };
        static readonly string[] HeatLosKeys = { "HEATLosReductionAt60", "HEATLosReductionAt70" };

        void DrawDiagram(ConfigEntryBase e)
        {
            string k = e.Definition.Key;
            // redraw only when something it shows changed
            string sig = k + "|" + string.Join(",", Cfg.Values.Select(x => Convert.ToString(x.BoxedValue, CultureInfo.InvariantCulture)));
            if (sig == _drawnSig) return;
            _drawnSig = sig;
            _cfgLabelUsed = 0;
            Clear(new Color32(18, 20, 24, 255));
            try
            {
                if (Array.IndexOf(ConeKeys, k) >= 0) DrawCones(k);
                else if (Array.IndexOf(ExitKeys, k) >= 0) DrawExit(k);
                else if (k.StartsWith("RicochetAngle")) DrawRicochet(k);
                else if (k == "OvermatchCalibreRatio") DrawOvermatch();
                else if (k == "HEBurstBackAlongPath" || k == "HEBurstOffSurface") DrawHEBurst(k);
                else if (k == "APHEFuseDistance" || k == "APHEFuseSensitivityMm") DrawFuse(k);
                else if (k == "HEATJetLossPerMetre") DrawJetLoss();
                else if (Array.IndexOf(HeatLosKeys, k) >= 0) DrawHeatLos(k);
                else if (k == "RicochetPenetrationLoss") DrawRicoLoss();
                else if (k == "HEPenetrationCurve") DrawPoints(ShellConfig.ParsePoints(ShellConfig.HEPenCurve.Value), x => ShellConfig.HEPen(x), "HE filler (kg)", "fragment penetration (mm)", true);
                else if (k == "HEFragmentCurve") DrawPoints(ShellConfig.HEFragPoints(), x => ShellConfig.HEFragments(x), "HE filler (g)", "burst fragments", false);
                else if (k == "APHEFragmentCurve") DrawPoints(ShellConfig.APHEFragPoints(), x => ShellConfig.APHEFragments(x), "APHE filler (g)", "burst fragments", false);
                else DrawGeneric(e);
            }
            catch (Exception ex) { MPLog.Debug("diagram: " + ex.Message); }
            for (int i = _cfgLabelUsed; i < _cfgLabels.Count; i++) _cfgLabels[i].text = "";
            try { _cfgTex.SetPixels32(_px); _cfgTex.Apply(false); } catch (Exception ex) { MPLog.Warn("diagram upload: " + ex.Message); }
        }

        // ---- colours
        static readonly Color32 CPlate = new Color32(150, 155, 165, 255), CShell = new Color32(90, 255, 90, 255), CDim = new Color32(110, 115, 125, 255),
            CSel = new Color32(255, 215, 90, 255), CCyan = new Color32(90, 220, 255, 255), CRed = new Color32(255, 90, 70, 255), COrange = new Color32(255, 140, 40, 255), CGrid = new Color32(40, 44, 52, 255);
        static readonly Color32[] Pal = { new Color32(255, 215, 90, 255), new Color32(90, 220, 255, 255), new Color32(255, 120, 200, 255), new Color32(140, 255, 140, 255), new Color32(255, 150, 70, 255), new Color32(180, 150, 255, 255), new Color32(255, 255, 255, 255) };

        void DrawCones(string k)
        {
            float px = 110f, cy = 192f;
            Line(px, 30, px, 354, 16, CPlate);
            Text(px - 40, 360, "plate", CDim, 13);
            Arrow(8, cy, px - 10, cy, 4, CShell);
            Text(8, cy + 10, "shell", CShell, 13);
            var types = new (string name, float ang, string key)[]
            {
                ("AP / APHE", ShellConfig.ConeAngle(ShellType.AP), "SpallConeHalfAngleDegrees"),
                ("APDS", ShellConfig.ConeAngle(ShellType.APDS), "APDSSpallConeNarrowing"),
                ("APFSDS", ShellConfig.ConeAngle(ShellType.APFSDS), "APFSDSConeNarrowing"),
                ("HEAT", ShellConfig.ConeAngle(ShellType.HEAT), "HEATConeNarrowing"),
            };
            float R = 262f; float ax = px + 9f;
            int selIdx = Array.FindIndex(types, x => x.key == k); if (selIdx < 0) selIdx = 0;
            for (int i = types.Length - 1; i >= 0; i--)
            {
                var c = Pal[i]; bool sel = i == selIdx;
                Wedge(ax, cy, R, -types[i].ang, types[i].ang, new Color32(c.r, c.g, c.b, (byte)(sel ? 60 : 18)));
                float a = types[i].ang * Mathf.Deg2Rad;
                byte al = (byte)(sel ? 255 : 110);
                Line(ax, cy, ax + R * Mathf.Cos(a), cy + R * Mathf.Sin(a), sel ? 2.5f : 1.2f, new Color32(c.r, c.g, c.b, al));
                Line(ax, cy, ax + R * Mathf.Cos(a), cy - R * Mathf.Sin(a), sel ? 2.5f : 1.2f, new Color32(c.r, c.g, c.b, al));
            }
            // fragment rays of the selected cone (a 75 mm gun)
            int n = Mathf.Max(1, Mathf.RoundToInt(ShellConfig.SpallFragmentsPer10mm.Value * 7.5f));
            if (selIdx == 0) n = Mathf.Max(1, Mathf.RoundToInt(n * (k == "APHESpallReduction" ? 1f - ShellConfig.APHESpallReduction.Value : 1f)));
            if (selIdx == 2) n = Mathf.RoundToInt(n * (1f + ShellConfig.APFSDSSpallIncrease.Value));
            float sa = types[selIdx].ang;
            for (int i = 0; i < n; i++)
            {
                float f = n == 1 ? 0f : -sa + 2f * sa * i / (n - 1), rr = 120f + 110f * ((i * 37) % 11) / 10f;
                float a = f * Mathf.Deg2Rad;
                Line(ax, cy, ax + rr * Mathf.Cos(a), cy + rr * Mathf.Sin(a), 1f, new Color32(255, 235, 120, 150));
                Disc(ax + rr * Mathf.Cos(a), cy + rr * Mathf.Sin(a), 2.2f, new Color32(255, 235, 120, 230));
            }
            Arc(ax, cy, 70f, 0f, sa, 2f, CSel);
            for (int i = 0; i < types.Length; i++)
                Text(232, 368 - i * 20, $"{types[i].name}: {types[i].ang:0.#} deg half-angle", Pal[i], i == selIdx ? 15 : 13);
            Text(140, 18, $"{n} fragments for a 75 mm {(selIdx == 0 && k == "APHESpallReduction" ? "APHE" : types[selIdx].name)} shell", CSel, 13);
        }

        void DrawExit(string k)
        {
            float ang = k == "ExitNormalizationAt70" ? 70f : 60f, th = ang * Mathf.Deg2Rad;
            float cx = 150f, cy = 200f;
            var tdir = new Vector2(Mathf.Sin(th), Mathf.Cos(th));      // along the plate
            var nrm = new Vector2(-Mathf.Cos(th), Mathf.Sin(th));      // plate normal, towards the shell
            // plate band
            Line(cx - tdir.x * 175, cy - tdir.y * 175, cx + tdir.x * 175, cy + tdir.y * 175, 18, CPlate);
            // normal
            Dashed(cx + nrm.x * 120, cy + nrm.y * 120, cx - nrm.x * 150, cy - nrm.y * 150, 1.2f, CDim);
            Text(cx + nrm.x * 120 - 20, cy + nrm.y * 120 + 6, "normal", CDim, 12);
            // incoming shell, impact angle
            Arrow(cx - 145, cy, cx - 6, cy, 4, CShell);
            Arc(cx, cy, 48f, 180f - ang, 180f, 1.5f, CShell);
            Text(cx - 120, cy + 14, $"hits at {ang:0} deg", CShell, 13);
            // straight on (no turn)
            Dashed(cx, cy, cx + 225, cy, 1.5f, CDim);
            Text(cx + 160, cy - 18, "straight", CDim, 12);
            float baseTurn = ShellConfig.ExitNormalization(ang), cap = Mathf.Clamp01(ShellConfig.APCapNormalizationReduction.Value), maxTurn = 90f - ang;
            float apheN = Mathf.Clamp01(ShellConfig.APHENormalizationFactor.Value);
            var types = new (string name, float mult, string key)[]
            {
                ("AP", 1f, "ExitNormalizationAt60"),
                ("APC", 1f - cap, "APCapNormalizationReduction"),
                ("APHE", apheN, "APHENormalisation"),
                ("APHEC", apheN * (1f - cap), "-"),
                ("APDS", Mathf.Clamp01(1f - ShellConfig.APDSNormalizationReduction.Value), "APDSNormalizationReduction"),
                ("APFSDS", Mathf.Clamp01(1f - ShellConfig.APFSDSNormalizationReduction.Value), "APFSDSNormalizationReduction"),
            };
            string selKey = k == "ExitNormalizationAt70" ? "ExitNormalizationAt60" : k;
            int sel = Math.Max(0, Array.FindIndex(types, x => x.key == selKey));
            float L = 225f;
            for (int i = types.Length - 1; i >= 0; i--)
            {
                float turn = Mathf.Min(baseTurn * types[i].mult, maxTurn), a = turn * Mathf.Deg2Rad;
                var c = Pal[i]; bool s = i == sel;
                Line(cx, cy, cx + L * Mathf.Cos(a), cy + L * Mathf.Sin(a), s ? 3f : 1.3f, new Color32(c.r, c.g, c.b, (byte)(s ? 255 : 140)));
                if (s) { Arc(cx, cy, 150f, 0f, turn, 2f, c); Disc(cx + L * Mathf.Cos(a), cy + L * Mathf.Sin(a), 4f, c); }
                Text(190, 120 - i * 18, $"{types[i].name}: turns {turn:0.0} deg", c, s ? 15 : 13);
            }
            Text(20, 362, $"leaving a plate hit at {ang:0} deg, the shell turns", CDim, 13);
            Text(20, 344, "away from the normal (toward the plate surface)", CDim, 13);
            Text(20, 18, "60 deg: ExitNormalizationAt60 - 70 deg+: At70 (linear below)", CDim, 12);
        }

        void DrawRicochet(string k)
        {
            float px = 192f, py = 72f;
            Line(16, py - 8, 368, py - 8, 14, CPlate);
            Dashed(px, py, px, py + 290, 1.2f, CDim);
            Text(px + 4, py + 290, "normal", CDim, 12);
            var types = new (string name, float ang, string key)[]
            {
                ("AP", ShellConfig.RicoAP.Value, "RicochetAngleAP"), ("APC", ShellConfig.RicoAPC.Value, "RicochetAngleAPC"),
                ("APHE", ShellConfig.RicoAPHE.Value, "RicochetAngleAPHE"), ("APHEC", ShellConfig.RicoAPHEC.Value, "RicochetAngleAPHEC"),
                ("APDS", ShellConfig.RicoAPDS.Value, "RicochetAngleAPDS"), ("APFSDS", ShellConfig.RicoAPFSDS.Value, "RicochetAngleAPFSDS"),
                ("HEAT", ShellConfig.RicoHEAT.Value, "RicochetAngleHEAT"),
            };
            int sel = Math.Max(0, Array.FindIndex(types, x => x.key == k));
            float sa = Mathf.Clamp(types[sel].ang, 0f, 90f);
            Wedge(px, py, 280f, 90f, 90f + sa, new Color32(80, 220, 90, 40));
            Wedge(px, py, 280f, 90f + sa, 180f, new Color32(255, 80, 60, 50));
            for (int i = 0; i < types.Length; i++)
            {
                float a = (90f + Mathf.Clamp(types[i].ang, 0f, 90f)) * Mathf.Deg2Rad; var c = Pal[i]; bool s = i == sel;
                Line(px, py, px + 280 * Mathf.Cos(a), py + 280 * Mathf.Sin(a), s ? 3f : 1f, new Color32(c.r, c.g, c.b, (byte)(s ? 255 : 120)));
                Text(250, 362 - i * 18, $"{types[i].name}: {types[i].ang:0.#} deg", c, s ? 15 : 13);
            }
            // a shell bouncing at the selected angle + 5 deg, one going in at -10
            float b = Mathf.Min(89f, sa + 6f) * Mathf.Deg2Rad;
            Arrow(px - 200 * Mathf.Sin(b), py + 200 * Mathf.Cos(b), px - 4, py + 4, 3, CRed);
            Arrow(px, py + 2, px + 150 * Mathf.Sin(b), py + 150 * Mathf.Cos(b), 2, new Color32(255, 90, 70, 160));
            Arc(px, py, 60f, 90f, 90f + sa, 2f, CSel);
            Text(px - 175, py + 30, "ricochets", CRed, 14);
            Text(px - 80, py + 210, "penetration\nattempted", new Color32(120, 240, 120, 255), 14);
            Text(16, 30, "angle measured from the plate normal (0 = flat on)", CDim, 12);
            Text(16, 14, "overmatching shells never ricochet", CDim, 12);
        }

        void DrawOvermatch()
        {
            float ratio = Mathf.Max(0.1f, ShellConfig.OvermatchRatio.Value), plateMm = 20f, sc = 2.2f;
            float cal = plateMm * ratio, w = plateMm * sc;
            float x0 = 70f;
            Line(x0, 40, x0, 344, w, CPlate);
            Text(x0 - w / 2, 352, $"{plateMm:0} mm plate", CPlate, 14);
            float r = Mathf.Min(150f, cal * sc / 2f);
            Disc(250, 192, r, new Color32(90, 255, 90, 70)); Arc(250, 192, r, 0, 360, 2f, CShell);
            Line(250 - r, 192, 250 + r, 192, 1.2f, CShell);
            Text(250 - 60, 192 + 8, $"{cal:0} mm shell", CShell, 15);
            Text(20, 40, $"calibre >= {ratio:0.##} x plate thickness: overmatch", CSel, 14);
            Text(20, 22, "no ricochet, slope ignored (plate counts at its real thickness)", CDim, 12);
            if (cal * sc / 2f > 150f) Text(150, 360, "(shell drawn smaller than scale)", CDim, 12);
        }

        void DrawHEBurst(string k)
        {
            float ppm = 600f;
            var C = new Vector2(250, 150);
            var d = new Vector2(1f, -0.35f).normalized;
            var n = new Vector2(-0.5f, 0.866f);
            var t = new Vector2(0.866f, 0.5f);
            Line(C.x - t.x * 200, C.y - t.y * 200, C.x + t.x * 150, C.y + t.y * 150, 5, CPlate);
            Text(C.x + t.x * 60 + 8, C.y + t.y * 60 - 22, "thin fender", CPlate, 13);
            Arrow(C.x - d.x * 240, C.y - d.y * 240, C.x - d.x * 4, C.y - d.y * 4, 3, CShell);
            Dashed(C.x, C.y, C.x + n.x * 90, C.y + n.y * 90, 1f, CDim);
            float back = Mathf.Clamp(ShellConfig.HEBurstBack.Value, 0f, 1f) * ppm, off = Mathf.Clamp(ShellConfig.HEBurstOffSurface.Value, 0f, 1f) * ppm;
            var B1 = C - d * Mathf.Min(back, 220f); var B2 = B1 + n * Mathf.Min(off, 200f);
            Disc(C.x, C.y, 5, CRed); Text(C.x + 8, C.y - 20, "impact", CRed, 13);
            Arrow(C.x, C.y, B1.x, B1.y, k == "HEBurstBackAlongPath" ? 3f : 1.8f, CSel);
            Text((C.x + B1.x) / 2 - 10, (C.y + B1.y) / 2 - 24, $"back {ShellConfig.HEBurstBack.Value * 100f:0} cm", CSel, k == "HEBurstBackAlongPath" ? 15 : 13);
            Arrow(B1.x, B1.y, B2.x, B2.y, k == "HEBurstOffSurface" ? 3f : 1.8f, CCyan);
            Text(B2.x - 150, (B1.y + B2.y) / 2, $"off surface {ShellConfig.HEBurstOffSurface.Value * 100f:0} cm", CCyan, k == "HEBurstOffSurface" ? 15 : 13);
            Disc(B2.x, B2.y, 12, new Color32(255, 140, 40, 200)); Arc(B2.x, B2.y, 22, 0, 360, 2, COrange);
            Text(B2.x + 26, B2.y + 6, "burst", COrange, 14);
            Text(16, 362, "HE bursting on impact: where the blast starts", CDim, 13);
            Text(16, 18, "to scale (600 px per metre)", CDim, 12);
        }

        void DrawFuse(string k)
        {
            float ppm = 85f, px = 50f, cy = 200f;
            float th = Mathf.Clamp(ShellConfig.FuseSensitivity.Value, 1f, 200f) * 0.35f;
            Line(px, 60, px, 340, Mathf.Max(4f, th), CPlate);
            for (int i = 0; i <= 7; i++) { float x = px + i * 0.5f * ppm; Line(x, 52, x, 58, 1, CDim); if (i % 2 == 0) Text(x - 6, 30, $"{i * 0.5f:0} m", CDim, 12); }
            Line(px, 55, px + 3.5f * ppm, 55, 1, CDim);
            Arrow(8, cy, px - 10, cy, 3, CShell);
            float fd = Mathf.Clamp(ShellConfig.FuseDistance.Value, 0f, 3.9f);
            float bx = px + fd * ppm;
            Line(px, cy, bx, cy, 3, CShell);
            Disc(bx, cy, 10, new Color32(255, 140, 40, 220)); Arc(bx, cy, 40, 0, 360, 2, COrange);
            for (int i = 0; i < 16; i++) { float a = i * 22.5f * Mathf.Deg2Rad; Line(bx, cy, bx + 55 * Mathf.Cos(a), cy + 55 * Mathf.Sin(a), 1, new Color32(190, 90, 255, 170)); }
            Text(bx - 40, cy + 60, $"bursts {ShellConfig.FuseDistance.Value:0.##} m behind", COrange, k == "APHEFuseDistance" ? 15 : 13);
            Text(16, 362, $"fuse arms after >= {ShellConfig.FuseSensitivity.Value:0} mm of armour", CSel, k == "APHEFuseSensitivityMm" ? 15 : 13);
            Text(16, 344, "(plates thinner than that: the shell passes through unarmed)", CDim, 12);
            Text(16, 326, "values for a new design - each design can override them in the designer", CDim, 11);
        }

        void DrawJetLoss()
        {
            float loss = Mathf.Max(0f, ShellConfig.HEATJetLossPerMetre.Value);
            Graph(1.5f, "gap between plates (m)", "jet penetration left");
            Curve(d => Mathf.Clamp01(1f - loss * d), 1.5f, CSel);
            float zero = loss > 0f ? 1f / loss : 99f;
            Text(70, 362, $"loses {loss * 100f:0}% per metre" + (zero < 1.5f ? $" - nothing left after {zero:0.##} m" : ""), CSel, 14);
        }

        void DrawHeatLos(string k)
        {
            Graph(90f, "impact angle (deg)", "LOS counted for HEAT");
            Curve(a => 1f - ShellConfig.HeatLosReduction(a), 90f, CSel);
            Curve(a => 1f, 90f, CDim);
            Text(70, 362, $"60 deg: -{ShellConfig.HEATRed60.Value * 100f:0}%   70 deg+: -{ShellConfig.HEATRed70.Value * 100f:0}%", CSel, 14);
        }

        void DrawRicoLoss()
        {
            float l = Mathf.Clamp01(ShellConfig.RicoPenLoss.Value);
            float px = 192f, py = 90f;
            Line(16, py - 8, 368, py - 8, 14, CPlate);
            Arrow(40, 300, px - 3, py + 3, 4, CShell);
            float w2 = Mathf.Max(0.5f, 4f * (1f - l));
            Arrow(px + 3, py + 3, 344, 300, w2, new Color32(90, 255, 90, (byte)(80 + 175 * (1f - l))));
            Text(30, 320, "100% penetration", CShell, 14);
            Text(250, 320, $"{(1f - l) * 100f:0}% after the bounce", CSel, 15);
        }

        /// <summary>A curve setting ('x:y' points): the points, the curve between / past them, and a few values.</summary>
        void DrawPoints((float x, float y)[] pts, Func<float, float> f, string xl, string yl, bool extends)
        {
            float xmax = pts.Length > 0 ? pts[pts.Length - 1].x * 1.25f : 1f, ymax = 1f;
            for (int i = 0; i <= 60; i++) ymax = Mathf.Max(ymax, f(xmax * i / 60f));
            ymax = NiceCeil(ymax * 1.05f); xmax = NiceCeil(xmax);
            for (int i = 0; i <= 4; i++)
            {
                float y = 60 + i * 67.5f; Line(60, y, 360, y, 1, CGrid); Text(10, y - 8, $"{ymax * i / 4f:0.#}", CDim, 11);
                float x = 60 + i * 75f; Line(x, 60, x, 330, 1, CGrid); Text(x - 12, 40, $"{xmax * i / 4f:0.##}", CDim, 11);
            }
            Text(140, 18, xl, CDim, 12); Text(64, 336, yl, CDim, 12);
            float X(float v) => 60 + 300 * v / xmax; float Y(float v) => 60 + 270 * Mathf.Clamp01(v / ymax);
            float lx = X(0), ly = Y(f(0));
            for (int i = 1; i <= 120; i++) { float xv = xmax * i / 120f, nx = X(xv), ny = Y(f(xv)); Line(lx, ly, nx, ny, 2.5f, CSel); lx = nx; ly = ny; }
            foreach (var p in pts) if (p.x > 0f) Disc(X(p.x), Y(p.y), 4.5f, CCyan);
            Text(70, 362, extends ? "points: cyan - past the last one it keeps the last slope" : "points: cyan - past the last one it stays level", CDim, 12);
        }
        static float NiceCeil(float v)
        {
            if (v <= 0f) return 1f;
            float m = Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(v)));
            foreach (var s in new[] { 1f, 2f, 2.5f, 5f, 10f }) if (v <= s * m) return s * m;
            return 10f * m;
        }

        void DrawGeneric(ConfigEntryBase e)
        {
            var v = e.BoxedValue;
            if (v is bool b)
            {
                Disc(192, 200, 60, b ? new Color32(70, 200, 90, 255) : new Color32(90, 90, 100, 255));
                Text(160, 190, b ? "ON" : "OFF", new Color32(255, 255, 255, 255), 28);
                return;
            }
            if (v is float || v is int)
            {
                float val = Convert.ToSingle(v, CultureInfo.InvariantCulture), def = Convert.ToSingle(e.DefaultValue, CultureInfo.InvariantCulture);
                if (!CfgRange(e, out var mn, out var mx)) { mn = Mathf.Min(0f, val); mx = Mathf.Max(val, def) * 1.5f + 1f; }
                float x0 = 40f, x1 = 344f, y = 200f;
                Line(x0, y, x1, y, 10, CGrid);
                float u = Mathf.Clamp01((val - mn) / Mathf.Max(1e-6f, mx - mn)), ud = Mathf.Clamp01((def - mn) / Mathf.Max(1e-6f, mx - mn));
                Line(x0, y, x0 + (x1 - x0) * u, y, 10, new Color32(90, 160, 90, 255));
                Line(x0 + (x1 - x0) * ud, y - 18, x0 + (x1 - x0) * ud, y + 18, 2, CDim);
                Disc(x0 + (x1 - x0) * u, y, 9, CSel);
                Text(x0, y - 40, $"{mn:0.##}", CDim, 12); Text(x1 - 30, y - 40, $"{mx:0.##}", CDim, 12);
                Text(x0 + (x1 - x0) * ud - 20, y + 24, "default", CDim, 12);
                Text(150, 280, Fmt(e), CSel, 28);
                return;
            }
            Text(20, 200, Convert.ToString(v, CultureInfo.InvariantCulture), CSel, 16);
        }

        // ---- graph helpers (x 0..xmax on 60..360 px, y 0..1 on 60..330 px)
        float _gx; void Graph(float xmax, string xl, string yl)
        {
            _gx = xmax;
            for (int i = 0; i <= 4; i++) { float y = 60 + i * 67.5f; Line(60, y, 360, y, 1, CGrid); Text(14, y - 8, $"{i * 25}%", CDim, 11); }
            for (int i = 0; i <= 3; i++) { float x = 60 + i * 100; Line(x, 60, x, 330, 1, CGrid); Text(x - 10, 40, $"{xmax * i / 3f:0.##}", CDim, 11); }
            Text(150, 18, xl, CDim, 12); Text(64, 336, yl, CDim, 12);
        }
        void Curve(Func<float, float> f, float xmax, Color32 c)
        {
            float lx = 60, ly = 60 + 270 * f(0);
            for (int i = 1; i <= 60; i++) { float x = xmax * i / 60f, X = 60 + 300 * i / 60f, Y = 60 + 270 * Mathf.Clamp01(f(x)); Line(lx, ly, X, Y, 2.5f, c); lx = X; ly = Y; }
        }

        // ---- labels
        void Text(float x, float y, string s, Color32 c, float size)
        {
            if (_cfgLabelUsed >= _cfgLabels.Count) return;
            var l = _cfgLabels[_cfgLabelUsed++];
            l.text = s; l.color = c; l.fontSize = size;
            l.rectTransform.pivot = Vector2.zero; l.rectTransform.anchoredPosition = new Vector2(x, y); l.rectTransform.sizeDelta = new Vector2(Mathf.Max(60f, S - x), 40f);
        }

        // ---- software raster (texture y is up)
        void Clear(Color32 c) { for (int i = 0; i < _px.Length; i++) _px[i] = c; for (int y = 0; y < S; y += 32) for (int x = 0; x < S; x++) _px[y * S + x] = new Color32(26, 29, 34, 255); }
        void Blend(int x, int y, Color32 c, float a)
        {
            if ((uint)x >= S || (uint)y >= S || a <= 0f) return;
            a *= c.a / 255f; if (a > 1f) a = 1f;
            ref var p = ref _px[y * S + x];
            p.r = (byte)(p.r + (c.r - p.r) * a); p.g = (byte)(p.g + (c.g - p.g) * a); p.b = (byte)(p.b + (c.b - p.b) * a); p.a = 255;
        }
        void Line(float ax, float ay, float bx, float by, float w, Color32 c)
        {
            float h = w * 0.5f + 1f;
            int x0 = (int)Mathf.Max(0, Mathf.Min(ax, bx) - h), x1 = (int)Mathf.Min(S - 1, Mathf.Max(ax, bx) + h), y0 = (int)Mathf.Max(0, Mathf.Min(ay, by) - h), y1 = (int)Mathf.Min(S - 1, Mathf.Max(ay, by) + h);
            float dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float t = l2 > 0 ? ((x - ax) * dx + (y - ay) * dy) / l2 : 0f; if (t < 0) t = 0; else if (t > 1) t = 1;
                    float ex = ax + dx * t - x, ey = ay + dy * t - y, dd = Mathf.Sqrt(ex * ex + ey * ey);
                    float cov = w * 0.5f + 0.5f - dd; if (cov > 0) Blend(x, y, c, cov > 1 ? 1 : cov);
                }
        }
        void Dashed(float ax, float ay, float bx, float by, float w, Color32 c)
        {
            float L = Mathf.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)); int n = Mathf.Max(1, (int)(L / 10f));
            for (int i = 0; i < n; i += 2) { float t0 = (float)i / n, t1 = Mathf.Min(1f, (i + 1f) / n); Line(ax + (bx - ax) * t0, ay + (by - ay) * t0, ax + (bx - ax) * t1, ay + (by - ay) * t1, w, c); }
        }
        void Arrow(float ax, float ay, float bx, float by, float w, Color32 c)
        {
            Line(ax, ay, bx, by, w, c);
            var d = new Vector2(bx - ax, by - ay); if (d.sqrMagnitude < 1f) return; d.Normalize();
            var p = new Vector2(-d.y, d.x); float hl = 8f + w * 2f;
            Line(bx, by, bx - d.x * hl + p.x * hl * 0.5f, by - d.y * hl + p.y * hl * 0.5f, w, c);
            Line(bx, by, bx - d.x * hl - p.x * hl * 0.5f, by - d.y * hl - p.y * hl * 0.5f, w, c);
        }
        void Disc(float cx, float cy, float r, Color32 c)
        {
            for (int y = (int)(cy - r - 1); y <= (int)(cy + r + 1); y++)
                for (int x = (int)(cx - r - 1); x <= (int)(cx + r + 1); x++)
                { float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)); float cov = r + 0.5f - d; if (cov > 0) Blend(x, y, c, cov > 1 ? 1 : cov); }
        }
        static bool InArc(float ang, float a0, float a1) { float span = a1 - a0; if (span >= 360f) return true; float u = Mathf.Repeat(ang - a0, 360f); return u <= span; }
        void Wedge(float cx, float cy, float r, float a0, float a1, Color32 c)
        {
            for (int y = (int)Mathf.Max(0, cy - r); y <= (int)Mathf.Min(S - 1, cy + r); y++)
                for (int x = (int)Mathf.Max(0, cx - r); x <= (int)Mathf.Min(S - 1, cx + r); x++)
                {
                    float dx = x - cx, dy = y - cy; if (dx * dx + dy * dy > r * r) continue;
                    if (InArc(Mathf.Atan2(dy, dx) * Mathf.Rad2Deg, a0, a1)) Blend(x, y, c, 1f);
                }
        }
        void Arc(float cx, float cy, float r, float a0, float a1, float w, Color32 c)
        {
            if (a1 < a0) { var tmp = a0; a0 = a1; a1 = tmp; }
            float R = r + w;
            for (int y = (int)Mathf.Max(0, cy - R); y <= (int)Mathf.Min(S - 1, cy + R); y++)
                for (int x = (int)Mathf.Max(0, cx - R); x <= (int)Mathf.Min(S - 1, cx + R); x++)
                {
                    float dx = x - cx, dy = y - cy, d = Mathf.Sqrt(dx * dx + dy * dy), cov = w * 0.5f + 0.5f - Mathf.Abs(d - r);
                    if (cov > 0 && InArc(Mathf.Atan2(dy, dx) * Mathf.Rad2Deg, a0, a1)) Blend(x, y, c, cov > 1 ? 1 : cov);
                }
        }
    }
}
