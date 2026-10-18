using System.Diagnostics;
using System.IO;

namespace PZModInspector.App.Services;

/// <summary>One side of a comparison: the file and how to call it (mod and load position, or the base game).</summary>
public sealed record DiffSide(string Path, string Label);

/// <summary>The compare program chosen in the settings: two files, the older copy on the left.</summary>
public static class DiffTool
{
    /// <summary>Where the WinMerge installer puts it, for the hint in the settings.</summary>
    public static string? DetectWinMerge()
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        ];
        return roots.Where(r => r.Length > 0)
            .Select(r => Path.Combine(r, "WinMerge", "WinMergeU.exe"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Any program gets the two paths. WinMerge also gets the labels and both sides read only: the files are the mods'
    /// and the game's, not to be changed by mistake.
    /// </summary>
    /// <exception cref="System.ComponentModel.Win32Exception">The program cannot be started.</exception>
    public static void Launch(string program, DiffSide left, DiffSide right)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false };
        if (Path.GetFileName(program).StartsWith("WinMerge", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var arg in new[] { "/u", "/wl", "/wr", "/dl", left.Label, "/dr", right.Label })
                start.ArgumentList.Add(arg);
        }
        start.ArgumentList.Add(left.Path);
        start.ArgumentList.Add(right.Path);
        Process.Start(start)?.Dispose();
    }
}
