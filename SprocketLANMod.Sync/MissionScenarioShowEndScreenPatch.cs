using HarmonyLib;
using Il2CppSystem.Threading.Tasks;
using Sprocket;
using UnityEngine;

namespace SprocketLANMod.Sync;

[HarmonyPatch(typeof(MissionScenarioGameState), "ShowEndScreen")]
public static class MissionScenarioShowEndScreenPatch
{
	public static bool Prefix(ref Task __result)
	{
		MultiplayerController mc = MultiplayerController.Instance;
		if (mc != null && ((mc.Transport != null && mc.Transport.IsConnected) || (Object)(object)mc.VehicleSync?.RemoteVehicle != (Object)null))
		{
			__result = Task.CompletedTask;
			return false;
		}
		return true;
	}
}
