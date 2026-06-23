#region
using Chaos.Packets.Abstractions;
#endregion

namespace Chaos.Client.Networking;

/// <summary>
///     Client copy of the SWM market request (ClientOpCode.MarketRequest = 91). One navigation step or action in the
///     Temuair Exchange window. The wire format must match the server's MarketRequestArgs.
/// </summary>
public sealed record MarketRequestArgs : IPacketSerializable
{
    public MarketClientAction Action { get; init; }
    public string Arg { get; init; } = string.Empty;
    public byte Slot { get; init; }
    public byte ListingType { get; init; }
    public int Quantity { get; init; }
    public int Price { get; init; }
    public int BuyNow { get; init; }
    public int Hours { get; init; }
    public int MaxBid { get; init; }
    public byte AtNpc { get; init; }
    public byte FromBank { get; init; } //listing a bank item (Arg holds the item name)
}

/// <summary>The action carried by a <see cref="MarketRequestArgs" />.</summary>
public enum MarketClientAction : byte
{
    OpenCatalog = 0,
    RequestSellers = 1,
    RequestMyListings = 2,
    RequestMyBids = 3,
    RequestStorage = 4,
    RequestSellPicker = 5,
    Buy = 10,
    BuyNow = 11,
    Bid = 12,
    ListItem = 13,
    CancelListing = 14,
    CollectStorage = 15,
    CollectStorageToBank = 16
}
