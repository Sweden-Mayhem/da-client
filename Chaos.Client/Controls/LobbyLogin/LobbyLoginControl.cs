#region
using System.Reflection;
using Chaos.Client.Controls.Components;
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

        Background = LoadBackground();

        //left column (icon + plate), measured against the art and scaled to 640x480
        //rows sit at ~41px pitch, 32px tall to cover each wooden plate
        SubmitCreateButton = AddHotspot("Create", 29, 155, 138, 32);
        ContinueButton = AddHotspot("Continue", 29, 197, 138, 32);
        PasswordButton = AddHotspot("Password", 29, 241, 138, 32);
        ExitButton = AddHotspot("Exit", 29, 286, 138, 32);

        //right column (same rows)
        CreditButton = AddHotspot("Credit", 462, 155, 135, 32);
        HomepageButton = AddHotspot("Homepage", 462, 197, 135, 32);
        OptionsButton = AddHotspot("Options", 462, 241, 135, 32);
        NewsButton = AddHotspot("News", 462, 286, 135, 32);

        SetButtonsEnabled(false);
    }

    private HotspotButton AddHotspot(string name, int x, int y, int width, int height)
    {
        var button = new HotspotButton
        {
            Name = name,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Enabled = false
        };

        AddChild(button);

        return button;
    }

    private static Texture2D LoadBackground()
    {
        var assembly = Assembly.GetExecutingAssembly();

        using var stream = assembly.GetManifestResourceStream("da_login.png")
                           ?? throw new InvalidOperationException("Embedded resource 'da_login.png' not found");

        return Texture2D.FromStream(ChaosGame.Device, stream);
    }

    public void EnableButtons() => SetButtonsEnabled(true);

    public void SetButtonsEnabled(bool enabled)
    {
        foreach (var child in Children)
            child.Enabled = enabled;
    }
}
