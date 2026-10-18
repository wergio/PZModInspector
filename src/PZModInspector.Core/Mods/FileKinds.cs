namespace PZModInspector.Core.Mods;

/// <summary>Text or binary file, for the filter on the overwritten files.</summary>
public static class FileKinds
{
    private const int SniffLength = 8000;

    /// <summary>The usual formats of the mods, decided without reading the file.</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lua", ".txt", ".xml", ".json", ".info", ".ini", ".cfg", ".csv", ".md",
        ".glsl", ".vert", ".frag", ".hlsl", ".fx", ".h",
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".dds", ".bmp", ".tga", ".gif",
        ".ogg", ".wav", ".mp3", ".bank", ".fbx", ".pack", ".tiles", ".bin", ".jar", ".dll", ".zip",
    };

    /// <summary>
    /// By extension when it is a known one, otherwise like git: binary when the first 8000 bytes contain a zero byte.
    /// An empty file is text; an unreadable one counts as binary.
    /// </summary>
    public static bool IsText(string path)
    {
        var extension = Path.GetExtension(path);
        if (TextExtensions.Contains(extension))
            return true;
        if (BinaryExtensions.Contains(extension))
            return false;
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[SniffLength];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return buffer.AsSpan(0, read).IndexOf((byte)0) < 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
