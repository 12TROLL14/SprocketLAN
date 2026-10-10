using HarmonyLib;
using Sprocket.MissionFramework;
using UnityEngine;

namespace SprocketLANMod.Sync;

[HarmonyPatch(typeof(ImmobilizeObjective), "UpdateObjective")]
public static class ImmobilizeObjectivePatch
{
	public static bool Prefix(ref ObjectiveResult __result)
	{
		MultiplayerController mc = MultiplayerController.Instance;
		if (mc != null && ((mc.Transport != null && mc.Transport.IsConnected) || (Object)(object)mc.VehicleSync?.RemoteVehicle != (Object)null))
		{
			__result = (ObjectiveResult)1;
			return false;
		}
		return true;
	}
}
