using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SprocketMP.UI
{
    /// <summary>Field repair progress: a radial ring (filled Image on a generated ring sprite) with the seconds left.</summary>
    public partial class MenuUI
    {
        GameObject _ring; Image _ringBg, _ringFill; TextMeshProUGUI _ringText; string _ringPos; float _ringSize;

        static Sprite RingSprite()
        {
            const int N = 128; float ro = N * 0.5f - 1f, ri = ro * 0.72f;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = x - N * 0.5f + 0.5f, dy = y - N * 0.5f + 0.5f, d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(ro - d + 0.5f) * Mathf.Clamp01(d - ri + 0.5f);   // anti-aliased edges
                    px[y * N + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            tex.SetPixels32(px); tex.Apply(false, true);
            tex.hideFlags = HideFlags.HideAndDontSave;
            var sp = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
            sp.hideFlags = HideFlags.HideAndDontSave;
            return sp;
        }

        void BuildRepairRing()
        {
            var sprite = RingSprite();
            _ring = new GameObject("RepairRing"); _ring.transform.SetParent(_root.transform, false);
            _ring.AddComponent<RectTransform>();
            Image Ring(string name, Color c)
            {
                var go = new GameObject(name); go.transform.SetParent(_ring.transform, false);
                var rt = go.AddComponent<RectTransform>(); rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
                var im = go.AddComponent<Image>(); im.sprite = sprite; im.color = c; im.raycastTarget = false;
                return im;
            }
            _ringBg = Ring("Back", new Color(0f, 0f, 0f, 0.45f));
            _ringFill = Ring("Fill", new Color(0.55f, 0.95f, 0.8f, 0.95f));
            _ringFill.type = Image.Type.Filled; _ringFill.fillMethod = Image.FillMethod.Radial360;
            _ringFill.fillOrigin = (int)Image.Origin360.Top; _ringFill.fillClockwise = true; _ringFill.fillAmount = 0f;
            var tgo = new GameObject("Text"); tgo.transform.SetParent(_ring.transform, false);
            var trt = tgo.AddComponent<RectTransform>(); trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = new Vector2(-60, -40); trt.offsetMax = new Vector2(60, 0);
            _ringText = tgo.AddComponent<TextMeshProUGUI>(); StyleText(_ringText, 18, TextAlignmentOptions.Center);
            _ring.SetActive(false);
        }

        void LayoutRing()
        {
            _ringPos = Plugin.RepairRingPosition.Value; _ringSize = Mathf.Clamp(Plugin.RepairRingSize.Value, 24f, 600f);
            var rt = _ring.GetComponent<RectTransform>();
            Vector2 a; Vector2 off;
            switch (_ringPos)
            {
                case "BelowCenter": a = new Vector2(0.5f, 0.5f); off = new Vector2(0, -_ringSize - 60f); break;
                case "BottomCenter": a = new Vector2(0.5f, 0f); off = new Vector2(0, _ringSize * 0.5f + 140f); break;
                case "TopRight": a = new Vector2(1f, 1f); off = new Vector2(-_ringSize * 0.5f - 24f, -_ringSize * 0.5f - 110f); break;
                default: a = new Vector2(0.5f, 0.5f); off = Vector2.zero; break;
            }
            rt.anchorMin = rt.anchorMax = a; rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = off; rt.sizeDelta = new Vector2(_ringSize, _ringSize);
            _ringText.fontSize = Mathf.Clamp(_ringSize * 0.2f, 12f, 40f);
        }

        // looking through a sight (gunner / commander optics) rather than the third-person camera
        Sprocket.VehicleControl.VehicleControlPlayerState[] _pstates = new Sprocket.VehicleControl.VehicleControlPlayerState[0]; float _nextScopeScan;
        bool Scoped()
        {
            try
            {
                if (Time.unscaledTime >= _nextScopeScan) { _nextScopeScan = Time.unscaledTime + 1f; _pstates = UnityEngine.Object.FindObjectsOfType<Sprocket.VehicleControl.VehicleControlPlayerState>(); }
                foreach (var ps in _pstates)
                {
                    if (ps == null || ps.WasCollected) continue;
                    var sc = ps.scopeController;
                    if (sc != null && sc.Scoped) return true;
                }
            }
            catch { }
            return false;
        }

        void UpdateRepairRing()
        {
            if (_ring == null) return;
            var bm = Runner.Battle;
            bool on = Plugin.RepairRing.Value && bm != null && bm.InBattle && bm.Repairing && !(Plugin.RepairRingHideScoped.Value && Scoped());
            if (_ring.activeSelf != on) _ring.SetActive(on);
            if (!on) return;
            if (_ringPos != Plugin.RepairRingPosition.Value || _ringSize != Plugin.RepairRingSize.Value) LayoutRing();
            _ringFill.fillAmount = bm.RepairProgress;
            _ringText.text = $"<b>{bm.RepairRemaining:0}s</b>";
        }
    }
}
