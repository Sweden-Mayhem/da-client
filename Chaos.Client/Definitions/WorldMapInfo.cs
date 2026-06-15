namespace Chaos.Client.Definitions;

/// <summary>
///     First-draft travel info for world-map destinations, shown by the world-map window's "Info" button. Keyed by the
///     node's display name (the same text the server sends for each world-map node). The recommended levels are a ROUGH
///     starter guide and were NOT verified against this server's actual monster placements - edit freely, same spirit as
///     <see cref="StatInfo" />. Unknown destinations fall back to a generic line.
/// </summary>
public static class WorldMapInfo
{
    public readonly record struct Entry(string Recommended, string Description);

    private static readonly Entry Unknown = new(
        "Unknown",
        "No travel notes are recorded for this destination yet. Approach with care until you know the area.");

    //keyed by the world-map node text. Names follow the common Temuair towns; add/adjust as the world fills in.
    private static readonly Dictionary<string, Entry> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mileth"]   = new("Level 1 - 10", "The starting village. Safe streets, the first crypt below, gentle fields all around. Where most Aislings begin."),
        ["Abel"]     = new("Level 11 - 30", "A coastal port town. The dungeon and the surrounding shores host tougher foes than Mileth."),
        ["Rucesion"] = new("Level 11 - 41", "A walled town to the south. Its forest and the road between towns hold mid-level monsters."),
        ["Suomi"]    = new("Level 41 - 71", "A remote northern settlement. The wilds here are unforgiving to the under-leveled."),
        ["Undine"]   = new("Level 31 - 56", "A seaside town. The reefs and caves nearby suit experienced adventurers."),
        ["Piet"]     = new("Level 31 - 56", "A town built among the hills. Watch the passes and the deeper caverns."),
        ["Loures"]   = new("Level 56 - 99", "The royal capital. Its catacombs and the lands beyond are meant for veterans."),
        ["Tagor"]    = new("Level 71 - 99", "A grim eastern town. Only the well-geared should linger past its gates."),
        ["Astrid"]   = new("Level 41 - 71", "A mountain town. Cold, high, and ringed by dangerous trails."),
        ["Oren"]     = new("Level 56 - 99", "A frontier town. The surrounding ruins and beasts demand real strength."),
    };

    public static Entry Get(string? name)
        => (name is not null) && Table.TryGetValue(name.Trim(), out var entry) ? entry : Unknown;
}
