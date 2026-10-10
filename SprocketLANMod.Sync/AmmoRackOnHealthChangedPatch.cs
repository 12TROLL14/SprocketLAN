using System;
using BepInEx.Core.Logging.Interpolation;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Sprocket.Vehicles;
using Sprocket.Vehicles.AmmunitionStorage;
using UnityEngine;

namespace SprocketLANMod.Sync;

[HarmonyPatch(typeof(AmmoRack), "OnHealthChanged")]
public static class AmmoRackOnHealthChangedPatch
{
	public static void Postfix(AmmoRack __instance)
	{
		//IL_0099: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a0: Expected Obj, but got Unknown
		if ((Object)(object)__instance == (Object)null || ((Il2CppObjectBase)__instance).Pointer == IntPtr.Zero)
		{
			return;
		}
		try
		{
			VehicleSync vs = MultiplayerController.Instance?.VehicleSync;
			if (vs == null || !((Object)(object)vs.LocalVehicle != (Object)null))
			{
				return;
			}
			VehicleBehaviour veh = ((Component)__instance).GetComponentInParent<VehicleBehaviour>();
			if (!((Object)(object)veh != (Object)null) || !(((Il2CppObjectBase)veh).Pointer == ((Il2CppObjectBase)vs.LocalVehicle).Pointer) || !(((VehicleComponent)__instance).HealthFraction <= 0.95f))
			{
				return;
			}
			Plugin instance = Plugin.Instance;
			if (instance != null)
			{
				ManualLogSource log = ((BasePlugin)instance).Log;
				if (log != null)
				{
					bool flag = default;
					BepInExInfoLogInterpolatedStringHandler val = new BepInExInfoLogInterpolatedStringHandler(88, 1, ref flag);
					if (flag)
					{
						((BepInExLogInterpolatedStringHandler)val).AppendLiteral("[LAN Mod] Ammo rack hit (health: ");
						((BepInExLogInterpolatedStringHandler)val).AppendFormatted<float>(((VehicleComponent)__instance).HealthFraction, "P0");
						((BepInExLogInterpolatedStringHandler)val).AppendLiteral(") - triggering instant detonation and tank destruction!");
					}
					log.LogInfo(val);
				}
			}
			try
			{
				AmmoRackBehaviour behaviour = __instance.behaviour;
				if (behaviour != null)
				{
					behaviour.Detonate();
				}
			}
			catch
			{
			}
			vs.TriggerLocalVehicleDestroyed("Ammunition Exploded");
		}
		catch
		{
		}
	}
}
