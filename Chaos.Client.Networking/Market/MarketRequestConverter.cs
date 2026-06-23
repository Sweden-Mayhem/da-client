#region
using Chaos.IO.Memory;
using Chaos.Packets.Abstractions;
#endregion

namespace Chaos.Client.Networking;

// SWM market request (client -> server), opcode 91 (0x5B). The wire format must match the server's
// Chaos.Networking.Converters.Client.MarketRequestConverter. In-world packet, so MD5 encryption on both ends.
internal sealed class MarketRequestConverter : PacketConverterBase<MarketRequestArgs>
{
    public const byte MARKET_REQUEST_OPCODE = 91;

    public override byte OpCode => MARKET_REQUEST_OPCODE;

    public override MarketRequestArgs Deserialize(ref SpanReader reader)
    {
        //client never receives this (client -> server only), but the base type requires a reader
        var action = reader.ReadByte();
        var arg = reader.ReadString8();
        var slot = reader.ReadByte();
        var listingType = reader.ReadByte();
        var quantity = (int)reader.ReadUInt32();
        var price = (int)reader.ReadUInt32();
        var buyNow = (int)reader.ReadUInt32();
        var hours = (int)reader.ReadUInt32();
        var maxBid = (int)reader.ReadUInt32();
        var atNpc = reader.ReadByte();
        var fromBank = reader.ReadByte();

        return new MarketRequestArgs
        {
            Action = (MarketClientAction)action,
            Arg = arg,
            Slot = slot,
            ListingType = listingType,
            Quantity = quantity,
            Price = price,
            BuyNow = buyNow,
            Hours = hours,
            MaxBid = maxBid,
            AtNpc = atNpc,
            FromBank = fromBank
        };
    }

    public override void Serialize(ref SpanWriter writer, MarketRequestArgs args)
    {
        writer.WriteByte((byte)args.Action);
        writer.WriteString8(args.Arg ?? string.Empty);
        writer.WriteByte(args.Slot);
        writer.WriteByte(args.ListingType);
        writer.WriteUInt32((uint)Math.Max(0, args.Quantity));
        writer.WriteUInt32((uint)Math.Max(0, args.Price));
        writer.WriteUInt32((uint)Math.Max(0, args.BuyNow));
        writer.WriteUInt32((uint)Math.Max(0, args.Hours));
        writer.WriteUInt32((uint)Math.Max(0, args.MaxBid));
        writer.WriteByte(args.AtNpc);
        writer.WriteByte(args.FromBank);
    }
}
