using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PZModInspector.Core;
using PZModInspector.Core.Analysis;
using PZModInspector.Core.Localization;
using PZModInspector.Core.Logs;
using PZModInspector.Core.Model;
using PZModInspector.Core.Mods;
using PZModInspector.Core.Workshop;

namespace PZModInspector.Cli;

/// <summary>One method per command: each builds its record, then prints it as text or, with --json, as JSON.</summary>
internal sealed class Commands(Session session, CliArgs args, TextWriter writer)
{
    private const int ShortListLimit = 15;
    private const int FileLimit = 50;
    private const int FrameLimit = 6;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Accents, «» and paths stay readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TextOutput _out = new(writer);

    private static Loc L => Loc.Instance;

    public int Sources()
    {
        var data = session.Inspector.Sources
            .Select(s => new SourceDto(s.Key, Text.SourceTitle(s), s.ModsFilePath, s.LastPlayedUtc, s.IsLatestSave, s == session.Source))
            .ToList();
        return Print(data, list =>
        {
            _out.Table(["Key", "List", "Last played"],
                list.Select(s => new[]
                {
                    (s.Selected ? "* " : "  ") + s.Key,
                    s.Title + (s.IsLatestSave ? " · " + L["Source.Latest"] : ""),
                    s.LastPlayedUtc is null ? "" : Text.Date(s.LastPlayedUtc),
                }), [0, 0, 0]);
            _out.Line();
            _out.Line("* = the list used without --source: the one opened last in the program.");
        });
    }

    public int Env() =>
        Print(new EnvDto(Header(), DetailRows.Environment(session.Inspector), DetailRows.IgnoredFolders(session.Inspector)), d =>
        {
            PrintHeader(d.Header);
            _out.Title(L["Tabs.Environment"]);
            _out.Rows(d.Rows);
            if (d.IgnoredFolders.Count == 0)
                return;
            _out.Title(L.Format("Tabs.Ignored", d.IgnoredFolders.Count));
            _out.Line(L["Ignored.Hint"]);
            foreach (var folder in d.IgnoredFolders)
            {
                _out.Line();
                _out.Line($"  {folder.Name.Trim()}{(folder.Id.Length > 0 ? $" [{folder.Id}]" : "")}  —  {folder.Reason}");
                if (folder.WorkshopUrl is { } url)
                    _out.Line($"    Workshop: {folder.WorkshopText.Trim()}  {url}");
                if (folder.Note.Length > 0)
                    _out.Line($"    {folder.Note}");
                _out.Line($"    {folder.Directory}");
            }
        });

    public int Report()
    {
        var result = session.Result;
        var all = result.AllIssues.ToList();
        var shown = all
            .Where(i => args.Has("info") || i.Severity >= Severity.Warning)
            .OrderByDescending(i => i.Severity)
            .ThenBy(i => i.ModId is null ? -1 : result.Find(i.ModId)?.LoadIndex ?? int.MaxValue)
            .Select(ToDto)
            .ToList();
        var data = new ReportDto(Header(), result.Entries.Count(e => e.ListIndex is not null), result.Plan.Loaded.Count,
            all.Count(i => i.Severity == Severity.Error), all.Count(i => i.Severity == Severity.Warning),
            all.Count(i => i.Severity == Severity.Info), result.Conflicts.Count, shown);
        return Print(data, d =>
        {
            PrintHeader(d.Header);
            _out.Line(L.Format("Summary", d.Listed, d.Loaded, d.Errors, d.Warnings));
            PrintIssues(d.Issues);
            _out.Line();
            if (!args.Has("info") && d.Infos > 0)
                _out.Line($"{d.Infos} informational notes not shown: add --info.");
            _out.Line($"Overwritten files: {d.FileConflicts} (PZModInspectorCLI overwritten).");
        });
    }

    public int ModList()
    {
        var rows = session.Result.Entries
            .OrderBy(e => e.LoadIndex ?? int.MaxValue)
            .ThenBy(e => e.ListIndex ?? int.MaxValue)
            .Select(e => new ModRowDto(e.LoadIndex, e.ListIndex, Status(e), e.Id, e.DisplayName.Trim(), e.WorkshopId, e.Mod?.Info.ModVersion,
                e.Workshop?.TimeUpdated ?? e.Install?.TimeUpdated, e.Issues.Count(i => i.Severity >= Severity.Warning), e.AutoLoadedBy))
            .ToList();
        return Print(new ModsDto(Header(), rows), d =>
        {
            PrintHeader(d.Header);
            _out.Line();
            _out.Table(
                ["#", L["Column.ListIndex"], "Status", L["Column.Id"], L["Column.Name"], "Workshop", L["Column.Version"], L["Column.Updated"],
                    L["Column.Problems"]],
                d.Mods.Select(m => new[]
                {
                    Number(m.LoadIndex), Number(m.ListIndex), m.Status, m.Id, m.Name, m.WorkshopId?.ToString(CultureInfo.InvariantCulture) ?? "",
                    m.Version ?? "", m.UpdatedUtc is { } u ? u.ToLocalTime().ToString("d", L.Culture) : "", m.Problems > 0 ? Number(m.Problems) : "",
                }),
                [0, 0, 14, 34, 46, 0, 12, 0, 0]);
        });
    }

    public int ModDetails()
    {
        var entry = ResolveMod(args.Text ?? throw new CliUsageException("Say which mod: PZModInspectorCLI mod <id, Workshop id or part of the name>."));
        var result = session.Result;
        var lost = result.Conflicts.Where(c => c.Overridden.Any(o => o.ModId == entry.Id)).Select(c => FileRow(c, c.Winner));
        var won = result.Conflicts.Where(c => c.Winner.ModId == entry.Id).Select(c => FileRow(c, c.Overridden[^1]));
        var data = new ModDto(Header(), entry.Id, entry.DisplayName.Trim(), Status(entry),
            DetailRows.ModInfo(entry, result, session.Catalog), DetailRows.Dependencies(entry, result, session.Catalog),
            entry.Issues.OrderByDescending(i => i.Severity).Select(ToDto).ToList(), lost.ToList(), won.ToList(),
            entry.OtherCopies.Select(c => c.Directory).ToList());
        return Print(data, d =>
        {
            PrintHeader(d.Header);
            _out.Line();
            _out.Line($"{d.Name} [{d.Id}] · {d.Status}");
            _out.Title(L["Details.Info"]);
            _out.Rows(d.Info);
            if (d.Dependencies.Count > 0)
            {
                _out.Title(L["Details.Dependencies"]);
                foreach (var group in d.Dependencies)
                {
                    _out.Line("  " + group.Title);
                    foreach (var item in group.Items)
                    {
                        var status = item.Status.Length == 0 ? "" : " — " + item.Status;
                        var severity = item.Severity is { } s ? $" ({Text.Severity(s)})" : "";
                        _out.Line($"    {item.Name.Trim()} [{item.Id}]{status}{severity}");
                    }
                }
            }
            _out.Title(L.Format("Tabs.Problems", d.Issues.Count));
            if (d.Issues.Count == 0)
                _out.Line("  " + L["Details.NoProblems"]);
            foreach (var issue in d.Issues)
                _out.Line($"  • [{Text.Severity(Enum.Parse<Severity>(issue.Severity))}] {issue.Message}");
            PrintFileRows(L.Format("Files.Lost", d.FilesLost.Count), d.FilesLost, "→");
            PrintFileRows(L.Format("Files.Won", d.FilesWon.Count), d.FilesWon, "←");
            if (d.OtherCopies.Count > 0)
            {
                _out.Title(L["Details.Copies"]);
                foreach (var copy in d.OtherCopies)
                    _out.Line("  " + copy);
            }
        });
    }

    public int FindFile()
    {
        var query = args.Text ?? throw new CliUsageException("Say which file: PZModInspectorCLI file <name or path>, also with * and ?.");
        var files = session.Files;
        var paths = files.MatchPaths(query);
        var limit = args.Has("all") ? int.MaxValue : FileLimit;
        var data = new FilesDto(Header(), query, paths.Count, paths.Take(limit).Select(p => FileMatch(p, files.Owners(p))).ToList());
        return Print(data, d =>
        {
            PrintHeader(d.Header);
            _out.Line();
            if (d.Total == 0)
            {
                _out.Line($"No file of the loaded mods matches \"{d.Query}\".");
                return;
            }
            _out.Line($"{d.Total} files match \"{d.Query}\".");
            foreach (var file in d.Files)
            {
                _out.Line();
                PrintFileMatch(file, "");
            }
            if (d.Total > d.Files.Count)
                _out.Line(More(d.Total - d.Files.Count, "--all"));
        });
    }

    /// <summary>Like the "Overwritten files" tab: --lua, --text and --binary are its drop-down.</summary>
    public int Overwritten()
    {
        var all = session.Result.Conflicts;
        IEnumerable<FileConflict> conflicts = all;
        if (args.Has("lua"))
            conflicts = conflicts.Where(c => IsLua(c.RelativePath));
        if (args.Has("text"))
            conflicts = conflicts.Where(c => FileKinds.IsText(c.Winner.AbsolutePath));
        if (args.Has("binary"))
            conflicts = conflicts.Where(c => !FileKinds.IsText(c.Winner.AbsolutePath));
        var modId = args.Option("mod") is { } query ? ResolveMod(query).Id : null;
        var pairs = conflicts
            .SelectMany(c => c.Overridden.Select(loser => (c.Winner, Loser: loser, File: Text.ConflictFile(c))))
            .Where(x => modId is null || x.Winner.ModId == modId || x.Loser.ModId == modId)
            .GroupBy(x => (Winner: x.Winner.ModId, Loser: x.Loser.ModId))
            .Select(g => new ConflictPairDto(g.Key.Winner, session.NameOf(g.Key.Winner).Trim(), g.First().Winner.LoadIndex,
                g.Key.Loser, session.NameOf(g.Key.Loser).Trim(), g.First().Loser.LoadIndex, g.Select(x => x.File).ToList()))
            .OrderBy(p => p.WinnerLoadIndex)
            .ThenBy(p => p.LoserLoadIndex)
            .ToList();
        var data = new ConflictsDto(Header(), all.Count, all.Count(c => IsLua(c.RelativePath)), pairs);
        return Print(data, d =>
        {
            PrintHeader(d.Header);
            _out.Line($"{d.Files} overwritten files ({d.LuaFiles} .lua): files overwritten by a different copy from a mod loaded later, and translation "
                      + "keys given another text. Equal copies (for Lua and scripts also when only comments and spaces differ) and files "
                      + "the game merges or does not read are left out.");
            if (d.Pairs.Count == 0)
                _out.Line("No files shared by the chosen mods.");
            foreach (var pair in d.Pairs)
            {
                _out.Line();
                _out.Line($"{pair.WinnerName} [{pair.WinnerId}] (#{pair.WinnerLoadIndex}) overwrites {pair.Files.Count} files of "
                          + $"{pair.LoserName} [{pair.LoserId}] (#{pair.LoserLoadIndex})");
                PrintLimited(pair.Files, "    ");
            }
        });
    }

    public int Log()
    {
        var path = Path.GetFullPath(args.Text ?? Path.Combine(session.Environment.ZomboidDirectory, "console.txt"));
        if (!File.Exists(path))
            throw new CliUsageException($"Log file not found: {path}");
        var entries = ConsoleLogParser.ReadFile(path);
        if (args.Has("errors"))
            entries = entries.Where(e => e.Severity == LogSeverity.Error).ToList();
        var findings = LogAnalyzer.Analyze(ConsoleLogParser.Group(entries), session.Result.Plan, session.Catalog, session.Files,
            session.Environment.GameDirectory);
        if (args.Has("mods"))
            findings = findings.Where(f => f.IsModRelated).ToList();
        var data = new LogDto(Header(), path, File.GetLastWriteTimeUtc(path), entries.Count(e => e.Severity == LogSeverity.Error),
            entries.Count(e => e.Severity == LogSeverity.Warning), findings.Select(ToDto).ToList());
        return Print(data, d =>
        {
            PrintHeader(d.Header);
            _out.Line($"{d.Path} · modified {Text.Date(d.ModifiedUtc)} · {d.Errors} errors and {d.Warnings} warnings in {d.Groups.Count} groups");
            var related = d.Groups.Where(g => g.Mods.Count > 0).ToList();
            var other = d.Groups.Where(g => g.Mods.Count == 0).ToList();
            if (related.Count > 0)
                _out.Title($"Linked to mods ({related.Count})");
            foreach (var group in related)
                PrintLogGroup(group);
            if (other.Count == 0)
                return;
            _out.Title($"Not linked to mods ({other.Count})");
            foreach (var group in other)
                _out.Line($"  [{SeverityText(group.Severity)} ×{group.Count}] " + TextOutput.Cut(LogMessage(group), 180));
        });
    }

    public async Task<int> RefreshAsync()
    {
        var host = session.Host;
        var client = host.OnlineClient?.Invoke() ?? new SteamWorkshopClient();
        try
        {
            var workshop = new WorkshopService(client, host.WorkshopCachePath);
            if (!args.Json)
                _out.Line(L["Status.QueryingWorkshop"]);
            var ok = await workshop.RefreshDetailsAsync(session.Inspector.AllWorkshopIds(), host.Settings.SteamApiKey, args.Has("force"),
                CancellationToken.None);
            var data = new RefreshDto(ok, workshop.LastDetailsFetch, host.Settings.SteamApiKeyProtected is not null, workshop.ApiKeyRejected,
                ok ? null : workshop.LastError);
            Print(data, d =>
            {
                var last = d.WorkshopDataUtc is null ? L["Status.Never"] : Text.Date(d.WorkshopDataUtc);
                _out.Line(d.Ok ? L.Format("Status.WorkshopOk", last) : L.Format("Status.WorkshopOffline", last) + $" ({d.Error})");
                if (d.ApiKeyRejected)
                    _out.Line(L["Status.ApiKeyRejected"]);
                else if (!d.HasApiKey)
                    _out.Line(L["Status.NoApiKey"]);
            });
            return ok ? 0 : 2;
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }

    // ---- shared pieces ----

    private int Print<T>(T data, Action<T> text)
    {
        if (args.Json)
            writer.WriteLine(JsonSerializer.Serialize(data, JsonOptions));
        else
            text(data);
        return 0;
    }

    private HeaderDto Header() => new(AppInfo.Version, session.Source.Key, Text.SourceTitle(session.Source), session.Environment.GameVersion.ToString(),
        session.Inspector.Workshop.LastDetailsFetch, session.Host.Settings.SteamApiKeyProtected is not null);

    private void PrintHeader(HeaderDto header)
    {
        var workshop = header.WorkshopDataUtc is null
            ? "no Workshop data in cache (PZModInspectorCLI refresh)"
            : L.Format("Status.WorkshopOk", Text.Date(header.WorkshopDataUtc));
        if (!header.HasApiKey)
            workshop += " · " + L["Status.NoApiKey"];
        _out.Line($"{header.SourceTitle} · game {header.GameVersion} · {workshop}");
    }

    private IssueDto ToDto(Issue issue) =>
        new(issue.Severity.ToString(), issue.Code.ToString(), issue.ModId, issue.ModId is null ? null : session.NameOf(issue.ModId).Trim(),
            Text.Issue(issue), issue.Args);

    private void PrintIssues(IReadOnlyList<IssueDto> issues)
    {
        foreach (var group in issues.GroupBy(i => i.Severity))
        {
            var title = Enum.Parse<Severity>(group.Key) switch
            {
                Severity.Error => L["Problems.Errors"],
                Severity.Warning => L["Problems.Warnings"],
                _ => L["Problems.Info"],
            };
            _out.Title($"{title} ({group.Count()})");
            foreach (var issue in group)
            {
                var mod = issue.ModId is null ? L["Problems.General"] : session.Label(issue.ModId);
                _out.Line($"  • {mod}: {issue.Message}");
            }
        }
    }

    private FileRowDto FileRow(FileConflict conflict, FileOwner other) =>
        new(conflict.RelativePath, other.ModId, session.NameOf(other.ModId).Trim(), other.LoadIndex, conflict.Keys);

    private void PrintFileRows(string title, IReadOnlyList<FileRowDto> rows, string arrow)
    {
        if (rows.Count == 0)
            return;
        _out.Title(title);
        PrintLimited(rows.Select(r => $"{(r.Keys is { Count: > 0 } keys ? $"{r.Path} · keys: {string.Join(", ", keys)}" : r.Path)}  {arrow} "
                                      + $"{r.OtherModName} [{r.OtherModId}] (#{r.OtherLoadIndex})").ToList(), "  ");
    }

    private void PrintLimited(IReadOnlyList<string> lines, string indent)
    {
        var limit = args.Has("files") ? int.MaxValue : ShortListLimit;
        foreach (var line in lines.Take(limit))
            _out.Line(indent + line);
        if (lines.Count > limit)
            _out.Line(indent + More(lines.Count - limit, "--files"));
    }

    private static string More(int count, string option) => $"… and {count} more ({option} to see them all)";

    private FileMatchDto FileMatch(string relativePath, IReadOnlyList<FileOwner> owners) =>
        new(relativePath, InGame(relativePath), Camel(GameFileRules.Classify(relativePath).ToString()), OwnerRows(relativePath, owners));

    /// <summary>In load order: for a replaced file the last one is the copy the game uses.</summary>
    private List<OwnerDto> OwnerRows(string relativePath, IReadOnlyList<FileOwner> owners)
    {
        var uses = session.Files.CopyUses(relativePath, owners);
        return owners.Select((o, i) => new OwnerDto(o.ModId, session.NameOf(o.ModId).Trim(), o.LoadIndex, o.AbsolutePath,
            uses[i] is CopyUse.Used or CopyUse.Merged, Camel(uses[i].ToString()))).ToList();
    }

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    private void PrintFileMatch(FileMatchDto file, string indent)
    {
        var note = file.FileUse switch
        {
            "merged" => "  (the game reads every copy and merges them" + (file.InGame ? " with the base game's)" : ")"),
            "unused" => "  (the game does not read this file)",
            _ => file.InGame ? "  (the base game has it too, the mods overwrite it)" : "",
        };
        _out.Line(indent + file.RelativePath + note);
        foreach (var owner in file.Owners)
        {
            var used = owner.Used ? "→" : " ";
            var what = owner.Use switch
            {
                "used" => "  · copy the game uses",
                "replacedBySame" => "  · overwritten by an equal copy (at most comments and spaces differ)",
                _ => "",
            };
            _out.Line($"{indent}  {used} #{owner.LoadIndex} {owner.ModName} [{owner.ModId}]{what}");
            _out.Line($"{indent}      {owner.Path}");
        }
    }

    private bool InGame(string relativePath) =>
        session.Environment.GameDirectory is { } game && File.Exists(Path.Combine(game, relativePath));

    private LogGroupDto ToDto(LogFinding finding)
    {
        var e = finding.Group.First;
        return new LogGroupDto(e.Severity.ToString(), finding.Group.Count, e.LineNumber, finding.Group.LastLineNumber, e.Frame, e.Category,
            e.Location, e.Message, e.Exception, e.LuaFrames.Select(f => f.ToString()).ToList(),
            finding.ModIds.Select(id => new ModRefDto(id, session.NameOf(id).Trim(), session.Result.Plan.Find(id)?.LoadIndex)).ToList(),
            finding.Files.Select(f => FileMatch(f.RelativePath, f.Owners)).ToList(),
            finding.Require is { } r
                ? new RequireDto(r.Module, r.Found.Select(Location).ToList(), r.SameName.Select(Location).ToList())
                : null);
    }

    private void PrintLogGroup(LogGroupDto group)
    {
        _out.Line();
        _out.Line($"[{SeverityText(group.Severity)} ×{group.Count}] lines {group.FirstLine}–{group.LastLine} · {group.Category} · f:{group.Frame}");
        _out.Line("  " + LogMessage(group));
        if (group.Exception is { } exception)
            _out.Line("  " + exception);
        foreach (var frame in group.LuaFrames.Take(FrameLimit))
            _out.Line("  Lua: " + frame);
        foreach (var mod in group.Mods)
            _out.Line($"  Mod: {mod.Name} [{mod.Id}]" + (mod.LoadIndex is { } i ? $" (#{i})" : " (" + L["Status.NotLoaded"] + ")"));
        foreach (var file in group.Files)
            PrintFileMatch(file, "  ");
        if (group.Require is { } require)
        {
            var module = $"require(\"{require.Module}\")";
            _out.Line("  " + (require.Found.Count > 0
                ? $"{module}: the file exists, {Where(require.Found)}, but require looks only in the folders already loaded "
                  + "(shared first, then client, then server): it is required too early or from the wrong folder"
                : require.SameName.Count > 0
                    ? $"{module}: nothing at that path; with the same name there is {Where(require.SameName)}: the path in require is probably wrong"
                    : $"{module}: no loaded mod has it, nor the game"));
        }
    }

    private FileMatchDto Location(LuaFileLocation location) =>
        new(location.RelativePath, location.InGame, Camel(GameFileRules.Classify(location.RelativePath).ToString()),
            OwnerRows(location.RelativePath, location.Owners));

    /// <summary>"path (base game, Mod [id] #n); path (...)".</summary>
    private static string Where(IEnumerable<FileMatchDto> locations) =>
        string.Join("; ", locations.Select(l =>
        {
            IEnumerable<string> who = l.InGame ? ["base game"] : [];
            return $"{l.RelativePath} ({string.Join(", ", who.Concat(l.Owners.Select(o => $"{o.ModName} [{o.ModId}] #{o.LoadIndex}")))})";
        }));

    private static string LogMessage(LogGroupDto group) => group.Location is null ? group.Message : $"{group.Location} > {group.Message}";

    private static string SeverityText(string severity) =>
        Text.Severity(severity == nameof(LogSeverity.Error) ? Severity.Error : Severity.Warning);

    private ModEntry ResolveMod(string query)
    {
        var found = session.FindEntries(query);
        if (found.Count == 1)
            return found[0];
        if (found.Count > 1)
        {
            throw new CliUsageException($"More than one mod matches \"{query}\": give the id.\n"
                + string.Join("\n", found.Take(30).Select(e => $"  {e.Id}  {e.DisplayName.Trim()}")));
        }
        var installed = session.Catalog.Mods
            .Where(m => m.Id.Equals(query, StringComparison.OrdinalIgnoreCase)
                        || m.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                        || m.WorkshopId?.ToString(CultureInfo.InvariantCulture) == query)
            .ToList();
        if (installed.Count == 0)
            throw new CliUsageException($"No installed mod matches \"{query}\".");
        throw new CliUsageException($"\"{query}\" is installed but not in the list \"{Text.SourceTitle(session.Source)}\":\n"
            + string.Join("\n", installed.Take(30).Select(m => $"  {m.Name.Trim()} [{m.Id}]  {m.Directory}")));
    }

    private static bool IsLua(string path) => path.EndsWith(".lua", StringComparison.Ordinal);

    private static string Status(ModEntry entry) =>
        entry.IsLoaded
            ? Text.Severity(entry.WorstSeverity is { } worst && worst >= Severity.Warning ? worst : null)
            : L["Status.NotLoaded"];

    private static string Number(int? value) => value?.ToString(L.Culture) ?? "";
}
