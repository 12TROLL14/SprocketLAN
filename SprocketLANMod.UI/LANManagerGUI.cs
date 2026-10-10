using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.Gameplay.UI;
using Sprocket.Vehicles;
using SprocketLANMod.Network;
using SprocketLANMod.Sync;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SprocketLANMod.UI;

public class LANManagerGUI
{
	private class CustomClickableButton
	{
		public RectTransform Rect;

		public Image Image;

		public Color NormalColor;

		public Color HoverColor;

		public Action OnClick;
	}

	public class DamageLogEntry
	{
		public string ComponentName;

		public bool IsDestroyed;

		public float Damage;

		public float Timestamp;
	}

	private bool _isVisible = true;

	private string _localIp = "Detecting...";

	private string _statusMessage = "Ready to host or join LAN match.";

	private GameObject _canvasObject;

	private Canvas _canvas;

	private GameObject _mainPanel;

	private GameObject _minimizedBar;

	private TextMeshProUGUI _headerBadgeText;

	private TextMeshProUGUI _quickGlanceText;

	private TextMeshProUGUI _networkInfoText;

	private TextMeshProUGUI _localVehicleText;

	private TextMeshProUGUI _remoteVehicleText;

	private TextMeshProUGUI _combatDiagText;

	private TextMeshProUGUI _statusLogText;

	private TextMeshProUGUI _minimizedBarText;

	private TMP_FontAsset _fontAsset;

	private bool _uiBuilt;

	private readonly List<CustomClickableButton> _clickableButtons = new List<CustomClickableButton>();

	private GameObject _destructionBannerObject;

	private CanvasGroup _destructionBannerCanvasGroup;

	private TextMeshProUGUI _destructionBannerText;

	private Image _destructionBannerAccentImage;

	private float _destructionBannerTimer;

	private GameObject _hitReportObject;

	private CanvasGroup _hitReportCanvasGroup;

	private TextMeshProUGUI _hitReportText;

	private float _hitReportTimer;

	private readonly List<DamageLogEntry> _recentDamageList = new List<DamageLogEntry>();

	private float _lastDamageTime;

	public bool IsVisible
	{
		get
		{
			return _isVisible;
		}
		set
		{
			_isVisible = value;
			UpdatePanelVisibility();
		}
	}

	public string TargetIp { get; set; } = "127.0.0.1";

	public int TargetPort { get; set; } = 7777;

	public static LANManagerGUI Instance { get; private set; }

	public void ToggleVisibility()
	{
		_isVisible = !_isVisible;
		UpdatePanelVisibility();
	}

	public void Initialize()
	{
		Instance = this;
		_localIp = GetLocalIPAddress();
	}

	public void Update(UdpNetworkTransport transport, VehicleSync sync)
	{
		try
		{
			if (Time.time < 0.5f)
			{
				return;
			}
			if (!_uiBuilt)
			{
				TryBuildUI(transport, sync);
			}
			if (!_uiBuilt)
			{
				return;
			}
			if ((Object)(object)_fontAsset == (Object)null)
			{
				_fontAsset = FindFont();
				if ((Object)(object)_fontAsset != (Object)null)
				{
					ApplyFontToAll();
				}
			}
			HandleButtonClicks();
			UpdateTelemetryDisplays(transport, sync);
			if (_destructionBannerTimer > 0f)
			{
				_destructionBannerTimer -= Time.deltaTime;
				if ((Object)(object)_destructionBannerObject != (Object)null)
				{
					if (!_destructionBannerObject.activeSelf)
					{
						_destructionBannerObject.SetActive(true);
					}
					if (_destructionBannerTimer < 1f && (Object)(object)_destructionBannerCanvasGroup != (Object)null)
					{
						_destructionBannerCanvasGroup.alpha = Mathf.Clamp01(_destructionBannerTimer);
					}
					else if ((Object)(object)_destructionBannerCanvasGroup != (Object)null)
					{
						_destructionBannerCanvasGroup.alpha = 1f;
					}
				}
			}
			else if ((Object)(object)_destructionBannerObject != (Object)null && _destructionBannerObject.activeSelf)
			{
				_destructionBannerObject.SetActive(false);
			}
			if (_hitReportTimer > 0f)
			{
				_hitReportTimer -= Time.deltaTime;
				if ((Object)(object)_hitReportObject != (Object)null)
				{
					if (!_hitReportObject.activeSelf)
					{
						_hitReportObject.SetActive(true);
					}
					if (_hitReportTimer < 1f && (Object)(object)_hitReportCanvasGroup != (Object)null)
					{
						_hitReportCanvasGroup.alpha = Mathf.Clamp01(_hitReportTimer);
					}
					else if ((Object)(object)_hitReportCanvasGroup != (Object)null)
					{
						_hitReportCanvasGroup.alpha = 1f;
					}
				}
			}
			else if ((Object)(object)_hitReportObject != (Object)null && _hitReportObject.activeSelf)
			{
				_hitReportObject.SetActive(false);
			}
		}
		catch
		{
		}
	}

	private void TryBuildUI(UdpNetworkTransport transport, VehicleSync sync)
	{
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Expected Obj, but got Unknown
		//IL_007e: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			_fontAsset = FindFont();
			_canvasObject = new GameObject("SprocketLANMod_Canvas");
			Object.DontDestroyOnLoad((Object)(object)_canvasObject);
			((Object)_canvasObject).hideFlags = (HideFlags)61;
			_canvas = _canvasObject.AddComponent<Canvas>();
			_canvas.renderMode = (RenderMode)0;
			_canvas.sortingOrder = 30000;
			CanvasScaler val = _canvasObject.AddComponent<CanvasScaler>();
			val.uiScaleMode = (ScaleMode)1;
			val.referenceResolution = new Vector2(1920f, 1080f);
			val.matchWidthOrHeight = 0.5f;
			_canvasObject.AddComponent<GraphicRaycaster>();
			BuildMainPanel(transport, sync);
			BuildMinimizedBar();
			BuildDestructionBanner();
			BuildHitReportCard();
			_uiBuilt = true;
			if ((Object)(object)_fontAsset != (Object)null)
			{
				ApplyFontToAll();
			}
			UpdatePanelVisibility();
			Plugin instance = Plugin.Instance;
			if (instance != null)
			{
				ManualLogSource log = ((BasePlugin)instance).Log;
				if (log != null)
				{
					log.LogInfo((object)"[LAN Mod] Tactical Canvas UI constructed successfully on Screen.");
				}
			}
		}
		catch (Exception ex)
		{
			Plugin instance2 = Plugin.Instance;
			if (instance2 != null)
			{
				ManualLogSource log2 = ((BasePlugin)instance2).Log;
				if (log2 != null)
				{
					log2.LogWarning((object)("BuildUI deferred: " + ex.Message));
				}
			}
		}
	}

	private void BuildMainPanel(UdpNetworkTransport transport, VehicleSync sync)
	{
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		//IL_005f: Unknown result type (might be due to invalid IL or missing references)
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0098: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ff: Unknown result type (might be due to invalid IL or missing references)
		//IL_0130: Unknown result type (might be due to invalid IL or missing references)
		//IL_013f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0173: Unknown result type (might be due to invalid IL or missing references)
		//IL_0182: Unknown result type (might be due to invalid IL or missing references)
		//IL_019b: Unknown result type (might be due to invalid IL or missing references)
		//IL_01cd: Unknown result type (might be due to invalid IL or missing references)
		//IL_01dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f5: Unknown result type (might be due to invalid IL or missing references)
		//IL_0204: Unknown result type (might be due to invalid IL or missing references)
		//IL_0226: Unknown result type (might be due to invalid IL or missing references)
		//IL_0235: Unknown result type (might be due to invalid IL or missing references)
		//IL_027e: Unknown result type (might be due to invalid IL or missing references)
		//IL_028d: Unknown result type (might be due to invalid IL or missing references)
		//IL_02c2: Unknown result type (might be due to invalid IL or missing references)
		//IL_02d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_02ea: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_031b: Unknown result type (might be due to invalid IL or missing references)
		//IL_032a: Unknown result type (might be due to invalid IL or missing references)
		//IL_03a8: Unknown result type (might be due to invalid IL or missing references)
		//IL_03b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_03d0: Unknown result type (might be due to invalid IL or missing references)
		//IL_03fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_040c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0425: Unknown result type (might be due to invalid IL or missing references)
		//IL_0452: Unknown result type (might be due to invalid IL or missing references)
		//IL_0461: Unknown result type (might be due to invalid IL or missing references)
		//IL_047a: Unknown result type (might be due to invalid IL or missing references)
		//IL_04ac: Unknown result type (might be due to invalid IL or missing references)
		//IL_04bb: Unknown result type (might be due to invalid IL or missing references)
		//IL_04f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_04ff: Unknown result type (might be due to invalid IL or missing references)
		//IL_0518: Unknown result type (might be due to invalid IL or missing references)
		//IL_0527: Unknown result type (might be due to invalid IL or missing references)
		//IL_054b: Unknown result type (might be due to invalid IL or missing references)
		//IL_055a: Unknown result type (might be due to invalid IL or missing references)
		//IL_058f: Unknown result type (might be due to invalid IL or missing references)
		//IL_059e: Unknown result type (might be due to invalid IL or missing references)
		//IL_05b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_05e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_05f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_0611: Unknown result type (might be due to invalid IL or missing references)
		//IL_0620: Unknown result type (might be due to invalid IL or missing references)
		//IL_0644: Unknown result type (might be due to invalid IL or missing references)
		//IL_0653: Unknown result type (might be due to invalid IL or missing references)
		//IL_0688: Unknown result type (might be due to invalid IL or missing references)
		//IL_0697: Unknown result type (might be due to invalid IL or missing references)
		//IL_06b0: Unknown result type (might be due to invalid IL or missing references)
		//IL_06e2: Unknown result type (might be due to invalid IL or missing references)
		//IL_06f1: Unknown result type (might be due to invalid IL or missing references)
		//IL_070a: Unknown result type (might be due to invalid IL or missing references)
		//IL_073c: Unknown result type (might be due to invalid IL or missing references)
		//IL_074b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0764: Unknown result type (might be due to invalid IL or missing references)
		//IL_0796: Unknown result type (might be due to invalid IL or missing references)
		//IL_07a5: Unknown result type (might be due to invalid IL or missing references)
		//IL_07be: Unknown result type (might be due to invalid IL or missing references)
		//IL_07cd: Unknown result type (might be due to invalid IL or missing references)
		//IL_07f1: Unknown result type (might be due to invalid IL or missing references)
		//IL_0800: Unknown result type (might be due to invalid IL or missing references)
		//IL_0839: Unknown result type (might be due to invalid IL or missing references)
		//IL_0848: Unknown result type (might be due to invalid IL or missing references)
		//IL_0861: Unknown result type (might be due to invalid IL or missing references)
		//IL_0870: Unknown result type (might be due to invalid IL or missing references)
		//IL_0894: Unknown result type (might be due to invalid IL or missing references)
		//IL_08a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_08e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_08f2: Unknown result type (might be due to invalid IL or missing references)
		_mainPanel = CreatePanel(_canvasObject.transform, "MainPanel", new Vector2(25f, -25f), new Vector2(480f, 680f), new Color(0.06f, 0.08f, 0.12f, 0.94f), new Vector2(0f, 1f));
		GameObject header = CreatePanel(_mainPanel.transform, "Header", new Vector2(0f, 0f), new Vector2(480f, 44f), new Color(0.1f, 0.14f, 0.2f, 1f), new Vector2(0.5f, 1f));
		CreateText(header.transform, "Title", new Vector2(14f, 0f), new Vector2(240f, 44f), "<b><color=#00E5FF>SPROCKET LAN PVP</color></b> <size=12><color=#8892B0>v1.2</color></size>", 16f, (TextAlignmentOptions)4097);
		_headerBadgeText = CreateText(header.transform, "HeaderBadge", new Vector2(250f, 0f), new Vector2(160f, 44f), "<color=#FFB300>● IDLE</color>", 14f, (TextAlignmentOptions)4100);
		CreateButton(header.transform, "MinButton", new Vector2(430f, -8f), new Vector2(36f, 28f), "<b>_</b>", new Color(0.25f, 0.32f, 0.42f), () =>
		{
			ToggleVisibility();
		});
		GameObject glanceBar = CreatePanel(_mainPanel.transform, "GlanceBar", new Vector2(10f, -50f), new Vector2(460f, 30f), new Color(0.12f, 0.16f, 0.24f, 0.85f), new Vector2(0f, 1f));
		_quickGlanceText = CreateText(glanceBar.transform, "GlanceText", new Vector2(10f, 0f), new Vector2(440f, 30f), "Local IP: <color=#00E5FF>" + _localIp + "</color> | Ping: -- | Distance: --", 12f, (TextAlignmentOptions)4097);
		CreateText(_mainPanel.transform, "NetHeader", new Vector2(14f, -86f), new Vector2(450f, 22f), "<b><color=#00E5FF>LAN CONNECTION & SESSION:</color></b>", 13f, (TextAlignmentOptions)4097);
		GameObject netCard = CreatePanel(_mainPanel.transform, "NetCard", new Vector2(10f, -110f), new Vector2(460f, 85f), new Color(0.1f, 0.13f, 0.18f, 0.85f), new Vector2(0f, 1f));
		_networkInfoText = CreateText(netCard.transform, "NetInfo", new Vector2(10f, -8f), new Vector2(440f, 32f), $"Peer Target: <b><color=#00E5FF>{TargetIp}:{TargetPort}</color></b>\nStatus: Awaiting action", 12f, (TextAlignmentOptions)257);
		CreateButton(netCard.transform, "HostBtn", new Vector2(10f, -44f), new Vector2(140f, 32f), "<b>HOST LAN</b>", new Color(0.05f, 0.65f, 0.35f), () =>
		{
			transport?.StartHost(TargetPort);
			SetStatus($"Hosting on UDP port {TargetPort}. Waiting for peer...");
		});
		CreateButton(netCard.transform, "JoinBtn", new Vector2(160f, -44f), new Vector2(140f, 32f), "<b>JOIN PEER</b>", new Color(0f, 0.6f, 0.9f), () =>
		{
			transport?.Connect(TargetIp, TargetPort);
			SetStatus($"Connecting to {TargetIp}:{TargetPort}...");
		});
		CreateButton(netCard.transform, "StopBtn", new Vector2(310f, -44f), new Vector2(140f, 32f), "<b>DISCONNECT</b>", new Color(0.75f, 0.22f, 0.22f), () =>
		{
			transport?.Stop();
			SetStatus("Disconnected session.");
		});
		CreateText(_mainPanel.transform, "CombatHeader", new Vector2(14f, -204f), new Vector2(450f, 22f), "<b><color=#00E5FF>VEHICLE PAIRING & COMBAT TELEMETRY:</color></b>", 13f, (TextAlignmentOptions)4097);
		GameObject localCard = CreatePanel(_mainPanel.transform, "LocalCard", new Vector2(10f, -228f), new Vector2(460f, 85f), new Color(0.09f, 0.14f, 0.2f, 0.85f), new Vector2(0f, 1f));
		_localVehicleText = CreateText(localCard.transform, "LocalInfo", new Vector2(10f, -6f), new Vector2(310f, 72f), "<b><color=#00E5FF>YOUR TANK:</color></b> <Detecting...>\nBallistics: Native Physics\nTurrets: 0 | Cannons: 0", 12f, (TextAlignmentOptions)257);
		CreateButton(localCard.transform, "CycleLocalBtn", new Vector2(330f, -25f), new Vector2(120f, 36f), "Cycle Tank", new Color(0.18f, 0.25f, 0.35f), () =>
		{
			sync?.CycleLocalVehicle();
			SetStatus("Cycled local player tank.");
		});
		GameObject remoteCard = CreatePanel(_mainPanel.transform, "RemoteCard", new Vector2(10f, -320f), new Vector2(460f, 85f), new Color(0.18f, 0.1f, 0.12f, 0.85f), new Vector2(0f, 1f));
		_remoteVehicleText = CreateText(remoteCard.transform, "RemoteInfo", new Vector2(10f, -6f), new Vector2(310f, 72f), "<b><color=#FF5252>OPPONENT TANK:</color></b> <None Linked>\nBallistics: Native Physics\nTurrets: 0 | Cannons: 0", 12f, (TextAlignmentOptions)257);
		CreateButton(remoteCard.transform, "CycleRemoteBtn", new Vector2(330f, -25f), new Vector2(120f, 36f), "Cycle Tank", new Color(0.35f, 0.18f, 0.22f), () =>
		{
			sync?.CycleRemoteVehicle();
			SetStatus("Cycled opponent tank.");
		});
		CreateButton(_mainPanel.transform, "RescanBtn", new Vector2(10f, -415f), new Vector2(225f, 34f), "<b>RESCAN TANKS</b>", new Color(0.2f, 0.26f, 0.36f), () =>
		{
			sync?.FindVehiclesIfNeeded();
			SetStatus("Rescanned scene vehicles.");
		});
		CreateButton(_mainPanel.transform, "FireBtn", new Vector2(245f, -415f), new Vector2(225f, 34f), "<b>TEST FIRE</b>", new Color(0.7f, 0.35f, 0.1f), () =>
		{
			//IL_000f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0015: Unknown result type (might be due to invalid IL or missing references)
			sync?.TriggerRemoteFiring();
			SetStatus("Triggered test firing on remote tank!");
		});
		GameObject diagCard = CreatePanel(_mainPanel.transform, "DiagCard", new Vector2(10f, -460f), new Vector2(460f, 120f), new Color(0.08f, 0.1f, 0.15f, 0.9f), new Vector2(0f, 1f));
		_combatDiagText = CreateText(diagCard.transform, "DiagInfo", new Vector2(10f, -6f), new Vector2(440f, 108f), "Ballistics: Native Penetration & Spalling\nAim: Yaw +0.0° | Pitch +0.0°\nPackets: Sent 0 | Recv 0", 12f, (TextAlignmentOptions)257);
		GameObject footerCard = CreatePanel(_mainPanel.transform, "FooterCard", new Vector2(10f, -590f), new Vector2(460f, 75f), new Color(0.06f, 0.08f, 0.12f, 0.95f), new Vector2(0f, 1f));
		_statusLogText = CreateText(footerCard.transform, "StatusLog", new Vector2(10f, -6f), new Vector2(440f, 36f), "<color=#8892B0>Log:</color> " + _statusMessage, 11f, (TextAlignmentOptions)257);
		CreateText(footerCard.transform, "Keybinds", new Vector2(10f, -44f), new Vector2(440f, 26f), "<color=#8892B0>Hotkeys:</color> <b>[F8]</b> Cycle | <b>[F9]</b> HUD | <b>[F10]</b> Host | <b>[F11]</b> Join", 11f, (TextAlignmentOptions)4097);
	}

	private void BuildMinimizedBar()
	{
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_0053: Unknown result type (might be due to invalid IL or missing references)
		//IL_007e: Unknown result type (might be due to invalid IL or missing references)
		//IL_008d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ee: Unknown result type (might be due to invalid IL or missing references)
		_minimizedBar = CreatePanel(_canvasObject.transform, "MinimizedBar", new Vector2(25f, -25f), new Vector2(520f, 36f), new Color(0.06f, 0.09f, 0.14f, 0.92f), new Vector2(0f, 1f));
		_minimizedBarText = CreateText(_minimizedBar.transform, "MinText", new Vector2(12f, 0f), new Vector2(440f, 36f), "<b><color=#00E5FF>[F9] LAN PvP</color></b> | ● IDLE | Ping: -- | Dist: --", 13f, (TextAlignmentOptions)4097);
		CreateButton(_minimizedBar.transform, "OpenBtn", new Vector2(460f, -4f), new Vector2(48f, 28f), "<b>Open</b>", new Color(0f, 0.65f, 0.9f), () =>
		{
			ToggleVisibility();
		});
	}

	private void BuildDestructionBanner()
	{
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_0053: Unknown result type (might be due to invalid IL or missing references)
		//IL_008e: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e7: Expected Obj, but got Unknown
		//IL_0105: Unknown result type (might be due to invalid IL or missing references)
		//IL_0110: Unknown result type (might be due to invalid IL or missing references)
		//IL_0125: Unknown result type (might be due to invalid IL or missing references)
		//IL_012f: Unknown result type (might be due to invalid IL or missing references)
		//IL_017b: Unknown result type (might be due to invalid IL or missing references)
		_destructionBannerObject = CreatePanel(_canvasObject.transform, "DestructionBanner", new Vector2(0f, -65f), new Vector2(720f, 65f), new Color(0.12f, 0.02f, 0.04f, 0.94f), new Vector2(0.5f, 1f));
		_destructionBannerCanvasGroup = _destructionBannerObject.AddComponent<CanvasGroup>();
		GameObject accentPanel = CreatePanel(_destructionBannerObject.transform, "BannerAccent", new Vector2(0f, 0f), new Vector2(720f, 4f), new Color(1f, 0.15f, 0.15f, 0.95f), new Vector2(0.5f, 0f));
		_destructionBannerAccentImage = accentPanel.GetComponent<Image>();
		GameObject textGO = new GameObject("DestructionText");
		textGO.transform.SetParent(_destructionBannerObject.transform, false);
		RectTransform val = textGO.AddComponent<RectTransform>();
		val.anchorMin = Vector2.zero;
		val.anchorMax = Vector2.one;
		val.pivot = new Vector2(0.5f, 0.5f);
		val.sizeDelta = Vector2.zero;
		_destructionBannerText = textGO.AddComponent<TextMeshProUGUI>();
		((TMP_Text)_destructionBannerText).text = "";
		((TMP_Text)_destructionBannerText).fontSize = 20f;
		((TMP_Text)_destructionBannerText).alignment = (TextAlignmentOptions)514;
		((Graphic)_destructionBannerText).color = Color.white;
		((TMP_Text)_destructionBannerText).enableWordWrapping = true;
		if ((Object)(object)_fontAsset != (Object)null)
		{
			((TMP_Text)_destructionBannerText).font = _fontAsset;
		}
		_destructionBannerObject.SetActive(false);
	}

	public void ShowDestructionIndicator(string tankName, bool isKillConfirmation = false)
	{
		//IL_010c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0068: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			string msg;
			if (isKillConfirmation)
			{
				msg = "TARGET DESTROYED: " + tankName;
				if ((Object)(object)_destructionBannerText != (Object)null)
				{
					((TMP_Text)_destructionBannerText).text = "<b><color=#00E5FF>\ud83c\udfaf  " + msg.ToUpper() + "  \ud83c\udfaf</color></b>";
				}
				if ((Object)(object)_destructionBannerAccentImage != (Object)null)
				{
					((Graphic)_destructionBannerAccentImage).color = new Color(0f, 0.9f, 1f, 0.95f);
				}
				SetStatus("Target destroyed: " + tankName + "!");
				try
				{
					KillConfirmDisplayer kcd = Object.FindObjectOfType<KillConfirmDisplayer>();
					if ((Object)(object)kcd != (Object)null && ((Il2CppObjectBase)kcd).Pointer != IntPtr.Zero)
					{
						kcd.LogKill(tankName);
					}
				}
				catch
				{
				}
			}
			else
			{
				msg = "YOUR TANK WAS DESTROYED";
				if ((Object)(object)_destructionBannerText != (Object)null)
				{
					((TMP_Text)_destructionBannerText).text = "<b><color=#FF2A2A>☠  " + msg + "  ☠</color></b>";
				}
				if ((Object)(object)_destructionBannerAccentImage != (Object)null)
				{
					((Graphic)_destructionBannerAccentImage).color = new Color(1f, 0.15f, 0.15f, 0.95f);
				}
				SetStatus(msg);
			}
			_destructionBannerTimer = 5f;
			if ((Object)(object)_destructionBannerCanvasGroup != (Object)null)
			{
				_destructionBannerCanvasGroup.alpha = 1f;
			}
			if ((Object)(object)_destructionBannerObject != (Object)null)
			{
				_destructionBannerObject.SetActive(true);
			}
			MainMessageDisplayService displayService = Object.FindObjectOfType<MainMessageDisplayService>();
			if ((Object)(object)displayService != (Object)null && ((Il2CppObjectBase)displayService).Pointer != IntPtr.Zero)
			{
				displayService.SendMessage(isKillConfirmation ? ("Destroyed " + tankName) : msg, 5f);
			}
		}
		catch (Exception ex)
		{
			Plugin instance = Plugin.Instance;
			if (instance != null)
			{
				ManualLogSource log = ((BasePlugin)instance).Log;
				if (log != null)
				{
					log.LogWarning((object)("ShowDestructionIndicator note: " + ex.Message));
				}
			}
		}
	}

	private void BuildHitReportCard()
	{
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_0053: Unknown result type (might be due to invalid IL or missing references)
		//IL_008e: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00db: Expected Obj, but got Unknown
		//IL_00f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0104: Unknown result type (might be due to invalid IL or missing references)
		//IL_0119: Unknown result type (might be due to invalid IL or missing references)
		//IL_012e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0142: Unknown result type (might be due to invalid IL or missing references)
		//IL_018e: Unknown result type (might be due to invalid IL or missing references)
		_hitReportObject = CreatePanel(_canvasObject.transform, "HitReportCard", new Vector2(-30f, -80f), new Vector2(360f, 180f), new Color(0.06f, 0.08f, 0.12f, 0.94f), new Vector2(1f, 1f));
		_hitReportCanvasGroup = _hitReportObject.AddComponent<CanvasGroup>();
		CreatePanel(_hitReportObject.transform, "HitAccent", new Vector2(0f, 0f), new Vector2(4f, 180f), new Color(0f, 0.9f, 1f, 0.95f), new Vector2(0f, 0.5f));
		GameObject textGO = new GameObject("HitReportText");
		textGO.transform.SetParent(_hitReportObject.transform, false);
		RectTransform val = textGO.AddComponent<RectTransform>();
		val.anchorMin = Vector2.zero;
		val.anchorMax = Vector2.one;
		val.pivot = new Vector2(0.5f, 0.5f);
		val.offsetMin = new Vector2(14f, 8f);
		val.offsetMax = new Vector2(-10f, -8f);
		_hitReportText = textGO.AddComponent<TextMeshProUGUI>();
		((TMP_Text)_hitReportText).text = "";
		((TMP_Text)_hitReportText).fontSize = 12f;
		((TMP_Text)_hitReportText).alignment = (TextAlignmentOptions)257;
		((Graphic)_hitReportText).color = Color.white;
		((TMP_Text)_hitReportText).enableWordWrapping = true;
		if ((Object)(object)_fontAsset != (Object)null)
		{
			((TMP_Text)_hitReportText).font = _fontAsset;
		}
		_hitReportObject.SetActive(false);
	}

	public void RegisterOpponentDamage(string compName, bool isDestroyed, float damage)
	{
		try
		{
			if (string.IsNullOrEmpty(compName))
			{
				compName = "Armor / Structure";
			}
			if (Time.time - _lastDamageTime > 0.8f)
			{
				_recentDamageList.Clear();
			}
			_lastDamageTime = Time.time;
			DamageLogEntry existing = _recentDamageList.Find((DamageLogEntry x) => string.Equals(x.ComponentName, compName, StringComparison.OrdinalIgnoreCase));
			if (existing != null)
			{
				if (isDestroyed)
				{
					existing.IsDestroyed = true;
				}
				existing.Damage += damage;
				existing.Timestamp = Time.time;
			}
			else
			{
				_recentDamageList.Add(new DamageLogEntry
				{
					ComponentName = compName,
					IsDestroyed = isDestroyed,
					Damage = damage,
					Timestamp = Time.time
				});
			}
			if (_recentDamageList.Count > 6)
			{
				_recentDamageList.RemoveAt(0);
			}
			UpdateHitReportDisplay();
			_hitReportTimer = 6f;
			if ((Object)(object)_hitReportCanvasGroup != (Object)null)
			{
				_hitReportCanvasGroup.alpha = 1f;
			}
			if ((Object)(object)_hitReportObject != (Object)null)
			{
				_hitReportObject.SetActive(true);
			}
		}
		catch (Exception ex)
		{
			Plugin instance = Plugin.Instance;
			if (instance != null)
			{
				ManualLogSource log = ((BasePlugin)instance).Log;
				if (log != null)
				{
					log.LogWarning((object)("RegisterOpponentDamage error: " + ex.Message));
				}
			}
		}
	}

	private void UpdateHitReportDisplay()
	{
		if (!((Object)(object)_hitReportText == (Object)null))
		{
			StringBuilder sb = new StringBuilder();
			sb.AppendLine("<b><color=#00E5FF>⚡ HIT REPORT</color></b> <size=11><color=#8892B0>Target Damaged</color></size>");
			for (int i = 0; i < _recentDamageList.Count; i++)
			{
				DamageLogEntry entry = _recentDamageList[i];
				string statusText;
				if (entry.IsDestroyed)
				{
					statusText = ((entry.ComponentName.Contains("Driver") || entry.ComponentName.Contains("Gunner") || entry.ComponentName.Contains("Loader") || entry.ComponentName.Contains("Commander") || entry.ComponentName.Contains("Radio")) ? "<color=#FF1744><b>(KNOCKED OUT)</b></color>" : "<color=#FF1744><b>(DESTROYED)</b></color>");
				}
				else
				{
					statusText = $"<color=#FFD54F>(Damaged -{entry.Damage:F0} HP)</color>";
				}
				StringBuilder stringBuilder = sb;
				StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(26, 2, stringBuilder);
				handler.AppendLiteral("• <color=#FFFFFF>");
				handler.AppendFormatted(entry.ComponentName);
				handler.AppendLiteral("</color> ");
				handler.AppendFormatted(statusText);
				stringBuilder.AppendLine(ref handler);
			}
			((TMP_Text)_hitReportText).text = sb.ToString().TrimEnd();
		}
	}

	private void UpdatePanelVisibility()
	{
		if ((Object)(object)_mainPanel != (Object)null)
		{
			_mainPanel.SetActive(_isVisible);
		}
		if ((Object)(object)_minimizedBar != (Object)null)
		{
			_minimizedBar.SetActive(!_isVisible);
		}
	}

	private void HandleButtonClicks()
	{
		//IL_0010: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_017d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fc: Unknown result type (might be due to invalid IL or missing references)
		Mouse mouse = Mouse.current;
		if (mouse == null)
		{
			return;
		}
		Vector2 mousePos = ((InputControl<Vector2>)(object)((Pointer)mouse).position).ReadValue();
		bool clicked = mouse.leftButton.wasPressedThisFrame;
		for (int i = 0; i < _clickableButtons.Count; i++)
		{
			CustomClickableButton btn = _clickableButtons[i];
			if (btn == null)
			{
				continue;
			}
			try
			{
				if ((Object)(object)btn.Rect == (Object)null || ((Il2CppObjectBase)btn.Rect).Pointer == IntPtr.Zero || (Object)(object)((Component)btn.Rect).gameObject == (Object)null || ((Il2CppObjectBase)((Component)btn.Rect).gameObject).Pointer == IntPtr.Zero || !((Component)btn.Rect).gameObject.activeInHierarchy)
				{
					continue;
				}
				if (RectTransformUtility.RectangleContainsScreenPoint(btn.Rect, mousePos, (Camera)null))
				{
					if ((Object)(object)btn.Image != (Object)null && ((Il2CppObjectBase)btn.Image).Pointer != IntPtr.Zero)
					{
						((Graphic)btn.Image).color = btn.HoverColor;
					}
					if (!clicked)
					{
						continue;
					}
					try
					{
						btn.OnClick?.Invoke();
					}
					catch (Exception ex)
					{
						Plugin instance = Plugin.Instance;
						if (instance != null)
						{
							ManualLogSource log = ((BasePlugin)instance).Log;
							if (log != null)
							{
								log.LogWarning((object)("Button click error: " + ex.Message));
							}
						}
					}
					continue;
				}
				if ((Object)(object)btn.Image != (Object)null && ((Il2CppObjectBase)btn.Image).Pointer != IntPtr.Zero)
				{
					((Graphic)btn.Image).color = btn.NormalColor;
				}
			}
			catch
			{
			}
		}
	}

	private void UpdateTelemetryDisplays(UdpNetworkTransport transport, VehicleSync sync)
	{
		bool num = transport?.IsConnected ?? false;
		bool isHosting = transport?.IsHosting ?? false;
		string statusBadge;
		if (num)
		{
			statusBadge = "<color=#00E676>● CONNECTED</color>";
		}
		else
		{
			statusBadge = (isHosting ? "<color=#00E676>● HOSTING</color>" : "<color=#FFB300>● IDLE</color>");
		}
		if ((Object)(object)_headerBadgeText != (Object)null)
		{
			((TMP_Text)_headerBadgeText).text = statusBadge;
		}
		string pingStr = (num ? $"{transport.RoundTripTimeMs:F1} ms" : "--");
		string distStr = ((sync != null && !sync.IsResetting && (Object)(object)sync.RemoteVehicle != (Object)null && ((Il2CppObjectBase)sync.RemoteVehicle).Pointer != IntPtr.Zero) ? $"{sync.DistanceBetweenTanks:F1} m" : "--");
		if ((Object)(object)_quickGlanceText != (Object)null)
		{
			((TMP_Text)_quickGlanceText).text = $"Local IP: <color=#00E5FF>{_localIp}</color>  |  Ping: <color=#00E676><b>{pingStr}</b></color>  |  Distance: <color=#FFD54F><b>{distStr}</b></color>";
		}
		if ((Object)(object)_networkInfoText != (Object)null)
		{
			string peerStr;
			if (transport?.RemoteEndPoint != null)
			{
				peerStr = transport.RemoteEndPoint.ToString();
			}
			else
			{
				peerStr = (isHosting ? "Awaiting client..." : "Not connected");
			}
			((TMP_Text)_networkInfoText).text = $"Peer Target: <b><color=#00E5FF>{TargetIp}:{TargetPort}</color></b> | Connected: <color=#00E676>{peerStr}</color>\nPackets: Sent {transport?.PacketsSent ?? 0:N0} | Recv {transport?.PacketsReceived ?? 0:N0} | RTT: <b>{pingStr}</b>";
		}
		if ((Object)(object)_localVehicleText != (Object)null)
		{
			string localName = SafeGetVehicleName(sync?.LocalVehicle);
			int tCount = ((sync != null && !sync.IsResetting) ? sync.LocalTurretCount : 0);
			int cCount = ((sync != null && !sync.IsResetting) ? sync.LocalCannonCount : 0);
			float yaw = ((sync != null && !sync.IsResetting) ? sync.LocalTurretYaw : 0f);
			float pitch = ((sync != null && !sync.IsResetting) ? sync.LocalGunPitch : 0f);
			string ready = ((sync != null && !sync.IsResetting && sync.LocalWeaponReady) ? "<color=#00E676>READY</color>" : "<color=#FFB300>CYCLING</color>");
			((TMP_Text)_localVehicleText).text = $"<b><color=#00E5FF>YOUR TANK:</color></b> {localName}\nBallistics: <color=#00E676><b>Native Physics & Armor Sim</b></color>\nTurrets: <b>{tCount}</b> | Cannons: <b>{cCount}</b> | Status: <b>{ready}</b>\nAim: <b>{yaw:+0.0;-0.0;0.0}° / {pitch:+0.0;-0.0;0.0}°</b>";
		}
		if ((Object)(object)_remoteVehicleText != (Object)null)
		{
			string remoteName = SafeGetVehicleName(sync?.RemoteVehicle);
			int tCount2 = ((sync != null && !sync.IsResetting) ? sync.RemoteTurretCount : 0);
			int cCount2 = ((sync != null && !sync.IsResetting) ? sync.RemoteCannonCount : 0);
			float yaw2 = ((sync != null && !sync.IsResetting) ? sync.RemoteTurretYaw : 0f);
			float pitch2 = ((sync != null && !sync.IsResetting) ? sync.RemoteGunPitch : 0f);
			string ready2 = ((sync != null && !sync.IsResetting && sync.RemoteWeaponReady) ? "<color=#00E676>READY</color>" : "<color=#FFB300>CYCLING</color>");
			((TMP_Text)_remoteVehicleText).text = $"<b><color=#FF5252>OPPONENT TANK:</color></b> {remoteName}\nBallistics: <color=#00E676><b>Native Physics & Armor Sim</b></color>\nTurrets: <b>{tCount2}</b> | Cannons: <b>{cCount2}</b> | Status: <b>{ready2}</b>\nAim: <b>{yaw2:+0.0;-0.0;0.0}° / {pitch2:+0.0;-0.0;0.0}°</b>";
		}
		if ((Object)(object)_combatDiagText != (Object)null)
		{
			string syncState = sync?.SyncStatus ?? "Idle";
			string damageSummary = ((_recentDamageList.Count > 0) ? string.Join(", ", _recentDamageList.ConvertAll((DamageLogEntry d) => d.ComponentName + " (" + (d.IsDestroyed ? "Destroyed" : "Damaged") + ")")) : "None");
			((TMP_Text)_combatDiagText).text = $"Ballistics: <color=#00E676><b>Native Penetration & Spalling</b></color>\nSync Status: <color=#00E5FF>{syncState}</color>\nLatest Hit: <color=#FFD54F>{damageSummary}</color>\nMulti-Cannon Engine: <color=#00E676>Multi-Breech Aim & Fire Sync</color>";
		}
		if ((Object)(object)_statusLogText != (Object)null)
		{
			((TMP_Text)_statusLogText).text = "<color=#8892B0>Log:</color> " + _statusMessage;
		}
		if ((Object)(object)_minimizedBarText != (Object)null)
		{
			((TMP_Text)_minimizedBarText).text = $"<b><color=#00E5FF>[F9] LAN PvP</color></b> | {statusBadge} | Ping: <b>{pingStr}</b> | Dist: <b>{distStr}</b> | Ballistics: <b>Native Sim</b>";
		}
	}

	public void SetStatus(string msg)
	{
		_statusMessage = msg;
		Plugin instance = Plugin.Instance;
		if (instance != null)
		{
			ManualLogSource log = ((BasePlugin)instance).Log;
			if (log != null)
			{
				log.LogInfo((object)("[LAN Mod Status] " + msg));
			}
		}
	}

	private GameObject CreatePanel(Transform parent, string name, Vector2 pos, Vector2 size, Color color, Vector2 anchor)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		//IL_004d: Expected Obj, but got Unknown
		GameObject val = new GameObject(name);
		val.transform.SetParent(parent, false);
		RectTransform val2 = val.AddComponent<RectTransform>();
		val2.anchorMin = anchor;
		val2.anchorMax = anchor;
		val2.pivot = anchor;
		val2.anchoredPosition = pos;
		val2.sizeDelta = size;
		((Graphic)val.AddComponent<Image>()).color = color;
		return val;
	}

	private TextMeshProUGUI CreateText(Transform parent, string name, Vector2 pos, Vector2 size, string text, float fontSize, TextAlignmentOptions alignment)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_004e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0059: Unknown result type (might be due to invalid IL or missing references)
		//IL_005f: Unknown result type (might be due to invalid IL or missing references)
		//IL_007d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0085: Unknown result type (might be due to invalid IL or missing references)
		GameObject val = new GameObject(name);
		val.transform.SetParent(parent, false);
		RectTransform val2 = val.AddComponent<RectTransform>();
		val2.anchorMin = new Vector2(0f, 1f);
		val2.anchorMax = new Vector2(0f, 1f);
		val2.pivot = new Vector2(0f, 1f);
		val2.anchoredPosition = pos;
		val2.sizeDelta = size;
		TextMeshProUGUI tmp = val.AddComponent<TextMeshProUGUI>();
		((TMP_Text)tmp).text = text;
		((TMP_Text)tmp).fontSize = fontSize;
		((TMP_Text)tmp).alignment = alignment;
		((Graphic)tmp).color = Color.white;
		((TMP_Text)tmp).enableWordWrapping = true;
		if ((Object)(object)_fontAsset != (Object)null)
		{
			((TMP_Text)tmp).font = _fontAsset;
		}
		return tmp;
	}

	private GameObject CreateButton(Transform parent, string name, Vector2 pos, Vector2 size, string label, Color bgColor, Action onClick)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Expected Obj, but got Unknown
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_003b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0062: Unknown result type (might be due to invalid IL or missing references)
		//IL_0071: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ef: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0128: Unknown result type (might be due to invalid IL or missing references)
		//IL_0096: Unknown result type (might be due to invalid IL or missing references)
		//IL_0098: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00aa: Unknown result type (might be due to invalid IL or missing references)
		//IL_00af: Unknown result type (might be due to invalid IL or missing references)
		GameObject go = new GameObject(name);
		go.transform.SetParent(parent, false);
		RectTransform rt = go.AddComponent<RectTransform>();
		rt.anchorMin = new Vector2(0f, 1f);
		rt.anchorMax = new Vector2(0f, 1f);
		rt.pivot = new Vector2(0f, 1f);
		rt.anchoredPosition = pos;
		rt.sizeDelta = size;
		Image img = go.AddComponent<Image>();
		((Graphic)img).color = bgColor;
		if (onClick != null)
		{
			_clickableButtons.Add(new CustomClickableButton
			{
				Rect = rt,
				Image = img,
				NormalColor = bgColor,
				HoverColor = Color.Lerp(bgColor, Color.white, 0.25f),
				OnClick = onClick
			});
		}
		GameObject val = new GameObject("Label");
		val.transform.SetParent(go.transform, false);
		RectTransform val2 = val.AddComponent<RectTransform>();
		val2.anchorMin = Vector2.zero;
		val2.anchorMax = Vector2.one;
		val2.sizeDelta = Vector2.zero;
		TextMeshProUGUI tmp = val.AddComponent<TextMeshProUGUI>();
		((TMP_Text)tmp).text = label;
		((TMP_Text)tmp).fontSize = 13f;
		((TMP_Text)tmp).alignment = (TextAlignmentOptions)514;
		((Graphic)tmp).color = Color.white;
		if ((Object)(object)_fontAsset != (Object)null)
		{
			((TMP_Text)tmp).font = _fontAsset;
		}
		return go;
	}

	private void ApplyFontToAll()
	{
		if ((Object)(object)_canvasObject == (Object)null || (Object)(object)_fontAsset == (Object)null)
		{
			return;
		}
		try
		{
			foreach (TextMeshProUGUI t in _canvasObject.GetComponentsInChildren<TextMeshProUGUI>(true))
			{
				if ((Object)(object)t != (Object)null)
				{
					((TMP_Text)t).font = _fontAsset;
				}
			}
		}
		catch
		{
		}
	}

	private TMP_FontAsset FindFont()
	{
		try
		{
			if ((Object)(object)TMP_Settings.defaultFontAsset != (Object)null)
			{
				return TMP_Settings.defaultFontAsset;
			}
		}
		catch
		{
		}
		try
		{
			Il2CppArrayBase<TMP_FontAsset> fontAssets = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
			if (fontAssets != null && fontAssets.Length > 0)
			{
				foreach (TMP_FontAsset fa in fontAssets)
				{
					if ((Object)(object)fa != (Object)null && !string.IsNullOrEmpty(((Object)fa).name))
					{
						return fa;
					}
				}
			}
		}
		catch
		{
		}
		try
		{
			Il2CppArrayBase<TextMeshProUGUI> tmps = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>();
			if (tmps != null && tmps.Length > 0)
			{
				foreach (TextMeshProUGUI t in tmps)
				{
					if ((Object)(object)t != (Object)null && (Object)(object)((TMP_Text)t).font != (Object)null)
					{
						return ((TMP_Text)t).font;
					}
				}
			}
		}
		catch
		{
		}
		return null;
	}

	private string SafeGetVehicleName(VehicleBehaviour vehicle)
	{
		if ((Object)(object)vehicle == (Object)null)
		{
			return "<None Detected>";
		}
		try
		{
			if (((Il2CppObjectBase)vehicle).Pointer == IntPtr.Zero)
			{
				return "<None Detected>";
			}
			GameObject go = ((Component)vehicle).gameObject;
			if ((Object)(object)go != (Object)null && ((Il2CppObjectBase)go).Pointer != IntPtr.Zero)
			{
				return ((Object)go).name;
			}
		}
		catch
		{
		}
		return "<Tank>";
	}

	private string GetLocalIPAddress()
	{
		try
		{
			IPAddress[] addressList = Dns.GetHostEntry(Dns.GetHostName()).AddressList;
			foreach (IPAddress ip in addressList)
			{
				if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
				{
					return ip.ToString();
				}
			}
		}
		catch
		{
		}
		return "127.0.0.1";
	}
}
