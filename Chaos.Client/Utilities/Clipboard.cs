namespace Chaos.Client.Utilities;

/// <summary>
///     System clipboard access via SDL2 (<see cref="Sdl" />), the same clipboard the SDL window participates in, so
///     copy/paste works on the actual runtime (Windows under Wine / X11 / Wayland) and stays in sync with the OS
///     selection. The text controls call this for Ctrl+C / Ctrl+X / Ctrl+V.
/// </summary>
public static class Clipboard
{
    public static string GetText()
    {
        try
        {
            return Sdl.GetClipboardText();
        } catch
        {
            return string.Empty;
        }
    }

    public static void SetText(string text)
    {
        try
        {
            Sdl.SDL_SetClipboardText(text ?? string.Empty);
        } catch
        {
            //clipboard unavailable, ignore
        }
    }
}
