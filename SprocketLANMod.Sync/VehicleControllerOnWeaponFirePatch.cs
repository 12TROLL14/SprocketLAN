using System;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppSystem;
using Sprocket;
using Sprocket.Gameplay.VehicleControl;
using Sprocket.WeaponFramework;
using UnityEngine;

namespace SprocketLANMod.Sync;

[HarmonyPatch(typeof(VehicleController), "OnWeaponFire")]
public static class VehicleControllerOnWeaponFirePatch
{
	public static void Postfix(VehicleController __instance, Object sender, WeaponFireEventArgs e)
	{
		if ((Object)(object)__instance == (Object)null || ((Il2CppObjectBase)__instance).Pointer == IntPtr.Zero)
		{
			return;
		}
		try
		{
			VehicleSync vs = MultiplayerController.Instance?.VehicleSync;
			if (vs != null && !vs.IsTriggeringRemoteFiring && (Object)(object)vs.LocalVehicle != (Object)null)
			{
				WeaponBehaviour wb = ((sender != null) ? ((Il2CppObjectBase)sender).TryCast<WeaponBehaviour>() : null);
				vs.OnLocalWeaponFired(wb);
			}
		}
		catch
		{
		}
	}
}
