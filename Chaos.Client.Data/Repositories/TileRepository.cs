#region
using Chaos.Client.Data.Models;
using Chaos.Client.Data.Utilities;
using DALib.Drawing;
using DALib.Drawing.Virtualized;
using DALib.Definitions;
using DALib.Utility;
using System.IO.Compression;
using System.Runtime.InteropServices;
#endregion

namespace Chaos.Client.Data.Repositories;

public sealed class TileRepository
{
    private readonly TileAnimationTable BgAnimations = DatArchives.Seo.TryGetValue("gndani.tbl", out var bgAnimEntry)
        ? TileAnimationTable.FromEntry(bgAnimEntry)
        : new TileAnimationTable();

    private readonly TileAnimationTable FgAnimations = DatArchives.Ia.TryGetValue("stcani.tbl", out var fgAnimEntry)
        ? TileAnimationTable.FromEntry(fgAnimEntry)
        : new TileAnimationTable();

    private TilesetView Tileset = TilesetView.FromArchive("tilea", DatArchives.Seo);
    private bool UseSnowTileset;

    public PaletteLookup BackgroundPaletteLookup { get; } = PaletteLookup.FromArchive("mpt", DatArchives.Seo)
                                                                         .Freeze();

    public PaletteLookup ForegroundPaletteLookup { get; } = PaletteLookup.FromArchive("stc", DatArchives.Ia)
                                                                         .Freeze();

    /// <summary>
    ///     Background tile ID to ground attribute mapping (color tint, walk-blocking, foreground height override). Parsed from
    ///     gndattr.tbl.
    /// </summary>
    public Dictionary<int, GroundAttribute> GroundAttributes { get; } = DatArchives.Seo.TryGetValue("gndattr.tbl", out var gndAttrEntry)
        ? GroundAttributeParser.Parse(gndAttrEntry)
        : [];

    /// <summary>
    ///     SOTP (Sector Object Type Properties) raw byte data from the Ia archive. Each byte encodes tile properties such as
    ///     walkability for a given foreground tile index.
    /// </summary>
    public TileFlags[] SotpData { get; private set; } = DatArchives.Ia.TryGetValue("sotp.dat", out var sotpEntry)
        ? MemoryMarshal.Cast<byte, TileFlags>(sotpEntry.ToSpan())
                       .ToArray()
        : [];

    /// <summary>
    ///     Replaces <see cref="SotpData" /> with the server's collision table when the server-pushed <c>SwmSotp</c>
    ///     metafile has been synced to <c>Data/metafile</c> (server authoritative, so client walls/walkability match the
    ///     table the server checks movement against); otherwise keeps the ia.dat table. Call after the metadata sync.
    ///     New collision takes effect on the next map entry, when the renderers and pathfinding rebuild their wall data.
    /// </summary>
    public void ReloadSotpOverride()
    {
        var path = Path.Combine(DataContext.DataPath, "metafile", "SwmSotp");

        if (!File.Exists(path))
            return;

        try
        {
            using var fileStream = File.OpenRead(path);
            using var zlibStream = new ZLibStream(fileStream, CompressionMode.Decompress);
            using var memoryStream = new MemoryStream();
            zlibStream.CopyTo(memoryStream);

            var raw = memoryStream.ToArray();

            if (raw.Length == 0)
                return;

            SotpData = MemoryMarshal.Cast<byte, TileFlags>(raw)
                                    .ToArray();

            Console.WriteLine($"[sotp] using server collision table ({SotpData.Length} entries)");
        }
        //corrupt/partial sync -> keep the ia.dat table rather than break all collision
        catch (Exception ex)
        {
            Console.WriteLine($"[sotp] server collision table unreadable, keeping ia.dat: {ex.Message}");
        }
    }

    public Palettized<Tile>? GetBackgroundTile(int tileId)
    {
        if ((tileId <= 0) || ((tileId - 1) >= Tileset.Count))
            return null;

        return new Palettized<Tile>
        {
            Entity = Tileset[tileId - 1],
            Palette = BackgroundPaletteLookup.GetPaletteForId(tileId + 1)
        };
    }

    public TileAnimationEntry? GetBgAnimation(int tileId) => BgAnimations.TryGetEntry(tileId, out var entry) ? entry : null;

    public TileAnimationEntry? GetFgAnimation(int tileId) => FgAnimations.TryGetEntry(tileId, out var entry) ? entry : null;

    public Palettized<CompressedHpfFile>? GetForegroundTile(int tileId)
    {
        if (!DatArchives.Ia.TryGetValue($"stc{tileId:D5}.hpf", out var entry))
            return null;

        return new Palettized<CompressedHpfFile>
        {
            Entity = CompressedHpfFile.FromEntry(entry),
            Palette = ForegroundPaletteLookup.GetPaletteForId(tileId + 1)
        };
    }

    public void ToggleSnowTileset()
    {
        UseSnowTileset = !UseSnowTileset;

        Tileset = TilesetView.FromArchive(UseSnowTileset ? "tileas" : "tilea", DatArchives.Seo);
    }
}