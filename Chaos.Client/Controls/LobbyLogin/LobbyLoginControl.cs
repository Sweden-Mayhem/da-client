#region
using System.Reflection;
using Chaos.Client.Controls.Components;
using Chaos.Client.Rendering.Utility;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.LobbyLogin;

/// <summary>
///     The custom start screen. Unlike the retail "_nstart" prefab, the whole layout (background plus
///     the eight buttons) is a single painted image (da_login.png, baked into the exe). This panel draws that
///     image full-screen and overlays a transparent <see cref="HotspotButton" /> on each drawn button, sized
///     and positioned to match the artwork (640x480 virtual space). The screen wires each one to its action.
/// </summary>
public sealed class LobbyLoginControl : UIPanel
{
	private readonly Texture2D ButtonMaskHover;
	private readonly Texture2D ButtonMaskPress;

    public HotspotButton? SubmitCreateButton { get; }
    public HotspotButton? ContinueButton { get; }
    public HotspotButton? PasswordButton { get; }
    public HotspotButton? ExitButton { get; }
    public HotspotButton? CreditButton { get; }
    public HotspotButton? HomepageButton { get; }
    public HotspotButton? OptionsButton { get; }
    public HotspotButton? NewsButton { get; }

    public LobbyLoginControl()
    {
        Name = "StartScreen";
        X = 0;
        Y = 0;
        Width = ChaosGame.VIRTUAL_WIDTH;
        Height = ChaosGame.VIRTUAL_HEIGHT;

        Background = LoadTexture("da_login.png");

        var buttonHotspotArea = LoadTexture("da_login_button_mask.png");
        ButtonMaskHover = ImageUtil.BuildButtonMaskHoverTint(ChaosGame.Device, buttonHotspotArea);
        ButtonMaskPress = ImageUtil.BuildButtonMaskPressTint(ChaosGame.Device, buttonHotspotArea);

        //left column (icon + plate), measured against the art and scaled to 640x480
        SubmitCreateButton = AddHotspot("Create", 31, 155-3, ButtonMaskHover, ButtonMaskPress);
        ContinueButton = AddHotspot("Continue", 31, 197-3, ButtonMaskHover, ButtonMaskPress);
        PasswordButton = AddHotspot("Password", 31, 241-3, ButtonMaskHover, ButtonMaskPress);
        ExitButton = AddHotspot("Exit", 31, 285-3, ButtonMaskHover, ButtonMaskPress);

        //right column (same rows)
        CreditButton = AddHotspot("Credit", 465, 155-3, ButtonMaskHover, ButtonMaskPress);
        HomepageButton = AddHotspot("Homepage", 465, 197-3, ButtonMaskHover, ButtonMaskPress);
        OptionsButton = AddHotspot("Options", 465, 241-3, ButtonMaskHover, ButtonMaskPress);
        NewsButton = AddHotspot("News", 465, 285-3, ButtonMaskHover, ButtonMaskPress);

        SetButtonsEnabled(false);
    }

    private HotspotButton AddHotspot(string name, int x, int y, Texture2D hover, Texture2D press)
    {
        var button = new HotspotButton
        {
            Name = name,
            X = x,
            Y = y,
            Width = hover.Width,
            Height = hover.Height,
            HoverTexture = hover,
            PressedTexture = press,
            Enabled = false
        };

        AddChild(button);

        return button;
    }

    private static Texture2D LoadTexture(String path)
    {
        var assembly = Assembly.GetExecutingAssembly();

        using var stream = assembly.GetManifestResourceStream(path) ?? throw new InvalidOperationException($"Embedded resource '{path}' not found");

        return Texture2D.FromStream(ChaosGame.Device, stream);
    }

    public void EnableButtons() => SetButtonsEnabled(true);

    public void SetButtonsEnabled(bool enabled)
    {
        foreach (var child in Children)
            child.Enabled = enabled;
    }
}
