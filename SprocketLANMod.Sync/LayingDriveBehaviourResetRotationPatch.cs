using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Sprocket.Vehicles.Weapons;

namespace SprocketLANMod.Sync;

[HarmonyPatch(typeof(LayingDriveBehaviour), "ResetRotation")]
public static class LayingDriveBehaviourResetRotationPatch
{
	public static bool Prefix(LayingDriveBehaviour __instance)
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
