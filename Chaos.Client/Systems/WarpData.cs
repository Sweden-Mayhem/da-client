#region
using System.Reflection;
using Chaos.Client.Data;
using DALib.Data;
#endregion

namespace Chaos.Client.Systems;

/// <summary>
///     Static warp table for the town-map (T) overlay. <see cref="GetClusters" /> groups a map's warps by destination and
///     merges adjacent tiles, so a whole row/rectangle of warps to the same place shows as a single icon.
///     <para>
///         Three sources, in priority order. First the server-pushed <c>SwmWarps</c> metafile (authoritative, always
///         current, synced to <c>Data/metafile/</c> at world entry, <see cref="ReloadFromServer" />), else an external
///         <c>warps.txt</c> in the Data folder, else the embedded baked <c>warps.txt</c>. The embedded/external text is
///         one warp per line, <c>srcMapId\tsrcX\tsrcY\tdestMapId\tdestName</c>. The metafile is the same data keyed per
///         source map, one entry whose Key is the source map id and whose Properties are warps in groups of four
///         (<c>x, y, destMapId, destName</c>). <see cref="Load" /> runs the text path at boot before the metafile sync,
///         <see cref="ReloadFromServer" /> swaps in the metafile once it has been synced.
///     </para>
/// </summary>
public static class WarpData
{
    //tiles within this Chebyshev distance (with the same destination) merge into one cluster
    private const int CLUSTER_GAP = 2;

    //the server-pushed metafile carrying the warp table, see ReloadFromServer
    private const string META_FILE_NAME = "SwmWarps";

    private readonly record struct Warp(int X, int Y, int DestMapId, string DestName);

    /// <summary>A merged group of warps that lead to the same place. <see cref="TileX" />/<see cref="TileY" /> is the
    ///     (fractional) center for the icon, <see cref="RepX" />/<see cref="RepY" /> is a real warp tile in the cluster
    ///     (nearest the center) to path the player onto.</summary>
    public readonly record struct WarpCluster(float TileX, float TileY, int RepX, int RepY, int DestMapId, string DestName, int Count);

    private static readonly Dictionary<int, List<Warp>> ByMap = [];
    private static readonly Dictionary<int, List<WarpCluster>> ClusterCache = [];
    private static readonly Dictionary<int, HashSet<(int X, int Y)>> WarpTileCache = [];

    /// <summary>
    ///     True when the given tile of the given map carries a warp. Mirrors the server's collision rule ("a wall is
    ///     walkable when a warp reactor overrides it"), so stepping onto warp doors/arches is allowed client-side too.
    /// </summary>
    public static bool HasWarpAt(int mapId, int x, int y)
    {
        if (!WarpTileCache.TryGetValue(mapId, out var tiles))
        {
            tiles = [];

            if (ByMap.TryGetValue(mapId, out var warps))
                foreach (var warp in warps)
                    tiles.Add((warp.X, warp.Y));

            WarpTileCache[mapId] = tiles;
        }

        return tiles.Contains((x, y));
    }

    /// <summary>
    ///     Loads the baked warp table (external <c>Data/warps.txt</c>, else embedded). Called once at boot, before the
    ///     metafile sync, so the T-map works on first map entry and against a server that does not ship <c>SwmWarps</c>.
    ///     <see cref="ReloadFromServer" /> replaces this with the server metafile once it has been synced.
    /// </summary>
    public static void Load()
    {
        var (text, source) = ReadSource();

        Populate(ParseText(text));

        Console.WriteLine($"[warps] loaded {ByMap.Count} maps from {source}");
    }

    /// <summary>
    ///     If the server pushed a <c>SwmWarps</c> metafile (synced to <c>Data/metafile/</c> at world entry), it is the
    ///     authoritative, always-current warp table and replaces whatever <see cref="Load" /> read at boot. Call this from
    ///     the metadata-sync-complete handler. No-op (keeps the baked table) when the server ships no such metafile.
    /// </summary>
    public static void ReloadFromServer()
    {
        var meta = DataContext.MetaFiles.Get(META_FILE_NAME);

        if (meta is null or { Count: 0 })
            return;

        Populate(ParseMetaFile(meta));

        Console.WriteLine($"[warps] loaded {ByMap.Count} maps from server metafile '{META_FILE_NAME}'");
    }

    //external Data/warps.txt wins so it can refresh without a rebuild, else the embedded default
    private static (string Text, string Source) ReadSource()
    {
        var external = Path.Combine(GlobalSettings.DataPath, "warps.txt");

        if (File.Exists(external))
            return (File.ReadAllText(external), $"external {external}");

        using var stream = Assembly.GetExecutingAssembly()
                                   .GetManifestResourceStream("warps.txt");

        if (stream is null)
            return (string.Empty, "no source");

        using var reader = new StreamReader(stream);

        return (reader.ReadToEnd(), "embedded warps.txt");
    }

    //one warp per line, srcMapId \t srcX \t srcY \t destMapId \t destName
    private static IEnumerable<(int MapId, Warp Warp)> ParseText(string text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();

            if ((line.Length == 0) || (line[0] == '#'))
                continue;

            var parts = line.Split('\t');

            if (parts.Length < 5)
                continue;

            if (int.TryParse(parts[0], out var mapId)
                && int.TryParse(parts[1], out var x)
                && int.TryParse(parts[2], out var y)
                && int.TryParse(parts[3], out var destMapId))
                yield return (mapId, new Warp(x, y, destMapId, parts[4]));
        }
    }

    //server metafile, one entry per source map, Key = srcMapId, Properties = warps in groups of 4 (x, y, destMapId, destName)
    private static IEnumerable<(int MapId, Warp Warp)> ParseMetaFile(MetaFile meta)
    {
        foreach (var entry in meta)
        {
            if (!int.TryParse(entry.Key, out var mapId))
                continue;

            var props = entry.Properties;

            for (var i = 0; (i + 3) < props.Count; i += 4)
                if (int.TryParse(props[i], out var x)
                    && int.TryParse(props[i + 1], out var y)
                    && int.TryParse(props[i + 2], out var destMapId))
                    yield return (mapId, new Warp(x, y, destMapId, props[i + 3]));
        }
    }

    //rebuild ByMap and drop the cluster cache from a flat warp stream
    private static void Populate(IEnumerable<(int MapId, Warp Warp)> warps)
    {
        ByMap.Clear();
        ClusterCache.Clear();
        WarpTileCache.Clear();

        foreach (var (mapId, warp) in warps)
        {
            if (!ByMap.TryGetValue(mapId, out var list))
            {
                list = [];
                ByMap[mapId] = list;
            }

            list.Add(warp);
        }
    }

    /// <summary>Returns the clustered warps for a map (empty if none). Computed once per map and cached.</summary>
    public static IReadOnlyList<WarpCluster> GetClusters(int mapId)
    {
        if (ClusterCache.TryGetValue(mapId, out var cached))
            return cached;

        var clusters = ByMap.TryGetValue(mapId, out var warps) ? Cluster(warps) : [];
        ClusterCache[mapId] = clusters;

        return clusters;
    }

    //groups warps by destination, then merges tiles within CLUSTER_GAP of each other (Chebyshev)
    private static List<WarpCluster> Cluster(List<Warp> warps)
    {
        var result = new List<WarpCluster>();
        var used = new bool[warps.Count];

        for (var i = 0; i < warps.Count; i++)
        {
            if (used[i])
                continue;

            var dest = warps[i].DestMapId;
            var members = new List<int> { i };
            used[i] = true;

            //grow the cluster, pull in any same-destination tile near a current member
            var grew = true;

            while (grew)
            {
                grew = false;

                for (var j = 0; j < warps.Count; j++)
                {
                    if (used[j] || (warps[j].DestMapId != dest))
                        continue;

                    foreach (var m in members)
                        if ((Math.Abs(warps[j].X - warps[m].X) <= CLUSTER_GAP)
                            && (Math.Abs(warps[j].Y - warps[m].Y) <= CLUSTER_GAP))
                        {
                            members.Add(j);
                            used[j] = true;
                            grew = true;

                            break;
                        }
                }
            }

            float sx = 0;
            float sy = 0;

            foreach (var m in members)
            {
                sx += warps[m].X;
                sy += warps[m].Y;
            }

            var cx = sx / members.Count;
            var cy = sy / members.Count;

            //representative is the real warp tile nearest the center (a walkable tile to path the player onto)
            var repIndex = members[0];
            var bestDist = float.MaxValue;

            foreach (var m in members)
            {
                var dx = warps[m].X - cx;
                var dy = warps[m].Y - cy;
                var dist = (dx * dx) + (dy * dy);

                if (dist < bestDist)
                {
                    bestDist = dist;
                    repIndex = m;
                }
            }

            result.Add(
                new WarpCluster(
                    cx,
                    cy,
                    warps[repIndex].X,
                    warps[repIndex].Y,
                    dest,
                    warps[i].DestName,
                    members.Count));
        }

        return result;
    }
}
