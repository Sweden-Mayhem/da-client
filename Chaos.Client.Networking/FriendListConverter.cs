#region
using Chaos.IO.Memory;
using Chaos.Packets.Abstractions;
#endregion

namespace Chaos.Client.Networking;

// Custom extension (client to server) that uploads the local Friends list ("O" window, up to 20 names)
// so the server can drive presence notifications (friends-online on entry, and "X has come online" pushes)
// The args type is client-local (the NuGet Chaos.Networking has no FriendList type), only the wire
// format must match the server's Chaos.Networking.Converters.Client.FriendListConverter
//   byte count, then `count` length-prefixed (String8) names
// OpCode 90 (0x5A) mirrors the server's ClientOpCode.FriendList, it is not in the NuGet enum, so it is
// written as a literal here. 90 falls through to MD5 client encryption on both ends
internal sealed record FriendListArgs : IPacketSerializable
{
    public IReadOnlyList<string> Names { get; init; } = [];
}

internal sealed class FriendListConverter : PacketConverterBase<FriendListArgs>
{
    private const int MAX_FRIENDS = 20;

    public override byte OpCode => 90;

    public override FriendListArgs Deserialize(ref SpanReader reader)
    {
        //client never receives this packet (it is client -> server only), but the base type requires a reader
        var count = reader.ReadByte();
        var names = new List<string>(count);

        for (var i = 0; i < count; i++)
            names.Add(reader.ReadString8());

        return new FriendListArgs
        {
            Names = names
        };
    }

    public override void Serialize(ref SpanWriter writer, FriendListArgs args)
    {
        var names = args.Names
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Take(MAX_FRIENDS)
                        .ToList();

        writer.WriteByte((byte)names.Count);

        foreach (var name in names)
            writer.WriteString8(name);
    }
}
