using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Sprocket.Vehicles.Weapons;

namespace SprocketLANMod.Sync;

[HarmonyPatch(typeof(TurretBehaviour), "ResetRotation")]
public static class TurretBehaviourResetRotationPatch
{
	public static bool Prefix(TurretBehaviour __instance)
	{
		if (__instance != null)
		{
			MultiplayerController instance = MultiplayerController.Instance;
			if (instance != null && instance.VehicleSync?.IsRemoteAimable(((Il2CppObjectBase)__instance).Pointer) == true)
			{
				return false;
			}
		}
		return true;
	}
}
