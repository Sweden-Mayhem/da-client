#region
using Chaos.IO.Memory;
using Chaos.Packets.Abstractions;
#endregion

namespace Chaos.Client.Networking;

// SWM extension: the screenshot "Album" protocol (client side). Byte-for-byte mirror of the server's converters
// (Chaos.Networking.Converters.Server/Client). Opcodes are written as literals - the NuGet enums lack them - and
// match the server's ServerOpCode/ClientOpCode:
//   server -> client: Album 118, AlbumImage 119
//   client -> server: AlbumManifestRequest 118, AlbumUpload 119, AlbumDelete 120, AlbumImageRequest 122
// Reusing 118/119 across directions is safe here: the client deserializes by TYPE (Deserialize<T>) and only the
// incoming opcodes land in ConnectionManager's PacketHandlers, so there is no opcode-keyed collision.

// ---------- server -> client ----------

/// <summary>The album manifest: one entry per stored image, identified by a stable id (lazy-fetched by id).</summary>
public sealed record AlbumArgs : IPacketSerializable
{
    public List<AlbumEntryInfo> Entries { get; init; } = [];
}

/// <summary>One album image's manifest row: its stable id and a "when taken" label.</summary>
public sealed record AlbumEntryInfo
{
    public uint Id { get; init; }
    public string Date { get; init; } = string.Empty;
}

/// <summary>Deserializes <see cref="AlbumArgs" /> (ServerOpCode.Album = 118). Mirror of the server converter.</summary>
public sealed class AlbumConverter : PacketConverterBase<AlbumArgs>
{
    public const byte OPCODE = 118;

    public override byte OpCode => OPCODE;

    public override AlbumArgs Deserialize(ref SpanReader reader)
    {
        var entries = new List<AlbumEntryInfo>();
        var count = reader.ReadByte();

        for (var i = 0; i < count; i++)
            entries.Add(
                new AlbumEntryInfo
                {
                    Id = reader.ReadUInt32(),
                    Date = reader.ReadString8()
                });

        return new AlbumArgs { Entries = entries };
    }

    public override void Serialize(ref SpanWriter writer, AlbumArgs args)
    {
        writer.WriteByte((byte)args.Entries.Count);

        foreach (var entry in args.Entries)
        {
            writer.WriteUInt32(entry.Id);
            writer.WriteString8(entry.Date);
        }
    }
}

/// <summary>One CHUNK of an album image's JPEG bytes (a reply to AlbumImageRequest). A large image is split across
///     several of these in order; <see cref="Last" /> marks the final chunk so the receiver can reassemble + decode.</summary>
public sealed record AlbumImageArgs : IPacketSerializable
{
    public uint Id { get; init; }
    public byte[] Data { get; init; } = [];
    public bool Last { get; init; }
}

/// <summary>Deserializes <see cref="AlbumImageArgs" /> (ServerOpCode.AlbumImage = 119). Wire: UInt32 id, Data16 chunk, Boolean last.</summary>
public sealed class AlbumImageConverter : PacketConverterBase<AlbumImageArgs>
{
    public const byte OPCODE = 119;

    public override byte OpCode => OPCODE;

    public override AlbumImageArgs Deserialize(ref SpanReader reader)
        => new()
        {
            Id = reader.ReadUInt32(),
            Data = reader.ReadData16(),
            Last = reader.ReadBoolean()
        };

    public override void Serialize(ref SpanWriter writer, AlbumImageArgs args)
    {
        writer.WriteUInt32(args.Id);
        writer.WriteData16(args.Data);
        writer.WriteBoolean(args.Last);
    }
}

// ---------- client -> server ----------

/// <summary>A no-payload request for the album manifest, sent when the Album tab opens (ClientOpCode 118).</summary>
public sealed record AlbumManifestRequestArgs : IPacketSerializable;

public sealed class AlbumManifestRequestConverter : PacketConverterBase<AlbumManifestRequestArgs>
{
    public const byte OPCODE = 118;

    public override byte OpCode => OPCODE;

    public override AlbumManifestRequestArgs Deserialize(ref SpanReader reader) => new();

    public override void Serialize(ref SpanWriter writer, AlbumManifestRequestArgs args) { }
}

/// <summary>One CHUNK of a captured screenshot (JPEG) to store in the album (ClientOpCode 119). A large screenshot is
///     split across several of these in order; <see cref="Last" /> marks the final chunk so the server reassembles + saves.</summary>
public sealed record AlbumUploadArgs : IPacketSerializable
{
    public byte[] Data { get; init; } = [];
    public bool Last { get; init; }
}

public sealed class AlbumUploadConverter : PacketConverterBase<AlbumUploadArgs>
{
    public const byte OPCODE = 119;

    public override byte OpCode => OPCODE;

    public override AlbumUploadArgs Deserialize(ref SpanReader reader)
        => new()
        {
            Data = reader.ReadData16(),
            Last = reader.ReadBoolean()
        };

    public override void Serialize(ref SpanWriter writer, AlbumUploadArgs args)
    {
        writer.WriteData16(args.Data);
        writer.WriteBoolean(args.Last);
    }
}

/// <summary>Delete one album image by id (ClientOpCode 120).</summary>
public sealed record AlbumDeleteArgs : IPacketSerializable
{
    public uint Id { get; init; }
}

public sealed class AlbumDeleteConverter : PacketConverterBase<AlbumDeleteArgs>
{
    public const byte OPCODE = 120;

    public override byte OpCode => OPCODE;

    public override AlbumDeleteArgs Deserialize(ref SpanReader reader) => new() { Id = reader.ReadUInt32() };

    public override void Serialize(ref SpanWriter writer, AlbumDeleteArgs args) => writer.WriteUInt32(args.Id);
}

/// <summary>Fetch one album image's bytes by id (ClientOpCode 122).</summary>
public sealed record AlbumImageRequestArgs : IPacketSerializable
{
    public uint Id { get; init; }
}

public sealed class AlbumImageRequestConverter : PacketConverterBase<AlbumImageRequestArgs>
{
    public const byte OPCODE = 122;

    public override byte OpCode => OPCODE;

    public override AlbumImageRequestArgs Deserialize(ref SpanReader reader) => new() { Id = reader.ReadUInt32() };

    public override void Serialize(ref SpanWriter writer, AlbumImageRequestArgs args) => writer.WriteUInt32(args.Id);
}
