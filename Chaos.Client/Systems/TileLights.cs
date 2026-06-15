#region
using System.Globalization;
using System.Reflection;
using Chaos.Client.Data.Models;
using Chaos.Client.Rendering;
using Microsoft.Xna.Framework;
#endregion

namespace Chaos.Client.Systems;

/// <summary>How the light behaves: a steady glow, or a flame that flickers and jitters a little.</summary>
public enum LightKind
{
    Solid,
    Flame
}

/// <summary>A per-foreground-tile light definition: which tile gives off light, its shape, screen offset, colour, behaviour, the
/// foreground tile ids this light does NOT interact with (won't be blocked/shadowed by, e.g. its own lamp post), and
/// the optional named tuning extras (brightness / soft / angle / source / pool / flicker / speed).</summary>
public readonly record struct TileLightDef(
    LightShape Shape,
    int OffsetX,
    int OffsetY,
    Color Color,
    LightKind Kind,
    int Radius,
    int[] Ignore,
    float Brightness,
    float Soft,
    float AngleDeg,
    float SourceFrac,
    float PoolFrac,
    float FlickerAmount,
    float FlickerSpeed);

/// <summary>
///     Maps foreground tile ids to light definitions, loaded from <c>tilelights.ini</c> (an external
///     <c>Data/tilelights.ini</c> overrides the embedded default). When a configured foreground tile appears on a map,
///     <see cref="LightingSystem" /> places a coloured light on it. This replaces the baked <c>seo*.hea</c> light maps.
///     Edit the file to set what tiles glow, their shape/offset/colour and whether they flicker. Hand-edited, a restart
///     applies changes.
/// </summary>
public static class TileLights
{
    private const string FileName = "tilelights.ini";

    private static readonly Dictionary<int, TileLightDef> Defs = new();
    private static readonly Dictionary<(LightShape, int, int, int, int), LightMask> MaskCache = new();

    //foreground tile ids listed under [no_shadow], they never block ANY light (cast no shadow). For lamp posts, thin
    //railings, decorative bits that shouldn't throw odd shadows across a pool
    private static readonly HashSet<int> NoShadow = new();

    /// <summary>The number of configured tile lights (0 = none, so the foreground scan is skipped entirely).</summary>
    public static int Count => Defs.Count;

    //the largest configured glow radius, drives how far OFF-screen a lamp can still light the visible edge
    private static int MaxRadius;

    /// <summary>How many tiles PAST the visible bounds to scan for light emitters, so a lamp whose tile has just scrolled
    /// off-screen still casts its pool onto the visible edge instead of popping out. Covers the biggest light's reach
    /// (a spotlight's downward spill is ~1.45x its radius, one map tile row is 14px).</summary>
    public static int GatherMarginTiles => MaxRadius <= 0 ? 2 : (int)Math.Ceiling((MaxRadius * 1.6f) / 14f) + 2;

    /// <summary>Looks up the light definition for a foreground tile id, if it is configured to cast one.</summary>
    public static bool TryGet(int tileId, out TileLightDef def) => Defs.TryGetValue(tileId, out def);

    //the union of every light-emitter tile AND every per-light "ignore" tile, i.e. every tile that is PART OF a lamp
    private static readonly HashSet<int> AllLampTiles = new();

    /// <summary>True if this foreground tile is a light EMITTER (it never occludes any light - it IS the source).</summary>
    public static bool IsEmitter(int tileId) => Defs.ContainsKey(tileId);

    /// <summary>True if this foreground tile is in the global <c>[no_shadow]</c> list, it never blocks any light.</summary>
    public static bool IsGloballyNonOccluding(int tileId) => NoShadow.Contains(tileId);

    /// <summary>True if this tile is part of any lamp (an emitter, or in some light's ignore-list). Used by the baked
    /// grey ground-shadow strip so the lamp sprites don't show a hard checkerboard shadow inside the dynamic pool.</summary>
    public static bool IsNonOccluder(int tileId) => AllLampTiles.Contains(tileId);

    /// <summary>
    ///     Loads the tile-light table. An external <c>Data/tilelights.ini</c> wins so it can be edited without a rebuild,
    ///     otherwise the embedded default is used. Safe to call again to reload.
    /// </summary>
    public static void Load(string dataPath)
    {
        Defs.Clear();
        AllLampTiles.Clear();
        NoShadow.Clear();
        MaxRadius = 0;

        var external = Path.Combine(dataPath, FileName);
        string? text = null;
        string source;

        if (File.Exists(external))
        {
            text = File.ReadAllText(external);
            source = $"external {external}";
        }
        else
        {
            source = "embedded default";

            using var stream = Assembly.GetExecutingAssembly()
                                       .GetManifestResourceStream(FileName);

            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
            }
        }

        if (text is not null)
            Parse(text);

        //say exactly which file won, so an external Data/tilelights.ini silently shadowing the embedded one is clear
        Console.WriteLine($"[tilelights] loaded {source}: {Defs.Count} lights, {NoShadow.Count} no-shadow tiles");
    }

    //INI sections. Each light is its own [<tileId>] section with key = value lines (shape, x, y, color, kind, radius,
    //brightness, soft, angle, source, pool, flicker, speed, ignore). A single [no_shadow] section lists tile ids (one
    //per line, or comma-separated) that never block any light. See the ini header for what each key does
    private static void Parse(string text)
    {
        string? section = null;
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Flush()
        {
            if ((section is not null) && int.TryParse(section, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tileId))
                BuildLight(tileId, kv);

            kv.Clear();
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = StripComment(raw).Trim();

            if (line.Length == 0)
                continue;

            if (line[0] == '[')
            {
                var end = line.IndexOf(']');

                if (end <= 0)
                    continue;

                Flush();
                section = line[1..end].Trim();

                continue;
            }

            //inside [no_shadow], bare tile ids (one per line or comma-separated)
            if (IsNoShadowSection(section))
            {
                foreach (var p in line.Split(','))
                    if (int.TryParse(p.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    {
                        NoShadow.Add(id);
                        AllLampTiles.Add(id); //a non-occluder shouldn't show a baked grey ground-shadow either
                    }

                continue;
            }

            //inside a [<tileId>] section, key = value
            var eq = line.IndexOf('=');

            if (eq <= 0)
                continue;

            kv[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }

        Flush();
    }

    private static bool IsNoShadowSection(string? section)
        => section is not null && section.ToLowerInvariant() is "no_shadow" or "noshadow" or "no_light";

    private static void BuildLight(int tileId, Dictionary<string, string> kv)
    {
        var shape = LightShape.Round;

        if (kv.TryGetValue("shape", out var sh))
            TryParseShape(sh, out shape);

        var kind = LightKind.Solid;

        if (kv.TryGetValue("kind", out var kd))
            TryParseKind(kd, out kind);

        var ox = GetInt(kv, "x", 0);
        var oy = GetInt(kv, "y", 0);
        var (r, g, b) = ParseColor(kv.GetValueOrDefault("color"), 255, 220, 160);

        //tuning extras, all optional. Defaults reproduce the stock look (round soft 0.7 = a defined disc with a soft rim,
        //the spotlight defaults match the town lamp's tuned shape)
        var radius = GetInt(kv, "radius", DefaultRadius(shape));
        var brightness = Math.Clamp(GetFloat(kv, "brightness", 1f), 0f, 2f);
        var soft = Math.Clamp(GetFloat(kv, "soft", shape == LightShape.Spotlight ? 0.45f : 0.70f), 0.02f, 1f);
        var angle = Math.Clamp(GetFloat(kv, "angle", 80f), 5f, 160f);
        var sourceFrac = Math.Clamp(GetFloat(kv, "source", 0.16f), 0f, 0.9f);
        var poolFrac = Math.Clamp(GetFloat(kv, "pool", 0.38f), 0.05f, 0.95f);
        var flickAmount = Math.Clamp(GetFloat(kv, "flicker", 1f), 0f, 3f);
        var flickSpeed = Math.Clamp(GetFloat(kv, "speed", 1f), 0.1f, 5f);
        var ignore = ParseIdList(kv.GetValueOrDefault("ignore"));

        Defs[tileId] = new TileLightDef(
            shape,
            ox,
            oy,
            new Color(Clamp(r), Clamp(g), Clamp(b)),
            kind,
            radius,
            ignore,
            brightness,
            soft,
            angle,
            sourceFrac,
            poolFrac,
            flickAmount,
            flickSpeed);

        if (radius > MaxRadius)
            MaxRadius = radius;

        //record every tile that is PART OF a lamp (emitter + its per-light ignored structure) for the baked-shadow strip
        AllLampTiles.Add(tileId);

        foreach (var ig in ignore)
            AllLampTiles.Add(ig);
    }

    //cut an inline or whole-line comment (everything from the first ';' or '#')
    private static string StripComment(string s)
    {
        var i = s.IndexOfAny([';', '#']);

        return i < 0 ? s : s[..i];
    }

    private static int GetInt(Dictionary<string, string> kv, string key, int fallback)
        => kv.TryGetValue(key, out var s) && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static float GetFloat(Dictionary<string, string> kv, string key, float fallback)
        => kv.TryGetValue(key, out var s) && float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static (int R, int G, int B) ParseColor(string? s, int dr, int dg, int db)
    {
        if (string.IsNullOrWhiteSpace(s))
            return (dr, dg, db);

        var parts = s.Split(',');

        if (parts.Length < 3)
            return (dr, dg, db);

        return (ParseInt(parts[0]), ParseInt(parts[1]), ParseInt(parts[2]));
    }

    private static int[] ParseIdList(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return Array.Empty<int>();

        var list = new List<int>();

        foreach (var p in s.Split(','))
            if (int.TryParse(p.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                list.Add(id);

        return list.ToArray();
    }

    private static int ParseInt(string s) => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);

    private static bool TryParseShape(string s, out LightShape shape)
    {
        switch (s.Trim()
                 .ToLowerInvariant())
        {
            case "round":
                shape = LightShape.Round;

                return true;
            case "spotlight":
                shape = LightShape.Spotlight;

                return true;
            default:
                shape = LightShape.Round;

                return false;
        }
    }

    private static bool TryParseKind(string s, out LightKind kind)
    {
        switch (s.Trim()
                 .ToLowerInvariant())
        {
            case "solid":
                kind = LightKind.Solid;

                return true;
            case "flame":
                kind = LightKind.Flame;

                return true;
            default:
                kind = LightKind.Solid;

                return false;
        }
    }

    private static int DefaultRadius(LightShape shape) => shape == LightShape.Spotlight ? 90 : 96;

    /// <summary>
    ///     Returns (and caches) the light mask for a definition. The deferred renderer only reads its SIZE (the
    ///     gradient texture is drawn scaled to it). The spotlight's width follows the SAME aspect formula as the
    ///     gradient (<see cref="LightingRenderer.SpotlightAspect" />) so the cone is never stretched. The pixel data
    ///     (0..32 like the retail EPF lantern masks) is kept for the legacy carve path.
    /// </summary>
    public static LightMask GetMask(in TileLightDef def)
    {
        var key = (def.Shape, def.Radius, (int)MathF.Round(def.AngleDeg), (int)MathF.Round(def.SourceFrac * 100f),
            (int)MathF.Round(def.PoolFrac * 100f));

        if (MaskCache.TryGetValue(key, out var cached))
            return cached;

        LightMask mask;

        if (def.Shape == LightShape.Spotlight)
        {
            var halfH = Math.Max(1, (int)(def.Radius * 1.45f));
            var halfW = Math.Max(1, (int)(halfH * LightingRenderer.SpotlightAspect(def.AngleDeg, def.SourceFrac, def.PoolFrac)));
            mask = BuildSpotlight(halfW, halfH);
        } else
            mask = BuildRound(def.Radius);

        MaskCache[key] = mask;

        return mask;
    }

    private static LightMask BuildRound(int radius)
    {
        var size = (radius * 2) + 1;
        var pixels = new byte[size * size];

        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var dx = x - radius;
                var dy = y - radius;
                var d = MathF.Sqrt((dx * dx) + (dy * dy)) / radius;
                pixels[(y * size) + x] = Falloff(d);
            }

        return new LightMask
        {
            Width = size,
            Height = size,
            Pixels = pixels
        };
    }

    private static LightMask BuildSpotlight(int halfW, int halfH)
    {
        //a DOWNWARD teardrop sized to the cone's aspect. Only the SIZE matters to the deferred renderer, the pixel
        //teardrop is kept for the legacy carve path
        var w = (halfW * 2) + 1;
        var h = (halfH * 2) + 1;
        var pixels = new byte[w * h];

        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var dx = x - halfW;
                var dy = y - halfH;
                //up falls off fast, down reaches to the bottom of the mask (the pool)
                var vy = dy < 0 ? dy / (halfH * 0.42f) : dy / (halfH * 1.02f);
                //the cone widens going down
                var widen = dy > 0 ? 0.34f + (0.66f * (dy / (float)halfH)) : 0.34f;
                var vx = dx / Math.Max(1f, halfW * widen);
                var f = MathF.Exp(-2.7f * ((vx * vx) + (vy * vy)));
                pixels[(y * w) + x] = (byte)(f * Peak);
            }

        return new LightMask
        {
            Width = w,
            Height = h,
            Pixels = pixels
        };
    }

    //the bright-core strength (0..255). Held below full 255 so a light LIFTS the dark rather than punching a
    //full-daylight hole (which read as a hard bright ball). Full 8-bit so the carve gradient is smooth, not banded
    private const float Peak = 205f;

    //round glow, d is 0 at the centre, 1 at the edge. Gaussian is soft, no hard rim
    private static byte Falloff(float d)
    {
        if (d >= 1f)
            return 0;

        var f = MathF.Exp(-3.2f * d * d);

        return (byte)(f * Peak);
    }
}
