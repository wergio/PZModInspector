using System.Reflection;

namespace PZModInspector.Core;

/// <summary>The version of PZ Mod Inspector, the same for the window and the command line.</summary>
public static class AppInfo
{
    /// <summary>
    /// "1.0.0", from &lt;Version&gt; in Directory.Build.props. The SDK adds "+commit" to the informational version
    /// in a git repository: it is cut off.
    /// </summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var info = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
            ?? "?";
        var plus = info.IndexOf('+');
        return plus < 0 ? info : info[..plus];
    }
}
