#region
using Chaos.IO.Memory;
using Chaos.Packets.Abstractions;
#endregion

namespace Chaos.Client.Networking;

// SWM extension: the player's OWN profile picture AND profile text, pushed by the server (server is the source of
// truth - the per-character portrait.jpg + profile.txt, also editable from the website). Byte-for-byte mirror of the
// server's SelfPortraitConverter. Opcode written as a literal (the NuGet enum lacks it); matches ServerOpCode.SelfPortrait.

/// <summary>The player's own profile picture (encoded JPEG; empty if none set) and profile text.</summary>
public sealed record SelfPortraitArgs : IPacketSerializable
{
    public byte[] Portrait { get; init; } = [];
    public string ProfileText { get; init; } = string.Empty;
}

/// <summary>Deserializes <see cref="SelfPortraitArgs" /> (ServerOpCode.SelfPortrait = 120). Wire: Data16 portrait, String16 text.</summary>
public sealed class SelfPortraitConverter : PacketConverterBase<SelfPortraitArgs>
{
    public const byte OPCODE = 120;

    public override byte OpCode => OPCODE;

    public override SelfPortraitArgs Deserialize(ref SpanReader reader)
        => new()
        {
            Portrait = reader.ReadData16(),
            ProfileText = reader.ReadString16()
        };

    public override void Serialize(ref SpanWriter writer, SelfPortraitArgs args)
    {
        writer.WriteData16(args.Portrait);
        writer.WriteString16(args.ProfileText);
    }
}
