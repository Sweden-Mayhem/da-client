#region
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Chaos.Client.Data;
using Chaos.Client.Systems;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client;

public static class GlobalSettings
{
    private static readonly string[] PreLoadedAssemblies = ["Chaos.Networking"];
    private static readonly Type[] PreInitializedStatics = [typeof(DataContext), typeof(MachineIdentity)];
    public static readonly SamplerState Sampler = SamplerState.PointClamp; //SamplerState.LinearClamp;
    private static ushort ClientVersion => 741;

    public const string CustomClientVersion = "0.11.0";

    // The folder the executable actually lives in. For a self-extracting single-file build,
    // AppContext.BaseDirectory is the temp extraction dir, so we use the real exe path instead;
    // the Data folder sits next to da-swm.exe.
    private static readonly string AppDir =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    // Sweden Mayhem connection details are hard-coded here. There is no config file to ship or
    // edit. The DA_* environment variables below exist only for local development (pointing the
    // client at a server on 127.0.0.1); they are never set on a player's machine.
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
