using System;
using System.Collections.Generic;
using System.Net;
using BepInEx.Core.Logging.Interpolation;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSystem;
using Il2CppSystem.Collections.Generic;
using Il2CppSystem.Threading;
using Sprocket;
using Sprocket.ArtificialIntelligence;
using Sprocket.DamageModelling;
using Sprocket.GameControl;
using Sprocket.Gameplay.VehicleControl;
using Sprocket.Vehicles;
using Sprocket.Vehicles.AmmunitionStorage;
using Sprocket.Vehicles.Cannons;
using Sprocket.Vehicles.Control;
using Sprocket.Vehicles.CrewSystems;
using Sprocket.Vehicles.Fires;
using Sprocket.Vehicles.FuelTanks;
using Sprocket.Vehicles.PartDecoupling;
using Sprocket.Vehicles.Powertrains;
using Sprocket.Vehicles.Tracks;
using Sprocket.Vehicles.Turrets;
using Sprocket.Vehicles.Weapons;
using Sprocket.Vehicles.Weapons.Cannons;
using Sprocket.WeaponFramework;
using SprocketLANMod.Network;
using SprocketLANMod.UI;
using UnityEngine;
using UnityEngine.VFX;

namespace SprocketLANMod.Sync;

public class VehicleSync
{
	private Rigidbody _remoteRigidbody;

	private readonly List<Cannon> _localCannons = new List<Cannon>();

	private readonly List<TraverseMotor> _localTraverseMotors = new List<TraverseMotor>();

	private readonly List<LayingDrive> _localLayingDrives = new List<LayingDrive>();

	private readonly List<float> _localCannonsLastFireTime = new List<float>();

	private readonly List<Cannon> _remoteCannons = new List<Cannon>();

	private readonly List<TraverseMotor> _remoteTraverseMotors = new List<TraverseMotor>();

	private readonly List<LayingDrive> _remoteLayingDrives = new List<LayingDrive>();

	private readonly HashSet<IntPtr> _remoteAimablePointers = new HashSet<IntPtr>();

	private Vector3 _targetPosition;

	private Quaternion _targetRotation;

	private Vector3 _targetVelocity;

	private Vector3 _targetAngularVelocity;

	private Vector3 _targetAimPoint;

	private readonly List<Quaternion> _targetTurretRotations = new List<Quaternion>();

	private readonly List<Quaternion> _targetCannonRotations = new List<Quaternion>();

	private readonly Dictionary<int, float> _remoteCannonsLastFireTime = new Dictionary<int, float>();

	private readonly Dictionary<int, float> _localCannonsLastSendPacketTime = new Dictionary<int, float>();

	private readonly List<int> _localCannonProjectileIDs = new List<int>();

	private readonly List<ProjectileTypeID> _remoteCannonProjectileIDs = new List<ProjectileTypeID>();

	private bool _hasReceivedState;

	private float _lastSendTime;

	private float _lastVehicleSearchTime;

	private uint _localSequence;

	private bool _lastLocalFiringState;

	private bool _localWasDestroyed;

	private bool _remoteWasDestroyed;

	private float _localVehicleSpawnTime;

	private float _lastResetTime = -999f;

	private bool _localDestructionHandled;

	private bool _remoteDestructionHandled;

	private float _lastRespawnTime;

	public VehicleBehaviour LocalVehicle { get; private set; }

	public VehicleController LocalController { get; private set; }

	public VehicleBehaviour RemoteVehicle { get; private set; }

	public VehicleController RemoteController { get; private set; }

	public float SendRateHz { get; set; } = 30f;

	public bool SmoothInterpolation { get; set; } = true;

	public float InterpolationSpeed { get; set; } = 15f;

	public bool DisableRemoteAI { get; set; } = true;

	public string SyncStatus { get; private set; } = "Initializing";

	public float RemoteHealthFraction { get; private set; } = 1f;

	public float LocalHealthFraction { get; private set; } = 1f;

	public float DistanceBetweenTanks { get; private set; }

	public int LocalTurretCount => _localTraverseMotors.Count;

	public int LocalCannonCount => _localCannons.Count;

	public int RemoteTurretCount => _remoteTraverseMotors.Count;

	public int RemoteCannonCount => _remoteCannons.Count;

	public string LocalVehicleName
	{
		get
		{
			try
			{
				if ((Object)(object)LocalVehicle != (Object)null && ((Il2CppObjectBase)LocalVehicle).Pointer != IntPtr.Zero && (Object)(object)((Component)LocalVehicle).gameObject != (Object)null && ((Il2CppObjectBase)((Component)LocalVehicle).gameObject).Pointer != IntPtr.Zero)
				{
					return ((Object)((Component)LocalVehicle).gameObject).name;
				}
			}
			catch
			{
			}
			return "None";
		}
	}

	public string RemoteVehicleName
	{
		get
		{
			try
			{
				if ((Object)(object)RemoteVehicle != (Object)null && ((Il2CppObjectBase)RemoteVehicle).Pointer != IntPtr.Zero && (Object)(object)((Component)RemoteVehicle).gameObject != (Object)null && ((Il2CppObjectBase)((Component)RemoteVehicle).gameObject).Pointer != IntPtr.Zero)
				{
					return ((Object)((Component)RemoteVehicle).gameObject).name;
				}
			}
			catch
			{
			}
			return "None";
		}
	}

	public float LocalTurretYaw { get; private set; }

	public float LocalGunPitch { get; private set; }

	public float RemoteTurretYaw { get; private set; }

	public float RemoteGunPitch { get; private set; }

	public bool LocalWeaponReady { get; private set; } = true;

	public bool RemoteWeaponReady { get; private set; } = true;

	public bool IsTriggeringRemoteFiring { get; private set; }

	public bool IsResetting { get; private set; }

	public bool IsResettingFromPeer { get; set; }

	public bool IsRemoteAimable(IntPtr ptr)
	{
		if (ptr == IntPtr.Zero)
		{
			return false;
		}
		return _remoteAimablePointers.Contains(ptr);
	}

	public static bool IsValid(Object obj)
	{
		if (obj == (Object)null)
		{
			return false;
		}
		if (((Il2CppObjectBase)obj).Pointer == IntPtr.Zero)
		{
			return false;
		}
		return true;
	}

	public static bool IsValid(Component comp)
	{
		if ((Object)(object)comp == (Object)null)
		{
			return false;
		}
		if (((Il2CppObjectBase)comp).Pointer == IntPtr.Zero)
		{
			return false;
		}
		try
		{
			return (Object)(object)comp.gameObject != (Object)null && ((Il2CppObjectBase)comp.gameObject).Pointer != IntPtr.Zero && (Object)(object)comp.transform != (Object)null;
		}
		catch
		{
			return false;
		}
	}

	public static bool IsValid(GameObject go)
	{
		if ((Object)(object)go == (Object)null)
		{
			return false;
		}
		if (((Il2CppObjectBase)go).Pointer == IntPtr.Zero)
		{
			return false;
		}
		try
		{
			return (Object)(object)go.transform != (Object)null;
		}
		catch
		{
			return false;
		}
	}

	public void ClearCachedComponents()
	{
		_localCannons.Clear();
		_localTraverseMotors.Clear();
		_localLayingDrives.Clear();
		_localCannonsLastFireTime.Clear();
		_localCannonsLastSendPacketTime.Clear();
		_localCannonProjectileIDs.Clear();
		_remoteCannons.Clear();
		_remoteTraverseMotors.Clear();
		_remoteLayingDrives.Clear();
		_remoteAimablePointers.Clear();
		_remoteCannonsLastFireTime.Clear();
		_remoteCannonProjectileIDs.Clear();
	}

	public void ClearVehicles()
	{
		LocalVehicle = null;
		RemoteVehicle = null;
		LocalController = null;
		RemoteController = null;
		_remoteRigidbody = null;
		_localWasDestroyed = false;
		_remoteWasDestroyed = false;
		_localDestructionHandled = false;
		_remoteDestructionHandled = false;
		_localVehicleSpawnTime = Time.time;
		ClearCachedComponents();
	}

	public void Initialize(UdpNetworkTransport transport)
	{
		if (transport != null)
		{
			transport.PacketReceived += OnPacketReceived;
		}
		SyncStatus = "Sync Ready";
	}

	public void Dispose(UdpNetworkTransport transport)
	{
		if (transport != null)
		{
			transport.PacketReceived -= OnPacketReceived;
		}
	}

	public void ResetSession()
	{
		_localWasDestroyed = false;
		_remoteWasDestroyed = false;
		_localDestructionHandled = false;
		_remoteDestructionHandled = false;
		_localVehicleSpawnTime = Time.time;
		_hasReceivedState = false;
		_remoteCannonsLastFireTime.Clear();
		_remoteCannonProjectileIDs.Clear();
		_localCannonsLastSendPacketTime.Clear();
		_localCannonProjectileIDs.Clear();
		if ((Object)(object)RemoteVehicle != (Object)null && ((Il2CppObjectBase)RemoteVehicle).Pointer != IntPtr.Zero)
		{
			RepairAndRestoreVehicle(RemoteVehicle);
			CacheRemoteVehicleComponents();
			NeutralizeAIOnRemoteVehicle(RemoteVehicle);
		}
	}

	public void Update(UdpNetworkTransport transport)
	{
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0043: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Invalid comparison between Unknown and I4
		//IL_006f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0076: Unknown result type (might be due to invalid IL or missing references)
		//IL_01cc: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d3: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			if (IsResetting && Time.time - _lastResetTime < 1f)
			{
				return;
			}
			FindVehiclesIfNeeded();
			if (IsValid((Component)(object)LocalVehicle))
			{
				bool localHasOnFire = (LocalVehicle.Flags & 0x40) > 0;
				if (!localHasOnFire)
				{
					try
					{
						FireControlVehicleModule fm = default;
						if (LocalVehicle.TryGetModule<FireControlVehicleModule>(ref fm) && fm != null && fm.OnFire)
						{
							VehicleBehaviour localVehicle = LocalVehicle;
							localVehicle.Flags = (VehicleFlags)(localVehicle.Flags | 0x40);
							localHasOnFire = true;
						}
					}
					catch
					{
					}
					if (!localHasOnFire)
					{
						Il2CppArrayBase<FuelTank> fuelTanks = ((Component)LocalVehicle).GetComponentsInChildren<FuelTank>(true);
						if (fuelTanks != null)
						{
							foreach (FuelTank ft in fuelTanks)
							{
								if ((Object)(object)ft != (Object)null && ((Il2CppObjectBase)ft).Pointer != IntPtr.Zero && ft.behaviour != null && ft.behaviour.fireStarted)
								{
									ft.behaviour.fireStarted = false;
								}
							}
						}
					}
				}
				else
				{
					try
					{
						FireControlVehicleModule fm2 = default;
						if (LocalVehicle.TryGetModule<FireControlVehicleModule>(ref fm2) && fm2 != null && !fm2.OnFire)
						{
							fm2.StartFire(45f);
						}
					}
					catch
					{
					}
					Il2CppArrayBase<FuelTank> fuelTanks2 = ((Component)LocalVehicle).GetComponentsInChildren<FuelTank>(true);
					if (fuelTanks2 != null)
					{
						foreach (FuelTank ft2 in fuelTanks2)
						{
							if (!((Object)(object)ft2 != (Object)null) || !(((Il2CppObjectBase)ft2).Pointer != IntPtr.Zero) || ft2.behaviour == null || !(((VehicleComponent)ft2).HealthFraction <= 0.2f) || ft2.behaviour.fireStarted)
							{
								continue;
							}
							try
							{
								ft2.behaviour.StartFire();
								ft2.behaviour.fireStarted = true;
								VehicleBehaviour localVehicle2 = LocalVehicle;
								localVehicle2.Flags = (VehicleFlags)(localVehicle2.Flags | 0x40);
								Plugin instance = Plugin.Instance;
								if (instance != null)
								{
									ManualLogSource log = ((BasePlugin)instance).Log;
									if (log != null)
									{
										log.LogInfo((object)"[LAN Mod] Fuel tank critically damaged - fire ignited!");
									}
								}
							}
							catch
							{
							}
						}
					}
				}
				bool localDead = IsVehicleDestroyed(LocalVehicle);
				if (localDead && !_localDestructionHandled)
				{
					OnVehicleDestroyed(LocalVehicle, isLocal: true, transport);
				}
				else if (!localDead && _localWasDestroyed)
				{
					_localWasDestroyed = false;
					_localDestructionHandled = false;
				}
			}
			if (IsValid((Component)(object)RemoteVehicle))
			{
				IReadOnlyList<AI> registeredAIs = AIRegister.RegisteredAIs;
				List<AI> registered = ((registeredAIs != null) ? ((Il2CppObjectBase)registeredAIs).TryCast<List<AI>>() : null);
				if (registered != null && registered.Count > 0)
				{
					NeutralizeAIOnRemoteVehicle(RemoteVehicle);
				}
				bool remoteDead = IsVehicleDestroyed(RemoteVehicle);
				if (remoteDead && !_remoteDestructionHandled)
				{
					OnVehicleDestroyed(RemoteVehicle, isLocal: false, transport);
				}
				else if (!remoteDead && _remoteWasDestroyed)
				{
					_remoteWasDestroyed = false;
					_remoteDestructionHandled = false;
				}
			}
			if (transport == null || !transport.IsConnected)
			{
				SyncStatus = "Waiting for peer connection";
				UpdateVehicleStats();
				return;
			}
			float sendInterval = 1f / Mathf.Clamp(SendRateHz, 10f, 60f);
			if (Time.time - _lastSendTime >= sendInterval)
			{
				_lastSendTime = Time.time;
				SendLocalState(transport);
			}
			CheckLocalFiring(transport);
			if (IsValid((Component)(object)RemoteVehicle) && _hasReceivedState)
			{
				ApplyRemoteAimingMechanisms(Time.deltaTime);
			}
			UpdateVehicleStats();
		}
		catch
		{
		}
	}

	public void FixedUpdate()
	{
		try
		{
			if (IsValid((Component)(object)RemoteVehicle) && _hasReceivedState && (Object)(object)_remoteRigidbody != (Object)null)
			{
				ApplyRemoteState();
			}
		}
		catch
		{
		}
	}

	public void FindVehiclesIfNeeded()
	{
		//IL_0120: Unknown result type (might be due to invalid IL or missing references)
		//IL_012a: Unknown result type (might be due to invalid IL or missing references)
		//IL_029c: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a3: Expected Obj, but got Unknown
		//IL_024d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0257: Unknown result type (might be due to invalid IL or missing references)
		//IL_034f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0356: Expected Obj, but got Unknown
		if (Time.time - _lastVehicleSearchTime < 0.5f)
		{
			return;
		}
		_lastVehicleSearchTime = Time.time;
		bool flag = default;
		if ((Object)(object)LocalController == (Object)null || (Object)(object)LocalVehicle == (Object)null || !IsValid((Component)(object)LocalVehicle))
		{
			foreach (VehicleController c in Object.FindObjectsOfType<VehicleController>())
			{
				if (!((Object)(object)c != (Object)(object)RemoteController) || c.ControlledVehicle == null || !(((Il2CppObjectBase)c.ControlledVehicle).Pointer != IntPtr.Zero))
				{
					continue;
				}
				VehicleBehaviour veh = ((Il2CppObjectBase)c.ControlledVehicle).TryCast<VehicleBehaviour>();
				if (!((Object)(object)veh != (Object)null) || !(((Il2CppObjectBase)veh).Pointer != IntPtr.Zero))
				{
					continue;
				}
				bool validGo = false;
				try
				{
					validGo = (Object)(object)((Component)veh).gameObject != (Object)null && ((Il2CppObjectBase)((Component)veh).gameObject).Pointer != IntPtr.Zero;
				}
				catch
				{
				}
				if (!validGo)
				{
					continue;
				}
				LocalController = c;
				LocalVehicle = veh;
				_localVehicleSpawnTime = Time.time;
				_localWasDestroyed = false;
				try
				{
					VehicleBehaviour localVehicle = LocalVehicle;
					localVehicle.Flags = (VehicleFlags)(localVehicle.Flags | 0x129);
				}
				catch
				{
				}
				CacheLocalVehicleComponents();
				if (!IsResetting)
				{
					break;
				}
				IsResetting = false;
				IsResettingFromPeer = false;
				_localDestructionHandled = false;
				_remoteDestructionHandled = false;
				SendRespawnPacket(MultiplayerController.Instance?.Transport);
				Plugin instance = Plugin.Instance;
				if (instance != null)
				{
					ManualLogSource log = ((BasePlugin)instance).Log;
					if (log != null)
					{
						log.LogInfo((object)"[LAN Mod] Scene reset complete - local vehicle found & respawn packet dispatched!");
					}
				}
				break;
			}
		}
		else if ((Object)(object)LocalController != (Object)null && LocalController.ControlledVehicle != null)
		{
			VehicleBehaviour currentControlled = ((Il2CppObjectBase)LocalController.ControlledVehicle).TryCast<VehicleBehaviour>();
			if ((Object)(object)currentControlled != (Object)null && ((Il2CppObjectBase)currentControlled).Pointer != ((Il2CppObjectBase)LocalVehicle).Pointer)
			{
				LocalVehicle = currentControlled;
				_localVehicleSpawnTime = Time.time;
				_localWasDestroyed = false;
				_localDestructionHandled = false;
				IsResetting = false;
				IsResettingFromPeer = false;
				try
				{
					VehicleBehaviour localVehicle2 = LocalVehicle;
					localVehicle2.Flags = (VehicleFlags)(localVehicle2.Flags | 0x129);
				}
				catch
				{
				}
				CacheLocalVehicleComponents();
				SendRespawnPacket(MultiplayerController.Instance?.Transport);
				Plugin instance2 = Plugin.Instance;
				if (instance2 != null)
				{
					ManualLogSource log2 = ((BasePlugin)instance2).Log;
					if (log2 != null)
					{
						BepInExInfoLogInterpolatedStringHandler val = new BepInExInfoLogInterpolatedStringHandler(49, 1, ref flag);
						if (flag)
						{
							((BepInExLogInterpolatedStringHandler)val).AppendLiteral("[LAN Mod] Local vehicle switch/respawn detected: ");
							BepInExInfoLogInterpolatedStringHandler val2 = val;
							GameObject gameObject = ((Component)LocalVehicle).gameObject;
							((BepInExLogInterpolatedStringHandler)val2).AppendFormatted<string>((gameObject != null) ? ((Object)gameObject).name : null);
						}
						log2.LogInfo(val);
					}
				}
			}
		}
		if (IsValid((Component)(object)LocalVehicle) && _localWasDestroyed && !IsVehicleDestroyed(LocalVehicle))
		{
			_localWasDestroyed = false;
			_localCannonsLastFireTime.Clear();
			CacheLocalVehicleComponents();
			SendRespawnPacket(MultiplayerController.Instance?.Transport);
			Plugin instance2 = Plugin.Instance;
			if (instance2 != null)
			{
				ManualLogSource log2 = ((BasePlugin)instance2).Log;
				if (log2 != null)
				{
					BepInExInfoLogInterpolatedStringHandler val = new BepInExInfoLogInterpolatedStringHandler(55, 1, ref flag);
					if (flag)
					{
						((BepInExLogInterpolatedStringHandler)val).AppendLiteral("[LAN Mod] Local vehicle restored from destroyed state: ");
						((BepInExLogInterpolatedStringHandler)val).AppendFormatted<string>(LocalVehicleName);
					}
					log2.LogInfo(val);
				}
			}
		}
		if ((Object)(object)LocalVehicle != (Object)null && ((Object)(object)RemoteVehicle == (Object)null || ((Il2CppObjectBase)RemoteVehicle).Pointer == ((Il2CppObjectBase)LocalVehicle).Pointer || !IsValid((Component)(object)RemoteVehicle)))
		{
			foreach (VehicleBehaviour v in Object.FindObjectsOfType<VehicleBehaviour>())
			{
				if (((Il2CppObjectBase)v).Pointer != ((Il2CppObjectBase)LocalVehicle).Pointer)
				{
					bool validGo2 = false;
					try
					{
						validGo2 = (Object)(object)((Component)v).gameObject != (Object)null && ((Il2CppObjectBase)((Component)v).gameObject).Pointer != IntPtr.Zero;
					}
					catch
					{
					}
					if (validGo2)
					{
						SetRemoteVehicle(v);
						break;
					}
				}
			}
		}
		if (IsValid((Component)(object)RemoteVehicle) && (_remoteCannons.Count == 0 || _remoteTraverseMotors.Count == 0))
		{
			Il2CppArrayBase<TraverseMotor> motors = ((Component)RemoteVehicle).GetComponentsInChildren<TraverseMotor>(true);
			Il2CppArrayBase<Cannon> cannons = ((Component)RemoteVehicle).GetComponentsInChildren<Cannon>(true);
			if ((motors != null && motors.Length > 0) || (cannons != null && cannons.Length > 0))
			{
				Plugin instance3 = Plugin.Instance;
				if (instance3 != null)
				{
					ManualLogSource log3 = ((BasePlugin)instance3).Log;
					if (log3 != null)
					{
						log3.LogInfo((object)"[LAN Mod] Async tank parts loaded on remote vehicle. Re-caching components and neutralizing AI.");
					}
				}
				RepairAndRestoreVehicle(RemoteVehicle);
				CacheRemoteVehicleComponents();
				NeutralizeAIOnRemoteVehicle(RemoteVehicle);
			}
		}
		if (IsValid((Component)(object)LocalVehicle) && (_localCannons.Count == 0 || _localTraverseMotors.Count == 0))
		{
			Il2CppArrayBase<TraverseMotor> motors2 = ((Component)LocalVehicle).GetComponentsInChildren<TraverseMotor>(true);
			Il2CppArrayBase<Cannon> cannons2 = ((Component)LocalVehicle).GetComponentsInChildren<Cannon>(true);
			if ((motors2 != null && motors2.Length > 0) || (cannons2 != null && cannons2.Length > 0))
			{
				CacheLocalVehicleComponents();
			}
		}
	}

	private void CacheLocalVehicleComponents()
	{
		//IL_00f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_010a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0181: Unknown result type (might be due to invalid IL or missing references)
		//IL_0194: Unknown result type (might be due to invalid IL or missing references)
		//IL_0298: Unknown result type (might be due to invalid IL or missing references)
		//IL_029f: Expected Obj, but got Unknown
		_localCannons.Clear();
		_localTraverseMotors.Clear();
		_localLayingDrives.Clear();
		_localCannonsLastFireTime.Clear();
		_localCannonsLastSendPacketTime.Clear();
		_localCannonProjectileIDs.Clear();
		if ((Object)(object)LocalVehicle == (Object)null)
		{
			return;
		}
		try
		{
			foreach (Cannon c in ((Component)LocalVehicle).GetComponentsInChildren<Cannon>(true))
			{
				if (!((Object)(object)c != (Object)null))
				{
					continue;
				}
				_localCannons.Add(c);
				_localCannonsLastFireTime.Add((c.Weapon != null) ? c.Weapon.LastFireTime : 0f);
				int localProjId = 0;
				if (c.Weapon != null && ((Il2CppObjectBase)c.Weapon).Pointer != IntPtr.Zero)
				{
					try
					{
						c.Weapon.Functioning = true;
					}
					catch
					{
					}
					try
					{
						c.Weapon.NeedsReload = false;
					}
					catch
					{
					}
					try
					{
						if (c.Weapon.LoadedProjectileTypeID.Value != 0)
						{
							localProjId = c.Weapon.LoadedProjectileTypeID.Value;
						}
					}
					catch
					{
					}
				}
				if (localProjId == 0 && (Object)(object)LocalVehicle != (Object)null)
				{
					try
					{
						Il2CppArrayBase<AmmoRack> racks = ((Component)LocalVehicle).GetComponentsInChildren<AmmoRack>(true);
						if (racks != null)
						{
							foreach (AmmoRack r in racks)
							{
								if ((Object)(object)r != (Object)null && ((Il2CppObjectBase)r).Pointer != IntPtr.Zero && r.behaviour != null && r.behaviour.StoredTypeID.Value != 0)
								{
									localProjId = r.behaviour.StoredTypeID.Value;
									break;
								}
							}
						}
					}
					catch
					{
					}
				}
				_localCannonProjectileIDs.Add(localProjId);
			}
			foreach (TraverseMotor m in ((Component)LocalVehicle).GetComponentsInChildren<TraverseMotor>(true))
			{
				if ((Object)(object)m != (Object)null)
				{
					_localTraverseMotors.Add(m);
				}
			}
			foreach (LayingDrive d in ((Component)LocalVehicle).GetComponentsInChildren<LayingDrive>(true))
			{
				if ((Object)(object)d != (Object)null)
				{
					_localLayingDrives.Add(d);
				}
			}
			Plugin instance = Plugin.Instance;
			if (instance == null)
			{
				return;
			}
			ManualLogSource log = ((BasePlugin)instance).Log;
			if (log != null)
			{
				bool flag = default;
				BepInExInfoLogInterpolatedStringHandler val = new BepInExInfoLogInterpolatedStringHandler(59, 3, ref flag);
				if (flag)
				{
					((BepInExLogInterpolatedStringHandler)val).AppendLiteral("[LAN Mod] Cached Local Tank: ");
					((BepInExLogInterpolatedStringHandler)val).AppendFormatted<int>(_localTraverseMotors.Count);
					((BepInExLogInterpolatedStringHandler)val).AppendLiteral(" Turrets, ");
					((BepInExLogInterpolatedStringHandler)val).AppendFormatted<int>(_localLayingDrives.Count);
					((BepInExLogInterpolatedStringHandler)val).AppendLiteral(" Mantlets, ");
					((BepInExLogInterpolatedStringHandler)val).AppendFormatted<int>(_localCannons.Count);
					((BepInExLogInterpolatedStringHandler)val).AppendLiteral(" Cannons.");
				}
				log.LogInfo(val);
			}
		}
		catch (Exception ex)
		{
			Plugin instance2 = Plugin.Instance;
			if (instance2 != null)
			{
				ManualLogSource log2 = ((BasePlugin)instance2).Log;
				if (log2 != null)
				{
					log2.LogWarning((object)("Error caching local components: " + ex.Message));
				}
			}
		}
	}

	private void CacheRemoteVehicleComponents()
	{
		//IL_00e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0192: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00de: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_007d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0082: Unknown result type (might be due to invalid IL or missing references)
		//IL_0155: Unknown result type (might be due to invalid IL or missing references)
		//IL_0168: Unknown result type (might be due to invalid IL or missing references)
		//IL_016d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0348: Unknown result type (might be due to invalid IL or missing references)
		//IL_034f: Expected Obj, but got Unknown
		_remoteCannons.Clear();
		_remoteTraverseMotors.Clear();
		_remoteLayingDrives.Clear();
		_remoteAimablePointers.Clear();
		_remoteCannonProjectileIDs.Clear();
		if ((Object)(object)RemoteVehicle == (Object)null)
		{
			return;
		}
		try
		{
			foreach (Cannon c in ((Component)RemoteVehicle).GetComponentsInChildren<Cannon>(true))
			{
				if (!((Object)(object)c != (Object)null))
				{
					continue;
				}
				_remoteCannons.Add(c);
				ProjectileTypeID nativeProjId = ProjectileTypeID.Invalid;
				if (c.Weapon != null && ((Il2CppObjectBase)c.Weapon).Pointer != IntPtr.Zero)
				{
					try
					{
						c.Weapon.Functioning = true;
					}
					catch
					{
					}
					try
					{
						c.Weapon.NeedsReload = false;
					}
					catch
					{
					}
					try
					{
						if (c.Weapon.LoadedProjectileTypeID.Value != 0)
						{
							nativeProjId = c.Weapon.LoadedProjectileTypeID;
						}
					}
					catch
					{
					}
				}
				if (nativeProjId.Value == 0 && (Object)(object)RemoteVehicle != (Object)null)
				{
					try
					{
						Il2CppArrayBase<AmmoRack> racks = ((Component)RemoteVehicle).GetComponentsInChildren<AmmoRack>(true);
						if (racks != null)
						{
							foreach (AmmoRack r in racks)
							{
								if ((Object)(object)r != (Object)null && ((Il2CppObjectBase)r).Pointer != IntPtr.Zero && r.behaviour != null && r.behaviour.StoredTypeID.Value != 0)
								{
									nativeProjId = r.behaviour.StoredTypeID;
									break;
								}
							}
						}
					}
					catch
					{
					}
				}
				_remoteCannonProjectileIDs.Add(nativeProjId);
				if (c.HorizontalAimingMechanism != null && ((Il2CppObjectBase)c.HorizontalAimingMechanism).Pointer != IntPtr.Zero)
				{
					_remoteAimablePointers.Add(((Il2CppObjectBase)c.HorizontalAimingMechanism).Pointer);
				}
				if (c.VerticalAimingMechanism != null && ((Il2CppObjectBase)c.VerticalAimingMechanism).Pointer != IntPtr.Zero)
				{
					_remoteAimablePointers.Add(((Il2CppObjectBase)c.VerticalAimingMechanism).Pointer);
				}
			}
			foreach (TraverseMotor m in ((Component)RemoteVehicle).GetComponentsInChildren<TraverseMotor>(true))
			{
				if ((Object)(object)m != (Object)null)
				{
					_remoteTraverseMotors.Add(m);
					if (m.Behaviour != null && ((Il2CppObjectBase)m.Behaviour).Pointer != IntPtr.Zero)
					{
						_remoteAimablePointers.Add(((Il2CppObjectBase)m.Behaviour).Pointer);
					}
				}
			}
			foreach (LayingDrive d in ((Component)RemoteVehicle).GetComponentsInChildren<LayingDrive>(true))
			{
				if ((Object)(object)d != (Object)null)
				{
					_remoteLayingDrives.Add(d);
					if (d.Behaviour != null && ((Il2CppObjectBase)d.Behaviour).Pointer != IntPtr.Zero)
					{
						_remoteAimablePointers.Add(((Il2CppObjectBase)d.Behaviour).Pointer);
					}
				}
			}
			Plugin instance = Plugin.Instance;
			if (instance == null)
			{
				return;
			}
			ManualLogSource log = ((BasePlugin)instance).Log;
			if (log != null)
			{
				bool flag = default;
				BepInExInfoLogInterpolatedStringHandler val = new BepInExInfoLogInterpolatedStringHandler(104, 4, ref flag);
				if (flag)
				{
					((BepInExLogInterpolatedStringHandler)val).AppendLiteral("[LAN Mod] Cached Remote Tank: ");
					((BepInExLogInterpolatedStringHandler)val).AppendFormatted<int>(_remoteTraverseMotors.Count);
					((BepInExLogInterpolatedStringHandler)val).AppendLiteral(" Turrets, ");
					((BepInExLogInterpolatedStringHandler)val).AppendFormatted<int>(_remoteLayingDrives.Count);
					((BepInExLogInterpolatedStringHandler)val).AppendLiteral(" Mantlets, ");
					((BepInExLogInterpolatedStringHandler)val).AppendFormatted<int>(_remoteCannons.Count);
					((BepInExLogInterpolatedStringHandler)val).AppendLiteral(" Cannons. Registered ");
					((BepInExLogInterpolatedStringHandler)val).AppendFormatted<int>(_remoteAimablePointers.Count);
					((BepInExLogInterpolatedStringHandler)val).AppendLiteral(" aimables for motor suppression.");
				}
				log.LogInfo(val);
			}
		}
		catch (Exception ex)
		{
			Plugin instance2 = Plugin.Instance;
			if (instance2 != null)
			{
				ManualLogSource log2 = ((BasePlugin)instance2).Log;
				if (log2 != null)
				{
					log2.LogWarning((object)("Error caching remote components: " + ex.Message));
				}
			}
		}
	}

	public void SetRemoteVehicle(VehicleBehaviour vehicle)
	{
		//IL_0065: Unknown result type (might be due to invalid IL or missing references)
		//IL_0058: Unknown result type (might be due to invalid IL or missing references)
		//IL_006a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0096: Unknown result type (might be due to invalid IL or missing references)
		//IL_0089: Unknown result type (might be due to invalid IL or missing references)
		//IL_009b: Unknown result type (might be due to invalid IL or missing references)
		RemoteVehicle = vehicle;
		if ((Object)(object)RemoteVehicle != (Object)null && ((Il2CppObjectBase)RemoteVehicle).Pointer != IntPtr.Zero)
		{
			_remoteRigidbody = RemoteVehicle.Rigidbody;
			_targetPosition = (((Object)(object)_remoteRigidbody != (Object)null) ? _remoteRigidbody.position : RemoteVehicle.Position);
			_targetRotation = (((Object)(object)_remoteRigidbody != (Object)null) ? _remoteRigidbody.rotation : ((Component)RemoteVehicle).transform.rotation);
			_targetTurretRotations.Clear();
			_targetCannonRotations.Clear();
			_remoteWasDestroyed = false;
			RepairAndRestoreVehicle(RemoteVehicle);
			CacheRemoteVehicleComponents();
			NeutralizeAIOnRemoteVehicle(RemoteVehicle);
			try
			{
				SyncStatus = "Remote Tank Linked: " + ((Object)((Component)RemoteVehicle).gameObject).name;
				return;
			}
			catch
			{
				SyncStatus = "Remote Tank Linked";
				return;
			}
		}
		_remoteAimablePointers.Clear();
	}

	public void NeutralizeAIOnRemoteVehicle(VehicleBehaviour vehicle)
	{
		//IL_00d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d8: Expected Obj, but got Unknown
		if ((Object)(object)vehicle == (Object)null || ((Il2CppObjectBase)vehicle).Pointer == IntPtr.Zero)
		{
			return;
		}
		try
		{
			IReadOnlyList<AI> registeredAIs = AIRegister.RegisteredAIs;
			List<AI> registered = ((registeredAIs != null) ? ((Il2CppObjectBase)registeredAIs).TryCast<List<AI>>() : null);
			int count = 0;
			if (registered != null)
			{
				count = registered.Count;
				for (int i = 0; i < registered.Count; i++)
				{
					AI ai = registered[i];
					if (ai == null || !(((Il2CppObjectBase)ai).Pointer != IntPtr.Zero))
					{
						continue;
					}
					try
					{
						ai.Enabled = false;
					}
					catch
					{
					}
					CommanderAI cmd = ((Il2CppObjectBase)ai).TryCast<CommanderAI>();
					if (cmd != null && ((Il2CppObjectBase)cmd).Pointer != IntPtr.Zero)
					{
						try
						{
							cmd.Release();
						}
						catch
						{
						}
						try
						{
							cmd.Halt((Object)null, (CancellationToken)null);
						}
						catch
						{
						}
					}
				}
			}
			AIRegister.Clear();
			Plugin instance = Plugin.Instance;
			if (instance != null)
			{
				ManualLogSource log = ((BasePlugin)instance).Log;
				if (log != null)
				{
					bool flag = default;
					BepInExInfoLogInterpolatedStringHandler val = new BepInExInfoLogInterpolatedStringHandler(82, 1, ref flag);
					if (flag)
					{
						((BepInExLogInterpolatedStringHandler)val).AppendLiteral("[LAN Mod] Neutralized AI on puppet tank (");
						((BepInExLogInterpolatedStringHandler)val).AppendFormatted<int>(count);
						((BepInExLogInterpolatedStringHandler)val).AppendLiteral(" AI entities halted, AIRegister cleared).");
					}
					log.LogInfo(val);
				}
			}
			foreach (TraverseMotor motor in _remoteTraverseMotors)
			{
				if (!((Object)(object)motor != (Object)null) || !(((Il2CppObjectBase)motor).Pointer != IntPtr.Zero) || motor.Behaviour == null || !(((Il2CppObjectBase)motor.Behaviour).Pointer != IntPtr.Zero))
				{
					continue;
				}
				try
				{
					IAimable val2 = ((Il2CppObjectBase)motor.Behaviour).TryCast<IAimable>();
					if (val2 != null)
					{
						val2.ClearTarget();
					}
				}
				catch
				{
				}
			}
			foreach (LayingDrive drive in _remoteLayingDrives)
			{
				if (!((Object)(object)drive != (Object)null) || !(((Il2CppObjectBase)drive).Pointer != IntPtr.Zero) || drive.Behaviour == null || !(((Il2CppObjectBase)drive.Behaviour).Pointer != IntPtr.Zero))
				{
					continue;
				}
				try
				{
					IAimable val3 = ((Il2CppObjectBase)drive.Behaviour).TryCast<IAimable>();
					if (val3 != null)
					{
						val3.ClearTarget();
					}
				}
				catch
				{
				}
			}
		}
		catch (Exception ex)
		{
			Plugin instance2 = Plugin.Instance;
			if (instance2 != null)
			{
				ManualLogSource log2 = ((BasePlugin)instance2).Log;
				if (log2 != null)
				{
					log2.LogWarning((object)("NeutralizeAI note: " + ex.Message));
				}
			}
		}
	}

	public void CycleLocalVehicle()
	{
		Il2CppArrayBase<VehicleBehaviour> allVehicles = Object.FindObjectsOfType<VehicleBehaviour>();
		if (allVehicles.Length == 0)
		{
			return;
		}
		int currentIndex = -1;
		for (int i = 0; i < allVehicles.Length; i++)
		{
			if ((Object)(object)LocalVehicle != (Object)null && ((Il2CppObjectBase)allVehicles[i]).Pointer == ((Il2CppObjectBase)LocalVehicle).Pointer)
			{
				currentIndex = i;
				break;
			}
		}
		int nextIndex = (currentIndex + 1) % allVehicles.Length;
		LocalVehicle = allVehicles[nextIndex];
		CacheLocalVehicleComponents();
		if ((Object)(object)LocalController != (Object)null && (Object)(object)LocalVehicle != (Object)null)
		{
			try
			{
				LocalController.Hook(((Il2CppObjectBase)LocalVehicle).Cast<IVehicleBehaviour>());
			}
			catch
			{
			}
		}
		if ((Object)(object)RemoteVehicle != (Object)null && ((Il2CppObjectBase)RemoteVehicle).Pointer == ((Il2CppObjectBase)LocalVehicle).Pointer)
		{
			RemoteVehicle = null;
		}
		FindVehiclesIfNeeded();
	}

	public void CycleRemoteVehicle()
	{
		Il2CppArrayBase<VehicleBehaviour> allVehicles = Object.FindObjectsOfType<VehicleBehaviour>();
		if (allVehicles.Length < 2)
		{
			return;
		}
		int currentIndex = -1;
		for (int i = 0; i < allVehicles.Length; i++)
		{
			if ((Object)(object)RemoteVehicle != (Object)null && ((Il2CppObjectBase)allVehicles[i]).Pointer == ((Il2CppObjectBase)RemoteVehicle).Pointer)
			{
				currentIndex = i;
				break;
			}
		}
		for (int offset = 1; offset < allVehicles.Length; offset++)
		{
			int nextIndex = (currentIndex + offset) % allVehicles.Length;
			if ((Object)(object)LocalVehicle == (Object)null || ((Il2CppObjectBase)allVehicles[nextIndex]).Pointer != ((Il2CppObjectBase)LocalVehicle).Pointer)
			{
				SetRemoteVehicle(allVehicles[nextIndex]);
				break;
			}
		}
	}

	private void SendLocalState(UdpNetworkTransport transport)
	{
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_005e: Unknown result type (might be due to invalid IL or missing references)
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0088: Unknown result type (might be due to invalid IL or missing references)
		//IL_0080: Unknown result type (might be due to invalid IL or missing references)
		//IL_008d: Unknown result type (might be due to invalid IL or missing references)
		//IL_009f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0097: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00af: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d5: Unknown result type (might be due to invalid IL or missing references)
		//IL_0115: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fe: Unknown result type (might be due to invalid IL or missing references)
		//IL_0130: Unknown result type (might be due to invalid IL or missing references)
		//IL_0122: Unknown result type (might be due to invalid IL or missing references)
		//IL_0127: Unknown result type (might be due to invalid IL or missing references)
		//IL_012b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0160: Unknown result type (might be due to invalid IL or missing references)
		//IL_0161: Unknown result type (might be due to invalid IL or missing references)
		//IL_016b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0170: Unknown result type (might be due to invalid IL or missing references)
		//IL_0146: Unknown result type (might be due to invalid IL or missing references)
		//IL_014b: Unknown result type (might be due to invalid IL or missing references)
		//IL_014f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0154: Unknown result type (might be due to invalid IL or missing references)
		//IL_018b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0192: Unknown result type (might be due to invalid IL or missing references)
		//IL_0197: Unknown result type (might be due to invalid IL or missing references)
		//IL_019c: Unknown result type (might be due to invalid IL or missing references)
		//IL_019f: Unknown result type (might be due to invalid IL or missing references)
		//IL_017d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0182: Unknown result type (might be due to invalid IL or missing references)
		//IL_0186: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c9: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ce: Unknown result type (might be due to invalid IL or missing references)
		//IL_0232: Unknown result type (might be due to invalid IL or missing references)
		//IL_020f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0214: Unknown result type (might be due to invalid IL or missing references)
		//IL_0225: Unknown result type (might be due to invalid IL or missing references)
		//IL_022a: Unknown result type (might be due to invalid IL or missing references)
		//IL_022f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0290: Unknown result type (might be due to invalid IL or missing references)
		//IL_0295: Unknown result type (might be due to invalid IL or missing references)
		//IL_02d9: Unknown result type (might be due to invalid IL or missing references)
		//IL_02bb: Unknown result type (might be due to invalid IL or missing references)
		//IL_02c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_02cc: Unknown result type (might be due to invalid IL or missing references)
		//IL_02d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_02d6: Unknown result type (might be due to invalid IL or missing references)
		//IL_039b: Unknown result type (might be due to invalid IL or missing references)
		//IL_03a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_03a4: Invalid comparison between Unknown and I4
		if ((Object)(object)LocalVehicle == (Object)null || transport == null)
		{
			return;
		}
		PacketWriter pw = new PacketWriter(PacketType.TransformSync);
		_localSequence++;
		pw.Write(_localSequence);
		pw.Write(Time.time);
		Rigidbody rb = LocalVehicle.Rigidbody;
		Vector3 pos = (((Object)(object)rb != (Object)null) ? rb.position : ((Component)LocalVehicle).transform.position);
		Quaternion rot = (((Object)(object)rb != (Object)null) ? rb.rotation : ((Component)LocalVehicle).transform.rotation);
		Vector3 vel = (((Object)(object)rb != (Object)null) ? rb.velocity : Vector3.zero);
		Vector3 angVel = (((Object)(object)rb != (Object)null) ? rb.angularVelocity : Vector3.zero);
		pw.Write(pos);
		pw.Write(rot);
		pw.Write(vel);
		pw.Write(angVel);
		Ray aimRay;
		Vector3 val;
		if ((Object)(object)LocalController != (Object)null)
		{
			aimRay = LocalController.AimRay;
			if (aimRay.direction != Vector3.zero)
			{
				aimRay = LocalController.AimRay;
				val = aimRay.direction;
				goto IL_0130;
			}
		}
		val = ((Component)LocalVehicle).transform.forward;
		goto IL_0130;
		IL_0130:
		Vector3 aimDir = val;
		Vector3 val2;
		if ((Object)(object)LocalController != (Object)null)
		{
			aimRay = LocalController.AimRay;
			if (aimRay.origin != Vector3.zero)
			{
				aimRay = LocalController.AimRay;
				val2 = aimRay.origin;
				goto IL_018b;
			}
		}
		val2 = pos + Vector3.up * 2f;
		goto IL_018b;
		IL_018b:
		Vector3 aimPoint = val2 + aimDir * 500f;
		pw.Write(aimPoint);
		byte turretCount = (byte)Mathf.Clamp(_localTraverseMotors.Count, 0, 16);
		pw.Write(turretCount);
		for (int i = 0; i < turretCount; i++)
		{
			Quaternion tRot = Quaternion.identity;
			TraverseMotor motor = _localTraverseMotors[i];
			if ((Object)(object)motor != (Object)null)
			{
				VehicleTransform controlledTransform = motor.ControlledTransform;
				if ((Object)(object)((controlledTransform != null) ? ((Component)controlledTransform).transform : null) != (Object)null)
				{
					tRot = Quaternion.Inverse(((Component)LocalVehicle).transform.rotation) * ((Component)motor.ControlledTransform).transform.rotation;
				}
			}
			pw.Write(tRot);
		}
		bool isDead = _localWasDestroyed || IsVehicleDestroyed(LocalVehicle);
		byte cannonCount = (byte)Mathf.Clamp(_localCannons.Count, 0, 16);
		pw.Write(cannonCount);
		for (int j = 0; j < cannonCount; j++)
		{
			Cannon cannon = _localCannons[j];
			Quaternion cRot = Quaternion.identity;
			if ((Object)(object)cannon != (Object)null && (Object)(object)((Component)cannon).transform != (Object)null)
			{
				cRot = Quaternion.Inverse(((Component)LocalVehicle).transform.rotation) * ((Component)cannon).transform.rotation;
			}
			pw.Write(cRot);
			pw.Write(value: false);
		}
		pw.Write(value: false);
		pw.Write(isDead ? 0f : 1f);
		pw.Write(0f);
		pw.Write(((Object)(object)LocalController != (Object)null) ? LocalController.throttleTarget : 0f);
		pw.Write(((Object)(object)LocalController != (Object)null) ? LocalController.steerTarget : 0f);
		bool localOnFire = false;
		try
		{
			if ((Object)(object)LocalVehicle != (Object)null && ((Il2CppObjectBase)LocalVehicle).Pointer != IntPtr.Zero)
			{
				localOnFire = (LocalVehicle.Flags & 0x40) > 0;
				if (!localOnFire)
				{
					try
					{
						FireControlVehicleModule fm = default;
						if (LocalVehicle.TryGetModule<FireControlVehicleModule>(ref fm) && fm != null && fm.OnFire)
						{
							localOnFire = true;
						}
					}
					catch
					{
					}
				}
				if (!localOnFire)
				{
					Il2CppArrayBase<FuelTank> fts = ((Component)LocalVehicle).GetComponentsInChildren<FuelTank>(true);
					if (fts != null)
					{
						foreach (FuelTank ft in fts)
						{
							if ((Object)(object)ft != (Object)null && ((Il2CppObjectBase)ft).Pointer != IntPtr.Zero && ft.behaviour != null && ft.behaviour.fireStarted)
							{
								localOnFire = true;
								break;
							}
						}
					}
				}
			}
		}
		catch
		{
		}
		pw.Write(localOnFire);
		transport.Send(pw.ToArray());
	}

	private void CheckLocalFiring(UdpNetworkTransport transport)
	{
		if (transport == null || (Object)(object)LocalVehicle == (Object)null || !IsValid((Component)(object)LocalVehicle) || _localWasDestroyed)
		{
			return;
		}
		while (_localCannonsLastFireTime.Count < _localCannons.Count)
		{
			Cannon cannon = _localCannons[_localCannonsLastFireTime.Count];
			_localCannonsLastFireTime.Add((((cannon != null) ? cannon.Weapon : null) != null) ? cannon.Weapon.LastFireTime : 0f);
		}
		for (int i = 0; i < _localCannons.Count; i++)
		{
			Cannon cannon2 = _localCannons[i];
			if ((Object)(object)cannon2 != (Object)null && cannon2.Weapon != null && i < _localCannonsLastFireTime.Count)
			{
				float lastTime = _localCannonsLastFireTime[i];
				if (cannon2.Weapon.LastFireTime > lastTime && cannon2.Weapon.LastFireTime - lastTime > 0.15f)
				{
					_localCannonsLastFireTime[i] = cannon2.Weapon.LastFireTime;
					SendIndividualFirePacket(transport, i, cannon2);
				}
			}
		}
		bool controllerFiring = (Object)(object)LocalController != (Object)null && LocalController.Firing;
		if (((_localCannons.Count == 0) & controllerFiring) && !_lastLocalFiringState)
		{
			SendIndividualFirePacket(transport, 0, null);
		}
		_lastLocalFiringState = controllerFiring;
	}

	public void OnLocalWeaponFired(WeaponBehaviour weapon)
	{
		if (IsTriggeringRemoteFiring || (Object)(object)LocalVehicle == (Object)null || ((Il2CppObjectBase)LocalVehicle).Pointer == IntPtr.Zero || _localWasDestroyed)
		{
			return;
		}
		UdpNetworkTransport transport = MultiplayerController.Instance?.Transport;
		if (transport == null || !transport.IsConnected)
		{
			return;
		}
		int slot = -1;
		Cannon firedCannon = null;
		if (weapon != null && ((Il2CppObjectBase)weapon).Pointer != IntPtr.Zero)
		{
			if ((Object)(object)RemoteVehicle != (Object)null && ((Il2CppObjectBase)RemoteVehicle).Pointer != IntPtr.Zero)
			{
				try
				{
					if ((Object)(object)weapon.transform != (Object)null && (Object)(object)((Component)RemoteVehicle).transform != (Object)null && weapon.transform.IsChildOf(((Component)RemoteVehicle).transform))
					{
						return;
					}
				}
				catch
				{
				}
			}
			for (int i = 0; i < _localCannons.Count; i++)
			{
				Cannon c = _localCannons[i];
				if ((Object)(object)c != (Object)null && c.Weapon != null && ((Il2CppObjectBase)c.Weapon).Pointer == ((Il2CppObjectBase)weapon).Pointer)
				{
					slot = i;
					firedCannon = c;
					break;
				}
			}
		}
		if (slot >= 0)
		{
			if (slot < _localCannonsLastFireTime.Count && (Object)(object)firedCannon != (Object)null && firedCannon.Weapon != null)
			{
				_localCannonsLastFireTime[slot] = firedCannon.Weapon.LastFireTime;
			}
			SendIndividualFirePacket(transport, slot, firedCannon);
			return;
		}
		bool isLocal = false;
		if (weapon != null && ((Il2CppObjectBase)weapon).Pointer != IntPtr.Zero)
		{
			try
			{
				if ((Object)(object)weapon.transform != (Object)null && (Object)(object)((Component)LocalVehicle).transform != (Object)null && weapon.transform.IsChildOf(((Component)LocalVehicle).transform))
				{
					isLocal = true;
				}
			}
			catch
			{
			}
		}
		else
		{
			isLocal = true;
		}
		if (isLocal)
		{
			SendIndividualFirePacket(transport, 0, (_localCannons.Count > 0) ? _localCannons[0] : null);
		}
	}

	private void SendIndividualFirePacket(UdpNetworkTransport transport, int slot, Cannon cannon)
	{
		//IL_0058: Unknown result type (might be due to invalid IL or missing references)
		//IL_005d: Unknown result type (might be due to invalid IL or missing references)
		//IL_005e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0063: Unknown result type (might be due to invalid IL or missing references)
		//IL_012a: Unknown result type (might be due to invalid IL or missing references)
		//IL_012f: Unknown result type (might be due to invalid IL or missing references)
		//IL_013b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0140: Unknown result type (might be due to invalid IL or missing references)
		//IL_0081: Unknown result type (might be due to invalid IL or missing references)
		//IL_0086: Unknown result type (might be due to invalid IL or missing references)
		//IL_008d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0092: Unknown result type (might be due to invalid IL or missing references)
		//IL_0142: Unknown result type (might be due to invalid IL or missing references)
		//IL_0149: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00be: Unknown result type (might be due to invalid IL or missing references)
		//IL_0201: Unknown result type (might be due to invalid IL or missing references)
		//IL_0208: Expected Obj, but got Unknown
		//IL_00d5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ea: Unknown result type (might be due to invalid IL or missing references)
		//IL_010e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0113: Unknown result type (might be due to invalid IL or missing references)
		//IL_0117: Unknown result type (might be due to invalid IL or missing references)
		//IL_0101: Unknown result type (might be due to invalid IL or missing references)
		//IL_0169: Unknown result type (might be due to invalid IL or missing references)
		//IL_011c: Unknown result type (might be due to invalid IL or missing references)
		//IL_017b: Unknown result type (might be due to invalid IL or missing references)
		if (_localCannonsLastSendPacketTime.TryGetValue(slot, out var lastSend) && Time.time - lastSend < 0.25f)
		{
			return;
		}
		_localCannonsLastSendPacketTime[slot] = Time.time;
		PacketWriter pw = new PacketWriter(PacketType.Fire);
		_localSequence++;
		pw.Write(_localSequence);
		pw.Write(slot);
		Vector3 muzzlePos = Vector3.zero;
		Vector3 fireDir = Vector3.forward;
		if ((Object)(object)cannon != (Object)null && (Object)(object)((Component)cannon).transform != (Object)null)
		{
			muzzlePos = ((Component)cannon).transform.position;
			fireDir = ((Component)cannon).transform.forward;
		}
		else if ((Object)(object)LocalController != (Object)null)
		{
			muzzlePos = ((LocalController.activeGunner != null) ? LocalController.activeGunner.FirePosition : ((Component)LocalVehicle).transform.position);
			Ray aimRay = LocalController.AimRay;
			Vector3 val;
			if (!(aimRay.direction != Vector3.zero))
			{
				val = ((Component)LocalVehicle).transform.forward;
			}
			else
			{
				aimRay = LocalController.AimRay;
				val = aimRay.direction;
			}
			fireDir = val;
		}
		else
		{
			muzzlePos = ((Component)LocalVehicle).transform.position;
			fireDir = ((Component)LocalVehicle).transform.forward;
		}
		pw.Write(muzzlePos);
		pw.Write(fireDir);
		int projTypeId = 0;
		try
		{
			if ((Object)(object)cannon != (Object)null && cannon.Weapon != null && cannon.Weapon.LoadedProjectileTypeID.Value != 0)
			{
				projTypeId = cannon.Weapon.LoadedProjectileTypeID.Value;
				if (slot >= 0 && slot < _localCannonProjectileIDs.Count)
				{
					_localCannonProjectileIDs[slot] = projTypeId;
				}
			}
		}
		catch
		{
		}
		if (projTypeId == 0 && slot >= 0 && slot < _localCannonProjectileIDs.Count)
		{
			projTypeId = _localCannonProjectileIDs[slot];
		}
		pw.Write(projTypeId);
		transport.Send(pw.ToArray());
		Plugin instance = Plugin.Instance;
		if (instance == null)
		{
			return;
		}
		ManualLogSource log = ((BasePlugin)instance).Log;
		if (log != null)
		{
			bool flag = default;
			BepInExInfoLogInterpolatedStringHandler val2 = new BepInExInfoLogInterpolatedStringHandler(67, 2, ref flag);
			if (flag)
			{
				((BepInExLogInterpolatedStringHandler)val2).AppendLiteral("[LAN Mod] Cannon slot ");
				((BepInExLogInterpolatedStringHandler)val2).AppendFormatted<int>(slot);
				((BepInExLogInterpolatedStringHandler)val2).AppendLiteral(" fired! Dispatched Fire packet (projTypeId=");
				((BepInExLogInterpolatedStringHandler)val2).AppendFormatted<int>(projTypeId);
				((BepInExLogInterpolatedStringHandler)val2).AppendLiteral(").");
			}
			log.LogInfo(val2);
		}
	}

	private void ApplyRemoteState()
	{
		//IL_0161: Unknown result type (might be due to invalid IL or missing references)
		//IL_0167: Unknown result type (might be due to invalid IL or missing references)
		//IL_017f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0190: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b0: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_0252: Unknown result type (might be due to invalid IL or missing references)
		//IL_0263: Unknown result type (might be due to invalid IL or missing references)
		//IL_0211: Unknown result type (might be due to invalid IL or missing references)
		//IL_0217: Unknown result type (might be due to invalid IL or missing references)
		//IL_021d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0233: Unknown result type (might be due to invalid IL or missing references)
		//IL_0239: Unknown result type (might be due to invalid IL or missing references)
		//IL_023f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0274: Unknown result type (might be due to invalid IL or missing references)
		//IL_0285: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)_remoteRigidbody == (Object)null || ((Il2CppObjectBase)_remoteRigidbody).Pointer == IntPtr.Zero || (Object)(object)RemoteVehicle == (Object)null || ((Il2CppObjectBase)RemoteVehicle).Pointer == IntPtr.Zero)
		{
			if ((Object)(object)RemoteVehicle != (Object)null && ((Il2CppObjectBase)RemoteVehicle).Pointer != IntPtr.Zero)
			{
				_remoteRigidbody = RemoteVehicle.Rigidbody;
			}
			if ((Object)(object)_remoteRigidbody == (Object)null || ((Il2CppObjectBase)_remoteRigidbody).Pointer == IntPtr.Zero)
			{
				return;
			}
		}
		try
		{
			if (float.IsNaN(_targetPosition.x) || float.IsNaN(_targetPosition.y) || float.IsNaN(_targetPosition.z) || float.IsNaN(_targetRotation.x) || float.IsNaN(_targetRotation.y) || float.IsNaN(_targetRotation.z) || float.IsNaN(_targetRotation.w))
			{
				return;
			}
			if (_remoteRigidbody.isKinematic)
			{
				_remoteRigidbody.isKinematic = false;
			}
			if (_remoteRigidbody.IsSleeping())
			{
				_remoteRigidbody.WakeUp();
			}
			if (Vector3.Distance(_remoteRigidbody.position, _targetPosition) > 25f)
			{
				_remoteRigidbody.position = _targetPosition;
				_remoteRigidbody.rotation = _targetRotation;
				_remoteRigidbody.velocity = Vector3.zero;
				_remoteRigidbody.angularVelocity = Vector3.zero;
				((Component)RemoteVehicle).transform.position = _targetPosition;
				((Component)RemoteVehicle).transform.rotation = _targetRotation;
				return;
			}
			if (SmoothInterpolation)
			{
				float t = Mathf.Clamp01(Time.fixedDeltaTime * InterpolationSpeed);
				_remoteRigidbody.position = Vector3.Lerp(_remoteRigidbody.position, _targetPosition, t);
				_remoteRigidbody.rotation = Quaternion.Slerp(_remoteRigidbody.rotation, _targetRotation, t);
			}
			else
			{
				_remoteRigidbody.position = _targetPosition;
				_remoteRigidbody.rotation = _targetRotation;
			}
			_remoteRigidbody.velocity = _targetVelocity;
			_remoteRigidbody.angularVelocity = _targetAngularVelocity;
		}
		catch
		{
		}
	}

	private void ApplyRemoteAimingMechanisms(float deltaTime = 0.02f)
	{
		//IL_00ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ba: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_0199: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_02e7: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f4: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_02fe: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fa: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_01fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_0200: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ed: Unknown result type (might be due to invalid IL or missing references)
		//IL_0358: Unknown result type (might be due to invalid IL or missing references)
		//IL_0329: Unknown result type (might be due to invalid IL or missing references)
		//IL_032e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0331: Unknown result type (might be due to invalid IL or missing references)
		//IL_031e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0213: Unknown result type (might be due to invalid IL or missing references)
		//IL_0266: Unknown result type (might be due to invalid IL or missing references)
		//IL_026b: Unknown result type (might be due to invalid IL or missing references)
		//IL_026e: Unknown result type (might be due to invalid IL or missing references)
		//IL_025b: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)RemoteVehicle == (Object)null || ((Il2CppObjectBase)RemoteVehicle).Pointer == IntPtr.Zero)
		{
			return;
		}
		float rotLerp = (SmoothInterpolation ? Mathf.Clamp01(deltaTime * InterpolationSpeed) : 1f);
		for (int i = 0; i < _remoteTraverseMotors.Count; i++)
		{
			TraverseMotor motor = _remoteTraverseMotors[i];
			if (!((Object)(object)motor == (Object)null) && !(((Il2CppObjectBase)motor).Pointer == IntPtr.Zero) && i < _targetTurretRotations.Count)
			{
				VehicleTransform controlledTransform = motor.ControlledTransform;
				if ((Object)(object)((controlledTransform != null) ? ((Component)controlledTransform).transform : null) != (Object)null)
				{
					Quaternion targetWorldRot = ((Component)RemoteVehicle).transform.rotation * _targetTurretRotations[i];
					Transform t = ((Component)motor.ControlledTransform).transform;
					t.rotation = (SmoothInterpolation ? Quaternion.Slerp(t.rotation, targetWorldRot, rotLerp) : targetWorldRot);
					motor.ControlledTransform.LocalRotation = t.localRotation;
					motor.ControlledTransform.NotifyTransformChanged();
				}
			}
		}
		for (int j = 0; j < _remoteLayingDrives.Count; j++)
		{
			LayingDrive drive = _remoteLayingDrives[j];
			if ((Object)(object)drive == (Object)null || ((Il2CppObjectBase)drive).Pointer == IntPtr.Zero)
			{
				continue;
			}
			int rotIdx = Mathf.Min(j, _targetCannonRotations.Count - 1);
			if (rotIdx >= 0 && rotIdx < _targetCannonRotations.Count)
			{
				Quaternion targetWorldRot2 = ((Component)RemoteVehicle).transform.rotation * _targetCannonRotations[rotIdx];
				if ((Object)(object)((VehiclePart)drive).VehicleTransform != (Object)null && (Object)(object)((Component)((VehiclePart)drive).VehicleTransform).transform != (Object)null)
				{
					VehicleTransform vt = ((VehiclePart)drive).VehicleTransform;
					((Component)vt).transform.rotation = (SmoothInterpolation ? Quaternion.Slerp(((Component)vt).transform.rotation, targetWorldRot2, rotLerp) : targetWorldRot2);
					vt.LocalRotation = ((Component)vt).transform.localRotation;
					vt.NotifyTransformChanged();
				}
				ILayingDriveBehaviour behaviour = drive.Behaviour;
				LayingDriveBehaviour ldb = ((behaviour != null) ? ((Il2CppObjectBase)behaviour).TryCast<LayingDriveBehaviour>() : null);
				if (ldb != null && (Object)(object)ldb.trunnions != (Object)null)
				{
					ldb.trunnions.rotation = (SmoothInterpolation ? Quaternion.Slerp(ldb.trunnions.rotation, targetWorldRot2, rotLerp) : targetWorldRot2);
				}
			}
		}
		for (int k = 0; k < _remoteCannons.Count; k++)
		{
			Cannon cannon = _remoteCannons[k];
			if (!((Object)(object)cannon == (Object)null) && !(((Il2CppObjectBase)cannon).Pointer == IntPtr.Zero) && k < _targetCannonRotations.Count)
			{
				Quaternion targetWorldRot3 = ((Component)RemoteVehicle).transform.rotation * _targetCannonRotations[k];
				if ((Object)(object)((Component)cannon).transform != (Object)null)
				{
					((Component)cannon).transform.rotation = (SmoothInterpolation ? Quaternion.Slerp(((Component)cannon).transform.rotation, targetWorldRot3, rotLerp) : targetWorldRot3);
				}
				if ((Object)(object)((VehiclePart)cannon).VehicleTransform != (Object)null)
				{
					((VehiclePart)cannon).VehicleTransform.LocalRotation = ((Component)cannon).transform.localRotation;
					((VehiclePart)cannon).VehicleTransform.NotifyTransformChanged();
				}
			}
		}
	}

	private void OnPacketReceived(PacketReader reader, IPEndPoint endPoint)
	{
		//IL_0182: Unknown result type (might be due to invalid IL or missing references)
		//IL_0187: Unknown result type (might be due to invalid IL or missing references)
		//IL_018a: Unknown result type (might be due to invalid IL or missing references)
		//IL_018f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0192: Unknown result type (might be due to invalid IL or missing references)
		//IL_0197: Unknown result type (might be due to invalid IL or missing references)
		//IL_019a: Unknown result type (might be due to invalid IL or missing references)
		//IL_019f: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_0570: Unknown result type (might be due to invalid IL or missing references)
		//IL_0572: Unknown result type (might be due to invalid IL or missing references)
		//IL_0578: Unknown result type (might be due to invalid IL or missing references)
		//IL_057a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0580: Unknown result type (might be due to invalid IL or missing references)
		//IL_0582: Unknown result type (might be due to invalid IL or missing references)
		//IL_0588: Unknown result type (might be due to invalid IL or missing references)
		//IL_058a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0590: Unknown result type (might be due to invalid IL or missing references)
		//IL_0592: Unknown result type (might be due to invalid IL or missing references)
		//IL_04b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_04bc: Expected Obj, but got Unknown
		//IL_053f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0546: Expected Obj, but got Unknown
		//IL_0619: Unknown result type (might be due to invalid IL or missing references)
		//IL_0620: Unknown result type (might be due to invalid IL or missing references)
		//IL_0625: Unknown result type (might be due to invalid IL or missing references)
		//IL_0661: Unknown result type (might be due to invalid IL or missing references)
		//IL_0666: Unknown result type (might be due to invalid IL or missing references)
		//IL_0669: Unknown result type (might be due to invalid IL or missing references)
		//IL_066e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0671: Unknown result type (might be due to invalid IL or missing references)
		//IL_0673: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d3: Expected Obj, but got Unknown
		//IL_03bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_03c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_05cf: Unknown result type (might be due to invalid IL or missing references)
		//IL_05d4: Unknown result type (might be due to invalid IL or missing references)
		//IL_063e: Unknown result type (might be due to invalid IL or missing references)
		//IL_05eb: Unknown result type (might be due to invalid IL or missing references)
		//IL_05f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_01fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_081c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0821: Unknown result type (might be due to invalid IL or missing references)
		//IL_0824: Unknown result type (might be due to invalid IL or missing references)
		//IL_0829: Unknown result type (might be due to invalid IL or missing references)
		//IL_06b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_06bb: Expected Obj, but got Unknown
		//IL_0872: Unknown result type (might be due to invalid IL or missing references)
		//IL_0874: Unknown result type (might be due to invalid IL or missing references)
		//IL_0797: Unknown result type (might be due to invalid IL or missing references)
		//IL_079e: Expected Obj, but got Unknown
		//IL_0848: Unknown result type (might be due to invalid IL or missing references)
		//IL_084f: Expected Obj, but got Unknown
		//IL_0861: Unknown result type (might be due to invalid IL or missing references)
		//IL_02fb: Unknown result type (might be due to invalid IL or missing references)
		//IL_0302: Unknown result type (might be due to invalid IL or missing references)
		//IL_0304: Invalid comparison between Unknown and I4
		bool flag = default;
		if (reader.Type == PacketType.Handshake)
		{
			string playerName = reader.ReadString();
			string tankName = reader.ReadString();
			SyncStatus = $"Connected to {playerName} ({tankName})";
			_remoteWasDestroyed = false;
			if ((Object)(object)RemoteVehicle != (Object)null && ((Il2CppObjectBase)RemoteVehicle).Pointer != IntPtr.Zero)
			{
				RepairAndRestoreVehicle(RemoteVehicle);
				CacheRemoteVehicleComponents();
				NeutralizeAIOnRemoteVehicle(RemoteVehicle);
			}
			Plugin instance = Plugin.Instance;
			if (instance != null)
			{
				ManualLogSource log = ((BasePlugin)instance).Log;
				if (log != null)
				{
					BepInExInfoLogInterpolatedStringHandler val = new BepInExInfoLogInterpolatedStringHandler(78, 2, ref flag);
					if (flag)
					{
						((BepInExLogInterpolatedStringHandler)val).AppendLiteral("[LAN Mod] Handshake received from ");
						((BepInExLogInterpolatedStringHandler)val).AppendFormatted<string>(playerName);
						((BepInExLogInterpolatedStringHandler)val).AppendLiteral(" playing ");
						((BepInExLogInterpolatedStringHandler)val).AppendFormatted<string>(tankName);
						((BepInExLogInterpolatedStringHandler)val).AppendLiteral(". Remote puppet restored & unmuted.");
					}
					log.LogInfo(val);
				}
			}
			UdpNetworkTransport transport = MultiplayerController.Instance?.Transport;
			if (transport != null && transport.IsHosting)
			{
				PacketWriter pw = new PacketWriter(PacketType.Handshake);
				pw.Write(LocalVehicleName);
				pw.Write(LocalVehicleName);
				transport.Send(pw.ToArray());
			}
		}
		else if (reader.Type == PacketType.TransformSync)
		{
			reader.ReadUInt();
			reader.ReadFloat();
			Vector3 pos = reader.ReadVector3();
			Quaternion rot = reader.ReadQuaternion();
			Vector3 vel = reader.ReadVector3();
			Vector3 angVel = reader.ReadVector3();
			Vector3 aim = reader.ReadVector3();
			byte turretCount = reader.ReadByte();
			_targetTurretRotations.Clear();
			for (int i = 0; i < turretCount; i++)
			{
				_targetTurretRotations.Add(reader.ReadQuaternion());
			}
			byte cannonCount = reader.ReadByte();
			_targetCannonRotations.Clear();
			for (int j = 0; j < cannonCount; j++)
			{
				_targetCannonRotations.Add(reader.ReadQuaternion());
				reader.ReadBool();
			}
			reader.ReadBool();
			float health = reader.ReadFloat();
			reader.ReadFloat();
			reader.ReadFloat();
			reader.ReadFloat();
			bool peerOnFire = reader.RemainingBytes > 0 && reader.ReadBool();
			if (health <= 0.05f && !_remoteWasDestroyed)
			{
				_remoteWasDestroyed = true;
				if (IsValid((Component)(object)RemoteVehicle))
				{
					OnVehicleDestroyed(RemoteVehicle, isLocal: false, MultiplayerController.Instance?.Transport);
				}
			}
			else if (health > 0.5f && _remoteWasDestroyed)
			{
				_remoteWasDestroyed = false;
				if (IsValid((Component)(object)RemoteVehicle))
				{
					RepairAndRestoreVehicle(RemoteVehicle);
					CacheRemoteVehicleComponents();
					NeutralizeAIOnRemoteVehicle(RemoteVehicle);
					_remoteCannonsLastFireTime.Clear();
				}
			}
			if (IsValid((Component)(object)RemoteVehicle))
			{
				bool puppetCurrentlyOnFire = (RemoteVehicle.Flags & 0x40) > 0;
				if (!puppetCurrentlyOnFire)
				{
					try
					{
						FireControlVehicleModule fm = default;
						if (RemoteVehicle.TryGetModule<FireControlVehicleModule>(ref fm) && fm != null && fm.OnFire)
						{
							puppetCurrentlyOnFire = true;
						}
					}
					catch
					{
					}
				}
				if (!puppetCurrentlyOnFire)
				{
					Il2CppArrayBase<FuelTank> fts = ((Component)RemoteVehicle).GetComponentsInChildren<FuelTank>(true);
					if (fts != null)
					{
						foreach (FuelTank ft in fts)
						{
							if ((Object)(object)ft != (Object)null && ((Il2CppObjectBase)ft).Pointer != IntPtr.Zero && ft.behaviour != null && ft.behaviour.fireStarted)
							{
								puppetCurrentlyOnFire = true;
								break;
							}
						}
					}
				}
				if (peerOnFire && !puppetCurrentlyOnFire)
				{
					try
					{
						VehicleBehaviour remoteVehicle = RemoteVehicle;
						remoteVehicle.Flags = (VehicleFlags)(remoteVehicle.Flags | 0x40);
						FireControlVehicleModule fm2 = default;
						if (RemoteVehicle.TryGetModule<FireControlVehicleModule>(ref fm2) && fm2 != null)
						{
							fm2.StartFire(45f);
						}
						Il2CppArrayBase<FuelTank> fts2 = ((Component)RemoteVehicle).GetComponentsInChildren<FuelTank>(true);
						if (fts2 != null && fts2.Length > 0)
						{
							foreach (FuelTank ft2 in fts2)
							{
								if ((Object)(object)ft2 != (Object)null && ((Il2CppObjectBase)ft2).Pointer != IntPtr.Zero && ft2.behaviour != null)
								{
									ft2.behaviour.StartFire();
									ft2.behaviour.fireStarted = true;
								}
							}
						}
						Plugin instance2 = Plugin.Instance;
						if (instance2 != null)
						{
							ManualLogSource log2 = ((BasePlugin)instance2).Log;
							if (log2 != null)
							{
								log2.LogInfo((object)"[LAN Mod] Peer is on fire -> Ignited puppet tank fire effects & modules.");
							}
						}
					}
					catch (Exception ex)
					{
						Plugin instance = Plugin.Instance;
						if (instance != null)
						{
							ManualLogSource log = ((BasePlugin)instance).Log;
							if (log != null)
							{
								BepInExWarningLogInterpolatedStringHandler val2 = new BepInExWarningLogInterpolatedStringHandler(38, 1, ref flag);
								if (flag)
								{
									((BepInExLogInterpolatedStringHandler)val2).AppendLiteral("[LAN Mod] Error igniting puppet fire: ");
									((BepInExLogInterpolatedStringHandler)val2).AppendFormatted<string>(ex.Message);
								}
								log.LogWarning(val2);
							}
						}
					}
				}
				else if (!peerOnFire & puppetCurrentlyOnFire)
				{
					try
					{
						ExtinguishVehicle(RemoteVehicle);
						Plugin instance3 = Plugin.Instance;
						if (instance3 != null)
						{
							ManualLogSource log3 = ((BasePlugin)instance3).Log;
							if (log3 != null)
							{
								log3.LogInfo((object)"[LAN Mod] Peer extinguished fire -> Puppet tank fire extinguished.");
							}
						}
					}
					catch (Exception ex2)
					{
						Plugin instance = Plugin.Instance;
						if (instance != null)
						{
							ManualLogSource log = ((BasePlugin)instance).Log;
							if (log != null)
							{
								BepInExWarningLogInterpolatedStringHandler val2 = new BepInExWarningLogInterpolatedStringHandler(43, 1, ref flag);
								if (flag)
								{
									((BepInExLogInterpolatedStringHandler)val2).AppendLiteral("[LAN Mod] Error extinguishing puppet fire: ");
									((BepInExLogInterpolatedStringHandler)val2).AppendFormatted<string>(ex2.Message);
								}
								log.LogWarning(val2);
							}
						}
					}
				}
			}
			_targetPosition = pos;
			_targetRotation = rot;
			_targetVelocity = vel;
			_targetAngularVelocity = angVel;
			_targetAimPoint = aim;
			_hasReceivedState = true;
			if ((Object)(object)_remoteRigidbody != (Object)null && ((Il2CppObjectBase)_remoteRigidbody).Pointer != IntPtr.Zero && Vector3.Distance(_remoteRigidbody.position, pos) > 35f)
			{
				_remoteRigidbody.position = pos;
				_remoteRigidbody.rotation = rot;
			}
		}
		else if (reader.Type == PacketType.Fire)
		{
			reader.ReadUInt();
			int weaponSlot = reader.ReadInt();
			reader.ReadVector3();
			Vector3 fireDir = reader.ReadVector3();
			int projTypeId = ((reader.RemainingBytes >= 4) ? reader.ReadInt() : 0);
			TriggerRemoteFiring(weaponSlot, fireDir, projTypeId);
		}
		else if (reader.Type == PacketType.Respawn)
		{
			reader.ReadUInt();
			reader.ReadFloat();
			Vector3 spawnPos = reader.ReadVector3();
			Quaternion spawnRot = reader.ReadQuaternion();
			OnRemotePlayerRespawned(spawnPos, spawnRot);
		}
		else if (reader.Type == PacketType.TankDestroyed)
		{
			string victimName = reader.ReadString();
			reader.ReadBool();
			Plugin instance = Plugin.Instance;
			if (instance != null)
			{
				ManualLogSource log = ((BasePlugin)instance).Log;
				if (log != null)
				{
					BepInExInfoLogInterpolatedStringHandler val = new BepInExInfoLogInterpolatedStringHandler(45, 1, ref flag);
					if (flag)
					{
						((BepInExLogInterpolatedStringHandler)val).AppendLiteral("[LAN Mod] Received TankDestroyed packet for: ");
						((BepInExLogInterpolatedStringHandler)val).AppendFormatted<string>(victimName);
					}
					log.LogInfo(val);
				}
			}
			if (!_remoteDestructionHandled)
			{
				if (IsValid((Component)(object)RemoteVehicle))
				{
					OnVehicleDestroyed(RemoteVehicle, isLocal: false, MultiplayerController.Instance?.Transport);
					return;
				}
				_remoteWasDestroyed = true;
				_remoteDestructionHandled = true;
				LANManagerGUI.Instance?.ShowDestructionIndicator(victimName, isKillConfirmation: true);
				MultiplayerController.Instance?.ShowBanner("[LAN Mod] Target Destroyed: " + victimName + "!");
			}
		}
		else if (reader.Type == PacketType.Hit)
		{
			string hitComp = reader.ReadString();
			float hitDmg = reader.ReadFloat();
			bool hitDead = reader.ReadBool();
			Plugin instance = Plugin.Instance;
			if (instance != null)
			{
				ManualLogSource log = ((BasePlugin)instance).Log;
				if (log != null)
				{
					BepInExInfoLogInterpolatedStringHandler val = new BepInExInfoLogInterpolatedStringHandler(56, 3, ref flag);
					if (flag)
					{
						((BepInExLogInterpolatedStringHandler)val).AppendLiteral("[LAN Mod] Received Hit confirmation: ");
						((BepInExLogInterpolatedStringHandler)val).AppendFormatted<string>(hitComp);
						((BepInExLogInterpolatedStringHandler)val).AppendLiteral(" (-");
						((BepInExLogInterpolatedStringHandler)val).AppendFormatted<float>(hitDmg, "F0");
						((BepInExLogInterpolatedStringHandler)val).AppendLiteral(" HP, Destroyed=");
						((BepInExLogInterpolatedStringHandler)val).AppendFormatted<bool>(hitDead);
						((BepInExLogInterpolatedStringHandler)val).AppendLiteral(")");
					}
					log.LogInfo(val);
				}
			}
			LANManagerGUI.Instance?.RegisterOpponentDamage(hitComp, hitDead, hitDmg);
		}
		else
		{
			if (reader.Type != PacketType.BattleReset)
			{
				return;
			}
			Vector3 peerPos = reader.ReadVector3();
			Quaternion peerRot = reader.ReadQuaternion();
			Plugin instance = Plugin.Instance;
			if (instance != null)
			{
				ManualLogSource log = ((BasePlugin)instance).Log;
				if (log != null)
				{
					BepInExInfoLogInterpolatedStringHandler val = new BepInExInfoLogInterpolatedStringHandler(59, 1, ref flag);
					if (flag)
					{
						((BepInExLogInterpolatedStringHandler)val).AppendLiteral("[LAN Mod] Received BattleReset packet from peer. Peer pos: ");
						((BepInExLogInterpolatedStringHandler)val).AppendFormatted<Vector3>(peerPos);
					}
					log.LogInfo(val);
				}
			}
			OnPeerBattleResetReceived(peerPos, peerRot);
		}
	}

	public void TriggerRemoteFiring(int slot = -1, Vector3 fireDir = default(Vector3), int projTypeId = 0)
	{
		//IL_00a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00aa: Unknown result type (might be due to invalid IL or missing references)
		//IL_017e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0185: Expected Obj, but got Unknown
		if ((Object)(object)RemoteVehicle == (Object)null || ((Il2CppObjectBase)RemoteVehicle).Pointer == IntPtr.Zero)
		{
			FindVehiclesIfNeeded();
			if ((Object)(object)RemoteVehicle == (Object)null || ((Il2CppObjectBase)RemoteVehicle).Pointer == IntPtr.Zero)
			{
				return;
			}
		}
		if (_remoteWasDestroyed)
		{
			_remoteWasDestroyed = false;
			RepairAndRestoreVehicle(RemoteVehicle);
			CacheRemoteVehicleComponents();
			NeutralizeAIOnRemoteVehicle(RemoteVehicle);
			_remoteCannonsLastFireTime.Clear();
		}
		if (_remoteCannons.Count == 0)
		{
			CacheRemoteVehicleComponents();
		}
		VehicleBehaviour remoteVehicle = RemoteVehicle;
		remoteVehicle.Flags = (VehicleFlags)(remoteVehicle.Flags | 0x21);
		int debounceKey = ((slot >= 0) ? slot : (-1));
		if (_remoteCannonsLastFireTime.TryGetValue(debounceKey, out var lastFireTime) && Time.time - lastFireTime < 0.12f)
		{
			return;
		}
		_remoteCannonsLastFireTime[debounceKey] = Time.time;
		ApplyRemoteAimingMechanisms(1f);
		bool fired = false;
		IsTriggeringRemoteFiring = true;
		try
		{
			if (slot >= 0 && slot < _remoteCannons.Count)
			{
				fired = FireRemoteCannon(_remoteCannons[slot], slot, projTypeId);
			}
			else
			{
				for (int i = 0; i < _remoteCannons.Count; i++)
				{
					if (FireRemoteCannon(_remoteCannons[i], i, projTypeId))
					{
						fired = true;
					}
				}
			}
		}
		finally
		{
			IsTriggeringRemoteFiring = false;
		}
		if (!fired)
		{
			return;
		}
		Plugin instance = Plugin.Instance;
		if (instance == null)
		{
			return;
		}
		ManualLogSource log = ((BasePlugin)instance).Log;
		if (log != null)
		{
			bool flag = default;
			BepInExInfoLogInterpolatedStringHandler val = new BepInExInfoLogInterpolatedStringHandler(56, 1, ref flag);
			if (flag)
			{
				((BepInExLogInterpolatedStringHandler)val).AppendLiteral("[LAN Mod] Remote cannon slot ");
				((BepInExLogInterpolatedStringHandler)val).AppendFormatted<string>((slot >= 0) ? slot.ToString() : "ALL");
				((BepInExLogInterpolatedStringHandler)val).AppendLiteral(" fired physical projectile!");
			}
			log.LogInfo(val);
		}
	}

	private bool FireRemoteCannon(Cannon cannon, int slot, int peerProjTypeId = 0)
	{
		//IL_007e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0083: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b8: Unknown result type (might be due to invalid IL or missing references)
		//IL_01cf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c1: Unknown result type (might be due to invalid IL or missing references)
		//IL_01dd: Unknown result type (might be due to invalid IL or missing references)
		//IL_02ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_02b5: Expected Obj, but got Unknown
		//IL_0285: Unknown result type (might be due to invalid IL or missing references)
		//IL_0230: Unknown result type (might be due to invalid IL or missing references)
		//IL_0237: Expected Obj, but got Unknown
		//IL_03ef: Unknown result type (might be due to invalid IL or missing references)
		//IL_03f6: Expected Obj, but got Unknown
		//IL_00d3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d8: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c9: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e5: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_0204: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f4: Unknown result type (might be due to invalid IL or missing references)
		//IL_0302: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_0165: Unknown result type (might be due to invalid IL or missing references)
		//IL_0178: Unknown result type (might be due to invalid IL or missing references)
		//IL_017d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0197: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)cannon == (Object)null || ((Il2CppObjectBase)cannon).Pointer == IntPtr.Zero || cannon.Weapon == null || ((Il2CppObjectBase)cannon.Weapon).Pointer == IntPtr.Zero)
		{
			return false;
		}
		try
		{
			((Behaviour)cannon).enabled = true;
			cannon.Weapon.Functioning = true;
			cannon.Weapon.NeedsReload = false;
			cannon.Weapon.salvoFired = false;
			try
			{
				CannonBehaviour behaviour = cannon.Behaviour;
				if (behaviour != null)
				{
					behaviour.ResetRecoil();
				}
			}
			catch
			{
			}
			ProjectileTypeID projToFire = ProjectileTypeID.Invalid;
			if (slot >= 0 && slot < _remoteCannonProjectileIDs.Count && _remoteCannonProjectileIDs[slot].Value != 0)
			{
				projToFire = _remoteCannonProjectileIDs[slot];
			}
			else if (cannon.Weapon.LoadedProjectileTypeID.Value != 0)
			{
				projToFire = cannon.Weapon.LoadedProjectileTypeID;
				if (slot >= 0 && slot < _remoteCannonProjectileIDs.Count)
				{
					_remoteCannonProjectileIDs[slot] = projToFire;
				}
			}
			else if ((Object)(object)RemoteVehicle != (Object)null)
			{
				try
				{
					Il2CppArrayBase<AmmoRack> racks = ((Component)RemoteVehicle).GetComponentsInChildren<AmmoRack>(true);
					if (racks != null)
					{
						foreach (AmmoRack r in racks)
						{
							if ((Object)(object)r != (Object)null && ((Il2CppObjectBase)r).Pointer != IntPtr.Zero && r.behaviour != null && r.behaviour.StoredTypeID.Value != 0)
							{
								projToFire = r.behaviour.StoredTypeID;
								if (slot >= 0 && slot < _remoteCannonProjectileIDs.Count)
								{
									_remoteCannonProjectileIDs[slot] = projToFire;
								}
								break;
							}
						}
					}
				}
				catch
				{
				}
			}
			if (projToFire.Value == 0 && peerProjTypeId != 0)
			{
				try
				{
					projToFire = new ProjectileTypeID(peerProjTypeId);
				}
				catch
				{
				}
			}
			if (projToFire.Value != 0)
			{
				cannon.Weapon.LoadedProjectileTypeID = projToFire;
			}
			bool shotFired = false;
			try
			{
				if (projToFire.Value != 0)
				{
					cannon.Weapon.FireInternal(projToFire);
					shotFired = true;
				}
				else
				{
					cannon.Weapon.Fire();
					shotFired = true;
				}
			}
			catch (Exception ex)
			{
				Plugin instance = Plugin.Instance;
				bool flag = default;
				if (instance != null)
				{
					ManualLogSource log = ((BasePlugin)instance).Log;
					if (log != null)
					{
						BepInExWarningLogInterpolatedStringHandler val = new BepInExWarningLogInterpolatedStringHandler(60, 2, ref flag);
						if (flag)
						{
							((BepInExLogInterpolatedStringHandler)val).AppendLiteral("Remote cannon slot ");
							((BepInExLogInterpolatedStringHandler)val).AppendFormatted<int>(slot);
							((BepInExLogInterpolatedStringHandler)val).AppendLiteral(" FireInternal note (");
							((BepInExLogInterpolatedStringHandler)val).AppendFormatted<string>(ex.Message);
							((BepInExLogInterpolatedStringHandler)val).AppendLiteral(") - attempting Fire()");
						}
						log.LogWarning(val);
					}
				}
				try
				{
					cannon.Weapon.Fire();
					shotFired = true;
				}
				catch (Exception ex2)
				{
					instance = Plugin.Instance;
					if (instance != null)
					{
						ManualLogSource log = ((BasePlugin)instance).Log;
						if (log != null)
						{
							BepInExWarningLogInterpolatedStringHandler val = new BepInExWarningLogInterpolatedStringHandler(31, 2, ref flag);
							if (flag)
							{
								((BepInExLogInterpolatedStringHandler)val).AppendLiteral("Remote cannon slot ");
								((BepInExLogInterpolatedStringHandler)val).AppendFormatted<int>(slot);
								((BepInExLogInterpolatedStringHandler)val).AppendLiteral(" Fire note: ");
								((BepInExLogInterpolatedStringHandler)val).AppendFormatted<string>(ex2.Message);
							}
							log.LogWarning(val);
						}
					}
				}
			}
			if (projToFire.Value != 0)
			{
				cannon.Weapon.LoadedProjectileTypeID = projToFire;
			}
			cannon.Weapon.Functioning = true;
			cannon.Weapon.NeedsReload = false;
			cannon.Weapon.salvoFired = false;
			try
			{
				CannonBehaviour behaviour2 = cannon.Behaviour;
				if (behaviour2 != null)
				{
					behaviour2.ResetRecoil();
				}
			}
			catch
			{
			}
			if ((Object)(object)RemoteVehicle != (Object)null)
			{
				try
				{
					Il2CppArrayBase<AmmoRack> racks2 = ((Component)RemoteVehicle).GetComponentsInChildren<AmmoRack>(true);
					if (racks2 != null)
					{
						foreach (AmmoRack r2 in racks2)
						{
							if ((Object)(object)r2 != (Object)null && ((Il2CppObjectBase)r2).Pointer != IntPtr.Zero && r2.behaviour != null)
							{
								r2.behaviour.amountStored = r2.behaviour.Capacity;
							}
						}
					}
				}
				catch
				{
				}
			}
			return shotFired;
		}
		catch (Exception ex3)
		{
			Plugin instance = Plugin.Instance;
			if (instance != null)
			{
				ManualLogSource log = ((BasePlugin)instance).Log;
				if (log != null)
				{
					bool flag2 = default;
					BepInExWarningLogInterpolatedStringHandler val = new BepInExWarningLogInterpolatedStringHandler(32, 2, ref flag2);
					if (flag2)
					{
						((BepInExLogInterpolatedStringHandler)val).AppendLiteral("Remote cannon slot ");
						((BepInExLogInterpolatedStringHandler)val).AppendFormatted<int>(slot);
						((BepInExLogInterpolatedStringHandler)val).AppendLiteral(" fire error: ");
						((BepInExLogInterpolatedStringHandler)val).AppendFormatted<string>(ex3.Message);
					}
					log.LogWarning(val);
				}
			}
			return false;
		}
	}

	private void UpdateVehicleStats()
	{
		//IL_00b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_013b: Unknown result type (might be due to invalid IL or missing references)
		//IL_02e2: Unknown result type (might be due to invalid IL or missing references)
		//IL_0274: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			if (!IsValid((Component)(object)LocalVehicle))
			{
				LocalVehicle = null;
				LocalController = null;
				_localCannons.Clear();
				_localTraverseMotors.Clear();
				_localLayingDrives.Clear();
			}
			if (!IsValid((Component)(object)RemoteVehicle))
			{
				RemoteVehicle = null;
				RemoteController = null;
				_remoteRigidbody = null;
				_remoteCannons.Clear();
				_remoteTraverseMotors.Clear();
				_remoteLayingDrives.Clear();
				_remoteAimablePointers.Clear();
			}
			if ((Object)(object)LocalVehicle != (Object)null && (Object)(object)RemoteVehicle != (Object)null)
			{
				DistanceBetweenTanks = Vector3.Distance(((Component)LocalVehicle).transform.position, ((Component)RemoteVehicle).transform.position);
			}
			if (_localTraverseMotors.Count > 0)
			{
				TraverseMotor motor = _localTraverseMotors[0];
				if ((Object)(object)motor != (Object)null && ((Il2CppObjectBase)motor).Pointer != IntPtr.Zero && (Object)(object)motor.ControlledTransform != (Object)null && ((Il2CppObjectBase)motor.ControlledTransform).Pointer != IntPtr.Zero)
				{
					LocalTurretYaw = ((Component)motor.ControlledTransform).transform.localEulerAngles.y;
					if (LocalTurretYaw > 180f)
					{
						LocalTurretYaw -= 360f;
					}
				}
			}
			if (_localCannons.Count > 0)
			{
				Cannon cannon = _localCannons[0];
				if ((Object)(object)cannon != (Object)null && ((Il2CppObjectBase)cannon).Pointer != IntPtr.Zero)
				{
					LocalGunPitch = ((Component)cannon).transform.localEulerAngles.x;
					if (LocalGunPitch > 180f)
					{
						LocalGunPitch -= 360f;
					}
					LocalWeaponReady = cannon.Weapon != null && ((Il2CppObjectBase)cannon.Weapon).Pointer != IntPtr.Zero && cannon.Weapon.CanFire;
				}
			}
			if (_remoteTraverseMotors.Count > 0)
			{
				TraverseMotor motor2 = _remoteTraverseMotors[0];
				if ((Object)(object)motor2 != (Object)null && ((Il2CppObjectBase)motor2).Pointer != IntPtr.Zero && (Object)(object)motor2.ControlledTransform != (Object)null && ((Il2CppObjectBase)motor2.ControlledTransform).Pointer != IntPtr.Zero)
				{
					RemoteTurretYaw = ((Component)motor2.ControlledTransform).transform.localEulerAngles.y;
					if (RemoteTurretYaw > 180f)
					{
						RemoteTurretYaw -= 360f;
					}
				}
			}
			if (_remoteCannons.Count <= 0)
			{
				return;
			}
			Cannon cannon2 = _remoteCannons[0];
			if ((Object)(object)cannon2 != (Object)null && ((Il2CppObjectBase)cannon2).Pointer != IntPtr.Zero)
			{
				RemoteGunPitch = ((Component)cannon2).transform.localEulerAngles.x;
				if (RemoteGunPitch > 180f)
				{
					RemoteGunPitch -= 360f;
				}
				RemoteWeaponReady = cannon2.Weapon != null && ((Il2CppObjectBase)cannon2.Weapon).Pointer != IntPtr.Zero && cannon2.Weapon.CanFire;
			}
		}
		catch
		{
		}
	}

	public static string CleanVehicleName(VehicleBehaviour vehicle)
	{
		if ((Object)(object)vehicle == (Object)null || ((Il2CppObjectBase)vehicle).Pointer == IntPtr.Zero || (Object)(object)((Component)vehicle).gameObject == (Object)null)
		{
			return "Tank";
		}
		try
		{
			string name = ((Object)((Component)vehicle).gameObject).name;
			if (name.EndsWith("(Clone)"))
			{
				name = name.Substring(0, name.Length - 7);
			}
			name = name.Replace('_', ' ').Trim();
			return string.IsNullOrEmpty(name) ? "Tank" : name;
		}
		catch
		{
			return "Tank";
		}
	}

	public void OnAmmoRackDetonated(AmmoRackBehaviour behaviour)
	{
		if (behaviour == null || ((Il2CppObjectBase)behaviour).Pointer == IntPtr.Zero)
		{
			return;
		}
		try
		{
			Plugin instance = Plugin.Instance;
			if (instance != null)
			{
				ManualLogSource log = ((BasePlugin)instance).Log;
				if (log != null)
				{
					log.LogInfo((object)"[LAN Mod] Ammo rack detonated - native flameout and cookoff running.");
				}
			}
		}
		catch (Exception ex)
		{
			Plugin instance2 = Plugin.Instance;
			if (instance2 != null)
			{
				ManualLogSource log2 = ((BasePlugin)instance2).Log;
				if (log2 != null)
				{
					log2.LogWarning((object)("OnAmmoRackDetonated error: " + ex.Message));
				}
			}
		}
	}

	public bool IsVehicleDestroyed(VehicleBehaviour vehicle)
	{
		//IL_009b: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cb: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)vehicle == (Object)null || ((Il2CppObjectBase)vehicle).Pointer == IntPtr.Zero)
		{
			return true;
		}
		try
		{
			if (!((Component)vehicle).gameObject.activeInHierarchy)
			{
				return true;
			}
			bool num = (Object)(object)LocalVehicle != (Object)null && ((Il2CppObjectBase)vehicle).Pointer == ((Il2CppObjectBase)LocalVehicle).Pointer;
			bool isRemoteVeh = (Object)(object)RemoteVehicle != (Object)null && ((Il2CppObjectBase)vehicle).Pointer == ((Il2CppObjectBase)RemoteVehicle).Pointer;
			if (num)
			{
				if (Time.time - _localVehicleSpawnTime < 3f)
				{
					return false;
				}
				if ((vehicle.Flags & 1) == 0)
				{
					return true;
				}
				if (IsCrewEliminated(vehicle))
				{
					return true;
				}
			}
			else if (isRemoteVeh)
			{
				if (_remoteWasDestroyed)
				{
					return true;
				}
				if ((vehicle.Flags & 1) == 0)
				{
					return true;
				}
				if (IsCrewEliminated(vehicle))
				{
					return true;
				}
			}
		}
		catch
		{
		}
		return false;
	}

	public bool IsCrewEliminated(VehicleBehaviour vehicle)
	{
		//IL_01fe: Unknown result type (might be due to invalid IL or missing references)
		//IL_0203: Unknown result type (might be due to invalid IL or missing references)
		//IL_02d5: Unknown result type (might be due to invalid IL or missing references)
		//IL_02dc: Expected Obj, but got Unknown
		//IL_020e: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)vehicle == (Object)null || ((Il2CppObjectBase)vehicle).Pointer == IntPtr.Zero)
		{
			return true;
		}
		try
		{
			if ((Object)(object)LocalVehicle != (Object)null && ((Il2CppObjectBase)vehicle).Pointer == ((Il2CppObjectBase)LocalVehicle).Pointer && Time.time - _localVehicleSpawnTime < 3f)
			{
				return false;
			}
			if ((Object)(object)LocalController != (Object)null && ((Il2CppObjectBase)LocalController).Pointer != IntPtr.Zero && (Object)(object)LocalVehicle != (Object)null && ((Il2CppObjectBase)vehicle).Pointer == ((Il2CppObjectBase)LocalVehicle).Pointer && LocalController.currentTask != null && LocalController.abandonTask != null && ((Il2CppObjectBase)LocalController.currentTask).Pointer != IntPtr.Zero && ((Il2CppObjectBase)LocalController.currentTask).Pointer == ((Il2CppObjectBase)LocalController.abandonTask).Pointer)
			{
				Plugin instance = Plugin.Instance;
				if (instance != null)
				{
					ManualLogSource log = ((BasePlugin)instance).Log;
					if (log != null)
					{
						log.LogInfo((object)"[LAN Mod] Tank abandoned by player task!");
					}
				}
				return true;
			}
			Il2CppArrayBase<CrewSeat> seats = ((Component)vehicle).GetComponentsInChildren<CrewSeat>(true);
			if (seats != null && seats.Length > 0)
			{
				int totalSeats = 0;
				int aliveCrew = 0;
				foreach (CrewSeat seat in seats)
				{
					if ((Object)(object)seat == (Object)null || ((Il2CppObjectBase)seat).Pointer == IntPtr.Zero)
					{
						continue;
					}
					totalSeats++;
					bool isSeatAlive = true;
					if (seat.healthPool != null && ((Il2CppObjectBase)seat.healthPool).Pointer != IntPtr.Zero)
					{
						HealthPool pool = ((Il2CppObjectBase)seat.healthPool).TryCast<HealthPool>();
						if (pool != null)
						{
							if (!pool.Alive || pool.current <= 0.01f || pool.Fraction <= 0.01f)
							{
								isSeatAlive = false;
							}
						}
						else
						{
							HealthInfo hi = seat.healthPool.HealthInfo;
							if (!hi.Alive || hi.Current <= 0.01f || hi.Fraction <= 0.01f)
							{
								isSeatAlive = false;
							}
						}
					}
					if (((VehicleComponent)seat).HealthFraction <= 0.01f)
					{
						isSeatAlive = false;
					}
					OperatorSeat op = seat.Operator;
					if (op != null && ((Il2CppObjectBase)op).Pointer != IntPtr.Zero)
					{
						IVehicleOperatorBehaviour behav = op.ActiveBehaviour;
						if (behav != null && ((Il2CppObjectBase)behav).Pointer != IntPtr.Zero && behav.HealthFraction <= 0.01f)
						{
							isSeatAlive = false;
						}
					}
					if (isSeatAlive)
					{
						aliveCrew++;
					}
				}
				if (totalSeats > 0 && aliveCrew == 0)
				{
					Plugin instance2 = Plugin.Instance;
					if (instance2 != null)
					{
						ManualLogSource log2 = ((BasePlugin)instance2).Log;
						if (log2 != null)
						{
							bool flag = default;
							BepInExInfoLogInterpolatedStringHandler val = new BepInExInfoLogInterpolatedStringHandler(67, 2, ref flag);
							if (flag)
							{
								((BepInExLogInterpolatedStringHandler)val).AppendLiteral("[LAN Mod] All CrewSeat components eliminated on ");
								GameObject gameObject = ((Component)vehicle).gameObject;
								((BepInExLogInterpolatedStringHandler)val).AppendFormatted<string>((gameObject != null) ? ((Object)gameObject).name : null);
								((BepInExLogInterpolatedStringHandler)val).AppendLiteral(" (total=");
								((BepInExLogInterpolatedStringHandler)val).AppendFormatted<int>(totalSeats);
								((BepInExLogInterpolatedStringHandler)val).AppendLiteral(", alive=0)!");
							}
							log2.LogInfo(val);
						}
					}
					return true;
				}
				if (aliveCrew > 0)
				{
					return false;
				}
			}
		}
		catch
		{
		}
		return false;
	}

	private void OnVehicleDestroyed(VehicleBehaviour vehicle, bool isLocal, UdpNetworkTransport transport)
	{
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0074: Expected Obj, but got Unknown
		if ((Object)(object)vehicle == (Object)null || ((Il2CppObjectBase)vehicle).Pointer == IntPtr.Zero)
		{
			return;
		}
		if (isLocal)
		{
			if (_localDestructionHandled)
			{
				return;
			}
			_localDestructionHandled = true;
			_localWasDestroyed = true;
		}
		else
		{
			if (_remoteDestructionHandled)
			{
				return;
			}
			_remoteDestructionHandled = true;
			_remoteWasDestroyed = true;
		}
		string name = CleanVehicleName(vehicle);
		Plugin instance = Plugin.Instance;
		if (instance != null)
		{
			ManualLogSource log = ((BasePlugin)instance).Log;
			if (log != null)
			{
				bool flag = default;
				BepInExInfoLogInterpolatedStringHandler val = new BepInExInfoLogInterpolatedStringHandler(40, 2, ref flag);
				if (flag)
				{
					((BepInExLogInterpolatedStringHandler)val).AppendLiteral("[LAN Mod] Vehicle destroyed: ");
					((BepInExLogInterpolatedStringHandler)val).AppendFormatted<string>(name);
					((BepInExLogInterpolatedStringHandler)val).AppendLiteral(" (isLocal=");
					((BepInExLogInterpolatedStringHandler)val).AppendFormatted<bool>(isLocal);
					((BepInExLogInterpolatedStringHandler)val).AppendLiteral(")");
				}
				log.LogInfo(val);
			}
		}
		LANManagerGUI.Instance?.ShowDestructionIndicator(name, !isLocal);
		MultiplayerController.Instance?.ShowBanner(isLocal ? "[LAN Mod] Your tank was destroyed!" : ("[LAN Mod] Target Destroyed: " + name + "!"));
		if (isLocal && transport != null && transport.IsConnected)
		{
			PacketWriter pw = new PacketWriter(PacketType.TankDestroyed);
			pw.Write(name);
			pw.Write(value: true);
			transport.Send(pw.ToArray());
		}
	}

	public void TriggerLocalVehicleDestroyed(string reason = "Tank Destroyed")
	{
		//IL_00b8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bf: Expected Obj, but got Unknown
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_007a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0080: Expected Obj, but got Unknown
		if ((Object)(object)LocalVehicle == (Object)null || ((Il2CppObjectBase)LocalVehicle).Pointer == IntPtr.Zero || _localDestructionHandled)
		{
			return;
		}
		bool flag = default;
		try
		{
			VehicleBehaviour localVehicle = LocalVehicle;
			localVehicle.Flags = (VehicleFlags)(localVehicle.Flags & -2);
			OnVehicleDestroyed(LocalVehicle, isLocal: true, MultiplayerController.Instance?.Transport);
			Plugin instance = Plugin.Instance;
			if (instance == null)
			{
				return;
			}
			ManualLogSource log = ((BasePlugin)instance).Log;
			if (log != null)
			{
				BepInExInfoLogInterpolatedStringHandler val = new BepInExInfoLogInterpolatedStringHandler(49, 1, ref flag);
				if (flag)
				{
					((BepInExLogInterpolatedStringHandler)val).AppendLiteral("[LAN Mod] TriggerLocalVehicleDestroyed executed: ");
					((BepInExLogInterpolatedStringHandler)val).AppendFormatted<string>(reason);
				}
				log.LogInfo(val);
			}
		}
		catch (Exception ex)
		{
			Plugin instance = Plugin.Instance;
			if (instance == null)
			{
				return;
			}
			ManualLogSource log = ((BasePlugin)instance).Log;
			if (log != null)
			{
				BepInExWarningLogInterpolatedStringHandler val2 = new BepInExWarningLogInterpolatedStringHandler(36, 1, ref flag);
				if (flag)
				{
					((BepInExLogInterpolatedStringHandler)val2).AppendLiteral("TriggerLocalVehicleDestroyed error: ");
					((BepInExLogInterpolatedStringHandler)val2).AppendFormatted<string>(ex.Message);
				}
				log.LogWarning(val2);
			}
		}
	}

	public void ExtinguishVehicle(VehicleBehaviour vehicle)
	{
		//IL_0080: Unknown result type (might be due to invalid IL or missing references)
		//IL_0085: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)vehicle == (Object)null || ((Il2CppObjectBase)vehicle).Pointer == IntPtr.Zero)
		{
			return;
		}
		try
		{
			vehicle.Flags = (VehicleFlags)(vehicle.Flags & -65);
			try
			{
				FireControlVehicleModule fm = default;
				if (vehicle.TryGetModule<FireControlVehicleModule>(ref fm) && fm != null)
				{
					try
					{
						FireControl fireControl = fm.fireControl;
						if (fireControl != null)
						{
							fireControl.Extinguish();
						}
					}
					catch
					{
					}
					try
					{
						fm.ManualExtinguishInProgress = false;
					}
					catch
					{
					}
				}
			}
			catch
			{
			}
			try
			{
				FlameoutControlVehicleModule foc = default;
				if (vehicle.TryGetModule<FlameoutControlVehicleModule>(ref foc) && foc != null)
				{
					try
					{
						foc.flameoutVisible = false;
					}
					catch
					{
					}
					try
					{
						FlameoutSimulation sim = foc.sim;
						sim.Burning = 0f;
						sim.InternalPressure = 0f;
						sim.Unburnt = 0f;
						sim.Burnt = 0f;
						foc.sim = sim;
					}
					catch
					{
					}
				}
			}
			catch
			{
			}
			try
			{
				Il2CppArrayBase<FireEffect> fireEffects = ((Component)vehicle).GetComponentsInChildren<FireEffect>(true);
				if (fireEffects != null)
				{
					foreach (FireEffect fe in fireEffects)
					{
						if (!((Object)(object)fe != (Object)null) || !(((Il2CppObjectBase)fe).Pointer != IntPtr.Zero))
						{
							continue;
						}
						try
						{
							((Effect)fe).Stop();
						}
						catch
						{
						}
						try
						{
							((Effect)fe).OnEffectEnd();
						}
						catch
						{
						}
						try
						{
							if ((Object)(object)fe.visualEffect != (Object)null)
							{
								fe.visualEffect.Stop();
							}
						}
						catch
						{
						}
					}
				}
			}
			catch
			{
			}
			try
			{
				Il2CppArrayBase<VisualEffect> vfxList = ((Component)vehicle).GetComponentsInChildren<VisualEffect>(true);
				if (vfxList != null)
				{
					foreach (VisualEffect vfx in vfxList)
					{
						if ((Object)(object)vfx != (Object)null && ((Il2CppObjectBase)vfx).Pointer != IntPtr.Zero)
						{
							try
							{
								vfx.Stop();
							}
							catch
							{
							}
							try
							{
								vfx.Reinit();
							}
							catch
							{
							}
							try
							{
								vfx.pause = true;
							}
							catch
							{
							}
							try
							{
								((Behaviour)vfx).enabled = false;
							}
							catch
							{
							}
						}
					}
				}
			}
			catch
			{
			}
			Il2CppArrayBase<FuelTank> fuelTanks = ((Component)vehicle).GetComponentsInChildren<FuelTank>(true);
			if (fuelTanks != null)
			{
				foreach (FuelTank ft in fuelTanks)
				{
					if ((Object)(object)ft != (Object)null && ((Il2CppObjectBase)ft).Pointer != IntPtr.Zero && ft.behaviour != null)
					{
						ft.behaviour.fireStarted = false;
					}
				}
			}
			Il2CppArrayBase<ParticleSystem> particles = ((Component)vehicle).GetComponentsInChildren<ParticleSystem>(true);
			if (particles != null)
			{
				foreach (ParticleSystem ps in particles)
				{
					if (!((Object)(object)ps != (Object)null) || !(((Il2CppObjectBase)ps).Pointer != IntPtr.Zero))
					{
						continue;
					}
					string pName = (((Object)(object)((Component)ps).gameObject != (Object)null) ? ((Object)((Component)ps).gameObject).name.ToLowerInvariant() : "");
					if (pName.Contains("fire") || pName.Contains("flame") || pName.Contains("burn") || pName.Contains("smoke"))
					{
						try
						{
							ps.Stop(true, (ParticleSystemStopBehavior)0);
						}
						catch
						{
						}
					}
				}
			}
			Il2CppArrayBase<AudioSource> audios = ((Component)vehicle).GetComponentsInChildren<AudioSource>(true);
			if (audios == null)
			{
				return;
			}
			foreach (AudioSource audio in audios)
			{
				if (!((Object)(object)audio != (Object)null) || !(((Il2CppObjectBase)audio).Pointer != IntPtr.Zero))
				{
					continue;
				}
				string aName = (((Object)(object)((Component)audio).gameObject != (Object)null) ? ((Object)((Component)audio).gameObject).name.ToLowerInvariant() : "");
				if (aName.Contains("fire") || aName.Contains("flame") || aName.Contains("burn") || aName.Contains("cookoff"))
				{
					try
					{
						audio.Stop();
					}
					catch
					{
					}
				}
			}
		}
		catch (Exception ex)
		{
			Plugin instance = Plugin.Instance;
			if (instance != null)
			{
				ManualLogSource log = ((BasePlugin)instance).Log;
				if (log != null)
				{
					log.LogWarning((object)("ExtinguishVehicle error: " + ex.Message));
				}
			}
		}
	}

	public void RepairAndRestoreVehicle(VehicleBehaviour vehicle)
	{
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_004e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0fe2: Unknown result type (might be due to invalid IL or missing references)
		//IL_0fe9: Expected Obj, but got Unknown
		//IL_0f88: Unknown result type (might be due to invalid IL or missing references)
		//IL_0f8f: Expected Obj, but got Unknown
		//IL_0d4d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0d5d: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)vehicle == (Object)null || ((Il2CppObjectBase)vehicle).Pointer == IntPtr.Zero)
		{
			return;
		}
		bool flag = default;
		try
		{
			try
			{
				((Component)vehicle).gameObject.SetActive(true);
			}
			catch
			{
			}
			try
			{
				((Behaviour)vehicle).enabled = true;
			}
			catch
			{
			}
			ExtinguishVehicle(vehicle);
			vehicle.Flags = (VehicleFlags)(vehicle.Flags | 0x129);
			try
			{
				CoupledPartRegister cpr = default;
				if (vehicle.TryGetModule<CoupledPartRegister>(ref cpr) && cpr != null && cpr.decoupledParts != null)
				{
					for (int i = cpr.decoupledParts.Count - 1; i >= 0; i--)
					{
						PartDecoupler pd = cpr.decoupledParts[i];
						if ((Object)(object)pd != (Object)null && ((Il2CppObjectBase)pd).Pointer != IntPtr.Zero)
						{
							try
							{
								pd.Recouple();
							}
							catch
							{
							}
							try
							{
								pd.RecoupleTransform();
							}
							catch
							{
							}
						}
					}
					try
					{
						cpr.decoupledParts.Clear();
					}
					catch
					{
					}
				}
			}
			catch
			{
			}
			try
			{
				Il2CppArrayBase<PartDecoupler> decouplers = ((Component)vehicle).GetComponentsInChildren<PartDecoupler>(true);
				if (decouplers != null)
				{
					foreach (PartDecoupler d in decouplers)
					{
						if ((Object)(object)d != (Object)null && ((Il2CppObjectBase)d).Pointer != IntPtr.Zero)
						{
							try
							{
								d.Recouple();
							}
							catch
							{
							}
							try
							{
								d.RecoupleTransform();
							}
							catch
							{
							}
						}
					}
				}
			}
			catch
			{
			}
			try
			{
				Il2CppArrayBase<PartDecoupler> allDecouplers = Object.FindObjectsOfType<PartDecoupler>();
				if (allDecouplers != null)
				{
					foreach (PartDecoupler d2 in allDecouplers)
					{
						if (!((Object)(object)d2 != (Object)null) || !(((Il2CppObjectBase)d2).Pointer != IntPtr.Zero) || !d2.Decoupled)
						{
							continue;
						}
						try
						{
							if ((Object)(object)d2.parentOnDecouple != (Object)null && (((Il2CppObjectBase)d2.parentOnDecouple).Pointer == ((Il2CppObjectBase)((Component)vehicle).transform).Pointer || d2.parentOnDecouple.IsChildOf(((Component)vehicle).transform)))
							{
								try
								{
									d2.Recouple();
								}
								catch
								{
								}
								try
								{
									d2.RecoupleTransform();
								}
								catch
								{
								}
							}
						}
						catch
						{
						}
					}
				}
			}
			catch
			{
			}
			foreach (Explosive exp in ((Component)vehicle).GetComponentsInChildren<Explosive>(true))
			{
				if ((Object)(object)exp != (Object)null && ((Il2CppObjectBase)exp).Pointer != IntPtr.Zero)
				{
					try
					{
						exp.ResetBehaviour();
					}
					catch
					{
					}
				}
			}
			foreach (AmmoRack ar in ((Component)vehicle).GetComponentsInChildren<AmmoRack>(true))
			{
				if ((Object)(object)ar != (Object)null && ((Il2CppObjectBase)ar).Pointer != IntPtr.Zero && ar.behaviour != null)
				{
					try
					{
						ar.behaviour.FullyLoad();
					}
					catch
					{
					}
					try
					{
						ar.behaviour.amountStored = ar.behaviour.Capacity;
					}
					catch
					{
					}
				}
			}
			VehicleHealthRegister healthRegister = vehicle.healthRegister;
			object obj17 = ((healthRegister != null) ? ((Il2CppObjectBase)healthRegister).TryCast<VehicleHealthRegister>() : null);
			if (obj17 == null)
			{
				IVehicleHealth health = vehicle.Health;
				obj17 = ((health != null) ? ((Il2CppObjectBase)health).TryCast<VehicleHealthRegister>() : null);
				if (obj17 == null)
				{
					IHealthPoolSource healthPool = vehicle.HealthPool;
					obj17 = ((healthPool != null) ? ((Il2CppObjectBase)healthPool).TryCast<VehicleHealthRegister>() : null);
				}
			}
			VehicleHealthRegister vhr = (VehicleHealthRegister)obj17;
			if (vhr != null && ((Il2CppObjectBase)vhr).Pointer != IntPtr.Zero)
			{
				HealthRegister reg = vhr.register;
				if (reg != null && ((Il2CppObjectBase)reg).Pointer != IntPtr.Zero)
				{
					List<HealthPool> poolList = reg.register;
					if (poolList != null)
					{
						for (int j = 0; j < poolList.Count; j++)
						{
							HealthPool pool = poolList[j];
							if (pool != null && ((Il2CppObjectBase)pool).Pointer != IntPtr.Zero)
							{
								try
								{
									pool.released = false;
								}
								catch
								{
								}
								try
								{
									pool.current = ((pool.max > 0f) ? pool.max : 100f);
								}
								catch
								{
								}
								try
								{
									pool.publishedHealthValue = pool.current;
								}
								catch
								{
								}
								try
								{
									pool.hasChanged = true;
								}
								catch
								{
								}
								try
								{
									IDurable val = ((Il2CppObjectBase)pool).Cast<IDurable>();
									HealthUtils.FullyHeal(val);
									HealthUtils.SetHealthFraction(val, 1f);
								}
								catch
								{
								}
							}
						}
					}
					try
					{
						reg.NotifyHealthChanges();
					}
					catch
					{
					}
				}
				List<VehicleComponentHealth> entries = vhr.entries;
				if (entries != null)
				{
					for (int k = 0; k < entries.Count; k++)
					{
						VehicleComponentHealth e = entries[k];
						if (e == null || !(((Il2CppObjectBase)e).Pointer != IntPtr.Zero))
						{
							continue;
						}
						if (e.HealthPool != null && ((Il2CppObjectBase)e.HealthPool).Pointer != IntPtr.Zero)
						{
							HealthPool pool2 = ((Il2CppObjectBase)e.HealthPool).TryCast<HealthPool>();
							if (pool2 != null)
							{
								try
								{
									pool2.released = false;
								}
								catch
								{
								}
								try
								{
									pool2.current = ((pool2.max > 0f) ? pool2.max : 100f);
								}
								catch
								{
								}
								try
								{
									pool2.publishedHealthValue = pool2.current;
								}
								catch
								{
								}
								try
								{
									pool2.hasChanged = true;
								}
								catch
								{
								}
							}
							try
							{
								HealthUtils.FullyHeal(e.HealthPool);
							}
							catch
							{
							}
							try
							{
								HealthUtils.SetHealthFraction(e.HealthPool, 1f);
							}
							catch
							{
							}
						}
						if ((Object)(object)e.Owner != (Object)null && ((Il2CppObjectBase)e.Owner).Pointer != IntPtr.Zero)
						{
							try
							{
								e.Owner.HealthFraction = 1f;
							}
							catch
							{
							}
							try
							{
								e.Owner.RequiresRebuild = true;
							}
							catch
							{
							}
							try
							{
								e.Owner.RequestRebuild();
							}
							catch
							{
							}
						}
					}
				}
			}
			foreach (CrewSeat cs in ((Component)vehicle).GetComponentsInChildren<CrewSeat>(true))
			{
				if (!((Object)(object)cs != (Object)null) || !(((Il2CppObjectBase)cs).Pointer != IntPtr.Zero))
				{
					continue;
				}
				try
				{
					((Behaviour)cs).enabled = true;
				}
				catch
				{
				}
				if (cs.healthPool != null && ((Il2CppObjectBase)cs.healthPool).Pointer != IntPtr.Zero)
				{
					HealthPool pool3 = ((Il2CppObjectBase)cs.healthPool).TryCast<HealthPool>();
					if (pool3 != null)
					{
						try
						{
							pool3.released = false;
						}
						catch
						{
						}
						try
						{
							pool3.current = ((pool3.max > 0f) ? pool3.max : 100f);
						}
						catch
						{
						}
						try
						{
							pool3.publishedHealthValue = pool3.current;
						}
						catch
						{
						}
						try
						{
							pool3.hasChanged = true;
						}
						catch
						{
						}
						try
						{
							pool3.NotifyHealthChanges();
						}
						catch
						{
						}
					}
					try
					{
						HealthUtils.FullyHeal(cs.healthPool);
					}
					catch
					{
					}
					try
					{
						HealthUtils.SetHealthFraction(cs.healthPool, 1f);
					}
					catch
					{
					}
				}
				try
				{
					((VehicleComponent)cs).HealthFraction = 1f;
				}
				catch
				{
				}
				try
				{
					((VehicleComponent)cs).EnableBehaviour((IBehaviourEnableContext)null);
				}
				catch
				{
				}
			}
			foreach (CrewMember cm in ((Component)vehicle).GetComponentsInChildren<CrewMember>(true))
			{
				if (cm != null && ((Il2CppObjectBase)cm).Pointer != IntPtr.Zero)
				{
					try
					{
						cm.HealthFraction = 1f;
					}
					catch
					{
					}
					try
					{
						cm.BaseEfficiency = 1f;
					}
					catch
					{
					}
				}
			}
			foreach (Driver drv in ((Component)vehicle).GetComponentsInChildren<Driver>(true))
			{
				if (drv != null && ((Il2CppObjectBase)drv).Pointer != IntPtr.Zero)
				{
					try
					{
						drv.StartupInstant();
					}
					catch
					{
					}
					try
					{
						drv.ResetControls();
					}
					catch
					{
					}
					try
					{
						drv.HoldingIgnition = false;
					}
					catch
					{
					}
				}
			}
			foreach (PowertrainBehaviour pt in ((Component)vehicle).GetComponentsInChildren<PowertrainBehaviour>(true))
			{
				if (pt != null && ((Il2CppObjectBase)pt).Pointer != IntPtr.Zero)
				{
					try
					{
						pt.functioning = true;
					}
					catch
					{
					}
					try
					{
						pt.DriverFunctioning = true;
					}
					catch
					{
					}
					try
					{
						pt.IgnitionFraction = 1f;
					}
					catch
					{
					}
					try
					{
						pt.ignitionFraction = 1f;
					}
					catch
					{
					}
					try
					{
						pt.standbyToMove = true;
					}
					catch
					{
					}
					try
					{
						pt.stopTime = 0f;
					}
					catch
					{
					}
					try
					{
						pt.noSteerTime = 0f;
					}
					catch
					{
					}
					try
					{
						pt.leftBrakeVelocity = 0f;
					}
					catch
					{
					}
					try
					{
						pt.rightBrakeVelocity = 0f;
					}
					catch
					{
					}
					try
					{
						pt.appliedThrottle = 0f;
					}
					catch
					{
					}
					try
					{
						pt.throttleTarget = 0f;
					}
					catch
					{
					}
					try
					{
						pt.steerTarget = 0f;
					}
					catch
					{
					}
				}
			}
			Il2CppArrayBase<FuelTank> fuelTanks = ((Component)vehicle).GetComponentsInChildren<FuelTank>(true);
			if (fuelTanks != null)
			{
				foreach (FuelTank ft in fuelTanks)
				{
					if (!((Object)(object)ft != (Object)null) || !(((Il2CppObjectBase)ft).Pointer != IntPtr.Zero))
					{
						continue;
					}
					try
					{
						((VehicleComponent)ft).HealthFraction = 1f;
					}
					catch
					{
					}
					if (ft.behaviour != null)
					{
						try
						{
							ft.behaviour.HealthFraction = 1f;
						}
						catch
						{
						}
						try
						{
							ft.behaviour.fireStarted = false;
						}
						catch
						{
						}
						try
						{
							ft.behaviour.Fill();
						}
						catch
						{
						}
						try
						{
							ft.behaviour.SetLeakRate(0f);
						}
						catch
						{
						}
					}
				}
			}
			foreach (TrackAssembly track in ((Component)vehicle).GetComponentsInChildren<TrackAssembly>(true))
			{
				if ((Object)(object)track != (Object)null && ((Il2CppObjectBase)track).Pointer != IntPtr.Zero)
				{
					try
					{
						track.Broken = false;
					}
					catch
					{
					}
					try
					{
						((VehicleComponent)track).HealthFraction = 1f;
					}
					catch
					{
					}
				}
			}
			foreach (WheelMount wm in ((Component)vehicle).GetComponentsInChildren<WheelMount>(true))
			{
				if ((Object)(object)wm != (Object)null && ((Il2CppObjectBase)wm).Pointer != IntPtr.Zero)
				{
					try
					{
						((VehicleComponent)wm).HealthFraction = 1f;
					}
					catch
					{
					}
					try
					{
						wm.RequestTrackRebuild();
					}
					catch
					{
					}
				}
			}
			foreach (Cannon c in ((Component)vehicle).GetComponentsInChildren<Cannon>(true))
			{
				if (!((Object)(object)c != (Object)null) || !(((Il2CppObjectBase)c).Pointer != IntPtr.Zero))
				{
					continue;
				}
				try
				{
					((Behaviour)c).enabled = true;
				}
				catch
				{
				}
				try
				{
					((VehicleComponent)c).HealthFraction = 1f;
				}
				catch
				{
				}
				if (c.Weapon != null && ((Il2CppObjectBase)c.Weapon).Pointer != IntPtr.Zero)
				{
					try
					{
						c.Weapon.Functioning = true;
					}
					catch
					{
					}
					try
					{
						c.Weapon.salvoFired = false;
					}
					catch
					{
					}
					try
					{
						if (c.Weapon.LoadTask != null)
						{
							c.Weapon.LoadTask.LoadInstantly();
						}
						else
						{
							c.Weapon.RequestReload(EventArgs.Empty);
							if (c.Weapon.LoadTask != null)
							{
								c.Weapon.LoadTask.LoadInstantly();
							}
						}
					}
					catch
					{
					}
				}
				CannonBreech breech = ((Component)c).GetComponentInChildren<CannonBreech>(true);
				if ((Object)(object)breech != (Object)null && ((Il2CppObjectBase)breech).Pointer != IntPtr.Zero)
				{
					try
					{
						((Behaviour)breech).enabled = true;
					}
					catch
					{
					}
					try
					{
						((VehicleComponent)breech).HealthFraction = 1f;
					}
					catch
					{
					}
				}
				CannonBarrelModel barrel = ((Component)c).GetComponentInChildren<CannonBarrelModel>(true);
				if ((Object)(object)barrel != (Object)null && ((Il2CppObjectBase)barrel).Pointer != IntPtr.Zero)
				{
					try
					{
						((Behaviour)barrel).enabled = true;
					}
					catch
					{
					}
					try
					{
						barrel.SetHealthFraction(1f);
					}
					catch
					{
					}
				}
			}
			foreach (VehicleComponent comp in ((Component)vehicle).GetComponentsInChildren<VehicleComponent>(true))
			{
				if ((Object)(object)comp != (Object)null && ((Il2CppObjectBase)comp).Pointer != IntPtr.Zero)
				{
					try
					{
						comp.HealthFraction = 1f;
					}
					catch
					{
					}
					try
					{
						comp.RequiresRebuild = true;
					}
					catch
					{
					}
					try
					{
						comp.RequestRebuild();
					}
					catch
					{
					}
				}
			}
			foreach (VehicleObjectModel m in ((Component)vehicle).GetComponentsInChildren<VehicleObjectModel>(true))
			{
				if ((Object)(object)m != (Object)null && ((Il2CppObjectBase)m).Pointer != IntPtr.Zero)
				{
					try
					{
						m.ForceRebuild();
					}
					catch
					{
					}
				}
			}
			if ((Object)(object)vehicle.Rigidbody != (Object)null && ((Il2CppObjectBase)vehicle.Rigidbody).Pointer != IntPtr.Zero)
			{
				vehicle.Rigidbody.isKinematic = false;
				vehicle.Rigidbody.velocity = Vector3.zero;
				vehicle.Rigidbody.angularVelocity = Vector3.zero;
				try
				{
					vehicle.Rigidbody.WakeUp();
				}
				catch
				{
				}
			}
			try
			{
				Il2CppArrayBase<ProjectileImpactMark> allMarks = Object.FindObjectsOfType<ProjectileImpactMark>();
				if (allMarks != null)
				{
					foreach (ProjectileImpactMark mark in allMarks)
					{
						if (!((Object)(object)mark != (Object)null) || !(((Il2CppObjectBase)mark).Pointer != IntPtr.Zero))
						{
							continue;
						}
						try
						{
							if (!((Object)(object)((Component)mark).transform != (Object)null) || !((Component)mark).transform.IsChildOf(((Component)vehicle).transform))
							{
								continue;
							}
							try
							{
								if ((Object)(object)mark.decalProjector != (Object)null)
								{
									((Behaviour)mark.decalProjector).enabled = false;
								}
							}
							catch
							{
							}
							try
							{
								if ((Object)(object)mark.visualEffect != (Object)null)
								{
									mark.visualEffect.Stop();
								}
							}
							catch
							{
							}
							try
							{
								if ((Object)(object)mark.audioSource != (Object)null)
								{
									mark.audioSource.Stop();
								}
							}
							catch
							{
							}
						}
						catch
						{
						}
					}
				}
				Il2CppArrayBase<ProjectileImpactMark> marksOnVeh = ((Component)vehicle).GetComponentsInChildren<ProjectileImpactMark>(true);
				if (marksOnVeh != null)
				{
					foreach (ProjectileImpactMark mark2 in marksOnVeh)
					{
						if (!((Object)(object)mark2 != (Object)null) || !(((Il2CppObjectBase)mark2).Pointer != IntPtr.Zero))
						{
							continue;
						}
						try
						{
							((Effect)mark2).Stop();
						}
						catch
						{
						}
						try
						{
							((Effect)mark2).OnEffectEnd();
						}
						catch
						{
						}
						try
						{
							((Effect)mark2).stopRequested = true;
						}
						catch
						{
						}
						try
						{
							((Effect)mark2).lifeTime = 0f;
						}
						catch
						{
						}
						try
						{
							if ((Object)(object)mark2.decalProjector != (Object)null)
							{
								((Behaviour)mark2.decalProjector).enabled = false;
							}
						}
						catch
						{
						}
						try
						{
							if ((Object)(object)mark2.visualEffect != (Object)null)
							{
								mark2.visualEffect.Stop();
							}
						}
						catch
						{
						}
						try
						{
							if ((Object)(object)mark2.audioSource != (Object)null)
							{
								mark2.audioSource.Stop();
							}
						}
						catch
						{
						}
					}
				}
			}
			catch
			{
			}
			Plugin instance = Plugin.Instance;
			if (instance == null)
			{
				return;
			}
			ManualLogSource log = ((BasePlugin)instance).Log;
			if (log != null)
			{
				BepInExInfoLogInterpolatedStringHandler val2 = new BepInExInfoLogInterpolatedStringHandler(39, 1, ref flag);
				if (flag)
				{
					((BepInExLogInterpolatedStringHandler)val2).AppendLiteral("[LAN Mod] Restored & repaired vehicle: ");
					GameObject gameObject = ((Component)vehicle).gameObject;
					((BepInExLogInterpolatedStringHandler)val2).AppendFormatted<string>((gameObject != null) ? ((Object)gameObject).name : null);
				}
				log.LogInfo(val2);
			}
		}
		catch (Exception ex)
		{
			Plugin instance = Plugin.Instance;
			if (instance == null)
			{
				return;
			}
			ManualLogSource log = ((BasePlugin)instance).Log;
			if (log != null)
			{
				BepInExWarningLogInterpolatedStringHandler val3 = new BepInExWarningLogInterpolatedStringHandler(41, 1, ref flag);
				if (flag)
				{
					((BepInExLogInterpolatedStringHandler)val3).AppendLiteral("[LAN Mod] RepairAndRestoreVehicle error: ");
					((BepInExLogInterpolatedStringHandler)val3).AppendFormatted<string>(ex.Message);
				}
				log.LogWarning(val3);
			}
		}
	}

	public void RespawnLocalVehicle(UdpNetworkTransport transport)
	{
		//IL_01a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01be: Unknown result type (might be due to invalid IL or missing references)
		//IL_0382: Unknown result type (might be due to invalid IL or missing references)
		//IL_0389: Expected Obj, but got Unknown
		//IL_007b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0080: Unknown result type (might be due to invalid IL or missing references)
		//IL_0083: Unknown result type (might be due to invalid IL or missing references)
		//IL_0088: Unknown result type (might be due to invalid IL or missing references)
		//IL_008e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0099: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00aa: Unknown result type (might be due to invalid IL or missing references)
		//IL_0102: Unknown result type (might be due to invalid IL or missing references)
		//IL_0107: Unknown result type (might be due to invalid IL or missing references)
		//IL_0111: Unknown result type (might be due to invalid IL or missing references)
		//IL_0116: Unknown result type (might be due to invalid IL or missing references)
		//IL_012b: Unknown result type (might be due to invalid IL or missing references)
		//IL_013c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0151: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)LocalVehicle == (Object)null || ((Il2CppObjectBase)LocalVehicle).Pointer == IntPtr.Zero)
		{
			FindVehiclesIfNeeded();
			if ((Object)(object)LocalVehicle == (Object)null || ((Il2CppObjectBase)LocalVehicle).Pointer == IntPtr.Zero)
			{
				return;
			}
		}
		if (Time.time - _lastRespawnTime < 0.3f)
		{
			return;
		}
		_lastRespawnTime = Time.time;
		try
		{
			Quaternion rotation = ((Component)LocalVehicle).transform.rotation;
			Vector3 currentEuler = rotation.eulerAngles;
			Quaternion uprightRot = Quaternion.Euler(0f, currentEuler.y, 0f);
			((Component)LocalVehicle).transform.rotation = uprightRot;
			if ((Object)(object)LocalVehicle.Rigidbody != (Object)null && ((Il2CppObjectBase)LocalVehicle.Rigidbody).Pointer != IntPtr.Zero)
			{
				LocalVehicle.Rigidbody.isKinematic = false;
				Rigidbody rigidbody = LocalVehicle.Rigidbody;
				rigidbody.position += Vector3.up * 0.5f;
				LocalVehicle.Rigidbody.rotation = uprightRot;
				LocalVehicle.Rigidbody.velocity = Vector3.zero;
				LocalVehicle.Rigidbody.angularVelocity = Vector3.zero;
				try
				{
					LocalVehicle.Rigidbody.WakeUp();
				}
				catch
				{
				}
			}
			try
			{
				((Component)LocalVehicle).gameObject.SetActive(true);
			}
			catch
			{
			}
			try
			{
				((Behaviour)LocalVehicle).enabled = true;
			}
			catch
			{
			}
			VehicleBehaviour localVehicle = LocalVehicle;
			localVehicle.Flags = (VehicleFlags)(localVehicle.Flags & -65);
			VehicleBehaviour localVehicle2 = LocalVehicle;
			localVehicle2.Flags = (VehicleFlags)(localVehicle2.Flags | 0x129);
			LocalVehicle.ControlType = (ControlType)1;
			RepairAndRestoreVehicle(LocalVehicle);
			CacheLocalVehicleComponents();
			_localWasDestroyed = false;
			if ((Object)(object)LocalController == (Object)null || ((Il2CppObjectBase)LocalController).Pointer == IntPtr.Zero)
			{
				foreach (VehicleController c in Object.FindObjectsOfType<VehicleController>())
				{
					if ((Object)(object)c != (Object)null && ((Il2CppObjectBase)c).Pointer != IntPtr.Zero && (Object)(object)c != (Object)(object)RemoteController)
					{
						LocalController = c;
						break;
					}
				}
			}
			if ((Object)(object)LocalController != (Object)null && ((Il2CppObjectBase)LocalController).Pointer != IntPtr.Zero)
			{
				try
				{
					((Behaviour)LocalController).enabled = true;
				}
				catch
				{
				}
				try
				{
					LocalController.firing = false;
				}
				catch
				{
				}
				try
				{
					LocalController.throttleTarget = 0f;
				}
				catch
				{
				}
				try
				{
					LocalController.steerTarget = 0f;
				}
				catch
				{
				}
				try
				{
					LocalController.ResetControl();
				}
				catch
				{
				}
				try
				{
					LocalController.ResetDriveInputs();
				}
				catch
				{
				}
				try
				{
					LocalController.Hook(((Il2CppObjectBase)LocalVehicle).Cast<IVehicleBehaviour>());
				}
				catch
				{
				}
			}
			SendRespawnPacket(transport);
			LANManagerGUI.Instance?.SetStatus("Tank respawned and fully repaired.");
			Plugin instance = Plugin.Instance;
			if (instance != null)
			{
				ManualLogSource log = ((BasePlugin)instance).Log;
				if (log != null)
				{
					log.LogInfo((object)"[LAN Mod] Local vehicle respawned & fully repaired. Controls restored.");
				}
			}
		}
		catch (Exception ex)
		{
			Plugin instance2 = Plugin.Instance;
			if (instance2 == null)
			{
				return;
			}
			ManualLogSource log2 = ((BasePlugin)instance2).Log;
			if (log2 != null)
			{
				bool flag = default;
				BepInExWarningLogInterpolatedStringHandler val = new BepInExWarningLogInterpolatedStringHandler(27, 1, ref flag);
				if (flag)
				{
					((BepInExLogInterpolatedStringHandler)val).AppendLiteral("RespawnLocalVehicle error: ");
					((BepInExLogInterpolatedStringHandler)val).AppendFormatted<string>(ex.Message);
				}
				log2.LogWarning(val);
			}
		}
	}

	public void SendRespawnPacket(UdpNetworkTransport transport)
	{
		//IL_0086: Unknown result type (might be due to invalid IL or missing references)
		//IL_0074: Unknown result type (might be due to invalid IL or missing references)
		//IL_008b: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00aa: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ca: Unknown result type (might be due to invalid IL or missing references)
		if (transport != null && !((Object)(object)LocalVehicle == (Object)null) && !(((Il2CppObjectBase)LocalVehicle).Pointer == IntPtr.Zero))
		{
			PacketWriter pw = new PacketWriter(PacketType.Respawn);
			_localSequence++;
			pw.Write(_localSequence);
			pw.Write(Time.time);
			Vector3 pos = (((Object)(object)LocalVehicle.Rigidbody != (Object)null) ? LocalVehicle.Rigidbody.position : ((Component)LocalVehicle).transform.position);
			Quaternion rot = (((Object)(object)LocalVehicle.Rigidbody != (Object)null) ? LocalVehicle.Rigidbody.rotation : ((Component)LocalVehicle).transform.rotation);
			pw.Write(pos);
			pw.Write(rot);
			transport.Send(pw.ToArray());
		}
	}

	public void OnRemotePlayerRespawned(Vector3 spawnPos, Quaternion spawnRot)
	{
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		//IL_005d: Unknown result type (might be due to invalid IL or missing references)
		//IL_006a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0074: Unknown result type (might be due to invalid IL or missing references)
		//IL_0165: Unknown result type (might be due to invalid IL or missing references)
		//IL_0166: Unknown result type (might be due to invalid IL or missing references)
		//IL_016c: Unknown result type (might be due to invalid IL or missing references)
		//IL_016d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0173: Unknown result type (might be due to invalid IL or missing references)
		//IL_0178: Unknown result type (might be due to invalid IL or missing references)
		//IL_017e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0183: Unknown result type (might be due to invalid IL or missing references)
		//IL_01fe: Unknown result type (might be due to invalid IL or missing references)
		//IL_0205: Expected Obj, but got Unknown
		//IL_014d: Unknown result type (might be due to invalid IL or missing references)
		//IL_015e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ed: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0105: Unknown result type (might be due to invalid IL or missing references)
		//IL_0115: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bb: Expected Obj, but got Unknown
		//IL_01ca: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)RemoteVehicle == (Object)null || ((Il2CppObjectBase)RemoteVehicle).Pointer == IntPtr.Zero)
		{
			return;
		}
		bool flag = default;
		try
		{
			try
			{
				((Component)RemoteVehicle).gameObject.SetActive(true);
			}
			catch
			{
			}
			try
			{
				((Behaviour)RemoteVehicle).enabled = true;
			}
			catch
			{
			}
			VehicleBehaviour remoteVehicle = RemoteVehicle;
			remoteVehicle.Flags = (VehicleFlags)(remoteVehicle.Flags & -65);
			VehicleBehaviour remoteVehicle2 = RemoteVehicle;
			remoteVehicle2.Flags = (VehicleFlags)(remoteVehicle2.Flags | 0x129);
			RemoteVehicle.ControlType = (ControlType)0;
			RepairAndRestoreVehicle(RemoteVehicle);
			CacheRemoteVehicleComponents();
			NeutralizeAIOnRemoteVehicle(RemoteVehicle);
			_remoteWasDestroyed = false;
			_remoteCannonsLastFireTime.Clear();
			if ((Object)(object)_remoteRigidbody != (Object)null && ((Il2CppObjectBase)_remoteRigidbody).Pointer != IntPtr.Zero)
			{
				_remoteRigidbody.isKinematic = false;
				_remoteRigidbody.position = spawnPos;
				_remoteRigidbody.rotation = spawnRot;
				_remoteRigidbody.velocity = Vector3.zero;
				_remoteRigidbody.angularVelocity = Vector3.zero;
				try
				{
					_remoteRigidbody.WakeUp();
				}
				catch
				{
				}
			}
			else if ((Object)(object)((Component)RemoteVehicle).transform != (Object)null)
			{
				((Component)RemoteVehicle).transform.position = spawnPos;
				((Component)RemoteVehicle).transform.rotation = spawnRot;
			}
			_targetPosition = spawnPos;
			_targetRotation = spawnRot;
			_targetVelocity = Vector3.zero;
			_targetAngularVelocity = Vector3.zero;
			LANManagerGUI.Instance?.SetStatus("Opponent tank respawned and restored.");
			Plugin instance = Plugin.Instance;
			if (instance == null)
			{
				return;
			}
			ManualLogSource log = ((BasePlugin)instance).Log;
			if (log != null)
			{
				BepInExInfoLogInterpolatedStringHandler val = new BepInExInfoLogInterpolatedStringHandler(71, 1, ref flag);
				if (flag)
				{
					((BepInExLogInterpolatedStringHandler)val).AppendLiteral("[LAN Mod] Remote player respawned at ");
					((BepInExLogInterpolatedStringHandler)val).AppendFormatted<Vector3>(spawnPos);
					((BepInExLogInterpolatedStringHandler)val).AppendLiteral(". Puppet fully restored and reset.");
				}
				log.LogInfo(val);
			}
		}
		catch (Exception ex)
		{
			Plugin instance = Plugin.Instance;
			if (instance == null)
			{
				return;
			}
			ManualLogSource log = ((BasePlugin)instance).Log;
			if (log != null)
			{
				BepInExWarningLogInterpolatedStringHandler val2 = new BepInExWarningLogInterpolatedStringHandler(31, 1, ref flag);
				if (flag)
				{
					((BepInExLogInterpolatedStringHandler)val2).AppendLiteral("OnRemotePlayerRespawned error: ");
					((BepInExLogInterpolatedStringHandler)val2).AppendFormatted<string>(ex.Message);
				}
				log.LogWarning(val2);
			}
		}
	}

	public void OnLocalSceneResetting()
	{
		if (Time.time - _lastResetTime < 2f)
		{
			Plugin instance = Plugin.Instance;
			if (instance != null)
			{
				ManualLogSource log = ((BasePlugin)instance).Log;
				if (log != null)
				{
					log.LogInfo((object)"[LAN Mod] OnLocalSceneResetting debounced (reset already in progress).");
				}
			}
			return;
		}
		_lastResetTime = Time.time;
		IsResetting = true;
		_localWasDestroyed = false;
		_remoteWasDestroyed = false;
		Plugin instance2 = Plugin.Instance;
		if (instance2 != null)
		{
			ManualLogSource log2 = ((BasePlugin)instance2).Log;
			if (log2 != null)
			{
				log2.LogInfo((object)"[LAN Mod] Native SceneResetter resetting local scene. Clearing vehicles and pausing sync.");
			}
		}
		ClearVehicles();
	}

	public void OnNativeResetPrefix()
	{
		OnLocalSceneResetting();
	}

	public void TriggerBattleReset(bool fromPeer = false)
	{
		if (Time.time - _lastResetTime < 3f)
		{
			return;
		}
		_lastResetTime = Time.time;
		IsResetting = true;
		try
		{
			if (!fromPeer)
			{
				SendBattleResetPacket(MultiplayerController.Instance?.Transport);
			}
			ClearVehicles();
			TriggerNativeResetOnly();
		}
		catch (Exception ex)
		{
			Plugin instance = Plugin.Instance;
			if (instance != null)
			{
				ManualLogSource log = ((BasePlugin)instance).Log;
				if (log != null)
				{
					log.LogWarning((object)("TriggerBattleReset error: " + ex.Message));
				}
			}
		}
	}

	public void SendBattleResetPacket(UdpNetworkTransport transport)
	{
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_0020: Unknown result type (might be due to invalid IL or missing references)
		if (transport == null || !transport.IsConnected)
		{
			return;
		}
		PacketWriter writer = new PacketWriter(PacketType.BattleReset);
		writer.Write(Vector3.zero);
		writer.Write(Quaternion.identity);
		transport.Send(writer.ToArray());
		Plugin instance = Plugin.Instance;
		if (instance != null)
		{
			ManualLogSource log = ((BasePlugin)instance).Log;
			if (log != null)
			{
				log.LogInfo((object)"[LAN Mod] Dispatched BattleReset packet to LAN peer.");
			}
		}
	}

	public void OnPeerBattleResetReceived(Vector3 peerPos, Quaternion peerRot)
	{
		if (Time.time - _lastResetTime < 3f)
		{
			return;
		}
		_lastResetTime = Time.time;
		IsResetting = true;
		IsResettingFromPeer = true;
		_localDestructionHandled = false;
		_remoteDestructionHandled = false;
		_localWasDestroyed = false;
		_remoteWasDestroyed = false;
		Plugin instance = Plugin.Instance;
		if (instance != null)
		{
			ManualLogSource log = ((BasePlugin)instance).Log;
			if (log != null)
			{
				log.LogInfo((object)"[LAN Mod] Received BattleReset packet from peer. Executing clean scene reset without echoing.");
			}
		}
		try
		{
			ClearVehicles();
			TriggerNativeResetOnly();
		}
		catch (Exception ex)
		{
			Plugin instance2 = Plugin.Instance;
			if (instance2 != null)
			{
				ManualLogSource log2 = ((BasePlugin)instance2).Log;
				if (log2 != null)
				{
					log2.LogWarning((object)("OnPeerBattleResetReceived error: " + ex.Message));
				}
			}
		}
	}

	public void TriggerNativeReset()
	{
		TriggerNativeResetOnly();
	}

	public void TriggerNativeResetOnly()
	{
		try
		{
			Il2CppArrayBase<DeathmatchGameMode> deathmatchModes = Object.FindObjectsOfType<DeathmatchGameMode>();
			if (deathmatchModes != null && deathmatchModes.Length > 0 && deathmatchModes[0].sceneResetter != null)
			{
				Plugin instance = Plugin.Instance;
				if (instance != null)
				{
					ManualLogSource log = ((BasePlugin)instance).Log;
					if (log != null)
					{
						log.LogInfo((object)"[LAN Mod] Triggering native reset via DeathmatchGameMode.sceneResetter.ResetScene()...");
					}
				}
				deathmatchModes[0].sceneResetter.ResetScene();
				return;
			}
			GameController gc = Object.FindObjectOfType<GameController>();
			if ((Object)(object)gc != (Object)null && (Object)(object)gc.gamemode != (Object)null)
			{
				DeathmatchGameMode dm = ((Il2CppObjectBase)gc.gamemode).TryCast<DeathmatchGameMode>();
				if ((Object)(object)dm != (Object)null && dm.sceneResetter != null)
				{
					Plugin instance2 = Plugin.Instance;
					if (instance2 != null)
					{
						ManualLogSource log2 = ((BasePlugin)instance2).Log;
						if (log2 != null)
						{
							log2.LogInfo((object)"[LAN Mod] Triggering native reset via DeathmatchGameMode.sceneResetter.ResetScene()...");
						}
					}
					dm.sceneResetter.ResetScene();
					return;
				}
				Plugin instance3 = Plugin.Instance;
				if (instance3 != null)
				{
					ManualLogSource log3 = ((BasePlugin)instance3).Log;
					if (log3 != null)
					{
						log3.LogInfo((object)"[LAN Mod] Triggering native reset via GameMode.ResetLevel()...");
					}
				}
				gc.gamemode.ResetLevel();
				return;
			}
			Plugin instance4 = Plugin.Instance;
			if (instance4 != null)
			{
				ManualLogSource log4 = ((BasePlugin)instance4).Log;
				if (log4 != null)
				{
					log4.LogWarning((object)"[LAN Mod] No DeathmatchGameMode or GameMode found to execute native reset.");
				}
			}
		}
		catch (Exception ex)
		{
			Plugin instance5 = Plugin.Instance;
			if (instance5 != null)
			{
				ManualLogSource log5 = ((BasePlugin)instance5).Log;
				if (log5 != null)
				{
					log5.LogWarning((object)("TriggerNativeResetOnly error: " + ex.Message));
				}
			}
		}
	}

	public void SendHitPacket(UdpNetworkTransport transport, string compName, float damage, bool isDestroyed)
	{
		if (transport == null || !transport.IsConnected)
		{
			return;
		}
		try
		{
			PacketWriter writer = new PacketWriter(PacketType.Hit);
			writer.Write(compName ?? "Armor / Structure");
			writer.Write(damage);
			writer.Write(isDestroyed);
			transport.Send(writer.ToArray());
		}
		catch (Exception ex)
		{
			Plugin instance = Plugin.Instance;
			if (instance != null)
			{
				ManualLogSource log = ((BasePlugin)instance).Log;
				if (log != null)
				{
					log.LogWarning((object)("SendHitPacket error: " + ex.Message));
				}
			}
		}
	}

	private string ResolveCrewRole(VehicleBehaviour vehicle, IDurable healthPool, VehicleComponent comp)
	{
		//IL_0157: Unknown result type (might be due to invalid IL or missing references)
		//IL_015c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0179: Unknown result type (might be due to invalid IL or missing references)
		//IL_017e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0182: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)vehicle == (Object)null || ((Il2CppObjectBase)vehicle).Pointer == IntPtr.Zero)
		{
			return null;
		}
		try
		{
			VehicleCrewWorkspaceModule crewMod = default;
			if (vehicle.TryGetModule<VehicleCrewWorkspaceModule>(ref crewMod) && crewMod != null)
			{
				Il2CppReferenceArray<InjectedOperator> operators = crewMod.operators;
				Il2CppReferenceArray<InjectedTask> tasks = crewMod.tasks;
				if (operators != null && tasks != null)
				{
					for (int i = 0; i < ((Il2CppArrayBase<InjectedOperator>)(object)operators).Count; i++)
					{
						InjectedOperator op = ((Il2CppArrayBase<InjectedOperator>)(object)operators)[i];
						if (op == null || op.seat == null)
						{
							continue;
						}
						VehicleComponent seatComp = op.seat.Component;
						bool seatMatch = false;
						if ((Object)(object)seatComp != (Object)null)
						{
							if ((Object)(object)comp != (Object)null && (((Il2CppObjectBase)seatComp).Pointer == ((Il2CppObjectBase)comp).Pointer || ((Object)(object)((Component)seatComp).gameObject != (Object)null && (Object)(object)((Component)comp).gameObject != (Object)null && ((Il2CppObjectBase)((Component)seatComp).gameObject).Pointer == ((Il2CppObjectBase)((Component)comp).gameObject).Pointer) || ((Component)seatComp).transform.IsChildOf(((Component)comp).transform) || ((Component)comp).transform.IsChildOf(((Component)seatComp).transform)))
							{
								seatMatch = true;
							}
							else if (healthPool != null)
							{
								CrewSeat cs = ((Il2CppObjectBase)seatComp).TryCast<CrewSeat>();
								if ((Object)(object)cs != (Object)null && cs.healthPool != null && ((Il2CppObjectBase)cs.healthPool).Pointer == ((Il2CppObjectBase)healthPool).Pointer)
								{
									seatMatch = true;
								}
							}
						}
						if (!seatMatch)
						{
							continue;
						}
						RoleID roleId = op.ActiveRoleID;
						for (int j = 0; j < ((Il2CppArrayBase<InjectedTask>)(object)tasks).Count; j++)
						{
							InjectedTask task = ((Il2CppArrayBase<InjectedTask>)(object)tasks)[j];
							if (task == null)
							{
								continue;
							}
							RoleID roleID = task.RoleID;
							if (!roleID.Equals(roleId))
							{
								continue;
							}
							CrewTask it = task.internalTask;
							if (it != null)
							{
								string tName = ((object)it).GetType().Name.ToLowerInvariant();
								if (tName.Contains("driver"))
								{
									return "Driver";
								}
								if (tName.Contains("gunner") || tName.Contains("gunlayer"))
								{
									return "Gunner";
								}
								if (tName.Contains("commander"))
								{
									return "Commander";
								}
								if (tName.Contains("load"))
								{
									return "Loader";
								}
								if (tName.Contains("radio"))
								{
									return "Radio Operator";
								}
							}
							string icon = task.IconIdentifier;
							if (!string.IsNullOrEmpty(icon))
							{
								string iconLower = icon.ToLowerInvariant();
								if (iconLower.Contains("driver"))
								{
									return "Driver";
								}
								if (iconLower.Contains("gunner"))
								{
									return "Gunner";
								}
								if (iconLower.Contains("commander"))
								{
									return "Commander";
								}
								if (iconLower.Contains("load"))
								{
									return "Loader";
								}
								if (iconLower.Contains("radio"))
								{
									return "Radio Operator";
								}
							}
						}
					}
				}
			}
			Il2CppArrayBase<CrewSeat> seats = ((Component)vehicle).GetComponentsInChildren<CrewSeat>(true);
			if (seats != null)
			{
				for (int sIdx = 0; sIdx < seats.Length; sIdx++)
				{
					CrewSeat seat = seats[sIdx];
					if ((Object)(object)seat == (Object)null || ((Il2CppObjectBase)seat).Pointer == IntPtr.Zero)
					{
						continue;
					}
					bool match = false;
					if (healthPool != null && seat.healthPool != null && ((Il2CppObjectBase)seat.healthPool).Pointer == ((Il2CppObjectBase)healthPool).Pointer)
					{
						match = true;
					}
					else if ((Object)(object)comp != (Object)null && (Object)(object)((Component)seat).gameObject != (Object)null && (Object)(object)((Component)comp).gameObject != (Object)null && (((Il2CppObjectBase)((Component)seat).gameObject).Pointer == ((Il2CppObjectBase)((Component)comp).gameObject).Pointer || ((Component)seat).transform.IsChildOf(((Component)comp).transform)))
					{
						match = true;
					}
					if (!match)
					{
						continue;
					}
					OperatorSeat op2 = seat.Operator;
					if (op2 != null && ((Il2CppObjectBase)op2).Pointer != IntPtr.Zero)
					{
						IVehicleOperatorBehaviour behav = op2.ActiveBehaviour;
						if (behav != null && ((Il2CppObjectBase)behav).Pointer != IntPtr.Zero)
						{
							string bName = ((object)behav).GetType().Name.ToLowerInvariant();
							if (bName.Contains("driver"))
							{
								return "Driver";
							}
							if (bName.Contains("gunner") || bName.Contains("gunlayer"))
							{
								return "Gunner";
							}
							if (bName.Contains("commander"))
							{
								return "Commander";
							}
							if (bName.Contains("load"))
							{
								return "Loader";
							}
							if (bName.Contains("radio"))
							{
								return "Radio Operator";
							}
						}
					}
					string text = (((Object)(object)((Component)seat).gameObject != (Object)null) ? ((Object)((Component)seat).gameObject).name : "").ToLowerInvariant();
					string pName = (((Object)(object)((Component)seat).transform.parent != (Object)null) ? ((Object)((Component)seat).transform.parent).name : "").ToLowerInvariant();
					string combined = text + " " + pName;
					if (combined.Contains("driver"))
					{
						return "Driver";
					}
					if (combined.Contains("gunner"))
					{
						return "Gunner";
					}
					if (combined.Contains("commander"))
					{
						return "Commander";
					}
					if (combined.Contains("load"))
					{
						return "Loader";
					}
					if (combined.Contains("radio"))
					{
						return "Radio Operator";
					}
					return sIdx switch
					{
						0 => "Driver", 
						1 => "Commander", 
						2 => "Gunner", 
						3 => "Loader", 
						_ => $"Crew Member #{sIdx + 1}", 
					};
				}
			}
		}
		catch
		{
		}
		return null;
	}

	public string FormatComponentName(VehicleComponent comp, IDurable healthPool = null, VehicleBehaviour targetVehicle = null)
	{
		if ((Object)(object)targetVehicle != (Object)null)
		{
			string crewRole = ResolveCrewRole(targetVehicle, healthPool, comp);
			if (!string.IsNullOrEmpty(crewRole))
			{
				return crewRole;
			}
		}
		if ((Object)(object)comp != (Object)null && ((Il2CppObjectBase)comp).Pointer != IntPtr.Zero)
		{
			string rawName = "";
			try
			{
				if ((Object)(object)((VehiclePart)comp).VehicleTransform != (Object)null && ((Il2CppObjectBase)((VehiclePart)comp).VehicleTransform).Pointer != IntPtr.Zero)
				{
					rawName = ((Object)((VehiclePart)comp).VehicleTransform).name;
				}
			}
			catch
			{
			}
			if (string.IsNullOrEmpty(rawName))
			{
				try
				{
					rawName = ((object)comp).GetType().Name;
				}
				catch
				{
				}
			}
			string lower = rawName.ToLowerInvariant();
			if (lower.Contains("commander"))
			{
				return "Commander";
			}
			if (lower.Contains("gunner"))
			{
				return "Gunner";
			}
			if (lower.Contains("driver"))
			{
				return "Driver";
			}
			if (lower.Contains("loader"))
			{
				return "Loader";
			}
			if (lower.Contains("radioman") || lower.Contains("radio operator"))
			{
				return "Radio Operator";
			}
			if (lower.Contains("crew"))
			{
				return "Crew Member";
			}
			if (lower.Contains("breech"))
			{
				return "Cannon Breech";
			}
			if (lower.Contains("barrel"))
			{
				return "Cannon Barrel";
			}
			if (lower.Contains("mantlet"))
			{
				return "Gun Mantlet";
			}
			if (lower.Contains("cannon") || lower.Contains("gun"))
			{
				return "Main Cannon";
			}
			if (lower.Contains("turretring") || lower.Contains("turret ring") || (lower.Contains("ring") && lower.Contains("turret")))
			{
				return "Turret Ring";
			}
			if (lower.Contains("turretbasket") || lower.Contains("basket"))
			{
				return "Turret Basket";
			}
			if (lower.Contains("turret"))
			{
				return "Turret";
			}
			if (lower.Contains("track") || lower.Contains("sprocket") || lower.Contains("idler") || lower.Contains("roadwheel") || lower.Contains("suspension") || lower.Contains("wheel"))
			{
				return "Track / Suspension";
			}
			if (lower.Contains("ammo") || lower.Contains("ammunition") || (lower.Contains("rack") && !lower.Contains("track")) || lower.Contains("storage"))
			{
				return "Ammo Rack";
			}
			if (lower.Contains("fuel") || lower.Contains("tank"))
			{
				return "Fuel Tank";
			}
			if (lower.Contains("engine") || lower.Contains("motor"))
			{
				return "Engine";
			}
			if (lower.Contains("transmission") || lower.Contains("gearbox") || lower.Contains("powertrain"))
			{
				return "Transmission";
			}
			if (lower.Contains("radios") || lower.Contains("radio"))
			{
				return "Radio";
			}
			if (!string.IsNullOrEmpty(rawName))
			{
				string clean = rawName.Replace("Component", "").Replace("Behaviour", "").Replace("Model", "")
					.Trim();
				if (!string.IsNullOrEmpty(clean))
				{
					return clean;
				}
			}
		}
		if (healthPool != null && ((Il2CppObjectBase)healthPool).Pointer != IntPtr.Zero)
		{
			try
			{
				string hpType = ((object)healthPool).GetType().Name;
				if (hpType.Contains("Crew"))
				{
					return "Crew Member";
				}
				if (hpType.Contains("Ammo"))
				{
					return "Ammo Rack";
				}
				if (hpType.Contains("Fuel"))
				{
					return "Fuel Tank";
				}
				if (hpType.Contains("Engine"))
				{
					return "Engine";
				}
				if (hpType.Contains("Transmission"))
				{
					return "Transmission";
				}
			}
			catch
			{
			}
		}
		return "Armor / Hull Structure";
	}

	public void HandleHealthChanged(VehicleHealthRegister register, IDurable healthPool, HealthChangeInfo change)
	{
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		if (register == null || ((Il2CppObjectBase)register).Pointer == IntPtr.Zero || healthPool == null || ((Il2CppObjectBase)healthPool).Pointer == IntPtr.Zero || change.Delta >= -0.01f)
		{
			return;
		}
		float damage = Mathf.Abs(change.Delta);
		bool isDestroyed = !change.Alive || change.NewValue <= 0.001f;
		if (damage < 0.5f && !isDestroyed)
		{
			return;
		}
		try
		{
			VehicleBehaviour targetVehicle = null;
			VehicleComponent hitComp = null;
			if (register.entries != null)
			{
				List<VehicleComponentHealth> entries = register.entries;
				int count = entries.Count;
				for (int i = 0; i < count; i++)
				{
					VehicleComponentHealth entry = entries[i];
					if (entry != null && entry.HealthPool != null && ((Il2CppObjectBase)entry.HealthPool).Pointer == ((Il2CppObjectBase)healthPool).Pointer)
					{
						hitComp = entry.Owner;
						if ((Object)(object)hitComp != (Object)null && hitComp.Vehicle != null && ((Il2CppObjectBase)hitComp.Vehicle).Pointer != IntPtr.Zero && hitComp.Vehicle.Behaviour != null)
						{
							targetVehicle = ((Il2CppObjectBase)hitComp.Vehicle.Behaviour).TryCast<VehicleBehaviour>();
						}
						break;
					}
				}
			}
			if ((Object)(object)targetVehicle == (Object)null)
			{
				if ((Object)(object)LocalVehicle != (Object)null && ((Il2CppObjectBase)LocalVehicle).Pointer != IntPtr.Zero && LocalVehicle.healthRegister != null && ((Il2CppObjectBase)LocalVehicle.healthRegister).Pointer == ((Il2CppObjectBase)register).Pointer)
				{
					targetVehicle = LocalVehicle;
				}
				else if ((Object)(object)RemoteVehicle != (Object)null && ((Il2CppObjectBase)RemoteVehicle).Pointer != IntPtr.Zero && RemoteVehicle.healthRegister != null && ((Il2CppObjectBase)RemoteVehicle.healthRegister).Pointer == ((Il2CppObjectBase)register).Pointer)
				{
					targetVehicle = RemoteVehicle;
				}
			}
			string compName = FormatComponentName(hitComp, healthPool, targetVehicle);
			if ((Object)(object)targetVehicle != (Object)null && (Object)(object)LocalVehicle != (Object)null && ((Il2CppObjectBase)targetVehicle).Pointer == ((Il2CppObjectBase)LocalVehicle).Pointer)
			{
				UdpNetworkTransport transport = MultiplayerController.Instance?.Transport;
				if (transport != null && transport.IsConnected)
				{
					SendHitPacket(transport, compName, damage, isDestroyed);
				}
				if (isDestroyed && IsCrewEliminated(LocalVehicle) && !_localDestructionHandled)
				{
					OnVehicleDestroyed(LocalVehicle, isLocal: true, transport);
				}
			}
			else if ((Object)(object)targetVehicle != (Object)null && (Object)(object)RemoteVehicle != (Object)null && ((Il2CppObjectBase)targetVehicle).Pointer == ((Il2CppObjectBase)RemoteVehicle).Pointer)
			{
				LANManagerGUI.Instance?.RegisterOpponentDamage(compName, isDestroyed, damage);
				if (isDestroyed && IsCrewEliminated(RemoteVehicle) && !_remoteDestructionHandled)
				{
					OnVehicleDestroyed(RemoteVehicle, isLocal: false, MultiplayerController.Instance?.Transport);
				}
			}
		}
		catch (Exception ex)
		{
			Plugin instance = Plugin.Instance;
			if (instance != null)
			{
				ManualLogSource log = ((BasePlugin)instance).Log;
				if (log != null)
				{
					log.LogWarning((object)("HandleHealthChanged error: " + ex.Message));
				}
			}
		}
	}
}
