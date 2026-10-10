using System;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Sprocket;
using Sprocket.Vehicles;
using Sprocket.Vehicles.Fires;
using Sprocket.Vehicles.FuelTanks;
using Sprocket.Vehicles.Powertrains;
using UnityEngine;

namespace SprocketLANMod.Sync;

[HarmonyPatch(typeof(FuelTankBehaviour), "OnHealthChanged", new Type[]
{
	typeof(HealthInfo),
	typeof(HealthChangeInfo)
})]
public static class FuelTankBehaviourOnHealthChangedInfoPatch
{
	public static void Postfix(FuelTankBehaviour __instance)
	{
		//IL_00a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a8: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			if (__instance == null || !(((Il2CppObjectBase)__instance).Pointer != IntPtr.Zero) || !(__instance.HealthFraction <= 0.2f) || __instance.fireStarted)
			{
				return;
			}
			__instance.StartFire();
			__instance.fireStarted = true;
			if (__instance.tank == null || !(((Il2CppObjectBase)__instance.tank).Pointer != IntPtr.Zero))
			{
				return;
			}
			FuelTank ft = ((Il2CppObjectBase)__instance.tank).TryCast<FuelTank>();
			VehicleBehaviour veh = (((Object)(object)ft != (Object)null) ? ((Component)ft).GetComponentInParent<VehicleBehaviour>() : null);
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
