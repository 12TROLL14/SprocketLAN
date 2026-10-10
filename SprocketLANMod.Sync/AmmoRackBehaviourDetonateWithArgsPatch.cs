using System;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.Vehicles.AmmunitionStorage;
using UnityEngine;

namespace SprocketLANMod.Sync;

[HarmonyPatch(typeof(AmmoRackBehaviour), "Detonate", new Type[]
{
	typeof(float),
	typeof(float)
})]
public static class AmmoRackBehaviourDetonateWithArgsPatch
{
	public static bool Prefix(AmmoRackBehaviour __instance)
	{
		if (__instance == null || ((Il2CppObjectBase)__instance).Pointer == IntPtr.Zero)
		{
			return true;
		}
		try
		{
			VehicleSync vs = MultiplayerController.Instance?.VehicleSync;
			if (vs != null && (Object)(object)vs.LocalVehicle != (Object)null && ((Il2CppObjectBase)vs.LocalVehicle).Pointer != IntPtr.Zero)
			{
				Il2CppArrayBase<AmmoRack> racks = ((Component)vs.LocalVehicle).GetComponentsInChildren<AmmoRack>(true);
				if (racks != null)
				{
					foreach (AmmoRack r in racks)
					{
						if (!((Object)(object)r != (Object)null) || !(((Il2CppObjectBase)r).Pointer != IntPtr.Zero) || r.behaviour == null || !(((Il2CppObjectBase)r.behaviour).Pointer == ((Il2CppObjectBase)__instance).Pointer))
						{
							continue;
						}
						Plugin instance = Plugin.Instance;
						if (instance != null)
						{
							ManualLogSource log = ((BasePlugin)instance).Log;
							if (log != null)
							{
								log.LogInfo((object)"[LAN Mod] Local ammo rack detonated (with args) - instantly destroying tank!");
							}
						}
						vs.TriggerLocalVehicleDestroyed("Ammunition Detonated");
						break;
					}
				}
			}
		}
		catch
		{
		}
		return true;
	}
}
