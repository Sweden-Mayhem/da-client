using Chaos.DarkAges.Definitions;
using Chaos.IO.Memory;
using Chaos.Networking.Abstractions.Definitions;
using Chaos.Networking.Entities.Client;
using Chaos.Packets.Abstractions;

namespace Chaos.Client.Networking;

// Overrides the default PublicMessageConverter (which uses 1-byte string length, max 255)
// to use a 2-byte length prefix, allowing messages up to 65535 bytes
// must match the server-side PublicMessageConverter
internal sealed class LongPublicMessageConverter : PacketConverterBase<PublicMessageArgs>
{
    public override byte OpCode => (byte)ClientOpCode.PublicMessage;

    public override PublicMessageArgs Deserialize(ref SpanReader reader)
    {
        var publicMessageType = reader.ReadByte();
        var message = reader.ReadString16();

        return new PublicMessageArgs
        {
            PublicMessageType = (PublicMessageType)publicMessageType,
            Message = message
        };
    }

    public override void Serialize(ref SpanWriter writer, PublicMessageArgs args)
    {
        writer.WriteByte((byte)args.PublicMessageType);
        writer.WriteString16(args.Message);
    }
}
