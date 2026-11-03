using System;
using TMPro;
using UnityEngine;

namespace SprocketMP.UI
{
    /// <summary>Panel for the extra modelling tools (Game/ModelTools.cs), shown while a compartment is being edited.</summary>
    public partial class MenuUI
    {
        GameObject _mtPanel; TextMeshProUGUI _mtStatus; bool _mtOn, _mtHidden; float _nextMtUi; string _mtKeyName; UnityEngine.InputSystem.Key _mtKey = UnityEngine.InputSystem.Key.None;

        void BuildModelToolsPanel()
        {
            const float W = 400f, H = 262f;
            _mtPanel = Panel(_root.transform, "ModelTools", Vector2.zero, new Vector2(W, H), PanelCol);
            var rt = _mtPanel.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1, 0);   // bottom right
            rt.anchoredPosition = new Vector2(-14, 64);
            var t = _mtPanel.transform; float y = -10;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            Label(t, new Vector2(12, y), new Vector2(W - 24, 28), $"<b>MODELLING TOOLS</b> <size=13><color=#9aa>[{Plugin.ModelToolsKey.Value}] hide - Ctrl+Z undoes</color></size>", 18); y -= 36;
            var c1 = new Color(0.22f, 0.32f, 0.42f, 1f);

            Button B(float x, float w, string label, ModelTools.Tool tool) { var b = MakeButton(t, new Vector2(x, y), new Vector2(w, 28), label, c1, () => { Commit(); ModelTools.Run(tool); _nextMtUi = 0; }); b.Label.fontSize = 14; return b; }
            void F(float x, string label, float lw, string val, Action<float> set) { Label(t, new Vector2(x, y), new Vector2(lw, 28), $"<color=#9aa>{label}</color>", 13); MakeField(t, new Vector2(x + lw, y), new Vector2(54, 28), val, 6, false, v => { if (TryF(v, out var f)) set(f); _nextMtUi = 0; }); }

            Label(t, new Vector2(12, y), new Vector2(W - 24, 22), "<b>Faces</b>", 15); y -= 24;
            B(12, 120, "INSET FACE", ModelTools.Tool.Inset); B(138, 130, "POLYGON HOLE", ModelTools.Tool.PolyHole);
            F(274, "by mm", 56, ModelTools.InsetMm.ToString("0", ci), f => ModelTools.InsetMm = Mathf.Clamp(f, 1f, 5000f)); y -= 34;
            B(12, 120, "ROUND HOLE", ModelTools.Tool.RoundHole); B(138, 130, "ROUND INSET", ModelTools.Tool.RoundInset); y -= 32;
            F(12, "edges", 46, ModelTools.HoleEdges.ToString(ci), f => ModelTools.HoleEdges = Mathf.Clamp(Mathf.RoundToInt(f), 3, 128));
            F(130, "radius mm", 76, ModelTools.HoleRadiusMm.ToString("0", ci), f => ModelTools.HoleRadiusMm = Mathf.Clamp(f, 1f, 5000f)); y -= 40;

            Label(t, new Vector2(12, y), new Vector2(W - 24, 22), "<b>Knife</b>", 15); y -= 24;
            B(12, 180, "CONNECT 2 VERTICES", ModelTools.Tool.Connect); y -= 36;

            _mtStatus = Label(t, new Vector2(12, y), new Vector2(W - 24, H + y - 6), "", 13);
            _mtStatus.enableWordWrapping = true; _mtStatus.alignment = TextAlignmentOptions.TopLeft;
            _mtPanel.SetActive(false);
        }

        void UpdateModelToolsPanel()
        {
            if (_mtPanel == null) return;
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (_mtKeyName != Plugin.ModelToolsKey.Value) { _mtKeyName = Plugin.ModelToolsKey.Value; if (!Enum.TryParse(_mtKeyName, true, out _mtKey)) _mtKey = UnityEngine.InputSystem.Key.None; }
                if (kb != null && _mtKey != UnityEngine.InputSystem.Key.None && kb[_mtKey].wasPressedThisFrame && !AnyFieldFocused() && ModelTools.EditorOpen) _mtHidden = !_mtHidden;
            }
            catch { }
            bool on = ModelTools.EditorOpen && !Runner.Battle.InBattle && !_mtHidden;
            if (on != _mtOn) { _mtOn = on; _mtPanel.SetActive(on); if (!on) Commit(); }
            if (!on || Time.realtimeSinceStartup < _nextMtUi) return;
            _nextMtUi = Time.realtimeSinceStartup + 0.25f;
            _mtStatus.text = ModelTools.Status;
        }
    }
}
