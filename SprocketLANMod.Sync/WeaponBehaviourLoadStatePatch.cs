using System;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Sprocket.WeaponFramework;

namespace SprocketLANMod.Sync;

[HarmonyPatch(/*Could not decode attribute arguments.*/)]
public static class WeaponBehaviourLoadStatePatch
{
	public static bool Prefix(WeaponBehaviour __instance, ref LoadState __result)
	{
		if (__instance == null || ((Il2CppObjectBase)__instance).Pointer == IntPtr.Zero)
		{
			return true;
		}
		try
		{
			VehicleSync vs = MultiplayerController.Instance?.VehicleSync;
			if (vs != null && vs.IsTriggeringRemoteFiring)
			{
				__result = (LoadState)0;
				return false;
			}
		}
		catch
		{
		}
		return true;
	}
}
