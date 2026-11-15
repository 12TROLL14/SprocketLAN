using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SprocketMP.UI
{
    /// <summary>The multiplayer lobby panel (F9 / LOBBY button), three columns:
    /// - players (team, tank, state), your team, your tank's card (era / weight / cost against the rules), READY,
    ///   and the tank rules (the host edits them, everyone sees them);
    /// - your designs (search, era / weight / cost on each row, the ones that break the rules marked, "fits only" filter);
    /// - the battle: map, team count, team spawns (placed on the map overview) and START (host).</summary>
    public partial class MenuUI
    {
        GameObject _lobbyPanel; bool _lobbyOn; float _nextLobbyUi; string _lobbyKeyName; Key _lobbyKey = Key.None;
        TextMeshProUGUI _lobbyTitle, _lobbyPlayersHdr, _lobbyStatus, _lobbyMapLbl, _lobbyMapHint, _lobbyPageLbl, _lobbyCardName, _lobbyCardInfo, _lobbyCardCheck, _lobbyRulesText, _lobbyEraMinLbl, _lobbyEraMaxLbl, _lobbyRulesHdr;
        readonly List<Button> _lobbyTeamBtns = new List<Button>(), _lobbyDesignRows = new List<Button>(), _lobbyTeamCount = new List<Button>(), _lobbySpawnBtns = new List<Button>(), _lobbyPlaceBtns = new List<Button>();
        readonly List<GameObject> _lobbyHostOnly = new List<GameObject>();
        readonly List<(GameObject go, Image stripe, TextMeshProUGUI name, TextMeshProUGUI tank, TextMeshProUGUI state)> _lobbyRows = new List<(GameObject, Image, TextMeshProUGUI, TextMeshProUGUI, TextMeshProUGUI)>();
        Button _lobbyReady, _lobbyStart, _lobbyMapPrev, _lobbyMapNext, _lobbyFitsBtn;
        List<DesignShare.Design> _lobbyDesigns = new List<DesignShare.Design>(), _lobbyAll = new List<DesignShare.Design>(); int _lobbyPage;
        Field _lobbySearch, _lobbyMapSearch, _lobbyMaxMass, _lobbyMaxCost; string _lobbyFilter = "", _mapFilter = "", _rulesSeen = "";
        bool _lobbyFitsOnly;

        const int LobbyDesignRows = 14, LobbyPlayerRows = 10;
        const float LW = 1180f, LH = 840f, ColA = 16f, ColAW = 420f, ColB = 452f, ColBW = 360f, ColC = 828f, ColCW = 336f;
        static readonly Color CardCol = new Color(0.12f, 0.13f, 0.15f, 1f);
        static readonly Color LineCol = new Color(0.35f, 0.4f, 0.48f, 0.6f);
        static readonly Color GoodCol = new Color(0.2f, 0.5f, 0.27f, 1f), WaitCol = new Color(0.45f, 0.32f, 0.15f, 1f), BadCol = new Color(0.4f, 0.18f, 0.17f, 1f), OffCol = new Color(0.27f, 0.27f, 0.29f, 1f);

        RawImage _lobbyMap; RectTransform _lobbyMapRect; GameObject _lobbyNoMap;
        readonly List<(RectTransform rt, Image img, TextMeshProUGUI lbl)> _lobbyMarkers = new List<(RectTransform, Image, TextMeshProUGUI)>();
        int _placingTeam = -1;
        static readonly Color[] TeamCols = { new Color(0.29f, 0.56f, 1f), new Color(1f, 0.35f, 0.29f), new Color(0.29f, 0.82f, 0.42f), new Color(0.94f, 0.82f, 0.25f) };
        static string TeamHex(int t) => "#" + ColorUtility.ToHtmlStringRGB(TeamCols[Mathf.Clamp(t, 0, 3)]);
        static string NoTags(string s) => (s ?? "").Replace("<", "(").Replace(">", ")");

        /// <summary>Maps matching the map search box (all of them when it's empty).</summary>
        List<Lobby.MapEntry> FilteredMaps()
        {
            var maps = Lobby.Maps();
            var f = (_lobbyMapSearch?.Value ?? "").Trim();
            if (f == "") return maps;
            var words = f.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return maps.Where(m => words.All(w => (m.Title + " " + m.Key).ToLowerInvariant().Contains(w))).ToList();
        }
        void MapSearchTick()
        {
            if (_lobbyMapSearch == null || !Runner.Session.IsHost) return;
            var f = (_lobbyMapSearch.Value ?? "").Trim();
            if (f == _mapFilter) return;
            _mapFilter = f;
            if (f == "") return;
            var m = FilteredMaps();
            if (m.Count > 0 && !m.Any(x => x.Key == Lobby.Cur.Map)) { Lobby.HostSetMap(m[0].Key, m[0].Title); _placingTeam = -1; }
        }
        void LobbyRelist()
        {
            _lobbyAll = DesignShare.ListDesigns();
            var l = FilterDesigns(_lobbyAll, _lobbyFilter);
            if (_lobbyFitsOnly) { var r = Lobby.Cur.Rules; l = l.Where(d => r.Check(d.Path) == "").ToList(); }
            _lobbyDesigns = l;
            if (_lobbyPage * LobbyDesignRows >= _lobbyDesigns.Count) _lobbyPage = 0;
        }

        // ------------------------------------------------------------------------------------------- layout helpers
        void Section(Transform t, float x, float y, float w, string title, out TextMeshProUGUI lbl)
        {
            lbl = Label(t, new Vector2(x, y), new Vector2(w, 24), $"<b><color=#8fb8e8>{title}</color></b>", 15);
            var line = Panel(t, "Line", new Vector2(x, y - 24), new Vector2(w, 1), LineCol);
            line.GetComponent<Image>().raycastTarget = false;
        }
        void Section(Transform t, float x, float y, float w, string title) => Section(t, x, y, w, title, out _);
        TextMeshProUGUI Small(Transform t, Vector2 pos, Vector2 size, string text, float fs = 14, TextAlignmentOptions a = TextAlignmentOptions.Left)
        {
            var l = Label(t, pos, size, text, fs); l.alignment = a; l.enableWordWrapping = false; l.overflowMode = TextOverflowModes.Ellipsis; return l;
        }
        T HostOnly<T>(T x) where T : class
        {
            var go = x is Button b ? b.Rect.gameObject : x is Field f ? f.Rect.gameObject : x is Component c ? c.gameObject : x as GameObject;
            if (go != null) _lobbyHostOnly.Add(go);
            return x;
        }

        void BuildLobbyPanel()
        {
            _lobbyPanel = Panel(_root.transform, "Lobby", new Vector2(596, -40), new Vector2(LW, LH), PanelCol);
            var t = _lobbyPanel.transform;
            // ---------------- title bar
            var bar = Panel(t, "Bar", new Vector2(0, 0), new Vector2(LW, 46), new Color(0.12f, 0.15f, 0.2f, 1f)); bar.GetComponent<Image>().raycastTarget = false;
            Label(t, new Vector2(16, -8), new Vector2(330, 32), "<b>MULTIPLAYER LOBBY</b>", 24);
            _lobbyTitle = Small(t, new Vector2(340, -11), new Vector2(LW - 340 - 130, 26), "", 16, TextAlignmentOptions.Right);
            MakeButton(t, new Vector2(LW - 118, -9), new Vector2(104, 28), $"CLOSE [{Plugin.LobbyKey.Value}]", OffCol, ToggleLobby).Label.fontSize = 14;

            // ================= column A: players, your setup, rules
            float x = ColA, y = -58;
            Section(t, x, y, ColAW, "PLAYERS", out _lobbyPlayersHdr); y -= 32;
            for (int i = 0; i < LobbyPlayerRows; i++)
            {
                var row = Panel(t, "PlayerRow", new Vector2(x, y), new Vector2(ColAW, 28), CardCol); row.GetComponent<Image>().raycastTarget = false;
                var stripe = Panel(row.transform, "Team", new Vector2(0, 0), new Vector2(5, 28), Color.white).GetComponent<Image>(); stripe.raycastTarget = false;
                var nm = Small(row.transform, new Vector2(12, -3), new Vector2(150, 22), "", 15);
                var tk = Small(row.transform, new Vector2(166, -3), new Vector2(160, 22), "", 14);
                var st = Small(row.transform, new Vector2(ColAW - 96, -3), new Vector2(88, 22), "", 13, TextAlignmentOptions.Right);
                _lobbyRows.Add((row, stripe, nm, tk, st)); y -= 30;
            }
            y -= 8;
            Section(t, x, y, ColAW, "YOUR TEAM"); y -= 32;
            for (int i = 0; i < Lobby.MaxTeams; i++)
            {
                int k = i;
                var b = MakeButton(t, new Vector2(x + i * 106, y), new Vector2(100, 30), $"TEAM {i + 1}", FieldCol, () => { Lobby.SetTeam((byte)k); _nextLobbyUi = 0; });
                b.Label.fontSize = 15; _lobbyTeamBtns.Add(b);
            }
            y -= 42;
            Section(t, x, y, ColAW, "YOUR TANK"); y -= 32;
            var card = Panel(t, "Card", new Vector2(x, y), new Vector2(ColAW, 78), CardCol); card.GetComponent<Image>().raycastTarget = false;
            _lobbyCardName = Small(card.transform, new Vector2(12, -6), new Vector2(ColAW - 24, 26), "", 19);
            _lobbyCardInfo = Small(card.transform, new Vector2(12, -32), new Vector2(ColAW - 24, 20), "", 14);
            _lobbyCardCheck = Small(card.transform, new Vector2(12, -52), new Vector2(ColAW - 24, 20), "", 14);
            y -= 86;
            _lobbyReady = MakeButton(t, new Vector2(x, y), new Vector2(ColAW, 44), "READY", WaitCol, () => { Lobby.ToggleReady(); _nextLobbyUi = 0; });
            _lobbyReady.Label.fontSize = 18;
            y -= 56;
            Section(t, x, y, ColAW, "TANK RULES", out _lobbyRulesHdr); y -= 32;
            float ry = y;
            _lobbyRulesText = Small(t, new Vector2(x, ry), new Vector2(ColAW, 60), "", 15); _lobbyRulesText.enableWordWrapping = true; _lobbyRulesText.alignment = TextAlignmentOptions.TopLeft;
            // host controls (same place)
            HostOnly(Small(t, new Vector2(x, ry - 3), new Vector2(80, 24), "Era from", 15));
            HostOnly(MakeButton(t, new Vector2(x + 82, ry), new Vector2(30, 28), "<", BtnCol, () => StepEra(true, -1)));
            _lobbyEraMinLbl = HostOnly(Small(t, new Vector2(x + 114, ry - 3), new Vector2(92, 24), "", 15, TextAlignmentOptions.Center));
            HostOnly(MakeButton(t, new Vector2(x + 208, ry), new Vector2(30, 28), ">", BtnCol, () => StepEra(true, 1)));
            HostOnly(Small(t, new Vector2(x + 246, ry - 3), new Vector2(26, 24), "to", 15));
            HostOnly(MakeButton(t, new Vector2(x + 272, ry), new Vector2(30, 28), "<", BtnCol, () => StepEra(false, -1)));
            _lobbyEraMaxLbl = HostOnly(Small(t, new Vector2(x + 304, ry - 3), new Vector2(84, 24), "", 15, TextAlignmentOptions.Center));
            HostOnly(MakeButton(t, new Vector2(x + 390, ry), new Vector2(30, 28), ">", BtnCol, () => StepEra(false, 1)));
            ry -= 34;
            HostOnly(Small(t, new Vector2(x, ry - 3), new Vector2(120, 24), "Max weight (t)", 15));
            _lobbyMaxMass = HostOnly(MakeField(t, new Vector2(x + 122, ry), new Vector2(76, 28), "", 7, false, null));
            HostOnly(Small(t, new Vector2(x + 210, ry - 3), new Vector2(80, 24), "Max cost", 15));
            _lobbyMaxCost = HostOnly(MakeField(t, new Vector2(x + 290, ry), new Vector2(130, 28), "", 10, true, null));
            try { _lobbyMaxMass.Input.contentType = TMP_InputField.ContentType.DecimalNumber; } catch { }
            ry -= 30;
            HostOnly(Small(t, new Vector2(x, ry - 2), new Vector2(ColAW, 20), "<color=#9aa>Empty or 0 = no limit. Players can't READY with a tank that breaks them.</color>", 13));

            // ================= column B: your designs
            x = ColB; y = -58;
            Section(t, x, y, ColBW, "PICK YOUR TANK"); y -= 32;
            _lobbySearch = MakeField(t, new Vector2(x, y), new Vector2(ColBW - 108, 28), "", 40, false, null);
            MakeButton(t, new Vector2(x + ColBW - 102, y), new Vector2(102, 28), "REFRESH", OffCol, () => { LobbyRelist(); _nextLobbyUi = 0; }).Label.fontSize = 14;
            y -= 34;
            for (int i = 0; i < LobbyDesignRows; i++)
            {
                int row = i;
                var b = MakeButton(t, new Vector2(x, y), new Vector2(ColBW, 38), "", FieldCol, () =>
                {
                    int idx = _lobbyPage * LobbyDesignRows + row;
                    if (idx < _lobbyDesigns.Count) Lobby.PickDesign(_lobbyDesigns[idx]);
                    _nextLobbyUi = 0;
                });
                b.Label.alignment = TextAlignmentOptions.Left; b.Label.margin = new Vector4(10, 0, 8, 0); b.Label.fontSize = 16; b.Label.lineSpacing = -10f;
                b.Label.enableWordWrapping = false; b.Label.overflowMode = TextOverflowModes.Ellipsis;
                _lobbyDesignRows.Add(b); y -= 40;
            }
            y -= 4;
            MakeButton(t, new Vector2(x, y), new Vector2(50, 28), "<", BtnCol, () => { if (_lobbyPage > 0) _lobbyPage--; _nextLobbyUi = 0; });
            _lobbyPageLbl = Small(t, new Vector2(x + 54, y - 2), new Vector2(120, 24), "", 14, TextAlignmentOptions.Center);
            MakeButton(t, new Vector2(x + 178, y), new Vector2(50, 28), ">", BtnCol, () => { if ((_lobbyPage + 1) * LobbyDesignRows < _lobbyDesigns.Count) _lobbyPage++; _nextLobbyUi = 0; });
            _lobbyFitsBtn = MakeButton(t, new Vector2(x + 236, y), new Vector2(ColBW - 236, 28), "", OffCol, () => { _lobbyFitsOnly = !_lobbyFitsOnly; _lobbyPage = 0; LobbyRelist(); _nextLobbyUi = 0; });
            _lobbyFitsBtn.Label.fontSize = 13;

            // ================= column C: the battle
            x = ColC; y = -58;
            Section(t, x, y, ColCW, "BATTLE"); y -= 32;
            _lobbyMapPrev = MakeButton(t, new Vector2(x, y), new Vector2(32, 30), "<", BtnCol, () => CycleMap(-1));
            _lobbyMapLbl = Small(t, new Vector2(x + 36, y - 2), new Vector2(ColCW - 72, 26), "", 18, TextAlignmentOptions.Center);
            _lobbyMapNext = MakeButton(t, new Vector2(x + ColCW - 32, y), new Vector2(32, 30), ">", BtnCol, () => CycleMap(1));
            y -= 36;
            HostOnly(Small(t, new Vector2(x, y - 2), new Vector2(90, 24), "<color=#9aa>Find map</color>", 14));
            _lobbyMapSearch = HostOnly(MakeField(t, new Vector2(x + 90, y), new Vector2(ColCW - 90, 28), "", 30, false, null));   // typing picks the first map that matches
            y -= 36;
            Small(t, new Vector2(x, y - 2), new Vector2(80, 24), "Teams", 16);
            for (int n = 2; n <= Lobby.MaxTeams; n++)
            {
                int k = n;
                var b = MakeButton(t, new Vector2(x + 80 + (n - 2) * 58, y), new Vector2(52, 28), n.ToString(), FieldCol, () => Lobby.HostSetTeams(k));
                _lobbyTeamCount.Add(b);
            }
            y -= 34;
            for (int i = 0; i < Lobby.MaxTeams; i++)
            {
                int k = i;
                var b = MakeButton(t, new Vector2(x, y), new Vector2(ColCW - 86, 26), "", FieldCol, () => { Lobby.HostCycleSpawn(k); _placingTeam = -1; });
                b.Label.alignment = TextAlignmentOptions.Left; b.Label.margin = new Vector4(8, 0, 6, 0); b.Label.fontSize = 14;
                _lobbySpawnBtns.Add(b);
                var pb = MakeButton(t, new Vector2(x + ColCW - 80, y), new Vector2(80, 26), "PLACE", new Color(0.25f, 0.3f, 0.42f, 1f), () => { _placingTeam = _placingTeam == k ? -1 : k; _nextLobbyUi = 0; });
                pb.Label.fontSize = 13; _lobbyPlaceBtns.Add(pb);
                y -= 29;
            }
            y -= 4;
            // the map overview (north up)
            var mgo = new GameObject("Map"); mgo.transform.SetParent(t, false);
            _lobbyMapRect = mgo.AddComponent<RectTransform>();
            _lobbyMapRect.anchorMin = _lobbyMapRect.anchorMax = _lobbyMapRect.pivot = new Vector2(0, 1);
            _lobbyMapRect.anchoredPosition = new Vector2(x, y); _lobbyMapRect.sizeDelta = new Vector2(ColCW, ColCW);
            _lobbyMap = mgo.AddComponent<RawImage>(); _lobbyMap.raycastTarget = false; _lobbyMap.color = new Color(0.15f, 0.16f, 0.18f, 1f);
            var nm2 = Label(mgo.transform, new Vector2(16, -120), new Vector2(ColCW - 32, 100), "<color=#9aa>No overview of this map yet.\nPlay one battle on it (any battle) and it is drawn here; until then teams use the map's own spawns A / B.</color>", 14);
            nm2.alignment = TextAlignmentOptions.Center; nm2.enableWordWrapping = true; _lobbyNoMap = nm2.gameObject;
            for (int i = 0; i < Lobby.MaxTeams + 2; i++)
            {
                var g = new GameObject("Marker"); g.transform.SetParent(mgo.transform, false);
                var rt = g.AddComponent<RectTransform>(); rt.anchorMin = rt.anchorMax = new Vector2(0, 0); rt.pivot = new Vector2(0.5f, 0.5f); rt.sizeDelta = new Vector2(22, 22);
                var img = g.AddComponent<Image>(); img.raycastTarget = false;
                var lg = new GameObject("L"); lg.transform.SetParent(g.transform, false);
                var lrt = lg.AddComponent<RectTransform>(); lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.sizeDelta = Vector2.zero;
                var lbl = lg.AddComponent<TextMeshProUGUI>(); StyleText(lbl, 14, TextAlignmentOptions.Center);
                _lobbyMarkers.Add((rt, img, lbl)); g.SetActive(false);
            }
            y -= ColCW + 4;
            _lobbyMapHint = Small(t, new Vector2(x, y), new Vector2(ColCW, 20), "", 13);
            y -= 26;
            _lobbyStart = MakeButton(t, new Vector2(x, y), new Vector2(ColCW, 46), "START", BtnCol, () => Lobby.HostStart(false));
            _lobbyStart.Label.fontSize = 18;

            // ---------------- status line
            Panel(t, "Line", new Vector2(16, -LH + 44), new Vector2(LW - 32, 1), LineCol).GetComponent<Image>().raycastTarget = false;
            _lobbyStatus = Small(t, new Vector2(16, -LH + 38), new Vector2(LW - 32, 28), "", 14);
            _lobbyPanel.SetActive(false);
        }

        void StepEra(bool min, int dir)
        {
            if (!Runner.Session.IsHost) return;
            var r = Lobby.HostRules.Copy(); int n = Eras.All.Count;
            // 255 = no limit, shown at the open end of the list
            int v = min ? (r.EraMin == 255 ? -1 : r.EraMin) : (r.EraMax == 255 ? n : r.EraMax);
            v += dir;
            if (min) { v = Mathf.Clamp(v, -1, n - 1); r.EraMin = v < 0 ? (byte)255 : (byte)v; if (r.EraMin != 255 && r.EraMax != 255 && r.EraMax < r.EraMin) r.EraMax = r.EraMin; }
            else { v = Mathf.Clamp(v, 0, n); r.EraMax = v >= n ? (byte)255 : (byte)v; if (r.EraMin != 255 && r.EraMax != 255 && r.EraMin > r.EraMax) r.EraMin = r.EraMax; }
            Lobby.HostSetRules(r); _nextLobbyUi = 0;
        }
        /// <summary>Host: the weight / cost fields apply when they lose focus (or Enter).</summary>
        void RulesFieldsTick()
        {
            if (!Runner.Session.IsHost || _lobbyMaxMass == null) return;
            bool focused = false; try { focused = _lobbyMaxMass.Input.isFocused || _lobbyMaxCost.Input.isFocused; } catch { }
            var r = Lobby.HostRules;
            if (focused) return;
            float.TryParse((_lobbyMaxMass.Value ?? "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var t);
            int.TryParse(_lobbyMaxCost.Value, out var c);
            int kg = Mathf.Clamp(Mathf.RoundToInt(t * 1000f), 0, 100000000); c = Mathf.Clamp(c, 0, 1000000000);
            if (kg != r.MaxMassKg || c != r.MaxCost) { var n = r.Copy(); n.MaxMassKg = kg; n.MaxCost = c; Lobby.HostSetRules(n); }
            ShowRulesInFields();
        }
        void ShowRulesInFields()
        {
            var r = Lobby.HostRules;
            string m = r.MaxMassKg > 0 ? (r.MaxMassKg / 1000f).ToString("0.###", CultureInfo.InvariantCulture) : "", c = r.MaxCost > 0 ? r.MaxCost.ToString() : "";
            try { if (_lobbyMaxMass.Input.text != m) _lobbyMaxMass.Input.text = m; if (_lobbyMaxCost.Input.text != c) _lobbyMaxCost.Input.text = c; } catch { }
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
                if (_lobbyMaxMass != null) ShowRulesInFields();
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
            var maps = FilteredMaps(); if (maps.Count == 0) maps = Lobby.Maps(); if (maps.Count == 0) return;
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
            try { MapSearchTick(); } catch { }
            if (!_lobbyOn) return;
            var s = Runner.Session; bool host = s.IsHost, conn = Lobby.Connected;
            var cur = Lobby.Cur; var rules = cur.Rules;
            var img = MapCapture.Get(cur.Map);

            // placing a spawn: click on the overview
            var mouse = Mouse.current;
            if (host && _placingTeam >= 0 && img != null && mouse != null && mouse.leftButton.wasPressedThisFrame && MapToWorld(img, mouse.position.ReadValue(), out var wp))
            {
                Lobby.HostPlaceSpawn(_placingTeam, wp); _placingTeam = -1; _nextLobbyUi = 0;
            }
            try { RulesFieldsTick(); } catch { }

            // search as you type; the rules changed: the "fits" filter / marks follow
            string flt = _lobbySearch?.Value ?? "";
            string rc = rules.Code();
            if (flt != _lobbyFilter || rc != _rulesSeen) { bool f = flt != _lobbyFilter; _lobbyFilter = flt; _rulesSeen = rc; LobbyRelist(); if (f) _lobbyPage = 0; _nextLobbyUi = 0; }

            if (Time.realtimeSinceStartup < _nextLobbyUi) return;
            _nextLobbyUi = Time.realtimeSinceStartup + 0.15f;

            // ---- title bar
            string hostName = cur.Slots.FirstOrDefault(x => x.Id == 0)?.Name ?? "";
            _lobbyTitle.text = !conn ? "<color=#f88>Not connected - host or join a game in the [F10] menu first</color>"
                : host ? $"<color=#9aa>You are hosting</color>  ·  <b>{cur.Slots.Count}</b> player{(cur.Slots.Count == 1 ? "" : "s")}"
                : $"<color=#9aa>Game of</color> <b>{NoTags(hostName)}</b>  ·  <b>{cur.Slots.Count}</b> player{(cur.Slots.Count == 1 ? "" : "s")}";

            // ---- players
            int ready = cur.Slots.Count(x => x.Ready);
            _lobbyPlayersHdr.text = $"<b><color=#8fb8e8>PLAYERS</color></b>  <color=#9aa>{ready}/{cur.Slots.Count} ready</color>";
            var slots = cur.Slots.OrderBy(x => x.Team).ThenBy(x => x.Id).ToList();
            byte me = host ? (byte)0 : s.LocalId;
            for (int i = 0; i < _lobbyRows.Count; i++)
            {
                var row = _lobbyRows[i];
                if (i >= slots.Count)
                {
                    row.go.SetActive(conn && i == 0 && slots.Count == 0);
                    if (row.go.activeSelf) { row.stripe.color = OffCol; row.name.text = "<color=#9aa>waiting for the host...</color>"; row.tank.text = ""; row.state.text = ""; }
                    continue;
                }
                if (i == _lobbyRows.Count - 1 && slots.Count > _lobbyRows.Count) { row.go.SetActive(true); row.stripe.color = OffCol; row.name.text = $"<color=#9aa>+ {slots.Count - i} more</color>"; row.tank.text = ""; row.state.text = ""; continue; }
                var sl = slots[i]; row.go.SetActive(true);
                row.stripe.color = TeamCols[Mathf.Clamp(sl.Team, 0, 3)];
                bool mine = sl.Id == me;
                row.name.text = $"{(mine ? "<b>" : "")}<color={TeamHex(sl.Team)}>{sl.Team + 1}</color> {NoTags(sl.Name)}{(mine ? "</b> <color=#8c8><size=12>you</size></color>" : "")}{(sl.Id == 0 ? " <color=#9aa><size=12>host</size></color>" : "")}";
                row.tank.text = string.IsNullOrEmpty(sl.Design) ? "<color=#777>no tank yet</color>" : sl.BreaksRules ? $"<color=#f88>{NoTags(sl.Design)}</color>" : $"<color=#fd8>{NoTags(sl.Design)}</color>";
                row.state.text = sl.InBattle ? "<color=#cc8>IN BATTLE</color>" : sl.BreaksRules ? "<color=#f88>BREAKS RULES</color>" : !sl.HaveAll ? "<color=#f96>RECEIVING</color>" : sl.Ready ? "<color=#8f8><b>READY</b></color>" : "<color=#999>not ready</color>";
            }

            // ---- your team
            byte myTeam = Lobby.EffectiveMyTeam;
            for (int i = 0; i < _lobbyTeamBtns.Count; i++)
            {
                var b = _lobbyTeamBtns[i]; b.Rect.gameObject.SetActive(i < cur.Teams);
                b.Normal = i == myTeam ? TeamCols[i] * 0.7f + new Color(0, 0, 0, 0.3f) : FieldCol;
                b.Label.text = i == myTeam ? $"<b>TEAM {i + 1}</b> <size=12>(you)</size>" : $"TEAM {i + 1}";
            }

            // ---- your tank card + ready
            string myWhy = Lobby.MyRuleProblem;
            if (string.IsNullOrEmpty(Lobby.MyDesign))
            {
                _lobbyCardName.text = "<color=#9aa>No tank picked</color>";
                _lobbyCardInfo.text = "<color=#9aa>Pick one in the list in the middle.</color>"; _lobbyCardCheck.text = "";
            }
            else
            {
                var di = DesignInfo.Of(Lobby.MyDesignPath);
                _lobbyCardName.text = $"<b>{NoTags(Lobby.MyDesign)}</b>";
                _lobbyCardInfo.text = di.Ok ? $"<color=#cde>{di.EraTitle}</color>  ·  {di.MassKg / 1000f:0.#} t  ·  cost {DesignInfo.Money(di.Cost)}  <color=#9aa>({di.Date:yyyy-MM-dd})</color>" : "<color=#f96>Can't read this design's header</color>";
                _lobbyCardCheck.text = !rules.Any ? "<color=#9aa>No tank rules in this lobby</color>" : myWhy == "" ? "<color=#8f8>Meets the lobby's rules</color>" : $"<color=#f88>Breaks the rules: {myWhy}</color>";
            }
            bool canReady = !string.IsNullOrEmpty(Lobby.MyDesign) && myWhy == "" && conn;
            _lobbyReady.Normal = Lobby.MyReady ? GoodCol : canReady ? WaitCol : OffCol;
            _lobbyReady.Label.text = !conn ? "<b>NOT CONNECTED</b>"
                : Lobby.MyReady ? "<b>READY</b>  <size=13>(click to cancel)</size>"
                : string.IsNullOrEmpty(Lobby.MyDesign) ? "<b>PICK A TANK FIRST</b>"
                : myWhy != "" ? "<b>THIS TANK BREAKS THE RULES</b>"
                : "<b>CLICK WHEN READY</b>";

            // ---- rules
            foreach (var go in _lobbyHostOnly) go.SetActive(host && conn);
            _lobbyRulesText.gameObject.SetActive(!(host && conn));
            _lobbyRulesHdr.text = $"<b><color=#8fb8e8>TANK RULES</color></b>  <color=#9aa>{(host ? "you set them" : "set by the host")}</color>";
            if (host && conn)
            {
                var hr = Lobby.HostRules;
                _lobbyEraMinLbl.text = hr.EraMin == 255 ? "<color=#9aa>any</color>" : $"<b>{Eras.Title(hr.EraMin)}</b>";
                _lobbyEraMaxLbl.text = hr.EraMax == 255 ? "<color=#9aa>any</color>" : $"<b>{Eras.Title(hr.EraMax)}</b>";
            }
            else _lobbyRulesText.text = !conn ? "" : !rules.Any ? "<color=#9aa>Any tank can be used.</color>"
                : $"<color=#fd8>{(rules.EraMin != 255 || rules.EraMax != 255 ? "Era: <b>" + rules.EraText() + "</b>" : "Any era")}" +
                  $"{(rules.MaxMassKg > 0 ? $"   ·   Max weight: <b>{rules.MaxMassKg / 1000f:0.#} t</b>" : "")}" +
                  $"{(rules.MaxCost > 0 ? $"   ·   Max cost: <b>{DesignInfo.Money(rules.MaxCost)}</b>" : "")}</color>";

            // ---- design list
            for (int i = 0; i < LobbyDesignRows; i++)
            {
                int idx = _lobbyPage * LobbyDesignRows + i; var b = _lobbyDesignRows[i];
                if (idx >= _lobbyDesigns.Count) { b.Rect.gameObject.SetActive(false); continue; }
                b.Rect.gameObject.SetActive(true);
                var d = _lobbyDesigns[idx]; bool sel = d.Path == Lobby.MyDesignPath;
                var di = DesignInfo.Of(d.Path); string why = rules.Check(di);
                b.Normal = sel ? new Color(0.22f, 0.4f, 0.26f, 1f) : why != "" ? new Color(0.2f, 0.15f, 0.15f, 1f) : FieldCol;
                string second = why != "" ? $"<color=#f88>{why}</color>" : $"<color=#9aa>{di.Short}  ·  {NoTags(d.Faction)}</color>";
                b.Label.text = $"{(sel ? "<b>> " : "")}{(why != "" ? "<color=#bbb>" : "")}{NoTags(d.Name)}{(why != "" ? "</color>" : "")}{(sel ? "</b>" : "")}\n<size=12>{second}</size>";
            }
            int pages = Math.Max(1, (_lobbyDesigns.Count + LobbyDesignRows - 1) / LobbyDesignRows);
            _lobbyPageLbl.text = $"<color=#9aa>{_lobbyPage + 1} / {pages}  <size=12>({_lobbyDesigns.Count})</size></color>";
            _lobbyFitsBtn.Rect.gameObject.SetActive(rules.Any);
            _lobbyFitsBtn.Normal = _lobbyFitsOnly ? new Color(0.25f, 0.38f, 0.5f, 1f) : OffCol;
            _lobbyFitsBtn.Label.text = _lobbyFitsOnly ? "<b>ONLY TANKS THAT FIT</b>" : "<b>SHOWING ALL TANKS</b>";
            if (!rules.Any && _lobbyFitsOnly) { _lobbyFitsOnly = false; LobbyRelist(); }

            // ---- map + teams + spawns
            _lobbyMapPrev.Rect.gameObject.SetActive(host); _lobbyMapNext.Rect.gameObject.SetActive(host);
            _lobbyMapLbl.text = $"<b>{NoTags(string.IsNullOrEmpty(cur.MapTitle) ? (string.IsNullOrEmpty(cur.Map) ? "no map" : cur.Map) : cur.MapTitle)}</b>";
            for (int i = 0; i < _lobbyTeamCount.Count; i++) { var b = _lobbyTeamCount[i]; b.Normal = cur.Teams == i + 2 ? BtnCol : FieldCol; }
            for (int i = 0; i < Lobby.MaxTeams; i++)
            {
                bool on = i < cur.Teams;
                var b = _lobbySpawnBtns[i]; b.Rect.gameObject.SetActive(on);
                _lobbyPlaceBtns[i].Rect.gameObject.SetActive(on && host && img != null);
                if (!on) continue;
                int n = cur.Slots.Count(x => x.Team == i);
                b.Label.text = $"<color={TeamHex(i)}><b>Team {i + 1}</b></color> <color=#9aa>({n})</color>  {Lobby.SpawnText(cur.Spawns[i])}";
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
                if (img.A.HasValue) Mark(img.A.Value, new Color(0, 0, 0, 0.55f), "A", 18);
                if (img.B.HasValue) Mark(img.B.Value, new Color(0, 0, 0, 0.55f), "B", 18);
                for (int i = 0; i < cur.Teams; i++)
                {
                    var sp = cur.Spawns[i];
                    Vector3? w = sp.Kind == Lobby.SpawnCustom ? sp.Pos : sp.Kind == Lobby.SpawnAttacker ? img.A : img.B;
                    if (w.HasValue) Mark(w.Value + new Vector3(i * 6f, 0, 0), TeamCols[i], (i + 1).ToString(), 22);
                }
            }
            for (int i = mk; i < _lobbyMarkers.Count; i++) _lobbyMarkers[i].rt.gameObject.SetActive(false);
            _lobbyMapHint.text = _placingTeam >= 0 ? $"<color=#fd8>Click on the map where team {_placingTeam + 1} spawns</color>"
                : img != null ? "<color=#9aa>A / B = the map's spawns · north up · grid 250 m" + (host ? " · click a team to switch spawn" : "") + "</color>" : "";

            // ---- start
            _lobbyStart.Rect.gameObject.SetActive(conn);
            string blocker = host ? Lobby.StartBlocker() : "";
            if (host)
            {
                _lobbyStart.Normal = blocker == "" ? GoodCol : OffCol;
                _lobbyStart.Label.text = blocker == "" ? "<b>START BATTLE</b>" : $"<b>START</b>\n<size=13><color=#ddd>{NoTags(blocker)}</color></size>";
            }
            else
            {
                _lobbyStart.Normal = CardCol;
                _lobbyStart.Label.text = $"<color=#9aa>The host starts the battle</color>\n<size=13><color=#9aa>{ready}/{cur.Slots.Count} ready</color></size>";
            }

            string note = Time.realtimeSinceStartup - Lobby.NoticeAt < 12f ? Lobby.Notice : "";
            _lobbyStatus.text = note != "" ? note : $"<color=#9aa>Other players' tanks are saved to {NoTags(Lobby.MpFolder)}</color>";
        }
    }
}
