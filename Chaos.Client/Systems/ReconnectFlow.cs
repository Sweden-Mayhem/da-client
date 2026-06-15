#region
using Chaos.Client.Networking;
using Chaos.Client.Networking.Definitions;
using Chaos.DarkAges.Definitions;
using Chaos.Networking.Entities.Server;
#endregion

namespace Chaos.Client.Systems;

/// <summary>
///     Drives a silent reconnect after an unexpected world disconnect. It replays the normal lobby -> login -> world
///     handshake using the session's stored credentials, auto-selecting the only server and skipping the EULA we
///     already accepted this session, retrying every <see cref="RetryIntervalSeconds" /> and giving up after
///     <see cref="GiveUpSeconds" />. It owns NO UI, the <c>WorldScreen</c> shows the "Reconnecting..." overlay and reacts
///     to <see cref="Succeeded" /> / <see cref="GaveUp" />.
///     The flow mirrors the proven <c>LobbyLoginScreen</c> sequence; it just runs headlessly behind the frozen world.
/// </summary>
public sealed class ReconnectFlow
{
    /// <summary>Seconds to wait between connect attempts.</summary>
    public const float RetryIntervalSeconds = 3f;

    /// <summary>Total seconds to keep trying before giving up to the lobby.</summary>
    public const float GiveUpSeconds = 30f;

    private enum Phase
    {
        Attempting,
        WaitingRetry,
        Done
    }

    private readonly ConnectionManager Connection;
    private readonly string Host;
    private readonly int Port;
    private readonly ushort Version;

    private Phase State;
    private float Elapsed;        //total time since Begin
    private float RetryTimer;     //counts down while WaitingRetry
    private bool LoginSubmitted;  //per-attempt guard so we submit the login exactly once

    /// <summary>Fired when the reconnect reaches the world again. The handler reloads the world screen.</summary>
    public event Action? Succeeded;

    /// <summary>
    ///     Fired when the reconnect gives up (total timeout, the server actively rejected the login, or the player
    ///     cancelled). The argument is a message to surface on the lobby, or <see langword="null" /> when there is
    ///     nothing worth showing (the lobby's own "Cannot reach the server" line already covers a plain timeout).
    /// </summary>
    public event Action<string?>? GaveUp;

    public ReconnectFlow(
        ConnectionManager connection,
        string host,
        int port,
        ushort version)
    {
        Connection = connection;
        Host = host;
        Port = port;
        Version = version;
    }

    /// <summary>Starts the first connect attempt immediately and subscribes to the connection events it needs.</summary>
    public void Begin()
    {
        Elapsed = 0f;
        State = Phase.Attempting;

        Connection.OnServerTableReceived += OnServerTable;
        Connection.OnLoginNotice += OnLoginNotice;
        Connection.OnLoginMessage += OnLoginMessage;
        Connection.OnError += OnError;
        Connection.StateChanged += OnStateChanged;

        StartAttempt();
    }

    /// <summary>Advances the retry / give-up timers. Drive this once per frame while the flow is active.</summary>
    public void Update(float dt)
    {
        if (State == Phase.Done)
            return;

        Elapsed += dt;

        if (Elapsed >= GiveUpSeconds)
        {
            //plain timeout, the lobby shows its own "Cannot reach the server. Retrying..." line, so no popup message
            Finish(false, null);

            return;
        }

        if (State != Phase.WaitingRetry)
            return;

        RetryTimer -= dt;

        if (RetryTimer <= 0f)
        {
            State = Phase.Attempting;
            StartAttempt();
        }
    }

    /// <summary>The player asked to stop waiting and drop to the lobby now.</summary>
    public void Cancel() => Finish(false, null);

    /// <summary>
    ///     Silently detaches from the connection without firing <see cref="Succeeded" /> / <see cref="GaveUp" />.
    ///     Used when the owning screen is torn down so a dead flow can't keep reacting to connection events.
    /// </summary>
    public void Abort()
    {
        if (State == Phase.Done)
            return;

        State = Phase.Done;
        Unsubscribe();
    }

    private void StartAttempt()
    {
        LoginSubmitted = false;

        //ConnectToLobbyAsync handles its own failures (sets Disconnected + fires OnError) so this never throws
        _ = Connection.ConnectToLobbyAsync(Host, Port, Version);
    }

    private void OnServerTable(ServerTableData data)
    {
        if (State != Phase.Attempting)
            return;

        //auto-select the first/only server (there is exactly one), mirroring LobbyLoginScreen
        if (data.Servers.Count > 0)
        {
            Connection.ServerName = data.Servers[0].Name;
            Connection.SelectServer(data.Servers[0].Id);
        }
    }

    private void OnLoginNotice(LoginNoticeArgs _)
    {
        //the notice arrives in the Login state, the same point a fresh login submits. We already accepted the EULA this
        //session, so skip it and re-send the stored credentials, once per attempt
        if ((State != Phase.Attempting) || LoginSubmitted)
            return;

        LoginSubmitted = true;
        Connection.ReLogin();
    }

    private void OnLoginMessage(LoginMessageArgs args)
    {
        if (State == Phase.Done)
            return;

        //Confirm means the manager redirects us to the world, OnStateChanged(World) will fire Succeeded
        if (args.LoginMessageType == LoginMessageType.Confirm)
            return;

        //the server actively rejected the login (bad password, character problem). Retrying the same credentials
        //won't help, drop to the lobby with the server's message so the player can see why
        Finish(false, args.Message ?? "Login failed.");
    }

    private void OnError(string _) => ScheduleRetry();

    private void OnStateChanged(ConnectionState _, ConnectionState newState)
    {
        if (State == Phase.Done)
            return;

        if (newState == ConnectionState.World)
        {
            Finish(true, null);

            return;
        }

        //a connect attempt died before reaching the world. The brief disconnect WHILE redirecting (lobby -> login ->
        //world closes the old socket on purpose) is expected, not a failure, guarded by IsRedirecting
        if ((newState == ConnectionState.Disconnected) && !Connection.IsRedirecting)
            ScheduleRetry();
    }

    private void ScheduleRetry()
    {
        //OnError and the Disconnected state change can both fire for one failed attempt, only the first transitions
        if (State != Phase.Attempting)
            return;

        State = Phase.WaitingRetry;
        RetryTimer = RetryIntervalSeconds;
    }

    private void Finish(bool success, string? message)
    {
        if (State == Phase.Done)
            return;

        State = Phase.Done;
        Unsubscribe();

        if (success)
            Succeeded?.Invoke();
        else
            GaveUp?.Invoke(message);
    }

    private void Unsubscribe()
    {
        Connection.OnServerTableReceived -= OnServerTable;
        Connection.OnLoginNotice -= OnLoginNotice;
        Connection.OnLoginMessage -= OnLoginMessage;
        Connection.OnError -= OnError;
        Connection.StateChanged -= OnStateChanged;
    }
}
