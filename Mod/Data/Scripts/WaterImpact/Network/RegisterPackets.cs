using ProtoBuf;

namespace WaterImpact.NetworkLib
{
    // Every packet type of this mod, with tags from 10 up (see NetworkLib/Usage.txt).
    [ProtoInclude(10, typeof(WaterEntryPacket))]
    public abstract partial class PacketBase
    {
    }
}
