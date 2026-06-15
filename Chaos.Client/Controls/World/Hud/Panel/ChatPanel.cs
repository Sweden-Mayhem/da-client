#region
using Chaos.Client.Collections;
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.Generic;
using Chaos.Client.Systems;
using Chaos.Client.ViewModel;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.Hud.Panel;

/// <summary>
///     Chat display panel (F key) showing word-wrapped message history
/// </summary>
public sealed class ChatPanel : ExpandablePanel
{
    private const int MAX_CHAT_LINES = 200;
    private const int GLYPH_HEIGHT = 12;

    //each line draws a 1px outline plus a drop shadow this many pixels down
    private const int CHAT_LINE_SHADOW_Y = 3;

    //extra height per line so a descender's drop shadow isn't clipped
    private const int CHAT_LINE_PAD = 4;
    private readonly List<ChatLine> ChatLog = [];
    private readonly Rectangle NormalDisplayBounds;
    private readonly int PanelOriginX;
    private readonly int PanelOriginY;
    private readonly ScrollBarControl ScrollBar;

    //0 = retail bitmap font, >0 = render lines with the TrueType font at this size
    //mutable so the chat window can rescale the log live from the font size slider
    private int CustomFontSize;
    private int LineHeight;

    //un-wrapped messages kept so the log can be re-wrapped on resize or tab change
    private readonly List<ChatLine> RawMessages = [];
    private readonly bool IncludeOrangeBar;

    //active tab filter, null = show every channel
    private ChatChannel? ActiveChannel;
    private string? ActiveChannelName; //when set, the active tab is a custom channel filtered by name

    private Rectangle DisplayBounds;
    private Rectangle ExpandedDisplayBounds;
    private UILabel[] Lines;

    //opacity of the host window chrome, 1 = shown, 0 = faded out
    //a line shows at max(window opacity, its own recency) so recent lines stay readable while the chrome fades
    private float WindowOpacity = 1f;

    //post time in game seconds of the line in each slot, drives the per-line age fade
    private double[] LineTimes;
    private double NowSeconds;

    private int LogVersion;
    private int MaxVisibleLines;
    private int RenderedVersion = -1;
    private int ScrollOffset;

    public ChatPanel(Rectangle displayBounds, Rectangle panelBounds, int customFontSize = 0, bool drawBackground = true, bool includeOrangeBar = false)
    {
        Name = "Chat";
        NormalDisplayBounds = displayBounds;
        DisplayBounds = displayBounds;
        PanelOriginX = panelBounds.X;
        PanelOriginY = panelBounds.Y;

        CustomFontSize = customFontSize;
        LineHeight = (customFontSize > 0) && TtfTextRenderer.Available ? TtfTextRenderer.LineHeight(customFontSize) + CHAT_LINE_PAD : GLYPH_HEIGHT;

        if (drawBackground)
            Background = UiRenderer.Instance!.GetSpfTexture("_nchatbk.spf");

        MaxVisibleLines = displayBounds.Height > 0 ? displayBounds.Height / LineHeight : 0;
        Lines = new UILabel[MaxVisibleLines];
        LineTimes = new double[MaxVisibleLines];

        var relX = displayBounds.X - panelBounds.X;

        for (var i = 0; i < MaxVisibleLines; i++)
        {
            Lines[i] = new UILabel
            {
                Name = $"ChatLine{i}",
                X = relX,
                Width = displayBounds.Width - ScrollBarControl.DEFAULT_WIDTH,
                Height = LineHeight,
                CustomFontSize = customFontSize,
                PaddingLeft = 0,
                PaddingTop = 0,
                ShadowStyle = ShadowStyle.BottomRight,
                ShadowOffset = new Point(0, CHAT_LINE_SHADOW_Y)
            };

            AddChild(Lines[i]);
        }

        RepositionLabels();

        //position relative to panel origin
        var relY = displayBounds.Y - panelBounds.Y;

        ScrollBar = new ScrollBarControl
        {
            X = relX + displayBounds.Width - ScrollBarControl.DEFAULT_WIDTH,
            Y = relY,
            Height = displayBounds.Height
        };

        ScrollBar.OnValueChanged += v =>
        {
            ScrollOffset = ScrollBar.MaxValue - v;
            LogVersion++;
        };

        AddChild(ScrollBar);
        WorldState.Chat.MessageAdded += OnMessageAdded;

        if (includeOrangeBar)
        {
            IncludeOrangeBar = true;
            WorldState.Chat.OrangeBarMessageAdded += OnOrangeBarMessageAdded;
        }
    }

    private void OnOrangeBarMessageAdded(Chat.OrangeBarMessage msg) => AddMessage(msg.Text, msg.Color, ChatChannel.System);

    //friend presence lines put the name first, like "Name has come online"
    private static readonly string[] FriendPresenceMarkers = [" has come online", " has gone offline"];
    private const string FriendsOnlinePrefix = "Friends online: ";

    private readonly List<(string Name, int Start)> NameSpans = [];

    /// <summary>
    ///     Returns the clickable player name at the given screen point in the chat log, or null
    ///     Used for click-to-whisper and the hover hand cursor
    /// </summary>
    public string? NameAt(int screenX, int screenY)
    {
        if ((CustomFontSize <= 0) || !TtfTextRenderer.Available)
            return null;

        foreach (var label in Lines)
        {
            //ignore empty rows and faded ones since their names aren't really visible to click
            if (label is null || string.IsNullOrEmpty(label.Text) || (label.Opacity < 0.3f))
                continue;

            if ((screenY < label.ScreenY) || (screenY >= label.ScreenY + LineHeight))
                continue;

            CollectNameSpans(label.Text, NameSpans);

            foreach (var (name, start) in NameSpans)
            {
                //offset the hit area by the width of any prefix text before the name
                var prefixWidth = start > 0 ? TtfTextRenderer.MeasureWidth(label.Text[..start], CustomFontSize) : 0;
                var nameWidth = TtfTextRenderer.MeasureWidth(name, CustomFontSize);
                var nameLeft = label.ScreenX + prefixWidth;

                if ((screenX >= nameLeft) && (screenX <= nameLeft + nameWidth + 2))
                    return name;
            }

            return null; //only one line can be on this y
        }

        return null;
    }

    //finds every clickable player name in a chat line, each with where it starts
    private static void CollectNameSpans(string text, List<(string Name, int Start)> spans)
    {
        spans.Clear();

        if (text.Length == 0)
            return;

        //friend came online or went offline, the name is the leading token before the marker
        foreach (var marker in FriendPresenceMarkers)
        {
            var at = text.IndexOf(marker, StringComparison.Ordinal);

            if (at > 0)
            {
                if (IsPlayerName(text[..at]))
                    spans.Add((text[..at], 0));

                return;
            }
        }

        //in the friends-online list every comma separated name is clickable
        if (text.StartsWith(FriendsOnlinePrefix, StringComparison.Ordinal))
        {
            var i = FriendsOnlinePrefix.Length;

            while (i < text.Length)
            {
                while ((i < text.Length) && !char.IsLetter(text[i]))
                    i++;

                var start = i;

                while ((i < text.Length) && char.IsLetter(text[i]))
                    i++;

                if (i > start)
                    spans.Add((text[start..i], start));
            }

            return;
        }

        //everything else has at most one name
        if (ParseChatName(text) is { } single)
            spans.Add(single);
    }

    //finds the clickable player name in a chat line plus where it starts
    //public and yell put the name at the start, whisper wraps it in brackets, channel tags have a space after the tag
    private static (string Name, int Start)? ParseChatName(string text)
    {
        if (text.Length == 0)
            return null;

        //a whisper puts the name in brackets followed right away by ':' or '>'
        //a channel tag has a space after the ']' instead, so skip the tag and read the real name after it
        if (text[0] == '[')
        {
            var close = text.IndexOf(']');

            if ((close > 1) && (close + 1 < text.Length))
            {
                var after = text[close + 1];

                if (after is ':' or '>')
                    return IsPlayerName(text[1..close]) ? (text[1..close], 1) : null;

                if (after == ' ')
                {
                    var start = close + 1;

                    while ((start < text.Length) && (text[start] == ' '))
                        start++;

                    return ParseLeadingName(text, start);
                }
            }

            return null;
        }

        return ParseLeadingName(text, 0);
    }

    //reads a leading name beginning at offset start, returned with its absolute start index
    private static (string Name, int Start)? ParseLeadingName(string text, int start)
    {
        var sep = text.IndexOfAny([':', '!'], start) - start;

        if (sep is <= 0 or > 24)
            return null;

        var name = text.Substring(start, sep);

        return IsPlayerName(name) ? (name, start) : null;
    }

    //a single token of letters, short enough to be a name
    private static bool IsPlayerName(string token)
    {
        if (token.Length is 0 or > 24)
            return false;

        foreach (var c in token)
            if (!char.IsLetter(c))
                return false;

        return true;
    }

    /// <summary>
    ///     Switches the visible tab, pass null for every channel
    /// </summary>
    public void SetActiveChannel(ChatChannel? channel, string? channelName = null)
    {
        if ((ActiveChannel == channel) && (ActiveChannelName == channelName))
            return;

        ActiveChannel = channel;
        ActiveChannelName = channelName;
        RewrapAll();
        ScrollToBottom();
    }

    //both null shows everything, a custom-channel tab shows only that channel
    //a built-in tab shows its channel but not custom-channel messages, so channel chat never leaks into Public
    private bool PassesFilter(ChatChannel channel, string? channelName)
    {
        if (ActiveChannel is null && ActiveChannelName is null)
            return true;

        if (ActiveChannelName is not null)
            return channelName == ActiveChannelName;

        return (channel == ActiveChannel) && (channelName is null);
    }

    private void AddMessage(string text, Color color, ChatChannel channel, string? channelName = null)
    {
        //drop blank messages so a stray newline or an empty send never leaves a gap in the log
        if (string.IsNullOrWhiteSpace(text))
            return;

        var time = NowSeconds;
        RawMessages.Add(new ChatLine(text, color, channel, time, channelName));

        if (RawMessages.Count > MAX_CHAT_LINES)
            RawMessages.RemoveRange(0, RawMessages.Count - MAX_CHAT_LINES);

        //messages on other channels stay in RawMessages so a tab switch can reveal them, but aren't shown now
        if (!PassesFilter(channel, channelName))
            return;

        WrapInto(text, color, time);
        AfterLogChanged();
    }

    //word-wraps one message to the current width and appends the lines to the display log
    //each wrapped line keeps the source post time so the age fade treats a multi-line message as one unit
    private void WrapInto(string text, Color color, double time)
    {
        var maxWidth = DisplayBounds.Width - ScrollBarControl.DEFAULT_WIDTH;

        if (maxWidth <= 0)
            return;

        if (CustomFontSize > 0)
        {
            //TrueType path wraps by measured width in the custom font, skipping any blank wrapped line
            foreach (var line in TtfTextRenderer.WrapText(text, maxWidth, CustomFontSize))
                if (!string.IsNullOrWhiteSpace(line))
                    ChatLog.Add(new ChatLine(line, color, ChatChannel.Public, time));
        } else
        {
            var remaining = text;

            while (remaining.Length > 0)
            {
                var lineEnd = TextRenderer.FindLineBreak(remaining, maxWidth);

                var line = remaining[..lineEnd]
                    .TrimEnd();

                remaining = remaining[lineEnd..]
                    .TrimStart();

                if (!string.IsNullOrWhiteSpace(line))
                    ChatLog.Add(new ChatLine(line, color, ChatChannel.Public, time));
            }
        }
    }

    private void AfterLogChanged()
    {
        if (ChatLog.Count > MAX_CHAT_LINES)
            ChatLog.RemoveRange(0, ChatLog.Count - MAX_CHAT_LINES);

        var wasAtBottom = ScrollOffset == 0;

        ScrollBar.TotalItems = ChatLog.Count;
        ScrollBar.VisibleItems = MaxVisibleLines;
        ScrollBar.MaxValue = Math.Max(0, ChatLog.Count - MaxVisibleLines);

        if (wasAtBottom)
        {
            ScrollOffset = 0;
            ScrollBar.Value = ScrollBar.MaxValue;
        } else
            ScrollBar.Value = ScrollBar.MaxValue - ScrollOffset;

        LogVersion++;
    }

    //re-wraps the whole stored history to the current width, called after a resize
    private void RewrapAll()
    {
        ChatLog.Clear();

        foreach (var msg in RawMessages)
            if (PassesFilter(msg.Channel, msg.ChannelName))
                WrapInto(msg.Text, msg.Color, msg.Time);

        ScrollOffset = 0;
        AfterLogChanged();
    }

    /// <summary>Tracks the host window's chrome opacity and fades the scrollbar with it
    ///     Recent lines stay readable while the window is faded, older lines fade out with it</summary>
    public void SetContentOpacity(float opacity)
    {
        WindowOpacity = opacity;
        ScrollBar.Opacity = opacity;
    }

    /// <summary>
    ///     Sets up expand support for the large HUD chat panel
    /// </summary>
    public void ConfigureExpand(Texture2D? expandedBackground, Rectangle expandedBounds, Rectangle panelBounds)
    {
        ExpandedDisplayBounds = expandedBounds;

        //clear the normal background so the expand offset comes from panel height, not texture height
        Background = null;
        Height = panelBounds.Height;

        ConfigureExpand(expandedBackground);

        //create the extra labels the expanded line count needs
        var expandedMaxLines = expandedBounds.Height / LineHeight;

        if (expandedMaxLines > Lines.Length)
        {
            var relX = NormalDisplayBounds.X - PanelOriginX;
            var relY = NormalDisplayBounds.Y - PanelOriginY;
            var oldCount = Lines.Length;
            Array.Resize(ref Lines, expandedMaxLines);
            Array.Resize(ref LineTimes, expandedMaxLines);

            for (var i = oldCount; i < expandedMaxLines; i++)
            {
                Lines[i] = new UILabel
                {
                    Name = $"ChatLine{i}",
                    X = relX,
                    Y = relY + NormalDisplayBounds.Height - (MaxVisibleLines - i) * LineHeight,
                    Width = NormalDisplayBounds.Width - ScrollBarControl.DEFAULT_WIDTH,
                    Height = LineHeight,
                    CustomFontSize = CustomFontSize,
                    PaddingLeft = 0,
                    PaddingTop = 0,
                    Visible = false,
                    ShadowStyle = ShadowStyle.BottomRight,
                    ShadowOffset = new Point(0, CHAT_LINE_SHADOW_Y)
                };

                AddChild(Lines[i]);
            }
        }

        //in the large hud the compact chat area is too small for a scrollbar
        ScrollBar.Visible = false;
    }

    public override void Dispose()
    {
        WorldState.Chat.MessageAdded -= OnMessageAdded;

        if (IncludeOrangeBar)
            WorldState.Chat.OrangeBarMessageAdded -= OnOrangeBarMessageAdded;

        base.Dispose();
    }

    //labels are children, drawn automatically by base.Draw

    private void OnMessageAdded(Chat.ChatMessage msg) => AddMessage(msg.Text, msg.Color, msg.Channel, msg.ChannelName);

    private void RefreshDisplay()
    {
        if (RenderedVersion == LogVersion)
            return;

        RenderedVersion = LogVersion;

        var maxLines = Math.Min(MaxVisibleLines, Lines.Length);
        var startIndex = Math.Max(0, ChatLog.Count - maxLines - ScrollOffset);
        var shown = Math.Min(ChatLog.Count - startIndex, maxLines);

        //bottom-anchored, when there are fewer messages than rows the empty rows sit at the top
        //the newest message is always in the bottom row and a new line pushes everything up
        var slot = maxLines - shown;

        for (var i = 0; i < slot; i++)
        {
            Lines[i].Text = string.Empty;
            LineTimes[i] = NowSeconds; //empty rows are never old
        }

        for (var i = 0; i < shown; i++)
        {
            var line = ChatLog[startIndex + i];
            Lines[slot + i].Text = line.Text;
            Lines[slot + i].ForegroundColor = line.Color;
            LineTimes[slot + i] = line.Time;
        }
    }

    private void RepositionLabels()
    {
        var relY = DisplayBounds.Y - PanelOriginY;
        var maxLines = Math.Min(MaxVisibleLines, Lines.Length);

        for (var i = 0; i < Lines.Length; i++)
            if (i < maxLines)
            {
                //bottom-up, line 0 at top and the last line at the bottom
                Lines[i].Y = relY + DisplayBounds.Height - (maxLines - i) * LineHeight;
                Lines[i].Visible = true;
            } else
                Lines[i].Visible = false;
    }

    public override void SetExpanded(bool expanded)
    {
        base.SetExpanded(expanded);

        DisplayBounds = expanded ? ExpandedDisplayBounds : NormalDisplayBounds;
        MaxVisibleLines = Math.Min(DisplayBounds.Height / LineHeight, Lines.Length);
        ScrollBar.Visible = expanded;
        ScrollBar.Height = DisplayBounds.Height;

        //show or hide labels based on the current line count
        for (var i = 0; i < Lines.Length; i++)
            Lines[i].Visible = i < MaxVisibleLines;

        //force a re-render with the new line count
        LogVersion++;
    }

    /// <summary>
    ///     Changes the TrueType font size of the chat log live and re-flows the history
    ///     Only valid for the TrueType chat where CustomFontSize is above 0
    /// </summary>
    public void SetCustomFontSize(int size)
    {
        if ((size <= 0) || (size == CustomFontSize))
            return;

        CustomFontSize = size;
        LineHeight = TtfTextRenderer.Available ? TtfTextRenderer.LineHeight(size) + CHAT_LINE_PAD : GLYPH_HEIGHT;

        foreach (var line in Lines)
        {
            line.CustomFontSize = size;
            line.Height = LineHeight;
        }

        //re-run the size-based layout and re-wrap the history at the new font
        Resize(Width, Height);
    }

    /// <summary>
    ///     Re-flows the chat to a new size when hosted in a resizable window
    ///     Assumes the panel's own origin is at zero, the HUD does not call this
    /// </summary>
    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;

        var db = new Rectangle(2, 2, Math.Max(0, width - 4), Math.Max(0, height - 4));
        DisplayBounds = db;
        var maxLines = db.Height > 0 ? db.Height / LineHeight : 0;

        if (maxLines > Lines.Length)
        {
            var old = Lines.Length;
            Array.Resize(ref Lines, maxLines);
            Array.Resize(ref LineTimes, maxLines);

            for (var i = old; i < maxLines; i++)
            {
                Lines[i] = new UILabel
                {
                    Name = $"ChatLine{i}",
                    X = db.X,
                    Width = db.Width - ScrollBarControl.DEFAULT_WIDTH,
                    Height = LineHeight,
                    CustomFontSize = CustomFontSize,
                    PaddingLeft = 0,
                    PaddingTop = 0,
                    ShadowStyle = ShadowStyle.BottomRight,
                    ShadowOffset = new Point(0, CHAT_LINE_SHADOW_Y)
                };

                AddChild(Lines[i]);
            }
        }

        MaxVisibleLines = maxLines;

        foreach (var line in Lines)
            line.Width = db.Width - ScrollBarControl.DEFAULT_WIDTH;

        RepositionLabels();

        ScrollBar.X = db.X + db.Width - ScrollBarControl.DEFAULT_WIDTH;
        ScrollBar.Y = db.Y;
        ScrollBar.Height = db.Height;

        //the stored lines were wrapped at the old width, re-wrap the whole history to the new one
        RewrapAll();
    }

    public void ScrollToBottom()
    {
        ScrollOffset = 0;
        ScrollBar.Value = ScrollBar.MaxValue;
        LogVersion++;
    }

    public override void OnMouseScroll(MouseScrollEvent e)
    {
        if (Scroll(e.Delta))
            e.Handled = true;
    }

    public bool Scroll(int delta)
    {
        if (ChatLog.Count <= MaxVisibleLines)
            return false;

        ScrollOffset = Math.Clamp(ScrollOffset + delta, 0, ChatLog.Count - MaxVisibleLines);
        ScrollBar.Value = ScrollBar.MaxValue - ScrollOffset;
        LogVersion++;

        return true;
    }

    public override void Update(GameTime gameTime)
    {
        if (!Visible || !Enabled)
            return;

        NowSeconds = gameTime.TotalGameTime.TotalSeconds;

        base.Update(gameTime);

        RefreshDisplay();
        UpdateLineOpacities();
    }

    //each line shows at max of window opacity and its own recency
    //a recent line stays visible while the chrome has faded and only fades once it is old
    private void UpdateLineOpacities()
    {
        var maxLines = Math.Min(MaxVisibleLines, Lines.Length);
        float delay = ClientSettings.ChatFadeDelaySeconds;

        for (var i = 0; i < maxLines; i++)
        {
            var recent = RecentFactor(NowSeconds - LineTimes[i], delay);
            Lines[i].Opacity = Math.Max(WindowOpacity, recent);

            //glow backs the text only while the chrome is gone, the dark window background backs it otherwise
            //the line-opacity factor is cubed so the glow fades away faster than the text once a line starts fading
            var vis = Lines[i].Opacity;
            Lines[i].GlowAlpha = vis * vis * vis * (1f - WindowOpacity);
        }
    }

    //1 while the line is younger than delaySeconds, easing to 0 over the second after
    //a delay of 0 or less means never fade
    private static float RecentFactor(double ageSeconds, float delaySeconds)
    {
        const float FADE_SPAN = 1f;

        if (delaySeconds <= 0f)
            return 1f;

        if (ageSeconds <= delaySeconds)
            return 1f;

        if (ageSeconds >= delaySeconds + FADE_SPAN)
            return 0f;

        return 1f - (float)(ageSeconds - delaySeconds) / FADE_SPAN;
    }

    private record struct ChatLine(string Text, Color Color, ChatChannel Channel = ChatChannel.Public, double Time = 0, string? ChannelName = null);
}