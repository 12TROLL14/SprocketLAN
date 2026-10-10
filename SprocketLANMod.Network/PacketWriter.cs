using System.IO;
using UnityEngine;

namespace SprocketLANMod.Network;

public class PacketWriter
{
	private readonly MemoryStream _stream;

	private readonly BinaryWriter _writer;

	public PacketWriter(PacketType type)
	{
		_stream = new MemoryStream();
		_writer = new BinaryWriter(_stream);
		_writer.Write((byte)83);
		_writer.Write((byte)type);
	}

	public void Write(byte value)
	{
		_writer.Write(value);
	}

	public void Write(int value)
	{
		_writer.Write(value);
	}

	public void Write(uint value)
	{
		_writer.Write(value);
	}

	public void Write(float value)
	{
		_writer.Write(value);
	}

	public void Write(bool value)
	{
		_writer.Write(value);
	}

	public void Write(string value)
	{
		_writer.Write(value ?? string.Empty);
	}

	public void Write(Vector3 v)
	{
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		_writer.Write(v.x);
		_writer.Write(v.y);
		_writer.Write(v.z);
	}

	public void Write(Quaternion q)
	{
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		_writer.Write(q.x);
		_writer.Write(q.y);
		_writer.Write(q.z);
		_writer.Write(q.w);
	}

	public byte[] ToArray()
	{
		return _stream.ToArray();
	}
}
