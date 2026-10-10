using System;
using BepInEx.Core.Logging.Interpolation;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Sprocket.WeaponFramework;

namespace SprocketLANMod.Sync;

[HarmonyPatch(typeof(WeaponBehaviour), "FireInternal")]
public static class WeaponBehaviourFireInternalPatch
{
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
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_003e: Expected Obj, but got Unknown
		if (__exception != null)
		{
			VehicleSync vs = MultiplayerController.Instance?.VehicleSync;
			if (vs != null && vs.IsTriggeringRemoteFiring)
			{
				Plugin instance = Plugin.Instance;
				if (instance != null)
				{
					ManualLogSource log = ((BasePlugin)instance).Log;
					if (log != null)
					{
						bool flag = default;
						BepInExWarningLogInterpolatedStringHandler val = new BepInExWarningLogInterpolatedStringHandler(58, 1, ref flag);
						if (flag)
						{
							((BepInExLogInterpolatedStringHandler)val).AppendLiteral("WeaponBehaviour.FireInternal remote exception suppressed: ");
							((BepInExLogInterpolatedStringHandler)val).AppendFormatted<string>(__exception.Message);
						}
						log.LogWarning(val);
					}
				}
				return null;
			}
		}
		return __exception;
	}
}
