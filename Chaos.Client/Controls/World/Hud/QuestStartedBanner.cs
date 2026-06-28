#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.Hud;

/// <summary>
///     A full-screen overlay that announces a newly started/tracked quest: a small "Quest Started" caption above the
///     quest name in a very large font, which fades in at screen centre while rising toward the top, then fades out.
///     Pass-through + non-hit-testable, drawn last (high ZIndex) over the rest of the HUD. Announcements queue so two
///     quests starting at once show one after another.
/// </summary>
public sealed class QuestStartedBanner : UIPanel
{
    //a hold-then-eased-slide-up + fade-in, a long hold, then a gentle fade-out. DELAY keeps the visual in sync with
    //the start sound (which has its own onset delay) and lets the screen settle before the title appears.
    private const float DELAY = 0.25f;      //wait this long after Announce before the title starts appearing
    private const float FADE_IN = 0.85f;    //slide-in + fade-in (slow + graceful)
    private const float FADE_OUT = 0.9f;    //gentle fade-out at the end (no slide)
    private const float HOLD = 1.4f;        //fully-visible hold (shorter - it lingered too long)
    private const float TOTAL = DELAY + FADE_IN + HOLD + FADE_OUT; //total seconds per announcement
    private const float REST_FRAC = 0.205f; //resting vertical position (fraction of screen height) - a bit higher up
    private const float SLIDE_PX = 48f;     //how far below the resting spot it starts before sliding up

    private static readonly Color NameColor = new(255, 226, 150);
    private static readonly Color CaptionColor = new(214, 198, 156);

    private readonly Queue<(string Name, string Caption)> Pending = new();
    private (string Name, string Caption)? Current;
    private float Timer;

    public QuestStartedBanner()
    {
        Name = "QuestStartedBanner";
        IsPassThrough = true;
        IsHitTestVisible = false;
        ZIndex = 250_000; //above the HUD + windows, below only modal/critical overlays
    }

    /// <summary>Queue an announcement (caption above the quest name, e.g. "Quest Started" / "Quest Complete").</summary>
    public void Announce(string questName, string caption = "Quest Started")
    {
        if (!string.IsNullOrWhiteSpace(questName))
            Pending.Enqueue((questName.Trim(), caption));
    }

    public override void Update(GameTime gameTime)
    {
        Width = ChaosGame.UiWidth;
        Height = ChaosGame.UiHeight;

        if (Current is null)
        {
            if (Pending.Count > 0)
            {
                Current = Pending.Dequeue();
                Timer = 0f;
            }

            return;
        }

        Timer += (float)gameTime.ElapsedGameTime.TotalSeconds;

        if (Timer >= TOTAL)
            Current = Pending.Count > 0 ? Pending.Dequeue() : null;
    }

    public override void Draw(SpriteBatchEx spriteBatch)
    {
        //full-screen clip; without this DrawTexture clips to an empty rect and nothing shows
        UpdateClipRect();

        if ((Current is null) || !TtfTextRenderer.Available)
            return;

        //t = time since the title started appearing (after the initial DELAY); invisible during the delay
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

        //slide up into the resting spot while opening (eased), then stay put (the close is a pure fade)
        var slide = t < FADE_IN ? (1f - EaseOutCubic(t / FADE_IN)) * SLIDE_PX : 0f;
        var centreY = REST_FRAC * h + slide;

        var nameSize = Math.Clamp(h / 11, 40, 80);
        var capSize = Math.Clamp(nameSize * 2 / 5, 16, 30);

        var nameTex = TtfTextRenderer.GetLine(Current.Value.Name, nameSize);
        var capTex = TtfTextRenderer.GetLine(Current.Value.Caption, capSize);

        if (nameTex is null)
            return;

        var nameH = TtfTextRenderer.LineHeight(nameSize);
        var capH = capTex is null ? 0 : TtfTextRenderer.LineHeight(capSize);
        const int gap = 6;
        var blockH = capH + gap + nameH;
        var top = centreY - blockH / 2f;

        if (capTex is not null)
            DrawCentered(spriteBatch, capTex, w, (int)top, CaptionColor * alpha, alpha);

        DrawCentered(spriteBatch, nameTex, w, (int)(top + capH + gap), NameColor * alpha, alpha);
    }

    //draws a white glyph texture centred horizontally with a soft black drop shadow + 1px outline for readability
    private void DrawCentered(SpriteBatchEx spriteBatch, Texture2D tex, int screenW, int y, Color color, float alpha)
    {
        var srcW = tex is CachedTexture2D { AtlasRegion: { } r } ? r.SourceRect.Width : tex.Width;
        var x = (screenW - srcW) / 2;
        var pos = new Vector2(x, y);
        var shadow = Color.Black * (alpha * alpha);

        DrawTexture(spriteBatch, tex, pos + new Vector2(0, 3), shadow);

        for (var oy = -1; oy <= 1; oy++)
            for (var ox = -1; ox <= 1; ox++)
                if ((ox != 0) || (oy != 0))
                    DrawTexture(spriteBatch, tex, pos + new Vector2(ox, oy), shadow);

        DrawTexture(spriteBatch, tex, pos, color);
    }

    private static float EaseOutCubic(float t)
    {
        var u = 1f - MathHelper.Clamp(t, 0f, 1f);

        return 1f - (u * u * u);
    }
}
