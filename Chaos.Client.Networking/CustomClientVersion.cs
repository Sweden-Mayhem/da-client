#region
using Chaos.IO.Memory;
using Chaos.Packets.Abstractions;
#endregion

namespace Chaos.Client.Networking;

/// <summary>
///     Custom packet that carries the client's build version (e.g. "0.0.1") as a single
///     length-prefixed string. The client sends it to the login server right before the login packet
///     so the server can reject out-of-date clients. This is separate from the Dark Ages protocol
///     version (741), which stays untouched.
///
///     Opcode 0x71 (113) is unused as a Client or Server opcode and the crypto table maps it to
///     EncryptionType.Normal (the same scheme the Login packet uses). That matters because a truly random
///     opcode would fall through to the default EncryptionType.MD5, which is the in-world scheme and
///     is not set up at the login stage, so the payload arrives scrambled. The server registers a
///     matching handler on the same opcode.
/// </summary>
public sealed record CustomClientVersionArgs : IPacketSerializable
{
    public string Version { get; set; } = string.Empty;
}

/// <summary>
///     Serializes <see cref="CustomClientVersionArgs" />. Auto-discovered by the client's packet
///     serializer (it scans loaded assemblies for IPacketConverter implementations).
/// </summary>
public sealed class CustomClientVersionConverter : PacketConverterBase<CustomClientVersionArgs>
{
    /// <summary>Free, Normal-encrypted opcode reserved for the custom version handshake.</summary>
    public const byte VERSION_OPCODE = 0x71;

    /// <inheritdoc />
    public override byte OpCode => VERSION_OPCODE;

    /// <inheritdoc />
    public override CustomClientVersionArgs Deserialize(ref SpanReader reader)
        => new()
        {
            Version = reader.ReadString8()
        };

    /// <inheritdoc />
    public override void Serialize(ref SpanWriter writer, CustomClientVersionArgs args) => writer.WriteString8(args.Version);
}
