using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Sprocket;
using SprocketLANMod.Network;
using SprocketLANMod.Sync;
using SprocketLANMod.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.SceneManagement;

namespace SprocketLANMod;

public class MultiplayerController : IDisposable
{
	private int _lastUpdateFrame = -1;

	public static MultiplayerController Instance { get; private set; }

	public UdpNetworkTransport Transport { get; private set; }

	public VehicleSync VehicleSync { get; private set; }

	public LANManagerGUI GUI { get; private set; }

	public ConfigEntry<bool> ConfigIsHost { get; set; }

	public ConfigEntry<string> ConfigTargetIp { get; set; }

	public ConfigEntry<int> ConfigPort { get; set; }

	public ConfigEntry<float> ConfigSendRate { get; set; }

	public ConfigEntry<bool> ConfigInterpolation { get; set; }

	public ConfigEntry<bool> ConfigDisableAI { get; set; }

	public MultiplayerController()
	{
		Instance = this;
		Transport = new UdpNetworkTransport();
		Transport.LogMessage += (string msg) =>
		{
			Plugin instance2 = Plugin.Instance;
			if (instance2 != null)
			{
				ManualLogSource log2 = ((BasePlugin)instance2).Log;
				if (log2 != null)
				{
					log2.LogInfo((object)msg);
				}
			}
		};
		VehicleSync = new VehicleSync();
		VehicleSync.Initialize(Transport);
		GUI = new LANManagerGUI();
		GUI.Initialize();
		Plugin instance = Plugin.Instance;
		if (instance != null)
		{
			ManualLogSource log = ((BasePlugin)instance).Log;
			if (log != null)
			{
				log.LogInfo((object)"MultiplayerController initialized successfully.");
			}
		}
	}

	public void BindConfig(ConfigFile config)
	{
		ConfigIsHost = config.Bind<bool>("Network", "IsHost", false, "Whether to automatically host on startup");
		ConfigTargetIp = config.Bind<string>("Network", "TargetIP", "127.0.0.1", "Remote IP address to connect to (LAN or loopback)");
		ConfigPort = config.Bind<int>("Network", "Port", 7777, "UDP port for LAN multiplayer");
		ConfigSendRate = config.Bind<float>("Sync", "SendRateHz", 30f, "Network tick rate (Hz)");
		ConfigInterpolation = config.Bind<bool>("Sync", "SmoothInterpolation", true, "Enable smooth interpolation for remote tank movement");
		ConfigDisableAI = config.Bind<bool>("Sync", "DisableRemoteAI", true, "Neutralize AI scripts on the remote tank");
		if (VehicleSync != null)
		{
			VehicleSync.SendRateHz = ConfigSendRate.Value;
			VehicleSync.SmoothInterpolation = ConfigInterpolation.Value;
			VehicleSync.DisableRemoteAI = ConfigDisableAI.Value;
		}
		if (GUI != null)
		{
			GUI.TargetIp = ConfigTargetIp.Value;
			GUI.TargetPort = ConfigPort.Value;
		}
		if (ConfigIsHost.Value)
		{
			StartHost(ConfigPort.Value);
		}
	}

	public void StartHost(int port = 7777)
	{
		VehicleSync?.ResetSession();
		Transport.StartHost(port);
		ShowBanner($"[LAN Mod] Hosting on UDP port {port}. Waiting for peer...");
	}

	public void Connect(string ip, int port = 7777)
	{
		VehicleSync?.ResetSession();
		Transport.Connect(ip, port);
		ShowBanner($"[LAN Mod] Connecting to {ip}:{port}...");
	}

	public void Disconnect()
	{
		VehicleSync?.ResetSession();
		Transport.Stop();
		ShowBanner("[LAN Mod] Disconnected session.");
	}

	public void Update()
	{
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		if (Time.frameCount == _lastUpdateFrame)
		{
			return;
		}
		_lastUpdateFrame = Time.frameCount;
		HandleHotkeys();
		Transport?.Update();
		try
		{
			Scene activeScene = SceneManager.GetActiveScene();
			string sceneName = activeScene.name ?? "";
			if (string.Equals(sceneName, "MainMenu", StringComparison.OrdinalIgnoreCase) || sceneName.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0 || sceneName.IndexOf("Load", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				VehicleSync?.ClearVehicles();
			}
			else
			{
				VehicleSync?.Update(Transport);
			}
		}
		catch
		{
		}
		try
		{
			GUI?.Update(Transport, VehicleSync);
		}
		catch
		{
		}
	}

	public void FixedUpdate()
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			Scene activeScene = SceneManager.GetActiveScene();
			string sceneName = activeScene.name ?? "";
			if (!string.Equals(sceneName, "MainMenu", StringComparison.OrdinalIgnoreCase) && sceneName.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) < 0 && sceneName.IndexOf("Load", StringComparison.OrdinalIgnoreCase) < 0)
			{
				VehicleSync?.FixedUpdate();
			}
		}
		catch
		{
		}
	}

	private void HandleHotkeys()
	{
		Keyboard kb = Keyboard.current;
		if (kb == null)
		{
			return;
		}
		if (((ButtonControl)kb.f9Key).wasPressedThisFrame || ((ButtonControl)kb.insertKey).wasPressedThisFrame)
		{
			GUI?.ToggleVisibility();
		}
		else if (((ButtonControl)kb.f10Key).wasPressedThisFrame)
		{
			if (Transport != null && !Transport.IsConnected && !Transport.IsHosting)
			{
				int port = ((ConfigPort != null) ? ConfigPort.Value : 7777);
				StartHost(port);
			}
		}
		else if (((ButtonControl)kb.f11Key).wasPressedThisFrame)
		{
			string targetIp = GUI?.TargetIp ?? ConfigTargetIp?.Value ?? "127.0.0.1";
			int port2 = GUI?.TargetPort ?? ConfigPort?.Value ?? 7777;
			Connect(targetIp, port2);
		}
		else if (((ButtonControl)kb.f8Key).wasPressedThisFrame)
		{
			VehicleSync?.FindVehiclesIfNeeded();
			ShowBanner("[LAN Mod] Tanks: Local=" + VehicleSync?.LocalVehicleName + ", Remote=" + VehicleSync?.RemoteVehicleName);
		}
	}

	public void ShowBanner(string text)
	{
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0034: Unknown result type (might be due to invalid IL or missing references)
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_003f: Expected Obj, but got Unknown
		Plugin instance = Plugin.Instance;
		if (instance != null)
		{
			ManualLogSource log = ((BasePlugin)instance).Log;
			if (log != null)
			{
				log.LogInfo((object)text);
			}
		}
		try
		{
			UIMessageDisplayer displayer = Object.FindObjectOfType<UIMessageDisplayer>();
			if ((Object)(object)displayer != (Object)null)
			{
				Message msg = new Message(text, (MessageFlag)0, Color.yellow, (Sprite)null, Color.yellow);
				displayer.SendMessage(ref msg);
			}
		}
		catch
		{
		}
	}

	public void Dispose()
	{
		try
		{
			VehicleSync?.Dispose(Transport);
			Transport?.Stop();
		}
		catch
		{
		}
	}
}
