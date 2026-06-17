#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Data.Models;
using Chaos.Client.ViewModel;
using Chaos.Extensions.Common;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Controls.World.Hud;

public enum ChatMode
{
    None,
    Normal,
    Shout,
    WhisperName,
    WhisperMessage,
    IgnoreModeSelect,
    IgnoreAdd,
    IgnoreRemove,
    Prompt
}

public sealed class ChatInputControl : UIPanel
{
    private const int MAX_WHISPER_HISTORY = 5;
    private const int MAX_SENT_HISTORY = 50;
    private const int WHISPER_COMPLETE_MAX = 200;

    private int FullWidth;
    private readonly UILabel PrefixLabel;
    private readonly UITextBox TextBox;
    private readonly List<string> WhisperHistory = [];

    //bash-style recall of previously sent lines: Up walks toward the oldest, Down toward the in-progress draft.
    //HistoryNav is the number of entries back from the newest (0 = the live draft). Not persisted between sessions.
    private readonly List<string> SentHistory = [];
    private int HistoryNav;
    private string HistoryDraft = string.Empty;

    private Action<string>? PromptCallback;
    private Color? SavedFocusedBackgroundColor;
    private int SavedMaxLength;
    private int WhisperHistoryIndex;
    private string? WhisperTarget;

    //the last non-whisper "place" a message was sent, so Enter reopens it. Whispering a PERSON is deliberately excluded
    //(it has its own keybind). Guild/group and custom "!name" channels are remembered (the channel kept in LastChannelTarget).
    private enum ChatPlace { Normal, Shout, Guild, Group, Whisper, Channel }
    private ChatPlace LastPlace = ChatPlace.Normal;
    private string? LastChannelTarget;
    private static readonly Color ChannelColor = new(120, 200, 230);

    private int FontSize;

    public ChatMode Mode { get; private set; }
    public bool IsFocused => TextBox.IsFocused;


    public Func<string>? PlayerNameProvider { get; set; }

    public int CustomFontSize
    {
        set
        {
            FontSize = value;
            TextBox.CustomFontSize = value;
            PrefixLabel.CustomFontSize = value;
        }
    }

    private int MeasurePrefix(string text)
        => (FontSize > 0) && TtfTextRenderer.Available ? TtfTextRenderer.MeasureWidth(text, FontSize) : TextRenderer.MeasureWidth(text);

    public ChatInputControl(ControlPrefabSet prefabSet)
    {
        Name = "ChatInput";

        var rect = PrefabPanel.GetRect(prefabSet, "SAY");
        X = rect.X;
        Y = rect.Y;
        Width = rect.Width;
        Height = rect.Height;
        FullWidth = rect.Width;

        PrefixLabel = new UILabel
        {
            Name = "ChatPrefix",
            X = 0,
            Y = 0,
            Width = 0,
            Height = rect.Height,
            BackgroundColor = Color.Black,
            PaddingLeft = 1,
            PaddingTop = 1,
            TruncateWithEllipsis = false,
            Visible = false
        };

        AddChild(PrefixLabel);

        TextBox = new UITextBox
        {
            Name = "ChatTextBox",
            X = 0,
            Y = 0,
            Width = rect.Width,
            Height = rect.Height,
            MaxLength = 512,
            PaddingLeft = 1,
            PaddingRight = 1,
            PaddingTop = 1,
            PaddingBottom = 1,
            FocusedBackgroundColor = new Color(0, 0, 0, 160)
        };

        AddChild(TextBox);

        //register the chat textbox so popups don't tear keyboard focus away while typing.
        if (InputDispatcher.Instance is { } dispatcher)
            dispatcher.ChatInputTextBox = TextBox;

        //clicking directly into the box (instead of pressing Enter) must still arm a real chat mode. Otherwise the
        //textbox accepts text but Enter sends nothing, because Mode is still None. A bare click-focus is promoted to
        //the normal typing line (or whisper, if that tab is active), exactly like pressing Enter. OnFocused fires only
        //on a click (the Focus* methods set IsFocused directly), so this never double-fires for our own focus changes.
        TextBox.OnFocused += _ =>
        {
            if (Mode == ChatMode.None)
                FocusForTyping();
        };

        //whispering a PERSON: backspace on an empty message goes back to picking the name (prefilled so you can edit it).
        //channels (!group/!guild) have no name step, so leave them be.
        TextBox.OnBackspaceWhenEmpty += () =>
        {
            if ((Mode == ChatMode.WhisperMessage) && WhisperTarget is { } target && !target.StartsWith('!'))
            {
                WhisperHistoryIndex = 0;
                FocusInternal(ChatMode.WhisperName, $"to [{target}]? ", TextColors.Whisper);
            }
        };

        //if focus is taken away from outside (e.g. a click lands outside the chat window mid-type), tear down the
        //typing state cleanly instead of leaving the prefix/mode stranded with no caret.
        UITextBox.TextBoxFocusLost += OnTextBoxFocusLost;
    }

    public override void Dispose()
    {
        UITextBox.TextBoxFocusLost -= OnTextBoxFocusLost;
        base.Dispose();
    }

    private void OnTextBoxFocusLost(UITextBox textBox)
    {
        //external defocus is equivalent to pressing Escape: drop any prompt/whisper/shout state and reset the prefix.
        //(internal Unfocus sets Mode to None before blurring, so this is inert for our own focus changes.)
        if ((textBox == TextBox) && (Mode != ChatMode.None))
            HandleEscape();
    }

    public void SetBounds(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        FullWidth = width;
        PrefixLabel.Height = height;
        TextBox.Height = height;
        TextBox.Width = width;
    }

    //--- events ---

    public event MessageSentHandler? MessageSent;
    public event ShoutSentHandler? ShoutSent;
    public event WhisperSentHandler? WhisperSent;
    public event IgnoreAddedHandler? IgnoreAdded;
    public event IgnoreRemovedHandler? IgnoreRemoved;
    public event IgnoreListRequestedHandler? IgnoreListRequested;
    public event FocusChangedHandler? FocusChanged;

    public event Action<int>? TabCycleRequested;

    //--- layout ---

    private void UpdateLayout(string prefix, Color color)
    {
        if (prefix.Length == 0)
        {
            PrefixLabel.Visible = false;
            TextBox.X = 0;
            TextBox.Width = FullWidth;

            return;
        }

        var prefixWidth = MeasurePrefix(prefix) + PrefixLabel.PaddingLeft;
        PrefixLabel.Text = prefix;
        PrefixLabel.ForegroundColor = color;
        PrefixLabel.Width = prefixWidth;
        PrefixLabel.Visible = true;

        TextBox.X = prefixWidth;
        TextBox.Width = FullWidth - prefixWidth;
    }

    //--- focus methods ---

    private void FocusInternal(ChatMode mode, string prefix, Color color)
    {
        Mode = mode;
        TextBox.MaxLength = 512; //default, the channel/whisper paths tighten it AFTER this
        UpdateLayout(prefix, color);
        TextBox.ForegroundColor = color;
        TextBox.IsFocused = true;
        FocusChanged?.Invoke(true);
    }

    public void Focus(string prefix, Color color)
    {
        ChatMode mode;

        if (prefix.EndsWithI("! "))
            mode = ChatMode.Shout;
        else if (prefix.StartsWithI("-> ") && prefix.EndsWithI(": "))
        {
            mode = ChatMode.WhisperMessage;
            WhisperTarget = prefix[3..^2];
        } else
            mode = ChatMode.Normal;

        FocusInternal(mode, prefix, color);
    }

    public void FocusForTyping()
    {
        if (Mode == ChatMode.Prompt)
        {
            HandleEscape();

            return;
        }

        switch (LastPlace)
        {
            case ChatPlace.Shout:
                FocusShout();

                break;
            case ChatPlace.Guild:
                FocusGuild();

                break;
            case ChatPlace.Group:
                FocusGroup();

                break;
            case ChatPlace.Whisper:
                FocusWhisper();

                break;
            case ChatPlace.Channel when LastChannelTarget is { Length: > 1 }:
                FocusChannel(LastChannelTarget, $"[{LastChannelTarget.TrimStart('!')}] ", ChannelColor);

                break;
            default:
                FocusSay();

                break;
        }
    }

    public void FocusSay() => Focus($"{PlayerNameProvider?.Invoke() ?? string.Empty}: ", Color.White);

    public void SetInputToTab(ChatChannel? channel, string? channelName)
    {
        if (channelName is { Length: > 1 } chan)
        {
            LastPlace = ChatPlace.Channel;
            LastChannelTarget = chan;
        }
        else
            LastPlace = channel switch
            {
                ChatChannel.Group   => ChatPlace.Group,
                ChatChannel.Guild   => ChatPlace.Guild,
                ChatChannel.Whisper => ChatPlace.Whisper,
                _                   => ChatPlace.Normal //All / Public / System -> public "/say"
            };

        if (IsFocused)
            FocusForTyping();
    }

    //remember a just-used channel target so Enter reopens it
    private void RecordChannelPlace(string target)
    {
        if (target.EqualsI("!guild"))
            LastPlace = ChatPlace.Guild;
        else if (target.EqualsI("!group"))
            LastPlace = ChatPlace.Group;
        else
        {
            LastPlace = ChatPlace.Channel;
            LastChannelTarget = target;
        }
    }

    public void FocusWhisper()
    {
        WhisperHistoryIndex = 0;

        //if we have whispered someone already, skip picking the name and go straight to the message for them.
        //backspace on an empty message drops back to choosing the name (see the OnBackspaceWhenEmpty wiring).
        if (WhisperHistory.Count > 0)
        {
            StartWhisperMessage(WhisperHistory[0]);

            return;
        }

        FocusInternal(ChatMode.WhisperName, "to []? ", TextColors.Whisper);
    }

    //open the input ready to type a whisper to a known person
    private void StartWhisperMessage(string target)
    {
        WhisperTarget = target;
        FocusInternal(ChatMode.WhisperMessage, $"-> {target}: ", TextColors.Whisper);
        TextBox.MaxLength = Math.Max(1, WHISPER_COMPLETE_MAX - target.Length - 4);
        TextBox.Text = string.Empty;
    }

    public void FocusShout()
        => FocusInternal(ChatMode.Shout, $"{PlayerNameProvider?.Invoke() ?? string.Empty}! ", TextColors.Shout);

    public void FocusGuild() => FocusChannel("!guild", "[Guild] ", TextColors.GuildChat);

    public void FocusGroup() => FocusChannel("!group", "[Group] ", TextColors.GroupChat);

    private void FocusChannel(string channel, string prefix, Color color)
    {
        WhisperTarget = channel;
        FocusInternal(ChatMode.WhisperMessage, prefix, color);
        TextBox.MaxLength = Math.Max(1, WHISPER_COMPLETE_MAX - channel.Length - 4); //same combined limit a whisper uses
    }

    public void FocusIgnore()
    {
        FocusInternal(ChatMode.IgnoreModeSelect, "a: add, d: delete, ?: see list>", TextColors.Default);
        TextBox.IsReadOnly = true;
    }

    public void ShowPrompt(string prefix, int maxLength, Action<string> onConfirm)
    {
        PromptCallback = onConfirm;
        SavedMaxLength = TextBox.MaxLength;
        SavedFocusedBackgroundColor = TextBox.FocusedBackgroundColor;

        TextBox.MaxLength = maxLength;
        TextBox.FocusedBackgroundColor = Color.White;
        TextBox.BackgroundColor = Color.White;
        TextBox.ForegroundColor = Color.Black;

        Mode = ChatMode.Prompt;
        PrefixLabel.BackgroundColor = Color.White;
        UpdateLayout(prefix, Color.Black);
        TextBox.Text = string.Empty;
        TextBox.IsFocused = true;
        FocusChanged?.Invoke(true);
    }

    public void Unfocus()
    {
        Mode = ChatMode.None;
        WhisperTarget = null;
        HistoryNav = 0;
        HistoryDraft = string.Empty;
        TextBox.MaxLength = 512;
        TextBox.IsReadOnly = false;
        TextBox.IsFocused = false;
        TextBox.Text = string.Empty;
        TextBox.ForegroundColor = Color.White;
        UpdateLayout(string.Empty, Color.White);
        InputDispatcher.Instance?.ClearExplicitFocus();
        FocusChanged?.Invoke(false);
    }

    public void SendPublic(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
            MessageSent?.Invoke(message);
    }

    public void SetText(string text, int cursorPosition)
    {
        TextBox.Text = text;
        TextBox.CursorPosition = cursorPosition;
        TextBox.ClearSelection();
    }

    private void RestoreFromPrompt()
    {
        PromptCallback = null;
        TextBox.MaxLength = SavedMaxLength;
        TextBox.FocusedBackgroundColor = SavedFocusedBackgroundColor;
        TextBox.BackgroundColor = null;
        PrefixLabel.BackgroundColor = Color.Black;
    }

    //--- whisper history ---

    private void AddWhisperTarget(string name)
    {
        WhisperHistory.Remove(name);
        WhisperHistory.Insert(0, name);

        if (WhisperHistory.Count > MAX_WHISPER_HISTORY)
            WhisperHistory.RemoveAt(WhisperHistory.Count - 1);
    }

    //when a whisper arrives and the player has never whispered anyone, make the sender the default whisper target so the
    //whisper key replies to them. We only seed when empty so it never overrides a target the player has chosen.
    public void SeedWhisperTargetIfEmpty(string name)
    {
        if ((WhisperHistory.Count == 0) && !string.IsNullOrEmpty(name))
            AddWhisperTarget(name);
    }

    private void CycleWhisperTarget(int direction)
    {
        if ((WhisperHistory.Count == 0) || (Mode != ChatMode.WhisperName))
            return;

        WhisperHistoryIndex = (WhisperHistoryIndex + direction + WhisperHistory.Count) % WhisperHistory.Count;
        UpdateLayout($"to [{WhisperHistory[WhisperHistoryIndex]}]? ", TextBox.ForegroundColor);
    }

    //--- sent-line history ---

    //records a line that was just sent so Up can recall it. De-dupes a repeat of the most recent line.
    private void PushHistory(string message)
    {
        if (message.Length == 0)
            return;

        if ((SentHistory.Count > 0) && string.Equals(SentHistory[^1], message, StringComparison.Ordinal))
            return;

        SentHistory.Add(message);

        if (SentHistory.Count > MAX_SENT_HISTORY)
            SentHistory.RemoveAt(0);
    }

    //walks the recall: dir +1 = further back (older), dir -1 = forward toward the live draft (HistoryNav 0).
    private void NavigateHistory(int dir)
    {
        if (SentHistory.Count == 0)
            return;

        //stepping into history from the live line: stash what was being typed so Down can bring it back
        if (HistoryNav == 0)
            HistoryDraft = TextBox.Text;

        var next = Math.Clamp(HistoryNav + dir, 0, SentHistory.Count);

        if (next == HistoryNav)
            return;

        HistoryNav = next;

        var text = HistoryNav == 0 ? HistoryDraft : SentHistory[^HistoryNav];
        SetText(text, text.Length);
    }

    private string GetBracketedWhisperTarget()
    {
        // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
        var prefix = PrefixLabel.Text ?? string.Empty;
        var start = prefix.IndexOf('[') + 1;
        var end = prefix.IndexOf(']');

        if ((start <= 0) || (end < start))
            return string.Empty;

        return prefix[start..end];
    }

    //--- input handling ---

    public override void OnKeyDown(KeyDownEvent e)
    {
        if (e.Key == Keys.Enter)
        {
            HandleEnter();
            e.Handled = true;

            return;
        }

        if (e.Key == Keys.Escape)
        {
            HandleEscape();
            e.Handled = true;
        }
    }

    private void HandleEnter()
    {
        var message = TextBox.Text.Trim();

        //empty / whitespace-only chat input: treat Enter like Escape and close without sending,
        //so we never push a blank line to the server (which would echo back as an empty gap in everyone's chat)
        if ((message.Length == 0) && Mode is ChatMode.Normal or ChatMode.Shout or ChatMode.WhisperMessage)
        {
            Unfocus();

            return;
        }

        //a line starting with "/" is a command. The server only runs commands from PUBLIC messages, so ALWAYS send it as
        //a normal message regardless of the current channel/mode. Typing e.g. "/joinchannel x" while in guild
        //or a custom channel would otherwise say it there instead of sending the command. The "last place" is left unchanged.
        //Only the chat-message modes are affected. A prompt (gold/item amount), ignore-list entry, or whisper-name pick is
        //not chat and must keep its "/" literally.
        if (message.StartsWith('/') && Mode is ChatMode.Normal or ChatMode.Shout or ChatMode.WhisperMessage)
        {
            PushHistory(message);
            MessageSent?.Invoke(message);
            Unfocus();

            return;
        }

        switch (Mode)
        {
            case ChatMode.Normal:
                PushHistory(message);
                MessageSent?.Invoke(message);
                LastPlace = ChatPlace.Normal;
                Unfocus();

                break;

            case ChatMode.Shout:
                PushHistory(message);
                ShoutSent?.Invoke(message);
                LastPlace = ChatPlace.Shout;
                Unfocus();

                break;

            case ChatMode.IgnoreModeSelect:
                Unfocus();

                break;

            case ChatMode.IgnoreAdd:
                if (message.Length > 0)
                    IgnoreAdded?.Invoke(message);

                Unfocus();

                break;

            case ChatMode.IgnoreRemove:
                if (message.Length > 0)
                    IgnoreRemoved?.Invoke(message);

                Unfocus();

                break;

            case ChatMode.WhisperName:
                var targetName = message.Length > 0 ? message : GetBracketedWhisperTarget();

                if (targetName.Length > 0)
                {
                    WhisperTarget = targetName;
                    Mode = ChatMode.WhisperMessage;
                    TextBox.MaxLength = Math.Max(1, WHISPER_COMPLETE_MAX - targetName.Length - 4);
                    UpdateLayout($"-> {targetName}: ", TextBox.ForegroundColor);
                    TextBox.Text = string.Empty;
                }

                break;

            case ChatMode.WhisperMessage:
                if (WhisperTarget is not null)
                {
                    //a channel target ("!...") is a remembered "place". A person whisper is NOT remembered as a place
                    //(it IS kept in the whisper-name history/cycle, unlike channels).
                    if (WhisperTarget.StartsWith('!'))
                        RecordChannelPlace(WhisperTarget);
                    else
                        AddWhisperTarget(WhisperTarget);

                    PushHistory(message); //the message body joins the shared Up/Down recall, same as a public line
                    WhisperSent?.Invoke(WhisperTarget, message);
                }

                Unfocus();

                break;

            case ChatMode.Prompt:
                var callback = PromptCallback;
                var text = TextBox.Text;
                RestoreFromPrompt();
                Unfocus();
                callback?.Invoke(text);

                break;
        }
    }

    private void HandleEscape()
    {
        if (Mode == ChatMode.Prompt)
            RestoreFromPrompt();

        Unfocus();
    }

    public override void OnTextInput(TextInputEvent e)
    {
        if (Mode != ChatMode.IgnoreModeSelect)
            return;

        switch (e.Character)
        {
            case 'a' or 'A':
                Mode = ChatMode.IgnoreAdd;
                TextBox.IsReadOnly = false;
                UpdateLayout("ID of people you wish to reject whisper >", TextBox.ForegroundColor);
                TextBox.Text = string.Empty;
                e.Handled = true;

                break;

            case 'd' or 'D':
                Mode = ChatMode.IgnoreRemove;
                TextBox.IsReadOnly = false;
                UpdateLayout("ID of people you wish to cancel rejection of whisper >", TextBox.ForegroundColor);
                TextBox.Text = string.Empty;
                e.Handled = true;

                break;

            case '?':
                IgnoreListRequested?.Invoke();
                Unfocus();
                e.Handled = true;

                break;

            default:
                e.Handled = true;

                break;
        }
    }

    public override void Update(GameTime gameTime)
    {
        base.Update(gameTime);

        if (!IsFocused)
            return;

        //Tab / Shift+Tab while typing cycles the chat tab forward/back, just like clicking the next/prev tab (the tab
        //switch also re-targets this input to that tab's channel). The TextBox swallows the Tab KeyDown, so poll it here.
        if (InputBuffer.WasKeyPressed(Keys.Tab))
        {
            var back = (InputBuffer.CurrentModifiers & KeyModifiers.Shift) != 0;
            TabCycleRequested?.Invoke(back ? -1 : 1);

            return;
        }

        //typing a chat command followed by a space in any message line switches the input to that channel; the command
        //text is removed. Works from Normal/Shout/channel/guild/group lines so you can hop between channels mid-type.
        //"/say " (or "/s ") public, "/whisper " (or "/w ") whisper-name, "/yell "//"/shout " (or "/y ") yell, "/guild ",
        //"/group " (or "/g "), and "/!name " for a custom channel (the "!" disambiguates from server slash-commands like
        ///joinchannel, which pass straight through). Polled (the TextBox owns the keystrokes), matched when the space lands.
        if (Mode is ChatMode.Normal or ChatMode.Shout or ChatMode.WhisperMessage)
        {
            var typed = TextBox.Text;

            if (string.Equals(typed, "/say ", StringComparison.OrdinalIgnoreCase)
                || string.Equals(typed, "/s ", StringComparison.OrdinalIgnoreCase))
            {
                SetText(string.Empty, 0);
                FocusSay();

                return;
            }

            if (string.Equals(typed, "/whisper ", StringComparison.OrdinalIgnoreCase)
                || string.Equals(typed, "/w ", StringComparison.OrdinalIgnoreCase))
            {
                SetText(string.Empty, 0);
                FocusWhisper();

                return;
            }

            if (string.Equals(typed, "/yell ", StringComparison.OrdinalIgnoreCase)
                || string.Equals(typed, "/shout ", StringComparison.OrdinalIgnoreCase)
                || string.Equals(typed, "/y ", StringComparison.OrdinalIgnoreCase))
            {
                SetText(string.Empty, 0);
                FocusShout();

                return;
            }

            if (string.Equals(typed, "/guild ", StringComparison.OrdinalIgnoreCase))
            {
                SetText(string.Empty, 0);
                FocusGuild();

                return;
            }

            if (string.Equals(typed, "/group ", StringComparison.OrdinalIgnoreCase)
                || string.Equals(typed, "/g ", StringComparison.OrdinalIgnoreCase))
            {
                SetText(string.Empty, 0);
                FocusGroup();

                return;
            }

            //custom channel: "/!name " -> talk in channel "!name" (a whisper the server routes to the channel)
            if ((typed.Length > 3) && (typed[0] == '/') && (typed[1] == '!') && (typed[^1] == ' '))
            {
                var channel = typed[1..^1].Trim();

                if (channel.Length > 1)
                {
                    SetText(string.Empty, 0);
                    FocusChannel(channel, $"[{channel.TrimStart('!')}] ", ChannelColor);

                    return;
                }
            }
        }

        //Up/Down cycle whisper targets while picking a name. Otherwise (normal/shout typing) they recall sent lines.
        //Polled from InputBuffer because the focused TextBox grabs the arrow KeyDowns for caret moves before they
        //could bubble here.
        if (Mode == ChatMode.WhisperName)
        {
            if (InputBuffer.WasKeyPressed(Keys.Up))
                CycleWhisperTarget(1);
            else if (InputBuffer.WasKeyPressed(Keys.Down))
                CycleWhisperTarget(-1);

            return;
        }

        //sent-line recall works in every message-typing mode (public/yell/whisper/guild/group/channel) off the one shared
        //history; WhisperName is the exception above (its Up/Down cycles the target instead).
        if (Mode is ChatMode.Normal or ChatMode.Shout or ChatMode.WhisperMessage)
        {
            if (InputBuffer.WasKeyPressed(Keys.Up))
                NavigateHistory(1);
            else if (InputBuffer.WasKeyPressed(Keys.Down))
                NavigateHistory(-1);
        }
    }
}