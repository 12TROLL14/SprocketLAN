using System.IO;
using UnityEngine;

namespace SprocketLANMod.Network;

public class PacketReader
{
	private readonly Stream _stream;

	private readonly BinaryReader _reader;

	public PacketType Type { get; }

	public bool IsValid { get; }

	public int RemainingBytes => (int)(_stream.Length - _stream.Position);

	public PacketReader(byte[] data, int length)
	{
		_stream = new MemoryStream(data, 0, length, writable: false);
		_reader = new BinaryReader(_stream);
		if (length < 2)
		{
			IsValid = false;
			return;
		}
		if (_reader.ReadByte() != 83)
		{
			IsValid = false;
			return;
		}
		Type = (PacketType)_reader.ReadByte();
		IsValid = true;
	}

	public byte ReadByte()
	{
		return _reader.ReadByte();
	}

	public int ReadInt()
	{
		return _reader.ReadInt32();
	}

	public uint ReadUInt()
	{
		return _reader.ReadUInt32();
	}

	public float ReadFloat()
	{
		return _reader.ReadSingle();
	}

	public bool ReadBool()
	{
		return _reader.ReadBoolean();
	}

	public string ReadString()
	{
		return _reader.ReadString();
	}

	public Vector3 ReadVector3()
	{
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		float num = _reader.ReadSingle();
		float y = _reader.ReadSingle();
		float z = _reader.ReadSingle();
		return new Vector3(num, y, z);
	}

	public Quaternion ReadQuaternion()
	{
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		float num = _reader.ReadSingle();
		float y = _reader.ReadSingle();
		float z = _reader.ReadSingle();
		float w = _reader.ReadSingle();
		return new Quaternion(num, y, z, w);
	}
}
