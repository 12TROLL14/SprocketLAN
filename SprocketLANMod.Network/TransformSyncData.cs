using UnityEngine;

namespace SprocketLANMod.Network;

public struct TransformSyncData
{
	public uint Sequence;

	public float Timestamp;

	public Vector3 Position;

	public Quaternion Rotation;

	public Vector3 Velocity;

	public Vector3 AngularVelocity;

	public Vector3 AimPoint;

	public bool IsFiring;

	public float HealthFraction;

	public float Throttle;

	public float Steer;
}
