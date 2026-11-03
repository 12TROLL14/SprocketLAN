using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SprocketMP.UI
{
    /// <summary>The multiplayer lobby panel (F9 / LOBBY button): players, own tank + team + READY, and for the host the
    /// map, team count, team spawns (placed on the map overview) and START.</summary>
    public partial class MenuUI
    {
        GameObject _lobbyPanel; bool _lobbyOn; float _nextLobbyUi; string _lobbyKeyName; Key _lobbyKey = Key.None;
        TextMeshProUGUI _lobbyPlayers, _lobbyStatus, _lobbyMapLbl, _lobbyMapHint, _lobbyPageLbl;
        readonly List<Button> _lobbyTeamBtns = new List<Button>(), _lobbyDesignRows = new List<Button>(), _lobbyTeamCount = new List<Button>(), _lobbySpawnBtns = new List<Button>(), _lobbyPlaceBtns = new List<Button>();
        Button _lobbyReady, _lobbyStart, _lobbyMapPrev, _lobbyMapNext;
        List<DesignShare.Design> _lobbyDesigns = new List<DesignShare.Design>(), _lobbyAll = new List<DesignShare.Design>(); int _lobbyPage;
        Field _lobbySearch; string _lobbyFilter = "";
        void LobbyRelist() { _lobbyAll = DesignShare.ListDesigns(); _lobbyDesigns = FilterDesigns(_lobbyAll, _lobbyFilter); if (_lobbyPage * LobbyDesignRows >= _lobbyDesigns.Count) _lobbyPage = 0; }
        const int LobbyDesignRows = 9;
        RawImage _lobbyMap; RectTransform _lobbyMapRect; GameObject _lobbyNoMap;
        readonly List<(RectTransform rt, Image img, TextMeshProUGUI lbl)> _lobbyMarkers = new List<(RectTransform, Image, TextMeshProUGUI)>();
        int _placingTeam = -1;
        static readonly Color[] TeamCols = { new Color(0.29f, 0.56f, 1f), new Color(1f, 0.35f, 0.29f), new Color(0.29f, 0.82f, 0.42f), new Color(0.94f, 0.82f, 0.25f) };
        static string TeamHex(int t) => "#" + ColorUtility.ToHtmlStringRGB(TeamCols[Mathf.Clamp(t, 0, 3)]);

        void BuildLobbyPanel()
        {
            _lobbyPanel = Panel(_root.transform, "Lobby", new Vector2(600, -60), new Vector2(1000, 830), PanelCol);
            var t = _lobbyPanel.transform;
            Label(t, new Vector2(16, -12), new Vector2(960, 34), $"<b>MULTIPLAYER LOBBY</b>   <size=15><color=#9aa>[{Plugin.LobbyKey.Value}] close</color></size>", 24);

            // ---------------- left: players, team, ready, tank
            float y = -54;
            _lobbyPlayers = Label(t, new Vector2(16, y), new Vector2(470, 250), "", 16);
            _lobbyPlayers.alignment = TextAlignmentOptions.TopLeft; _lobbyPlayers.enableWordWrapping = false;
            y -= 258;
            Label(t, new Vector2(16, y), new Vector2(110, 30), "Your team", 18);
            for (int i = 0; i < Lobby.MaxTeams; i++)
            {
                int k = i;
                var b = MakeButton(t, new Vector2(126 + i * 88, y), new Vector2(82, 30), $"TEAM {i + 1}", FieldCol, () => Lobby.SetTeam((byte)k));
                b.Label.fontSize = 15; _lobbyTeamBtns.Add(b);
            }
            y -= 40;
            _lobbyReady = MakeButton(t, new Vector2(16, y), new Vector2(470, 36), "READY", FieldCol, () => { Lobby.ToggleReady(); _nextLobbyUi = 0; });
            y -= 46;
            Label(t, new Vector2(16, y), new Vector2(110, 26), "<b>Your tank</b>", 17);
            Label(t, new Vector2(126, y), new Vector2(80, 26), "<color=#9aa>search</color>", 15);
            _lobbySearch = MakeField(t, new Vector2(196, y), new Vector2(290, 26), "", 40, false, null);
            y -= 30;
            for (int i = 0; i < LobbyDesignRows; i++)
            {
                int row = i;
                var b = MakeButton(t, new Vector2(16, y), new Vector2(470, 30), "", FieldCol, () =>
                {
                    int idx = _lobbyPage * LobbyDesignRows + row;
                    if (idx < _lobbyDesigns.Count) Lobby.PickDesign(_lobbyDesigns[idx]);
                    _nextLobbyUi = 0;
                });
                b.Label.alignment = TextAlignmentOptions.Left; b.Label.margin = new Vector4(10, 0, 10, 0); b.Label.fontSize = 16;
                _lobbyDesignRows.Add(b); y -= 33;
            }
            MakeButton(t, new Vector2(16, y), new Vector2(90, 28), "< PREV", BtnCol, () => { if (_lobbyPage > 0) _lobbyPage--; _nextLobbyUi = 0; });
            _lobbyPageLbl = Label(t, new Vector2(112, y), new Vector2(170, 28), "", 15);
            MakeButton(t, new Vector2(286, y), new Vector2(90, 28), "NEXT >", BtnCol, () => { if ((_lobbyPage + 1) * LobbyDesignRows < _lobbyDesigns.Count) _lobbyPage++; _nextLobbyUi = 0; });
            MakeButton(t, new Vector2(382, y), new Vector2(104, 28), "REFRESH", new Color(0.25f, 0.25f, 0.3f, 1f), () => { LobbyRelist(); _nextLobbyUi = 0; });

            // ---------------- right: map, teams, spawns, start (host)
            float x = 506; y = -54;
            _lobbyMapPrev = MakeButton(t, new Vector2(x, y), new Vector2(40, 30), "<", BtnCol, () => CycleMap(-1));
            _lobbyMapLbl = Label(t, new Vector2(x + 46, y), new Vector2(386, 30), "", 19); _lobbyMapLbl.alignment = TextAlignmentOptions.Center;
            _lobbyMapNext = MakeButton(t, new Vector2(x + 438, y), new Vector2(40, 30), ">", BtnCol, () => CycleMap(1));
            y -= 38;
            Label(t, new Vector2(x, y), new Vector2(80, 28), "Teams", 17);
            for (int n = 2; n <= Lobby.MaxTeams; n++)
            {
                int k = n;
                var b = MakeButton(t, new Vector2(x + 80 + (n - 2) * 60, y), new Vector2(54, 28), n.ToString(), FieldCol, () => Lobby.HostSetTeams(k));
                _lobbyTeamCount.Add(b);
            }
            y -= 36;
            for (int i = 0; i < Lobby.MaxTeams; i++)
            {
                int k = i;
                var b = MakeButton(t, new Vector2(x, y), new Vector2(370, 28), "", FieldCol, () => { Lobby.HostCycleSpawn(k); _placingTeam = -1; });
                b.Label.alignment = TextAlignmentOptions.Left; b.Label.margin = new Vector4(10, 0, 6, 0); b.Label.fontSize = 15;
                _lobbySpawnBtns.Add(b);
                var pb = MakeButton(t, new Vector2(x + 376, y), new Vector2(102, 28), "PLACE", new Color(0.25f, 0.3f, 0.42f, 1f), () => { _placingTeam = _placingTeam == k ? -1 : k; _nextLobbyUi = 0; });
                pb.Label.fontSize = 14; _lobbyPlaceBtns.Add(pb);
                y -= 32;
            }
            y -= 4;
            // the map overview (north up)
            var mgo = new GameObject("Map"); mgo.transform.SetParent(t, false);
            _lobbyMapRect = mgo.AddComponent<RectTransform>();
            _lobbyMapRect.anchorMin = _lobbyMapRect.anchorMax = _lobbyMapRect.pivot = new Vector2(0, 1);
            _lobbyMapRect.anchoredPosition = new Vector2(x + 39, y); _lobbyMapRect.sizeDelta = new Vector2(400, 400);
            _lobbyMap = mgo.AddComponent<RawImage>(); _lobbyMap.raycastTarget = false; _lobbyMap.color = new Color(0.15f, 0.16f, 0.18f, 1f);
            var nm = Label(mgo.transform, new Vector2(20, -150), new Vector2(360, 100), "<color=#9aa>No overview of this map yet.\nPlay one battle on it (any battle) and it is drawn here; until then teams use the map's own spawns A / B.</color>", 15);
            nm.alignment = TextAlignmentOptions.Center; nm.enableWordWrapping = true; _lobbyNoMap = nm.gameObject;
            for (int i = 0; i < Lobby.MaxTeams + 2; i++)
            {
                var g = new GameObject("Marker"); g.transform.SetParent(mgo.transform, false);
                var rt = g.AddComponent<RectTransform>(); rt.anchorMin = rt.anchorMax = new Vector2(0, 0); rt.pivot = new Vector2(0.5f, 0.5f); rt.sizeDelta = new Vector2(22, 22);
                var img = g.AddComponent<Image>(); img.raycastTarget = false;
                var lg = new GameObject("L"); lg.transform.SetParent(g.transform, false);
                var lrt = lg.AddComponent<RectTransform>(); lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.sizeDelta = Vector2.zero;
                var lbl = lg.AddComponent<TextMeshProUGUI>(); StyleText(lbl, 15, TextAlignmentOptions.Center);
                _lobbyMarkers.Add((rt, img, lbl)); g.SetActive(false);
            }
            y -= 406;
            _lobbyMapHint = Label(t, new Vector2(x, y), new Vector2(478, 24), "", 14);
            y -= 28;
            _lobbyStart = MakeButton(t, new Vector2(x, y), new Vector2(478, 40), "START", BtnCol, () => Lobby.HostStart(false));
            _lobbyStart.Label.fontSize = 20;

            _lobbyStatus = Label(t, new Vector2(16, -790), new Vector2(968, 32), "", 15);
            _lobbyStatus.enableWordWrapping = true;
            _lobbyPanel.SetActive(false);
        }

        public void ToggleLobby()
        {
            if (_lobbyPanel == null) return;
            _lobbyOn = !_lobbyOn;
            if (_lobbyOn)
            {
                if (_designOn) ToggleDesignPanel();
                if (_cfgOn) ToggleConfigPanel();
                LobbyRelist(); _nextLobbyUi = 0; _placingTeam = -1;
                // the page with the chosen tank
                int sel = _lobbyDesigns.FindIndex(d => d.Path == Lobby.MyDesignPath);
                _lobbyPage = sel >= 0 ? sel / LobbyDesignRows : 0;
            }
            else Commit();
            _lobbyPanel.SetActive(_lobbyOn);
        }
        public void HideLobby() { if (_lobbyOn) ToggleLobby(); }

        void CycleMap(int dir)
        {
            if (!Runner.Session.IsHost) return;
            var maps = Lobby.Maps(); if (maps.Count == 0) return;
            int i = maps.FindIndex(m => m.Key == Lobby.Cur.Map);
            i = ((i < 0 ? 0 : i + dir) % maps.Count + maps.Count) % maps.Count;
            Lobby.HostSetMap(maps[i].Key, maps[i].Title);
            _placingTeam = -1;
        }

        void LobbyKeys(Keyboard kb)
        {
            try
            {
                if (_lobbyKeyName != Plugin.LobbyKey.Value) { _lobbyKeyName = Plugin.LobbyKey.Value; if (!Enum.TryParse(_lobbyKeyName, true, out _lobbyKey)) _lobbyKey = Key.None; }
                if (_lobbyKey != Key.None && kb[_lobbyKey].wasPressedThisFrame && !AnyFieldFocused()) ToggleLobby();
            }
            catch { }
        }

        // map overview <-> world
        bool MapToWorld(MapCapture.MapImage m, Vector2 screen, out Vector3 world)
        {
            world = default;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_lobbyMapRect, screen, null, out var lp)) return false;
            float u = lp.x / _lobbyMapRect.rect.width, v = 1f + lp.y / _lobbyMapRect.rect.height;   // pivot top-left
            if (u < 0 || u > 1 || v < 0 || v > 1) return false;
            world = new Vector3(m.MinX + u * m.Size, 0f, m.MinZ + v * m.Size);
            return true;
        }
        Vector2 WorldToMap(MapCapture.MapImage m, Vector3 w) => new Vector2((w.x - m.MinX) / m.Size * _lobbyMapRect.rect.width, (w.z - m.MinZ) / m.Size * _lobbyMapRect.rect.height);

        void UpdateLobbyPanel()
        {
            if (!_lobbyOn) return;
            var s = Runner.Session; bool host = s.IsHost, conn = Lobby.Connected;
            var cur = Lobby.Cur;
            var img = MapCapture.Get(cur.Map);

            // placing a spawn: click on the overview
            var mouse = Mouse.current;
            if (host && _placingTeam >= 0 && img != null && mouse != null && mouse.leftButton.wasPressedThisFrame && MapToWorld(img, mouse.position.ReadValue(), out var wp))
            {
                Lobby.HostPlaceSpawn(_placingTeam, wp); _placingTeam = -1; _nextLobbyUi = 0;
            }

            // search as you type
            string flt = _lobbySearch?.Value ?? "";
            if (flt != _lobbyFilter) { _lobbyFilter = flt; _lobbyDesigns = FilterDesigns(_lobbyAll, flt); _lobbyPage = 0; _nextLobbyUi = 0; }

            if (Time.realtimeSinceStartup < _nextLobbyUi) return;
            _nextLobbyUi = Time.realtimeSinceStartup + 0.15f;

            // players
            var sb = new StringBuilder();
            if (!conn) sb.Append("<color=#f88>Not connected.</color> Host or join a game in the [F10] menu first.\n");
            else
            {
                sb.Append("<b>Players</b>\n");
                foreach (var sl in cur.Slots.OrderBy(x => x.Team).ThenBy(x => x.Id))
                {
                    bool me = sl.Id == (host ? 0 : s.LocalId);
                    string tank = string.IsNullOrEmpty(sl.Design) ? "<color=#888>no tank</color>" : $"<color=#fd8>{sl.Design}</color>";
                    string state = sl.InBattle ? "<color=#cc8>in battle</color>" : sl.Ready ? "<color=#8f8>READY</color>" : "<color=#aaa>not ready</color>";
                    string des = !sl.HaveAll ? " <color=#f96>receiving designs...</color>" : "";
                    sb.Append($"<color={TeamHex(sl.Team)}><b>T{sl.Team + 1}</b></color> {(me ? "<b>" : "")}{sl.Name}{(me ? "</b> <color=#8c8>(you)</color>" : "")}{(sl.Id == 0 ? " <color=#9aa>(host)</color>" : "")} - {tank} - {state}{des}\n");
                }
                if (cur.Slots.Count == 0) sb.Append("<color=#9aa>waiting for the host...</color>\n");
            }
            _lobbyPlayers.text = sb.ToString();

            byte myTeam = Lobby.EffectiveMyTeam;
            for (int i = 0; i < _lobbyTeamBtns.Count; i++)
            {
                var b = _lobbyTeamBtns[i]; b.Rect.gameObject.SetActive(i < cur.Teams);
                b.Normal = i == myTeam ? TeamCols[i] * 0.75f + new Color(0, 0, 0, 0.25f) : FieldCol;
            }
            _lobbyReady.Normal = Lobby.MyReady ? new Color(0.2f, 0.55f, 0.25f, 1f) : new Color(0.45f, 0.3f, 0.15f, 1f);
            _lobbyReady.Label.text = Lobby.MyReady ? "<b>READY</b>  <size=14>(click to cancel)</size>" : string.IsNullOrEmpty(Lobby.MyDesign) ? "<b>PICK YOUR TANK, THEN CLICK HERE WHEN READY</b>" : $"<b>CLICK WHEN READY</b>  <size=14>({Lobby.MyDesign})</size>";

            for (int i = 0; i < LobbyDesignRows; i++)
            {
                int idx = _lobbyPage * LobbyDesignRows + i; var b = _lobbyDesignRows[i];
                if (idx >= _lobbyDesigns.Count) { b.Rect.gameObject.SetActive(false); continue; }
                b.Rect.gameObject.SetActive(true);
                var d = _lobbyDesigns[idx]; bool sel = d.Path == Lobby.MyDesignPath;
                b.Normal = sel ? new Color(0.25f, 0.42f, 0.28f, 1f) : FieldCol;
                b.Label.text = $"{(sel ? "<b>> " : "")}{d.Name}{(sel ? "</b>" : "")}  <color=#9aa><size=13>{d.Faction} - {d.Modified:dd/MM HH:mm}</size></color>";
            }
            int pages = Math.Max(1, (_lobbyDesigns.Count + LobbyDesignRows - 1) / LobbyDesignRows);
            _lobbyPageLbl.text = $"<color=#9aa>page {_lobbyPage + 1}/{pages}{(_lobbyFilter.Trim() != "" ? $" ({_lobbyDesigns.Count} found)" : "")}</color>";

            // map + teams + spawns
            _lobbyMapPrev.Rect.gameObject.SetActive(host); _lobbyMapNext.Rect.gameObject.SetActive(host);
            _lobbyMapLbl.text = $"<b>{(string.IsNullOrEmpty(cur.MapTitle) ? (string.IsNullOrEmpty(cur.Map) ? "no map" : cur.Map) : cur.MapTitle)}</b>";
            for (int i = 0; i < _lobbyTeamCount.Count; i++)
            {
                var b = _lobbyTeamCount[i]; b.Normal = cur.Teams == i + 2 ? BtnCol : FieldCol;
            }
            for (int i = 0; i < Lobby.MaxTeams; i++)
            {
                bool on = i < cur.Teams;
                var b = _lobbySpawnBtns[i]; b.Rect.gameObject.SetActive(on);
                _lobbyPlaceBtns[i].Rect.gameObject.SetActive(on && host && img != null);
                if (!on) continue;
                int n = cur.Slots.Count(x => x.Team == i);
                b.Label.text = $"<color={TeamHex(i)}><b>Team {i + 1}</b></color> ({n}) spawns at: <b>{Lobby.SpawnText(cur.Spawns[i])}</b>{(host ? " <color=#9aa><size=12>(click to change)</size></color>" : "")}";
                b.Normal = FieldCol;
                _lobbyPlaceBtns[i].Normal = _placingTeam == i ? new Color(0.6f, 0.45f, 0.15f, 1f) : new Color(0.25f, 0.3f, 0.42f, 1f);
                _lobbyPlaceBtns[i].Label.text = _placingTeam == i ? "<b>CLICK MAP</b>" : "<b>PLACE</b>";
            }
            if (img != null) { _lobbyMap.texture = img.Tex; _lobbyMap.color = Color.white; } else { _lobbyMap.texture = null; _lobbyMap.color = new Color(0.15f, 0.16f, 0.18f, 1f); }
            _lobbyNoMap.SetActive(img == null);
            int mk = 0;
            if (img != null)
            {
                void Mark(Vector3 w, Color c, string txt, float size)
                {
                    if (mk >= _lobbyMarkers.Count) return;
                    var m = _lobbyMarkers[mk++]; m.rt.gameObject.SetActive(true);
                    m.rt.anchoredPosition = WorldToMap(img, w); m.rt.sizeDelta = new Vector2(size, size);
                    m.img.color = c; m.lbl.text = "<b>" + txt + "</b>";
                }
                if (img.A.HasValue) Mark(img.A.Value, new Color(0, 0, 0, 0.55f), "A", 20);
                if (img.B.HasValue) Mark(img.B.Value, new Color(0, 0, 0, 0.55f), "B", 20);
                for (int i = 0; i < cur.Teams; i++)
                {
                    var sp = cur.Spawns[i];
                    Vector3? w = sp.Kind == Lobby.SpawnCustom ? sp.Pos : sp.Kind == Lobby.SpawnAttacker ? img.A : img.B;
                    if (w.HasValue) Mark(w.Value + new Vector3(i * 6f, 0, 0), TeamCols[i], (i + 1).ToString(), 24);
                }
            }
            for (int i = mk; i < _lobbyMarkers.Count; i++) _lobbyMarkers[i].rt.gameObject.SetActive(false);
            _lobbyMapHint.text = _placingTeam >= 0 ? $"<color=#fd8>Click on the map where team {_placingTeam + 1} spawns</color>"
                : img != null ? "<color=#9aa>A / B = the map's own spawns. North is up, grid = 250 m.</color>" : "";

            // start
            _lobbyStart.Rect.gameObject.SetActive(host);
            string why = host ? Lobby.StartBlocker() : "";
            _lobbyStart.Normal = why == "" ? new Color(0.2f, 0.55f, 0.25f, 1f) : new Color(0.3f, 0.3f, 0.3f, 1f);
            _lobbyStart.Label.text = why == "" ? "<b>START BATTLE</b>" : $"<b>START</b>  <size=14>({why})</size>";

            string note = Time.realtimeSinceStartup - Lobby.NoticeAt < 12f ? Lobby.Notice : "";
            _lobbyStatus.text = (note != "" ? note + "   " : "") + (host ? "" : "<color=#9aa>The host picks the map and spawns and starts the battle. </color>") + $"<color=#9aa><size=13>Other players' tanks go to {Lobby.MpFolder}</size></color>";
        }
    }
}
