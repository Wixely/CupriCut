using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CupriCut.Services;

/// <summary>
/// Show a folder in whatever the desktop's file manager is.
///
/// <para>Rendering something and then having to go and find it is a small friction that happens
/// every single time. The window knows exactly where it just wrote; opening it is one call.</para>
///
/// <para>Deliberately not an MCP tool. Opening a window on the machine is something a person asks
/// for, and an agent asking for it would be reaching out of the render sandbox into the desktop.
/// The tools report the path instead, which is what an agent can actually use.</para>
/// </summary>
public static class Reveal
{
    /// <summary>
    /// Open <paramref name="path"/> - a folder, or a folder with the file selected.
    /// </summary>
    /// <exception cref="CutPolicyException">There is nothing there, or no file manager answered.</exception>
    public static void InFileManager(string path)
    {
        var isFile = File.Exists(path);
        if (!isFile && !Directory.Exists(path))
            throw new CutPolicyException($"There is nothing at '{path}' to open.");

        var (exe, args) = Command(path, isFile);
        try
        {
            // UseShellExecute so the desktop's own handler answers rather than this looking for an
            // executable on PATH - which is how "explorer" behaves differently when launched from a
            // service than from a shell.
            using var process = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new CutPolicyException($"Could not open '{path}': {ex.Message}");
        }
    }

    private static (string Exe, string Args) Command(string path, bool isFile)
    {
        // Windows: /select, puts the file manager on the file itself rather than merely in the
        // folder, which is the difference between "here it is" and "find it yourself".
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ("explorer.exe", isFile ? $"/select,\"{path}\"" : $"\"{path}\"");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return ("open", isFile ? $"-R \"{path}\"" : $"\"{path}\"");

        // Linux: xdg-open takes no "select" concept, so a file is shown by opening its folder.
        var target = isFile ? Path.GetDirectoryName(path)! : path;
        return ("xdg-open", $"\"{target}\"");
    }
}
