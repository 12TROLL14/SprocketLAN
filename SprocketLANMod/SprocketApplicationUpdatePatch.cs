using System;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Sprocket;

namespace SprocketLANMod;

[HarmonyPatch(typeof(SprocketApplication), "Update")]
public static class SprocketApplicationUpdatePatch
{
	public static void Postfix()
	{
		try
		{
			Plugin.Instance?.Controller?.Update();
		}
		catch (Exception ex)
		{
			Plugin instance = Plugin.Instance;
			if (instance != null)
			{
				ManualLogSource log = ((BasePlugin)instance).Log;
				if (log != null)
				{
					log.LogError((object)("Update error: " + ex));
				}
			}
		}
	}
}
