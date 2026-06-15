#region
using System.Diagnostics;
#endregion

namespace Chaos.Client.Utilities;

/// <summary>
///     Opens URLs in the player's default browser (shell-execute). Used by the lobby's Homepage/News buttons and the
///     GM-only /edit chat command.
/// </summary>
public static class Browser
{
    public static void Open(string url)
    {
        try
        {
            Process.Start(
                new ProcessStartInfo(url)
                {
                    UseShellExecute = true
                });
        } catch
        {
            //could not open a browser; nothing sensible to do
        }
    }
}
