using System;
using System.Text;
using TMPro;
using UnityEngine;

namespace SprocketMP.UI
{
    /// <summary>Designer panel shown while a crew seat is selected: make that crew member an extra loader of a gun.</summary>
    public partial class MenuUI
    {
        GameObject _ldPanel; TextMeshProUGUI _ldInfo, _ldGun; bool _ldOn; float _nextLdUi; IntPtr _ldSeatPtr;

        void BuildLoaderPanel()
        {
            const float W = 420f, H = 200f;
            _ldPanel = Panel(_root.transform, "Loaders", Vector2.zero, new Vector2(W, H), PanelCol);
            var rt = _ldPanel.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1, 0);   // bottom right
            rt.anchoredPosition = new Vector2(-14, 64);
            var t = _ldPanel.transform; float y = -10;
            Label(t, new Vector2(12, y), new Vector2(W - 24, 28), "<b>EXTRA LOADER</b> <size=13><color=#9aa>this crew member also loads a gun</color></size>", 18); y -= 36;
            MakeButton(t, new Vector2(12, y), new Vector2(34, 28), "<", BtnCol, () => CycleLoaderGun(-1));
            _ldGun = Label(t, new Vector2(52, y), new Vector2(W - 112, 28), "", 16); _ldGun.alignment = TextAlignmentOptions.Center;
            MakeButton(t, new Vector2(W - 46, y), new Vector2(34, 28), ">", BtnCol, () => CycleLoaderGun(1));
            y -= 36;
            _ldInfo = Label(t, new Vector2(12, y), new Vector2(W - 24, H + y - 6), "", 13);
            _ldInfo.enableWordWrapping = true; _ldInfo.alignment = TextAlignmentOptions.TopLeft;
            _ldPanel.SetActive(false);
        }

        void CycleLoaderGun(int dir)
        {
            var seat = Loaders.SelectedSeat; if (seat == null) return;
            if (!Loaders.HasNoJob(seat)) { _nextLdUi = 0; return; }   // only crew members with no job
            string design = Shells.ShellStore.DesignNameInScene(); if (string.IsNullOrEmpty(design)) return;
            int sv = Loaders.VuidOf(seat); if (sv == 0) return;
            var guns = Loaders.DesignerGuns();
            int cur = Loaders.GunOfSeat(design, sv);
            int i = guns.FindIndex(g => g.vuid == cur);   // -1 = none
            // next gun this crew member can reach (the game's arm reach to the breech); "none" always
            for (int step = 0; step <= guns.Count; step++)
            {
                i += dir; if (i < -1) i = guns.Count - 1; if (i >= guns.Count) i = -1;
                if (i < 0 || Loaders.DesignerReach(seat, guns[i].vuid) != 0) break;
            }
            Loaders.Assign(design, sv, i < 0 ? 0 : guns[i].vuid);
            _nextLdUi = 0;
        }

        void UpdateLoaderPanel()
        {
            if (_ldPanel == null) return;
            bool on = Plugin.LoadersEnabled.Value && !Runner.Battle.InBattle && Loaders.SelectedSeat != null;
            if (on != _ldOn) { _ldOn = on; _ldPanel.SetActive(on); }
            if (!on || Time.realtimeSinceStartup < _nextLdUi) return;
            _nextLdUi = Time.realtimeSinceStartup + 0.3f;
            var seat = Loaders.SelectedSeat;
            string design = Shells.ShellStore.DesignNameInScene();
            int sv = Loaders.VuidOf(seat);
            var guns = Loaders.DesignerGuns();
            int cur = Loaders.GunOfSeat(design, sv);
            var g = guns.Find(x => x.vuid == cur);
            bool free = Loaders.HasNoJob(seat);
            if (!free && cur != 0) Loaders.Assign(design, sv, 0);   // got a job since: not an extra loader any more
            int reach = cur != 0 && free ? Loaders.DesignerReach(seat, cur) : -1;
            int outOfReach = 0; foreach (var x in guns) if (free && Loaders.DesignerReach(seat, x.vuid) == 0) outOfReach++;
            _ldGun.text = !free ? "<color=#fd8>has a job: can't be an extra loader</color>" : cur == 0 ? "<color=#9aa>not an extra loader</color>"
                : reach == 0 ? $"<color=#f88>{(g.label ?? "gun " + cur)}: out of reach</color>" : $"<color=#8f8>loads {(g.label ?? "gun " + cur)}</color>";
            var sb = new StringBuilder();
            if (!free) sb.AppendLine("<color=#9aa>Only crew members with no job (no role assigned) can be extra loaders.</color>");
            if (reach == 0) sb.AppendLine("<color=#f88>This crew member's arms can't reach that gun's breech (the game's loader reach): move the seat closer, or it won't count in battle.</color>");
            else if (outOfReach > 0) sb.AppendLine($"<color=#9aa>{outOfReach} gun(s) out of this crew member's reach are skipped.</color>");
            if (string.IsNullOrEmpty(design)) sb.AppendLine("<color=#f88>Save the design once (it needs a name)</color>");
            if (guns.Count == 0) sb.AppendLine("<color=#fd8>No gun on this vehicle</color>");
            int shown = 0;
            foreach (var x in guns)
            {
                int n = Loaders.ExtraCount(design, x.vuid);
                if (n == 0 && x.vuid != cur) continue;   // only the guns that have extra loaders
                if (++shown > 6) { sb.AppendLine("<color=#9aa>...</color>"); break; }
                sb.AppendLine($"{x.label}: {1 + n} loaders <color=#9ec9ff>x{Loaders.Multiplier(1 + n):0.##} reload speed</color>");
            }
            if (shown == 0 && guns.Count > 0) sb.AppendLine($"<color=#9aa>{guns.Count} guns, none with extra loaders yet</color>");
            sb.Append("<color=#9aa>Saved next to the shell settings, not in the design. In battle, only extra loaders alive count.</color>");
            _ldInfo.text = sb.ToString();
        }
    }
}
