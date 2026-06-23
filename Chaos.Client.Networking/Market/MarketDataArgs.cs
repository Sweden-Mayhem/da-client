#region
using Chaos.Packets.Abstractions;
#endregion

namespace Chaos.Client.Networking;

/// <summary>
///     Client copy of the SWM market push (ServerOpCode.MarketData = 112). One screen of the Temuair Exchange window;
///     <see cref="Screen" /> says which collection is meaningful. The wire format must match the server's MarketDataArgs.
/// </summary>
public sealed record MarketDataArgs : IPacketSerializable
{
    public byte Screen { get; init; }
    public uint AvailableGold { get; init; }
    public byte AtNpc { get; init; }
    public string Message { get; init; } = string.Empty;
    public string Context { get; init; } = string.Empty;
    public List<MarketCategoryEntry> Categories { get; init; } = [];
    public List<MarketListingEntry> Listings { get; init; } = [];
    public List<MarketStorageEntry> Storage { get; init; } = [];
    public List<MarketSellEntry> SellItems { get; init; } = [];
}

/// <summary>Which market screen a <see cref="MarketDataArgs" /> fills.</summary>
public enum MarketScreen : byte
{
    Catalog = 0,
    Sellers = 1,
    MyListings = 2,
    MyBids = 3,
    Storage = 4,
    SellPicker = 5,
    Result = 6
}

public sealed record MarketCategoryEntry
{
    public string Name { get; init; } = string.Empty;
    public List<MarketItemEntry> Items { get; init; } = [];
}

public sealed record MarketItemEntry
{
    public string TemplateKey { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public ushort Sprite { get; init; }
    public byte Color { get; init; }
    public uint MinPrice { get; init; }
    public uint ListingCount { get; init; }
}

public sealed record MarketListingEntry
{
    public string ListingId { get; init; } = string.Empty;
    public string ItemName { get; init; } = string.Empty;
    public string TemplateKey { get; init; } = string.Empty;
    public ushort Sprite { get; init; }
    public byte Color { get; init; }
    public byte IsAuction { get; init; }
    public uint Price { get; init; }
    public uint MinBid { get; init; }
    public uint Count { get; init; }
    public uint BuyNow { get; init; }
    public uint SecondsLeft { get; init; }
    public byte HasBids { get; init; }
    public string HighBidder { get; init; } = string.Empty;
    public byte Winning { get; init; }
    public string SellerName { get; init; } = string.Empty;
}

public sealed record MarketStorageEntry
{
    public string UniqueId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public ushort Sprite { get; init; }
    public byte Color { get; init; }
    public uint Count { get; init; }
}

public sealed record MarketSellEntry
{
    public byte Slot { get; init; }
    public string Name { get; init; } = string.Empty;
    public ushort Sprite { get; init; }
    public byte Color { get; init; }
    public uint Count { get; init; }
    public byte Stackable { get; init; }
    public uint SuggestedPrice { get; init; }
    public uint Fee { get; init; }
    public byte FromBank { get; init; } //0 inventory, 1 bank
}
