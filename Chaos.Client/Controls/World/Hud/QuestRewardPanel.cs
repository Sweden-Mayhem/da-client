#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Networking;
using Chaos.Client.Rendering;
using Chaos.Client.Systems;
using Chaos.DarkAges.Definitions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.Hud;

/// <summary>
///     The visual reward reveal shown under the "Quest Complete" banner: an EXP line + a centred row of reward
///     icons - gold (the inventory gold-bag icon + amount in yellow), items (the real inventory icon + count),
///     and legend marks (the actual mark icon + title). Fades in, holds, fades out. No plain text dumps - the
///     rewards are the real art the player will see in their bags / legend.
/// </summary>
public sealed class QuestRewardPanel : UIElement
{
    private const float DELAY = 0.25f;       //match the banner: brief wait before appearing
    private const float FADE_IN = 0.85f;     //slow, graceful fade-in
    private const float FADE_OUT = 0.9f;
    private const float HOLD = 2.0f;         //rewards linger a touch longer than the banner, but not too long
    private const float TOTAL = DELAY + FADE_IN + HOLD + FADE_OUT;
    private const float REST_FRAC = 0.205f;  //the banner's resting line (a bit higher up)
    private const int BELOW_BANNER = 80;     //the reward block sits this far under the banner centre
    private const int CELL_PAD = 14;
    private const int ICON_CAP_GAP = 4;
    private const int CAP_FONT = 14;
    private const int EXP_FONT = 16;
    private const ushort GOLD_SPRITE = 136;

    private static readonly Color ExpColor = new(150, 210, 255);
    private static readonly Color GoldColor = new(255, 224, 96);
    private static readonly Color ItemColor = new(230, 224, 208);
    private static readonly Color MarkLabelColor = new(224, 192, 108);

    private readonly record struct Cell(Texture2D? Icon, Texture2D? Caption, Color CaptionColor);

    private readonly List<Cell> Cells = [];
    private Texture2D? ExpTex;
    private float Timer;
    private bool Active;

    //captured at Show time so the whole reveal scales with the UI (icons were unreadably small at native size)
    private float Scale = 1f;
    private int ExpFontPx = EXP_FONT;
    private int CapFontPx = CAP_FONT;

    public QuestRewardPanel()
    {
        Name = "QuestRewardPanel";
        IsPassThrough = true;
        IsHitTestVisible = false;
        ZIndex = 249_000; //just below the QuestStartedBanner (250_000)
    }

    /// <summary>Build the reward cells from a completion push + start the reveal.</summary>
    public void Show(QuestCompleteArgs args)
    {
        Cells.Clear();
        ExpTex = null;

        var ui = UiRenderer.Instance;

        if ((ui is null) || !TtfTextRenderer.Available)
            return;

        //scale the whole reveal with the UI so the icons + text are readable at any window size
        Scale = ClientSettings.EffectiveWindowScale;
        ExpFontPx = Math.Max(1, (int)Math.Round(EXP_FONT * Scale));
        CapFontPx = Math.Max(1, (int)Math.Round(CAP_FONT * Scale));

        if (args.Exp > 0)
            ExpTex = TtfTextRenderer.GetLine($"+{args.Exp:N0} EXP", ExpFontPx);

        if (args.Gold > 0)
            Cells.Add(new Cell(ui.GetItemIcon(GOLD_SPRITE), TtfTextRenderer.GetLine($"{args.Gold:N0}", CapFontPx), GoldColor));

        foreach (var it in args.Items)
        {
            var cap = it.Count > 1 ? $"x{it.Count}" : it.Name;
            Cells.Add(new Cell(ui.GetItemIcon(it.Sprite, (DisplayColor)it.Color), TtfTextRenderer.GetLine(cap, CapFontPx), ItemColor));
        }

        foreach (var m in args.Marks)
            Cells.Add(new Cell(SafeMark(ui, m.Icon), TtfTextRenderer.GetLine(m.Title, CapFontPx), MarkLabelColor));

        if ((Cells.Count == 0) && (ExpTex is null))
            return;

        Timer = 0f;
        Active = true;
    }

    private static Texture2D? SafeMark(UiRenderer ui, byte icon)
    {
        try
        {
            return ui.GetEpfTexture("legends.epf", icon);
        } catch
        {
            return null;
        }
    }

    public override void Update(GameTime gameTime)
    {
        Width = ChaosGame.UiWidth;
        Height = ChaosGame.UiHeight;

        if (!Active)
            return;

        Timer += (float)gameTime.ElapsedGameTime.TotalSeconds;

        if (Timer >= TOTAL)
            Active = false;
    }

    public override void Draw(SpriteBatchEx spriteBatch)
    {
        UpdateClipRect();

        if (!Active)
            return;

        var t = Timer - DELAY;

        if (t <= 0f)
            return;

        var visible = TOTAL - DELAY;

        var alpha = t < FADE_IN
            ? t / FADE_IN
            : t > visible - FADE_OUT
                ? (visible - t) / FADE_OUT
                : 1f;

        alpha = MathHelper.Clamp(alpha, 0f, 1f);

        if (alpha <= 0.001f)
            return;

        var w = ChaosGame.UiWidth;
        var h = ChaosGame.UiHeight;
        var top = (int)(REST_FRAC * h) + BELOW_BANNER;

        //--- measure the whole block first (icons scaled by the UI scale), so a dark backdrop can sit under it ---
        var expW = ExpTex is not null ? TexW(ExpTex) : 0;
        var expH = ExpTex is not null ? TtfTextRenderer.LineHeight(ExpFontPx) + 6 : 0;

        var cellWidths = new int[Cells.Count];
        var rowW = 0;
        var iconRowH = 0;

        for (var i = 0; i < Cells.Count; i++)
        {
            var iconW = Cells[i].Icon is { } ic ? IconW(ic) : 0;
            var capW = Cells[i].Caption is { } cp ? TexW(cp) : 0;
            cellWidths[i] = Math.Max(iconW, capW) + (int)(CELL_PAD * Scale);
            rowW += cellWidths[i];

            if (Cells[i].Icon is { } ic2)
                iconRowH = Math.Max(iconRowH, IconH(ic2));
        }

        var capH = Cells.Count > 0 ? TtfTextRenderer.LineHeight(CapFontPx) : 0;
        var rowH = Cells.Count > 0 ? iconRowH + ICON_CAP_GAP + capH : 0;
        var blockW = Math.Max(expW, rowW);
        var blockH = expH + rowH;

        if ((blockW <= 0) || (blockH <= 0))
            return;

        //--- smooth dark backdrop: one soft radial glow stretched behind the block (no hard rectangle edges) ---
        var glow = GetSoftGlow();
        var gw = blockW + (int)(150 * Scale);
        var gh = blockH + (int)(120 * Scale);
        spriteBatch.Draw(glow, new Rectangle((w - gw) / 2, top + (blockH / 2) - (gh / 2), gw, gh), Color.White * (0.72f * alpha));

        //--- content over the backdrop ---
        var y = top;

        if (ExpTex is not null)
        {
            DrawWithShadow(spriteBatch, ExpTex, (w - expW) / 2, y, ExpColor * alpha, alpha);
            y += expH;
        }

        if (Cells.Count == 0)
            return;

        var x = (w - rowW) / 2;

        for (var i = 0; i < Cells.Count; i++)
        {
            var cell = Cells[i];
            var cx = x + (cellWidths[i] / 2);

            if (cell.Icon is { } icon)
            {
                var iw = IconW(icon);
                var ih = IconH(icon);
                DrawIconScaled(spriteBatch, icon, cx - (iw / 2), y, iw, ih, Color.White * alpha);
            }

            if (cell.Caption is { } cap)
            {
                var capW = TexW(cap);
                DrawWithShadow(spriteBatch, cap, cx - (capW / 2), y + iconRowH + (int)(ICON_CAP_GAP * Scale), cell.CaptionColor * alpha, alpha);
            }

            x += cellWidths[i];
        }
    }

    //a soft radial alpha blob (black RGB so it stays dark in any blend mode), built once + stretched as the backdrop
    private static Texture2D? SoftGlow;

    private static Texture2D GetSoftGlow()
    {
        if (SoftGlow is { IsDisposed: false })
            return SoftGlow;

        const int s = 128;
        var data = new Color[s * s];
        var c = (s - 1) / 2f;

        for (var yy = 0; yy < s; yy++)
            for (var xx = 0; xx < s; xx++)
            {
                var dx = (xx - c) / c;
                var dy = (yy - c) / c;
                var d = MathF.Min(1f, MathF.Sqrt((dx * dx) + (dy * dy)));
                var a = 1f - d;
                a = a * a * (3f - (2f * a)); //smoothstep falloff = no banding, soft edge

                data[(yy * s) + xx] = new Color((byte)0, (byte)0, (byte)0, (byte)(a * 255));
            }

        SoftGlow = new Texture2D(ChaosGame.Device, s, s);
        SoftGlow.SetData(data);

        return SoftGlow;
    }

    private void DrawWithShadow(SpriteBatchEx sb, Texture2D tex, int x, int y, Color color, float alpha)
    {
        var pos = new Vector2(x, y);
        var shadow = Color.Black * (alpha * alpha);
        DrawTexture(sb, tex, pos + new Vector2(1, 1), shadow);
        DrawTexture(sb, tex, pos, color);
    }

    private static int TexW(Texture2D t) => t is CachedTexture2D { AtlasRegion: { } r } ? r.SourceRect.Width : t.Width;
    private static int TexH(Texture2D t) => t is CachedTexture2D { AtlasRegion: { } r } ? r.SourceRect.Height : t.Height;

    //icon size scaled by the UI scale
    private int IconW(Texture2D t) => (int)Math.Round(TexW(t) * Scale);
    private int IconH(Texture2D t) => (int)Math.Round(TexH(t) * Scale);

    //draws an icon scaled to a dest rect, resolving a CachedTexture2D atlas region (PointClamp UI batch = crisp)
    private static void DrawIconScaled(SpriteBatchEx sb, Texture2D tex, int x, int y, int w, int h, Color color)
    {
        Texture2D atlas;
        Rectangle src;

        if (tex is CachedTexture2D { AtlasRegion: { } r })
        {
            atlas = r.Atlas;
            src = r.SourceRect;
        } else
        {
            atlas = tex;
            src = new Rectangle(0, 0, tex.Width, tex.Height);
        }

        sb.Draw(atlas, new Rectangle(x, y, w, h), src, color);
    }
}
