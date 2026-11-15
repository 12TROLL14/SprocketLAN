using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SprocketMP.UI
{
    /// <summary>
    /// Range-marked sight overlay while scoped in: the selected fire group's shell trajectory is simulated from the
    /// real muzzle (position, barrel direction, muzzle velocity with the selected shell's multiplier, the tank's own
    /// velocity, gravity, optional drag) and projected through the sight's camera. Where the shell will be at
    /// 100 / 200 / ... m therefore shows up exactly on screen: the curve shows the drop AND the parallax between the
    /// gun and the sight (they sit in different places, so near targets need a different hold than far ones).
    /// </summary>
    public partial class MenuUI
    {
        GameObject _sight; RectTransform _sightRt;
        readonly List<RectTransform> _sightLines = new List<RectTransform>();
        readonly List<TextMeshProUGUI> _sightLabels = new List<TextMeshProUGUI>();
        int _lineUsed, _labelUsed;
        string _sightKeyName; UnityEngine.InputSystem.Key _sightKey = UnityEngine.InputSystem.Key.None;
        Color _sightCol = Color.white; string _sightColSrc;

        // ---- self-calibration from real shots: muzzle point and launch direction in the gun's own frame, launch speed per shell type
        sealed class Calib { public Vector3 LocalPos, LocalDir; public readonly Dictionary<int, float> SpeedRatio = new Dictionary<int, float>(); public bool Logged; }
        static readonly Dictionary<IntPtr, Calib> _calib = new Dictionary<IntPtr, Calib>();

        public static void CalibrateSight(Sprocket.WeaponFramework.WeaponBehaviour w, Vector3 pos, Vector3 vel, Shells.ShellType shell)
        {
            if (w == null || vel.sqrMagnitude < 1f) return;
            var tr = w.Transform; if (tr == null) return;
            if (!_calib.TryGetValue(w.Pointer, out var c)) { if (_calib.Count > 64) _calib.Clear(); _calib[w.Pointer] = c = new Calib(); }
            Vector3 inh = Vector3.zero; try { inh = w.InheritedVelocity; } catch { }
            var shot = vel - inh;
            float mv0 = 0f; try { mv0 = Shells.ShellSelect.ActiveCannon != null ? Shells.ShellSelect.ActiveCannon.muzzleVelocity : 0f; } catch { }
            // some launches report no velocity (0 m/s): calibrating from that gave a zero direction and hid the sight
            if (shot.magnitude < 30f || (mv0 > 1f && shot.magnitude < 0.2f * mv0)) { MPLog.Debug($"Range sight: shot ignored for calibration ({shot.magnitude:F0} m/s)"); return; }
            c.LocalPos = tr.InverseTransformPoint(pos);
            c.LocalDir = tr.InverseTransformDirection(shot.normalized);
            float mv = 0f; try { mv = Shells.ShellSelect.ActiveWeapon != null && Shells.ShellSelect.ActiveWeapon.Pointer == w.Pointer && Shells.ShellSelect.ActiveCannon != null ? Shells.ShellSelect.ActiveCannon.muzzleVelocity : 0f; } catch { }
            if (mv > 1f) c.SpeedRatio[(int)shell] = shot.magnitude / mv;
            if (!c.Logged)
            {
                c.Logged = true;
                float ang = Vector3.Angle(tr.forward, shot);
                MPLog.Info($"Range sight calibrated from a shot: barrel axis vs launch {ang:F2} deg, muzzle offset {(pos - w.ShellSpawnPosition).magnitude:F2} m, speed {shot.magnitude:F0} m/s (gun {mv:F0})");
            }
        }

        void BuildSight()
        {
            _sight = new GameObject("RangeSight"); _sight.transform.SetParent(_root.transform, false);
            _sightRt = _sight.AddComponent<RectTransform>();
            _sightRt.anchorMin = Vector2.zero; _sightRt.anchorMax = Vector2.one; _sightRt.offsetMin = _sightRt.offsetMax = Vector2.zero;
            _sight.transform.SetAsFirstSibling();   // under our panels
            _sight.SetActive(false);
        }

        RectTransform SightLine()
        {
            if (_lineUsed < _sightLines.Count) { var r = _sightLines[_lineUsed++]; r.gameObject.SetActive(true); return r; }
            var go = new GameObject("l"); go.transform.SetParent(_sight.transform, false);
            var rt = go.AddComponent<RectTransform>(); rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0f, 0.5f);
            var im = go.AddComponent<Image>(); im.raycastTarget = false;
            _sightLines.Add(rt); _lineUsed++;
            return rt;
        }
        TextMeshProUGUI SightLabel()
        {
            if (_labelUsed < _sightLabels.Count) { var t = _sightLabels[_labelUsed++]; t.gameObject.SetActive(true); return t; }
            var go = new GameObject("t"); go.transform.SetParent(_sight.transform, false);
            var rt = go.AddComponent<RectTransform>(); rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0f, 0.5f); rt.sizeDelta = new Vector2(120, 30);
            var t2 = go.AddComponent<TextMeshProUGUI>(); StyleText(t2, 16, TextAlignmentOptions.Left); t2.raycastTarget = false; t2.enableWordWrapping = false;
            _sightLabels.Add(t2); _labelUsed++;
            return t2;
        }

        void Seg(Vector2 a, Vector2 b, float w, Color c)
        {
            var d = b - a; float len = d.magnitude; if (len < 0.5f) return;
            var rt = SightLine();
            rt.anchoredPosition = a; rt.sizeDelta = new Vector2(len, w);
            rt.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
            rt.GetComponent<Image>().color = c;
        }

        // ---- the game's gun convergence readout ("300m"): moved to the right, out of the way of the range marks
        Sprocket.Vehicles.UI.VehicleGunConverganceDisplay[] _convDisplays = new Sprocket.Vehicles.UI.VehicleGunConverganceDisplay[0]; float _nextConvScan;
        readonly Dictionary<IntPtr, (Vector2 orig, Vector2 set)> _convPos = new Dictionary<IntPtr, (Vector2, Vector2)>();

        void MoveConvergenceText()
        {
            float dx = Plugin.SightConvergenceOffsetX.Value, dy = Plugin.SightConvergenceOffsetY.Value;
            if (Time.unscaledTime >= _nextConvScan) { _nextConvScan = Time.unscaledTime + 2f; _convDisplays = UnityEngine.Object.FindObjectsOfType<Sprocket.Vehicles.UI.VehicleGunConverganceDisplay>(); }
            foreach (var d in _convDisplays)
            {
                try
                {
                    if (d == null || d.WasCollected) continue;
                    var t = d.converganceDisplay; if (t == null) continue;
                    var rt = t.rectTransform; var key = rt.Pointer; var cur = rt.anchoredPosition;
                    if (!_convPos.TryGetValue(key, out var p) || (cur - p.set).sqrMagnitude > 0.01f) p.orig = cur;   // the game moved it: that's the new base
                    p.set = p.orig + new Vector2(dx, dy);
                    if ((cur - p.set).sqrMagnitude > 0.01f) rt.anchoredPosition = p.set;
                    _convPos[key] = p;
                }
                catch { }
            }
        }

        void UpdateSight()
        {
            if (_sight == null) return;
            try { MoveConvergenceText(); } catch (Exception e) { MPLog.Debug("convergence text: " + e.Message); }
            try
            {
                if (_sightKeyName != Plugin.SightKey.Value) { _sightKeyName = Plugin.SightKey.Value; if (!Enum.TryParse(_sightKeyName, true, out _sightKey)) _sightKey = UnityEngine.InputSystem.Key.None; }
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null && _sightKey != UnityEngine.InputSystem.Key.None && !AnyFieldFocused() && kb[_sightKey].wasPressedThisFrame) Plugin.SightEnabled.Value = !Plugin.SightEnabled.Value;
            }
            catch { }
            var bm = Runner.Battle;
            bool on = Plugin.SightEnabled.Value && bm.InBattle && !bm.LocalDestroyed && (!Plugin.SightScopedOnly.Value || Scoped());
            _lineUsed = 0; _labelUsed = 0;
            if (on) { try { on = DrawSight(); } catch (Exception e) { MPLog.Debug("range sight: " + e.Message); on = false; } }
            for (int i = _lineUsed; i < _sightLines.Count; i++) if (_sightLines[i].gameObject.activeSelf) _sightLines[i].gameObject.SetActive(false);
            for (int i = _labelUsed; i < _sightLabels.Count; i++) if (_sightLabels[i].gameObject.activeSelf) _sightLabels[i].gameObject.SetActive(false);
            if (_sight.activeSelf != on) _sight.SetActive(on);
        }

        bool DrawSight()
        {
            Shells.ShellSelect.TrackFireGroup();
            var w = Shells.ShellSelect.ActiveWeapon; var cn = Shells.ShellSelect.ActiveCannon;
            if (w == null || cn == null) return NoSight("no gun found for the selected fire group");
            var cam = Camera.main; if (cam == null) return NoSight("no main camera");

            float speed = cn.muzzleVelocity;
            if (speed <= 1f) return NoSight("the gun has no muzzle velocity yet");
            var shell = Shells.ShellSelect.Current;
            _calib.TryGetValue(w.Pointer, out var cal);
            if (cal != null && cal.SpeedRatio.TryGetValue((int)shell, out var ratio) && ratio > 0.2f && ratio < 5f) speed *= ratio;   // measured launch speed of this shell (before the mod's sabot boost)
            if (Plugin.ShellsOn && Shells.ShellConfig.IsSabot(shell)) speed *= Shells.ShellConfig.VelocityMult(shell);   // APDS / APFSDS fly faster with the mod
            var tr = w.Transform;
            Vector3 p = cal != null ? tr.TransformPoint(cal.LocalPos) : w.ShellSpawnPosition; Vector3 origin = p;
            Vector3 dir = cal != null && cal.LocalDir.sqrMagnitude > 0.5f ? tr.TransformDirection(cal.LocalDir).normalized : tr.forward;
            Vector3 v = dir * speed;
            try { v += w.InheritedVelocity; } catch { }

            if (_sightColSrc != Plugin.SightColour.Value) { _sightColSrc = Plugin.SightColour.Value; if (!ColorUtility.TryParseHtmlString(_sightColSrc, out _sightCol)) _sightCol = new Color(1f, 0.85f, 0.2f, 0.9f); }
            var col = _sightCol; var dim = new Color(col.r, col.g, col.b, col.a * 0.55f);

            float step = Mathf.Clamp(Plugin.SightStepMetres.Value, 25f, 1000f), max = Mathf.Clamp(Plugin.SightMaxRange.Value, 100f, 10000f);
            int labelEvery = Mathf.Max(1, Mathf.RoundToInt(Mathf.Clamp(Plugin.SightLabelEveryMetres.Value, step, max) / step));
            float drag = Mathf.Max(0f, Plugin.SightDrag.Value), tick = Mathf.Clamp(Plugin.SightTickPixels.Value, 4f, 200f), lw = Mathf.Clamp(Plugin.SightLineWidth.Value, 0.5f, 8f);
            float minR = Mathf.Max(0f, Plugin.SightMinRange.Value);

            // horizontal range is measured along the barrel's heading from the muzzle
            var h = new Vector3(dir.x, 0f, dir.z); if (h.sqrMagnitude < 1e-6f) return NoSight("the gun points straight up or down"); h.Normalize();
            var g = Physics.gravity; float dt = 0.005f; float tMax = 30f;
            float prevR = 0f; float nextMark = step; int mark = 0;
            Vector2? prevScreen = null;
            var rootRt = _root.GetComponent<RectTransform>();
            for (float t = 0f; t < tMax && nextMark <= max; t += dt)
            {
                var a = g; if (drag > 0f) a -= v * (v.magnitude * drag);
                v += a * dt; var np = p + v * dt;
                float r = Vector3.Dot(np - origin, h);
                if (r >= nextMark)
                {
                    float u = (nextMark - prevR) / Mathf.Max(1e-4f, r - prevR);
                    var at = Vector3.Lerp(p, np, u);
                    mark++;
                    if (nextMark >= minR)
                    {
                        var sp = cam.WorldToScreenPoint(at);
                        if (sp.z > 0f && RectTransformUtility.ScreenPointToLocalPointInRectangle(rootRt, new Vector2(sp.x, sp.y), null, out var loc))
                        {
                            bool big = mark % labelEvery == 0;
                            float tl = big ? tick : tick * 0.5f;
                            Seg(new Vector2(loc.x - tl * 0.5f, loc.y), new Vector2(loc.x + tl * 0.5f, loc.y), lw, big ? col : dim);
                            if (prevScreen.HasValue) Seg(prevScreen.Value, loc, Mathf.Max(0.5f, lw * 0.5f), dim);
                            if (big)
                            {
                                var lab = SightLabel();
                                lab.text = Plugin.SightLabelHundreds.Value ? ((int)(nextMark / 100f)).ToString() : $"{nextMark:0}";
                                lab.color = col; lab.fontSize = Mathf.Clamp(Plugin.SightFontSize.Value, 8f, 48f);
                                lab.rectTransform.anchoredPosition = new Vector2(loc.x + tl * 0.5f + 4f, loc.y);
                            }
                            prevScreen = loc;
                        }
                    }
                    nextMark += step;
                }
                prevR = r; p = np;
                if (t > 0.5f) dt = Mathf.Min(0.02f, dt * 1.01f);   // fine steps near the muzzle, coarser further out
            }
            if (_lineUsed == 0) return NoSight("no range mark is in front of the sight camera");
            _sightWhy = "";
            return true;
        }

        string _sightWhy = "";
        bool NoSight(string why)
        {
            if (why != _sightWhy) { _sightWhy = why; MPLog.Info("Range sight hidden: " + why); }
            return false;
        }
    }
}
