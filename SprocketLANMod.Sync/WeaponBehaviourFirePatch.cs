using System;
using BepInEx.Core.Logging.Interpolation;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Sprocket.WeaponFramework;

namespace SprocketLANMod.Sync;

[HarmonyPatch(typeof(WeaponBehaviour), "Fire")]
public static class WeaponBehaviourFirePatch
{
	public static bool Prefix(WeaponBehaviour __instance)
	{
		if (__instance == null || ((Il2CppObjectBase)__instance).Pointer == IntPtr.Zero)
		{
			return false;
		}
		return true;
	}

	public static void Postfix(WeaponBehaviour __instance)
	{
		if (__instance == null || ((Il2CppObjectBase)__instance).Pointer == IntPtr.Zero)
		{
			return;
		}
		try
		{
			VehicleSync vs = MultiplayerController.Instance?.VehicleSync;
			if (vs != null && !vs.IsTriggeringRemoteFiring)
			{
				vs.OnLocalWeaponFired(__instance);
			}
		}
		catch
		{
		}
	}

	public static Exception Finalizer(Exception __exception)
	{
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Expected Obj, but got Unknown
		if (__exception != null)
		{
			Plugin instance = Plugin.Instance;
			if (instance != null)
			{
				ManualLogSource log = ((BasePlugin)instance).Log;
				if (log != null)
				{
					bool flag = default;
					BepInExWarningLogInterpolatedStringHandler val = new BepInExWarningLogInterpolatedStringHandler(43, 1, ref flag);
					if (flag)
					{
						((BepInExLogInterpolatedStringHandler)val).AppendLiteral("WeaponBehaviour.Fire exception suppressed: ");
						((BepInExLogInterpolatedStringHandler)val).AppendFormatted<string>(__exception.Message);
					}
					log.LogWarning(val);
				}
			}
			return null;
		}
		return null;
	}
}
