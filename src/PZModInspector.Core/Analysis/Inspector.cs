using PZModInspector.Core.Environment;
using PZModInspector.Core.Mods;
using PZModInspector.Core.Parsing;
using PZModInspector.Core.Workshop;

namespace PZModInspector.Core.Analysis;

/// <summary>Everything read from disk once at start: environment, installed mods, mod lists. Read only on game files.</summary>
public sealed class Inspector
{
    private Inspector(PzEnvironment environment, WorkshopManifest manifest, ModCatalog catalog,
        IReadOnlyList<ModListSource> sources, IReadOnlyDictionary<string, WorkshopIdHint> hints, WorkshopService workshop)
    {
        Environment = environment;
        Manifest = manifest;
        Catalog = catalog;
        Sources = sources;
        Hints = hints;
        Workshop = workshop;
    }

    public PzEnvironment Environment { get; }
    public WorkshopManifest Manifest { get; }
    public ModCatalog Catalog { get; }
    public IReadOnlyList<ModListSource> Sources { get; }
    public IReadOnlyDictionary<string, WorkshopIdHint> Hints { get; }
    public WorkshopService Workshop { get; }

    /// <summary>Set by the caller after a failed Workshop refresh, so the result can say the data may be old.</summary>
    public bool WorkshopQueryFailed { get; set; }

    public static Inspector Load(EnvironmentOverrides overrides, WorkshopService workshop) =>
        Load(EnvironmentDetector.Detect(overrides), workshop);

    /// <exception cref="GameCheckException">The game is not there, its version cannot be read or is before 42.</exception>
    public static Inspector Load(PzEnvironment env, WorkshopService workshop)
    {
        if (env.GameProblem is not null)
            throw new GameCheckException(env);
        var manifest = WorkshopManifest.Load(env.WorkshopManifestPath);
        var catalog = ModScanner.Scan(env, manifest.Order);
        return new Inspector(env, manifest, catalog, ModListSources.Find(env), WorkshopIdHints.Load(env.ZomboidDirectory), workshop);
    }

    public AnalysisInput CreateInput(ModListSource source, IReadOnlyList<FileConflict>? conflicts = null) => new()
    {
        Environment = Environment,
        Catalog = Catalog,
        Manifest = Manifest,
        Source = source,
        ModsFile = ActiveModsFile.ReadFile(source.ModsFilePath),
        Details = Workshop.GetDetails,
        Hints = Hints,
        Conflicts = conflicts,
        WorkshopQueryFailed = WorkshopQueryFailed,
    };

    public InspectionResult Inspect(ModListSource source, IReadOnlyList<FileConflict>? conflicts = null) =>
        ModAnalyzer.Analyze(CreateInput(source, conflicts));

    /// <summary>Every Workshop item worth asking Steam about: the installed ones and the ones known only from hints.</summary>
    public IReadOnlyList<ulong> AllWorkshopIds() =>
        Catalog.WorkshopItemFolders.Concat(Hints.Values.Select(h => h.WorkshopId)).Distinct().Order().ToList();
}
