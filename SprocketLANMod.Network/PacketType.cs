namespace SprocketLANMod.Network;

public enum PacketType : byte
{
	Handshake = 1,
	Ping,
	Pong,
	TransformSync,
	Fire,
	Hit,
	ChatMessage,
	Disconnect,
	Respawn,
	TankDestroyed,
	BattleReset
}
