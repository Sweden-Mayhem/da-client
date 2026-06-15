#region
using Chaos.Client.Collections;
using Chaos.Client.Controls.Components;
using Chaos.Client.Systems;
using Chaos.Client.ViewModel;
using Microsoft.Xna.Framework;
#endregion

namespace Chaos.Client.Controls.World.Menu;

/// <summary>
///     Tab strip for the chat window. "All" shows every channel; the rest filter the chat to a single channel. The
///     Group and Guild tabs appear only while the player is in a group / guild. Notification behaviour: while viewing
///     "All" no tab ever flags itself (All already shows everything); while viewing a specific tab, a message that
///     lands on a DIFFERENT tab makes that other tab pulse a few times and then settle, so the player notices the
///     unread channel without leaving the tab they are on.
/// </summary>
public sealed class ChatTabBar : UIPanel
{
    private const int FONT = 13;
    private const int PAD_X = 6;
    private const int GAP = 3;
    private const float PULSE_DURATION_MS = 2400f; //roughly three slow pulses
    private const int PULSE_CYCLES = 3;

    private static readonly Color ActiveColor = new(234, 214, 156);
    private static readonly Color InactiveColor = new(120, 110, 86);
    private static readonly Color HoverColor = new(190, 176, 132);
    private static readonly Color PulseColor = new(255, 178, 84);
    private static readonly Color ActiveBg = new Color(48, 40, 26) * 0.95f;

    /// <summary>Raised when the player picks a tab: the built-in channel (or null for All / a custom channel) plus the
    ///     custom "!channel" name when a channel tab is picked (null otherwise).</summary>
    public event Action<ChatChannel?, string?>? TabSelected;

    /// <summary>Raised when the "+" button is clicked, so the host opens a "join channel" prompt.</summary>
    public event Action? AddChannelRequested;

    /// <summary>Raised when ANY tab is right-clicked: its (channel, "!name") identity and the screen x,y, so the host can
    ///     pop a context menu (Highlight toggle for all tabs; Leave for custom channels) there.</summary>
    public event Action<ChatChannel?, string?, int, int>? TabContext;

    /// <summary>Stable per-tab key for persisting per-tab settings (the Highlight mute): a custom "!channel" name, else the
    ///     built-in channel's enum name, else "All".</summary>
    public static string TabKey(ChatChannel? channel, string? channelName) => channelName ?? channel?.ToString() ?? "All";

    /// <summary>The server's group/guild chat run over the channel system internally (real names "!group-{id}" /
    ///     "!guild-{name}-{id}"), but they make available the override names "!group" / "!guild" in their join/leave notices.
    ///     Those are NOT user channels (they have dedicated built-in tabs), so map them to the built-in channel and never
    ///     spin up a custom tab for them. Returns the built-in channel for a reserved name, or null for a real custom channel.</summary>
    private static ChatChannel? ReservedChannel(string? channelName)
    {
        if (string.Equals(channelName, "!group", StringComparison.OrdinalIgnoreCase))
            return ChatChannel.Group;

        if (string.Equals(channelName, "!guild", StringComparison.OrdinalIgnoreCase))
            return ChatChannel.Guild;

        return null;
    }

    /// <summary>The built-in channel the player is currently viewing; null = the "All" tab OR a custom channel.</summary>
    public ChatChannel? ActiveChannel => Active;

    /// <summary>The custom "!channel" name the player is currently viewing, or null for a built-in/All tab.</summary>
    public string? ActiveChannelName => ActiveName;

    private readonly List<ChatTab> Tabs = [];
    private ChatChannel? Active; //null = All or a custom channel (disambiguated by ActiveName)
    private string? ActiveName; //the custom channel name when a channel tab is active
    private float ChromeOpacity = 1f; //follows the host window's fade
    private bool SeenGuildMessage; //reveal the Guild tab once a guild line arrives, even before the profile is fetched
    private bool LastGroupVisible;
    private bool LastGuildVisible;

    //horizontal scroll for when the present tabs don't all fit: ScrollIndex is the first present tab shown. The </> arrows
    //appear only on overflow; "+" is always at the far right and opens the join-channel prompt.
    private int ScrollIndex;
    private readonly ChatTab LeftArrow;
    private readonly ChatTab RightArrow;
    private readonly ChatTab PlusButton;

    public ChatTabBar(int width, int height)
    {
        Name = "ChatTabBar";
        Width = width;
        Height = height;
        IsPassThrough = true; //empty space between tabs falls through, the tab buttons themselves take the clicks

        AddTab("All", null);
        AddTab("Public", ChatChannel.Public);
        AddTab("Whisper", ChatChannel.Whisper);
        AddTab("System", ChatChannel.System);
        AddTab("Group", ChatChannel.Group);
        AddTab("Guild", ChatChannel.Guild);

        //group/guild start hidden until the player is in one (Present, not Visible, so the scroll layout owns Visible)
        GetTab(ChatChannel.Group)!.Present = false;
        GetTab(ChatChannel.Guild)!.Present = false;

        //overflow scroll arrows (shown only when the tabs don't fit) + a "+" to join a channel (always at the far right)
        LeftArrow = MakeButton("<", () =>
        {
            if (ScrollIndex > 0)
            {
                ScrollIndex--;
                Relayout();
            }
        }, "Scroll tabs\nShow earlier chat tabs that don't fit.");

        RightArrow = MakeButton(">", () =>
        {
            if (CanScrollRight())
            {
                ScrollIndex++;
                Relayout();
            }
        }, "Scroll tabs\nShow more chat tabs that don't fit.");

        PlusButton = MakeButton("+", () => AddChannelRequested?.Invoke(),
            "Join a channel\nJoin or create a custom chat channel to talk with a specific group of players.");

        SelectChannel(null, null, raise: false);
        Relayout();

        WorldState.Chat.MessageAdded += OnMessage;
    }

    private void AddTab(string label, ChatChannel? channel, string? channelName = null)
    {
        var tab = new ChatTab(label, channel, channelName, FONT, Height);
        tab.Activated += () => SelectChannel(channel, channelName, raise: true);
        tab.Tooltip = TabTooltip(channel, channelName);

        //right-click ANY tab pops a context menu (Highlight toggle for all, Leave for custom channels)
        tab.RightClicked += (sx, sy) => TabContext?.Invoke(channel, channelName, sx, sy);

        Tabs.Add(tab);
        AddChild(tab);
    }

    //a non-tab clickable button (the </> scroll arrows and the "+" join button). Reuses ChatTab's box/hover/click look but
    //is NOT in the Tabs list, so it never takes part in selection or filtering.
    private ChatTab MakeButton(string label, Action onClick, string? tooltip = null)
    {
        var btn = new ChatTab(label, null, null, FONT, Height);
        btn.Activated += onClick;
        btn.Tooltip = tooltip;
        AddChild(btn);

        return btn;
    }

    //hover help for each chat tab. Right-clicking any tab opens a context menu (mute, colour, leave), so that hint is shared.
    private static string TabTooltip(ChatChannel? channel, string? channelName)
    {
        const string rightClick = "\n\nRight-click for options.";

        if (channelName is not null)
            return $"{channelName} channel\nA custom chat channel you have joined. Messages here are seen by everyone in the channel.{rightClick}";

        return channel switch
        {
            null                 => $"All\nEvery channel combined - the full chat log.{rightClick}",
            ChatChannel.Public   => $"Public\nLocal say and shout, plus general gameplay notices.{rightClick}",
            ChatChannel.Whisper  => $"Whisper\nPrivate messages to and from individual players.{rightClick}",
            ChatChannel.System   => $"System\nServer and system notices.{rightClick}",
            ChatChannel.Group    => $"Group\nChat shared with your party members.{rightClick}",
            ChatChannel.Guild    => $"Guild\nChat shared with your guild.{rightClick}",
            _                    => $"Chat{rightClick}"
        };
    }

    //true while the rightmost present tab is scrolled out of view (so ">" has somewhere to go)
    private bool CanScrollRight()
    {
        ChatTab? last = null;

        foreach (var tab in Tabs)
            if (tab.Present)
                last = tab;

        return (last is not null) && !last.Visible;
    }

    //shows the tab for a custom "!channel" (created on first use, label = the name without the "!"), re-reveals it if it
    //had been hidden by a leave.
    private ChatTab EnsureChannelTab(string channelName)
    {
        //"!group"/"!guild" are not custom channels, hand back the built-in tab so they can never leak in as a custom one
        if (ReservedChannel(channelName) is { } reserved)
            return GetTab(reserved)!;

        if (GetTab(null, channelName) is { } existing)
        {
            if (!existing.Present)
            {
                existing.Present = true;
                Relayout();
            }

            return existing;
        }

        AddTab(channelName.TrimStart('!'), null, channelName);
        Relayout();

        return GetTab(null, channelName)!;
    }

    //hides a channel tab when the player leaves it (kept around so a rejoin re-reveals it). Falls back to All if it was
    //the tab being viewed.
    private void RemoveChannelTab(string channelName)
    {
        if (GetTab(null, channelName) is not { } tab)
            return;

        tab.Present = false;

        if (ActiveName == channelName)
            SelectChannel(null, null, raise: true);

        Relayout();
    }

    private ChatTab? GetTab(ChatChannel? channel) => GetTab(channel, null);

    private ChatTab? GetTab(ChatChannel? channel, string? channelName)
    {
        foreach (var tab in Tabs)
            if ((tab.Channel == channel) && (tab.ChannelName == channelName))
                return tab;

        return null;
    }

    /// <summary>Fades the strip with the host window's chrome. A pulsing tab overrides this so notifications still show.</summary>
    public void SetChromeOpacity(float opacity) => ChromeOpacity = opacity;

    /// <summary>Moves the selection to the next (+1) / previous (-1) present tab, wrapping around, and scrolls it into view.
    ///     Raises TabSelected so the chat filter and the input destination follow. Used by Tab / Shift+Tab while typing.</summary>
    public void CycleTab(int direction)
    {
        var present = Tabs.Where(t => t.Present)
                          .ToList();

        if (present.Count == 0)
            return;

        var current = present.FindIndex(t => (t.Channel == Active) && (t.ChannelName == ActiveName));

        if (current < 0)
            current = 0;

        var next = (((current + direction) % present.Count) + present.Count) % present.Count;
        var tab = present[next];

        SelectChannel(tab.Channel, tab.ChannelName, raise: true);

        //make sure the newly-selected tab is on-screen (the scroll window starts at ScrollIndex)
        ScrollIndex = next;
        Relayout();
    }

    private void SelectChannel(ChatChannel? channel, string? channelName, bool raise)
    {
        Active = channel;
        ActiveName = channelName;

        foreach (var tab in Tabs)
        {
            var isActive = (tab.Channel == channel) && (tab.ChannelName == channelName);
            tab.SetActive(isActive);

            if (isActive)
                tab.StopPulse(); //the tab you switch to is, by definition, caught up
        }

        if (raise)
            TabSelected?.Invoke(channel, channelName);
    }

    private void OnMessage(Chat.ChatMessage msg)
    {
        if (msg.Channel == ChatChannel.Guild)
            SeenGuildMessage = true;

        //make sure a freshly-relevant Group/Guild tab exists before we try to pulse it
        UpdateDynamicTabs();

        //a "joined/left channel !x" system notice creates/hides that channel's tab even before any chat flows in it. It is
        //tab plumbing, NOT chat, so it must NOT pulse a tab (it would otherwise flag System just for joining a channel).
        if ((msg.ChannelName is null) && TryParseChannelNotice(msg.Text, out var noticeName, out var leftChannel))
        {
            //the group/guild built-in channels fire the same "joined channel !group"/"!guild" notice when you form a
            //group/guild (they reuse the channel system). They already have dedicated tabs, so don't create a stray
            //empty custom one (the built-in Group/Guild tab is shown by UpdateDynamicTabs while you're in one)
            if (ReservedChannel(noticeName) is null)
            {
                if (leftChannel)
                    RemoveChannelTab(noticeName);
                else
                    EnsureChannelTab(noticeName);
            }

            return;
        }

        //a custom-channel message: ensure its tab exists. Its identity is (null channel, the "!name"). A reserved
        //group/guild "!name" never becomes a custom tab (its messages arrive as Group/GuildChat on the built-in tabs).
        if ((msg.ChannelName is { Length: > 0 } cname) && (ReservedChannel(cname) is null))
            EnsureChannelTab(cname);

        var msgChannel = msg.ChannelName is null ? msg.Channel : (ChatChannel?)null;
        var msgName = msg.ChannelName;

        //no notifications while on All (it already shows everything), and none for the tab you are already viewing
        if ((Active is null) && (ActiveName is null))
            return;

        if ((Active == msgChannel) && (ActiveName == msgName))
            return;

        //a tab whose Highlight was turned off (right-click menu, persisted) never pulses/flags on a new message
        if (ClientSettings.IsChatTabMuted(TabKey(msgChannel, msgName)))
            return;

        if (GetTab(msgChannel, msgName) is { Present: true } tab)
            tab.StartPulse(PULSE_DURATION_MS);
    }

    //recognizes the server's channel join/leave notices ("You have joined channel !x", "You are already in channel !x",
    //"You have left channel !x") and pulls out the "!x" name. Ignores "Channel !x not found" and unrelated lines
    private static bool TryParseChannelNotice(string text, out string channelName, out bool left)
    {
        channelName = string.Empty;
        left = false;

        var join = text.Contains("joined channel !", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("already in channel !", StringComparison.OrdinalIgnoreCase);
        left = text.Contains("left channel !", StringComparison.OrdinalIgnoreCase);

        if (!join && !left)
            return false;

        var marker = "channel !";
        var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);

        if (idx < 0)
            return false;

        var rest = text[(idx + "channel ".Length)..]; //starts at the "!"
        var space = rest.IndexOf(' ');
        channelName = (space >= 0 ? rest[..space] : rest).Trim();

        return channelName.Length > 1;
    }

    private void UpdateDynamicTabs()
    {
        var groupVisible = WorldState.Group.InGroup;
        var guildVisible = SeenGuildMessage || !string.IsNullOrEmpty(WorldState.GuildName);

        if ((groupVisible == LastGroupVisible) && (guildVisible == LastGuildVisible))
            return;

        LastGroupVisible = groupVisible;
        LastGuildVisible = guildVisible;

        GetTab(ChatChannel.Group)!.Present = groupVisible;
        GetTab(ChatChannel.Guild)!.Present = guildVisible;

        //if the tab we were viewing just disappeared, fall back to All
        if (((Active == ChatChannel.Group) && !groupVisible) || ((Active == ChatChannel.Guild) && !guildVisible))
            SelectChannel(null, null, raise: true);

        Relayout();
    }

    private void Relayout()
    {
        //the present tabs (built-in shown + joined channels), in order
        var present = new List<ChatTab>();

        foreach (var tab in Tabs)
        {
            tab.Visible = false; //the scroll window below re-reveals the ones that fit

            if (tab.Present)
                present.Add(tab);
        }

        var plusX = Math.Max(0, Width - PlusButton.Width);

        //do all present tabs fit without scroll arrows?
        var totalW = 0;

        foreach (var tab in present)
            totalW += tab.Width + GAP;

        if (totalW > 0)
            totalW -= GAP;

        var overflow = totalW > plusX;

        var regionStart = overflow ? LeftArrow.Width + GAP : 0;
        var regionEnd = overflow ? plusX - RightArrow.Width - GAP : plusX;

        if (!overflow)
            ScrollIndex = 0; //everything fits, never leave tabs scrolled off

        ScrollIndex = Math.Clamp(ScrollIndex, 0, Math.Max(0, present.Count - 1));

        //lay out present tabs from ScrollIndex, left to right, until the region is full (the first one always shows)
        var x = regionStart;
        var stopped = false;

        for (var i = 0; i < present.Count; i++)
        {
            if ((i < ScrollIndex) || stopped)
                continue;

            var tab = present[i];

            if ((i > ScrollIndex) && (x + tab.Width > regionEnd))
            {
                stopped = true;

                continue;
            }

            tab.X = x;
            tab.Y = 0;
            tab.Visible = true;
            x += tab.Width + GAP;
        }

        //arrows (only on overflow) flank the scroll region; "+" is pinned to the far right
        PlusButton.X = plusX;
        PlusButton.Y = 0;
        PlusButton.Visible = true;

        LeftArrow.Visible = overflow;
        RightArrow.Visible = overflow;

        if (overflow)
        {
            LeftArrow.X = 0;
            LeftArrow.Y = 0;
            RightArrow.X = plusX - RightArrow.Width;
            RightArrow.Y = 0;
        }
    }

    public override void Update(GameTime gameTime)
    {
        UpdateDynamicTabs();
        Relayout(); //re-run every frame so a window resize (Width change) reflows the strip

        var dt = (float)gameTime.ElapsedGameTime.TotalMilliseconds;

        foreach (var tab in Tabs)
            tab.Tick(dt, ChromeOpacity);

        LeftArrow.Tick(dt, ChromeOpacity);
        RightArrow.Tick(dt, ChromeOpacity);
        PlusButton.Tick(dt, ChromeOpacity);

        base.Update(gameTime);
    }

    public override void Dispose()
    {
        WorldState.Chat.MessageAdded -= OnMessage;

        base.Dispose();
    }

    /// <summary>One clickable tab: a text label in a box that lights up when active and pulses to flag unread messages.</summary>
    private sealed class ChatTab : UIPanel
    {
        public ChatChannel? Channel { get; }

        /// <summary>The custom "!channel" name this tab represents, or null for a built-in/All tab.</summary>
        public string? ChannelName { get; }

        /// <summary>Whether this tab is part of the strip at all (built-in shown, or a joined channel). The scroll layout
        ///     then decides which present tabs are actually on-screen (<see cref="UIElement.Visible" />).</summary>
        public bool Present { get; set; } = true;

        private readonly UILabel Label;
        private bool IsActive;
        private bool Hovered;
        private float PulseMs;

        public event Action? Activated;
        public event Action<int, int>? RightClicked; //screen x,y of the right-click

        public ChatTab(string text, ChatChannel? channel, string? channelName, int font, int height)
        {
            Channel = channel;
            ChannelName = channelName;
            Height = height;
            Width = MeasureLabel(text, font) + PAD_X * 2;

            Label = new UILabel
            {
                Text = text,
                X = 0,
                Y = 0,
                Width = Width,
                Height = height,
                CustomFontSize = font,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ForegroundColor = InactiveColor,
                IsHitTestVisible = false
            };

            AddChild(Label);
        }

        private static int MeasureLabel(string text, int font)
            => TtfTextRenderer.Available ? TtfTextRenderer.MeasureWidth(text, font) : TextRenderer.MeasureWidth(text);

        public void SetActive(bool active)
        {
            IsActive = active;
            BackgroundColor = active ? ActiveBg : null;
            Label.ForegroundColor = active ? ActiveColor : InactiveColor;
        }

        public void StartPulse(float ms) => PulseMs = ms;

        public void StopPulse() => PulseMs = 0f;

        public void Tick(float dt, float chromeOpacity)
        {
            if (PulseMs > 0f)
            {
                PulseMs -= dt;

                if (PulseMs < 0f)
                    PulseMs = 0f;

                //0 -> 1 -> 0 each cycle; ends back at the dim colour after PULSE_CYCLES, so the pulse "fades out"
                var elapsed = PULSE_DURATION_MS - PulseMs;
                var glow = 0.5f - 0.5f * MathF.Cos(elapsed / PULSE_DURATION_MS * PULSE_CYCLES * MathF.Tau);

                Label.Opacity = 1f; //a pulsing tab stays fully visible even while the rest of the window has faded
                Label.ForegroundColor = Color.Lerp(InactiveColor, PulseColor, glow);
                BackgroundColor = null;

                return;
            }

            //not pulsing: every tab (including the selected one) fades out with the window chrome
            //the player knows which tab they are on, so no reason to keep it lit once the window has faded away
            //only a pulsing tab (handled above) overrides this to stay visible as an unread flag
            Label.Opacity = chromeOpacity;
            Label.ForegroundColor = IsActive ? ActiveColor : (Hovered ? HoverColor : InactiveColor);
            BackgroundColor = IsActive ? ActiveBg * chromeOpacity : null;
        }

        public override void OnMouseEnter() => Hovered = true;

        public override void OnMouseLeave() => Hovered = false;

        public override void OnClick(ClickEvent e)
        {
            if (e.Button == MouseButton.Right)
                RightClicked?.Invoke(e.ScreenX, e.ScreenY);
            else
                Activated?.Invoke();

            e.Handled = true;
        }
    }
}
