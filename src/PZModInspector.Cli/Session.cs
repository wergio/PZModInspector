using PZModInspector.Core.Analysis;
using PZModInspector.Core.Environment;
using PZModInspector.Core.Localization;
using PZModInspector.Core.Model;
using PZModInspector.Core.Mods;
using PZModInspector.Core.Settings;
using PZModInspector.Core.Workshop;

namespace PZModInspector.Cli;

/// <summary>Where PZModInspectorCLI takes settings, cache and folders from: the program's own by default, fake ones in tests.</summary>
/// <param name="Environment">Null: detected like the window does, with the paths of the Settings.</param>
/// <param name="OnlineClient">Used only by "refresh": every other command reads the Workshop cache and sends nothing to Steam.</param>
public sealed record CliHost(AppSettings Settings, string WorkshopCachePath, Func<PzEnvironment>? Environment = null,
    Func<IWorkshopClient>? OnlineClient = null)
{
    public static CliHost Default() => new(AppSettings.Load(), AppSettings.WorkshopCachePath);
}

/// <summary>The mods on disk, the chosen mod list and its analysis, computed the first time a command needs them.</summary>
internal sealed class Session
{
    private InspectionResult? _result;
    private FileIndex? _files;

    private Session(CliHost host, Inspector inspector, ModListSource source)
    {
        Host = host;
        Inspector = inspector;
        Source = source;
    }

    public CliHost Host { get; }
    public Inspector Inspector { get; }
    public ModListSource Source { get; }
    public PzEnvironment Environment => Inspector.Environment;
    public ModCatalog Catalog => Inspector.Catalog;

    public InspectionResult Result
    {
        get
        {
            Analyze();
            return _result!;
        }
    }

    /// <summary>Every file of the loaded mods, to say which copy the game uses.</summary>
    public FileIndex Files
    {
        get
        {
            Analyze();
            return _files!;
        }
    }

    public static Session Open(CliHost host, string? sourceKey)
    {
        var workshop = new WorkshopService(OfflineClient.Instance, host.WorkshopCachePath);
        var env = host.Environment?.Invoke() ?? EnvironmentDetector.Detect(host.Settings.ToOverrides());
        var inspector = Inspector.Load(env, workshop);
        return new Session(host, inspector, PickSource(inspector.Sources, sourceKey, host.Settings.LastSource));
    }

    public string NameOf(string id) => DetailRows.NameOf(Result, Catalog, id);

    /// <summary>"Name [id]", or just the id when the name is the same.</summary>
    public string Label(string id)
    {
        var name = NameOf(id).Trim();
        return name == id ? id : $"{name} [{id}]";
    }

    /// <summary>By id, by Workshop id or by part of the name or id; several matches are returned for the caller to list.</summary>
    public IReadOnlyList<ModEntry> FindEntries(string query)
    {
        var entries = Result.Entries;
        var exact = entries.Where(e => e.Id == query).ToList();
        if (exact.Count == 0)
            exact = entries.Where(e => e.Id.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 0 && ulong.TryParse(query, out var workshopId))
            exact = entries.Where(e => e.WorkshopId == workshopId).ToList();
        if (exact.Count > 0)
            return exact;
        return entries.Where(e => e.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                  || e.Id.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void Analyze()
    {
        if (_result is not null)
            return;
        var input = Inspector.CreateInput(Source);
        var plan = ModAnalyzer.Plan(input);
        _files = FileIndex.Build(plan.Loaded);
        _result = ModAnalyzer.Analyze(input with { Conflicts = FileConflictAnalyzer.Analyze(_files) }, plan);
    }

    /// <summary>"default", "latest", "Mode/Save" or just the save name; without it, the list last used in the window.</summary>
    private static ModListSource PickSource(IReadOnlyList<ModListSource> sources, string? key, string? lastUsed)
    {
        if (key is null)
            return sources.FirstOrDefault(s => s.Key == lastUsed) ?? sources.FirstOrDefault(s => s.IsLatestSave) ?? sources[0];
        if (key.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return sources.FirstOrDefault(s => s.IsLatestSave) ?? sources[0];
        var byKey = sources.FirstOrDefault(s => s.Key.Equals(key.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        var byName = sources.Where(s => string.Equals(s.SaveName, key, StringComparison.OrdinalIgnoreCase)).ToList();
        return byKey ?? (byName.Count == 1 ? byName[0] : null)
               ?? throw new CliUsageException($"Mod list not found: {key}. Available: {string.Join(", ", sources.Select(s => s.Key))}");
    }

    /// <summary>The cache is all the offline commands need.</summary>
    private sealed class OfflineClient : IWorkshopClient
    {
        public static readonly OfflineClient Instance = new();

        public Task<IReadOnlyList<WorkshopItemDetails>> GetDetailsAsync(IReadOnlyCollection<ulong> ids, string? apiKey,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Only \"PZModInspectorCLI refresh\" asks Steam.");
    }
}
