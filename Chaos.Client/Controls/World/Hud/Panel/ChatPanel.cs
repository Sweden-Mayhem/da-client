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
///     Chat display panel (F key). Shows chat message history with word-wrap. Background loaded from _nchatbk.spf (shown
///     in tab area). Text rendered at ChatDisplayBounds (separate area of the HUD).
/// </summary>
public sealed class ChatPanel : ExpandablePanel
{
    private const int MAX_CHAT_LINES = 200;
    private const int GLYPH_HEIGHT = 12;

    //every chat line draws a black 1px outline plus a 50% drop shadow this many pixels down (further than the outline)
    private const int CHAT_LINE_SHADOW_Y = 3;

    //extra vertical room added to each TTF line so a descender's drop shadow (e.g. the tail of "g") isn't clipped by
    //the line's own height. Matches the shadow offset plus a hair.
    private const int CHAT_LINE_PAD = 4;
    private readonly List<ChatLine> ChatLog = [];
    private readonly Rectangle NormalDisplayBounds;
    private readonly int PanelOriginX;
    private readonly int PanelOriginY;
    private readonly ScrollBarControl ScrollBar;

    //0 = retail bitmap font (HUD default, unchanged). >0 = render lines with the optional TrueType font at this size.
    //mutable so the chat window can rescale the log live from the "Chat font size" slider (see SetCustomFontSize).
    private int CustomFontSize;
    private int LineHeight;

    //raw (un-wrapped) messages kept so the log can be re-wrapped when the window is resized or the active tab changes
    private readonly List<ChatLine> RawMessages = [];
    private readonly bool IncludeOrangeBar;

    //null shows all channels; otherwise only messages on this channel are shown
    private ChatChannel? ActiveChannel;
    private string? ActiveChannelName; //when set, the active tab is a custom "!channel" and messages are filtered by name

    private Rectangle DisplayBounds;
    private Rectangle ExpandedDisplayBounds;
    private UILabel[] Lines;

    //post time (game seconds) of the message currently shown in each line slot, parallel to Lines. Used to fade out
    //old lines while the window is idle. Empty slots are stamped "now" so they never count as old.
    //opacity of the host window chrome (1 = shown, 0 = faded out). A line is shown at max(window opacity, its own age
    //recency), so a recent line stays readable even while the window chrome has faded; old lines fade by age.
    private float WindowOpacity = 1f;

    //post time (game seconds) of the line currently in each slot, parallel to Lines; drives the per-line age fade.
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

        //position relative to panel origin (panel is placed at panelbounds by registertab)
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

    private void OnOrangeBarMessageAdded(Chat.OrangeBarMessage msg) => AddMessage(msg.Text, msg.Color, ChatChannel.System, channelName: null, msg.Timestamp);

    //friend presence lines put the name first, e.g. "Name has come online." or "Name has gone offline."
    private static readonly string[] FriendPresenceMarkers = [" has come online", " has gone offline"];
    private const string FriendsOnlinePrefix = "Friends online: ";

    private readonly List<(string Name, int Start)> NameSpans = [];

    /// <summary>
    ///     If a clickable player NAME is at the given SCREEN point in the chat log, returns it; else null. Used for
    ///     click-to-whisper and the hover hand cursor. Handles the single-name lines (public/yell/whisper/channel) as well
    ///     as friend presence ("Name has come online.") and the "Friends online: A, B, C." list, where every name clicks.
    /// </summary>
    public string? NameAt(int screenX, int screenY)
    {
        if ((CustomFontSize <= 0) || !TtfTextRenderer.Available)
            return null;

        foreach (var label in Lines)
        {
            //ignore empty rows and ones that have faded out (a faded chat's names aren't really visible to click)
            if (label is null || string.IsNullOrEmpty(label.Text) || (label.Opacity < 0.3f))
                continue;

            if ((screenY < label.ScreenY) || (screenY >= label.ScreenY + LineHeight))
                continue;

            CollectNameSpans(label.Text, NameSpans);

            foreach (var (name, start) in NameSpans)
            {
                //each name may sit after some prefix text, so offset the hit area by the width of what precedes it
                var prefixWidth = start > 0 ? TtfTextRenderer.MeasureWidth(label.Text[..start], CustomFontSize) : 0;
                var nameWidth = TtfTextRenderer.MeasureWidth(name, CustomFontSize);
                var nameLeft = label.ScreenX + prefixWidth;

                if ((screenX >= nameLeft) && (screenX <= nameLeft + nameWidth + 2))
                    return name;
            }

            return null; //only one line can be on this Y
        }

        return null;
    }

    //finds every clickable player name in a chat line, each with where it starts so the hit area can be placed on it
    private static void CollectNameSpans(string text, List<(string Name, int Start)> spans)
    {
        spans.Clear();

        if (text.Length == 0)
            return;

        //strip a leading "[HH:mm] " timestamp prefix before name detection so the name parsers see the
        //original text regardless of whether timestamps are currently shown. The offset is added back to
        //every Start value so the click hit area lands on the correct screen position.
        var tsOffset = 0;

        if ((text.Length > 8) && (text[0] == '[') && (text[3] == ':') && (text[6] == ']') && (text[7] == ' '))
            tsOffset = 8;

        var t = tsOffset > 0 ? text[tsOffset..] : text;

        //friend came online / went offline: the name is the leading token before the marker
        foreach (var marker in FriendPresenceMarkers)
        {
            var at = t.IndexOf(marker, StringComparison.Ordinal);

            if (at > 0)
            {
                if (IsPlayerName(t[..at]))
                    spans.Add((t[..at], tsOffset));

                return;
            }
        }

        //"Friends online: A, B, C." - every comma separated name is clickable
        if (t.StartsWith(FriendsOnlinePrefix, StringComparison.Ordinal))
        {
            var i = FriendsOnlinePrefix.Length;

            while (i < t.Length)
            {
                while ((i < t.Length) && !char.IsLetter(t[i]))
                    i++;

                var start = i;

                while ((i < t.Length) && char.IsLetter(t[i]))
                    i++;

                if (i > start)
                    spans.Add((t[start..i], tsOffset + start));
            }

            return;
        }

        //everything else has at most one name (public/yell/whisper/channel)
        if (ParseChatName(t) is { } single)
            spans.Add((single.Name, tsOffset + single.Start));
    }

    //the clickable player NAME in a chat line plus where it starts in the line (so the hit area lands on the drawn name).
    //  public / yell: "Name: msg" / "Name! msg" - name at the start
    //  whisper:       "[Name]: msg" (received) or "[Name]> msg" (sent echo) - name is in the brackets
    //  group / guild: "[Group] Name: msg" - the tag has a space after it, so skip the tag and read the real name
    //Filters body text that happens to contain a separator and multi-word system lines ("You say:").
    private static (string Name, int Start)? ParseChatName(string text)
    {
        if (text.Length == 0)
            return null;

        //a whisper puts the name in brackets followed immediately by ':' or '>'. A channel tag ("[Group] ") has a space
        //after the ']' instead, so we skip past the tag and read the real name from what follows.
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

    //a "Name: msg" / "Name! msg" name beginning at offset start, returned with its absolute start index
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
    ///     Switches the visible tab. Pass null for "All" (every channel). Re-wraps the stored history through the new
    ///     filter and snaps to the newest line.
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

    //All (both null) shows everything. A custom-channel tab shows only that channel's messages. A built-in tab shows its
    //channel AND only messages that aren't custom-channel ones (so channel chat never leaks into the Public tab).
    private bool PassesFilter(ChatChannel channel, string? channelName)
    {
        if (ActiveChannel is null && ActiveChannelName is null)
            return true;

        if (ActiveChannelName is not null)
            return channelName == ActiveChannelName;

        return (channel == ActiveChannel) && (channelName is null);
    }

    private void AddMessage(string text, Color color, ChatChannel channel, string? channelName = null, DateTime timestamp = default)
    {
        //drop blank / whitespace-only messages outright so a stray newline (in orange/system or NPC text) or an
        //empty send never leaves a gap in the log
        if (string.IsNullOrWhiteSpace(text))
            return;

        var time = NowSeconds;
        RawMessages.Add(new ChatLine(text, color, channel, time, channelName, timestamp));

        if (RawMessages.Count > MAX_CHAT_LINES)
            RawMessages.RemoveRange(0, RawMessages.Count - MAX_CHAT_LINES);

        //messages on other channels stay in RawMessages (so a tab switch can reveal them) but are not shown now
        if (!PassesFilter(channel, channelName))
            return;

        WrapInto(text, color, time, timestamp);
        AfterLogChanged();
    }

    //word-wraps one message to the current width and appends the resulting line(s) to the display log. Each wrapped line
    //carries the source message's post time so the age fade treats a multi-line message as one unit.
    //When ShowChatTimestamp is on, prepends "[HH:mm] " to the first visual line only.
    private void WrapInto(string text, Color color, double time, DateTime timestamp = default)
    {
        var maxWidth = DisplayBounds.Width - ScrollBarControl.DEFAULT_WIDTH;

        if (maxWidth <= 0)
            return;

        var prefix = (ClientSettings.ShowChatTimestamp && timestamp != default)
            ? $"[{timestamp:HH:mm}] "
            : string.Empty;

        var prefixedText = prefix.Length > 0 ? prefix + text : text;

        if (CustomFontSize > 0)
        {
            //WrapText handles the full prefixed string at once -the prefix naturally lands on the first line only
            foreach (var line in TtfTextRenderer.WrapText(prefixedText, maxWidth, CustomFontSize))
                if (!string.IsNullOrWhiteSpace(line))
                    ChatLog.Add(new ChatLine(line, color, ChatChannel.Public, time));
        } else
        {
            var firstLine = true;
            var remaining = prefixedText;

            while (remaining.Length > 0)
            {
                var lineEnd = TextRenderer.FindLineBreak(remaining, maxWidth);
                var line = remaining[..lineEnd].TrimEnd();
                remaining = remaining[lineEnd..].TrimStart();

                if (!string.IsNullOrWhiteSpace(line))
                {
                    ChatLog.Add(new ChatLine(line, color, ChatChannel.Public, time));

                    if (firstLine && (prefix.Length > 0))
                    {
                        //subsequent wrapped lines have no prefix -indent by the same number of spaces so they
                        //align with the message text rather than overflowing back to column 0
                        remaining = new string(' ', prefix.Length) + remaining.TrimStart();
                        firstLine = false;
                    }
                }
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

    //re-wraps the entire stored history to the current width (called after a resize or timestamp toggle)
    private void RewrapAll()
    {
        ChatLog.Clear();

        foreach (var msg in RawMessages)
            if (PassesFilter(msg.Channel, msg.ChannelName))
                WrapInto(msg.Text, msg.Color, msg.Time, msg.Timestamp);

        ScrollOffset = 0;
        AfterLogChanged();
    }

    /// <summary>Re-wraps all stored messages so timestamps are added to (or removed from) every line immediately.
    ///     Called when the "Show chat timestamps" setting is toggled.</summary>
    public void RefreshTimestamps() => RewrapAll();

    /// <summary>Tracks the hosting window's chrome opacity and fades the scrollbar with it. Recent chat lines stay fully
    ///     readable while the window is faded; lines older than the fade delay fade out with the window (see Update).</summary>
    public void SetContentOpacity(float opacity)
    {
        WindowOpacity = opacity;
        ScrollBar.Opacity = opacity;
    }

    /// <summary>
    ///     Configures expand support for the large HUD chat panel (larger text area).
    /// </summary>
    public void ConfigureExpand(Texture2D? expandedBackground, Rectangle expandedBounds, Rectangle panelBounds)
    {
        ExpandedDisplayBounds = expandedBounds;

        //clear the normal background so expandyoffset is computed from panel height, not the
        //texture height (which is the same as the expanded texture, yielding expandyoffset=0).
        Background = null;
        Height = panelBounds.Height;

        ConfigureExpand(expandedBackground);

        //create additional labels needed for the expanded line count
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

        //in the large hud, the compact chat area is too small for a scrollbar
        ScrollBar.Visible = false;
    }

    public override void Dispose()
    {
        WorldState.Chat.MessageAdded -= OnMessageAdded;

        if (IncludeOrangeBar)
            WorldState.Chat.OrangeBarMessageAdded -= OnOrangeBarMessageAdded;

        base.Dispose();
    }

    //labels are children -drawn automatically by base.draw()

    private void OnMessageAdded(Chat.ChatMessage msg) => AddMessage(msg.Text, msg.Color, msg.Channel, msg.ChannelName, msg.Timestamp);

    private void RefreshDisplay()
    {
        if (RenderedVersion == LogVersion)
            return;

        RenderedVersion = LogVersion;

        var maxLines = Math.Min(MaxVisibleLines, Lines.Length);
        var startIndex = Math.Max(0, ChatLog.Count - maxLines - ScrollOffset);
        var shown = Math.Min(ChatLog.Count - startIndex, maxLines);

        //bottom-anchored: when there are fewer messages than rows, the empty rows sit at the TOP and the newest
        //message is always in the bottom row. A new line pushes everything up.
        var slot = maxLines - shown;

        for (var i = 0; i < slot; i++)
        {
            Lines[i].Text = string.Empty;
            LineTimes[i] = NowSeconds; //empty rows are never "old"
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
                //bottom-up: line 0 at top, line maxlines-1 at bottom
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

        //show/hide labels based on current line count
        for (var i = 0; i < Lines.Length; i++)
            Lines[i].Visible = i < MaxVisibleLines;

        //force re-render with new line count
        LogVersion++;
    }

    /// <summary>
    ///     Changes the TrueType font size of the chat log live (the "Chat font size" slider). Updates the line height +
    ///     every label, then re-flows the history to the new metrics. Only valid for the TrueType chat (CustomFontSize &gt; 0).
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

        //re-run the size-based layout (line count, positions, scrollbar) + re-wrap the history at the new font
        Resize(Width, Height);
    }

    /// <summary>
    ///     Re-flows the chat to a new size when hosted in a resizable window. Assumes the panel's own origin is (0,0)
    ///     (i.e. it was constructed with a panelBounds at the origin). The HUD does not call this.
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

        //the stored lines were wrapped at the old width; re-wrap the whole history to the new width
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

    //each line shows at max(window opacity, its own age recency): a recent line stays visible even while the window
    //chrome has faded, and only fades once it is old. This is independent of the window fade (ChatWindow).
    private void UpdateLineOpacities()
    {
        var maxLines = Math.Min(MaxVisibleLines, Lines.Length);
        float delay = ClientSettings.ChatFadeDelaySeconds;

        for (var i = 0; i < maxLines; i++)
        {
            var recent = RecentFactor(NowSeconds - LineTimes[i], delay);
            Lines[i].Opacity = Math.Max(WindowOpacity, recent);

            //glow = how visible the line is while the window chrome is gone: full when the window has faded out but the
            //line is still up (recent), zero when the window is shown (its dark background already backs the text). The
            //line-opacity factor is CUBED so the dark glow fades away noticeably faster than the text once the line starts
            //fading - it is much darker than the text, so a linear fade left it lingering after the text was already gone.
            var vis = Lines[i].Opacity;
            Lines[i].GlowAlpha = vis * vis * vis * (1f - WindowOpacity);
        }
    }

    //1 while the line is younger than delaySeconds, easing to 0 over the second after. delaySeconds <= 0 = never fade.
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

    private record struct ChatLine(string Text, Color Color, ChatChannel Channel = ChatChannel.Public, double Time = 0, string? ChannelName = null, DateTime Timestamp = default);
}