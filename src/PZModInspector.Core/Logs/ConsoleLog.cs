using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PZModInspector.Core.Logs;

public enum LogSeverity
{
    Warning,
    Error,
}

/// <summary>
/// A frame of Lua code in the log: "Lua((MOD: name)).function(file.lua:line)" or "Lua(Vanilla).function(...)".
/// Warnings about require() give the file in place of the function: "Lua((MOD: name)).file.lua".
/// </summary>
/// <param name="ModName">The mod's name= as the game prints it; null for vanilla code.</param>
/// <param name="ModNameIsEnd">
/// Only the part after the last "." of the name: in the "at ..." column the game keeps what follows the last "." of
/// "Lua((MOD:name))", so "Bicycle Mod [B42.15+/MP] Bicycle!" becomes "15+/MP] Bicycle!".
/// </param>
public sealed record LuaFrame(string? ModName, string Function, string? File, int? Line, bool ModNameIsEnd = false)
{
    public override string ToString()
    {
        var where = File is null || (File == Function && Line is null) ? "" : Line is null ? $" ({File})" : $" ({File}:{Line})";
        var mod = ModName is null ? "Vanilla" : "MOD: " + (ModNameIsEnd ? "…" : "") + ModName;
        return $"{mod} · {Function}{where}";
    }
}

/// <summary>An ERROR or WARN line of console.txt with the lines that follow it (stack traces).</summary>
/// <param name="LineNumber">1-based line in the file.</param>
/// <param name="Frame">The game frame counter (f:N): equal values come from the same moment.</param>
public sealed record LogEntry(int LineNumber, LogSeverity Severity, string Category, int Frame, string? Location, string Message,
    IReadOnlyList<string> Details)
{
    /// <summary>Lua frames found in the location, the message and the stack trace, innermost first.</summary>
    public IReadOnlyList<LuaFrame> LuaFrames { get; init; } = [];

    /// <summary>First Java exception line, e.g. "java.lang.RuntimeException: Object tried to call nil in ...".</summary>
    public string? Exception { get; init; }
}

/// <summary>Entries that differ only by numbers (coordinates, ids, counters): the game often repeats an error every frame.</summary>
public sealed record LogGroup(LogEntry First, int Count, int LastLineNumber);

/// <summary>Reads the game's console.txt (or a log from Zomboid\Logs). Only ERROR and WARN entries are kept.</summary>
public static partial class ConsoleLogParser
{
    public static IReadOnlyList<LogEntry> ReadFile(string path)
    {
        // The game keeps console.txt open while it runs.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(ReadLines(reader));
    }

    public static IReadOnlyList<LogEntry> Parse(IEnumerable<string> lines)
    {
        var entries = new List<LogEntry>();
        Match? header = null;
        var headerLine = 0;
        var details = new List<string>();

        void Flush()
        {
            if (header is null)
                return;
            var location = header.Groups["loc"].Success ? header.Groups["loc"].Value.Trim() : null;
            var message = header.Groups["msg"].Value.Trim();
            var frames = new List<LuaFrame>();
            foreach (var text in new[] { location, message }.Concat(details))
            {
                if (text is null)
                    continue;
                foreach (Match m in FrameRegex().Matches(text))
                {
                    var frame = ToFrame(m);
                    if (frames.Count == 0 || frames[^1] != frame)
                        frames.Add(frame);
                }
            }
            if (location is not null && !location.Contains("Lua(", StringComparison.Ordinal)
                                     && CutFrameRegex().Match(location) is { Success: true } cut)
                frames.Insert(0, ToFrame(cut) with { ModNameIsEnd = true });
            // The message names the function, the stack trace adds file and line: keep the second.
            frames.RemoveAll(f => f.File is null && frames.Any(g => g.File is not null && g.ModName == f.ModName && g.Function == f.Function));
            entries.Add(new LogEntry(headerLine, header.Groups["level"].Value == "ERROR" ? LogSeverity.Error : LogSeverity.Warning,
                header.Groups["cat"].Value, int.Parse(header.Groups["frame"].Value, CultureInfo.InvariantCulture), location, message, details)
            {
                LuaFrames = frames,
                Exception = details.FirstOrDefault(d => ExceptionRegex().IsMatch(d)),
            });
            header = null;
            details = [];
        }

        var number = 0;
        foreach (var line in lines)
        {
            number++;
            var m = HeaderRegex().Match(line);
            if (m.Success)
            {
                Flush();
                if (m.Groups["level"].Value is "ERROR" or "WARN")
                {
                    header = m;
                    headerLine = number;
                }
                continue;
            }
            if (header is not null && line.Trim() is { Length: > 0 } detail)
                details.Add(detail);
        }
        Flush();
        return entries;
    }

    public static IReadOnlyList<LogGroup> Group(IEnumerable<LogEntry> entries)
    {
        var groups = new Dictionary<string, (LogEntry First, int Count, int Last)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var e in entries)
        {
            var key = string.Join('\u001f', e.Severity, e.Category, Normalize(e.Location), Normalize(e.Message), Normalize(e.Exception),
                e.LuaFrames.FirstOrDefault()?.ToString());
            if (groups.TryGetValue(key, out var g))
                groups[key] = (g.First, g.Count + 1, e.LineNumber);
            else
            {
                groups[key] = (e, 1, e.LineNumber);
                order.Add(key);
            }
        }
        return order.Select(k => new LogGroup(groups[k].First, groups[k].Count, groups[k].Last)).ToList();
    }

    private static LuaFrame ToFrame(Match m)
    {
        var mod = m.Groups["mod"].Success ? m.Groups["mod"].Value.Trim() : null;
        var function = m.Groups["func"].Value;
        if (m.Groups["file"].Success)
            return new LuaFrame(mod, function, m.Groups["file"].Value, int.Parse(m.Groups["line"].Value, CultureInfo.InvariantCulture));
        return function.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)
            ? new LuaFrame(mod, function, function, null)
            : new LuaFrame(mod, function, null, null);
    }

    private static string Normalize(string? text) => text is null ? "" : NumberRegex().Replace(text, "#");

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    /// <summary>
    /// "ERROR: General      f:0 at Location.method      > message" (location optional, sometimes ", t:millis" after f:).
    /// The logs in Zomboid\Logs put the time first: "[18-10-26 12:43:47.340] ERROR: ..."; the lines that follow do not have it.
    /// </summary>
    [GeneratedRegex(@"^(?:\[[^\]]*\]\s*)?(?<level>ERROR|WARN|LOG|DEBUG|TRACE)\s*:\s*(?<cat>\S+)\s+f:(?<frame>\d+)(?:,\s*t:\d+)?(?:\s+at\s+(?<loc>.*?))?\s*>\s?(?<msg>.*)$")]
    private static partial Regex HeaderRegex();

    /// <summary>The mod name can contain parentheses: the lazy match stops at the "))." that closes it.</summary>
    [GeneratedRegex(@"Lua\((?:\(MOD:\s*(?<mod>.*?)\)|Vanilla)\)\.(?<func>[^\s(>]+)(?:\((?<file>[^():\s]+):(?<line>\d+)\))?")]
    private static partial Regex FrameRegex();

    /// <summary>The end of "Lua((MOD:name)).file.lua" after the game dropped the name up to its last ".".</summary>
    [GeneratedRegex(@"^(?<mod>[^.()]+?)\)\)\.(?<func>[^\s(>]+)(?:\((?<file>[^():\s]+):(?<line>\d+)\))?$")]
    private static partial Regex CutFrameRegex();

    [GeneratedRegex(@"^[a-z][\w.]*\.[A-Z]\w*(?:Exception|Error)\b")]
    private static partial Regex ExceptionRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberRegex();
}
