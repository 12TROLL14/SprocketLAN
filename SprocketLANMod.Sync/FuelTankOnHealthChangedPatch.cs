using System;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Sprocket.Vehicles;
using Sprocket.Vehicles.Fires;
using Sprocket.Vehicles.FuelTanks;
using UnityEngine;

namespace SprocketLANMod.Sync;

[HarmonyPatch(typeof(FuelTank), "OnHealthChanged")]
public static class FuelTankOnHealthChangedPatch
{
	public static void Postfix(FuelTank __instance)
	{
		//IL_0081: Unknown result type (might be due to invalid IL or missing references)
		//IL_0088: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			if (!((Object)(object)__instance != (Object)null) || !(((Il2CppObjectBase)__instance).Pointer != IntPtr.Zero) || !(((VehicleComponent)__instance).HealthFraction <= 0.2f))
			{
				return;
			}
			if (__instance.behaviour != null && !__instance.behaviour.fireStarted)
			{
				__instance.behaviour.StartFire();
				__instance.behaviour.fireStarted = true;
			}
			VehicleBehaviour veh = ((Component)__instance).GetComponentInParent<VehicleBehaviour>();
			if (!((Object)(object)veh != (Object)null) || !(((Il2CppObjectBase)veh).Pointer != IntPtr.Zero))
			{
				return;
			}
			veh.Flags = (VehicleFlags)(veh.Flags | 0x40);
			try
			{
				FireControlVehicleModule fm = default;
				if (veh.TryGetModule<FireControlVehicleModule>(ref fm) && fm != null && !fm.OnFire)
				{
					fm.StartFire(45f);
				}
			}
			catch
			{
			}
		}
		catch
		{
		}
	}
}
