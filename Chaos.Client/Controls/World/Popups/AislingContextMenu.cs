#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.World.Menu;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Controls.World.Popups;

/// <summary>
///     A popup menu that appears when clicking a non-self aisling. Styled like the top-left "Menu" dropdown (dark/bronze
///     rows in the optional TrueType font): a non-clickable header row showing the player's name, then three clickable
///     options (Profile, Group Request, Whisper).
/// </summary>
public sealed class AislingContextMenu : UIPanel
{
    private const int ITEM_H = 22;
    private const int WIDTH = 150;

    private static readonly Color HeaderBg = new Color(27, 23, 16) * 0.97f;
    private static readonly Color HeaderText = new(214, 196, 150);
    private static readonly Color BorderCol = new(88, 72, 46);

    private static readonly string[] OPTION_LABELS =
    [
        "Profile",
        "Group Invite",
        "Whisper"
    ];

    private readonly UILabel NameLabel;
    private readonly Action[] OptionCallbacks = new Action[3];

    public AislingContextMenu()
    {
        Visible = false;
        UsesControlStack = true;
        Width = WIDTH;
        Height = ITEM_H * (OPTION_LABELS.Length + 1);

        //header row: a darker, display-only band with the player's name (matches the Menu button's header look)
        var header = new UIPanel
        {
            Name = "Header",
            X = 0,
            Y = 0,
            Width = WIDTH,
            Height = ITEM_H,
            BackgroundColor = HeaderBg,
            BorderColor = BorderCol,
            IsHitTestVisible = false
        };

        NameLabel = new UILabel
        {
            Name = "Name",
            X = 8,
            Y = 0,
            Width = WIDTH - 12,
            Height = ITEM_H,
            CustomFontSize = 16,
            ForegroundColor = HeaderText,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false,
            TruncateWithEllipsis = true
        };

        header.AddChild(NameLabel);
        AddChild(header);

        for (var i = 0; i < OPTION_LABELS.Length; i++)
        {
            var index = i;

            var item = new MenuItem(OPTION_LABELS[i], WIDTH, ITEM_H)
            {
                X = 0,
                Y = (i + 1) * ITEM_H
            };

            item.Activated += () =>
            {
                OptionCallbacks[index]?.Invoke();
                Hide();
            };

            AddChild(item);
        }
    }

    public void Hide()
    {
        InputDispatcher.Instance?.RemoveControl(this);
        Visible = false;
    }

    public void Show(
        int screenX,
        int screenY,
        string name,
        Action onProfile,
        Action onGroupRequest,
        Action onWhisper)
    {
        NameLabel.Text = name;
        OptionCallbacks[0] = onProfile;
        OptionCallbacks[1] = onGroupRequest;
        OptionCallbacks[2] = onWhisper;

        X = screenX;
        Y = screenY;

        //clamp to the native UI bounds (this is a Root child drawn in native coordinates, not the 640x480 virtual space)
        if ((X + Width) > ChaosGame.UiWidth)
            X = ChaosGame.UiWidth - Width;

        if ((Y + Height) > ChaosGame.UiHeight)
            Y = ChaosGame.UiHeight - Height;

        X = Math.Max(0, X);
        Y = Math.Max(0, Y);

        InputDispatcher.Instance?.PushControl(this);
        Visible = true;
    }

    public override void OnKeyDown(KeyDownEvent e)
    {
        if (e.Key == Keys.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    //a click that lands on the menu but not on an option row (the header band, or empty margin) dismisses it; option
    //rows are deeper children and swallow their own clicks before they reach here
    public override void OnClick(ClickEvent e)
    {
        Hide();
        e.Handled = true;
    }
}
