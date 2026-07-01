#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Definitions;
using Chaos.Client.Networking;
using Chaos.Client.Rendering;
using Chaos.Client.Systems;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Controls.World.Hud;

/// <summary>
///     SWM quest-tracker HUD panel. Stacks every active tracked quest the server pushes (ServerOpCode.QuestTracker
///     = 114) in a screen corner, each as a title plus ALL of its steps (current highlighted). It behaves like chat: the
///     box (fill + border) is invisible while idle and only appears when the cursor is over it, when it is pinned,
///     or briefly when a quest changes - the quest TEXT itself always stays readable (with a soft dark glow). It can
///     be dragged anywhere, and lays itself out based on which screen half its centre sits in: text left/right
///     aligned + pin button on the near horizontal edge, quests growing down from the top half or up from the bottom
///     half. The pin (the same widget the chat window uses) keeps the box open AND makes the quests clickable; a
///     click opens a floating quest-info window with the quest's description and each step's authored explanation.
/// </summary>
public sealed class QuestTrackerControl : UIPanel
{
    private const int PAD = 6;
    private const int QUEST_GAP = 7;
    private const int PIN_SIZE = 18;          //the pin widget art is 18x18 (same as the chat/close widgets)
    private const int HEADER_H = PIN_SIZE + 2; //strip reserved on the docked vertical edge for the pin button
    private const int TITLE_FONT_BASE = 15;    //base TTF sizes at 1.0x, multiplied by the chat font scale so it matches chat
    private const int ROW_FONT_BASE = 14;
    private const int DRAG_THRESHOLD = 4;      //px the cursor must move before a press becomes a drag (not a click)
    private const float BRIGHT_SECONDS = 2.5f; //how long the box stays lit after a quest change

    private static readonly Color TitleColor = new(255, 214, 110);   //gold quest title
    private static readonly Color CurrentColor = new(255, 232, 150);  //bright gold for the active objective
    private static readonly Color DoneColor = new(135, 165, 135);     //muted green-grey for finished steps (popup)
    private static readonly Color PendingColor = new(170, 162, 146);  //dim parchment for upcoming steps (popup)
    private static readonly Color CounterColor = new(206, 196, 150);   //soft gold for the lifetime counter line
    private static readonly Color HintColor = new(150, 214, 130);      //green for the "right place" guide hint

    //generic 18x18 pin widget art, loaded fresh per control (each UIPanel disposes its own Background), premultiplied.
    private static Texture2D? LoadWidgetArt(string resourceName)
    {
        try
        {
            using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);

            if (stream is null)
                return null;

            var texture = Texture2D.FromStream(ChaosGame.Device, stream);
            var pixels = new Color[texture.Width * texture.Height];
            texture.GetData(pixels);

            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = Color.FromNonPremultiplied(pixels[i].R, pixels[i].G, pixels[i].B, pixels[i].A);

            texture.SetData(pixels);

            return texture;
        } catch
        {
            return null;
        }
    }

    private readonly Texture2D? PinNormalTex;
    private readonly Texture2D? PinActiveTex;
    private readonly Texture2D? PinPressedTex;

    private readonly UIPanel PinBox;

    private readonly Texture2D? BookTex;     //in-game journal icon (_nbklgd.spf)
    private readonly UIPanel BookBox;        //journal shortcut button, sits beside the pin
    private readonly List<QuestRow> Rows = [];

    //the current quests, kept so a font-scale change or a quadrant flip can rebuild the labels without a new push.
    private IReadOnlyList<QuestTrackerQuestInfo> Quests = [];

    //the raw push (before the untrack filter) so re-applying the filter doesn't need a new server push.
    private IReadOnlyList<QuestTrackerQuestInfo> AllQuests = [];

    private int TitleFont = TITLE_FONT_BASE;
    private int RowFont = ROW_FONT_BASE;
    private float AppliedScale = -1f;

    private float ChromeOpacity;
    private double BrightSeconds;

    //#9 appear: a newly tracked quest's TITLE slides in from the docked screen side + fades (phase 1), then its STEPS
    //fold out from under the title - sliding down into their slots + fading in, slightly staggered (phase 2). Keyed by
    //quest key so it survives the per-push rebuild; the first push (login fill) only seeds - it does not animate.
    private const float SLIDE_SECONDS = 0.32f;                          //phase 1: the title slides in
    private const float FOLD_SECONDS = 0.5f;                            //phase 2: the steps fold out from under the title
    private const float APPEAR_SECONDS = SLIDE_SECONDS + FOLD_SECONDS;  //total appear time (timer is set to this)
    private const float FOLD_STAGGER = 0.4f;                            //fraction of the fold each step's start is offset across
    private const int SLIDE_DIST = 70;
    private readonly Dictionary<string, float> AppearTimers = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> PrevQuestKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool AppearSeeded;

    //first-seen order per quest so the tracker lists oldest-first / NEWEST LAST (a quest started during play drops to
    //the bottom) instead of the server's alphabetical-by-key order. Pruned when a quest leaves, so a re-start counts as new.
    private readonly Dictionary<string, long> AppearSeq = new(StringComparer.OrdinalIgnoreCase);
    private long AppearSeqNext;

    //a completed/abandoned/untracked quest fades + slides OUT before its row is removed (complement to the slide-in)
    private const float DEPART_SECONDS = 0.5f;
    private readonly Dictionary<string, QuestTrackerQuestInfo> Departing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> DepartTimers = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<QuestTrackerQuestInfo> ActiveQuests = [];

    //drag / click handling, all polled from InputBuffer in Update (the dispatcher can drop a mouse-up after a drag)
    private bool CursorOver;
    private bool PressActive;
    private bool Dragging;
    private int PressMouseX, PressMouseY;
    private int DragGrabX, DragGrabY;
    private int PressedQuestIndex = -1;
    private bool PressedOnPin;
    private bool PressedOnBook;

    //position persistence: OffsetX is center-relative (X - screenW/2); OffsetY is anchor-relative (>=0 = distance
    //from the top, <0 = distance from the bottom), exactly like the chat window, so it tracks its edge on a rescale.
    private bool OffsetLoaded;
    private int OffsetX, OffsetY;
    private int LastUiW = -1, LastUiH = -1;

    //the detail popup, a sibling on Root (so it is not clipped by / does not fade with the tracker). Lazily parented.
    private readonly QuestDetailPanel Detail = new();
    private bool DetailParented;
    private int DetailQuestIndex = -1;
    private bool DetailJustOpened;

    public bool Pinned { get; private set; }

    //raised when the journal book button (beside the pin) is clicked; WorldScreen opens the quest journal.
    public event Action? OpenJournalRequested;

    public QuestTrackerControl()
    {
        Name = "QuestTracker";
        Visible = false;

        PinNormalTex = LoadWidgetArt("window_widget_pin.png");
        PinActiveTex = LoadWidgetArt("window_widget_pin_active.png");
        PinPressedTex = LoadWidgetArt("window_widget_pin_pressed.png");

        //pin / sticky toggle. Decorative (the control polls its own rect for the click), fades with the box.
        PinBox = new UIPanel
        {
            Name = "QuestTrackerPin",
            Width = PIN_SIZE,
            Height = PIN_SIZE,
            Background = PinNormalTex,
            IsHitTestVisible = false,
            Visible = false
        };
        AddChild(PinBox);

        //journal shortcut: the in-game book sprite in the same 18x18 footprint as the pin, sitting beside it
        BookTex = UiRenderer.Instance?.GetSpfTexture("_nbklgd.spf");
        BookBox = new UIPanel
        {
            Name = "QuestTrackerJournal",
            Width = BookTex?.Width ?? PIN_SIZE,   //panel draws the sprite at native size; size the box (and hit-rect) to it
            Height = BookTex?.Height ?? PIN_SIZE,
            Background = BookTex,
            IsHitTestVisible = false,
            Visible = false
        };
        AddChild(BookBox);
    }

    private static int ScaledFont(int baseSize)
        => Math.Max(8, (int)MathF.Round(baseSize * ClientSettings.EffectiveQuestFontScale));

    private static int LineH(int size) => TtfTextRenderer.Available ? TtfTextRenderer.LineHeight(size) : size + 4;

    /// <summary>
    ///     Replaces the tracked quests. An empty list hides the panel. Any change brightens the box briefly (then it
    ///     fades back unless pinned), mirroring the chat window's recency flash.
    /// </summary>
    public void SetQuests(IReadOnlyList<QuestTrackerQuestInfo> quests)
    {
        AllQuests = quests;
        ApplyTrackFilter();
    }

    /// <summary>Re-applies the player's untrack filter to the last push (called when the journal toggles tracking).</summary>
    public void ApplyTrackFilter()
    {
        var filtered = AllQuests.Where(q => !ClientSettings.UntrackedQuests.Contains(q.QuestKey)).ToList();
        var newKeys = filtered.Select(q => q.QuestKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (AppearSeeded)
        {
            //a quest that WAS showing and is now gone (completed/abandoned/untracked) fades + slides OUT
            foreach (var old in Quests)
                if (!newKeys.Contains(old.QuestKey) && !Departing.ContainsKey(old.QuestKey))
                {
                    Departing[old.QuestKey] = old;
                    DepartTimers[old.QuestKey] = DEPART_SECONDS;
                }

            //#9: arm a slide-in for genuinely NEW quests
            foreach (var q in filtered)
                if (!PrevQuestKeys.Contains(q.QuestKey))
                    AppearTimers[q.QuestKey] = APPEAR_SECONDS;
        }

        //a re-added quest cancels its departure
        foreach (var q in filtered)
        {
            Departing.Remove(q.QuestKey);
            DepartTimers.Remove(q.QuestKey);
        }

        PrevQuestKeys = newKeys;
        AppearSeeded = true;

        //stamp a first-seen index on any quest we have not ordered yet, forget ones that left, then sort oldest-first
        //so the newest quest always sits at the bottom
        foreach (var q in filtered)
            if (!AppearSeq.ContainsKey(q.QuestKey))
                AppearSeq[q.QuestKey] = AppearSeqNext++;

        foreach (var key in AppearSeq.Keys.Where(k => !newKeys.Contains(k)).ToList())
            AppearSeq.Remove(key);

        ActiveQuests = filtered.OrderBy(q => AppearSeq.GetValueOrDefault(q.QuestKey, long.MaxValue)).ToList();

        RebuildRender();
    }

    //builds the rendered row set = active quests + any still fading out, and refreshes layout/visibility/detail.
    private void RebuildRender()
    {
        var quests = ActiveQuests.Concat(Departing.Values).ToList();
        Quests = quests;

        //keep the bottom edge fixed when docked low so the list grows upward as quests are added/removed
        var oldHeight = Height;
        var bottomAnchored = (LastUiH > 0) && (Y + Height / 2 >= LastUiH / 2);

        Rebuild();

        if (bottomAnchored && (Height != oldHeight))
            Y += oldHeight - Height;

        Visible = quests.Count > 0;

        if (quests.Count == 0)
        {
            CloseDetail();

            return;
        }

        //the box (chrome) no longer lights up on a quest change (start / end / progress) - it stays quiet and only
        //brightens on hover / pin / drag, so starting or finishing a quest does not pop a box; the text just updates

        //if the detail popup is open, refresh it (or close it if its quest is gone)
        if (Detail.Visible)
        {
            if ((DetailQuestIndex >= 0) && (DetailQuestIndex < quests.Count))
                Detail.Build(quests[DetailQuestIndex], TitleFont, RowFont);
            else
                CloseDetail();
        }
    }

    //(re)creates the row labels for the current quests at the current font scale: a title plus EVERY step, the
    //current step highlighted. Layout (positions + alignment) is done every frame in LayoutForQuadrant; this only
    //owns text/colour/size and the overall Height.
    private void Rebuild()
    {
        foreach (var row in Rows)
        {
            Children.Remove(row.Title);

            foreach (var step in row.Steps)
                Children.Remove(step);
        }

        Rows.Clear();

        AppliedScale = ClientSettings.EffectiveQuestFontScale;
        TitleFont = ScaledFont(TITLE_FONT_BASE);
        RowFont = ScaledFont(ROW_FONT_BASE);

        //width is chosen FIRST so the labels below wrap to it; each label's wrapped ContentHeight then drives the
        //row heights, so long objective text spills onto extra lines instead of being cropped
        Width = Math.Max(180, (int)MathF.Round(240 * ClientSettings.EffectiveQuestFontScale));

        var rowsHeight = 0;

        for (var q = 0; q < Quests.Count; q++)
        {
            var quest = Quests[q];

            var title = MakeLabel(quest.Title, TitleFont, TitleColor);
            var steps = new List<UILabel>(quest.Checkpoints.Count);

            //optional lifetime counter line (e.g. "Offerings made: 7"), shown under the title in a soft gold
            if (!string.IsNullOrEmpty(quest.Counter))
                steps.Add(MakeLabel(quest.Counter, RowFont, CounterColor));

            for (var i = 0; i < quest.Checkpoints.Count; i++)
            {
                var checkpoint = quest.Checkpoints[i];
                var current = (quest.CurrentIndex != 255) && (i == quest.CurrentIndex);
                var done = (quest.CurrentIndex != 255) && (i < quest.CurrentIndex);
                var color = current ? CurrentColor : done ? DoneColor : PendingColor;
                var marker = current ? "> " : done ? "- " : "  ";
                var detail = current && !string.IsNullOrEmpty(checkpoint.Detail) ? $"  {checkpoint.Detail}" : string.Empty;

                steps.Add(MakeLabel($"{marker}{checkpoint.Label}{detail}", RowFont, color));

                //green "right place" guide hint under the current step (e.g. the target monster is on this map)
                if (current && !string.IsNullOrEmpty(quest.Hint))
                    steps.Add(MakeLabel($"  {quest.Hint}", RowFont, HintColor));
            }

            Rows.Add(new QuestRow(q, title, steps));

            rowsHeight += title.Height;

            foreach (var step in steps)
                rowsHeight += step.Height;

            if (q < Quests.Count - 1)
                rowsHeight += QUEST_GAP;
        }

        Height = HEADER_H + PAD + rowsHeight + PAD;
    }

    private UILabel MakeLabel(string text, int fontSize, Color color)
    {
        var label = new UILabel
        {
            X = PAD,
            Width = Math.Max(20, Width - PAD * 2),
            CustomFontSize = fontSize,
            ForegroundColor = color,
            WordWrap = true,          //wrap long objective text onto extra lines instead of cropping it
            Text = text,
            //render like the chat text: a full 1px dark outline + drop shadow, so it stays readable on ANY
            //background (crisper than the old soft GlowAlpha backing, which washed out over bright/busy tiles)
            ShadowStyle = ShadowStyle.BottomRight,
            ShadowOffset = new Point(0, 3),
            IsHitTestVisible = false  //decorative; clicks are resolved by the control's own polling
        };

        label.Height = Math.Max(LineH(fontSize), label.ContentHeight) + 3; //+ bottom room for the drop shadow; tall enough for every wrapped line

        AddChild(label);

        return label;
    }

    //positions the pin + every row for the current screen quadrant. Cheap, so it runs each frame.
    private void LayoutForQuadrant(bool leftHalf, bool topHalf)
    {
        var innerW = Math.Max(20, Width - PAD * 2);
        var align = leftHalf ? HorizontalAlignment.Left : HorizontalAlignment.Right;

        //pin sits in the docked corner: near horizontal edge, on the docked vertical (header) strip
        PinBox.X = leftHalf ? PAD : Width - PAD - PIN_SIZE;
        PinBox.Y = topHalf ? 2 : Height - HEADER_H + 2;

        //the journal book sits right beside the pin (inward, so it always stays on the header strip), centred on it
        BookBox.X = leftHalf ? PinBox.X + PIN_SIZE + 2 : PinBox.X - BookBox.Width - 2;
        BookBox.Y = PinBox.Y + (PIN_SIZE - BookBox.Height) / 2;

        //rows fill the space opposite the header strip
        var y = topHalf ? HEADER_H + PAD : PAD;

        for (var ri = 0; ri < Rows.Count; ri++)
        {
            var row = Rows[ri];
            var blockTop = y;
            var key = ri < Quests.Count ? Quests[ri].QuestKey : null;

            //APPEAR: title slides in (phase 1), then steps fold out from under it (phase 2). DEPART: the whole block
            //slides out + fades. A settled row is a no-op (slideX 0, full opacity, fully unfolded).
            var slideX = 0;
            var titleOpacity = 1f;
            var appearing = false;
            var foldP = 1f;        //phase-2 progress: 1 = steps fully unfolded
            var departFade = 1f;   //1 = present; multiplies step opacity while a quest fades out

            if ((key is not null) && AppearTimers.TryGetValue(key, out var t))
            {
                appearing = true;
                var elapsed = APPEAR_SECONDS - t;
                var titleP = MathHelper.Clamp(elapsed / SLIDE_SECONDS, 0f, 1f);
                var titleEase = 1f - ((1f - titleP) * (1f - titleP) * (1f - titleP)); //easeOutCubic
                slideX = (int)((leftHalf ? -1 : 1) * SLIDE_DIST * (1f - titleEase));
                titleOpacity = titleEase;
                foldP = MathHelper.Clamp((elapsed - SLIDE_SECONDS) / FOLD_SECONDS, 0f, 1f);
            } else if ((key is not null) && DepartTimers.TryGetValue(key, out var td))
            {
                var p = MathHelper.Clamp(td / DEPART_SECONDS, 0f, 1f); //1 -> 0 over the fade
                var eased = p * p;
                slideX = (int)((leftHalf ? -1 : 1) * SLIDE_DIST * (1f - eased));
                titleOpacity = eased;
                departFade = eased;
            }

            row.Title.X = PAD + slideX;
            row.Title.Y = y;
            row.Title.Width = innerW;
            row.Title.HorizontalAlignment = align;
            row.Title.Opacity = titleOpacity;
            y += row.Title.Height;

            //the steps' collapsed origin is tucked right under the title; each lerps DOWN to its laid-out slot as it
            //unfolds. The block's height stays reserved (set in Rebuild), so this animates within it - no reflow.
            var titleBottom = y;
            var stepCount = row.Steps.Count;

            for (var si = 0; si < stepCount; si++)
            {
                var step = row.Steps[si];
                var targetY = y;

                if (appearing)
                {
                    //each step starts a little after the one above it, so they cascade out from under the title
                    var delay = stepCount > 1 ? (float)si / stepCount * FOLD_STAGGER : 0f;
                    var sp = MathHelper.Clamp((foldP - delay) / (1f - FOLD_STAGGER), 0f, 1f);
                    var spEased = 1f - ((1f - sp) * (1f - sp) * (1f - sp)); //easeOutCubic
                    step.Y = (int)MathHelper.Lerp(titleBottom, targetY, spEased);
                    step.Opacity = spEased;
                } else
                {
                    step.Y = targetY;
                    step.Opacity = departFade;
                }

                step.X = PAD + slideX;
                step.Width = innerW;
                step.HorizontalAlignment = align;
                y += step.Height;
            }

            row.BlockTop = blockTop;
            row.BlockBottom = y;
            y += QUEST_GAP;
        }
    }

    /// <inheritdoc />
    public override void Update(GameTime gameTime)
    {
        var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;

        //#9: decay the slide-in timers
        if (AppearTimers.Count > 0)
            foreach (var key in AppearTimers.Keys.ToList())
            {
                var remaining = AppearTimers[key] - dt;

                if (remaining <= 0f)
                    AppearTimers.Remove(key);
                else
                    AppearTimers[key] = remaining;
            }

        //decay the fade-OUT timers; when one finishes, drop the departed quest + rebuild without its row
        if (DepartTimers.Count > 0)
        {
            var anyFinished = false;

            foreach (var key in DepartTimers.Keys.ToList())
            {
                var remaining = DepartTimers[key] - dt;

                if (remaining <= 0f)
                {
                    DepartTimers.Remove(key);
                    Departing.Remove(key);
                    anyFinished = true;
                } else
                    DepartTimers[key] = remaining;
            }

            if (anyFinished)
                RebuildRender();
        }

        //lazily parent the detail popup onto Root once we know our parent (so it is not clipped by / faded with us)
        if (!DetailParented && (Parent is not null))
        {
            Parent.AddChild(Detail);
            DetailParented = true;
        }

        //re-apply a live chat-font-scale change (matches the chat window's behaviour)
        if (ClientSettings.EffectiveQuestFontScale != AppliedScale)
            Rebuild();

        //first update: load the saved position (or default to the top-left corner) into the offset encoding
        if (!OffsetLoaded)
        {
            OffsetLoaded = true;
            LastUiW = ChaosGame.UiWidth;
            LastUiH = ChaosGame.UiHeight;

            if ((ClientSettings.QuestTrackerOffsetX != int.MinValue) && (ClientSettings.QuestTrackerOffsetY != int.MinValue))
            {
                OffsetX = ClientSettings.QuestTrackerOffsetX;
                OffsetY = ClientSettings.QuestTrackerOffsetY;
            } else
            {
                //default: top-left corner
                X = 48 - PAD;
                Y = 72;
                StoreOffset();
            }

            ApplyOffset();
        }

        //follow the anchored edge when the game window is resized (only when not being dragged), like the chat window
        var uiW = ChaosGame.UiWidth;
        var uiH = ChaosGame.UiHeight;

        if (!Dragging && ((uiW != LastUiW) || (uiH != LastUiH)))
            ApplyOffset();

        LastUiW = uiW;
        LastUiH = uiH;

        HandlePointer();

        var centerX = X + Width / 2;
        var centerY = Y + Height / 2;
        var leftHalf = centerX < uiW / 2;
        var topHalf = centerY < uiH / 2;

        LayoutForQuadrant(leftHalf, topHalf);

        //box brightness: lit while interactive (hovered / pinned / dragging) or briefly after a change
        if (!Pinned && (BrightSeconds > 0))
            BrightSeconds -= dt;

        var lit = Pinned || Dragging || CursorOver || (BrightSeconds > 0);
        var target = lit ? 1f : 0f;
        ChromeOpacity = MathHelper.Lerp(ChromeOpacity, target, Math.Clamp(dt * 8f, 0f, 1f));

        ApplyChrome();

        UpdateDetailDismiss();

        base.Update(gameTime);
    }

    //the box fill + border + pin fade together with ChromeOpacity; the quest text never fades (it has its own outline).
    private void ApplyChrome()
    {
        BackgroundColor = new Color(12, 10, 8) * (ChromeOpacity * 0.8f);
        BorderColor = new Color(90, 78, 54) * ChromeOpacity;

        var overPin = PointInPin(InputBuffer.MouseX, InputBuffer.MouseY);
        PinBox.Background = PressedOnPin && PressActive && overPin ? PinPressedTex : Pinned ? PinActiveTex : PinNormalTex;
        PinBox.BackgroundOpacity = ChromeOpacity;
        PinBox.Visible = ChromeOpacity > 0.05f;

        var overBook = PointInBook(InputBuffer.MouseX, InputBuffer.MouseY);
        BookBox.BackgroundOpacity = ChromeOpacity * (PressedOnBook && PressActive && overBook ? 0.55f : 1f);
        BookBox.Visible = ChromeOpacity > 0.05f;
    }

    //press / drag / click resolution, polled from the global mouse state so a dropped mouse-up can never strand us
    private void HandlePointer()
    {
        var mx = InputBuffer.MouseX;
        var my = InputBuffer.MouseY;

        if (PressActive)
        {
            if (!Dragging && (Math.Abs(mx - PressMouseX) + Math.Abs(my - PressMouseY) > DRAG_THRESHOLD))
                Dragging = true;

            if (Dragging)
            {
                X = mx - DragGrabX;
                Y = my - DragGrabY;
                ClampOnScreen();
            }
        }
    }

    public override void OnMouseDown(MouseDownEvent e)
    {
        var mx = e.ScreenX;
        var my = e.ScreenY;

        PressActive = true;
        Dragging = false;
        PressMouseX = mx;
        PressMouseY = my;
        DragGrabX = mx - X;
        DragGrabY = my - Y;
        PressedOnPin = PointInPin(mx, my);
        PressedOnBook = PointInBook(mx, my);
        PressedQuestIndex = PressedOnPin || PressedOnBook ? -1 : QuestIndexAt(mx, my);

        e.Handled = true;
    }

    public override void OnMouseUp(MouseUpEvent e)
    {
        var mx = e.ScreenX;
        var my = e.ScreenY;

        if (Dragging)
            StoreOffset();
        else if (PressedOnPin && PointInPin(mx, my))
            TogglePinned();
        else if (PressedOnBook && PointInBook(mx, my))
        {
            SoundSystem.PlayUiClick();
            OpenJournalRequested?.Invoke();
        }
        else if ((PressedQuestIndex >= 0) && (QuestIndexAt(mx, my) == PressedQuestIndex))
            ToggleDetail(PressedQuestIndex);

        PressActive = false;
        Dragging = false;

        e.Handled = true;
    }

    public override void OnMouseEnter() => CursorOver = true;
    public override void OnMouseLeave() => CursorOver = false;

    private void TogglePinned()
    {
        Pinned = !Pinned;
        SoundSystem.PlayUiClick();

        if (!Pinned)
            BrightSeconds = BRIGHT_SECONDS; //ease out instead of snapping dark
    }

    private void ToggleDetail(int questIndex)
    {
        SoundSystem.PlayUiClick();

        if (Detail.Visible && (DetailQuestIndex == questIndex))
        {
            CloseDetail();

            return;
        }

        if ((questIndex < 0) || (questIndex >= Quests.Count))
            return;

        DetailQuestIndex = questIndex;
        Detail.Build(Quests[questIndex], TitleFont, RowFont);
        PositionDetail(questIndex);
        Detail.Visible = true;
        DetailJustOpened = true;
    }

    private void CloseDetail()
    {
        Detail.Visible = false;
        DetailQuestIndex = -1;
    }

    //places the detail popup on whichever side of the tracker has room, vertically near the clicked quest, on-screen
    private void PositionDetail(int questIndex)
    {
        var uiW = ChaosGame.UiWidth;
        var uiH = ChaosGame.UiHeight;
        var leftHalf = X + Width / 2 < uiW / 2;

        var dx = leftHalf ? X + Width + 6 : X - Detail.Width - 6;

        //if it would spill off, flip to the other side
        if (dx + Detail.Width > uiW)
            dx = X - Detail.Width - 6;

        if (dx < 0)
            dx = X + Width + 6;

        var block = Rows.Count > questIndex ? Rows[questIndex] : null;
        var dy = Y + (block?.BlockTop ?? 0);

        Detail.X = Math.Clamp(dx, 0, Math.Max(0, uiW - Detail.Width));
        Detail.Y = Math.Clamp(dy, 0, Math.Max(0, uiH - Detail.Height));
    }

    //closes the detail popup on Escape or a press anywhere outside it (and outside the tracker, which handles its own
    //clicks). The frame it opened is skipped so the opening click does not immediately dismiss it.
    private void UpdateDetailDismiss()
    {
        if (!Detail.Visible)
            return;

        if (DetailJustOpened)
        {
            DetailJustOpened = false;

            return;
        }

        if (InputBuffer.WasKeyPressed(Keys.Escape))
        {
            CloseDetail();

            return;
        }

        foreach (var evt in InputBuffer.Events)
            if (evt is { Kind: BufferedInputKind.MouseButton, IsPress: true })
            {
                var inDetail = Detail.ScreenBounds.Contains(evt.X, evt.Y);
                var inTracker = PointInPanel(evt.X, evt.Y);

                if (!inDetail && !inTracker)
                    CloseDetail();

                break;
            }
    }

    private bool PointInPanel(int x, int y)
        => (x >= ScreenX) && (x < ScreenX + Width) && (y >= ScreenY) && (y < ScreenY + Height);

    private bool PointInPin(int x, int y)
        => PinBox.Visible
           && (x >= ScreenX + PinBox.X) && (x < ScreenX + PinBox.X + PIN_SIZE)
           && (y >= ScreenY + PinBox.Y) && (y < ScreenY + PinBox.Y + PIN_SIZE);

    private bool PointInBook(int x, int y)
        => BookBox.Visible
           && (x >= ScreenX + BookBox.X) && (x < ScreenX + BookBox.X + BookBox.Width)
           && (y >= ScreenY + BookBox.Y) && (y < ScreenY + BookBox.Y + BookBox.Height);

    //which quest block the cursor is over (panel-local Y against the laid-out block extents), or -1
    private int QuestIndexAt(int x, int y)
    {
        var localY = y - ScreenY;

        foreach (var row in Rows)
            if ((localY >= row.BlockTop) && (localY < row.BlockBottom))
                return row.QuestIndex;

        return -1;
    }

    private void ClampOnScreen()
    {
        X = Math.Clamp(X, 0, Math.Max(0, ChaosGame.UiWidth - Width));
        Y = Math.Clamp(Y, 0, Math.Max(0, ChaosGame.UiHeight - Height));
    }

    //encode the current top-left into the center/anchor-relative offset and persist it
    private void StoreOffset()
    {
        var uiW = ChaosGame.UiWidth;
        var uiH = ChaosGame.UiHeight;
        OffsetX = X - uiW / 2;
        OffsetY = Y + Height / 2 < uiH / 2 ? Y : Y - uiH;
        ClientSettings.QuestTrackerOffsetX = OffsetX;
        ClientSettings.QuestTrackerOffsetY = OffsetY;
        ClientSettings.Save();
    }

    //resolve the offset back to a screen position for the current window size
    private void ApplyOffset()
    {
        X = ChaosGame.UiWidth / 2 + OffsetX;
        Y = OffsetY >= 0 ? OffsetY : ChaosGame.UiHeight + OffsetY;
        ClampOnScreen();
    }

    public override void Dispose()
    {
        PinBox.Background = null;
        PinNormalTex?.Dispose();
        PinActiveTex?.Dispose();
        PinPressedTex?.Dispose();

        BookBox.Background = null; //BookTex is a cached UiRenderer sprite, not owned here

        base.Dispose();
    }

    //one tracked quest's labels (title + every step) + its laid-out vertical extent (for click hit-testing)
    private sealed class QuestRow(int questIndex, UILabel title, List<UILabel> steps)
    {
        public int QuestIndex { get; } = questIndex;
        public UILabel Title { get; } = title;
        public List<UILabel> Steps { get; } = steps;
        public int BlockTop { get; set; }
        public int BlockBottom { get; set; }
    }

    /// <summary>
    ///     The floating quest-info window for one quest (the "journal entry"): the quest title + its description,
    ///     then every step with its own authored explanation, the current step highlighted. This is the "what am I
    ///     supposed to do now" panel. An always-opaque dark box that swallows its own clicks.
    /// </summary>
    private sealed class QuestDetailPanel : UIPanel
    {
        private const int DPAD = 9;
        private const int LINE_GAP = 2;
        private const int STEP_GAP = 6;
        private const int DESC_INDENT = 12;

        private static readonly Color QuestDescColor = new(210, 200, 176); //light parchment for the quest blurb
        private static readonly Color StepDescColor = new(182, 174, 158);  //dim for per-step explanations

        private readonly List<UILabel> Lines = [];

        public QuestDetailPanel()
        {
            Name = "QuestDetail";
            Visible = false;
            ZIndex = 120_000; //above the tracker + HUD, below modal dialogs
            BackgroundColor = new Color(16, 14, 10) * 0.97f;
            BorderColor = new Color(96, 82, 56);
        }

        public void Build(QuestTrackerQuestInfo quest, int titleFont, int rowFont)
        {
            foreach (var line in Lines)
                Children.Remove(line);

            Lines.Clear();

            var width = Math.Max(240, (int)MathF.Round(300 * ClientSettings.EffectiveQuestFontScale));
            var innerW = width - DPAD * 2;
            var y = DPAD;

            AddLine(quest.Title, titleFont + 1, TitleColor, DPAD, innerW, ref y, wrap: true);

            if (!string.IsNullOrEmpty(quest.Description))
            {
                y += LINE_GAP * 2;
                AddLine(quest.Description, rowFont, QuestDescColor, DPAD, innerW, ref y, wrap: true);
            }

            y += STEP_GAP;

            for (var i = 0; i < quest.Checkpoints.Count; i++)
            {
                var checkpoint = quest.Checkpoints[i];
                var current = (quest.CurrentIndex != 255) && (i == quest.CurrentIndex);
                var done = (quest.CurrentIndex != 255) && (i < quest.CurrentIndex);
                var color = current ? CurrentColor : done ? DoneColor : PendingColor;
                var marker = current ? "> " : done ? "- " : "  ";
                var detail = current && !string.IsNullOrEmpty(checkpoint.Detail) ? $"  {checkpoint.Detail}" : string.Empty;

                AddLine($"{marker}{checkpoint.Label}{detail}", rowFont, color, DPAD, innerW, ref y, wrap: false);

                if (!string.IsNullOrEmpty(checkpoint.Description))
                {
                    //the current step's explanation is brighter so "what to do now" reads at a glance
                    var descColor = current ? QuestDescColor : StepDescColor;
                    AddLine(checkpoint.Description, rowFont, descColor, DPAD + DESC_INDENT, innerW - DESC_INDENT, ref y, wrap: true);
                }

                y += STEP_GAP;
            }

            Width = width;
            Height = y - STEP_GAP + DPAD;
        }

        //adds one label at (x, y), advancing y. wrap=true word-wraps to the given width and grows to its content height.
        private void AddLine(string text, int fontSize, Color color, int x, int width, ref int y, bool wrap)
        {
            var label = new UILabel
            {
                X = x,
                Y = y,
                Width = width,
                WordWrap = wrap,             //set before Text so the wrap width is applied on the first measure
                CustomFontSize = fontSize,
                ForegroundColor = color,
                Text = text,
                TruncateWithEllipsis = !wrap,
                IsHitTestVisible = false
            };

            var lineH = TtfTextRenderer.Available ? TtfTextRenderer.LineHeight(fontSize) : fontSize + 4;
            //+2 covers the label's 1px top/bottom padding so the last wrapped line is never clipped
            label.Height = wrap ? Math.Max(lineH, label.ContentHeight) + 2 : lineH;

            AddChild(label);
            Lines.Add(label);
            y += label.Height + LINE_GAP;
        }
    }
}
