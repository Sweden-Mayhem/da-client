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
	private readonly Effect BackgroundEffect;
	private readonly EffectParameter BackgroundEffectTime;

	private readonly Texture2D WaterMask;
	private readonly Texture2D ButtonMaskPress;
	private readonly Texture2D ButtonMaskHover;

    public readonly UIButton SubmitCreateButton;
    public readonly UIButton ContinueButton;
    public readonly UIButton PasswordButton;
    public readonly UIButton ExitButton;
    public readonly UIButton CreditButton;
    public readonly UIButton HomepageButton;
    public readonly UIButton OptionsButton;
    public readonly UIButton NewsButton;

    public readonly UIAnimatedImage Campfire;

    public LobbyLoginControl()
    {
        Name = "StartScreen";
        X = 0;
        Y = 0;
        Width = ChaosGame.VIRTUAL_WIDTH;
        Height = ChaosGame.VIRTUAL_HEIGHT;

        Background = LoadTexture("da_login.png");
        WaterMask = LoadTexture("da_login_water_mask.png");
        Campfire = UIAnimatedImage.CreateWithTexture("Campfire", LoadTexture("da_login_campfire.png"), 104);
        Campfire.X = 254;
        Campfire.Y = 357;
        Campfire.FrameTime = 1000*5/60;
        Campfire.IsHitTestVisible = false;
        AddChild(Campfire);

        BackgroundEffect = LoadEffect("loginBackground.mgfxo");
        BackgroundEffectTime = BackgroundEffect.Parameters["Time"];
        BackgroundEffect.Parameters["WaterMaskTexture"].SetValue(WaterMask);

        var buttonHotspotArea = LoadTexture("da_login_button_mask.png");
        ButtonMaskPress = ImageUtil.BuildButtonMaskPressTint(ChaosGame.Device, buttonHotspotArea);
        ButtonMaskHover = ImageUtil.BuildButtonMaskHoverTint(ChaosGame.Device, buttonHotspotArea);
        buttonHotspotArea.Dispose();

        //left column (icon + plate), measured against the art and scaled to 640x480
        SubmitCreateButton = AddHotspot("Create", 31, 155-3, ButtonMaskPress, ButtonMaskHover);
        ContinueButton = AddHotspot("Continue", 31, 197-3, ButtonMaskPress, ButtonMaskHover);
        PasswordButton = AddHotspot("Password", 31, 241-3, ButtonMaskPress, ButtonMaskHover);
        ExitButton = AddHotspot("Exit", 31, 285-3, ButtonMaskPress, ButtonMaskHover);

        //right column (same rows)
        CreditButton = AddHotspot("Credit", 465, 155-3, ButtonMaskPress, ButtonMaskHover);
        HomepageButton = AddHotspot("Homepage", 465, 197-3, ButtonMaskPress, ButtonMaskHover);
        OptionsButton = AddHotspot("Options", 465, 241-3, ButtonMaskPress, ButtonMaskHover);
        NewsButton = AddHotspot("News", 465, 285-3, ButtonMaskPress, ButtonMaskHover);

        SetButtonsEnabled(false);
    }

    private UIButton AddHotspot(string name, int x, int y, Texture2D press, Texture2D hover)
    {
        var button = UIButton.CreateWithTexture(name, null, press, hover);
        button.X = x;
        button.Y = y;
        button.Enabled = false;

        AddChild(button);

        return button;
    }

    private static Texture2D LoadTexture(String path)
    {
        var assembly = Assembly.GetExecutingAssembly();

        using var stream = assembly.GetManifestResourceStream(path) ?? throw new InvalidOperationException($"Embedded resource '{path}' not found");

        return Texture2D.FromStream(ChaosGame.Device, stream);
    }

    private static Effect LoadEffect(String path)
    {
        var assembly = Assembly.GetExecutingAssembly();

        using var stream = assembly.GetManifestResourceStream(path) ?? throw new InvalidOperationException($"Embedded resource '{path}' not found");
        using var fxBuffer = new MemoryStream();
        stream.CopyTo(fxBuffer);

        return new Effect(ChaosGame.Device, fxBuffer.ToArray());
    }

    public void EnableButtons() => SetButtonsEnabled(true);

    public void SetButtonsEnabled(bool enabled)
    {
        foreach (var child in Children)
            child.Enabled = enabled;
    }

    public override void Update(GameTime gameTime)
    {
        if (!Visible || !Enabled)
            return;

        BackgroundEffectTime.SetValue((float)gameTime.TotalGameTime.TotalSeconds);

        base.Update(gameTime);
    }

    public override void Draw(SpriteBatch spriteBatch)
    {
        if (!Visible)
            return;

        spriteBatch.End();
        spriteBatch.Begin(samplerState: GlobalSettings.Sampler, effect: BackgroundEffect);

        base.Draw(spriteBatch);

        spriteBatch.End();
        spriteBatch.Begin(samplerState: GlobalSettings.Sampler);
    }
}
