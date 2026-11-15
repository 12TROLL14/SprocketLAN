using System;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketMP.UI
{
    /// <summary>Scoreboard (hold [Tab] by default): every player's kills, deaths, shots, hits, penetrations, accuracy.</summary>
    public partial class MenuUI
    {
        GameObject _score; TextMeshProUGUI _scoreText; bool _scoreOn, _scoreToggled; float _nextScore; string _scoreKeyName; Key _scoreKey = Key.None;

        void BuildScoreboard()
        {
            _score = Panel(_root.transform, "Scoreboard", Vector2.zero, new Vector2(1000, 520), new Color(0.06f, 0.07f, 0.09f, 0.88f));
            var rt = _score.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f); rt.anchoredPosition = new Vector2(0, 40);
            var go = new GameObject("Text"); go.transform.SetParent(_score.transform, false);
            var trt = go.AddComponent<RectTransform>(); trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = new Vector2(24, 16); trt.offsetMax = new Vector2(-24, -16);
            _scoreText = go.AddComponent<TextMeshProUGUI>(); StyleText(_scoreText, 20, TextAlignmentOptions.TopLeft);
            _scoreText.enableWordWrapping = false;
            _score.SetActive(false);
        }

        void UpdateScoreboard()
        {
            if (_score == null) return;
            bool want = false;
            if (Plugin.ScoreEnabled.Value)
            {
                try
                {
                    if (_scoreKeyName != Plugin.ScoreKey.Value) { _scoreKeyName = Plugin.ScoreKey.Value; if (!Enum.TryParse(_scoreKeyName, true, out _scoreKey)) _scoreKey = Key.None; }
                    var kb = Keyboard.current;
                    if (kb != null && _scoreKey != Key.None && !AnyFieldFocused())
                    {
                        if (Plugin.ScoreHold.Value) want = kb[_scoreKey].isPressed;
                        else { if (kb[_scoreKey].wasPressedThisFrame) _scoreToggled = !_scoreToggled; want = _scoreToggled; }
                    }
                }
                catch { }
                var bm = Runner.Battle;
                if (!(bm.InBattle || Runner.Session.Active)) want = false;
            }
            if (want != _scoreOn) { _scoreOn = want; _score.SetActive(want); _nextScore = 0f; }
            if (!want || Time.unscaledTime < _nextScore) return;
            _nextScore = Time.unscaledTime + 0.25f;
            _scoreText.text = ScoreText();
        }

        string ScoreText()
        {
            var s = Runner.Session; var bm = Runner.Battle;
            var rows = new System.Collections.Generic.List<(byte id, string name, string team, bool me, int ping)>();
            byte me = bm.LocalShooterId;
            string TeamOf(VehicleRig r) => r == null ? "" : r.Team.ToString().Replace("Team", "");
            rows.Add((me, Plugin.EffectiveName, TeamOf(bm.InBattle ? bm.Local : null), true, s.Active ? (s.IsHost ? 0 : s.LocalPingMs) : -1));
            if (s.Active)
                foreach (var p in s.Players.Values)
                {
                    bm.PuppetByPlayer.TryGetValue(p.Id, out var rig);
                    rows.Add((p.Id, p.Name, TeamOf(rig), false, p.PingMs));
                }
            Stats.Line L(byte id) => Stats.ById.TryGetValue(id, out var l) ? l : new Stats.Line();
            var ordered = rows.OrderBy(r => r.team).ThenByDescending(r => L(r.id).Kills).ThenBy(r => L(r.id).Deaths).ToList();
            bool ai = rows.Any(r => L(r.id).AiKills > 0);

            var sb = new StringBuilder();
            sb.Append("<size=26><b>SCOREBOARD</b></size>  <color=#9aa><size=16>");
            sb.Append(s.Active ? $"{rows.Count} player{(rows.Count == 1 ? "" : "s")}" : "offline");
            sb.Append($"  -  {FormatTime(Time.time - Stats.Since)}</size></color>\n\n");
            void Cols(string[] c, string colour)
            {
                int[] at = { 0, 300, 370, 440, 510, 590, 670, 750, 840, 920 };
                sb.Append($"<color={colour}>");
                for (int i = 0; i < c.Length && i < at.Length; i++) sb.Append($"<pos={at[i]}>").Append(c[i]);
                sb.Append("</color>\n");
            }
            Cols(new[] { "<b>PLAYER</b>", "<b>TEAM</b>", "<b>K</b>", "<b>D</b>", "<b>K/D</b>", "<b>SHOTS</b>", "<b>HITS</b>", "<b>PENS</b>", "<b>ACC</b>", ai ? "<b>AI</b>" : "<b>PING</b>" }, "#bcc");
            foreach (var r in ordered)
            {
                var l = L(r.id);
                string kd = l.Deaths == 0 ? l.Kills.ToString() : ((float)l.Kills / l.Deaths).ToString("0.0#");
                string acc = l.Shots > 0 ? $"{Mathf.Min(100, 100f * l.Hits / l.Shots):0}%" : "-";
                string name = r.name.Length > 22 ? r.name.Substring(0, 22) : r.name;
                Cols(new[] { (r.me ? "<b>" : "") + name + (r.me ? "</b>" : ""), r.team == "" ? "-" : r.team, l.Kills.ToString(), l.Deaths.ToString(), kd, l.Shots.ToString(), l.Hits.ToString(), l.Pens.ToString(), acc,
                             ai ? l.AiKills.ToString() : (r.ping >= 0 ? r.ping + "ms" : "-") }, r.me ? "#ffd060" : "#e8e8e8");
            }
            sb.Append($"\n<size=14><color=#899>Hits and pens come from the pen cam records (PenCam must be on). [{Plugin.ScoreKey.Value}] {(Plugin.ScoreHold.Value ? "hold" : "toggle")}</color></size>");
            return sb.ToString();
        }

        static string FormatTime(float s) { if (s < 0) s = 0; int m = (int)(s / 60); return $"{m}:{(int)(s % 60):00}"; }
    }
}
