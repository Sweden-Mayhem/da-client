#region
using System.IO.Compression;
using System.Text;
using Chaos.Client.Collections;
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.Generic;
using Chaos.Client.Controls.LobbyLogin;
using Chaos.Client.Controls.World.Menu;
using Chaos.Client.Data;
using Chaos.Client.Networking;
using Chaos.Client.Networking.Definitions;
using Chaos.Client.Rendering;
using Chaos.Client.Systems;
using Chaos.Client.Utilities;
using Chaos.Cryptography;
using Chaos.DarkAges.Definitions;
using Chaos.Networking.Entities.Server;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Screens;

public sealed class LobbyLoginScreen : IScreen
{
    //the custom lobby theme, clicking Create swaps to the creation theme
    //at login the world's map-music packet fades this out and the map track in
    private const int LoginMusicId = SoundSystem.MusicLobbyLogin;

    private readonly bool ReturningFromWorld;

    //optional message shown once the lobby loads, such as why a silent reconnect gave up
    private readonly string? StartupMessage;
    private bool AwaitingCharFinalize;

    private uint? CachedNoticeCheckSum;
    private bool ChangingPassword;
    private CharacterCreationControl CharCreateControl = null!;

    //flow state
    private bool Connecting;
    private static bool AutoLoginAttempted;

    //connection status and auto-retry, while the server is unreachable we show a status line and keep retrying
    //buttons stay disabled until the lobby is usable so the screen never looks frozen
    private bool LobbyReady;
    private float RetryTimer;
    private UILabel ConnectionStatus = null!;
    private UILabel ConnectionStatusShadow = null!;
    private const float RetryDelaySeconds = 5f;

    //while not connected the lobby image darkens so it does not look interactable, brightening once connected
    //a Root child that sits below the status text
    private UIPanel DimOverlay = null!;
    private float DimAlpha;
    private bool DimInitialized;
    private const float DimMax = 0.55f;
    private const float DimRate = 3f;

    private bool CreatingCharacter;
    private LoginNoticeControl LoginNoticeControl = null!;

    private ChaosGame Game = null!;
    private string? HomepageUrl;
    private LoginControl LoginControl = null!;
    private PasswordChangeControl PasswordChangeControl = null!;
    private bool PendingWorldSwitch;
    private OkPopupMessageControl LobbyLoginPopupMessage = null!;
    private IReadOnlyList<ServerTableEntry> ServerList = [];
    private ServerSelectControl ServerSelectControl = null!;

    private UIButton? LastClickedButton;

    //ui panels
    private LobbyLoginControl StartPanel = null!;

    //the custom Options window opened from the lobby, a curated pre-login subset
    private OptionsWindow? OptionsWin;
    private ControlsWindow? ControlsWin;

    /// <inheritdoc />
    public UIPanel? Root { get; private set; }

    public LobbyLoginScreen(bool returningFromWorld = false, string? startupMessage = null)
    {
        ReturningFromWorld = returningFromWorld;
        StartupMessage = startupMessage;
    }

    /// <inheritdoc />
    public void Dispose() => Root?.Dispose();

    private const int VERSION_FONT = 14;
    private static readonly Color VersionTextColor = new Color(200, 200, 200, 160);
    private const int VERSION_PAD = 8;

    //the animated login glow, a Root child so it draws under every popup or window
    //driven each frame via LoginGlow.Held so it is suppressed while the notice is up or input is gated
    private LoginGlowControl LoginGlow = null!;

    public void Draw(SpriteBatchEx spriteBatch, GameTime gameTime)
    {
        spriteBatch.Begin(samplerState: GlobalSettings.Sampler);
        Root!.Draw(spriteBatch);
        spriteBatch.End();

        DebugOverlay.SnapshotDrawCount();
    }

    /// <inheritdoc />
    public void DrawNativeUi(SpriteBatchEx spriteBatch, GameTime gameTime)
    {
        if (!TtfTextRenderer.Available)
            return;

        spriteBatch.Begin(samplerState: GlobalSettings.Sampler);

        //paint every native label and text box in the lobby tree crisply at native resolution
        //the lobby lives in 640 virtual space, screen origin is the letterbox corner and scale is how much that rect is upscaled
        if (Root is not null)
        {
            var rect = ChaosGame.WorldDrawRect;
            var scale = (float)rect.Width / ChaosGame.VIRTUAL_WIDTH;

            //the native pass paints on top of the already-blitted lobby frame which includes any popup art
            //so skip the native text of any child below the highest visible blocking overlay, the overlay and status line still draw
            var blockZ = int.MinValue;

            foreach (var overlay in (UIElement?[]) [LobbyLoginPopupMessage, OptionsWin, ControlsWin])
                if (overlay is { Visible: true })
                    blockZ = Math.Max(blockZ, overlay.ZIndex);

            //the dim darkens the lobby art in place but the native text is painted here on top of the composited frame
            //so fade the native text by the same dim below the overlay, the status line sits above it and stays readable
            var dimFactor = 1f - DimAlpha;

            foreach (var child in Root.Children)
                if (child.Visible && (child.ZIndex >= blockZ))
                {
                    //a slide-fade dialog fades its art via a render target, so fade its native text at the same alpha
                    //to keep the two in lockstep, the slide follows the child's Y on its own
                    var childAlpha = (child as SlideFadePanel)?.OpenFraction ?? 1f;

                    if (child.ZIndex < DimOverlay.ZIndex)
                        childAlpha *= dimFactor;

                    ScaleHost.WalkNativeText(spriteBatch, child, rect.X, rect.Y, 0, 0, scale, childAlpha);
                }
        }

        var verTex = TtfTextRenderer.GetLine($"v{GlobalSettings.CustomClientVersion}", VERSION_FONT);

        if (verTex is not null)
            //the version line is plain lobby text too, fade it with the dim like everything else
            spriteBatch.Draw(verTex, new Vector2(ChaosGame.UiWidth - verTex.Width - VERSION_PAD, ChaosGame.UiHeight - verTex.Height - VERSION_PAD), VersionTextColor * (1f - DimAlpha));

        spriteBatch.End();
    }

    /// <inheritdoc />
    public void Initialize(ChaosGame game)
    {
        Game = game;

        //hand the connection our build version so it gets sent at login for the server's version gate
        Game.Connection.CustomClientVersion = GlobalSettings.CustomClientVersion;

        Game.Connection.StateChanged += OnConnectionStateChanged;
        Game.Connection.OnError += OnConnectionError;
        Game.Connection.OnServerTableReceived += OnServerTableReceived;
        Game.Connection.OnRedirectReceived += OnRedirectReceived;
        Game.Connection.OnLoginMessage += OnLoginMessage;
        Game.Connection.OnLoginNotice += OnLoginNotice;
        Game.Connection.OnLoginControl += OnLoginControlReceived;

        //the lobby theme plays only at the very start of the game on the first lobby entry
        //on a logout return we stop music instead, a silent lobby is fine and the world track does not linger
        if (ReturningFromWorld)
            Game.SoundSystem.PlayMusic(0);
        else
            Game.SoundSystem.PlayMusic(LoginMusicId);
    }

    /// <inheritdoc />
    public void LoadContent(GraphicsDevice graphicsDevice)
    {
        StartPanel = new LobbyLoginControl();
        LoginControl = new LoginControl();
        ServerSelectControl = new ServerSelectControl();
        LoginNoticeControl = new LoginNoticeControl();
        CharCreateControl = new CharacterCreationControl(Game.AislingRenderer);
        PasswordChangeControl = new PasswordChangeControl();

        //wire button events
        StartPanel.ContinueButton?.Clicked += OnContinueClicked;
        StartPanel.ExitButton?.Clicked += OnExitClicked;
        StartPanel.SubmitCreateButton?.Clicked += OnCreateClicked;
        StartPanel.PasswordButton?.Clicked += OnPasswordClicked;
        StartPanel.CreditButton?.Clicked += OnCreditClicked;
        StartPanel.HomepageButton?.Clicked += OnHomepageClicked;
        StartPanel.OptionsButton?.Clicked += OnOptionsClicked;
        StartPanel.NewsButton?.Clicked += OnNewsClicked;

        //track the last-clicked start panel button so Enter can repeat it
        foreach (var btn in (UIButton?[]) [
                     StartPanel.ContinueButton,
                     StartPanel.ExitButton,
                     StartPanel.SubmitCreateButton,
                     StartPanel.PasswordButton,
                     StartPanel.CreditButton,
                     StartPanel.HomepageButton,
                     StartPanel.OptionsButton,
                     StartPanel.NewsButton
                 ])
            if (btn is not null)
                btn.Clicked += () => LastClickedButton = btn;

        LoginControl.OkButton?.Clicked += OnLoginOkClicked;
        LoginControl.CancelButton?.Clicked += OnLoginCancelClicked;

        ServerSelectControl.OnServerSelected += OnServerSelected;

        LoginNoticeControl.OnOk += OnLoginAccepted;
        LoginNoticeControl.OnCancel += OnLoginCancelled;

        CharCreateControl.OnOk += OnCharCreateOkClicked;
        CharCreateControl.OnCancel += OnCharCreateCancelClicked;

        PasswordChangeControl.OnOk += OnPasswordChangeOkClicked;
        PasswordChangeControl.OnCancel += OnPasswordChangeCancelClicked;

        //the lobby renders into the 640x480 letterboxed target so its popup centers on that screen, not native
        LobbyLoginPopupMessage = new OkPopupMessageControl(legacyCenter: true)
        {
            ZIndex = 1,
            Name = "LobbyLoginPopupMessage"
        };
        LobbyLoginPopupMessage.OnOk += OnLobbyLoginPopupMessageOk;

        Root = new LobbyRootPanel
        {
            Name = "LobbyRoot",
            Width = ChaosGame.VIRTUAL_WIDTH,
            Height = ChaosGame.VIRTUAL_HEIGHT
        };
        Root.AddChild(StartPanel);

        //the login glow sits above the lobby art but below every popup
        //added right after StartPanel at ZIndex 0 so the z-sort leaves it under the later windows, driven via LoginGlow.Held
        LoginGlow = new LoginGlowControl(Game.EffectRenderer) { Name = "LoginGlow" };
        Root.AddChild(LoginGlow);

        Root.AddChild(LoginControl);
        Root.AddChild(ServerSelectControl);
        Root.AddChild(LoginNoticeControl);
        Root.AddChild(CharCreateControl);
        Root.AddChild(PasswordChangeControl);
        Root.AddChild(LobbyLoginPopupMessage);

        //connection status line at the bottom of the lobby so the screen never looks frozen when the server is down
        //a dark shadow label sits a pixel below for legibility over the busy art, both hidden once the lobby is usable
        ConnectionStatusShadow = new UILabel
        {
            Name = "ConnectionStatusShadow",
            X = 2,
            Y = 432,
            Width = ChaosGame.VIRTUAL_WIDTH,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            CustomFontSize = 14,
            RenderNative = true,
            ForegroundColor = new Color(14, 10, 6),
            IsHitTestVisible = false,
            Visible = false,
            ZIndex = 5
        };
        Root.AddChild(ConnectionStatusShadow);

        ConnectionStatus = new UILabel
        {
            Name = "ConnectionStatus",
            X = 0,
            Y = 430,
            Width = ChaosGame.VIRTUAL_WIDTH,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            CustomFontSize = 14,
            RenderNative = true,
            ForegroundColor = new Color(236, 224, 200),
            IsHitTestVisible = false,
            Visible = false,
            ZIndex = 6
        };
        Root.AddChild(ConnectionStatus);

        //dim overlay above the lobby art but below the status text so that line stays readable over the darkened lobby
        //alpha is driven each frame in Update, non-hit-testable so it never blocks input
        DimOverlay = new UIPanel
        {
            Name = "LobbyDim",
            X = 0,
            Y = 0,
            Width = ChaosGame.VIRTUAL_WIDTH,
            Height = ChaosGame.VIRTUAL_HEIGHT,
            BackgroundColor = Color.Black * 0f,
            IsHitTestVisible = false,
            Visible = false,
            ZIndex = 4
        };
        Root.AddChild(DimOverlay);

        //the lobby Options window, built from the same shared builder as the in-world one
        //compact drops the in-world-only rows so the menu fits the lobby, everything else still persists and applies in the world
        ControlsWin = new ControlsWindow { ZIndex = 3 };
        OptionsWin = OptionsWindow.Create(Game, ControlsWin, compact: true);
        OptionsWin.ZIndex = 2;
        OptionsWin.X = (ChaosGame.VIRTUAL_WIDTH - OptionsWin.Width) / 2;
        OptionsWin.Y = 60;

        Root.AddChild(ControlsWin);
        Root.AddChild(OptionsWin);

        //build the ui atlas after all login controls are constructed
        UiRenderer.Instance?.BuildAtlas();

        WireRootInputHandlers();

        if (ReturningFromWorld)
        {
            //already connected to the login server via redirect, skip the lobby handshake and show login directly
            StartPanel.SetButtonsEnabled(false);
            LoginControl.Show();
        } else

            //fresh start, connect to the lobby
            BeginLobbyConnect();

        //show a one-off message handed in by the caller, such as a silent reconnect that gave up with a login error
        if (!string.IsNullOrEmpty(StartupMessage))
            LobbyLoginPopupMessage.Show(StartupMessage);
    }

    /// <inheritdoc />
    public void UnloadContent()
    {
        Game.Connection.StateChanged -= OnConnectionStateChanged;
        Game.Connection.OnError -= OnConnectionError;
        Game.Connection.OnServerTableReceived -= OnServerTableReceived;
        Game.Connection.OnRedirectReceived -= OnRedirectReceived;
        Game.Connection.OnLoginMessage -= OnLoginMessage;
        Game.Connection.OnLoginNotice -= OnLoginNotice;
        Game.Connection.OnLoginControl -= OnLoginControlReceived;
    }

    /// <inheritdoc />
    public void Update(GameTime gameTime)
    {
        var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;

        if (PendingWorldSwitch)
        {
            PendingWorldSwitch = false;

            //switch to the world right away, SnapToBlack just guarantees full dark if a fast redirect beat the fade
            //the switch cannot wait for the fade or the world-entry map packets land before WorldScreen is subscribed
            if (Game.Connection.State == ConnectionState.World)
            {
                Game.SnapToBlack();
                Game.Screens.Switch(new WorldScreen());
            } else
            {
                //connection died during the handoff, restart the login flow and reveal the fresh lobby
                Game.Screens.Switch(new LobbyLoginScreen());
                Game.FadeFromBlack();
            }

            //the lobby has been replaced, do not run the rest of this dead instance's Update
            return;
        }

        //darken the lobby while not connected, bright once connected
        //suppressed during a redirect since that socket handoff is expected, snapped to target on the first frame
        var notConnected = Game.Connection.State is ConnectionState.Disconnected or ConnectionState.Connecting;
        var dimmed = notConnected && !Game.Connection.IsRedirecting;
        var dimTarget = dimmed ? DimMax : 0f;

        if (!DimInitialized)
        {
            DimAlpha = dimTarget;
            DimInitialized = true;
        } else if (DimAlpha < dimTarget)
            DimAlpha = Math.Min(dimTarget, DimAlpha + (DimRate * dt));
        else if (DimAlpha > dimTarget)
            DimAlpha = Math.Max(dimTarget, DimAlpha - (DimRate * dt));

        DimOverlay.BackgroundColor = Color.Black * DimAlpha;
        DimOverlay.Visible = DimAlpha > 0.002f;

        //while the server is unreachable, count down and retry while refreshing the status so the lobby never looks frozen
        //this idles once a retry is in flight or the lobby is usable
        if ((RetryTimer > 0f) && !Connecting && !LobbyReady && (Game.Connection.State == ConnectionState.Disconnected))
        {
            RetryTimer -= dt;

            if (RetryTimer <= 0f)
                BeginLobbyConnect();
            else
                SetStatus($"Cannot reach the server. Retrying in {(int)Math.Ceiling(RetryTimer)}s...");
        }

        //while dimmed the lobby is not interactable, drop any text-field focus and swallow input
        //so a player cannot type into or click a dead lobby, cleared the moment we reconnect
        if (dimmed)
        {
            UITextBox.Blur();
            Game.Dispatcher.ClearExplicitFocus();
        }

        //input is gated during a full fade so a stray click cannot fire mid-fade
        //and while dimmed so a disconnected lobby ignores all input
        if (!Game.IsFading && !dimmed)
            Game.Dispatcher.ProcessInput(Root!, gameTime);

        //the glow is clickable only once the notice is closed and the lobby is interactable
        //otherwise it is held hidden with its clock frozen, it still ticks via Root.Update
        LoginGlow.Held = (LoginNoticeControl is { Visible: true }) || Game.IsFading || dimmed;

        Root!.Update(gameTime);
    }

    private void WireRootInputHandlers() => ((LobbyRootPanel)Root!).Screen = this;

    #region Button Handlers
    private void OnContinueClicked()
    {
        if (Connecting || LoginControl.Visible || PasswordChangeControl.Visible)
            return;

        LoginControl.Show();
        StartPanel.SetButtonsEnabled(false);
    }

    private void OnExitClicked() => Game.Exit();

    private void OnCreateClicked()
    {
        if (Connecting || LoginControl.Visible || PasswordChangeControl.Visible)
            return;

        //start the creation theme now so the music swaps as the screen darkens and brightens
        //then fade through black and show the creation screen at full black
        Game.SoundSystem.PlayMusic(SoundSystem.MusicLobbyCreation);
        Game.FadeThroughBlack(() => CharCreateControl.Show());
    }

    private void OnCharCreateOkClicked()
    {
        var name = CharCreateControl.NameField?.Text;
        var password = CharCreateControl.PasswordField?.Text;
        var passwordConfirm = CharCreateControl.PasswordConfirmField?.Text;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(password))
        {
            LobbyLoginPopupMessage.Show("Name and password are required.");

            return;
        }

        if (password != passwordConfirm)
        {
            LobbyLoginPopupMessage.Show("Passwords do not match.");
            CharCreateControl.PasswordField?.Text = string.Empty;
            CharCreateControl.PasswordConfirmField?.Text = string.Empty;

            return;
        }

        Connecting = true;
        CreatingCharacter = true;
        AwaitingCharFinalize = false;
        Game.Connection.CreateCharInitial(name, password);
    }

    private void OnCharCreateCancelClicked()
    {
        CreatingCharacter = false;
        AwaitingCharFinalize = false;
        //fade through black back to the lobby, the creation theme keeps playing since the intro only triggers at game start
        Game.FadeThroughBlack(() => CharCreateControl.Hide());
    }

    private void OnPasswordClicked()
    {
        if (Connecting || LoginControl.Visible || PasswordChangeControl.Visible)
            return;

        PasswordChangeControl.Show();
        StartPanel.SetButtonsEnabled(false);
    }

    private void OnPasswordChangeOkClicked()
    {
        var name = PasswordChangeControl.NameField?.Text ?? string.Empty;
        var currentPassword = PasswordChangeControl.CurrentPasswordField?.Text ?? string.Empty;
        var newPassword = PasswordChangeControl.NewPasswordField?.Text ?? string.Empty;
        var confirmPassword = PasswordChangeControl.ConfirmPasswordField?.Text ?? string.Empty;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(currentPassword) || string.IsNullOrWhiteSpace(newPassword))
        {
            LobbyLoginPopupMessage.Show("All fields are required.");

            return;
        }

        if (newPassword != confirmPassword)
        {
            LobbyLoginPopupMessage.Show("New passwords do not match.");
            PasswordChangeControl.NewPasswordField?.Text = string.Empty;
            PasswordChangeControl.ConfirmPasswordField?.Text = string.Empty;

            return;
        }

        Connecting = true;
        ChangingPassword = true;
        Game.Connection.ChangePassword(name, currentPassword, newPassword);
    }

    private void OnPasswordChangeCancelClicked()
    {
        PasswordChangeControl.Hide();
        ChangingPassword = false;
        StartPanel.SetButtonsEnabled(true);
    }

    private void OnCreditClicked()
        => LobbyLoginPopupMessage.Show("Sweden Mayhem\nA Dark Ages world for friends.\nBy Hezkore.");

    private void OnHomepageClicked()
    {
        //the homepage url is sent by the server at login
        if (!string.IsNullOrWhiteSpace(HomepageUrl))
            OpenUrl(HomepageUrl);
    }

    private void OnNewsClicked() => OpenUrl("https://swedenmayhem.se/darkages/news");

    private void OnOptionsClicked() => OptionsWin?.Toggle();

    private static void OpenUrl(string url) => Browser.Open(url);

    private void OnLoginOkClicked()
    {
        var username = LoginControl.UsernameField?.Text ?? string.Empty;
        var password = LoginControl.PasswordField?.Text ?? string.Empty;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))

            //username and password are required
            return;

        Connecting = true;
        LoginControl.Visible = false;

        WorldState.PlayerName = username;

        Game.Connection.Login(
            username,
            password,
            MachineIdentity.ClientId1,
            MachineIdentity.ClientId2);
    }

    private void OnLoginCancelClicked()
    {
        LoginControl.Hide();
        StartPanel.SetButtonsEnabled(true);
    }

    //dev login flag, once the lobby is ready to take credentials fill them in and submit
    //guarded so it fires at most once per lobby screen, a fresh screen after a disconnect resets the guard
    private void MaybeAutoLogin()
    {
        if (!LaunchArgs.AutoLogin || AutoLoginAttempted)
            return;

        AutoLoginAttempted = true;

        LoginControl.UsernameField?.Text = LaunchArgs.LoginUser!;
        LoginControl.PasswordField?.Text = LaunchArgs.LoginPassword!;
        OnLoginOkClicked();
    }

    private void OnLobbyLoginPopupMessageOk() => LobbyLoginPopupMessage.Hide();

    private void OnServerSelected(byte serverId)
    {
        ServerSelectControl.Visible = false;

        var server = ServerList.FirstOrDefault(s => s.Id == serverId);

        if (server is not null)
            Game.Connection.ServerName = server.Name;

        Game.Connection.SelectServer(serverId);
    }
    #endregion

    #region Connection Flow
    private async void BeginLobbyConnect()
    {
        Connecting = true;
        SetStatus("Connecting to server...");

        await Game.Connection.ConnectToLobbyAsync(DataContext.LobbyHost, DataContext.LobbyPort, DataContext.ClientVersion);
    }

    //sets or clears the bottom-of-lobby status line and its shadow, null or empty hides both
    private void SetStatus(string? text)
    {
        if (ConnectionStatus is null)
            return;

        var value = text ?? string.Empty;
        var visible = !string.IsNullOrEmpty(text);

        ConnectionStatus.Text = value;
        ConnectionStatus.Visible = visible;
        ConnectionStatusShadow.Text = value;
        ConnectionStatusShadow.Visible = visible;
    }

    //the lobby is now usable, enable the buttons, clear the status and stop retrying
    private void MarkLobbyReady()
    {
        LobbyReady = true;
        Connecting = false;
        RetryTimer = 0f;
        SetStatus(null);
        StartPanel.EnableButtons();
    }

    //a connection was lost or never established
    //schedule a retry with a visible countdown so the lobby never looks frozen and reconnects on its own
    private void OnLobbyConnectFailed()
    {
        Connecting = false;

        //if the connection dropped after login-confirm started the fade to black, reveal the lobby again
        //so the player is not stranded staring at a held black screen
        Game.FadeFromBlack();

        //if we were usable and the server went down, stop treating the lobby as ready
        //disable the buttons and hide any open login or password dialog so we are back at the start screen for the retry
        if (LobbyReady)
        {
            LobbyReady = false;
            StartPanel.SetButtonsEnabled(false);
            LoginControl.Hide();
            PasswordChangeControl.Hide();
        }

        RetryTimer = RetryDelaySeconds;
        SetStatus($"Cannot reach the server. Retrying in {(int)Math.Ceiling(RetryTimer)}s...");
    }

    private void OnConnectionStateChanged(ConnectionState oldState, ConnectionState newState)
    {
        switch (newState)
        {
            case ConnectionState.Lobby:
                //connected and progressing, clear any earlier cannot-reach status and stop retrying
                RetryTimer = 0f;
                SetStatus("Connecting to server...");

                break;

            case ConnectionState.Login:
                Connecting = false;
                RetryTimer = 0f;
                SetStatus(null); //the login flow takes over, buttons enable in OnLoginNotice

                break;

            case ConnectionState.World:
                PendingWorldSwitch = true;

                break;

            //a real disconnect, not the expected one from a redirect which closes the old socket on purpose
            //either a failed connect or the server dropping after we were usable, both auto-retry
            case ConnectionState.Disconnected when !Game.Connection.IsRedirecting:
                OnLobbyConnectFailed();

                break;
        }
    }

    private void OnConnectionError(string error) => OnLobbyConnectFailed();

    private void OnServerTableReceived(ServerTableData data)
    {
        ServerList = data.Servers;

        if (data is { ShowServerList: true, Servers.Count: > 1 })
        {
            //let the player pick a server
            ServerSelectControl.SetServers(data.Servers);
            ServerSelectControl.Visible = true;
        } else if (data.Servers.Count > 0)
        {
            //auto-select the only server
            Game.Connection.ServerName = data.Servers[0].Name;
            Game.Connection.SelectServer(data.Servers[0].Id);
        } else
        {
            //no servers available
        }
    }

    private void OnRedirectReceived(RedirectInfo _)
    {
        //following the redirect
    }

    private void OnLoginMessage(LoginMessageArgs args)
    {
        if (CreatingCharacter)
        {
            HandleCharCreateMessage(args);

            return;
        }

        if (ChangingPassword)
        {
            HandlePasswordChangeMessage(args);

            return;
        }

        if (args.LoginMessageType == LoginMessageType.Confirm)
        {
            //login accepted, begin the fade to black now
            //the redirect and world entry follow over the network so we are already dark when PendingWorldSwitch fires
            Game.FadeToBlackHold(null);

            return;
        }

        //login failed, show login again for a retry and clear the password
        Connecting = false;
        LoginControl.Visible = true;

        if (LoginControl.PasswordField is not null)
        {
            LoginControl.PasswordField.Text = string.Empty;
            LoginControl.PasswordField.IsFocused = true;
        }

        LobbyLoginPopupMessage.Show(args.Message ?? "Login failed.");
    }

    private void HandleCharCreateMessage(LoginMessageArgs args)
    {
        if (args.LoginMessageType == LoginMessageType.Confirm)
        {
            if (!AwaitingCharFinalize)
            {
                //initial step confirmed, send finalize with the chosen appearance
                AwaitingCharFinalize = true;

                Game.Connection.CreateCharFinalize(
                    CharCreateControl.SelectedHairStyle,
                    CharCreateControl.SelectedGender,
                    CharCreateControl.SelectedHairColor);
            } else
            {
                //finalize confirmed and the character is created, fade through black to the lobby and show the popup
                //the creation theme keeps playing since the intro only triggers at the start of the game
                Connecting = false;
                CreatingCharacter = false;
                AwaitingCharFinalize = false;
                Game.FadeThroughBlack(() =>
                {
                    CharCreateControl.Hide();
                    LobbyLoginPopupMessage.Show("Character has been created. Choose \"CONTINUE\".");
                });
            }

            return;
        }

        //creation failed, show the error popup and clear the relevant field
        Connecting = false;
        AwaitingCharFinalize = false;

        switch (args.LoginMessageType)
        {
            case LoginMessageType.ClearNameMessage:
                CharCreateControl.NameField?.Text = string.Empty;

                break;
            case LoginMessageType.ClearPswdMessage:
                CharCreateControl.PasswordField?.Text = string.Empty;
                CharCreateControl.PasswordConfirmField?.Text = string.Empty;

                break;
            case LoginMessageType.Confirm:
                break;
            case LoginMessageType.CharacterDoesntExist:
                break;
            case LoginMessageType.WrongPassword:
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        LobbyLoginPopupMessage.Show(args.Message ?? "Character creation failed.");
    }

    private void HandlePasswordChangeMessage(LoginMessageArgs args)
    {
        Connecting = false;
        ChangingPassword = false;

        if (args.LoginMessageType == LoginMessageType.Confirm)
        {
            PasswordChangeControl.Hide();
            LobbyLoginPopupMessage.Show("Password has been changed.");

            return;
        }

        LobbyLoginPopupMessage.Show(args.Message ?? "Password change failed.");
    }

    private void OnLoginNotice(LoginNoticeArgs args)
    {
        //returning from world, already accepted the notice this session so skip it
        if (ReturningFromWorld)
        {
            MarkLobbyReady();
            MaybeAutoLogin();

            return;
        }

        if (!args.IsFullResponse)
        {
            //checksum-only probe, request the full notice when we have no cached match
            if (CachedNoticeCheckSum.HasValue && (CachedNoticeCheckSum.Value == args.CheckSum))
            {
                //already accepted this notice, skip the display and enable the buttons
                MarkLobbyReady();
                MaybeAutoLogin();

                return;
            }

            Game.Connection.RequestNotice();

            return;
        }

        //full response, decompress and display it
        if (args.Data is null or { Length: 0 })
            return;

        var noticeText = DecompressNotice(args.Data);

        //auto-login accepts the notice on our behalf instead of waiting for a click
        if (LaunchArgs.AutoLogin)
        {
            OnLoginAccepted();

            return;
        }

        LoginNoticeControl.Show(noticeText);
    }

    private void OnLoginAccepted()
    {
        LoginNoticeControl.Hide();
        MarkLobbyReady();

        //default the Enter-repeat to Continue so pressing Enter right after the notice opens the login dialog
        //the Enter that accepted the notice is handled by the notice control so it does not fire here
        if (StartPanel.ContinueButton is not null)
            LastClickedButton = StartPanel.ContinueButton;

        MaybeAutoLogin();
    }

    private void OnLoginCancelled() => Game.Exit();

    private string DecompressNotice(byte[] compressedData)
    {
        using var compressed = new MemoryStream(compressedData);
        using var decompressor = new ZLibStream(compressed, CompressionMode.Decompress);
        using var decompressed = new MemoryStream();
        decompressor.CopyTo(decompressed);

        var rawBytes = decompressed.ToArray();
        CachedNoticeCheckSum = Crc.Generate32(rawBytes);

        return Encoding.GetEncoding(949)
                       .GetString(rawBytes);
    }

    private void OnLoginControlReceived(LoginControlArgs args)
    {
        if (args.LoginControlsType == LoginControlsType.Homepage)
            HomepageUrl = args.Message;
    }
    #endregion

    /// <summary>
    ///     Root panel for the lobby, handles Enter-to-repeat and Escape to dismiss server select when nothing else has focus
    /// </summary>
    //INativeTextRoot, the lobby renders into the letterbox target then upscales
    //so its native labels are suppressed in place and painted crisp by the DrawNativeUi pass instead
    private sealed class LobbyRootPanel : UIPanel, INativeTextRoot
    {
        public LobbyLoginScreen? Screen { get; set; }

        public override void OnKeyDown(KeyDownEvent e)
        {
            if (Screen is null)
                return;

            //Alt+Enter fullscreen is handled globally by ChaosGame, doing it here too would toggle it twice
            //so we leave it alone in the lobby

            //Enter repeats the last-clicked button when no sub-control is open
            if ((e.Key == Keys.Enter)
                && Screen.LastClickedButton is { Enabled: true }
                && !Screen.LoginControl.Visible
                && !Screen.ServerSelectControl.Visible
                && !Screen.CharCreateControl.Visible
                && !Screen.PasswordChangeControl.Visible)
            {
                Screen.LastClickedButton.PerformClick();
                e.Handled = true;

                return;
            }

            //Escape dismisses the server select when it is visible and nothing else claims focus
            if ((e.Key == Keys.Escape) && Screen.ServerSelectControl.Visible)
            {
                Screen.ServerSelectControl.Visible = false;
                e.Handled = true;
            }
        }
    }
}