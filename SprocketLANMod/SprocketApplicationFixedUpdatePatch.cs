using System;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Sprocket;

namespace SprocketLANMod;

[HarmonyPatch(typeof(SprocketApplication), "FixedUpdate")]
public static class SprocketApplicationFixedUpdatePatch
{
	public static void Postfix()
	{
		try
		{
			Plugin.Instance?.Controller?.FixedUpdate();
		}
		catch (Exception ex)
		{
			Plugin instance = Plugin.Instance;
			if (instance != null)
			{
				ManualLogSource log = ((BasePlugin)instance).Log;
				if (log != null)
				{
					log.LogError((object)("FixedUpdate error: " + ex));
				}
			}
		}
	}
}
