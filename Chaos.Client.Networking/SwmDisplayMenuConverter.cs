#region
using Chaos.DarkAges.Definitions;
using Chaos.IO.Memory;
using Chaos.Networking.Abstractions.Definitions;
using Chaos.Networking.Converters.Server;
using Chaos.Networking.Entities.Server;
using Chaos.Packets.Abstractions;
#endregion

namespace Chaos.Client.Networking;

// Wraps the upstream DisplayMenuConverter and reads two custom extensions
// ShowItems: a trailing marker byte (1) flags the menu as a craft menu, the args are remapped to
//   the synthetic SwmProtocol.CraftMenu type so the UI renders the items with the learn-menu list
//   panel (item icons, two columns) instead of the shop panel
// ShowPlayerItems: after the slot list the server appends a UInt16 count plus (sprite, color, name)
//   tuples for items the merchant will buy but the player does not currently carry. The client shows
//   these as greyed-out rows so the player knows what the merchant wants
internal sealed class SwmDisplayMenuConverter : PacketConverterBase<DisplayMenuArgs>
{
    private const ushort ITEM_SPRITE_OFFSET = 32768; //matches Chaos.Networking.Definitions.NETWORKING_CONSTANTS server-side

    private static readonly DisplayMenuConverter Inner = new();

    public override byte OpCode => Inner.OpCode;

    public override DisplayMenuArgs Deserialize(ref SpanReader reader)
    {
        var args = Inner.Deserialize(ref reader);

        if ((args.MenuType == MenuType.ShowItems) && (reader.Remaining >= 1))
        {
            var marker = reader.ReadByte();

            if (marker == 1)
                args = args with { MenuType = Definitions.SwmProtocol.CraftMenu };
            else if (marker == 2)
                args = args with { MenuType = Definitions.SwmProtocol.Market };
        }

        if (args.MenuType == MenuType.ShowPlayerItems && reader.Remaining >= 2)
        {
            var count = reader.ReadUInt16();
            if (count > 0)
            {
                var items = new List<ItemInfo>(count);

                for (var i = 0; i < count; i++)
                {
                    var sprite = reader.ReadUInt16();
                    var color = reader.ReadByte();
                    var name = reader.ReadString8();

                    items.Add(
                        new ItemInfo
                        {
                            Sprite = (ushort)(sprite - ITEM_SPRITE_OFFSET),
                            Color = (DisplayColor)color,
                            Name = name
                        });
                }

                args = args with { Items = items };
            }
        }

        return args;
    }

    public override void Serialize(ref SpanWriter writer, DisplayMenuArgs args) => Inner.Serialize(ref writer, args);
}
