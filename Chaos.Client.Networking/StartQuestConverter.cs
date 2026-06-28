#region
using Chaos.IO.Memory;
using Chaos.Packets.Abstractions;
#endregion

namespace Chaos.Client.Networking;

// SWM extension (client -> server): a request to act on a quest from the journal. The args type is client-local;
// only the wire format must match the server's Chaos.Networking.Converters.Client.StartQuestConverter:
//   byte Action (0 = start, 1 = abandon), String8 questKey. ClientOpCode.StartQuest = 92 (0x5C) - written as a
//   literal here since the NuGet ClientOpCode enum has no StartQuest value. Auto-discovered by the serializer.

/// <summary>Client-local mirror of the server's StartQuestArgs (ClientOpCode.StartQuest = 92).</summary>
public sealed record StartQuestArgs : IPacketSerializable
{
    /// <summary>0 = start the quest, 1 = abandon it.</summary>
    public byte Action { get; init; }

    /// <summary>The quest key to act on.</summary>
    public string QuestKey { get; init; } = string.Empty;
}

/// <summary>Serializes <see cref="StartQuestArgs" /> (client -> server). Auto-discovered by the packet serializer.</summary>
public sealed class StartQuestConverter : PacketConverterBase<StartQuestArgs>
{
    /// <summary>The quest-action client opcode (matches ClientOpCode.StartQuest on the server).</summary>
    public const byte OPCODE = 92;

    /// <inheritdoc />
    public override byte OpCode => OPCODE;

    /// <inheritdoc />
    public override StartQuestArgs Deserialize(ref SpanReader reader)
        => new() { Action = reader.ReadByte(), QuestKey = reader.ReadString8() }; //server never sends this

    /// <inheritdoc />
    public override void Serialize(ref SpanWriter writer, StartQuestArgs args)
    {
        writer.WriteByte(args.Action);
        writer.WriteString8(args.QuestKey);
    }
}
