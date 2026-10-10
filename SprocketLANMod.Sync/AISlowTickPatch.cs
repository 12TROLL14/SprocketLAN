using HarmonyLib;
using Sprocket.ArtificialIntelligence;
using UnityEngine;

namespace SprocketLANMod.Sync;

[HarmonyPatch(typeof(AI), "SlowTick")]
public static class AISlowTickPatch
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
