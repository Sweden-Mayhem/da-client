#region
using Chaos.IO.Memory;
using Chaos.Packets.Abstractions;
#endregion

namespace Chaos.Client.Networking;

// SWM market push (server -> client), opcode 112 (0x70). The wire format must match the server's
// Chaos.Networking.Converters.Server.MarketDataConverter.
internal sealed class MarketDataConverter : PacketConverterBase<MarketDataArgs>
{
    public const byte MARKET_DATA_OPCODE = 112;

    public override byte OpCode => MARKET_DATA_OPCODE;

    public override MarketDataArgs Deserialize(ref SpanReader reader)
    {
        var screen = reader.ReadByte();
        var gold = reader.ReadUInt32();
        var atNpc = reader.ReadByte();
        var message = reader.ReadString8();
        var context = reader.ReadString8();

        var categories = new List<MarketCategoryEntry>();
        var catCount = reader.ReadUInt32();

        for (var c = 0; c < catCount; c++)
        {
            var name = reader.ReadString8();
            var items = new List<MarketItemEntry>();
            var itemCount = reader.ReadUInt32();

            for (var i = 0; i < itemCount; i++)
                items.Add(
                    new MarketItemEntry
                    {
                        TemplateKey = reader.ReadString8(),
                        Name = reader.ReadString8(),
                        Sprite = (ushort)reader.ReadUInt32(),
                        Color = reader.ReadByte(),
                        MinPrice = reader.ReadUInt32(),
                        ListingCount = reader.ReadUInt32()
                    });

            categories.Add(
                new MarketCategoryEntry
                {
                    Name = name,
                    Items = items
                });
        }

        var listings = new List<MarketListingEntry>();
        var listingCount = reader.ReadUInt32();

        for (var l = 0; l < listingCount; l++)
            listings.Add(
                new MarketListingEntry
                {
                    ListingId = reader.ReadString8(),
                    ItemName = reader.ReadString8(),
                    TemplateKey = reader.ReadString8(),
                    Sprite = (ushort)reader.ReadUInt32(),
                    Color = reader.ReadByte(),
                    IsAuction = reader.ReadByte(),
                    Price = reader.ReadUInt32(),
                    MinBid = reader.ReadUInt32(),
                    Count = reader.ReadUInt32(),
                    BuyNow = reader.ReadUInt32(),
                    SecondsLeft = reader.ReadUInt32(),
                    HasBids = reader.ReadByte(),
                    HighBidder = reader.ReadString8(),
                    Winning = reader.ReadByte(),
                    SellerName = reader.ReadString8()
                });

        var storage = new List<MarketStorageEntry>();
        var storageCount = reader.ReadUInt32();

        for (var s = 0; s < storageCount; s++)
            storage.Add(
                new MarketStorageEntry
                {
                    UniqueId = reader.ReadString8(),
                    Name = reader.ReadString8(),
                    Sprite = (ushort)reader.ReadUInt32(),
                    Color = reader.ReadByte(),
                    Count = reader.ReadUInt32()
                });

        var sellItems = new List<MarketSellEntry>();
        var sellCount = reader.ReadUInt32();

        for (var s = 0; s < sellCount; s++)
            sellItems.Add(
                new MarketSellEntry
                {
                    Slot = reader.ReadByte(),
                    Name = reader.ReadString8(),
                    Sprite = (ushort)reader.ReadUInt32(),
                    Color = reader.ReadByte(),
                    Count = reader.ReadUInt32(),
                    Stackable = reader.ReadByte(),
                    SuggestedPrice = reader.ReadUInt32(),
                    Fee = reader.ReadUInt32(),
                    FromBank = reader.ReadByte()
                });

        return new MarketDataArgs
        {
            Screen = screen,
            AvailableGold = gold,
            AtNpc = atNpc,
            Message = message,
            Context = context,
            Categories = categories,
            Listings = listings,
            Storage = storage,
            SellItems = sellItems
        };
    }

    public override void Serialize(ref SpanWriter writer, MarketDataArgs args)
    {
        //client never sends this (server -> client only), but the base type requires a writer
        writer.WriteByte(args.Screen);
        writer.WriteUInt32(args.AvailableGold);
        writer.WriteByte(args.AtNpc);
        writer.WriteString8(args.Message ?? string.Empty);
        writer.WriteString8(args.Context ?? string.Empty);

        writer.WriteUInt32((uint)args.Categories.Count);

        foreach (var cat in args.Categories)
        {
            writer.WriteString8(cat.Name ?? string.Empty);
            writer.WriteUInt32((uint)cat.Items.Count);

            foreach (var item in cat.Items)
            {
                writer.WriteString8(item.TemplateKey ?? string.Empty);
                writer.WriteString8(item.Name ?? string.Empty);
                writer.WriteUInt32(item.Sprite);
                writer.WriteByte(item.Color);
                writer.WriteUInt32(item.MinPrice);
                writer.WriteUInt32(item.ListingCount);
            }
        }

        writer.WriteUInt32((uint)args.Listings.Count);

        foreach (var l in args.Listings)
        {
            writer.WriteString8(l.ListingId ?? string.Empty);
            writer.WriteString8(l.ItemName ?? string.Empty);
            writer.WriteString8(l.TemplateKey ?? string.Empty);
            writer.WriteUInt32(l.Sprite);
            writer.WriteByte(l.Color);
            writer.WriteByte(l.IsAuction);
            writer.WriteUInt32(l.Price);
            writer.WriteUInt32(l.MinBid);
            writer.WriteUInt32(l.Count);
            writer.WriteUInt32(l.BuyNow);
            writer.WriteUInt32(l.SecondsLeft);
            writer.WriteByte(l.HasBids);
            writer.WriteString8(l.HighBidder ?? string.Empty);
            writer.WriteByte(l.Winning);
            writer.WriteString8(l.SellerName ?? string.Empty);
        }

        writer.WriteUInt32((uint)args.Storage.Count);

        foreach (var s in args.Storage)
        {
            writer.WriteString8(s.UniqueId ?? string.Empty);
            writer.WriteString8(s.Name ?? string.Empty);
            writer.WriteUInt32(s.Sprite);
            writer.WriteByte(s.Color);
            writer.WriteUInt32(s.Count);
        }

        writer.WriteUInt32((uint)args.SellItems.Count);

        foreach (var s in args.SellItems)
        {
            writer.WriteByte(s.Slot);
            writer.WriteString8(s.Name ?? string.Empty);
            writer.WriteUInt32(s.Sprite);
            writer.WriteByte(s.Color);
            writer.WriteUInt32(s.Count);
            writer.WriteByte(s.Stackable);
            writer.WriteUInt32(s.SuggestedPrice);
            writer.WriteUInt32(s.Fee);
            writer.WriteByte(s.FromBank);
        }
    }
}
