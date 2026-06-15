#region
using System.Globalization;
#endregion

namespace Chaos.Client;

/// <summary>
///     Dev only command line flags, parsed once in <c>Program.cs</c> before the game is built.
///     They are not a shipped feature, they let the client be driven headlessly for testing.
///     <list type="bullet">
///         <item><c>--login USER PASS</c> auto-fills and submits the login as soon as the lobby reaches the login state
///         (and re-tries on every fresh lobby connect, so a server restart that disconnects us reconnects on its own).</item>
///         <item><c>--screenoutput PATH SECONDS</c> overwrites PATH with a PNG of the current composited frame (world +
///         UI) every SECONDS, so an external tool can watch one stable file to see what is on screen.</item>
///     </list>
///     There is no security here on purpose, the password is passed in the clear on the command line.
/// </summary>
public static class LaunchArgs
{
    public static string? LoginUser { get; private set; }
    public static string? LoginPassword { get; private set; }
    public static string? ScreenOutputPath { get; private set; }
    public static float ScreenOutputInterval { get; private set; }

    public static bool AutoLogin => !string.IsNullOrEmpty(LoginUser) && !string.IsNullOrEmpty(LoginPassword);
    public static bool ScreenOutput => !string.IsNullOrEmpty(ScreenOutputPath) && (ScreenOutputInterval > 0f);

    public static void Parse(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--login" when i + 2 < args.Length:
                    LoginUser = args[i + 1];
                    LoginPassword = args[i + 2];
                    i += 2;

                    break;

                case "--screenoutput" when i + 2 < args.Length:
                    ScreenOutputPath = args[i + 1];

                    if (float.TryParse(args[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out var secs) && (secs > 0f))
                        ScreenOutputInterval = secs;

                    i += 2;

                    break;
            }
    }
}
