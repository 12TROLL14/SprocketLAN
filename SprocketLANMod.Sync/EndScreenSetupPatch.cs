using HarmonyLib;
using Sprocket;
using UnityEngine;

namespace SprocketLANMod.Sync;

[HarmonyPatch(typeof(EndScreen), "Setup")]
public static class EndScreenSetupPatch
{
	public static bool Prefix(EndScreen __instance)
	{
		MultiplayerController mc = MultiplayerController.Instance;
		if (mc != null && ((mc.Transport != null && mc.Transport.IsConnected) || (Object)(object)mc.VehicleSync?.RemoteVehicle != (Object)null))
		{
			if ((Object)(object)__instance != (Object)null && (Object)(object)((Component)__instance).gameObject != (Object)null)
			{
				((Component)__instance).gameObject.SetActive(false);
			}
			return false;
		}
		return true;
	}
}
