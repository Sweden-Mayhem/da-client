#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.Generic;
using Chaos.Client.Networking.Definitions;
using Chaos.Client.Rendering.Models;
using Chaos.Client.Systems;
using Chaos.DarkAges.Definitions;
using Chaos.Networking.Entities.Server;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Controls.World.Popups.Dialog;

/// <summary>
///     NPC dialog/menu container built on the lnpcd prefab, always up while a dialog or menu is open
///     Holds the bottom bar, portrait, name, dialog text and nav buttons, with content sub-panels floating on top
/// </summary>
public sealed class NpcSessionControl : PrefabPanel
{
    //scroll arrow buttons for dialog text overflow (nd_arw.spf)
    private const float ARROW_ANIM_INTERVAL = 0.5f;

    //container controls from the lnpcd prefab
    private readonly UIButton? CloseButton;
    private readonly UILabel DialogTextLabel;
    private readonly MenuShopPanel MenuShop;
    private readonly UIButton? NextButton;

    private readonly UILabel? NpcNameLabel;
    private readonly UIImage? NpcTileImage;
    private readonly Rectangle PortraitRect;
    private readonly UIButton? PreviousButton;

    //sub-panels, made available so the input dispatcher can track focus
    public DialogTextEntryPanel DialogTextEntry { get; }
    public MenuListPanel MenuList { get; }

    public MenuTextEntryPanel MenuTextEntry { get; }
    public DialogOptionPanel DialogOption { get; }
    public DialogProtectedTextEntryPanel DialogProtectedTextEntry { get; }
    private readonly UIButton? ScrollDownButton;
    private readonly Texture2D?[] ScrollDownFrames = new Texture2D?[2];
    private readonly UIButton? ScrollUpButton;
    private readonly Texture2D?[] ScrollUpFrames = new Texture2D?[2];
    private readonly UIButton? TopButton;
    private bool ArrowAnimFrame;
    private float ArrowAnimTimer;
    private bool OwnsPortraitTexture;
    private SpriteFrame? PortraitSpriteFrame;

    //spoken text and NPC name use the optional TrueType font like the rest of the menu UI
    //line height and scroll math come from the TTF metrics, falling back to the bitmap font when it is missing
    private const int DIALOG_FONT_SIZE = 11;
    private const int NAME_FONT_SIZE = 12;

    //default spoken-text color where no color markup overrides it
    //warm cream, easier to read over the dark dialog frame
    private static readonly Color DialogTextColor = new(236, 230, 214);
    private static int DialogLineHeight => TtfTextRenderer.Available ? TtfTextRenderer.LineHeight(DIALOG_FONT_SIZE) : TextRenderer.CHAR_HEIGHT;

    //the dialog slides up into place on its first appearance only, paging gets no slide
    //eased 0..1 where OpenSlideProgress of 1 means settled
    private const float OPEN_SLIDE_PX = 28f;
    private const float OPEN_SLIDE_SECONDS = 0.2f;
    private float OpenSlideProgress = 1f;

    //the frame art is magnified by the host but the TTF text is drawn fresh at final size so it stays crisp
    //SpokenText is the dialog text after placeholder expansion, with color markup left for the native path to parse
    private const int VISIBLE_SPOKEN_LINES = 3;
    private string SpokenText = string.Empty;

    //tracks whether option choices are the active native content
    //left set on close so the option text keeps fading with the host instead of popping out, a content switch replaces it
    private enum NativeContent
    {
        None,
        Options
    }

    private NativeContent ActiveNativeContent;

    /// <summary>Screen-space Y offset the host uses to slide the dialog up as it fades in on first open</summary>
    public float ContentYOffset => (1f - SmoothStep(OpenSlideProgress)) * OPEN_SLIDE_PX;

    //portrait texture (owned illustration or cached sprite frame)
    private Texture2D? PortraitTexture;
    private int ScrollLine;
    private bool CloseWanted; //the nav layer wants a Close button, the scroll and at-top rules refine it
    //signature of the first screen of the current NPC conversation, its top
    //a later screen matching it is the start, so Top is hidden and Close stays even under a scroll-down arrow
    private string? ConversationTopSig;
    private bool AtTop; //the currently shown screen is the conversation's top screen
    private bool NextWanted; //the nav layer wants a Next button, only shown when no scroll-down arrow is up
    public DialogType? CurrentDialogType { get; private set; }
    public MenuType? CurrentMenuType { get; private set; }
    public ushort DialogId { get; private set; }
    public bool IsDialogOpcode { get; private set; }

    //menu args echoed back for MenuWithArgs
    public string? MenuArgs { get; private set; }

    //portrait metadata for WorldScreen to render
    public string? NpcName { get; private set; }
    public DisplayColor PortraitColor { get; private set; }
    public ushort PortraitSpriteId { get; private set; }
    public ushort PursuitId { get; private set; }

    /// <summary>
    ///     Server-sent index into the NPC's variant list, picks which SPF file to load when a name has several variants
    ///     0 is the near-universal default and means the first filename
    /// </summary>
    public byte IllustrationIndex { get; private set; }

    //session state
    public EntityType SourceEntityType { get; private set; }
    public uint? SourceId { get; private set; }

    //speak dialog prompt prefix and epilog suffix for the say broadcast
    public string? SpeakEpilog { get; private set; }
    public string? SpeakPrompt { get; private set; }

    public NpcSessionControl()
        : base("lnpcd", false)
    {
        Name = "NpcSession";
        Visible = false;
        UsesControlStack = true;
        X = 0;
        Y = 0;

        //the dialog is laid out in the retail 640x480 space
        //pin the panel to that whole canvas so a ScaleHost can scale and center it as one unit to fit the window
        Width = ChaosGame.VIRTUAL_WIDTH;
        Height = ChaosGame.VIRTUAL_HEIGHT;

        //darkness gradient (behind everything else in the dialog)
        AddChild(new DialogAlphaGradient());

        //background images, drawn after the gradient so they render on top of it
        CreateImage("MessageDialog"); //bottom dialog bar
        NpcTileImage = CreateImage("NPCTile"); //portrait background

        //container buttons, added after images so they draw on top
        CloseButton = CreateButton("CloseBtn");
        NextButton = CreateButton("NextBtn");
        PreviousButton = CreateButton("PrevBtn");
        TopButton = CreateButton("TopBtn");

        //scroll arrow buttons for dialog text overflow (nd_arw.spf frames 0-1 up, 2-3 down)
        var uiCache = UiRenderer.Instance!;
        ScrollUpFrames[0] = uiCache.GetSpfTexture("nd_arw.spf");
        ScrollUpFrames[1] = uiCache.GetSpfTexture("nd_arw.spf", 1);
        ScrollDownFrames[0] = uiCache.GetSpfTexture("nd_arw.spf", 2);
        ScrollDownFrames[1] = uiCache.GetSpfTexture("nd_arw.spf", 3);
        var upArrowTexture = ScrollUpFrames[0]!;
        var downArrowTexture = ScrollDownFrames[0]!;

        if (CloseButton is not null)
        {
            ScrollDownButton = UIButton.CreateWithTexture("ScrollDown", downArrowTexture);
            ScrollDownButton.X = CloseButton.X + CloseButton.Width - downArrowTexture.Width - 3;
            ScrollDownButton.Y = CloseButton.Y - downArrowTexture.Height - 1;
            ScrollDownButton.Visible = false;

            ScrollUpButton = UIButton.CreateWithTexture("ScrollUp", upArrowTexture);
            //center the up arrow over the down arrow in case the two frames differ in width
            ScrollUpButton.X = ScrollDownButton.X + ((downArrowTexture.Width - upArrowTexture.Width) / 2);
            ScrollUpButton.Y = ScrollDownButton.Y - upArrowTexture.Height - 27;
            ScrollUpButton.Visible = false;

            AddChild(ScrollDownButton);
            AddChild(ScrollUpButton);

            ScrollDownButton.Clicked += () => ScrollText(1);
            ScrollUpButton.Clicked += () => ScrollText(-1);
        }

        //layout rects
        NpcNameLabel = CreateLabel("Name");
        PortraitRect = GetRect("NPCTile");

        if ((NpcNameLabel is not null) && TtfTextRenderer.Available)
            NpcNameLabel.CustomFontSize = NAME_FONT_SIZE;

        //dialog text label, word-wrapped in the TTF font, region height is 3 lines so wrapping and scroll stay correct
        var textRect = GetRect("Text");

        DialogTextLabel = new UILabel
        {
            X = textRect.X,
            Y = textRect.Y + 1,
            Width = textRect.Width,
            Height = 3 * DialogLineHeight + 2,
            WordWrap = true,
            CustomFontSize = TtfTextRenderer.Available ? DIALOG_FONT_SIZE : 0,
            ForegroundColor = DialogTextColor
        };

        AddChild(DialogTextLabel);

        //wire container button events
        if (CloseButton is not null)
            CloseButton.Clicked += () =>
            {
                HideAll();
                OnClose?.Invoke();
            };

        if (NextButton is not null)
            NextButton.Clicked += () => OnNext?.Invoke();

        if (PreviousButton is not null)
            PreviousButton.Clicked += () => OnPrevious?.Invoke();

        if (TopButton is not null)
            //Top re-opens the conversation from the start, a content change not a close
            //do not HideAll here or the dialog fades out then back in as a visible flicker, the incoming show swaps content
            TopButton.Clicked += () => OnTop?.Invoke();

        //create sub-panels as children
        DialogOption = new DialogOptionPanel();
        DialogTextEntry = new DialogTextEntryPanel();
        MenuTextEntry = new MenuTextEntryPanel();
        MenuShop = new MenuShopPanel();
        MenuList = new MenuListPanel();
        DialogProtectedTextEntry = new DialogProtectedTextEntryPanel();

        AddChild(DialogOption);
        AddChild(DialogTextEntry);
        AddChild(MenuTextEntry);
        AddChild(MenuShop);
        AddChild(MenuList);
        AddChild(DialogProtectedTextEntry);

        //wire sub-panel events to forward to container events
        DialogOption.OnOptionSelected += index => OnOptionSelected?.Invoke(index);

        DialogOption.OnClose += () =>
        {
            HideAll();
            OnClose?.Invoke();
        };

        DialogTextEntry.OnTextSubmit += text => OnTextSubmit?.Invoke(text);

        DialogTextEntry.OnClose += () =>
        {
            HideAll();
            OnClose?.Invoke();
        };

        MenuTextEntry.OnTextSubmit += text => OnTextSubmit?.Invoke(text);

        MenuTextEntry.OnClose += () =>
        {
            HideAll();
            OnClose?.Invoke();
        };

        MenuShop.OnItemSelected += index => OnMerchantItemSelected?.Invoke(index);
        MenuShop.OnItemHoverEnter += name => OnItemHoverEnter?.Invoke(name);
        MenuShop.OnItemHoverExit += () => OnItemHoverExit?.Invoke();

        MenuShop.OnClose += () =>
        {
            HideAll();
            OnClose?.Invoke();
        };

        MenuList.OnItemSelected += index => OnListItemSelected?.Invoke(index);

        MenuList.OnClose += () =>
        {
            HideAll();
            OnClose?.Invoke();
        };

        DialogProtectedTextEntry.OnProtectedSubmit += (id, pw) => OnProtectedSubmit?.Invoke(id, pw);

        DialogProtectedTextEntry.OnClose += () =>
        {
            HideAll();
            OnClose?.Invoke();
        };
    }

    public override void Dispose()
    {
        DisposePortrait();
        base.Dispose();
    }

    private void DisposePortrait()
    {
        if (OwnsPortraitTexture)
            PortraitTexture?.Dispose();

        PortraitTexture = null;
        OwnsPortraitTexture = false;
    }

    public override void Draw(SpriteBatch spriteBatch)
    {
        if (!Visible)
            return;

        UpdateClipRect();

        if ((ClipRect.Width <= 0) || (ClipRect.Height <= 0))
            return;

        //1. background
        if (Background is not null)
            DrawTexture(
                spriteBatch,
                Background,
                new Vector2(ScreenX, ScreenY),
                Color.White);

        //2. base-layer children (gradient, bottom bar, portrait bg, buttons, labels)
        //the TTF text labels are skipped here, DrawTextNative draws them crisp instead of magnifying them
        foreach (var child in Children)
            if (child.Visible && !IsSubPanel(child) && (child != DialogTextLabel) && (child != NpcNameLabel))
            {
                child.Draw(spriteBatch);
                DebugOverlay.DrawElement(spriteBatch, child);
            }

        //3. portrait, on top of the base layer and behind sub-panels
        DrawPortrait(spriteBatch);

        //4. sub-panels, always in front of the portrait
        foreach (var child in Children)
            if (child.Visible && IsSubPanel(child))
            {
                child.Draw(spriteBatch);
                DebugOverlay.DrawElement(spriteBatch, child);
            }

    }

    /// <summary>
    ///     Draws the dialog's TTF text at native resolution on top of the scaled frame so it stays crisp
    ///     originX, originY and scale are the host's origin and magnification, alpha fades the text with the host
    /// </summary>
    public void DrawTextNative(SpriteBatch spriteBatch, int originX, int originY, float scale, float alpha)
    {
        if ((scale <= 0f) || (alpha <= 0f) || !TtfTextRenderer.Available)
            return;

        int MapX(int sx) => originX + (int)((sx - originX) * scale);
        int MapY(int sy) => originY + (int)((sy - originY) * scale);

        //emboss with a dark drop shadow down-right of each glyph so the text reads raised off the frame
        //GetLine renders white glyphs so a black tint gives the shadow, the +1 deepens the drop a touch
        var shadowOff = new Vector2(Math.Max(1, (int)MathF.Round(scale)) + 1);
        var shadowCol = Color.Black * (0.5f * alpha);

        //NPC name (single line)
        if ((NpcNameLabel is { } name) && !string.IsNullOrEmpty(name.Text))
        {
            var font = Math.Max(1, (int)MathF.Round(NAME_FONT_SIZE * scale));
            var tex = TtfTextRenderer.GetLine(name.Text, font);

            if (tex is not null)
            {
                var p = new Vector2(MapX(name.ScreenX), MapY(name.ScreenY));
                spriteBatch.Draw(tex, p + shadowOff, shadowCol);
                spriteBatch.Draw(tex, p, name.ForegroundColor * alpha);
            }
        }

        //spoken text, word-wrapped at the scaled width with color markup honored and line-scrolled by ScrollLine
        //each wrapped line is a list of colored runs and each run draws its own emboss shadow then its color
        if (!string.IsNullOrEmpty(SpokenText))
        {
            var font = Math.Max(1, (int)MathF.Round(DIALOG_FONT_SIZE * scale));
            var lines = RichText.Wrap(SpokenText, (int)(DialogTextLabel.Width * scale), font);
            var lh = TtfTextRenderer.LineHeight(font);
            var x = MapX(DialogTextLabel.ScreenX);
            var y = MapY(DialogTextLabel.ScreenY);
            var def = DialogTextLabel.ForegroundColor;

            for (var i = ScrollLine; (i < lines.Count) && (i < ScrollLine + VISIBLE_SPOKEN_LINES); i++)
            {
                var lineY = y + (i - ScrollLine) * lh;
                var runX = x;

                foreach (var run in lines[i])
                {
                    if (run.Text.Length == 0)
                        continue;

                    var tex = TtfTextRenderer.GetLine(run.Text, font);

                    if (tex is not null)
                    {
                        var p = new Vector2(runX, lineY);
                        spriteBatch.Draw(tex, p + shadowOff, shadowCol);
                        spriteBatch.Draw(tex, p, (run.Color ?? def) * alpha);
                    }

                    runX += TtfTextRenderer.MeasureWidth(run.Text, font);
                }
            }
        }

        //options fade out with the dialog, drawn whenever ActiveNativeContent is Options
        //it is not cleared on close so the option text keeps fading at the host's alpha during the close linger
        if (ActiveNativeContent == NativeContent.Options)
            DialogOption.DrawTextNative(spriteBatch, originX, originY, scale, alpha);

        //shop and list wares never fade out, the merchant window snaps closed so the text must too
        //gate on the panel's own visibility so it is gone the instant it hides, alpha still lets it fade in on open
        if (MenuShop.Visible)
            MenuShop.DrawTextNative(spriteBatch, originX, originY, scale, alpha);
        else if (MenuList.Visible)
            MenuList.DrawTextNative(spriteBatch, originX, originY, scale, alpha);
    }

    private void DrawPortrait(SpriteBatch spriteBatch)
    {
        if (PortraitTexture is null)
            return;

        if (OwnsPortraitTexture)
        {
            //NPC illustration, left-aligned with its bottom edge sitting on the bottom bar at y 372
            //drawn relative to the panel origin so it follows when the dialog is hosted in a ScaleHost
            var illustY = 372 - PortraitTexture.Height;
            DrawTexture(spriteBatch, PortraitTexture, new Vector2(ScreenX, ScreenY + illustY), Color.White);
        } else if (PortraitRect != Rectangle.Empty)
        {
            //creature or item sprite, center the sprite's visual anchor in the NPCTile rect
            var sx = ScreenX;
            var sy = ScreenY;
            var rectCenterX = sx + PortraitRect.X + PortraitRect.Width / 2;
            var rectCenterY = sy + PortraitRect.Y + PortraitRect.Height / 2;

            if (PortraitSpriteFrame is { } frame)
            {
                var drawX = rectCenterX - (PortraitTexture.Width + frame.Left) / 2;
                var drawY = rectCenterY - (PortraitTexture.Height + frame.Top) / 2;

                DrawTexture(spriteBatch, PortraitTexture, new Vector2(drawX, drawY), Color.White);
            } else
                DrawTexture(
                    spriteBatch,
                    PortraitTexture,
                    new Vector2((int)(rectCenterX - PortraitTexture.Width / 2f), (int)(rectCenterY - PortraitTexture.Height / 2f)),
                    Color.White);
        }
    }

    /// <summary>
    ///     Name of the list menu entry at the given index
    /// </summary>
    public string? GetListEntryName(int index) => MenuList.GetEntryName(index);

    /// <summary>
    ///     Slot byte for the list menu entry at the given index
    /// </summary>
    public byte? GetListEntrySlot(int index) => MenuList.GetEntrySlot(index);

    /// <summary>
    ///     Previous args string for menu text entry
    /// </summary>
    public string? GetMenuTextPreviousArgs() => MenuTextEntry.PreviousArgs;

    /// <summary>
    ///     Name of the merchant entry at the given index
    /// </summary>
    public string? GetMerchantEntryName(int index) => MenuShop.GetEntryName(index);

    /// <summary>
    ///     Slot byte for the merchant entry at the given index
    /// </summary>
    public byte? GetMerchantEntrySlot(int index) => MenuShop.GetEntrySlot(index);

    /// <summary>
    ///     Pursuit id for the option at the given index in the option sub-panel
    /// </summary>
    public ushort GetOptionPursuitId(int index) => DialogOption.GetOptionPursuitId(index);

    /// <summary>
    ///     Hides the container and all sub-panels
    /// </summary>
    public void HideAll()
    {
        DisposePortrait();
        HideAllSubPanels();
        ConversationTopSig = null; //conversation over, forget this NPC's top screen
        AtTop = false;
        Hide();
    }

    private void HideAllSubPanels()
    {
        DialogOption.Hide();
        DialogTextEntry.Hide();
        MenuTextEntry.Hide();
        MenuShop.Hide();
        MenuList.Hide();
        DialogProtectedTextEntry.Hide();
        ScrollLine = 0;
        DialogTextLabel.ScrollOffset = 0;
        //the spoken text is left alone here, a content change overwrites it via SetDialogText
        //on close it must persist so the native text overlay fades out with the host instead of vanishing
        UpdateScrollButtons();
    }

    private void HideNavigationButtons()
    {
        NextWanted = false;

        if (NextButton is not null)
        {
            NextButton.Visible = false;
            NextButton.Enabled = false;
        }

        if (PreviousButton is not null)
        {
            PreviousButton.Visible = false;
            PreviousButton.Enabled = false;
        }

        CloseWanted = false;

        if (CloseButton is not null)
        {
            CloseButton.Visible = false;
            CloseButton.Enabled = false;
        }

        if (TopButton is not null)
        {
            TopButton.Visible = false;
            TopButton.Enabled = false;
        }
    }

    private bool IsSubPanel(UIElement child)
        => (child == DialogOption)
           || (child == DialogTextEntry)
           || (child == MenuTextEntry)
           || (child == MenuShop)
           || (child == MenuList)
           || (child == DialogProtectedTextEntry);

    //events, WorldScreen wiring subscribes to these
    public event CloseHandler? OnClose;
    public event ItemHoverEnterHandler? OnItemHoverEnter;
    public event ItemHoverExitHandler? OnItemHoverExit;
    public event ItemSelectedHandler? OnListItemSelected;
    public event ItemSelectedHandler? OnMerchantItemSelected;
    public event NextHandler? OnNext;
    public event OptionSelectedHandler? OnOptionSelected;
    public event PreviousHandler? OnPrevious;
    public event ProtectedSubmitHandler? OnProtectedSubmit;
    public event TextSubmitHandler? OnTextSubmit;
    public event TopHandler? OnTop;

    //total wrapped lines of the spoken text at the TTF dialog font in 640-space width
    //RichText.Wrap measures only the visible text so the line count matches the native render and ScrollLine maps directly
    private int TotalSpokenLines()
        => (!TtfTextRenderer.Available || string.IsNullOrEmpty(SpokenText))
            ? 0
            : RichText.Wrap(SpokenText, DialogTextLabel.Width, DIALOG_FONT_SIZE).Count;

    private void ScrollText(int direction)
    {
        var maxScroll = Math.Max(0, TotalSpokenLines() - VISIBLE_SPOKEN_LINES);
        ScrollLine = Math.Clamp(ScrollLine + direction, 0, maxScroll);
        UpdateScrollButtons();
    }

    private void SetDialogText(string? text)
    {
        ScrollLine = 0;
        //expand placeholders like keybinds and player name once here
        //the color markup is left in and parsed per line at draw time
        SpokenText = TextMacros.Expand(text);
        DialogTextLabel.Text = SpokenText;
        UpdateScrollButtons();
    }

    private void SetNavigationButtons(bool hasNext, bool hasPrevious)
    {
        //Next and Prev are hidden when there is nowhere to go
        //ApplyNavVisibility owns Next's real visibility and placement
        NextWanted = hasNext;
        CloseWanted = true;

        if (PreviousButton is not null)
        {
            PreviousButton.Visible = hasPrevious;
            PreviousButton.Enabled = hasPrevious;
        }

        if (TopButton is not null)
        {
            TopButton.Visible = false;
            TopButton.Enabled = false;
        }

        ApplyNavVisibility();
    }

    //resolves Next and Close against the scroll state, NPC dialog opcodes only since menus manage their own Close
    //Next shows only when wanted with no scroll-down arrow and takes Close's slot, Close hides under that arrow except at top
    private void ApplyNavVisibility()
    {
        if (!IsDialogOpcode)
            return;

        var scrollDownPresent = ScrollDownButton?.Visible == true;
        var nextVisible = NextWanted && !scrollDownPresent;

        if (NextButton is not null)
        {
            NextButton.Visible = nextVisible;
            NextButton.Enabled = nextVisible;

            if (nextVisible && (CloseButton is not null))
            {
                NextButton.X = CloseButton.X;
                NextButton.Y = CloseButton.Y;
            }
        }

        if (CloseButton is not null)
        {
            //a scroll-down arrow hides Close except at the top menu when it has options the player should be able to close
            //at the top with no options it is just scrollable text so Close still hides
            var topException = AtTop && DialogOption.Visible;
            var show = CloseWanted && !nextVisible && (!scrollDownPresent || topException);
            CloseButton.Visible = show;
            CloseButton.Enabled = show;
        }
    }

    /// <summary>
    ///     Sets the NPC portrait texture, with ownsTexture true the container disposes it when replaced or hidden
    /// </summary>
    public void SetPortrait(Texture2D? texture, bool ownsTexture)
    {
        DisposePortrait();
        PortraitTexture = texture;
        PortraitSpriteFrame = null;
        OwnsPortraitTexture = ownsTexture;

        //show the NPCTile background only for sprite portraits, not illustrations and not when hidden
        NpcTileImage?.Visible = texture is not null && !ownsTexture;
    }

    public void SetPortrait(SpriteFrame spriteFrame)
    {
        DisposePortrait();
        PortraitTexture = spriteFrame.Texture;
        PortraitSpriteFrame = spriteFrame;
        OwnsPortraitTexture = false;

        NpcTileImage?.Visible = true;
    }

    /// <summary>
    ///     Shows the container for a DisplayDialog packet
    /// </summary>
    public void ShowDialog(DisplayDialogArgs args)
    {
        if (args.DialogType is DialogType.CloseDialog)
        {
            HideAll();

            return;
        }

        IsDialogOpcode = true;
        CurrentDialogType = args.DialogType;
        CurrentMenuType = null;
        SourceEntityType = args.EntityType;
        SourceId = args.SourceId;
        PursuitId = args.PursuitId ?? 0;
        DialogId = args.DialogId;
        NpcName = args.Name;
        PortraitSpriteId = args.Sprite;
        PortraitColor = args.Color;
        IllustrationIndex = args.IllustrationIndex;

        //a fresh open makes this dialog the conversation's top screen, otherwise we are at top only if it matches that screen
        //figured out before SetDialogText since the Close-visibility rule reads AtTop
        var dialogSig = DialogSignature(args);

        if (!Visible)
            ConversationTopSig = dialogSig;

        AtTop = dialogSig == ConversationTopSig;

        HideAllSubPanels();
        SetDialogText(args.Text);
        SetNavigationButtons(args.HasNextButton, args.HasPreviousButton);
        MenuArgs = null;
        SpeakPrompt = null;
        SpeakEpilog = null;

        //name and spoken text always draw, only options add a native sub-panel here
        ActiveNativeContent = NativeContent.None;

        switch (args.DialogType)
        {
            case DialogType.Normal:
                break;

            case DialogType.DialogMenu:
            case DialogType.CreatureMenu:
                if (args.Options is not null && (args.Options.Count > 0))
                {
                    //expand placeholders and strip color markup since options render single-color
                    var options = args.Options
                                      .Select(o => (RichText.Strip(TextMacros.Expand(o)), (ushort)0))
                                      .ToList();
                    DialogOption.ShowOptions(options);
                    ActiveNativeContent = NativeContent.Options;
                }

                break;

            case DialogType.TextEntry:
            case DialogType.Speak:
                HideNavigationButtons();

                var prompt = args.TextBoxPrompt ?? string.Empty;
                var epilog = string.Empty;

                if (args.DialogType is DialogType.Speak)
                {
                    SpeakPrompt = prompt;
                    SpeakEpilog = epilog;
                }

                DialogTextEntry.ShowTextEntry(prompt, (byte)(args.TextBoxLength ?? 255), epilog);

                break;

            case DialogType.Protected:
                HideNavigationButtons();
                DialogProtectedTextEntry.ShowProtected(args.Text);

                break;

            default:
                return;
        }

        //now that scroll state and options are known, apply the hide-Close-under-a-scroll-arrow rule
        ApplyNavVisibility();

        if (NpcNameLabel is not null)
        {
            NpcNameLabel.Text = args.Name;
            NpcNameLabel.ForegroundColor = LegendColors.CornflowerBlue;
        }

        var wasVisible = Visible;
        Show();

        //slide and fade only on a fresh open, never on paging or content changes
        if (!wasVisible)
            BeginOpenSlide();
    }

    //stable identity for a menu or dialog screen, used to recognize when a later screen is the conversation's top
    //the M and D prefixes keep menu and dialog signatures from colliding
    private static string MenuSignature(DisplayMenuArgs args)
    {
        var opts = args.Options is null
            ? string.Empty
            : string.Join(";", args.Options.Select(o => $"{o.Text}:{o.Pursuit}"));

        return $"M|{args.PursuitId}|{(int)args.MenuType}|{args.Text}|{opts}";
    }

    private static string DialogSignature(DisplayDialogArgs args)
    {
        var opts = args.Options is null
            ? string.Empty
            : string.Join(";", args.Options);

        return $"D|{args.PursuitId}|{args.DialogId}|{(int)args.DialogType}|{args.Text}|{opts}";
    }

    /// <summary>
    ///     Shows the container for a DisplayMenu packet
    /// </summary>
    public void ShowMenu(DisplayMenuArgs args)
    {
        IsDialogOpcode = false;
        CurrentDialogType = null;
        CurrentMenuType = args.MenuType;
        SourceEntityType = args.EntityType;
        SourceId = args.SourceId;
        PursuitId = args.PursuitId;
        DialogId = 0;
        NpcName = args.Name;
        PortraitSpriteId = args.Sprite;
        PortraitColor = args.Color;
        IllustrationIndex = args.IllustrationIndex;

        MenuArgs = args.Args;
        HideAllSubPanels();
        HideNavigationButtons();
        SetDialogText(args.Text);

        //pick which native sub-panel text this menu drives, text-entry menus are scaled so they stay None
        ActiveNativeContent = NativeContent.None;

        switch (args.MenuType)
        {
            case MenuType.Menu:
            case MenuType.MenuWithArgs:
                if (args.Options is not null && (args.Options.Count > 0))
                {
                    //expand placeholders and strip color markup since options render single-color
                    var options = args.Options
                                      .Select(o => (RichText.Strip(TextMacros.Expand(o.Text)), o.Pursuit))
                                      .ToList();
                    DialogOption.ShowOptions(options);
                    ActiveNativeContent = NativeContent.Options;
                }

                break;

            case MenuType.TextEntry:
                MenuTextEntry.ShowTextEntry(null);

                break;

            case MenuType.TextEntryWithArgs:
                MenuTextEntry.ShowTextEntry(args.Args);

                break;

            case MenuType.ShowItems:
                MenuShop.ShowMerchant(args);

                break;

            case MenuType.ShowPlayerItems:
            case MenuType.ShowSkills:
            case MenuType.ShowSpells:
            case MenuType.ShowPlayerSkills:
            case MenuType.ShowPlayerSpells:
            case SwmProtocol.CraftMenu: //server-listed items in the learn-menu list panel
                MenuList.ShowList(args);

                break;

            case SwmProtocol.Market: //the market is its own packet-driven window now; never shown as an NPC dialog
                return;

            default:
                return;
        }

        if (CloseButton is not null)
        {
            CloseButton.Visible = true;
            CloseButton.Enabled = true;
        }

        //Top returns to the NPC's start, hide it when we are on the conversation's top screen
        //the screen shown on a fresh open is the top, a later screen matching its signature is the start again
        var menuSig = MenuSignature(args);

        if (!Visible) //fresh open of this NPC conversation
            ConversationTopSig = menuSig;

        AtTop = menuSig == ConversationTopSig;

        if (TopButton is not null)
        {
            TopButton.Visible = !AtTop;
            TopButton.Enabled = !AtTop;
        }

        if (NpcNameLabel is not null)
        {
            NpcNameLabel.Text = args.Name;
            NpcNameLabel.ForegroundColor = LegendColors.CornflowerBlue;
        }

        var wasVisible = Visible;
        Show();

        //slide and fade only on a fresh open, never on paging or content changes
        if (!wasVisible)
            BeginOpenSlide();
    }

    //restart the slide-up, called only when the dialog goes from hidden to shown, not on content changes
    private void BeginOpenSlide() => OpenSlideProgress = 0f;

    private static float SmoothStep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);

        return t * t * (3f - 2f * t);
    }

    public override void Update(GameTime gameTime)
    {
        if (!Visible || !Enabled)
            return;

        //ease the first-appearance slide up to its resting position
        if (OpenSlideProgress < 1f)
            OpenSlideProgress = Math.Min(1f, OpenSlideProgress + (float)gameTime.ElapsedGameTime.TotalSeconds / OPEN_SLIDE_SECONDS);

        //animate scroll arrow buttons by flipping frames every 500ms
        var anyArrowVisible = ScrollUpButton is { Visible: true } || ScrollDownButton is { Visible: true };

        if (anyArrowVisible)
        {
            ArrowAnimTimer += (float)gameTime.ElapsedGameTime.TotalSeconds;

            if (ArrowAnimTimer >= ARROW_ANIM_INTERVAL)
            {
                ArrowAnimTimer -= ARROW_ANIM_INTERVAL;
                ArrowAnimFrame = !ArrowAnimFrame;
                var frameIndex = ArrowAnimFrame ? 1 : 0;

                if (ScrollUpButton is { Visible: true })
                    ScrollUpButton.NormalTexture = ScrollUpFrames[frameIndex];

                if (ScrollDownButton is { Visible: true })
                    ScrollDownButton.NormalTexture = ScrollDownFrames[frameIndex];
            }
        }

        base.Update(gameTime);
    }

    public override void OnKeyDown(KeyDownEvent e)
    {
        if (e.Key == Keys.Escape)
        {
            HideAll();
            OnClose?.Invoke();
            e.Handled = true;

            return;
        }

        //space or enter advances normal dialogs via the next button, or picks the first option in menus
        if (e.Key is Keys.Space or Keys.Enter)
        {
            if (DialogOption is { Visible: true, OptionCount: > 0 })
            {
                OnOptionSelected?.Invoke(0);
                e.Handled = true;
            } else if (NextButton is { Visible: true, Enabled: true })
            {
                OnNext?.Invoke();
                e.Handled = true;
            } else if (!DialogOption.Visible || (DialogOption.OptionCount == 0))
            {
                HideAll();
                OnClose?.Invoke();
                e.Handled = true;
            }
        }
    }

    private void UpdateScrollButtons()
    {
        var total = TotalSpokenLines();

        if (total <= VISIBLE_SPOKEN_LINES)
        {
            ScrollUpButton?.Visible = false;

            ScrollDownButton?.Visible = false;

            ApplyNavVisibility(); //no scroll arrow so Close shows normally

            return;
        }

        var maxScroll = total - VISIBLE_SPOKEN_LINES;

        if (ScrollUpButton is not null)
        {
            ScrollUpButton.Visible = ScrollLine > 0;
            ScrollUpButton.Enabled = ScrollLine > 0;
        }

        if (ScrollDownButton is not null)
        {
            ScrollDownButton.Visible = ScrollLine < maxScroll;
            ScrollDownButton.Enabled = ScrollLine < maxScroll;
        }

        ApplyNavVisibility(); //a scroll-down arrow appearing or going away toggles the Close button
    }
}