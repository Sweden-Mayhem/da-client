#region
using Chaos.Client.Collections;
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.World.Popups;
using Chaos.Client.Systems;
using Chaos.DarkAges.Definitions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.Hud;

/// <summary>
///     A small draggable circular minimap in a corner showing the town-map (M) inked parchment around the player. The
///     circle SHELL (border ring + faint dark backdrop) is always visible; only the CONTENT (the inked map, path dots,
///     entity/flag/player markers, warp names) fades in once the map for the current area is ready. Markers are composited
///     into the texture sub-pixel so they glide with the map; warp names are drawn on the GPU on top so they can overflow
///     the circle without being clipped.
/// </summary>
public sealed class MinimapControl : UIElement
{
    private const int BASE_DIAMETER = 176;
    private const int BASE_BORDER = 3;
    private const int BASE_NAME_FONT = 12;
    private const float FADE_SECONDS = 0.4f;
    private const int DRAG_THRESHOLD = 4;

    //the dark backdrop inside the circle is only this visible, so the game world shows through the empty parts
    private const float BG_OPACITY = 0.25f;

    //the most warp names that may pile at one spot before farther ones are dropped (keeps the closest)
    private const int NAME_MAX_STACK = 2;

    private static readonly Color RingColor = new(88, 72, 46);
    private static readonly Color FillColor = new(20, 14, 8);     //near-black backdrop
    private static readonly Color EnemyDot = new(222, 70, 58);
    private static readonly Color NpcDot = new(236, 202, 96);
    private static readonly Color PlayerDot = new(150, 210, 255);
    private static readonly Color FlagColor = new(222, 44, 44);
    private static readonly Color PathDot = Color.Black;          //soft black dots (drawn ~40% alpha) for the walk route
    private static readonly Color DotOutline = new(8, 5, 2);      //near-black halo so a dot reads on any map colour
    private static readonly Color NameColor = new(244, 236, 220);
    private static readonly Color NameOutline = Color.Black;

    private readonly GraphicsDevice Device;
    private Texture2D? Texture;
    private Color[] Buffer = new Color[BASE_DIAMETER * BASE_DIAMETER];

    //scale-based sizes (the circle SIZE scales with the option; tile coverage does not)
    private int Diam = BASE_DIAMETER;
    private int Border = BASE_BORDER;
    private int NameFont = BASE_NAME_FONT;
    private float Mag = 1f;

    private TownMapControl? Source;
    private Vector2 CenterWorld;
    private float InkedRadius = 60f;
    private IReadOnlyList<WarpData.WarpCluster> Warps = [];
    private IReadOnlyList<Point> PathTiles = [];
    private Point? Target;

    private Vector2 CenterInked;
    private float Zoom = 1f;
    private bool HasMap;
    private float FadeAlpha; //the CONTENT fade (map ink + markers). The shell (ring + backdrop) ignores this and is always shown.
    private float Dt;        //last frame delta, for the per-name fade bookkeeping
    private int FadeGen = int.MinValue; //the map generation the current content fade is for (a change restarts it)

    private bool Dragging;
    private bool PressArmed; //true only while a left-press that BEGAN on the minimap is held (so world clicks never drag it)
    private bool WasVisible; //re-fade the content in every time the minimap (re)appears
    private bool Placed;
    private int PressX, PressY, DragOffX, DragOffY;

    //warp-name placement + per-destination fade (so a name eases in when it enters range and out when it leaves / is crowded out)
    private readonly List<Rectangle> PlacedLabels = [];
    private readonly Dictionary<int, (string Name, Vector2 Local, float DistSq)> NearestPerDest = [];
    private readonly List<(int Id, string Name, Vector2 Local, float DistSq)> SortedLabels = [];
    private readonly Dictionary<int, float> DestFade = [];
    private readonly Dictionary<int, (Vector2 Local, string Name)> DestLast = [];
    private readonly Dictionary<int, Vector2> DestPos = []; //the SMOOTHED on-screen local pos per destination (eases to target)
    private Vector2 PrevCenterInked;
    private readonly HashSet<int> ShownIds = [];
    private readonly List<int> ScratchIds = [];

    //how fast a name slides to a new slot (stacking shift / a closer warp taking the name). Lower = snappier.
    private const float NAME_SLIDE_TAU = 0.07f;

    //warp names are GPU-drawn ON TOP (LinearClamp) so they overflow the circle freely and stay smooth. Each entry is a
    //pre-outlined name texture, its position relative to the control's top-left, and its fade.
    private readonly List<(Texture2D Tex, Vector2 Local, float Fade)> NameDraws = [];
    private readonly Dictionary<(string Text, int Size), Texture2D> NameTexCache = [];

    /// <summary>Raised when the player clicks a spot on the minimap: the tile to walk to.</summary>
    public event Action<int, int>? TargetPicked;

    public MinimapControl(GraphicsDevice device)
    {
        Device = device;
        Width = Diam;
        Height = Diam;
        Visible = false;
    }

    public void SetSource(
        TownMapControl source,
        Vector2 centerWorld,
        float inkedRadius,
        IReadOnlyList<WarpData.WarpCluster> warps,
        IReadOnlyList<Point> pathTiles,
        Point? target)
    {
        Source = source;
        CenterWorld = centerWorld;
        InkedRadius = Math.Max(8f, inkedRadius);
        Warps = warps;
        PathTiles = pathTiles;
        Target = target;
    }

    public override bool ContainsPoint(int screenX, int screenY)
    {
        if (!Visible)
            return false;

        var r = Diam / 2;
        var dx = screenX - (ScreenX + r);
        var dy = screenY - (ScreenY + r);

        return ((dx * dx) + (dy * dy)) <= (r * r);
    }

    public override void OnMouseDown(MouseDownEvent e)
    {
        if (e.Button == MouseButton.Left)
        {
            PressArmed = true; //a drag is only allowed when the press STARTED here
            PressX = e.ScreenX;
            PressY = e.ScreenY;
            DragOffX = e.ScreenX - X;
            DragOffY = e.ScreenY - Y;
        }

        e.Handled = true; //swallow so the click never reaches the world behind the minimap
    }

    public override void OnClick(ClickEvent e)
    {
        //a genuine click (no drag) walks the player to the clicked map spot
        if (!Dragging && (Source is not null) && HasMap)
        {
            var r = Diam / 2;
            var inked = new Vector2(
                CenterInked.X + ((e.ScreenX - (ScreenX + r)) / Zoom),
                CenterInked.Y + ((e.ScreenY - (ScreenY + r)) / Zoom));
            var (tx, ty) = Source.TileForInkedPixel(inked);
            TargetPicked?.Invoke(tx, ty);
        }

        e.Handled = true;
    }

    //apply the "Minimap size" option: a bigger circle, same tile coverage. Reallocates the texture/buffer on change.
    private void ApplyScale()
    {
        var scale = Math.Clamp(ClientSettings.MinimapScale, 0.1f, 4f);
        var diam = Math.Max(32, (int)MathF.Round(BASE_DIAMETER * scale));

        if (diam == Diam)
            return;

        Diam = diam;
        Mag = diam / (float)BASE_DIAMETER;
        Border = Math.Max(1, (int)MathF.Round(BASE_BORDER * Mag));
        NameFont = Math.Max(7, (int)MathF.Round(BASE_NAME_FONT * Mag));
        Width = diam;
        Height = diam;
        Buffer = new Color[diam * diam];
        Texture?.Dispose();
        Texture = null;
        ClearNameTextures(); //outline thickness tracks Mag; rebuild name textures at the new size
    }

    public override void Update(GameTime gameTime)
    {
        if (!Visible)
        {
            Dragging = false;
            PressArmed = false;
            WasVisible = false;

            return;
        }

        ApplyScale();

        //re-fade the CONTENT in every time the minimap appears (toggle on, or first show)
        if (!WasVisible)
        {
            FadeAlpha = 0f;
            DestFade.Clear();
            DestLast.Clear();
            DestPos.Clear();
            FadeGen = int.MinValue;
            WasVisible = true;
        }

        if (!Placed)
        {
            X = ClientSettings.MinimapX >= 0 ? ClientSettings.MinimapX : (ChaosGame.UiWidth - Diam - 12);
            Y = ClientSettings.MinimapY >= 0 ? ClientSettings.MinimapY : 12;
            Placed = true;
        }

        //drag ONLY when the left-press began on the minimap (PressArmed), else a world click whose held button is far from
        //a stale press point would teleport the minimap to the cursor
        if (PressArmed && InputBuffer.IsLeftButtonHeld)
        {
            if (!Dragging && ((Math.Abs(InputBuffer.MouseX - PressX) >= DRAG_THRESHOLD) || (Math.Abs(InputBuffer.MouseY - PressY) >= DRAG_THRESHOLD)))
                Dragging = true;

            if (Dragging)
            {
                X = InputBuffer.MouseX - DragOffX;
                Y = InputBuffer.MouseY - DragOffY;
            }
        }
        else if (!InputBuffer.IsLeftButtonHeld)
        {
            if (Dragging)
            {
                ClientSettings.MinimapX = X;
                ClientSettings.MinimapY = Y;
                ClientSettings.Save();
            }

            Dragging = false;
            PressArmed = false;
        }

        X = Math.Clamp(X, 0, Math.Max(0, ChaosGame.UiWidth - Diam));
        Y = Math.Clamp(Y, 0, Math.Max(0, ChaosGame.UiHeight - Diam));

        var r = Diam / 2;
        Zoom = (r - Border) / InkedRadius;

        var gen = Source?.MinimapMapGen ?? -1;
        HasMap = gen >= 0;

        //restart the CONTENT fade whenever the loaded map changes (the shell stays solid throughout)
        if (gen != FadeGen)
        {
            FadeGen = gen;
            FadeAlpha = 0f;
            DestFade.Clear();
            DestLast.Clear();
            DestPos.Clear();
        }

        Dt = (float)gameTime.ElapsedGameTime.TotalSeconds;

        FadeAlpha = HasMap ? Math.Min(1f, FadeAlpha + (Dt / FADE_SECONDS)) : 0f;

        CenterInked = HasMap ? Source!.InkedPixelForWorld(CenterWorld) : Vector2.Zero;

        Rebuild();
    }

    //builds the circle texture: the SHELL (faint backdrop + border ring) is always at full opacity; the map ink and all the
    //stamped markers fade in by FadeAlpha. Warp names are collected for the GPU pass (not stamped here).
    private void Rebuild()
    {
        var r = Diam / 2;
        Color[]? pixels = null;
        var mw = 0;
        var mh = 0;

        if (HasMap)
            Source!.TryGetMinimapPixels(out pixels, out mw, out mh);

        //the faint backdrop, premultiplied (near-black * 25%) - always present
        var bg = new Vector4(FillColor.R / 255f, FillColor.G / 255f, FillColor.B / 255f, 1f) * BG_OPACITY;
        var ringVec = new Vector4(RingColor.R / 255f, RingColor.G / 255f, RingColor.B / 255f, 1f); //opaque border
        var outer = (float)r;
        var inner = (float)(r - Border);

        for (var my = 0; my < Diam; my++)
            for (var mx = 0; mx < Diam; mx++)
            {
                float dx = mx - r;
                float dy = my - r;
                var dist = MathF.Sqrt((dx * dx) + (dy * dy));

                if (dist > outer + 1f)
                {
                    Buffer[(my * Diam) + mx] = Color.Transparent;

                    continue;
                }

                //map ink (premultiplied) faded by FadeAlpha, composited OVER the always-present backdrop
                var s = (HasMap && (pixels is not null))
                    ? SampleInked(pixels, mw, mh, CenterInked.X + (dx / Zoom), CenterInked.Y + (dy / Zoom))
                    : Vector4.Zero;

                var mf = s * FadeAlpha;
                var interior = mf + (bg * (1f - mf.W));

                //blend interior -> ring across a 1px-AA band at the inner edge, then -> transparent across a 1px-AA outer
                //rim, so BOTH the ring's edges are smooth (matches the smoothness of the map underneath)
                var ringW = Math.Clamp(dist - inner + 0.5f, 0f, 1f);
                var blended = Vector4.Lerp(interior, ringVec, ringW);
                var outerCov = Math.Clamp(outer + 0.5f - dist, 0f, 1f);
                var outc = blended * outerCov;

                Buffer[(my * Diam) + mx] = new Color(
                    (byte)Math.Clamp(outc.X * 255f, 0f, 255f),
                    (byte)Math.Clamp(outc.Y * 255f, 0f, 255f),
                    (byte)Math.Clamp(outc.Z * 255f, 0f, 255f),
                    (byte)Math.Clamp(outc.W * 255f, 0f, 255f));
            }

        StampOverlays();  //path / entity dots / flag / player (content, faded)
        BuildNameDraws(); //warp names for the GPU pass

        Texture ??= new Texture2D(Device, Diam, Diam);
        Texture.SetData(Buffer);
    }

    //CPU-stamps the moving markers into the buffer so they scroll as smoothly as the map. Content, faded by FadeAlpha.
    private void StampOverlays()
    {
        var r = Diam / 2;
        var a = FadeAlpha;

        if (a <= 0.02f)
            return;

        if ((Source is not null) && HasMap)
        {
            //walk path as a dotted trail, drawn BACKWARDS from the flag (destination) toward the player, every other step
            var pathRad = Math.Max(1.3f, 1.8f * Mag);

            for (var i = PathTiles.Count - 1; i >= 0; i -= 2)
            {
                //skip a dot that lands on the flag tile, it just renders under the flag and looks messy
                if (Target is { } tt && (PathTiles[i].X == tt.X) && (PathTiles[i].Y == tt.Y))
                    continue;

                var p = ToLocal(Source.InkedPixelForTile(PathTiles[i].X, PathTiles[i].Y));
                StampDisc(p.X, p.Y, pathRad, PathDot, a * 0.4f);                          //soft black dot
                StampDisc(p.X, p.Y, pathRad * 0.5f, Color.White, a * 0.4f);               //translucent white centre
            }

            //enemies / NPCs / other players
            foreach (var e in WorldState.GetEntities())
            {
                if ((e.Id == WorldState.PlayerEntityId) || e.IsHidden)
                    continue;

                Color? dot = e.Type switch
                {
                    ClientEntityType.Aisling                                             => PlayerDot,
                    ClientEntityType.Creature when e.CreatureType == CreatureType.Normal => EnemyDot,
                    ClientEntityType.Creature                                            => NpcDot,
                    _                                                                    => null
                };

                if (dot is null)
                    continue;

                var p = ToLocal(Source.InkedPixelForTile(e.TileX, e.TileY));
                StampDot(p.X, p.Y, dot.Value, a);
            }

            //the move target as a small red flag
            if (Target is { } t)
            {
                var p = ToLocal(Source.InkedPixelForTile(t.X, t.Y));
                StampFlag(p.X, p.Y, a);
            }
        }

        //the player, always at the centre
        StampDot(r, r, PlayerDot, a);
    }

    //a dot = a near-black halo disc with a coloured core, both stamped sub-pixel
    private void StampDot(float cx, float cy, Color color, float alpha)
    {
        StampDisc(cx, cy, Math.Max(1.6f, 2.4f * Mag), DotOutline, alpha);
        StampDisc(cx, cy, Math.Max(0.8f, 1.4f * Mag), color, alpha);
    }

    private void StampFlag(float cx, float cy, float alpha)
    {
        var pole = Math.Max(1.2f, 1.2f * Mag);
        var poleH = Math.Max(6f, 8f * Mag);
        var fw = Math.Max(5f, 7f * Mag);
        var fh = Math.Max(3f, 5f * Mag);

        StampRect(cx, cy - poleH, pole, poleH, DotOutline, alpha);                       //pole
        StampRect(cx + pole, cy - poleH, fw, fh, DotOutline, alpha);                     //flag outline
        StampRect(cx + pole + 1f, cy - poleH + 1f, fw - 2f, fh - 2f, FlagColor, alpha);  //flag fill
    }

    //collects the warp names to draw (nearest-per-destination, declutter, per-destination fade in/out). They are drawn on
    //the GPU in Draw so they can overflow the circle without clipping. Works in LOCAL coords (0..Diam from the top-left).
    private void BuildNameDraws()
    {
        NameDraws.Clear();

        if ((Source is null) || !HasMap || !TtfTextRenderer.Available)
            return;

        PlacedLabels.Clear();
        ShownIds.Clear();
        NearestPerDest.Clear();
        var r = Diam / 2;
        var lim = (float)(r - Border);

        //everything shifts by this much in local space as the player walks (the map scrolls). Adding it back in before the
        //ease means the smoothing does NOT fight the scroll - only genuine slot changes (stacking / reassignment) ease.
        var scrollShift = (PrevCenterInked - CenterInked) * Zoom;
        var smooth = 1f - MathF.Exp(-Dt / NAME_SLIDE_TAU);

        //in-range candidates, keeping the nearest warp per destination map (one name per place)
        foreach (var warp in Warps)
        {
            if (string.IsNullOrEmpty(warp.DestName))
                continue;

            var p = ToLocal(Source.InkedPixelForTile(warp.TileX, warp.TileY));
            var ddx = p.X - r;
            var ddy = p.Y - r;
            var distSq = (ddx * ddx) + (ddy * ddy);

            if (distSq > lim * lim) //the marker (center pixel) must be visible
                continue;

            if (!NearestPerDest.TryGetValue(warp.DestMapId, out var cur) || (distSq < cur.DistSq))
                NearestPerDest[warp.DestMapId] = (warp.DestName, p, distSq);
        }

        //nearest first, so the closest warps claim space and crowded-out farther ones are the ones dropped
        SortedLabels.Clear();

        foreach (var kv in NearestPerDest)
            SortedLabels.Add((kv.Key, kv.Value.Name, kv.Value.Local, kv.Value.DistSq));

        SortedLabels.Sort(static (l, r) => l.DistSq.CompareTo(r.DistSq));

        foreach (var (id, name, p, _) in SortedLabels)
        {
            if (GetNameTexture(name) is not { } tex)
                continue;

            var w = tex.Width;
            var h = tex.Height;
            var nx = p.X - (w / 2f);
            var ny = p.Y - (3f * Mag) - h; //sit just above the marker (names overflow the circle freely - no clamp)

            //try the spot, then a couple of tighter stacks; if it still collides it is too crowded -> hide it (fades out)
            var placed = false;
            var rect = LabelRect(nx, ny, w, h);

            for (var step = 0; step <= NAME_MAX_STACK; step++)
            {
                if (!LabelClashes(rect))
                {
                    placed = true;

                    break;
                }

                ny -= Math.Max(1f, h - (3f * Mag));
                rect = LabelRect(nx, ny, w, h);
            }

            if (!placed)
                continue;

            //keep the middle of the name inside the circle radius, the edges may overflow
            //so pull it radially inward when stacking pushed the centre past the rim
            var mcx = nx + (w / 2f);
            var mcy = ny + (h / 2f);
            var rcx = mcx - r;
            var rcy = mcy - r;
            var cd = MathF.Sqrt((rcx * rcx) + (rcy * rcy));

            if (cd > r)
            {
                var k = r / cd;
                nx = (r + (rcx * k)) - (w / 2f);
                ny = (r + (rcy * k)) - (h / 2f);
                rect = LabelRect(nx, ny, w, h);
            }

            PlacedLabels.Add(rect);
            ShownIds.Add(id);

            //ease the on-screen position toward the new target slot. Walking (the scroll) is compensated, so only stacking
            //shifts and a closer warp taking the name slide smoothly instead of jumping in one frame.
            var target = new Vector2(nx, ny);
            var cur = DestPos.TryGetValue(id, out var prev) ? Vector2.Lerp(prev + scrollShift, target, smooth) : target;
            DestPos[id] = cur;
            DestLast[id] = (cur, name);
        }

        //ramp each destination's fade toward 1 if shown this frame, else toward 0 (drawn at its last position while fading out)
        ScratchIds.Clear();
        ScratchIds.AddRange(DestFade.Keys);

        foreach (var id in ShownIds)
            if (!DestFade.ContainsKey(id))
            {
                DestFade[id] = 0f;
                ScratchIds.Add(id);
            }

        foreach (var id in ScratchIds)
        {
            var target = ShownIds.Contains(id) ? 1f : 0f;

            //a fading-out name isn't placed this frame; keep it drifting with the map scroll so it doesn't freeze in place
            if ((target == 0f) && DestPos.TryGetValue(id, out var fp) && DestLast.TryGetValue(id, out var fl))
            {
                var moved = fp + scrollShift;
                DestPos[id] = moved;
                DestLast[id] = (moved, fl.Name);
            }

            var f = DestFade[id];
            f = target > f ? Math.Min(1f, f + (Dt / FADE_SECONDS)) : Math.Max(0f, f - (Dt / FADE_SECONDS));

            if ((f <= 0.001f) && (target == 0f))
            {
                DestFade.Remove(id);
                DestLast.Remove(id);
                DestPos.Remove(id);

                continue;
            }

            DestFade[id] = f;

            if ((f > 0.02f) && DestLast.TryGetValue(id, out var info) && (GetNameTexture(info.Name) is { } t))
                NameDraws.Add((t, info.Local, f));
        }

        PrevCenterInked = CenterInked;
    }

    private static Rectangle LabelRect(float x, float y, int w, int h)
        => new((int)MathF.Round(x), (int)MathF.Round(y), w, h);

    //clash test that lets names sit a little closer: each box is deflated before testing so a few px of overlap is allowed
    private bool LabelClashes(Rectangle rect)
    {
        var pad = (int)MathF.Max(2f, 3f * Mag);
        var test = new Rectangle(rect.X + pad, rect.Y + 1, Math.Max(1, rect.Width - (2 * pad)), Math.Max(1, rect.Height - 2));

        foreach (var placed in PlacedLabels)
        {
            var p = new Rectangle(placed.X + pad, placed.Y + 1, Math.Max(1, placed.Width - (2 * pad)), Math.Max(1, placed.Height - 2));

            if (p.Intersects(test))
                return true;
        }

        return false;
    }

    //builds + caches a name with its black outline baked in at full opacity, as a premultiplied texture. Because the outline
    //is baked ONCE here, fading it later (a single tinted draw) scales the whole image uniformly - the black outline fades
    //in lockstep with the text instead of lingering.
    private Texture2D? GetNameTexture(string text)
    {
        var key = (text, NameFont);

        if (NameTexCache.TryGetValue(key, out var cached))
            return cached;

        if (TtfTextRenderer.GetLine(text, NameFont) is not { } glyphTex)
            return null;

        var gw = glyphTex.Width;
        var gh = glyphTex.Height;
        var src = new Color[gw * gh];
        glyphTex.GetData(src);

        var o = Math.Max(1, (int)MathF.Round(Mag)); //outline thickness
        var w = gw + (2 * o);
        var h = gh + (2 * o);
        var buf = new Color[w * h]; //starts transparent

        //solid black outline (8 surrounding copies), then the bright text on top, all at full opacity
        BlitGlyph(buf, w, src, gw, gh, 0, o, NameOutline);
        BlitGlyph(buf, w, src, gw, gh, 2 * o, o, NameOutline);
        BlitGlyph(buf, w, src, gw, gh, o, 0, NameOutline);
        BlitGlyph(buf, w, src, gw, gh, o, 2 * o, NameOutline);
        BlitGlyph(buf, w, src, gw, gh, 0, 0, NameOutline);
        BlitGlyph(buf, w, src, gw, gh, 2 * o, 0, NameOutline);
        BlitGlyph(buf, w, src, gw, gh, 0, 2 * o, NameOutline);
        BlitGlyph(buf, w, src, gw, gh, 2 * o, 2 * o, NameOutline);
        BlitGlyph(buf, w, src, gw, gh, o, o, NameColor);

        var tex = new Texture2D(Device, w, h);
        tex.SetData(buf);
        NameTexCache[key] = tex;

        return tex;
    }

    private void ClearNameTextures()
    {
        foreach (var t in NameTexCache.Values)
            t.Dispose();

        NameTexCache.Clear();
    }

    //composites a premultiplied-white glyph, tinted, at an integer offset into a destination buffer (premultiplied over)
    private static void BlitGlyph(Color[] dst, int dw, Color[] src, int sw, int sh, int ox, int oy, Color tint)
    {
        float tr = tint.R / 255f;
        float tg = tint.G / 255f;
        float tb = tint.B / 255f;

        for (var sy = 0; sy < sh; sy++)
            for (var sx = 0; sx < sw; sx++)
            {
                var s = src[(sy * sw) + sx];

                if (s.A == 0)
                    continue;

                var idx = ((oy + sy) * dw) + (ox + sx);
                var d = dst[idx];
                var sa = s.A / 255f;
                var inv = 1f - sa;

                dst[idx] = new Color(
                    (byte)Math.Clamp(((s.R / 255f * tr) + (d.R / 255f * inv)) * 255f, 0f, 255f),
                    (byte)Math.Clamp(((s.G / 255f * tg) + (d.G / 255f * inv)) * 255f, 0f, 255f),
                    (byte)Math.Clamp(((s.B / 255f * tb) + (d.B / 255f * inv)) * 255f, 0f, 255f),
                    (byte)Math.Clamp((sa + (d.A / 255f * inv)) * 255f, 0f, 255f));
            }
    }

    //inked-pixel space -> texture-local (0..Diam) space, the same continuous mapping the map crop uses, so a stamped marker
    //tracks the map exactly as the player walks (no whole-pixel snapping)
    private Vector2 ToLocal(Vector2 inked)
    {
        var r = Diam / 2;

        return new Vector2(r + ((inked.X - CenterInked.X) * Zoom), r + ((inked.Y - CenterInked.Y) * Zoom));
    }

    //sub-pixel anti-aliased filled disc, premultiplied-composited into the buffer and clipped to the minimap circle
    private void StampDisc(float cx, float cy, float radius, Color color, float alpha)
    {
        if (alpha <= 0f)
            return;

        var r = Diam / 2;
        var lim = r - Border;
        var minx = Math.Max(0, (int)MathF.Floor(cx - radius - 1f));
        var maxx = Math.Min(Diam - 1, (int)MathF.Ceiling(cx + radius + 1f));
        var miny = Math.Max(0, (int)MathF.Floor(cy - radius - 1f));
        var maxy = Math.Min(Diam - 1, (int)MathF.Ceiling(cy + radius + 1f));

        for (var y = miny; y <= maxy; y++)
            for (var x = minx; x <= maxx; x++)
            {
                float cdx = x + 0.5f - r;
                float cdy = y + 0.5f - r;

                if ((cdx * cdx) + (cdy * cdy) > lim * lim) //clip to the circle
                    continue;

                float dx = x + 0.5f - cx;
                float dy = y + 0.5f - cy;
                var cov = Math.Clamp(radius - MathF.Sqrt((dx * dx) + (dy * dy)) + 0.5f, 0f, 1f);

                if (cov > 0f)
                    Blend(x, y, color, cov * alpha);
            }
    }

    //sub-pixel anti-aliased filled rect (coverage = box overlap), premultiplied-composited and clipped to the circle
    private void StampRect(float rx, float ry, float rw, float rh, Color color, float alpha)
    {
        if ((alpha <= 0f) || (rw <= 0f) || (rh <= 0f))
            return;

        var r = Diam / 2;
        var lim = r - Border;
        var minx = Math.Max(0, (int)MathF.Floor(rx));
        var maxx = Math.Min(Diam - 1, (int)MathF.Ceiling(rx + rw) - 1);
        var miny = Math.Max(0, (int)MathF.Floor(ry));
        var maxy = Math.Min(Diam - 1, (int)MathF.Ceiling(ry + rh) - 1);

        for (var y = miny; y <= maxy; y++)
            for (var x = minx; x <= maxx; x++)
            {
                float cdx = x + 0.5f - r;
                float cdy = y + 0.5f - r;

                if ((cdx * cdx) + (cdy * cdy) > lim * lim)
                    continue;

                var covX = Math.Clamp(Math.Min(rx + rw, x + 1f) - Math.Max(rx, x), 0f, 1f);
                var covY = Math.Clamp(Math.Min(ry + rh, y + 1f) - Math.Max(ry, y), 0f, 1f);
                var cov = covX * covY;

                if (cov > 0f)
                    Blend(x, y, color, cov * alpha);
            }
    }

    //solid colour at a coverage alpha, premultiplied-composited into the buffer
    private void Blend(int x, int y, Color color, float srcA)
        => BlendPremul(x, y, color.R / 255f * srcA, color.G / 255f * srcA, color.B / 255f * srcA, srcA);

    //premultiplied source-over into the premultiplied buffer (src components already premultiplied, 0..1)
    private void BlendPremul(int x, int y, float sr, float sg, float sb, float sa)
    {
        if (sa <= 0f)
            return;

        var idx = (y * Diam) + x;
        var dst = Buffer[idx];
        var inv = 1f - sa;

        Buffer[idx] = new Color(
            (byte)Math.Clamp((sr + (dst.R / 255f * inv)) * 255f, 0f, 255f),
            (byte)Math.Clamp((sg + (dst.G / 255f * inv)) * 255f, 0f, 255f),
            (byte)Math.Clamp((sb + (dst.B / 255f * inv)) * 255f, 0f, 255f),
            (byte)Math.Clamp((sa + (dst.A / 255f * inv)) * 255f, 0f, 255f));
    }

    //bilinearly samples the baked inked map (premultiplied; out-of-bounds is transparent so beyond-the-map shows only bg)
    private static Vector4 SampleInked(Color[] px, int w, int h, float fx, float fy)
    {
        var x0 = (int)MathF.Floor(fx);
        var y0 = (int)MathF.Floor(fy);
        var tx = fx - x0;
        var ty = fy - y0;

        var a = Texel(px, w, h, x0, y0);
        var b = Texel(px, w, h, x0 + 1, y0);
        var c = Texel(px, w, h, x0, y0 + 1);
        var d = Texel(px, w, h, x0 + 1, y0 + 1);

        return Vector4.Lerp(Vector4.Lerp(a, b, tx), Vector4.Lerp(c, d, tx), ty);
    }

    //one inked texel as a premultiplied 0..1 Vector4; out-of-bounds is fully transparent
    private static Vector4 Texel(Color[] px, int w, int h, int x, int y)
    {
        if ((x < 0) || (x >= w) || (y < 0) || (y >= h))
            return Vector4.Zero;

        var s = px[(y * w) + x];

        return new Vector4(s.R / 255f, s.G / 255f, s.B / 255f, s.A / 255f);
    }

    public override void Draw(SpriteBatch spriteBatch)
    {
        if (!Visible || (Texture is null))
            return;

        base.Draw(spriteBatch);

        //the circle texture holds the map, the path/markers and the always-on border
        spriteBatch.Draw(Texture, new Vector2(ScreenX, ScreenY), Color.White);

        //warp names: a separate LinearClamp pass ON TOP so they can overflow the circle (no clip) and move smoothly. The
        //surrounding native-UI batch is begun with samplerState: GlobalSettings.Sampler (point), so restore exactly that.
        if (NameDraws.Count > 0)
        {
            spriteBatch.End();
            spriteBatch.Begin(samplerState: SamplerState.LinearClamp);

            foreach (var (tex, local, fade) in NameDraws)
                spriteBatch.Draw(tex, new Vector2(ScreenX + local.X, ScreenY + local.Y), Color.White * fade);

            spriteBatch.End();
            spriteBatch.Begin(samplerState: GlobalSettings.Sampler);
        }
    }

    public override void Dispose()
    {
        Texture?.Dispose();
        Texture = null;
        ClearNameTextures();
        base.Dispose();
    }
}
