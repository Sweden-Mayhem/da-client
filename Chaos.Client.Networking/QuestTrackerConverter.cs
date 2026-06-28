#region
using Chaos.IO.Memory;
using Chaos.Packets.Abstractions;
#endregion

namespace Chaos.Client.Networking;

// SWM extension (server -> client): the quest-tracker checkpoint state for the corner HUD panel. The args
// type is client-local (the NuGet Chaos.Networking has no QuestTracker type); only the wire format must match
// the server's Chaos.Networking.Converters.Server.QuestTrackerConverter:
//   byte questCount, then per quest: String8 questKey, String8 title, String16 description, byte currentIndex
//   (255 = none), byte checkpointCount, then per checkpoint: String8 label, String8 detail, String16 description.
//   (String16 for the descriptions so multi-sentence authored text past 255 bytes is fine.)
// OpCode 114 (0x72) is not in the NuGet enum, so it is written as a literal here; being a high/unknown
// in-world opcode it falls through to the in-world (MD5) encryption scheme on both ends. The converter is
// auto-discovered by the client's packet serializer (it scans loaded assemblies for IPacketConverter).

/// <summary>Client-local mirror of the server's QuestTrackerArgs (ServerOpCode.QuestTracker = 114).</summary>
public sealed record QuestTrackerArgs : IPacketSerializable
{
    /// <summary>Every active tracked quest, in display order (the client stacks them in the HUD).</summary>
    public List<QuestTrackerQuestInfo> Quests { get; init; } = [];

    /// <summary>Per-quest journal start status: a Start button (cooldown 0) or a live cooldown countdown (&gt;0).</summary>
    public List<QuestStartStatusInfo> StartStatuses { get; init; } = [];

    /// <summary>Quest keys the player has completed at least once (moves them to the journal's Completed tab).</summary>
    public List<string> CompletedKeys { get; init; } = [];

    /// <summary>Quest keys whose objectives are done and await the journal's "Claim Reward" button (no NPC turn-in).</summary>
    public List<string> ClaimableKeys { get; init; } = [];
}

/// <summary>One quest's journal start status: startable now (CooldownSeconds 0) or its remaining cooldown seconds.</summary>
public sealed record QuestStartStatusInfo
{
    public string Key { get; init; } = string.Empty;
    public long CooldownSeconds { get; init; }
}

/// <summary>One tracked quest: its title, its ordered checkpoints, and which one is current.</summary>
public sealed record QuestTrackerQuestInfo
{
    public string QuestKey { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    /// <summary>Quest-level description shown in the quest-info window (overall goal / lore); may be empty.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Optional lifetime counter line ("Label: value") shown in the tracker; empty if none.</summary>
    public string Counter { get; init; } = string.Empty;

    /// <summary>Optional "right place" guide hint for the current step (e.g. on the correct map); empty if none.</summary>
    public string Hint { get; init; } = string.Empty;

    /// <summary>0-based index of the current checkpoint; 255 = none active. Earlier render done, later pending.</summary>
    public byte CurrentIndex { get; init; } = 255;

    /// <summary>True if this quest should fire the "Quest Started" banner on first appearance (normal quests, or an
    ///     event/ambient quest whose author opted in via the editor). The server decides; the client just honours it.</summary>
    public bool AnnounceStart { get; init; }
    public List<QuestTrackerCheckpointInfo> Checkpoints { get; init; } = [];
}

/// <summary>One checkpoint row: its label, an optional live detail (e.g. "(1/3)"), and an authored explanation.</summary>
public sealed record QuestTrackerCheckpointInfo
{
    public string Label { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;

    /// <summary>Per-step explanation shown in the quest-info window ("what to do now" + where); may be empty.</summary>
    public string Description { get; init; } = string.Empty;
}

/// <summary>
///     Deserializes <see cref="QuestTrackerArgs" /> (opcode 114). Byte-for-byte mirror of the server's
///     QuestTrackerConverter. Auto-discovered by the client's packet serializer.
/// </summary>
public sealed class QuestTrackerConverter : PacketConverterBase<QuestTrackerArgs>
{
    /// <summary>The quest-tracker server opcode (matches ServerOpCode.QuestTracker on the server).</summary>
    public const byte OPCODE = 114;

    /// <inheritdoc />
    public override byte OpCode => OPCODE;

    /// <inheritdoc />
    public override QuestTrackerArgs Deserialize(ref SpanReader reader)
    {
        var quests = new List<QuestTrackerQuestInfo>();
        var questCount = reader.ReadByte();

        for (var q = 0; q < questCount; q++)
        {
            var questKey = reader.ReadString8();
            var title = reader.ReadString8();
            var description = reader.ReadString16();
            var counter = reader.ReadString8();
            var hint = reader.ReadString8();
            var currentIndex = reader.ReadByte();
            var announceStart = reader.ReadByte() != 0;

            var checkpoints = new List<QuestTrackerCheckpointInfo>();
            var checkpointCount = reader.ReadByte();

            for (var c = 0; c < checkpointCount; c++)
                checkpoints.Add(
                    new QuestTrackerCheckpointInfo
                    {
                        Label = reader.ReadString8(),
                        Detail = reader.ReadString8(),
                        Description = reader.ReadString16()
                    });

            quests.Add(
                new QuestTrackerQuestInfo
                {
                    QuestKey = questKey,
                    Title = title,
                    Description = description,
                    Counter = counter,
                    Hint = hint,
                    CurrentIndex = currentIndex,
                    AnnounceStart = announceStart,
                    Checkpoints = checkpoints
                });
        }

        var statuses = new List<QuestStartStatusInfo>();
        var statusCount = reader.ReadByte();

        for (var i = 0; i < statusCount; i++)
            statuses.Add(new QuestStartStatusInfo { Key = reader.ReadString8(), CooldownSeconds = reader.ReadUInt32() });

        var completed = new List<string>();
        var completedCount = reader.ReadByte();

        for (var i = 0; i < completedCount; i++)
            completed.Add(reader.ReadString8());

        var claimable = new List<string>();
        var claimableCount = reader.ReadByte();

        for (var i = 0; i < claimableCount; i++)
            claimable.Add(reader.ReadString8());

        return new QuestTrackerArgs
        {
            Quests = quests,
            StartStatuses = statuses,
            CompletedKeys = completed,
            ClaimableKeys = claimable
        };
    }

    /// <inheritdoc />
    public override void Serialize(ref SpanWriter writer, QuestTrackerArgs args)
    {
        // the client never sends this packet; implemented for symmetry / completeness
        writer.WriteByte((byte)args.Quests.Count);

        foreach (var quest in args.Quests)
        {
            writer.WriteString8(quest.QuestKey);
            writer.WriteString8(quest.Title);
            writer.WriteString16(quest.Description);
            writer.WriteString8(quest.Counter);
            writer.WriteString8(quest.Hint);
            writer.WriteByte(quest.CurrentIndex);
            writer.WriteByte((byte)(quest.AnnounceStart ? 1 : 0));
            writer.WriteByte((byte)quest.Checkpoints.Count);

            foreach (var checkpoint in quest.Checkpoints)
            {
                writer.WriteString8(checkpoint.Label);
                writer.WriteString8(checkpoint.Detail);
                writer.WriteString16(checkpoint.Description);
            }
        }

        writer.WriteByte((byte)args.StartStatuses.Count);

        foreach (var status in args.StartStatuses)
        {
            writer.WriteString8(status.Key);
            writer.WriteUInt32((uint)Math.Min(status.CooldownSeconds, uint.MaxValue));
        }

        writer.WriteByte((byte)args.CompletedKeys.Count);

        foreach (var key in args.CompletedKeys)
            writer.WriteString8(key);

        writer.WriteByte((byte)args.ClaimableKeys.Count);

        foreach (var key in args.ClaimableKeys)
            writer.WriteString8(key);
    }
}
