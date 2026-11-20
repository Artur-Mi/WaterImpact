using ProtoBuf;
using WaterImpact.NetworkLib;

namespace WaterImpact
{
    // Server -> clients: a round entered the water and this is what happens to it. Missile entity IDs are the same on
    // the server and every client (the server broadcasts the spawn), so the ID names the round.
    [ProtoContract]
    public class WaterEntryPacket : PacketBase
    {
        public WaterEntryPacket() { }

        [ProtoMember(1)]
        public long MissileId;

        [ProtoMember(2)]
        public byte Mode;

        [ProtoMember(3)]
        public double X;

        [ProtoMember(4)]
        public double Y;

        [ProtoMember(5)]
        public double Z;

        // Entry speed (m/s) and direction (unit vector), used by Penetrate.
        [ProtoMember(6)]
        public float Speed;

        [ProtoMember(7)]
        public float DirX;

        [ProtoMember(8)]
        public float DirY;

        [ProtoMember(9)]
        public float DirZ;

        public static event ReceiveDelegate<WaterEntryPacket> OnReceive;

        public override void Received(ref PacketInfo packetInfo, ulong senderSteamId)
        {
            OnReceive?.Invoke(this, ref packetInfo, senderSteamId);
        }
    }
}
