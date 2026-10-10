using System;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Sprocket;

namespace SprocketLANMod.Sync;

[HarmonyPatch(typeof(SceneResetter), "ResetScene")]
public static class SceneResetterResetScenePatch
{
	public static void Prefix(SceneResetter __instance)
	{
		try
		{
			MultiplayerController mc = MultiplayerController.Instance;
			if (mc == null || mc.Transport == null || !mc.Transport.IsConnected)
			{
				return;
			}
			VehicleSync vs = mc.VehicleSync;
			if (vs == null)
			{
				return;
			}
			if (!vs.IsResettingFromPeer)
			{
				Plugin instance = Plugin.Instance;
				if (instance != null)
				{
					ManualLogSource log = ((BasePlugin)instance).Log;
					if (log != null)
					{
						log.LogInfo((object)"[LAN Mod] SceneResetter.ResetScene() intercepted locally. Dispatching BattleReset to peer.");
					}
				}
				vs.SendBattleResetPacket(mc.Transport);
			}
			else
			{
				Plugin instance2 = Plugin.Instance;
				if (instance2 != null)
				{
					ManualLogSource log2 = ((BasePlugin)instance2).Log;
					if (log2 != null)
					{
						log2.LogInfo((object)"[LAN Mod] SceneResetter.ResetScene() running as response to peer BattleReset. Suppressing echo.");
					}
				}
			}
			vs.OnLocalSceneResetting();
		}
		catch (Exception ex)
		{
			Plugin instance3 = Plugin.Instance;
			if (instance3 != null)
			{
				ManualLogSource log3 = ((BasePlugin)instance3).Log;
				if (log3 != null)
				{
					log3.LogWarning((object)("Error in SceneResetter.ResetScene Prefix: " + ex.Message));
				}
			}
		}
	}
}
