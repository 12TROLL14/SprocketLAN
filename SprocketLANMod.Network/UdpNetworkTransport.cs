using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace SprocketLANMod.Network;

public class UdpNetworkTransport
{
	private UdpClient _udpClient;

	private Thread _receiveThread;

	private volatile bool _isRunning;

	private readonly ConcurrentQueue<byte[]> _incomingQueue = new ConcurrentQueue<byte[]>();

	private float _lastPingTime;

	public bool IsHosting { get; private set; }

	public bool IsConnected { get; private set; }

	public IPEndPoint RemoteEndPoint { get; private set; }

	public int LocalPort { get; private set; }

	public float RoundTripTimeMs { get; private set; }

	public int PacketsSent { get; private set; }

	public int PacketsReceived { get; private set; }

	public event Action<PacketReader, IPEndPoint> PacketReceived;

	public event Action<string> LogMessage;

	public void StartHost(int port = 7777)
	{
		Stop();
		try
		{
			LocalPort = port;
			_udpClient = new UdpClient(port);
			_udpClient.EnableBroadcast = true;
			IsHosting = true;
			_isRunning = true;
			_receiveThread = new Thread(ReceiveLoop)
			{
				IsBackground = true,
				Name = "Sprocket_UDP_Host_Thread"
			};
			_receiveThread.Start();
			LogMessage?.Invoke($"[Host] Started listening on UDP port {port}");
		}
		catch (Exception ex)
		{
			LogMessage?.Invoke($"[Host] Failed to start on port {port}: {ex.Message}");
			Stop();
		}
	}

	public void Connect(string hostIp, int port = 7777)
	{
		Stop();
		try
		{
			if (!IPAddress.TryParse(hostIp, out IPAddress ip))
			{
				IPAddress[] hostAddresses = Dns.GetHostAddresses(hostIp);
				if (hostAddresses.Length == 0)
				{
					throw new Exception("Could not resolve host: " + hostIp);
				}
				ip = hostAddresses[0];
			}
			RemoteEndPoint = new IPEndPoint(ip, port);
			_udpClient = new UdpClient();
			_udpClient.EnableBroadcast = true;
			_udpClient.Connect(RemoteEndPoint);
			IsHosting = false;
			IsConnected = true;
			_isRunning = true;
			_receiveThread = new Thread(ReceiveLoop)
			{
				IsBackground = true,
				Name = "Sprocket_UDP_Client_Thread"
			};
			_receiveThread.Start();
			PacketWriter pw = new PacketWriter(PacketType.Handshake);
			pw.Write("SprocketPlayer");
			pw.Write("Tank");
			Send(pw.ToArray());
			LogMessage?.Invoke($"[Client] Connected to {RemoteEndPoint}");
		}
		catch (Exception ex)
		{
			LogMessage?.Invoke("[Client] Failed to connect: " + ex.Message);
			Stop();
		}
	}

	public void Send(byte[] data)
	{
		if (!_isRunning || _udpClient == null)
		{
			return;
		}
		try
		{
			if (IsHosting)
			{
				if (RemoteEndPoint != null)
				{
					_udpClient.Send(data, data.Length, RemoteEndPoint);
					PacketsSent++;
				}
			}
			else
			{
				_udpClient.Send(data, data.Length);
				PacketsSent++;
			}
		}
		catch (Exception)
		{
		}
	}

	private void ReceiveLoop()
	{
		IPEndPoint endPoint = new IPEndPoint(IPAddress.Any, 0);
		while (_isRunning && _udpClient != null)
		{
			try
			{
				byte[] data = _udpClient.Receive(ref endPoint);
				if (data != null && data.Length != 0)
				{
					PacketsReceived++;
					if (IsHosting && RemoteEndPoint == null)
					{
						RemoteEndPoint = endPoint;
						IsConnected = true;
						LogMessage?.Invoke($"[Host] Client connected from {RemoteEndPoint}");
					}
					_incomingQueue.Enqueue(data);
				}
			}
			catch (SocketException)
			{
			}
			catch (Exception ex2)
			{
				if (_isRunning)
				{
					LogMessage?.Invoke("[Transport] Receive error: " + ex2.Message);
				}
			}
		}
	}

	public void Update()
	{
		byte[] raw;
		while (_incomingQueue.TryDequeue(out raw))
		{
			PacketReader reader = new PacketReader(raw, raw.Length);
			if (reader.IsValid)
			{
				HandleInternalPacket(reader);
				PacketReceived?.Invoke(reader, RemoteEndPoint);
			}
		}
		if (IsConnected && Time.time - _lastPingTime > 1f)
		{
			_lastPingTime = Time.time;
			PacketWriter pw = new PacketWriter(PacketType.Ping);
			pw.Write(Time.time);
			Send(pw.ToArray());
		}
	}

	private void HandleInternalPacket(PacketReader reader)
	{
		if (reader.Type == PacketType.Ping)
		{
			float sentTime = reader.ReadFloat();
			PacketWriter pong = new PacketWriter(PacketType.Pong);
			pong.Write(sentTime);
			Send(pong.ToArray());
		}
		else if (reader.Type == PacketType.Pong)
		{
			float sentTime2 = reader.ReadFloat();
			RoundTripTimeMs = (Time.time - sentTime2) * 1000f;
		}
		else if (reader.Type == PacketType.Disconnect)
		{
			LogMessage?.Invoke("[Transport] Peer disconnected.");
			if (!IsHosting)
			{
				Stop();
				return;
			}
			RemoteEndPoint = null;
			IsConnected = false;
		}
	}

	public void Stop()
	{
		if (!_isRunning && _udpClient == null)
		{
			return;
		}
		_isRunning = false;
		try
		{
			if (IsConnected)
			{
				PacketWriter pw = new PacketWriter(PacketType.Disconnect);
				Send(pw.ToArray());
			}
		}
		catch
		{
		}
		try
		{
			_udpClient?.Close();
			_udpClient = null;
		}
		catch
		{
		}
		IsHosting = false;
		IsConnected = false;
		RemoteEndPoint = null;
		LogMessage?.Invoke("[Transport] Stopped.");
	}
}
