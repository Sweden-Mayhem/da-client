#region
using Chaos.Client.Collections;
using Chaos.Client.Controls.Components;
using Chaos.Client.Definitions;
using Chaos.Client.Rendering;
using Chaos.Client.Systems;
using Chaos.DarkAges.Definitions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.Menu;

/// <summary>
///     The emote picker (Menu &gt; Emotes). A fixed grid of every emote, each cell showing the real emote face/bubble
///     graphic (rendered in the player's own skin tone) plus its name. The hovered cell animates multi-frame emotes;
///     clicking one sends it. Icons are built lazily on first show and rebuilt if the player's body color changes.
/// </summary>
public sealed class EmoteWindow : DraggableWindow
{
    private const int COLS = 6;
    private const int PAD = 8;
    private const int CELL_W = 84; //wide enough that the longest emote name ("Mischievous"/"Stone Faced") fits on ONE line
    private const int CELL_H = 84;
    private const int ICON_H = 58; //top region of a cell reserved for the character portrait; the name sits below

    //the character composite is COMPOSITE_WIDTH x COMPOSITE_HEIGHT with the aisling centered (LAYER_OFFSET_PADDING of
    //side padding) and every layer top-aligned, so the head is at the top. Crop to a head-and-shoulders portrait: trim
    //the side padding and keep the top band (head + any speech bubble + shoulders), dropping the legs.
    private const int CROP_X = AislingRenderer.LAYER_OFFSET_PADDING;
    private const int CROP_W = AislingRenderer.BODY_WIDTH;
    private const int CROP_Y = 0;
    private const int CROP_H = 52;

    //the emote list, display order, and labels are owned by Keybindings (so the menu, the keybinds, and the dispatch
    //all agree on one order). The window just renders a cell per Keybindings.EmoteOrder entry.
    private static BodyAnimation[] Emotes => Keybindings.EmoteOrder;

    private static int RowCount => (Emotes.Length + COLS - 1) / COLS;

    private readonly AislingRenderer Renderer;
    private readonly List<EmoteCell> Cells = [];

    //all rendered character-with-emote composites, flat for disposal (frame indices from two different anim files
    //collide, so they are not keyed (the per-emote render count is small)
    private readonly List<Texture2D> Icons = [];
    private AislingAppearance? BuiltAppearance;

    /// <summary>Raised with the chosen emote when the player clicks a cell.</summary>
    public Action<BodyAnimation>? EmoteChosen;

    public EmoteWindow(AislingRenderer renderer)
        : base("Emotes", 100, 100, useWoodFrame: true)
    {
        Renderer = renderer;
        X = 140;
        Y = 60;

        //the cells sit in Content (PAD margin) so they inset with the frame for free.
        ResizeToFitClientSize(COLS * CELL_W + 2 * PAD, 2 * PAD + RowCount * CELL_H);

        BuildCells();
    }

    private void BuildCells()
    {
        for (var i = 0; i < Emotes.Length; i++)
        {
            var emote = Emotes[i];
            var (start, count, duration) = AnimationSystem.ResolveEmoteFrames(emote);

            var cell = new EmoteCell(emote, Keybindings.EmoteLabel(emote), CELL_W, CELL_H, ICON_H, start, count, duration)
            {
                X = PAD + i % COLS * CELL_W,
                Y = PAD + i / COLS * CELL_H
            };
            cell.Chosen = a => EmoteChosen?.Invoke(a);

            Content.AddChild(cell);
            Cells.Add(cell);
        }
    }

    public override void Update(GameTime gameTime)
    {
        //ensure icons exist (and match the current skin) before the cells draw; cheap no-op once built
        RefreshIcons();
        base.Update(gameTime);
    }

    /// <summary>(Re)builds the character-with-emote composites from the player's current appearance. No-op until a
    ///     player exists, or while the appearance is unchanged (gear/dye changes rebuild so the portrait stays current).</summary>
    private void RefreshIcons()
    {
        if (WorldState.GetPlayerEntity()?.Appearance is not { } appearance)
            return;

        if ((BuiltAppearance == appearance) && (Icons.Count > 0))
            return;

        DisposeIcons();
        BuiltAppearance = appearance;

        foreach (var cell in Cells)
        {
            //BlowKiss and Wave aren't real face emotes, they're "03" body (arm) animations, so their emot01 face
            //frames are bogus (Wave's land on Snore's "Zzz" face). Render the actual body animation for those, and the
            //idle-body-plus-emote-overlay for every genuine face/bubble emote.
            var (suffix, framesPerDir, _, rightStart) = AnimationSystem.ResolveBodyAnimParams(cell.Emote);
            var isBodyAnim = framesPerDir > 0;
            var count = isBodyAnim ? framesPerDir : Math.Max(1, cell.FrameCount);
            var frames = new Texture2D?[count];

            for (var f = 0; f < count; f++)
            {
                //front-facing (south) = the Right-direction frames, flipped (mirrors AnimationSystem.GetAislingFrame)
                var tex = isBodyAnim
                    ? Renderer.Render(in appearance, rightStart + f, animSuffix: suffix, flipHorizontal: true, isFrontFacing: true)
                    : Renderer.RenderEmoteCharacter(in appearance, cell.StartFrame + f);

                if (tex is not null)
                    Icons.Add(tex);

                frames[f] = tex;
            }

            cell.SetFrames(frames);
        }
    }

    private void DisposeIcons()
    {
        foreach (var tex in Icons)
            tex.Dispose();

        Icons.Clear();
    }

    public override void Dispose()
    {
        DisposeIcons();
        base.Dispose();
    }

    /// <summary>One emote in the grid: a dark cell with the character portrait on top and the name below.</summary>
    private sealed class EmoteCell : UIPanel
    {
        private static readonly Color IdleBg = new(26, 23, 17);
        private static readonly Color HoverBg = new(52, 46, 34);
        private static readonly Color Edge = new(60, 52, 36);

        private readonly int IconBoxH;
        private readonly float PerFrameMs;

        private Texture2D?[] Frames = [];
        private double Elapsed;
        private int Index;
        private bool Hovered;

        public BodyAnimation Emote { get; }
        public int StartFrame { get; }
        public int FrameCount { get; }
        public Action<BodyAnimation>? Chosen;

        public EmoteCell(BodyAnimation emote, string label, int width, int height, int iconBoxH, int startFrame, int frameCount, float durationMs)
        {
            Emote = emote;
            StartFrame = startFrame;
            FrameCount = frameCount;
            Width = width;
            Height = height;
            IconBoxH = iconBoxH;
            PerFrameMs = frameCount > 0 ? durationMs / frameCount : durationMs;
            BackgroundColor = IdleBg;
            BorderColor = Edge;

            AddChild(
                new UILabel
                {
                    Text = label,
                    X = 1,
                    Y = iconBoxH,
                    Width = width - 2,
                    Height = height - iconBoxH,
                    //single line (the cell is wide enough now), so it centers both horizontally AND vertically instead
                    //of wrapping/top-anchoring
                    WordWrap = false,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    ForegroundColor = new Color(202, 186, 150),
                    IsHitTestVisible = false,
                    //the emote window is a plain (un-magnified) DraggableWindow, so a CustomFontSize label paints crisp
                    //TTF in-place, matches the rest of the converted menus instead of the retail bitmap font
                    CustomFontSize = 11
                });
        }

        public void SetFrames(Texture2D?[] frames) => Frames = frames;

        public override void OnMouseEnter()
        {
            Hovered = true;
            Elapsed = 0;
            Index = 0;
            BackgroundColor = HoverBg;
        }

        public override void OnMouseLeave()
        {
            Hovered = false;
            Elapsed = 0;
            Index = 0;
            BackgroundColor = IdleBg;
        }

        public override void OnClick(ClickEvent e)
        {
            Chosen?.Invoke(Emote);
            e.Handled = true;
        }

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);

            //only the hovered cell animates (and only if it has more than one frame)
            if (Hovered && (Frames.Length > 1) && (PerFrameMs > 0))
            {
                Elapsed += gameTime.ElapsedGameTime.TotalMilliseconds;
                Index = (int)(Elapsed / PerFrameMs) % Frames.Length;
            } else
                Index = 0;
        }

        public override void Draw(SpriteBatchEx spriteBatch)
        {
            base.Draw(spriteBatch); //cell background + the name label

            if (!Visible || (Frames.Length == 0))
                return;

            var tex = Frames[Index < Frames.Length ? Index : 0];

            if (tex is null)
                return;

            //draw the head-and-shoulders crop of the character composite, integer-scaled to fit (UI is PointClamp = crisp)
            const int inset = 3;
            var scale = Math.Max(1, Math.Min((Width - 2 * inset) / CROP_W, (IconBoxH - 2 * inset) / CROP_H));
            var dstW = CROP_W * scale;
            var dstH = CROP_H * scale;
            var dstX = ScreenX + (Width - dstW) / 2;
            var dstY = ScreenY + (IconBoxH - dstH) / 2;

            spriteBatch.Draw(tex, new Rectangle(dstX, dstY, dstW, dstH), new Rectangle(CROP_X, CROP_Y, CROP_W, CROP_H), Color.White);
        }
    }
}
