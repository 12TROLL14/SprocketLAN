using System;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Sprocket.Vehicles.Weapons;
using UnityEngine;

namespace SprocketLANMod.Sync;

[HarmonyPatch(typeof(ProjectileImpactMark), "EffectUpdate")]
public static class ProjectileImpactMarkEffectUpdatePatch
{
	public static bool Prefix(ProjectileImpactMark __instance)
	{
		try
		{
			if ((Object)(object)__instance == (Object)null || ((Il2CppObjectBase)__instance).Pointer == IntPtr.Zero || (Object)(object)((Component)__instance).gameObject == (Object)null || (Object)(object)((Component)__instance).transform == (Object)null)
			{
				return false;
			}
		}
		catch
		{
			return false;
		}
		return true;
	}

	public static Exception Finalizer(Exception __exception)
	{
		return null;
	}
}
