using HarmonyLib;
using Sprocket;
using UnityEngine;

namespace SprocketLANMod.Sync;

[HarmonyPatch(typeof(DeathmatchGameMode), "FailMission")]
public static class DeathmatchFailMissionPatch
{
	public static bool Prefix()
	{
		MultiplayerController mc = MultiplayerController.Instance;
		if (mc != null && ((mc.Transport != null && mc.Transport.IsConnected) || (Object)(object)mc.VehicleSync?.RemoteVehicle != (Object)null))
		{
			return false;
		}
		return true;
	}
}
