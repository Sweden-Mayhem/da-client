#region
using Chaos.Client.Collections;
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.World.Hud;
using Chaos.Client.Controls.World.Hud.Panel;
using Chaos.Client.Data;
using Chaos.Client.Systems;
using Chaos.Client.ViewModel;
using Microsoft.Xna.Framework;
#endregion

namespace Chaos.Client.Controls.World.Menu;

public sealed class ChatWindow : DraggableWindow
{
    private const int CHAT_FONT = 14; //base TrueType size at 1.0x, multiplied by the "Chat font size" slider
    private const int TABS_H = 18; //the tab strip across the top of the content

    private readonly ChatPanel Chat;
    private readonly ChatTabBar Tabs;

    //small right-click tab menu shown over the content: a per-tab "Highlight" toggle (all tabs) + "Leave channel" (custom
    //channels only). TabMenuKey is the persisted highlight key, TabMenuChannel is the "!name" for Leave.
    private readonly UIPanel TabMenu;
    private readonly UILabel TabMenuTitle;
    private readonly Checkbox TabMenuHighlight;
    private readonly MenuButton TabMenuSetColor;
    private readonly MenuButton TabMenuLeave;
    private string TabMenuKey = "";
    private string? TabMenuChannel;
    private bool TabMenuWasVisible;

    //"Set color" submenu: a colour-tinted list (no numbers to memorize). The names are what /setchannelcolor accepts.
    //The letters are the matching colour-code chars used to tint each label its own colour.
    private readonly UIPanel TabColorMenu;
    private string? TabColorChannel;
    private bool TabColorWasVisible;

    private static readonly (string Name, char Code)[] ChannelColors =
    [
        ("Red", 'b'), ("Orange", 's'), ("Yellow", 'c'), ("NeonGreen", 'q'), ("DarkGreen", 'd'), ("Blue", 'f'),
        ("HotPink", 'o'), ("Purple", 'p'), ("White", 'u'), ("Gray", 'a'), ("Silver", 'e'), ("Brown", 't')
    ];

    //current scaled font size + the matching input-line height, recomputed live when ClientSettings.ChatFontScale changes
    private int FontSize;
    private int InputH;
    private float AppliedChatScale = -1f;

    //seconds the window stays shown after the input loses focus (set on blur, counts down in Update)
    private float ShowTimer;

    //position/size persistence uses the same center-relative X, anchor-relative Y encoding as the minimap and menu button
    private bool OffsetLoaded;
    private bool HasOffset;
    private bool WasDragging;
    private bool SuppressResizeSave;
    private int LastUiW = -1, LastUiH = -1;
    //OffsetX is center-relative (X - screenW/2). OffsetY is anchor-relative: positive is distance from top,
    //negative is distance from bottom (Y - screenH), matching the element's vertical-center half.
    private int OffsetX, OffsetY;

    //HUD-avoidance rects filled by WorldScreen.AnchorHotbars() each frame (hotbars, orbs, minimap, menu button)
    private static readonly Rectangle[] HudBuf = new Rectangle[10];
    private static int HudCount;

    internal static void BeginHudRects() => HudCount = 0;

    internal static void AddHudRect(Rectangle r)
    {
        if ((r.Width > 0) && (r.Height > 0) && (HudCount < HudBuf.Length))
            HudBuf[HudCount++] = r;
    }

    private const int MIN_CHAT_W = 150;
    private const int MIN_CHAT_H = 80;

    public ChatInputControl ChatInput { get; }

    public ChatChannel? ActiveChannel => Tabs.ActiveChannel;

    public ChatWindow(int width, int height)
        //wood frame like the rest of the windows, no close box (chat is always present and fades on its own)
        : base("Chat", width, height, showClose: false, useWoodFrame: true)
    {
        AutoFade = true;
        IdleOpacity = 0f; //fully fade out when idle (chrome, text, and scrollbar)
        Resizable = true;
        Fade = 0f; //start invisible on login. New lines (incl. the login channel messages) flash it visible (below)

        //lay out off the base Content box (sized for the active frame mode) so the wood inset is respected automatically
        var contentW = Content.Width;
        var contentH = Content.Height;

        FontSize = ScaledFont();
        InputH = ScaledInputH(FontSize);
        AppliedChatScale = ClientSettings.EffectiveChatFontScale;

        var chatH = contentH - TABS_H - InputH; //chat log sits between the tab strip and the input line
        var displayH = chatH - 4;

        //tab strip across the top: All / Public / Whisper / System (+ Group / Guild when relevant)
        Tabs = new ChatTabBar(contentW - 4, TABS_H)
        {
            X = 2,
            Y = 0
        };
        Content.AddChild(Tabs);

        //panelBounds at the origin so the chat's internal coordinates are local to its own box (see ChatPanel.Resize).
        //system/orange messages now arrive as normal channel-tagged messages, so includeOrangeBar is no longer needed.
        Chat = new ChatPanel(
            new Rectangle(2, 2, contentW - 4, displayH),
            new Rectangle(0, 0, contentW, chatH),
            FontSize,
            drawBackground: false)
        {
            X = 0,
            Y = TABS_H,
            Width = contentW,
            Height = chatH
        };

        Content.AddChild(Chat);

        //picking a tab filters the chat to that channel (null = All)
        Tabs.TabSelected += Chat.SetActiveChannel;
        //...and, if you're already typing, switches your input to that tab's speaking mode (All/Public -> /say)
        Tabs.TabSelected += (channel, name) => ChatInput?.SetInputToTab(channel, name);

        //typing line, hosted here so it stays functional regardless of the hidden HUD (WorldScreen wires its send events).
        //it uses the SAME (scaled) TrueType font as the chat log, so what you type matches what is shown and it can
        //display characters the bitmap font lacks (å/ä/ö).
        ChatInput = new ChatInputControl(DataContext.UserControls.Get("_nbk_s")!) { CustomFontSize = FontSize };
        ChatInput.SetBounds(2, contentH - InputH, contentW - 4, InputH);
        Content.AddChild(ChatInput);

        //Tab / Shift+Tab while typing cycles the tab strip forward / back (wired here, after ChatInput exists)
        ChatInput.TabCycleRequested += Tabs.CycleTab;

        //the "+" tab button prompts for a channel name and joins it (the server prepends the "!" prefix)
        Tabs.AddChannelRequested += () =>
            ChatInput.ShowPrompt("Join channel: ", 24, name =>
            {
                name = name.Trim().TrimStart('!');

                if (name.Length > 0)
                    ChatInput.SendPublic("/joinchannel " + name);
            });

        //right-click a tab -> a tiny menu at the cursor (drops down over the chat log): a "Highlight" toggle on every tab
        //plus "Leave channel" on custom channels
        const int MENU_W = 150;

        TabMenu = new UIPanel
        {
            Width = MENU_W,
            Height = 94,
            BackgroundColor = new Color(27, 23, 16) * 0.97f,
            BorderColor = new Color(88, 72, 46),
            ZIndex = 50_000,
            Visible = false
        };

        TabMenuTitle = new UILabel
        {
            X = 6,
            Y = 2,
            Width = MENU_W - 12,
            Height = 20,
            CustomFontSize = 13,
            ForegroundColor = new Color(214, 196, 150),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            TruncateWithEllipsis = true
        };

        TabMenuHighlight = new Checkbox("Highlight", MENU_W - 12, true) { X = 6, Y = 22 };
        TabMenuHighlight.Changed += isChecked =>
        {
            if (TabMenuKey.Length > 0)
                ClientSettings.SetChatTabMuted(TabMenuKey, !isChecked); //unchecked = muted (no pulse), saved immediately
        };

        TabMenuSetColor = new MenuButton("Set color", MENU_W - 4, 22) { X = 2, Y = 46 };
        TabMenuSetColor.Clicked += _ =>
        {
            var chan = TabMenuChannel;
            var lx = TabMenu.X;
            var ly = TabMenu.Y;
            HideTabMenu();

            if (chan is { Length: > 0 })
                ShowColorMenu(chan, lx, ly);
        };

        TabMenuLeave = new MenuButton("Leave channel", MENU_W - 4, 22) { X = 2, Y = 70 };
        TabMenuLeave.Clicked += _ =>
        {
            if (TabMenuChannel is { Length: > 0 } chan)
                ChatInput.SendPublic("/leavechannel " + chan.TrimStart('!'));

            HideTabMenu();
        };

        TabMenu.AddChild(TabMenuTitle);
        TabMenu.AddChild(TabMenuHighlight);
        TabMenu.AddChild(TabMenuSetColor);
        TabMenu.AddChild(TabMenuLeave);
        Content.AddChild(TabMenu);

        //the colour submenu: a 2-column grid of colour-tinted buttons (kept short so it fits the chat content height).
        //Clicking one runs /setchannelcolor for the channel.
        const int COL_COLS = 2;
        const int COL_CELL_W = 92;
        const int COL_CELL_H = 22;
        var colRows = (ChannelColors.Length + COL_COLS - 1) / COL_COLS;

        TabColorMenu = new UIPanel
        {
            Width = (COL_COLS * COL_CELL_W) + 4,
            Height = (colRows * COL_CELL_H) + 4,
            BackgroundColor = new Color(27, 23, 16) * 0.97f,
            BorderColor = new Color(88, 72, 46),
            ZIndex = 50_001,
            Visible = false
        };

        for (var i = 0; i < ChannelColors.Length; i++)
        {
            var (name, code) = ChannelColors[i];
            var tint = TextRenderer.GetColorCode(code) ?? Color.White;
            var btn = new MenuButton(name, COL_CELL_W - 2, COL_CELL_H - 2, tint)
            {
                X = 2 + ((i % COL_COLS) * COL_CELL_W),
                Y = 2 + ((i / COL_COLS) * COL_CELL_H)
            };

            btn.Clicked += _ =>
            {
                var chan = TabColorChannel;
                HideColorMenu();

                if (chan is { Length: > 0 })
                    ChatInput.SendPublic($"/setchannelcolor {chan.TrimStart('!')} {name}");
            };

            TabColorMenu.AddChild(btn);
        }

        Content.AddChild(TabColorMenu);

        Tabs.TabContext += ShowTabMenu;

        //start typing -> raise the window above any others; stop typing -> keep it up for the linger, then it fades
        ChatInput.FocusChanged += focused =>
        {
            if (focused)
                BringToFront();
            else
                ShowTimer = ClientSettings.ChatWindowFadeSeconds;
        };

        //chat is non-modal: while typing, clicks inside this window are allowed (and stop typing); world clicks stay
        //blocked. Tell the dispatcher the WHOLE window is the focus container, not just the input line.
        if (InputDispatcher.Instance is { } dispatcher)
            dispatcher.ChatInputContainer = this;

        //NOTE: a new message does NOT reveal the window. It only pulses the relevant tab (handled in ChatTabBar, and
        //only for an inactive, non-"All" tab). Fading the whole window IN happens ONLY when the player starts typing.
    }

    //base size * the slider (clamped); the input line is tall enough for that font plus a little breathing room
    private static int ScaledFont() => Math.Max(8, (int)MathF.Round(CHAT_FONT * ClientSettings.EffectiveChatFontScale));
    private static int ScaledInputH(int fontSize) => (TtfTextRenderer.Available ? TtfTextRenderer.LineHeight(fontSize) : 16) + 4;

    //re-applies the chat font scale to the log + the input line, then re-lays the contents. Called live from Update when
    //the "Chat font size" slider moves.
    private void ApplyChatFontScale()
    {
        AppliedChatScale = ClientSettings.EffectiveChatFontScale;
        FontSize = ScaledFont();
        InputH = ScaledInputH(FontSize);

        ChatInput.CustomFontSize = FontSize;
        Chat.SetCustomFontSize(FontSize);
        LayoutContents();
    }

    //positions the tab strip, chat log and input line for the current content size + input height (shared by OnResized
    //and the font-scale change). The chat log fills the space between the tab strip and the input line.
    private void LayoutContents()
    {
        var contentW = Content.Width;
        var contentH = Content.Height;

        Tabs.Width = contentW - 4;
        Chat.Resize(contentW, contentH - TABS_H - InputH);
        ChatInput.SetBounds(2, contentH - InputH, contentW - 4, InputH);
    }

    public override void Update(GameTime gameTime)
    {
        //apply a live change to the "Chat font size" slider before the fade logic / base update
        if (ClientSettings.EffectiveChatFontScale != AppliedChatScale)
            ApplyChatFontScale();

        //first update: load saved position and size from settings
        if (!OffsetLoaded)
        {
            OffsetLoaded = true;
            var savedX = ClientSettings.ChatWindowOffsetX;
            var savedY = ClientSettings.ChatWindowOffsetY;
            var savedW = ClientSettings.ChatWindowWidth;
            var savedH = ClientSettings.ChatWindowHeight;

            if ((savedX != int.MinValue) && (savedY != int.MinValue))
            {
                HasOffset = true;
                OffsetX = savedX;
                OffsetY = savedY;

                if ((savedW > 0) && (savedH > 0))
                    Resize(savedW, savedH);
            }
        }

        //non-drag: recompute X/Y from stored offsets ON WINDOW RESIZE so the chat follows its anchor edge
        var uiW = ChaosGame.UiWidth;
        var uiH = ChaosGame.UiHeight;

        if (!IsUserDragging && HasOffset && ((uiW != LastUiW) || (uiH != LastUiH)))
        {
            X = ChaosGame.UiWidth / 2 + OffsetX;
            Y = OffsetY >= 0 ? OffsetY : ChaosGame.UiHeight + OffsetY;
        }

        var delay = ClientSettings.ChatWindowFadeSeconds;

        //the window fades IN only when typing (ComputeFadeTarget). After you stop typing it lingers for `delay` seconds;
        //while it is still substantially visible the cursor being inside REFRESHES the linger so you can read/scroll (and
        //re-shows it if it had started fading). The cursor does NOT reveal an already-hidden window, so a message arriving
        //(or hovering where the chat would be) never pops it up - only typing does.
        if (!ChatInput.IsFocused && !Pinned && (delay > 0))
        {
            if (CursorOnTop() && (Fade > 0.3f))
                ShowTimer = delay;
            else if (ShowTimer > 0f)
                ShowTimer = Math.Max(0f, ShowTimer - (float)gameTime.ElapsedGameTime.TotalSeconds);
        }

        //close the right-click tab menu / colour submenu on a press outside it, or Escape (the opening frame is skipped)
        TabMenuWasVisible = DismissMenuOnOutsideClick(TabMenu, TabMenuWasVisible, HideTabMenu);
        TabColorWasVisible = DismissMenuOnOutsideClick(TabColorMenu, TabColorWasVisible, HideColorMenu);

        base.Update(gameTime); //DraggableWindow: handles drag movement + KeepOnScreen (50%)

        if ((uiW != LastUiW) || (uiH != LastUiH))
        {
            ClampToScreen();
            LastUiW = uiW;
            LastUiH = uiH;
        }

        //at most 10% of the chat window may be off-screen at any time
        X = Math.Clamp(X, -(int)(Width * 0.05f), ChaosGame.UiWidth - (int)(Width * 0.95f));
        Y = Math.Clamp(Y, -(int)(Height * 0.05f), ChaosGame.UiHeight - (int)(Height * 0.95f));

        //detect drag end and persist the clamped position
        if (WasDragging && !IsUserDragging)
            SavePosition();

        WasDragging = IsUserDragging;
    }

    //keeps the chat on-screen and clear of HUD elements. For each axis:
    //  - push the chat inward from any overflowing screen edge
    //  - if that push would collide with a HUD element, constrain against it instead
    //  - if the constrained slot is narrower/shorter than the current size, shrink (not saved as user's size)
    private void ClampToScreen()
    {
        var uiW = ChaosGame.UiWidth;
        var uiH = ChaosGame.UiHeight;

        //available region starts as full screen; each HUD element shrinks it from the relevant side
        var loX = 0;
        var hiX = uiW;
        var loY = 0;
        var hiY = uiH;

        var chatCX = X + Width / 2;
        var chatCY = Y + Height / 2;

        for (var i = 0; i < HudCount; i++)
        {
            var h = HudBuf[i];
            var hR = h.X + h.Width;
            var hB = h.Y + h.Height;

            //vertical constraint, applied when the HUD horizontally overlaps the chat
            if ((h.X < X + Width) && (hR > X))
            {
                if (h.Y + h.Height / 2 <= chatCY)
                    loY = Math.Max(loY, hB); //HUD above chat centre, floor rises
                else
                    hiY = Math.Min(hiY, h.Y); //HUD below chat centre, ceiling drops
            }

            //horizontal constraint, applied when the HUD vertically overlaps the chat
            if ((h.Y < Y + Height) && (hB > Y))
            {
                if (h.X + h.Width / 2 <= chatCX)
                    loX = Math.Max(loX, hR); //HUD left of chat centre, left wall moves right
                else
                    hiX = Math.Min(hiX, h.X); //HUD right of chat centre, right wall moves left
            }
        }

        FitInSlot(X, Width, loX, hiX, chatCX >= uiW / 2, MIN_CHAT_W, uiW, out var newX, out var newW);
        FitInSlot(Y, Height, loY, hiY, chatCY >= uiH / 2, MIN_CHAT_H, uiH, out var newY, out var newH);

        X = newX;
        Y = newY;

        if ((newW != Width) || (newH != Height))
        {
            SuppressResizeSave = true;
            Resize(newW, newH);
            SuppressResizeSave = false;
        }
    }

    private static void FitInSlot(int pos, int size, int lo, int hi, bool nearFarEdge, int minSize, int screenSize, out int newPos, out int newSize)
    {
        var avail = hi - lo;

        if (avail <= 0) //conflicting constraints; fall back to screen-edge clamp only
        {
            newSize = Math.Max(minSize, Math.Min(size, screenSize));
            newPos = Math.Clamp(pos, 0, Math.Max(0, screenSize - newSize));

            return;
        }

        if (size > avail) //must shrink to fit in the slot
        {
            newSize = Math.Max(minSize, avail);
            newPos = nearFarEdge ? hi - newSize : lo; //anchor to whichever screen edge the chat is nearest
        }
        else //fits, just push into the slot
        {
            newSize = size;
            newPos = Math.Clamp(pos, lo, hi - size);
        }
    }

    private void SavePosition()
    {
        HasOffset = true;
        var cx = ChaosGame.UiWidth / 2;
        OffsetX = X - cx;
        OffsetY = Y + Height / 2 < ChaosGame.UiHeight / 2 ? Y : Y - ChaosGame.UiHeight;
        ClientSettings.ChatWindowOffsetX = OffsetX;
        ClientSettings.ChatWindowOffsetY = OffsetY;
        ClientSettings.ChatWindowWidth = Width;
        ClientSettings.ChatWindowHeight = Height;
        ClientSettings.Save();
    }

    public void CommitPosition() => SavePosition();

    //pops the tab menu under the right-clicked tab (clamped inside the content). "Leave" shows only for custom channels.
    private void ShowTabMenu(ChatChannel? channel, string? channelName, int screenX, int screenY)
    {
        TabMenuChannel = channelName;
        TabMenuKey = ChatTabBar.TabKey(channel, channelName);
        TabMenuTitle.Text = channelName ?? channel?.ToString() ?? "All";
        TabMenuHighlight.SetChecked(!ClientSettings.IsChatTabMuted(TabMenuKey));

        var isChannel = channelName is { Length: > 0 };
        TabMenuSetColor.Visible = isChannel;
        TabMenuLeave.Visible = isChannel;
        TabMenu.Height = isChannel ? 94 : 46; //title + highlight (+ set color + leave for channels)

        var lx = screenX - Content.ScreenX;
        var ly = screenY - Content.ScreenY;
        TabMenu.X = Math.Clamp(lx, 0, Math.Max(0, Content.Width - TabMenu.Width));
        TabMenu.Y = Math.Clamp(ly, 0, Math.Max(0, Content.Height - TabMenu.Height));
        TabMenu.Visible = true;
        BringToFront();
    }

    private void HideTabMenu()
    {
        TabMenu.Visible = false;
        TabMenuChannel = null;
    }

    //the colour submenu, opened from the tab menu's "Set color", positioned where the tab menu was
    private void ShowColorMenu(string channel, int localX, int localY)
    {
        TabColorChannel = channel;
        TabColorMenu.X = Math.Clamp(localX, 0, Math.Max(0, Content.Width - TabColorMenu.Width));
        TabColorMenu.Y = Math.Clamp(localY, 0, Math.Max(0, Content.Height - TabColorMenu.Height));
        TabColorMenu.Visible = true;
        BringToFront();
    }

    private void HideColorMenu()
    {
        TabColorMenu.Visible = false;
        TabColorChannel = null;
    }

    //closes a popup menu on a press outside it (or Escape), skipping the frame it opened. Returns the menu's visibility.
    private static bool DismissMenuOnOutsideClick(UIPanel menu, bool wasVisible, Action hide)
    {
        if (!menu.Visible)
            return false;

        if (wasVisible)
        {
            if (InputBuffer.WasKeyPressed(Microsoft.Xna.Framework.Input.Keys.Escape))
                hide();
            else
                foreach (var evt in InputBuffer.Events)
                    if (evt is { Kind: BufferedInputKind.MouseButton, IsPress: true })
                    {
                        if (!menu.ContainsPoint(evt.X, evt.Y))
                            hide();

                        break;
                    }
        }

        return menu.Visible;
    }

    //event-driven (no hover-to-show): shown while typing, while pinned, during the linger, or always when the timer is 0
    protected override float ComputeFadeTarget()
        => Pinned || ChatInput.IsFocused || (ClientSettings.ChatWindowFadeSeconds <= 0) || (ShowTimer > 0f) ? 1f : 0f;

    //clicking the chat while typing (anywhere but the input line) stops typing so the click lands on what was clicked.
    //EXCEPT the tab strip: clicking a tab keeps you typing and just switches the input to that tab's channel (above).
    public override void OnMouseDown(MouseDownEvent e)
    {
        if (ChatInput.IsFocused
            && !ChatInput.ContainsPoint(e.ScreenX, e.ScreenY)
            && !Tabs.ContainsPoint(e.ScreenX, e.ScreenY))
            ChatInput.Unfocus();

        base.OnMouseDown(e);
    }

    public string? NameAt(int screenX, int screenY) => Chat.NameAt(screenX, screenY);

    public void RefreshChatTimestamps() => Chat.RefreshTimestamps();

    protected override void OnResized()
    {
        LayoutContents();

        //save on user resize; suppressed during the offset-restore Resize() call and during ClampToScreen shrinks
        if (HasOffset && !SuppressResizeSave)
            SavePosition();
    }

    //fade IN fast when a new line flashes the window up, but fade OUT slowly so the disappearance (and the glow handoff)
    //is a smooth, visible transition rather than a near-instant snap.
    protected override float AutoFadeOutSeconds => 0.9f;

    //fade the chat text and scrollbar along with the chrome (the base only fades the frame). The tab strip fades too,
    //except a tab that is currently pulsing to flag unread messages; that stays visible (handled inside ChatTabBar).
    protected override void OnFade(float opacity)
    {
        Chat.SetContentOpacity(opacity);
        Tabs.SetChromeOpacity(opacity);
    }
}
