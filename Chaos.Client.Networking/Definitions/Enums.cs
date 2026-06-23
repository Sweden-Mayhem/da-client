namespace Chaos.Client.Networking.Definitions;

/// <summary>
///     Represents the current phase of the client's connection to the server.
/// </summary>
public enum ConnectionState
{
    /// <summary>
    ///     Not connected to any server.
    /// </summary>
    Disconnected,

    /// <summary>
    ///     TCP connection is being established.
    /// </summary>
    Connecting,

    /// <summary>
    ///     Connected to the lobby server, performing handshake (Version, ConnectionInfo, ServerTable).
    /// </summary>
    Lobby,

    /// <summary>
    ///     Connected to the login server, awaiting authentication.
    /// </summary>
    Login,

    /// <summary>
    ///     Connected to the world server, game is active.
    /// </summary>
    World
}

/// <summary>
///     Custom protocol extensions layered on top of the upstream Chaos.Networking wire format.
/// </summary>
public static class SwmProtocol
{
    /// <summary>
    ///     Synthetic MenuType for the "craft menu", a standard ShowItems payload followed by a
    ///     trailing marker byte (see SwmDisplayMenuConverter). It never appears on the wire as a type byte.
    ///     The converter remaps marked ShowItems menus to this value so the UI can render them with the
    ///     learn-menu list panel (item icons) instead of the shop panel.
    /// </summary>
    public const Chaos.DarkAges.Definitions.MenuType CraftMenu = (Chaos.DarkAges.Definitions.MenuType)250;

    /// <summary>
    ///     Synthetic MenuType for the bespoke Temuair Exchange market window: a ShowItems payload followed by a trailing
    ///     marker byte (2). The converter remaps marked ShowItems menus to this value so the UI renders the custom market
    ///     window (centered, searchable, icon + price) instead of the shop panel.
    /// </summary>
    public const Chaos.DarkAges.Definitions.MenuType Market = (Chaos.DarkAges.Definitions.MenuType)251;
}

[Flags]
public enum WorldEntryState : byte
{
    None = 0,
    UserId = 1 << 0,
    MapInfo = 1 << 1,
    MapLoaded = 1 << 2,
    MapChangeComplete = 1 << 3,
    Location = 1 << 4,
    Attributes = 1 << 5,

    AllRequired = UserId | MapInfo | MapLoaded | MapChangeComplete | Location | Attributes
}