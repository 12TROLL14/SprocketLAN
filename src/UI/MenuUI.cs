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
    public partial class MenuUI
    {
        class Button { public RectTransform Rect; public Image Img; public Color Normal; public Action OnClick; public TextMeshProUGUI Label; }
        class Field { public RectTransform Rect; public TMP_InputField Input; public Action<string> Commit; public bool WasFocused; public string Value => Input != null ? Input.text : ""; }

        GameObject _root, _panel, _hud, _feed, _death, _kf, _fc;
        TextMeshProUGUI _status, _hudText, _feedText, _deathText, _kfText, _fcText;
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
            _inst = this;
            if (_stabBtn != null) try { _stabBtn.Label.text = $"<b>STAB : {(Stabilizer.On ? "ON" : "OFF")}</b>  [{Plugin.StabKey.Value}]"; _stabBtn.Normal = Stabilizer.On ? new Color(0.2f, 0.55f, 0.25f, 1f) : new Color(0.3f, 0.3f, 0.3f, 1f); } catch { }
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
                if (kb.f10Key.wasPressedThisFrame && !AnyFieldFocused()) { _visible = !_visible; if (!_visible) Commit(); else if (_cfgOn) ToggleConfigPanel(); }
                if (kb.f11Key.wasPressedThisFrame) Runner.Battle.DumpDiagnostics("F11");
                try
                {
                    if (_designKeyName != Plugin.DesignKey.Value) { _designKeyName = Plugin.DesignKey.Value; if (!Enum.TryParse(_designKeyName, true, out _designKey)) _designKey = Key.None; }
                    if (_designKey != Key.None && kb[_designKey].wasPressedThisFrame && !AnyFieldFocused()) { if (_cfgOn) ToggleConfigPanel(); HideLobby(); ToggleDesignPanel(); }
                }
                catch { }
                LobbyKeys(kb);
            }

            bool inBattle = Runner.Battle.InBattle;
            if (inBattle && !_wasInBattle) { _visible = false; HideLobby(); }   // get out of the way when a battle starts
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
            else if (_shellPanelOn || _designOn || _cfgOn || _lobbyOn || _sbOn || _mtOn || _ldOn) { HandleMouse(); PollFields(); }
            try { UpdateModelToolsPanel(); } catch (Exception e) { MPLog.Warn("modelling panel: " + e.Message); }
            try { UpdateLoaderPanel(); } catch (Exception e) { MPLog.Debug("loader panel: " + e.Message); }
            try { UpdateScoreboard(); } catch (Exception e) { MPLog.Debug("scoreboard: " + e.Message); }
            try { UpdateSight(); } catch (Exception e) { MPLog.Debug("sight: " + e.Message); }
            try { UpdateRepairRing(); } catch (Exception e) { MPLog.Debug("repair ring: " + e.Message); }
            try { UpdateSandboxPanel(); } catch (Exception e) { MPLog.Warn("sandbox panel: " + e.Message); }
            if (_lobbyOn) { if (inBattle) { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; } try { UpdateLobbyPanel(); } catch (Exception e) { MPLog.Warn("lobby panel: " + e.Message); } }
            try { UpdateConfigPanel(); } catch (Exception e) { MPLog.Warn("settings panel: " + e.Message); }
            if (_designOn) { if (inBattle) { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; } UpdateDesignPanel(); }
            try
            {
                var kbh = Keyboard.current;
                if (kbh != null && !AnyFieldFocused() && Enum.TryParse<Key>(Plugin.ShellHudKey.Value, true, out var hk) && hk != Key.None && kbh[hk].wasPressedThisFrame)
                    Plugin.ShellHudShown.Value = !Plugin.ShellHudShown.Value;
            }
            catch { }
            if (Plugin.ShellsOn) try { Shells.ShellSelect.Update(AnyFieldFocused()); } catch (Exception e) { MPLog.Debug("shell select: " + e.Message); }
            if (Time.realtimeSinceStartup >= _nextText) { _nextText = Time.realtimeSinceStartup + 0.25f; RefreshText(); }
            try { FreeCam.Update(AnyFieldFocused(), _visible || _shellPanelOn || _designOn || _cfgOn || _lobbyOn || _sbOn || _mtOn); } catch (Exception e) { MPLog.Debug("free cam: " + e.Message); }
            if (_fc.activeSelf != FreeCam.Active) _fc.SetActive(FreeCam.Active);
            if (FreeCam.Active) _fcText.text = FreeCam.Help;
            RefreshFeed();
            RefreshDeath();
            try { RefreshCornerFeed(); } catch (Exception e) { MPLog.Debug("kill feed: " + e.Message); }
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

                // corner kill feed (position / size from [KillFeed])
                _kf = new GameObject("CornerKillFeed"); _kf.transform.SetParent(_root.transform, false);
                _kf.AddComponent<RectTransform>().sizeDelta = new Vector2(760, 330);
                _kfText = _kf.AddComponent<TextMeshProUGUI>();
                StyleText(_kfText, 20, TextAlignmentOptions.TopLeft);
                _kf.SetActive(false);

                // free cam help line (bottom centre)
                _fc = new GameObject("FreeCamHelp"); _fc.transform.SetParent(_root.transform, false);
                var fcrt = _fc.AddComponent<RectTransform>();
                fcrt.anchorMin = fcrt.anchorMax = fcrt.pivot = new Vector2(0.5f, 0f);
                fcrt.anchoredPosition = new Vector2(0, 14); fcrt.sizeDelta = new Vector2(1500, 40);
                _fcText = _fc.AddComponent<TextMeshProUGUI>();
                StyleText(_fcText, 19, TextAlignmentOptions.Bottom);
                _fc.SetActive(false);
                _penCam = new PenCamView(_root.transform, _font);
                if (Plugin.ShellsOn) BuildShellPanel();
                BuildDesignPanel();
                try { BuildConfigPanel(); } catch (Exception e) { MPLog.Warn("settings panel: " + e); }
                try { BuildLobbyPanel(); } catch (Exception e) { MPLog.Warn("lobby panel: " + e); }
                if (Plugin.SandboxOn) try { BuildSandboxPanel(); } catch (Exception e) { MPLog.Warn("sandbox panel: " + e); }
                try { BuildScoreboard(); } catch (Exception e) { MPLog.Warn("scoreboard: " + e); _score = null; }
                try { BuildSight(); } catch (Exception e) { MPLog.Warn("sight overlay: " + e); _sight = null; }
                try { BuildRepairRing(); LayoutRing(); } catch (Exception e) { MPLog.Warn("repair ring: " + e); _ring = null; }
                if (Plugin.ModelToolsOn) try { BuildModelToolsPanel(); } catch (Exception e) { MPLog.Warn("modelling panel: " + e); }
                try { BuildLoaderPanel(); } catch (Exception e) { MPLog.Warn("loader panel: " + e); }

                // main panel (top left): title bar, then sections - you, host, join / this game, Steam, tools, status
                _panel = Panel(_root.transform, "Panel", new Vector2(18, -40), new Vector2(560, PanelH), PanelCol);
                var mt = _panel.transform; const float MW = 528f;
                Panel(mt, "Bar", Vector2.zero, new Vector2(560, 46), new Color(0.12f, 0.15f, 0.2f, 1f)).GetComponent<Image>().raycastTarget = false;
                Label(mt, new Vector2(16, -8), new Vector2(300, 32), "<b>SPROCKET LAN</b>  <size=15><color=#9aa>v" + Plugin.VERSION + "</color></size>", 24);
                _connChip = Small(mt, new Vector2(250, -12), new Vector2(200, 24), "", 15, TextAlignmentOptions.Right);
                MakeButton(mt, new Vector2(460, -9), new Vector2(86, 28), "HIDE [F10]", OffCol, () => { _visible = false; Commit(); }).Label.fontSize = 13;
                float y = -58;

                Section(mt, 16, y, MW, "YOU"); y -= 32;
                Small(mt, new Vector2(16, y - 3), new Vector2(110, 26), "Player name", 16);
                MakeField(mt, new Vector2(130, y), new Vector2(MW - 114, 30), Plugin.EffectiveName, 24, false, v => { if (!string.IsNullOrWhiteSpace(v)) { Plugin.PlayerName.Value = v.Trim(); Plugin.CmdName = null; } });
                y -= 42;

                Section(mt, 16, y, MW, "HOST A GAME", out _hostHdr); y -= 32;
                Small(mt, new Vector2(16, y - 3), new Vector2(44, 26), "Port", 16);
                var portField = MakeField(mt, new Vector2(62, y), new Vector2(80, 30), Plugin.Port.Value.ToString(), 5, true, v => { if (int.TryParse(v, out var p) && p > 0 && p < 65536) Plugin.Port.Value = p; });
                _visBtn = MakeButton(mt, new Vector2(150, y), new Vector2(120, 30), "", BtnCol, () =>
                {
                    Plugin.HostVisibility.Value = Net.Session.IsPrivate ? "Public" : "Private";
                    _notice = Net.Session.IsPrivate ? "Private: your game won't show in the browser or LAN searches (invite / IP only)" : "Public: your game shows in the browser";
                    _noticeUntil = Time.realtimeSinceStartup + 6f;
                });
                _visBtn.Label.fontSize = 15;
                _hostBtn = MakeButton(mt, new Vector2(278, y), new Vector2(MW - 262, 30), "HOST GAME", BtnCol, () => { Commit(); Runner.Session.StartHost(Plugin.Port.Value); });
                _stopBtn = MakeButton(mt, new Vector2(278, y), new Vector2(MW - 262, 30), "DISCONNECT", BtnRed, () => Runner.Session.Stop());
                y -= 36;
                Small(mt, new Vector2(16, y - 3), new Vector2(90, 26), "Password", 16);
                var pwField = MakeField(mt, new Vector2(108, y), new Vector2(MW - 92, 30), Plugin.HostPassword.Value, 32, false, v => Plugin.HostPassword.Value = v);
                try { pwField.Input.contentType = TMP_InputField.ContentType.Password; pwField.Input.ForceLabelUpdate(); } catch { }
                y -= 32;
                Small(mt, new Vector2(16, y), new Vector2(MW, 18), "<color=#9aa>The password is for hosting, and for joining a game that has one. Empty = no password.</color>", 12);
                y -= 26;

                // ---- join a game (offline) / this game (connected): the same space
                Section(mt, 16, y, MW, "JOIN A GAME", out _midHdr); y -= 32;
                float midTop = y;
                _joinGroup.Add(Small(mt, new Vector2(16, y - 3), new Vector2(70, 26), "IP:port", 16).gameObject);
                var addrField = MakeField(mt, new Vector2(88, y), new Vector2(MW - 210, 30), Plugin.JoinAddress.Value, 64, false, v => Plugin.JoinAddress.Value = v.Trim());
                _joinGroup.Add(addrField.Rect.gameObject);
                _joinBtn = MakeButton(mt, new Vector2(MW - 106, y), new Vector2(122, 30), "JOIN", BtnCol, () => { Commit(); Runner.Session.StartClient(Plugin.JoinAddress.Value); });
                y -= 38;
                // LAN + Steam browser: FIND GAMES, a search box and pages of results (click one to join)
                _findBtn = MakeButton(mt, new Vector2(16, y), new Vector2(MW, 30), "FIND GAMES", new Color(0.2f, 0.35f, 0.45f, 1f), () => { Commit(); Net.Discovery.Search(); Net.SteamNet.SearchLobbies(); _searchedAny = true; _nextFindUi = 0; });
                y -= 34;
                _joinGroup.Add(Small(mt, new Vector2(16, y - 2), new Vector2(66, 24), "<color=#9aa>Search</color>", 15).gameObject);
                _browseSearch = MakeField(mt, new Vector2(80, y), new Vector2(MW - 206, 28), "", 32, false, null);
                _joinGroup.Add(_browseSearch.Rect.gameObject);
                _joinGroup.Add(MakeButton(mt, new Vector2(MW - 120, y), new Vector2(34, 28), "<", BtnCol, () => { if (_browsePage > 0) _browsePage--; _nextFindUi = 0; }).Rect.gameObject);
                _browsePageLbl = Small(mt, new Vector2(MW - 84, y - 2), new Vector2(64, 24), "", 13, TextAlignmentOptions.Center);
                _joinGroup.Add(_browsePageLbl.gameObject);
                _joinGroup.Add(MakeButton(mt, new Vector2(MW - 18, y), new Vector2(34, 28), ">", BtnCol, () => { _browsePage++; _nextFindUi = 0; }).Rect.gameObject);
                y -= 34;
                for (int i = 0; i < _foundBtns.Length; i++)
                {
                    int idx = i;
                    _foundBtns[i] = MakeButton(mt, new Vector2(16, y), new Vector2(MW, 40), "", FieldCol, () =>
                    {
                        if (idx >= _foundShown.Count) return;
                        var f = _foundShown[idx];
                        if (!f.Compatible) { MPLog.Warn($"{f.Name} runs SprocketLAN {f.Version} (protocol {f.Protocol}), you have {Plugin.VERSION} (protocol {Plugin.PROTOCOL})"); return; }
                        Commit();
                        if (f.Password && !Net.Session.HasPassword) { _notice = $"{f.Name}'s game has a password: type it in the Password field, then click the game again"; _noticeUntil = Time.realtimeSinceStartup + 8f; return; }
                        if (f.Steam) { Runner.Session.StartSteamClient(f.SteamHost); return; }
                        Plugin.JoinAddress.Value = f.Address;
                        try { addrField.Input.text = f.Address; } catch { }
                        Runner.Session.StartClient(f.Address);
                    });
                    _foundBtns[i].Label.fontSize = 14; _foundBtns[i].Label.alignment = TextAlignmentOptions.Left; _foundBtns[i].Label.margin = new Vector4(10, 0, 10, 0);
                    _foundBtns[i].Label.lineSpacing = -8f;
                    _foundBtns[i].Rect.gameObject.SetActive(false);
                    y -= 42;
                }
                // connected: the game's overview, in the same place
                _gameInfo = Label(mt, new Vector2(16, midTop), new Vector2(MW, midTop - y - 4), "", 15);
                _gameInfo.alignment = TextAlignmentOptions.TopLeft; _gameInfo.enableWordWrapping = true; _gameInfo.overflowMode = TextOverflowModes.Ellipsis;
                y -= 6;

                Section(mt, 16, y, MW, "STEAM", out _steamHdr); y -= 32;
                _steamInvite = MakeButton(mt, new Vector2(16, y), new Vector2(MW / 2 - 4, 30), "INVITE STEAM FRIENDS", new Color(0.16f, 0.3f, 0.48f, 1f), () => { Commit(); SteamInvite(false); });
                _steamInviteAll = MakeButton(mt, new Vector2(16 + MW / 2 + 4, y), new Vector2(MW / 2 - 4, 30), "INVITE FRIENDS IN SPROCKET", new Color(0.16f, 0.3f, 0.48f, 1f), () => { Commit(); SteamInvite(true); });
                _steamInvite.Label.fontSize = 14; _steamInviteAll.Label.fontSize = 14;
                _steamLine = Small(mt, new Vector2(16, y - 3), new Vector2(MW - 100, 24), "", 14);
                // steam_api64.dll missing / unusable: FIX IT looks for one in the player's other Steam games (after asking)
                _steamFix = MakeButton(mt, new Vector2(16 + MW - 92, y), new Vector2(92, 30), "FIX IT", new Color(0.55f, 0.4f, 0.12f, 1f), () => { _steamAsk.SetActive(true); });
                _steamFix.Label.fontSize = 15; _steamFix.Rect.gameObject.SetActive(false);
                y -= 40;

                Section(mt, 16, y, MW, "TOOLS"); y -= 32;
                MakeButton(mt, new Vector2(16, y), new Vector2(MW, 36), "MULTIPLAYER LOBBY  [" + Plugin.LobbyKey.Value + "]", new Color(0.2f, 0.4f, 0.5f, 1f), ToggleLobby).Label.fontSize = 18;
                y -= 42;
                MakeButton(mt, new Vector2(16, y), new Vector2(MW / 2 - 4, 30), "SEND DESIGNS  [" + Plugin.DesignKey.Value + "]", new Color(0.25f, 0.3f, 0.42f, 1f), () => { if (_cfgOn) ToggleConfigPanel(); HideLobby(); ToggleDesignPanel(); }).Label.fontSize = 15;
                MakeButton(mt, new Vector2(16 + MW / 2 + 4, y), new Vector2(MW / 2 - 4, 30), "SETTINGS  [" + Plugin.ConfigKey.Value + "]", new Color(0.25f, 0.25f, 0.4f, 1f), ToggleConfigPanel).Label.fontSize = 15;
                y -= 36;
                _stabBtn = MakeButton(mt, new Vector2(16, y), new Vector2(MW / 2 - 4, 30), "", new Color(0.25f, 0.3f, 0.25f, 1f), () => Stabilizer.Toggle());
                _stabBtn.Label.fontSize = 15;
                MakeButton(mt, new Vector2(16 + MW / 2 + 4, y), new Vector2(MW / 2 - 4, 30), "DIAGNOSTICS  [F11]", OffCol, () => Runner.Battle.DumpDiagnostics("button")).Label.fontSize = 15;
                y -= 40;

                Section(mt, 16, y, MW, "STATUS"); y -= 30;
                var srt = new GameObject("Status"); srt.transform.SetParent(mt, false);
                var rt = srt.AddComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
                rt.anchoredPosition = new Vector2(16, y); rt.sizeDelta = new Vector2(MW, PanelH + y - 10);
                _status = srt.AddComponent<TextMeshProUGUI>();
                StyleText(_status, 15, TextAlignmentOptions.TopLeft);
                _status.enableWordWrapping = true; _status.overflowMode = TextOverflowModes.Ellipsis;

                // FIX IT prompt (over the middle of the panel)
                _steamAsk = Panel(mt, "SteamAsk", new Vector2(10, -300), new Vector2(540, 250), new Color(0.1f, 0.12f, 0.16f, 0.98f));
                Panel(_steamAsk.transform, "Line", Vector2.zero, new Vector2(540, 3), new Color(0.85f, 0.6f, 0.2f, 1f)).GetComponent<Image>().raycastTarget = false;
                var ask = Label(_steamAsk.transform, new Vector2(18, -16), new Vector2(504, 170),
                    "<b>Find a steam_api64.dll for Steam multiplayer?</b>\n\n" +
                    "<size=15>SprocketLAN needs Valve's <b>steam_api64.dll</b> (Steamworks 1.58 or newer) next to SprocketLAN.dll for Steam invites, " +
                    "the lobby browser and playing through Steam. Many games ship one.\n\n" +
                    "The mod will look through the games in <b>your Steam libraries</b>, pick the newest one that has everything it needs, and copy it into " +
                    "<b>BepInEx/plugins/SprocketLAN</b>. Nothing else is changed. LAN play works without it.</size>", 18);
                ask.enableWordWrapping = true; ask.alignment = TextAlignmentOptions.TopLeft;
                MakeButton(_steamAsk.transform, new Vector2(18, -198), new Vector2(300, 36), "YES, LOOK FOR ONE", new Color(0.2f, 0.5f, 0.27f, 1f), () => { _steamAsk.SetActive(false); Net.SteamDllFinder.Start(); }).Label.fontSize = 16;
                MakeButton(_steamAsk.transform, new Vector2(330, -198), new Vector2(192, 36), "NO", OffCol, () => _steamAsk.SetActive(false)).Label.fontSize = 16;
                _steamAsk.SetActive(false);
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

        // ------------------------------------------------------------------------------------- design sending (F8)
        GameObject _designPanel; bool _designOn; string _designKeyName; Key _designKey = Key.None; float _nextDesignUi; string _notice; float _noticeUntil;
        List<DesignShare.Design> _designs = new List<DesignShare.Design>();
        int _designPage, _designSel = -1; const int DesignRows = 10;
        readonly List<Button> _designRows = new List<Button>(); readonly List<Button> _designTargets = new List<Button>();
        Field _designIp; TextMeshProUGUI _designStatus, _designPageLbl; Button _designSend;
        string _designTargetHint; Button _settingsApply, _settingsDiscard;

        void BuildDesignPanel()
        {
            const float W = 620f, IW = 588f;
            _designPanel = Panel(_root.transform, "Designs", new Vector2(600, -40), new Vector2(W, 1010), PanelCol);
            var t = _designPanel.transform; float y;
            Panel(t, "Bar", Vector2.zero, new Vector2(W, 46), new Color(0.12f, 0.15f, 0.2f, 1f)).GetComponent<Image>().raycastTarget = false;
            Label(t, new Vector2(16, -8), new Vector2(330, 32), "<b>SEND A DESIGN</b>", 24);
            MakeButton(t, new Vector2(W - 118, -9), new Vector2(104, 28), $"CLOSE [{Plugin.DesignKey.Value}]", OffCol, ToggleDesignPanel).Label.fontSize = 14;
            y = -58;

            Section(t, 16, y, IW, "YOUR DESIGNS", out _designHdr); y -= 32;
            _designSearch = MakeField(t, new Vector2(16, y), new Vector2(IW - 108, 28), "", 40, false, null);
            MakeButton(t, new Vector2(16 + IW - 102, y), new Vector2(102, 28), "REFRESH", OffCol, RefreshDesigns).Label.fontSize = 14;
            y -= 34;
            for (int i = 0; i < DesignRows; i++)
            {
                int row = i;
                var b = MakeButton(t, new Vector2(16, y), new Vector2(IW, 38), "", FieldCol, () => { int idx = _designPage * DesignRows + row; if (idx < _designs.Count) _designSel = idx; _nextDesignUi = 0; });
                b.Label.alignment = TextAlignmentOptions.Left; b.Label.margin = new Vector4(10, 0, 10, 0); b.Label.fontSize = 16; b.Label.lineSpacing = -10f;
                b.Label.enableWordWrapping = false; b.Label.overflowMode = TextOverflowModes.Ellipsis;
                _designRows.Add(b); y -= 40;
            }
            y -= 2;
            MakeButton(t, new Vector2(16, y), new Vector2(50, 28), "<", BtnCol, () => { if (_designPage > 0) _designPage--; _nextDesignUi = 0; });
            _designPageLbl = Small(t, new Vector2(70, y - 2), new Vector2(IW - 108, 24), "", 14, TextAlignmentOptions.Center);
            MakeButton(t, new Vector2(16 + IW - 50, y), new Vector2(50, 28), ">", BtnCol, () => { if ((_designPage + 1) * DesignRows < _designs.Count) _designPage++; _nextDesignUi = 0; });
            y -= 40;

            Section(t, 16, y, IW, "SEND TO"); y -= 32;
            _designTargetsLbl = Small(t, new Vector2(16, y - 2), new Vector2(84, 24), "<color=#9aa>Players</color>", 15);
            for (int i = 0; i < 4; i++)
            {
                int k = i;
                var b = MakeButton(t, new Vector2(100 + i * 124, y), new Vector2(118, 28), "", new Color(0.2f, 0.27f, 0.36f, 1f), () => PickTarget(k));
                b.Label.fontSize = 14; b.Label.enableWordWrapping = false; b.Label.overflowMode = TextOverflowModes.Ellipsis; _designTargets.Add(b);
            }
            _designNoPlayers = Small(t, new Vector2(100, y - 2), new Vector2(IW - 84, 24), "<color=#9aa>nobody connected: type an IP below</color>", 14);
            y -= 34;
            Small(t, new Vector2(16, y - 3), new Vector2(84, 26), "or IP", 16);
            _designIp = MakeField(t, new Vector2(100, y), new Vector2(IW - 250, 30), Plugin.DesignLastTarget.Value, 64, false, v => { Plugin.DesignLastTarget.Value = (v ?? "").Trim(); });
            _designSend = MakeButton(t, new Vector2(16 + IW - 142, y), new Vector2(142, 30), "SEND", BtnCol, () =>
            {
                if (_designSel < 0 || _designSel >= _designs.Count) { DesignShare.SendStatus = "<color=#f88>Pick a design first</color>"; return; }
                Plugin.DesignLastTarget.Value = _designIp.Value.Trim();
                if (ToPlayer(out var pid)) DesignShare.SendToPlayer(_designs[_designSel], pid);   // through the game connection (Steam, LAN...)
                else DesignShare.Send(_designs[_designSel], _designIp.Value);
            });
            y -= 36;
            MakeButton(t, new Vector2(16, y), new Vector2(IW, 28), "SEND MY ARMOUR / SHELL SETTINGS INSTEAD", new Color(0.25f, 0.25f, 0.42f, 1f), () =>
            {
                Plugin.DesignLastTarget.Value = _designIp.Value.Trim();
                if (ToPlayer(out var pid)) DesignShare.SendSettingsToPlayer(pid);
                else DesignShare.SendSettings(_designIp.Value);
            }).Label.fontSize = 14;
            y -= 40;

            Section(t, 16, y, IW, "RECEIVING"); y -= 32;
            Small(t, new Vector2(16, y - 3), new Vector2(150, 26), "Save without asking", 15);
            _autoAcceptBtn = MakeButton(t, new Vector2(166, y), new Vector2(170, 30), "", BtnCol, () =>
            {
                var m = Plugin.DesignAutoAccept.Value;
                Plugin.DesignAutoAccept.Value = m == "Friends" ? "Always" : m == "Always" ? "Never" : "Friends"; _nextDesignUi = 0;
            });
            _askBtn = MakeButton(t, new Vector2(344, y), new Vector2(IW - 328, 30), "", BtnCol, () => { Plugin.DesignAskOthers.Value = !Plugin.DesignAskOthers.Value; _nextDesignUi = 0; });
            _autoAcceptBtn.Label.fontSize = 14; _askBtn.Label.fontSize = 14;
            y -= 36;
            _pendingNone = Small(t, new Vector2(16, y - 2), new Vector2(IW, 24), "<color=#9aa>Nothing waiting for your OK.</color>", 14);
            // waiting for a decision
            for (int i = 0; i < 3; i++)
            {
                int k = i;
                var card = Panel(t, "Pending", new Vector2(16, y), new Vector2(IW, 30), CardCol); card.GetComponent<Image>().raycastTarget = false;
                var lbl = Small(t, new Vector2(26, y - 3), new Vector2(IW - 200, 24), "", 15);
                var ok = MakeButton(t, new Vector2(16 + IW - 184, y + -2), new Vector2(88, 26), "ACCEPT", GoodCol, () => { var p = DesignShare.Pending; if (k < p.Count) DesignShare.Accept(p[k]); _nextDesignUi = 0; });
                var no = MakeButton(t, new Vector2(16 + IW - 92, y + -2), new Vector2(88, 26), "DENY", BtnRed, () => { var p = DesignShare.Pending; if (k < p.Count) DesignShare.Deny(p[k]); _nextDesignUi = 0; });
                ok.Label.fontSize = 13; no.Label.fontSize = 13;
                _pendingRows.Add((lbl, ok, no)); _pendingCards.Add(card);
                y -= 33;
            }
            _settingsApply = MakeButton(t, new Vector2(16, y), new Vector2(IW - 148, 30), "", GoodCol, () => { DesignShare.SendStatus = DesignShare.ApplyPendingSettings(); _nextDesignUi = 0; });
            _settingsDiscard = MakeButton(t, new Vector2(16 + IW - 140, y), new Vector2(140, 30), "DISCARD", BtnRed, () => { DesignShare.PendingSettings = null; DesignShare.SendStatus = "Received settings discarded"; _nextDesignUi = 0; });
            _settingsApply.Label.fontSize = 14; _settingsDiscard.Label.fontSize = 14;
            y -= 36;
            Panel(t, "Line", new Vector2(16, y), new Vector2(IW, 1), LineCol).GetComponent<Image>().raycastTarget = false;
            y -= 6;
            _designStatus = Label(t, new Vector2(16, y), new Vector2(IW, 1010 + y - 8), "", 14);
            _designStatus.enableWordWrapping = true; _designStatus.alignment = TextAlignmentOptions.TopLeft; _designStatus.overflowMode = TextOverflowModes.Ellipsis;
            _designPanel.SetActive(false);
        }
        TextMeshProUGUI _designHdr, _designTargetsLbl, _designNoPlayers, _pendingNone;
        readonly List<GameObject> _pendingCards = new List<GameObject>();

        void ToggleDesignPanel()
        {
            _designOn = !_designOn;
            _designPanel.SetActive(_designOn);
            if (_designOn) { RefreshDesigns(); DesignShare.SendStatus = ""; _nextDesignUi = 0; }
            else Commit();
        }

        Field _designSearch; string _designFilter = ""; List<DesignShare.Design> _designsAll = new List<DesignShare.Design>();
        /// <summary>Designs whose name or faction contains every word typed (any case).</summary>
        static List<DesignShare.Design> FilterDesigns(List<DesignShare.Design> all, string filter)
        {
            var words = (filter ?? "").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return new List<DesignShare.Design>(all);
            return all.Where(d => { var s = (d.Name + " " + d.Faction).ToLowerInvariant(); return words.All(w => s.Contains(w)); }).ToList();
        }

        void RefreshDesigns()
        {
            string selPath = _designSel >= 0 && _designSel < _designs.Count ? _designs[_designSel].Path : null;
            _designsAll = DesignShare.ListDesigns();
            _designs = FilterDesigns(_designsAll, _designFilter);
            _designSel = selPath != null ? _designs.FindIndex(d => d.Path == selPath) : -1;
            if (_designPage * DesignRows >= _designs.Count) _designPage = 0;
        }

        /// <summary>Connected players' addresses (host: each client; client: the host).</summary>
        /// <summary>Everyone else in the session (the host included for a client).</summary>
        List<(string name, byte id)> TargetPlayers()
        {
            var l = new List<(string, byte)>(); var s = Runner.Session;
            try
            {
                if (!s.Active) return l;
                if (!s.IsHost && !s.Players.ContainsKey(0)) l.Add((Runner.Battle.NameOf(0) ?? "host", (byte)0));
                foreach (var p in s.Players.Values.OrderBy(p => p.Id)) if (p.Id != s.LocalId) l.Add((p.Name, p.Id));
            }
            catch { }
            return l;
        }
        static string StripPort(string ep) { int c = ep.LastIndexOf(':'); return c > 0 && ep.IndexOf(':') == c ? ep.Substring(0, c) : ep.Trim('[', ']'); }

        Button _autoAcceptBtn, _askBtn;
        readonly List<(TextMeshProUGUI lbl, Button ok, Button no)> _pendingRows = new List<(TextMeshProUGUI, Button, Button)>();
        byte _xferTarget = 255; string _xferTargetName;

        void PickTarget(int k)
        {
            var l = TargetPlayers(); if (k >= l.Count) return;
            _xferTarget = l[k].id; _xferTargetName = l[k].name;
            _designIp.Input.text = l[k].name;   // sent through the game connection; type an IP instead to send directly
        }
        /// <summary>The target is a player picked from the list (and still in the session): send through the session.</summary>
        bool ToPlayer(out byte id)
        {
            id = _xferTarget;
            if (_xferTarget == 255 || !Runner.Session.Active) return false;
            if ((_designIp.Value ?? "").Trim() != _xferTargetName) { _xferTarget = 255; return false; }   // edited: an IP now
            return Runner.Session.Players.ContainsKey(_xferTarget) || (_xferTarget == 0 && !Runner.Session.IsHost);
        }

        void UpdateDesignPanel()
        {
            string flt = _designSearch?.Value ?? "";
            if (flt != _designFilter)
            {
                string selPath = _designSel >= 0 && _designSel < _designs.Count ? _designs[_designSel].Path : null;
                _designFilter = flt; _designs = FilterDesigns(_designsAll, flt); _designPage = 0; _nextDesignUi = 0;
                _designSel = selPath != null ? _designs.FindIndex(d => d.Path == selPath) : -1;
            }
            if (Time.realtimeSinceStartup < _nextDesignUi) return;   // text/list refresh 10x per second is plenty
            _nextDesignUi = Time.realtimeSinceStartup + 0.1f;
            for (int i = 0; i < DesignRows; i++)
            {
                int idx = _designPage * DesignRows + i; var b = _designRows[i];
                if (idx >= _designs.Count) { b.Rect.gameObject.SetActive(false); continue; }
                b.Rect.gameObject.SetActive(true);
                var d = _designs[idx]; bool sel = idx == _designSel;
                b.Normal = sel ? new Color(0.22f, 0.4f, 0.26f, 1f) : FieldCol;
                var di = DesignInfo.Of(d.Path);
                b.Label.text = $"{(sel ? "<b>> " : "")}{NoTags(d.Name)}{(sel ? "</b>" : "")}\n<size=12><color=#9aa>{di.Short}  ·  {NoTags(d.Faction)}  ·  saved {d.Modified:dd/MM HH:mm}  ·  {d.Size / 1024} KB</color></size>";
            }
            int pages = Math.Max(1, (_designs.Count + DesignRows - 1) / DesignRows);
            _designPageLbl.text = $"<color=#9aa>page {_designPage + 1} / {pages}  <size=12>({_designs.Count} design{(_designs.Count == 1 ? "" : "s")})</size></color>";
            var seld = _designSel >= 0 && _designSel < _designs.Count ? _designs[_designSel] : null;
            _designHdr.text = $"<b><color=#8fb8e8>YOUR DESIGNS</color></b>  {(seld != null ? $"<color=#fd8>selected: {NoTags(seld.Name)}</color>" : "<color=#9aa>click one to select it</color>")}";
            var tp = TargetPlayers();
            for (int i = 0; i < _designTargets.Count; i++)
            {
                var b = _designTargets[i];
                b.Rect.gameObject.SetActive(i < tp.Count);
                if (i < tp.Count)
                {
                    bool picked = _xferTarget == tp[i].id && (_designIp.Value ?? "").Trim() == _xferTargetName;
                    b.Label.text = (picked ? "<b>" : "") + NoTags(tp[i].name) + (picked ? "</b>" : "");
                    b.Normal = picked ? new Color(0.25f, 0.45f, 0.6f, 1f) : new Color(0.2f, 0.27f, 0.36f, 1f);
                }
            }
            _designNoPlayers.gameObject.SetActive(tp.Count == 0);
            _designSend.Label.text = DesignShare.Sending ? "<b>SENDING...</b>" : "<b>SEND</b>";
            var mode = Plugin.DesignAutoAccept.Value;
            _autoAcceptBtn.Label.text = mode == "Always" ? "<b>EVERYONE</b>" : mode == "Never" ? "<b>NOBODY</b>" : "<b>STEAM FRIENDS</b>";
            _autoAcceptBtn.Normal = mode == "Always" ? new Color(0.45f, 0.32f, 0.15f, 1f) : mode == "Never" ? new Color(0.42f, 0.2f, 0.2f, 1f) : new Color(0.2f, 0.4f, 0.26f, 1f);
            _askBtn.Label.text = Plugin.DesignAskOthers.Value ? "<b>OTHERS: ASK ME</b>" : "<b>OTHERS: REFUSE SILENTLY</b>";
            _askBtn.Normal = Plugin.DesignAskOthers.Value ? new Color(0.25f, 0.3f, 0.42f, 1f) : new Color(0.3f, 0.3f, 0.3f, 1f);
            var pend = DesignShare.Pending;
            _pendingNone.gameObject.SetActive(pend.Count == 0);
            for (int i = 0; i < _pendingRows.Count; i++)
            {
                var (lbl, ok, no) = _pendingRows[i]; bool on = i < pend.Count;
                lbl.gameObject.SetActive(on); ok.Rect.gameObject.SetActive(on); no.Rect.gameObject.SetActive(on); _pendingCards[i].SetActive(on);
                if (!on) continue;
                var x = pend[i];
                lbl.text = x.Settings ? $"<color=#fd8><noparse>{x.From}</noparse></color>: their settings ({x.Offer?.Changes.Count ?? 0} differences)"
                                      : $"<color=#fd8><noparse>{x.From}</noparse></color>: <b><noparse>{x.Name}</noparse></b> <color=#9aa>({x.Bp.Length / 1024} KB)</color>";
            }
            var offer = DesignShare.PendingSettings;
            _settingsApply.Rect.gameObject.SetActive(offer != null); _settingsDiscard.Rect.gameObject.SetActive(offer != null);
            if (offer != null) _settingsApply.Label.text = $"<b>APPLY SETTINGS FROM {offer.From} ({offer.Changes.Count})</b>";
            string st = DesignShare.SendStatus;
            if (offer != null) st = (string.IsNullOrEmpty(st) ? "" : st + "\n") + "<size=13><color=#fd8>" + string.Join("\n", offer.Changes.Take(3)) + (offer.Changes.Count > 3 ? $"\n... and {offer.Changes.Count - 3} more" : "") + "</color></size>";
            _designStatus.text = (string.IsNullOrEmpty(st) ? "" : st + "\n") + $"<color=#9aa><size=13>Received designs go to: {NoTags(DesignShare.ReceiveFolder())}  ({(Plugin.DesignAccept.Value ? (DesignShare.ListenPort > 0 ? "your port " + DesignShare.ListenPort : "not listening") : "receiving OFF")})</size></color>";
        }

        void BuildShellPanel()
        {
            _shellPanel = Panel(_root.transform, "ShellPanel", Vector2.zero, new Vector2(430, 486), PanelCol);
            var rt = _shellPanel.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1, 1);
            rt.anchoredPosition = new Vector2(-14, -250);
            float y = -10;
            _shellTitle = Label(_shellPanel.transform, new Vector2(14, y), new Vector2(405, 28), "", 19); y -= 36;
            _shellFields[0] = ShellRow(ref y, "APHE filler (grams)", (d, f) => d.ApheFiller = Mathf.Clamp(f, 0f, 2000f));
            _shellFields[1] = ShellRow(ref y, "HE filler (grams)", (d, f) => d.HeFiller = Mathf.Clamp(f, 0f, 20000f));
            _shellFields[2] = ShellRow(ref y, "APHE fuse sensitivity (mm)", (d, f) => d.FuseSens = Mathf.Clamp(f, 0f, 500f));
            _shellFields[3] = ShellRow(ref y, "APHE fuse distance (m)", (d, f) => d.FuseDist = Mathf.Clamp(f, 0.05f, 10f));
            _capAP = MakeButton(_shellPanel.transform, new Vector2(14, y), new Vector2(196, 28), "", FieldCol, () => ToggleCap(false));
            _capAPHE = MakeButton(_shellPanel.transform, new Vector2(220, y), new Vector2(196, 28), "", FieldCol, () => ToggleCap(true));
            _capAP.Label.fontSize = 15; _capAPHE.Label.fontSize = 15;
            y -= 36;
            _autoBtn = MakeButton(_shellPanel.transform, new Vector2(14, y), new Vector2(196, 28), "", FieldCol, ToggleAuto);
            _autoBtn.Label.fontSize = 15;
            Label(_shellPanel.transform, new Vector2(220, y), new Vector2(110, 28), "rounds/min", 15);
            _rpmField = MakeField(_shellPanel.transform, new Vector2(320, y), new Vector2(96, 28), "", 6, false, v =>
            {
                if (!TryF(v, out var x) || string.IsNullOrEmpty(_shellDesign)) return;
                var d = Shells.ShellStore.Get(_shellDesign).Copy(); d.FireRate = Mathf.Clamp(x, 1f, 3000f); Shells.ShellStore.Set(_shellDesign, d); _nextShellInfo = 0f;
            });
            y -= 36;
            _spawnShellBtn = MakeButton(_shellPanel.transform, new Vector2(14, y), new Vector2(402, 28), "", FieldCol, CycleSpawnShell);
            _spawnShellBtn.Label.fontSize = 15;
            y -= 36;
            var go = new GameObject("Info"); go.transform.SetParent(_shellPanel.transform, false);
            var irt = go.AddComponent<RectTransform>(); irt.anchorMin = irt.anchorMax = irt.pivot = new Vector2(0, 1);
            irt.anchoredPosition = new Vector2(14, y); irt.sizeDelta = new Vector2(405, 486 + y - 8);
            _shellInfo = go.AddComponent<TextMeshProUGUI>(); StyleText(_shellInfo, 15, TextAlignmentOptions.TopLeft);

            _shellPanel.SetActive(false);
        }

        Button _capAP, _capAPHE, _autoBtn, _spawnShellBtn; Field _rpmField;
        /// <summary>The shell this gun has loaded when the tank spawns (AP, APHE, HE, APDS, APFSDS, HEAT).</summary>
        void CycleSpawnShell()
        {
            if (string.IsNullOrEmpty(_shellDesign)) return;
            var d = Shells.ShellStore.Get(_shellDesign).Copy(); d.DefaultShell = (byte)((d.DefaultShell + 1) % 6);
            Shells.ShellStore.Set(_shellDesign, d); RefreshCapButtons(); _nextShellInfo = 0f;
        }
        void ToggleAuto()
        {
            if (string.IsNullOrEmpty(_shellDesign)) return;
            var d = Shells.ShellStore.Get(_shellDesign).Copy(); d.AutoCannon = !d.AutoCannon;
            Shells.ShellStore.Set(_shellDesign, d); RefreshCapButtons(); _nextShellInfo = 0f;
        }
        void ToggleCap(bool aphe)
        {
            if (string.IsNullOrEmpty(_shellDesign)) return;
            var d = Shells.ShellStore.Get(_shellDesign).Copy();
            if (aphe) d.CapAPHE = !d.CapAPHE; else d.CapAP = !d.CapAP;
            Shells.ShellStore.Set(_shellDesign, d);
            RefreshCapButtons(); _nextShellInfo = 0f;
        }
        void RefreshCapButtons()
        {
            if (_capAP == null) return;
            var d = Shells.ShellStore.Get(_shellDesign ?? "");
            _capAP.Label.text = $"<b>AP cap (APC): {(d.CapAP ? "<color=#8f8>ON</color>" : "<color=#aaa>OFF</color>")}</b>";
            _capAPHE.Label.text = $"<b>APHE cap (APHEC): {(d.CapAPHE ? "<color=#8f8>ON</color>" : "<color=#aaa>OFF</color>")}</b>";
            _capAP.Normal = d.CapAP ? new Color(0.22f, 0.36f, 0.22f, 1f) : FieldCol;
            _capAPHE.Normal = d.CapAPHE ? new Color(0.22f, 0.36f, 0.22f, 1f) : FieldCol;
            if (_spawnShellBtn != null)
            {
                var t = (Shells.ShellType)Mathf.Clamp(d.DefaultShell, 0, 5);
                _spawnShellBtn.Label.text = $"<b>Loaded at spawn: <color={(t == Shells.ShellType.AP ? "#ddd" : "#ffd060")}>{Shells.ShellConfig.Name(t)}</color></b>  <size=13><color=#9aa>(click to change)</color></size>";
            }
            if (_autoBtn != null)
            {
                _autoBtn.Label.text = $"<b>Autocannon: {(d.AutoCannon ? "<color=#8f8>ON</color>" : "<color=#aaa>OFF</color>")}</b>";
                _autoBtn.Normal = d.AutoCannon ? new Color(0.22f, 0.36f, 0.22f, 1f) : FieldCol;
                bool foc = false; try { foc = _rpmField.Input.isFocused; } catch { }
                if (!foc) _rpmField.Input.text = d.FireRate.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
                _rpmField.Rect.gameObject.SetActive(d.AutoCannon);
            }
        }
        TextMeshProUGUI _shellTitle; readonly Field[] _shellFields = new Field[4]; string _shellDesign = null;

        Field ShellRow(ref float y, string label, Action<Shells.ShellStore.Design, float> set)
        {
            Label(_shellPanel.transform, new Vector2(14, y), new Vector2(230, 28), label, 17);
            var f = MakeField(_shellPanel.transform, new Vector2(250, y), new Vector2(130, 28), "", 7, false, v =>
            {
                if (!TryF(v, out var x) || string.IsNullOrEmpty(_shellDesign)) return;
                var d = Shells.ShellStore.Get(_shellDesign);
                var copy = d.Copy();
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
            string spec = RackSpec();
            string key = Shells.ShellStore.Key(name, spec);
            if (key == _shellDesign) return;
            _shellDesign = key; Shells.ShellStore.ActiveName = name; Shells.ShellStore.ActiveSpec = spec;
            var d = Shells.ShellStore.Get(key);
            {
                // fillers above what this gun's shell can hold (e.g. the default 50 g on a 20 mm gun): brought down to its maximum
                float mass = RackGunMass(spec);
                if (mass > 0f)
                {
                    float ma = Shells.ShellConfig.MaxFiller(Shells.ShellType.APHE, mass), mh = Shells.ShellConfig.MaxFiller(Shells.ShellType.HE, mass);
                    if (d.ApheFiller > ma + 0.01f || d.HeFiller > mh + 0.01f)
                    {
                        var c = d.Copy(); c.ApheFiller = Mathf.Min(c.ApheFiller, Mathf.Floor(ma * 10f) / 10f); c.HeFiller = Mathf.Min(c.HeFiller, Mathf.Floor(mh * 10f) / 10f);
                        Shells.ShellStore.Set(key, c); d = c;
                        MPLog.Info($"Shell panel: fillers capped to what the {spec} gun's shell holds (APHE {c.ApheFiller:F1} g, HE {c.HeFiller:F1} g)");
                    }
                }
            }
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            float[] vals = { d.ApheFiller, d.HeFiller, d.FuseSens, d.FuseDist };
            for (int i = 0; i < 4; i++) if (_shellFields[i]?.Input != null) _shellFields[i].Input.text = vals[i].ToString("0.##", ci);
            RefreshCapButtons();
            _shellTitle.text = $"<b>Shells</b>  <color=#fd8>{(string.IsNullOrEmpty(name) ? "?" : name)}</color>{(spec != "" ? $"  <color=#8cf>{spec} mm gun</color>" : "")} <size=13><color=#9aa>(keys 1-6 in battle)</color></size>";
            MPLog.Info($"Shell panel: design '{name}', gun {(spec == "" ? "?" : spec)}");
        }

        /// <summary>Projectile mass (kg) of the designer gun with this ammo spec (or the biggest gun), -1 if none.</summary>
        static float RackGunMass(string spec)
        {
            float mass = -1f; int cal = 0;
            try
            {
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Sprocket.Vehicles.Cannons.Cannon>())
                {
                    var bp = c?.Blueprint; if (bp == null) continue;
                    if (!string.IsNullOrEmpty(spec)) { if (Shells.ShellStore.SpecOf(c) != spec) continue; }
                    else if (bp.Caliber <= cal) continue;
                    cal = bp.Caliber; mass = new Sprocket.Vehicles.ShellProfile(bp.PropellantLength, bp.Caliber).ProjectileMass;
                }
            }
            catch { }
            return mass;
        }

        /// <summary>"20x200mm" -> "20x200" (else null).</summary>
        static string ParseSpec(string t)
        {
            if (string.IsNullOrEmpty(t)) return null;
            t = t.Trim().ToLowerInvariant().Replace(" ", "");
            if (!t.EndsWith("mm")) return null;
            t = t.Substring(0, t.Length - 2);
            int x = t.IndexOf('x'); if (x <= 0 || x == t.Length - 1) return null;
            string a = t.Substring(0, x), b = t.Substring(x + 1);
            foreach (var ch in a + b) if (ch < '0' || ch > '9') return null;
            return a + "x" + b;
        }
        /// <summary>The ammo rack being edited: its "calibre x propellant" (e.g. "20x200"), read from the rack's own panel.</summary>
        string RackSpec()
        {
            try
            {
                if (_ammoTitle == null) return "";
                Transform t = _ammoTitle.transform;
                for (int up = 0; up < 6 && t != null; up++, t = t.parent)
                    foreach (var tx in t.GetComponentsInChildren<TMP_Text>(true))
                    {
                        if (tx == null || !tx.gameObject.activeInHierarchy) continue;
                        var sp = ParseSpec(tx.text);
                        if (sp != null) return sp;
                    }
            }
            catch { }
            return "";
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
        static bool AmmoTitleShown(TMP_Text t)
        {
            if (t == null || !t.gameObject.activeInHierarchy || !t.enabled || t.text != "Ammo Rack") return false;
            if (t.color.a < 0.05f) return false;
            // hidden by a fading parent group
            var tr = t.transform;
            while (tr != null)
            {
                var cg = tr.GetComponent<CanvasGroup>();
                if (cg != null && cg.alpha < 0.05f) return false;
                tr = tr.parent;
            }
            // moved off screen
            var corners = new Vector3[4]; t.rectTransform.GetWorldCorners(corners);
            var cam = t.canvas != null && t.canvas.renderMode != RenderMode.ScreenSpaceOverlay ? t.canvas.worldCamera : null;
            var p = RectTransformUtility.WorldToScreenPoint(cam, (corners[0] + corners[2]) * 0.5f);
            return p.x >= 0 && p.y >= 0 && p.x <= Screen.width && p.y <= Screen.height;
        }

        void UpdateShellPanel(bool inBattle)
        {
            if (_shellPanel == null) return;
            if (inBattle) { if (_shellPanelOn) { _shellPanel.SetActive(false); _shellPanelOn = false; } return; }
            // a structure being edited: the Ammo Rack panel can't be open, and scanning every text in the scene
            // gets expensive when the editor shows a big selection
            if (ModelTools.EditorOpen) { if (_shellPanelOn) { _shellPanelOn = false; _shellPanel.SetActive(false); } }
            else if (Time.realtimeSinceStartup >= _nextAmmoScan)
            {
                _nextAmmoScan = Time.realtimeSinceStartup + 0.5f;
                bool found = false;
                try
                {
                    // the designer reuses / hides its properties panel without always deactivating it:
                    // the title must still read "Ammo Rack" and actually be visible
                    if (_ammoTitle != null && AmmoTitleShown(_ammoTitle.GetComponent<TMP_Text>())) found = true;
                    else if (Time.realtimeSinceStartup >= _nextTitleScan)
                    {
                        _nextTitleScan = Time.realtimeSinceStartup + 1.5f;   // the full scan less often
                        _ammoTitle = null;
                        foreach (var t in UnityEngine.Object.FindObjectsOfType<TMP_Text>())
                            if (t != null && !t.transform.IsChildOf(_root.transform) && AmmoTitleShown(t)) { _ammoTitle = t.rectTransform; found = true; break; }
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
        float _nextShellInfo, _nextTitleScan; Button _stabBtn;
        static MenuUI _inst;
        public static bool AnyFieldFocusedStatic() { try { return _inst != null && _inst.AnyFieldFocused(); } catch { return false; } }
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
                string want = Shells.ShellStore.ActiveSpec;
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Sprocket.Vehicles.Cannons.Cannon>())
                {
                    var bp = c?.Blueprint; if (bp == null) continue;
                    if (!string.IsNullOrEmpty(want)) { if (Shells.ShellStore.SpecOf(c) != want) continue; }   // the gun this rack feeds
                    else if (bp.Caliber <= cal) continue;
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
            var dsg = Shells.ShellStore.Active; float acDiv = dsg.AutoCannon ? Mathf.Max(1f, Shells.ShellConfig.AutocannonPenDivisor.Value) : 1f;
            float AcK(Shells.ShellType t) => t == Shells.ShellType.HE ? 1f : 1f / acDiv;
            string Pen(Shells.ShellType t, float g) => ap > 0 ? $"{Shells.ShellConfig.Penetration(t, ap, g) * AcK(t):F0} mm" : $"{Shells.ShellConfig.PenFactor(t, 100f, g) * AcK(t) * 100f:F0}% of AP";
            string head = cal > 0 ? $"<color=#9aa>{cal} mm gun, {mass:F1} kg shell, max filler APHE {Shells.ShellConfig.MaxFiller(A, mass):F0} g / HE {Shells.ShellConfig.MaxFiller(H, mass):F0} g</color>\n" : "<color=#9aa>no gun found</color>\n";
            return head +
                $"<b>AP</b>  {Pen(Shells.ShellType.AP, 0)}\n" +
                $"<b>APHE</b>  {Pen(A, fa)}, {fa:F0} g{capA}, fuse {Shells.ShellStore.Active.FuseSens:F0} mm / {Shells.ShellStore.Active.FuseDist:F2} m\n" +
                $"<b>HE</b>  frags {Pen(H, fh)}, {fh:F0} g{capH}, explodes on impact\n" +
                $"<b>APDS</b>  {Pen(Shells.ShellType.APDS, 0)}, narrower spall cone, x{Shells.ShellConfig.VelocityMult(Shells.ShellType.APDS):F2} velocity\n" +
                $"<b>APFSDS</b>  {Pen(Shells.ShellType.APFSDS, 0)}, narrowest cone, x{Shells.ShellConfig.VelocityMult(Shells.ShellType.APFSDS):F2} velocity\n" +
                $"<b>HEAT</b>  {Pen(Shells.ShellType.HEAT, 0)}, sloped armour up to {Shells.ShellConfig.HEATRed70.Value * 100f:F0}% less effective\n" +
                $"<color=#9aa>AP cap: -{Shells.ShellConfig.APCapPenPenalty.Value * 100f:F0}% pen, -{Shells.ShellConfig.APCapNormalizationReduction.Value * 100f:F0}% normalisation</color>" +
                (dsg.AutoCannon ? $"\n<color=#fd8>Autocannon: {dsg.FireRate:F0} rounds/min ({60f / Mathf.Max(1f, dsg.FireRate):F2} s), penetration / {acDiv:F2}</color>" : "");
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
            Transform modal = _steamAsk != null && _steamAsk.activeInHierarchy ? _steamAsk.transform : null;   // a prompt is open: only its buttons
            foreach (var b in _buttons)
            {
                if (b.Rect == null || !b.Rect.gameObject.activeInHierarchy) continue;
                if (modal != null && !b.Rect.IsChildOf(modal)) continue;
                bool over = RectTransformUtility.RectangleContainsScreenPoint(b.Rect, mp, null);
                b.Img.color = over ? Color.Lerp(b.Normal, Color.white, 0.25f) : b.Normal;
                if (over && click) { try { b.OnClick?.Invoke(); } catch (Exception e) { MPLog.Error("Button: " + e); } return; }
            }
            if (!click || modal != null) return;
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

        const float PanelH = 1014f;
        TextMeshProUGUI _connChip, _hostHdr, _midHdr, _steamHdr, _gameInfo, _steamLine;
        Button _steamFix; GameObject _steamAsk;
        readonly List<GameObject> _joinGroup = new List<GameObject>();
        Field _browseSearch; TextMeshProUGUI _browsePageLbl; int _browsePage; string _browseFilter = "";
        Button _steamInvite, _steamInviteAll;

        void SteamInvite(bool friendsInGame)
        {
            var s = Runner.Session;
            if (!Net.SteamNet.Available) { _notice = "Steam isn't available: " + Net.SteamNet.Problem; _noticeUntil = Time.realtimeSinceStartup + 8f; return; }
            if (!s.Active) s.StartHost(Plugin.Port.Value);   // inviting means hosting
            if (!s.IsHost) return;
            if (friendsInGame)
            {
                int n = Net.SteamNet.InviteFriendsInGame();
                _notice = n == 0 ? "No Steam friend is in Sprocket right now" : $"Invited {n} friend{(n == 1 ? "" : "s")} through Steam";
            }
            else { Net.SteamNet.OpenInviteDialog(); _notice = "Steam invite window opened (Shift+Tab if it doesn't show; or use INVITE FRIENDS IN SPROCKET)"; }
            _noticeUntil = Time.realtimeSinceStartup + 8f;
        }
        Button _visBtn; bool _searchedAny;
        Button _findBtn; readonly Button[] _foundBtns = new Button[5]; List<Net.Discovery.Found> _foundShown = new List<Net.Discovery.Found>(); float _nextFindUi;

        void RefreshFound(bool active)
        {
            if (_findBtn == null) return;
            if (Time.unscaledTime < _nextFindUi) return; _nextFindUi = Time.unscaledTime + 0.2f;
            _findBtn.Rect.gameObject.SetActive(!active);
            if (_visBtn != null)
            {
                bool priv = Net.Session.IsPrivate;
                _visBtn.Label.text = priv ? "<b>PRIVATE</b>" : "<b>PUBLIC</b>";
                _visBtn.Normal = priv ? new Color(0.42f, 0.24f, 0.2f, 1f) : new Color(0.2f, 0.4f, 0.26f, 1f);
            }
            bool steam = Net.SteamNet.Available;
            bool searching = Net.Discovery.Searching || Net.SteamNet.SearchingLobbies;
            var all = active ? new List<Net.Discovery.Found>() : Net.Discovery.Results.Concat(Net.SteamNet.Lobbies).ToList();
            int total = all.Count;
            // search by host name (every word typed), then your version first, most players first
            var flt = (_browseSearch?.Value ?? "").Trim();
            if (flt != _browseFilter) { _browseFilter = flt; _browsePage = 0; }
            if (flt != "")
            {
                var words = flt.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                all = all.Where(f => words.All(w => (f.Name ?? "").ToLowerInvariant().Contains(w))).ToList();
            }
            all = all.OrderByDescending(f => f.Compatible).ThenByDescending(f => f.Players).ThenBy(f => f.Name).ToList();
            int pages = Math.Max(1, (all.Count + _foundBtns.Length - 1) / _foundBtns.Length);
            _browsePage = Mathf.Clamp(_browsePage, 0, pages - 1);
            if (_browsePageLbl != null) _browsePageLbl.text = all.Count == 0 ? "" : $"<color=#9aa>page {_browsePage + 1}/{pages}</color>";
            _foundShown = all.Skip(_browsePage * _foundBtns.Length).Take(_foundBtns.Length).ToList();
            int n = all.Count;
            _findBtn.Label.text = searching ? "<b>SEARCHING...</b>"
                : !_searchedAny ? $"<b>FIND GAMES</b>  <size=13><color=#9aa>({(steam ? "Steam lobby browser + " : "")}your network)</color></size>"
                : n == 0 ? $"<b>NO GAMES FOUND - SEARCH AGAIN</b>  <size=13><color=#9aa>({(steam ? "no public Steam games, " : "")}nothing on your network)</color></size>"
                : n == 0 && total > 0 ? $"<b>NO GAME MATCHES \"{flt}\"</b>  <size=13><color=#9aa>({total} found)</color></size>"
                : $"<b>FOUND {n} GAME{(n > 1 ? "S" : "")} - SEARCH AGAIN</b>{(n != total ? $"  <size=13><color=#9aa>({total} in all)</color></size>" : "")}";
            for (int i = 0; i < _foundBtns.Length; i++)
            {
                var b = _foundBtns[i]; bool on = i < _foundShown.Count;
                b.Rect.gameObject.SetActive(on); if (!on) continue;
                var f = _foundShown[i];
                string state = f.InBattle ? "<color=#f96>in battle</color>" : "<color=#9e9>in menus</color>";
                string ver = f.Compatible ? $"<color=#9aa>v{f.Version}</color>" : $"<color=#f66>v{f.Version} - not your version</color>";
                string src = f.Steam ? "<color=#8cf>STEAM</color>" : "<color=#cde>LAN " + f.Address + "</color>";
                string pw = f.Password ? "  <color=#fd8>[PASSWORD]</color>" : "";
                string map = string.IsNullOrEmpty(f.Map) ? "" : $"<color=#cde>{f.Map.Replace("<", "(").Replace(">", ")")}</color>  ";
                string rules = f.Rules == null ? "<color=#888>tank rules: not shown by this version</color>"
                    : f.Rules.Any ? $"<color=#fd8>Tanks: {f.Rules.Summary()}</color>" : "<color=#9aa>any tank</color>";
                b.Label.text = $"<b><noparse>{(f.Name ?? "").Replace("</noparse>", "")}</noparse></b>  {src}  {f.Players} player{(f.Players == 1 ? "" : "s")}, {state}{pw}  {ver}\n<size=13>{map}{rules}</size>";
                b.Normal = f.Compatible ? new Color(0.22f, 0.3f, 0.26f, 1f) : new Color(0.3f, 0.22f, 0.22f, 1f);
            }
        }

        void RefreshButtons()
        {
            bool active = Runner.Session.Active;
            if (_steamInvite != null)
            {
                bool steamOn = Plugin.SteamEnabled.Value && Net.SteamNet.Available && (!active || Runner.Session.IsHost);
                _steamInvite.Rect.gameObject.SetActive(steamOn); _steamInviteAll.Rect.gameObject.SetActive(steamOn);
            }
            RefreshFound(active);
            _hostBtn.Rect.gameObject.SetActive(!active);
            _joinBtn.Rect.gameObject.SetActive(!active);
            _stopBtn.Label.text = Runner.Session.IsHost ? "<b>STOP HOSTING</b>" : "<b>DISCONNECT</b>";
            _stopBtn.Rect.gameObject.SetActive(active);
            // offline: join a game (address + browser); connected: this game's overview in the same place
            foreach (var g in _joinGroup) if (g.activeSelf == active) g.SetActive(!active);
            if (_gameInfo != null && _gameInfo.gameObject.activeSelf != active) _gameInfo.gameObject.SetActive(active);
            var s = Runner.Session;
            if (_midHdr != null) _midHdr.text = active ? "<b><color=#8fb8e8>THIS GAME</color></b>" : "<b><color=#8fb8e8>JOIN A GAME</color></b>  <color=#9aa>by address, or find one</color>";
            if (_hostHdr != null) _hostHdr.text = s.IsHost ? "<b><color=#8fb8e8>HOSTING</color></b>  <color=#9aa>password / visibility apply to new players</color>" : "<b><color=#8fb8e8>HOST A GAME</color></b>";
            if (_connChip != null)
                _connChip.text = !active ? "<color=#9aa>offline</color>"
                    : s.IsHost ? $"<color=#8f8><b>HOSTING</b></color>  <color=#9aa>{s.Players.Count + 1} players</color>"
                    : s.LocalId == 255 ? "<color=#fd8><b>CONNECTING...</b></color>"
                    : $"<color=#8f8><b>CONNECTED</b></color>  {(s.LocalPingMs >= 0 ? PingColor(s.LocalPingMs) + s.LocalPingMs + " ms</color>" : "")}";
            try { Net.SteamDllFinder.Tick(); } catch (Exception e) { MPLog.Warn("steam dll: " + e.Message); }
            if (_steamLine != null)
            {
                bool shown = _steamInvite != null && _steamInvite.Rect.gameObject.activeSelf;
                if (_steamLine.gameObject.activeSelf == shown) _steamLine.gameObject.SetActive(!shown);
                string fix = Net.SteamDllFinder.Status;
                if (!shown) _steamLine.text = !Plugin.SteamEnabled.Value ? "<color=#9aa>Steam is turned off in the settings</color>"
                    : fix != "" && (Net.SteamDllFinder.Busy || !Net.SteamNet.Available || fix.Contains("Restart")) ? fix
                    : !Net.SteamNet.Available ? $"<color=#f88>Steam isn't available: {Net.SteamNet.Problem}</color>"
                    : "<color=#9aa>Only the host can invite. Ask the host, or accept an invite in Steam.</color>";
                bool canFix = !shown && Net.SteamDllFinder.CanFix && !(_steamAsk?.activeSelf ?? false);
                if (_steamFix != null && _steamFix.Rect.gameObject.activeSelf != canFix) _steamFix.Rect.gameObject.SetActive(canFix);
                if (_steamFix != null) _steamFix.Label.text = Net.SteamDllFinder.Status.Contains("none") || Net.SteamDllFinder.Status.Contains("No other") ? "<b>TRY AGAIN</b>" : "<b>FIX IT</b>";
            }
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
            if (bm.InBattle && Plugin.ShellsOn && Plugin.ShellHudShown.Value) _hudText.text += "\n" + Shells.ShellSelect.HudText();
            while (DesignShare.Notices.TryDequeue(out var note)) { _notice = note; _noticeUntil = Time.realtimeSinceStartup + 8f; }
            if (_notice != null && Time.realtimeSinceStartup < _noticeUntil) _hudText.text += "\n<color=#ffd27f>" + _notice + "</color>";
            if (bm.Repairing) _hudText.text += $"\n<color=#9fd><b>REPAIRING {bm.RepairRemaining:F1}s</b> - hold position ([{Plugin.RepairKey.Value}] cancel)</color>";

            if (!_visible) return;
            if (_gameInfo != null && s.Active) _gameInfo.text = GameInfoText(s, bm);
            var sb = new StringBuilder();
            sb.AppendLine("<b>Status:</b> " + s.Status);
            if (Plugin.SteamEnabled.Value)
            {
                string rt = Net.SteamNet.RelayText(); bool ok = Net.SteamNet.Available && rt == "ready";
                sb.AppendLine(Net.SteamNet.Available ? $"<b>Steam:</b> {Net.SteamNet.MyName} - relays <color={(ok ? "#6f6" : "#f55")}>{rt}</color>" : $"<b>Steam:</b> <color=#f55>off ({Net.SteamNet.Problem})</color>");
            }
            if (Net.SteamNet.JoinState != "") sb.AppendLine($"<b>Steam link:</b> <color=#fd8>{Net.SteamNet.JoinState}</color>");
            if (!s.Active)
            {
                sb.AppendLine();
                sb.AppendLine("<color=#bbb><b>How to play</b>");
                sb.AppendLine("1. One player clicks HOST GAME. The others JOIN by address, or click the game in FIND GAMES.");
                sb.AppendLine($"2. Everyone opens the MULTIPLAYER LOBBY [{Plugin.LobbyKey.Value}], picks a tank and a team, and clicks READY. The host starts.");
                sb.AppendLine($"3. In battle: [F10] this menu, [{Plugin.RepairKey.Value}] field repair, [Tab] scoreboard.</color>");
            }
            if (!string.IsNullOrEmpty(bm.LastEvent)) sb.AppendLine("\n<color=#9aa>Last: " + bm.LastEvent + "</color>");
            _status.text = sb.ToString();
        }

        /// <summary>Connected: the game in the middle of the main panel (game, battle, players with ping and link state).</summary>
        string GameInfoText(Net.Session s, BattleManager bm)
        {
            var sb = new StringBuilder();
            if (s.IsHost) sb.AppendLine($"Your game is {(Net.Session.IsPrivate ? "<color=#fd8>private</color> (invite / IP only)" : "<color=#8f8>public</color> (in the browser)")}, {(Net.Session.HasPassword ? "with a password" : "no password")}. Port {Plugin.Port.Value}.");
            else sb.AppendLine($"Joined <b>{(s.HostAddress ?? "").Replace("<", "(")}</b>");
            var lr = Lobby.Cur;
            if (Lobby.Connected) sb.AppendLine($"Lobby: <b>{(string.IsNullOrEmpty(lr.MapTitle) ? "no map yet" : lr.MapTitle.Replace("<", "("))}</b>, {lr.Teams} teams, <color=#fd8>{lr.Rules.Summary()}</color>  <color=#9aa>[{Plugin.LobbyKey.Value}]</color>");
            sb.AppendLine(bm.InBattle ? $"You: in battle, driving <b>{(bm.LocalVehicleName ?? "").Replace("<", "(")}</b>{(bm.SpawnApplied ? " <color=#9aa>(spawn aligned)</color>" : "")}" : "You: in the menus");
            sb.AppendLine();
            sb.AppendLine($"<b>Players ({s.Players.Count + 1})</b>");
            sb.AppendLine($"  {Plugin.EffectiveName.Replace("<", "(")} <color=#8c8>(you)</color>{(s.IsHost ? " <color=#9aa>host</color>" : "")}  <color=#9aa>{(bm.InBattle ? "in battle" : "in menus")}</color>");
            foreach (var p in s.Players.Values.OrderBy(p => p.Id))
            {
                string where = p.InBattle ? $"in battle <color=#cde>{(p.Vehicle ?? "").Replace("<", "(")}</color>" : "in menus";
                string puppet = "";
                if (bm.InBattle && p.InBattle)
                {
                    if (bm.PuppetByPlayer.TryGetValue(p.Id, out var r)) puppet = Time.time - p.LastStateAt < 3 ? " <color=#8c8>linked</color>" : " <color=#cc8>linked, no data</color>";
                    else puppet = " <color=#e77>NO MATCHING AI TANK</color>";
                }
                string ping = p.PingMs >= 0 ? $"{PingColor(p.PingMs)}{p.PingMs} ms</color>  " : "";
                sb.AppendLine($"  {(p.Name ?? "").Replace("<", "(")}{(p.Id == 0 ? " <color=#9aa>host</color>" : "")}  {ping}<color=#9aa>{where}</color>{puppet}");
            }
            return sb.ToString();
        }

        static string PingColor(int ms) => ms < 60 ? "<color=#8c8>" : ms < 150 ? "<color=#dd8>" : "<color=#e77>";

        void RefreshDeath()
        {
            var bm = Runner.Battle;
            bool show = bm.LocalDestroyed && Plugin.DeathTextShown.Value && !FreeCam.Active;
            if (_death.activeSelf != show) _death.SetActive(show);
            if (!show) return;
            Place(_death, _deathText, Plugin.DeathTextX.Value, Plugin.DeathTextY.Value, Plugin.DeathTextSize.Value, true);
            string t = $"<color=#FF2A2A><b>{bm.DeathReason}</b></color>";
            if (!string.IsNullOrEmpty(bm.DeathKiller)) t += $"\n<size=50%><color=#FFB0A8>destroyed by {bm.DeathKiller}</color></size>";
            t += "\n<size=35%><color=#ddd>Esc > Reset to respawn</color></size>";
            _deathText.text = t;
        }

        void RefreshFeed()
        {
            var feed = Runner.Battle.KillFeed;
            bool on = !_visible && feed.Count > 0 && Plugin.KillTextShown.Value;
            if (_feed.activeSelf != on) _feed.SetActive(on);
            if (!on) return;
            Place(_feed, _feedText, Plugin.KillTextX.Value, Plugin.KillTextY.Value, Plugin.KillTextSize.Value, false);
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

        /// <summary>Puts a HUD text at (x %, y %) of the screen; near an edge it lines up on that edge.</summary>
        static void Place(GameObject go, TextMeshProUGUI t, float xPct, float yPct, float sizePct, bool middle)
        {
            var rt = go.GetComponent<RectTransform>();
            var a = new Vector2(Mathf.Clamp01(xPct / 100f), Mathf.Clamp01(yPct / 100f));
            if (rt.anchorMin != a || rt.anchoredPosition != Vector2.zero) { rt.anchorMin = rt.anchorMax = rt.pivot = a; rt.anchoredPosition = Vector2.zero; }
            float sc = Mathf.Clamp(sizePct, 10f, 400f) / 100f;
            if (Mathf.Abs(rt.localScale.x - sc) > 0.001f) rt.localScale = new Vector3(sc, sc, 1f);
            int h = a.x < 0.34f ? 0 : a.x > 0.66f ? 2 : 1;
            int v = middle ? 1 : a.y >= 0.5f ? 0 : 2;
            var al = v == 0 ? (h == 0 ? TextAlignmentOptions.TopLeft : h == 2 ? TextAlignmentOptions.TopRight : TextAlignmentOptions.Top)
                   : v == 1 ? (h == 0 ? TextAlignmentOptions.Left : h == 2 ? TextAlignmentOptions.Right : TextAlignmentOptions.Center)
                   : (h == 0 ? TextAlignmentOptions.BottomLeft : h == 2 ? TextAlignmentOptions.BottomRight : TextAlignmentOptions.Bottom);
            if (t.alignment != al) t.alignment = al;
        }

        string _kfLast = "";
        void RefreshCornerFeed()
        {
            var bm = Runner.Battle;
            var log = bm.KillLog;
            bool on = Plugin.KillFeedEnabled.Value && bm.InBattle && log.Count > 0 && !_visible;
            float now = Time.time, life = Mathf.Max(1f, Plugin.KillFeedSeconds.Value);
            if (on && now - log[log.Count - 1].Time > life) on = false;
            if (_kf.activeSelf != on) _kf.SetActive(on);
            if (!on) return;
            bool under = Plugin.KillFeedBelowPenCam.Value;
            if (under)
            {
                // right edge, just under the pen cam panel
                var rt = _kf.GetComponent<RectTransform>();
                var a = new Vector2(1f, 1f);
                if (rt.anchorMin != a) { rt.anchorMin = rt.anchorMax = rt.pivot = a; }
                var pos = new Vector2(-12f, -(PenCamView.BottomFromTop + 8f));
                if (rt.anchoredPosition != pos) rt.anchoredPosition = pos;
                float sc = Mathf.Clamp(Plugin.KillFeedSize.Value, 10f, 400f) / 100f;
                if (Mathf.Abs(rt.localScale.x - sc) > 0.001f) rt.localScale = new Vector3(sc, sc, 1f);
                if (_kfText.alignment != TextAlignmentOptions.TopRight) _kfText.alignment = TextAlignmentOptions.TopRight;
            }
            else Place(_kf, _kfText, Plugin.KillFeedX.Value, Plugin.KillFeedY.Value, Plugin.KillFeedSize.Value, false);
            var sb = new StringBuilder();
            int max = Mathf.Clamp(Plugin.KillFeedLines.Value, 1, 20), shown = 0;
            for (int i = log.Count - 1; i >= 0 && shown < max; i--)
            {
                var e = log[i];
                float age = now - e.Time;
                if (age > life || age < 0) break;
                int alpha = (int)(255 * Mathf.Clamp01((life - age) / 1.5f));
                var line = new StringBuilder($"<alpha=#{alpha:X2}><mark=#000000{Math.Min(alpha / 2, 110):X2}>");
                if (e.Killer != null)
                {
                    line.Append(e.MeKiller ? "<color=#FFD93D><b>" : "<color=#FFFFFF><b>").Append(Esc(e.Killer)).Append("</b></color>");
                    if (Plugin.KillFeedShowShell.Value && !string.IsNullOrEmpty(e.Shell)) line.Append($" <color=#9EC9FF>[{e.Shell}]</color>");
                    line.Append(" <color=#999>»</color> ");
                }
                line.Append(e.MeVictim ? "<color=#FFD93D><b>" : "<color=#FF6B5B><b>").Append(Esc(e.Victim)).Append("</b></color>");
                line.Append(e.Reason == 1 ? "  <color=#FFA040><size=85%>ammo rack</size></color>" : "  <color=#BBBBBB><size=85%>crew</size></color>");
                line.Append("</mark>");
                // newest at the top when the feed hangs from the top of the screen, at the bottom otherwise
                if (under || Plugin.KillFeedY.Value >= 50f) sb.AppendLine(line.ToString()); else sb.Insert(0, line.ToString() + "\n");
                shown++;
            }
            var txt = sb.ToString();
            if (txt != _kfLast) { _kfLast = txt; _kfText.text = txt; }
        }
        static string Esc(string s) => (s ?? "").Replace("<", "<\u200B");

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
