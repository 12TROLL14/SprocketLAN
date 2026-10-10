using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace SprocketLANMod;

[BepInPlugin("com.universalmodder.sprocket.pvplan", "Sprocket LAN Multiplayer PvP", "1.0.0")]
public class Plugin : BasePlugin
{
	private Harmony _harmony;

	public static Plugin Instance { get; private set; }

	public MultiplayerController Controller { get; private set; }

	public override void Load()
	{
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_0042: Expected Obj, but got Unknown
		Instance = this;
		((BasePlugin)this).Log.LogInfo((object)"=== Sprocket LAN Multiplayer PvP Mod Loading ===");
		try
		{
			Controller = new MultiplayerController();
			Controller.BindConfig(((BasePlugin)this).Config);
			_harmony = new Harmony("com.universalmodder.sprocket.pvplan");
			_harmony.PatchAll(typeof(Plugin).Assembly);
			((BasePlugin)this).Log.LogInfo((object)"Sprocket LAN Multiplayer PvP Mod loaded successfully!");
			((BasePlugin)this).Log.LogInfo((object)"LAN Mod Menu: Press [F9] to Toggle In-Game Tactical HUD / Mod Menu");
		}
		catch (Exception ex)
		{
			((BasePlugin)this).Log.LogError((object)("Failed to initialize Sprocket LAN Multiplayer Mod: " + ex));
		}
	}
}
