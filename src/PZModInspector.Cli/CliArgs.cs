namespace PZModInspector.Cli;

/// <summary>Wrong command line: PZModInspectorCLI prints the message (and exits with 1).</summary>
public sealed class CliUsageException(string message) : Exception(message);

/// <summary>"PZModInspectorCLI &lt;command&gt; [arguments] [--flag] [--option value]", options anywhere.</summary>
internal sealed class CliArgs
{
    public static readonly string[] Commands = ["help", "sources", "env", "report", "mods", "mod", "file", "overwritten", "conflicts", "log", "refresh", "version"];
    private static readonly HashSet<string> Flags = ["json", "info", "lua", "text", "binary", "files", "errors", "mods", "force", "all"];
    private static readonly HashSet<string> Options = ["source", "mod"];

    private readonly HashSet<string> _flags = [];
    private readonly Dictionary<string, string> _options = [];

    public string Command { get; private set; } = "help";
    public List<string> Arguments { get; } = [];

    public bool Json => Has("json");
    public string? Source => Option("source");

    public bool Has(string flag) => _flags.Contains(flag);

    public string? Option(string name) => _options.GetValueOrDefault(name);

    /// <summary>All the free arguments joined: names with spaces need no quotes.</summary>
    public string? Text => Arguments.Count == 0 ? null : string.Join(' ', Arguments);

    public static CliArgs Parse(IReadOnlyList<string> args)
    {
        var result = new CliArgs();
        string? command = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help" or "/?")
            {
                command = "help";
                continue;
            }
            if (arg is "--version")
            {
                command = "version";
                continue;
            }
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var name = arg[2..].ToLowerInvariant();
                if (Flags.Contains(name))
                    result._flags.Add(name);
                else if (Options.Contains(name))
                    result._options[name] = i + 1 < args.Count ? args[++i] : throw new CliUsageException($"Missing value for {arg}.");
                else
                    throw new CliUsageException($"Unknown option: {arg}. See PZModInspectorCLI help.");
                continue;
            }
            if (command is null)
                command = arg.ToLowerInvariant();
            else
                result.Arguments.Add(arg);
        }

        command ??= "help";
        if (!Commands.Contains(command))
            throw new CliUsageException($"Unknown command: {command}. See PZModInspectorCLI help.");
        result.Command = command;
        return result;
    }
}
