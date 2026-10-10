using UnityEngine;

namespace SprocketLANMod.Network;

public struct FireData
{
	public uint Sequence;

	public int WeaponIndex;

	public Vector3 MuzzlePosition;

	public Vector3 FireDirection;
}
