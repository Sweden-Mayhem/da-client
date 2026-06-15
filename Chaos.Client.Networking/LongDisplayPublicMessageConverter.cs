using Chaos.DarkAges.Definitions;
using Chaos.IO.Memory;
using Chaos.Networking.Abstractions.Definitions;
using Chaos.Networking.Entities.Server;
using Chaos.Packets.Abstractions;

namespace Chaos.Client.Networking;

// Overrides the default DisplayPublicMessageConverter (which uses 1-byte string length, max 255)
// to use a 2-byte length prefix, allowing messages up to 65535 bytes
// must match the server-side DisplayPublicMessageConverter
internal sealed class LongDisplayPublicMessageConverter : PacketConverterBase<DisplayPublicMessageArgs>
{
    public override byte OpCode => (byte)ServerOpCode.DisplayPublicMessage;

    public override DisplayPublicMessageArgs Deserialize(ref SpanReader reader)
    {
        var messageType = reader.ReadByte();
        var sourceId = reader.ReadUInt32();
        var message = reader.ReadString16();

        return new DisplayPublicMessageArgs
        {
            PublicMessageType = (PublicMessageType)messageType,
            SourceId = sourceId,
            Message = message
        };
    }

    public override void Serialize(ref SpanWriter writer, DisplayPublicMessageArgs args)
    {
        writer.WriteByte((byte)args.PublicMessageType);
        writer.WriteUInt32(args.SourceId);
        writer.WriteString16(args.Message);
    }
}
