#region
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Chaos.Client.Data;
using Chaos.Client.Systems;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client;

/// <summary>
///     Static configuration for the client: version, data path, lobby host/port, and sampler state. Triggers all one-time
///     initialization (encoding providers, data archives, text colors) via the static constructor.
/// </summary>
public static class GlobalSettings
{
    private static readonly string[] PreLoadedAssemblies = ["Chaos.Networking"];
    private static readonly Type[] PreInitializedStatics = [typeof(DataContext), typeof(MachineIdentity)];
    public static readonly SamplerState Sampler = SamplerState.PointClamp; //SamplerState.LinearClamp;
    private static ushort ClientVersion => 741;

    /// <summary>Build version sent to the server at login so it can reject out-of-date clients.</summary>
    public const string CustomClientVersion = "0.9.1";

    // the folder the executable lives in; for a self-extracting build AppContext.BaseDirectory is the temp
    // extraction dir, so we use the real exe path and look for the Data folder next to da-swm.exe
    private static readonly string AppDir =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    // Connection details are hard-coded here; there is no config file to ship or edit
    // The DA_* environment variables exist only for local development and are never set on a player's machine
    private const string ServerHost = "darkages.swedenmayhem.se";
    private const int ServerPort = 4200;

    public static string DataPath
    {
        get
        {
            var p = Environment.GetEnvironmentVariable("DA_PATH");

            if (string.IsNullOrEmpty(p))
                p = "Data"; // bundled alongside the client

            return Path.IsPathRooted(p) ? p : Path.Combine(AppDir, p);
        }
    }

    public static string LobbyHost
        => Environment.GetEnvironmentVariable("DA_LOBBY_HOST") ?? ServerHost;

    public static int LobbyPort
        => int.TryParse(Environment.GetEnvironmentVariable("DA_LOBBY_PORT"), out var env) ? env : ServerPort;

    /// <summary>
    ///     When true, walking onto a water tile requires either the GM flag or the "Swimming" skill.
    ///     When false (default), any character can swim freely and pathfinding routes through water tiles.
    /// </summary>
    public static bool RequireSwimmingSkill => false;

    static GlobalSettings() => InitializeOthers();

    private static void InitializeOthers()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        DataContext.Initialize(
            ClientVersion,
            DataPath,
            LobbyHost,
            LobbyPort);

        LegendColors.Initialize();
        TextColors.Initialize();

        foreach (var name in PreLoadedAssemblies)
            Assembly.Load(name);

        foreach (var type in PreInitializedStatics)
            RuntimeHelpers.RunClassConstructor(type.TypeHandle);
    }
}
