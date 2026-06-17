#region
using System.Collections.Frozen;
#endregion

namespace Chaos.Client.Definitions;

/// <summary>Hardcoded sign foreground tile IDs. Signs are tall sprites that extend above their base tile.</summary>
public static class SignTable
{
    private static readonly FrozenSet<short> SignIds = new HashSet<short>
    {
        733, 734, 735, 736, 737, 738, 739, 740
    }.ToFrozenSet();

    /// <summary>True when the tile ID belongs to a sign foreground sprite.</summary>
    public static bool IsSignTileId(short tileId) => SignIds.Contains(tileId);
}
