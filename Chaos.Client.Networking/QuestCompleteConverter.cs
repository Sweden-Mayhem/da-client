#region
using Chaos.IO.Memory;
using Chaos.Packets.Abstractions;
#endregion

namespace Chaos.Client.Networking;

// SWM extension (server -> client): a quest finished and paid its structured rewards. The client shows the
// "Quest Complete" banner + a visual reward panel (item slots, gold icon + amount, legend-mark icons). The args
// type is client-local; only the wire format must match the server's QuestCompleteConverter:
//   String8 questName, UInt32 exp, UInt32 gold, byte itemCount, then per item: UInt16 sprite, byte colour,
//   byte count, String8 name; byte markCount, then per mark: byte icon, byte colour, String8 title.
// OpCode 115 (0x73) is not in the NuGet enum, so it is written as a literal (high/unknown in-world opcode ->
// in-world MD5 scheme). Auto-discovered by the client's packet serializer.

/// <summary>Client-local mirror of the server's QuestCompleteArgs (ServerOpCode.QuestComplete = 115).</summary>
public sealed record QuestCompleteArgs : IPacketSerializable
{
    public string QuestName { get; init; } = string.Empty;
    public uint Exp { get; init; }
    public uint Gold { get; init; }
    public List<QuestRewardItemInfo> Items { get; init; } = [];
    public List<QuestRewardMarkInfo> Marks { get; init; } = [];
}

/// <summary>One item reward: the inventory panel sprite + colour + count + name.</summary>
public sealed record QuestRewardItemInfo
{
    public ushort Sprite { get; init; }
    public byte Color { get; init; }
    public byte Count { get; init; } = 1;
    public string Name { get; init; } = string.Empty;
}

/// <summary>One legend-mark reward: the MarkIcon byte (legends.epf frame), the MarkColor byte, and the title.</summary>
public sealed record QuestRewardMarkInfo
{
    public byte Icon { get; init; }
    public byte Color { get; init; }
    public string Title { get; init; } = string.Empty;
}

/// <summary>Deserializes <see cref="QuestCompleteArgs" /> (opcode 115). Byte-for-byte mirror of the server.</summary>
public sealed class QuestCompleteConverter : PacketConverterBase<QuestCompleteArgs>
{
    public const byte OPCODE = 115;

    /// <inheritdoc />
    public override byte OpCode => OPCODE;

    /// <inheritdoc />
    public override QuestCompleteArgs Deserialize(ref SpanReader reader)
    {
        var questName = reader.ReadString8();
        var exp = reader.ReadUInt32();
        var gold = reader.ReadUInt32();

        var items = new List<QuestRewardItemInfo>();
        var itemCount = reader.ReadByte();

        for (var i = 0; i < itemCount; i++)
            items.Add(
                new QuestRewardItemInfo
                {
                    Sprite = reader.ReadUInt16(),
                    Color = reader.ReadByte(),
                    Count = reader.ReadByte(),
                    Name = reader.ReadString8()
                });

        var marks = new List<QuestRewardMarkInfo>();
        var markCount = reader.ReadByte();

        for (var i = 0; i < markCount; i++)
            marks.Add(
                new QuestRewardMarkInfo
                {
                    Icon = reader.ReadByte(),
                    Color = reader.ReadByte(),
                    Title = reader.ReadString8()
                });

        return new QuestCompleteArgs
        {
            QuestName = questName,
            Exp = exp,
            Gold = gold,
            Items = items,
            Marks = marks
        };
    }

    /// <inheritdoc />
    public override void Serialize(ref SpanWriter writer, QuestCompleteArgs args)
    {
        // the client never sends this; implemented for symmetry
        writer.WriteString8(args.QuestName);
        writer.WriteUInt32(args.Exp);
        writer.WriteUInt32(args.Gold);
        writer.WriteByte((byte)args.Items.Count);

        foreach (var item in args.Items)
        {
            writer.WriteUInt16(item.Sprite);
            writer.WriteByte(item.Color);
            writer.WriteByte(item.Count);
            writer.WriteString8(item.Name);
        }

        writer.WriteByte((byte)args.Marks.Count);

        foreach (var mark in args.Marks)
        {
            writer.WriteByte(mark.Icon);
            writer.WriteByte(mark.Color);
            writer.WriteString8(mark.Title);
        }
    }
}
