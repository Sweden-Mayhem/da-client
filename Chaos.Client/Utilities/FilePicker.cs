#region
using NativeFileDialogNET;
#endregion

namespace Chaos.Client.Utilities;

/// <summary>
///     The OS-native "open file" dialog, via nativefiledialog-extended (NativeFileDialogNET) - the Windows Common
///     Item Dialog (<c>IFileOpenDialog</c>), and the GTK/portal dialog on Linux. Modal: blocks the calling thread
///     until the user picks a file or cancels.
/// </summary>
public static class FilePicker
{
    /// <summary>
    ///     Opens a picker filtered to image files. Returns the chosen path, null if the user cancels. On a genuine
    ///     failure it logs loudly (never a silent dead button) and returns null.
    /// </summary>
    public static string? OpenImage()
    {
        try
        {
            using var dialog = new NativeFileDialog().SelectFile()
                                                     .AddFilter("Image files", "png,jpg,jpeg,bmp,gif");

            var result = dialog.Open(out string? path, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));

            return result == DialogResult.Okay ? path : null;
        } catch (Exception ex)
        {
            Console.Error.WriteLine($"[FilePicker] could not open the native file dialog: {ex.Message}");

            return null;
        }
    }
}
