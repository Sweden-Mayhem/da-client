#region
using Chaos.IO.Memory;
using Chaos.Packets.Abstractions;
#endregion

namespace Chaos.Client.Networking;

// SWM extension (server -> client): a quest-giver NPC offers an available quest. The client shows the Yes/No
// offer window (quest name, description, reward preview); Accept sends the StartQuest request. Byte-for-byte
// mirror of the server's QuestOfferConverter. OpCode 116 (0x74) is written as a literal (high in-world opcode).

/// <summary>Client-local mirror of the server's QuestOfferArgs (ServerOpCode.QuestOffer = 116).</summary>
public sealed record QuestOfferArgs : IPacketSerializable
{
    public string QuestKey { get; init; } = string.Empty;
    public string QuestName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public uint Exp { get; init; }
    public uint Gold { get; init; }
    public List<QuestRewardItemInfo> Items { get; init; } = [];
    public List<QuestRewardMarkInfo> Marks { get; init; } = [];
    public uint RepeatSeconds { get; init; } //repeat cooldown (0 = one-off) so the offer can show "Daily - repeatable"
}

/// <summary>Deserializes <see cref="QuestOfferArgs" /> (opcode 116). Byte-for-byte mirror of the server.</summary>
public sealed class QuestOfferConverter : PacketConverterBase<QuestOfferArgs>
{
    public const byte OPCODE = 116;

    /// <inheritdoc />
    public override byte OpCode => OPCODE;

    /// <inheritdoc />
    public override QuestOfferArgs Deserialize(ref SpanReader reader)
    {
        var questKey = reader.ReadString8();
        var questName = reader.ReadString8();
        var description = reader.ReadString8();
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
            marks.Add(new QuestRewardMarkInfo { Icon = reader.ReadByte(), Color = reader.ReadByte(), Title = reader.ReadString8() });

        var repeatSeconds = reader.ReadUInt32();

        return new QuestOfferArgs
        {
            QuestKey = questKey,
            QuestName = questName,
            Description = description,
            Exp = exp,
            Gold = gold,
            Items = items,
            Marks = marks,
            RepeatSeconds = repeatSeconds
        };
    }

    /// <inheritdoc />
    public override void Serialize(ref SpanWriter writer, QuestOfferArgs args)
    {
        // the client never sends this; implemented for symmetry
        writer.WriteString8(args.QuestKey);
        writer.WriteString8(args.QuestName);
        writer.WriteString8(args.Description);
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

        writer.WriteUInt32(args.RepeatSeconds);
    }
}
