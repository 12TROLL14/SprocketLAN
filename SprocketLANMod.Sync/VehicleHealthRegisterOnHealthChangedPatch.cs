using System;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Sprocket;
using Sprocket.Vehicles;

namespace SprocketLANMod.Sync;

[HarmonyPatch(typeof(VehicleHealthRegister), "OnHealthChanged")]
public static class VehicleHealthRegisterOnHealthChangedPatch
{
	public static void Postfix(VehicleHealthRegister __instance, IDurable healthPool, HealthChangeInfo change)
	{
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		if (__instance == null || ((Il2CppObjectBase)__instance).Pointer == IntPtr.Zero || healthPool == null || ((Il2CppObjectBase)healthPool).Pointer == IntPtr.Zero)
		{
			return;
		}
		try
		{
			(MultiplayerController.Instance?.VehicleSync)?.HandleHealthChanged(__instance, healthPool, change);
		}
		catch
		{
		}
	}
}
