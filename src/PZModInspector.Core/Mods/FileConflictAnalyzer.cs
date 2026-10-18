using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PZModInspector.Core.Mods;

public sealed record FileOwner(string ModId, string AbsolutePath, int LoadIndex);

/// <param name="RelativePath">Lower-case path relative to the mod's common/version folder, e.g. "media/lua/client/foo.lua".</param>
/// <param name="Winner">The copy the game uses: the one from the mod loaded last; for a translation, the mod whose texts win.</param>
/// <param name="Overridden">Only copies that differ from the winner: an identical copy replaced changes nothing.</param>
/// <param name="Keys">For a translation file, which the game merges: the keys <paramref name="Winner"/> gives another text.</param>
public sealed record FileConflict(string RelativePath, FileOwner Winner, IReadOnlyList<FileOwner> Overridden, IReadOnlyList<string>? Keys = null);

/// <summary>What happens to one copy of a file.</summary>
public enum CopyUse
{
    Used,
    Replaced,

    /// <summary>Replaced by a copy with the same content, or for Lua and scripts different only in comments and whitespace.</summary>
    ReplacedBySame,

    /// <summary>Read together with the copies of the other mods (<see cref="FileUse.Merged"/>).</summary>
    Merged,
    Unused,
}

/// <summary>
/// Replicates ZomboidFileSystem.loadMod: for each loaded mod, files from "common" and then from the version folder go
/// into one map keyed by lower-case relative path, so a later mod replaces the files of earlier ones.
/// Only files under media/ are considered: the root files (mod.info, poster.png, icon.png) do not matter in game.
/// </summary>
public sealed class FileIndex
{
    private readonly Dictionary<string, List<FileOwner>> _owners;
    private readonly ConcurrentDictionary<(string, string), bool> _same = new();
    private ILookup<string, string>? _byName;

    private FileIndex(Dictionary<string, List<FileOwner>> owners) => _owners = owners;

    /// <summary>Relative path → the mods that have it, in load order: the last one is the copy the game uses.</summary>
    public IEnumerable<(string Path, IReadOnlyList<FileOwner> Owners)> Files =>
        _owners.Select(kv => (kv.Key, (IReadOnlyList<FileOwner>)kv.Value));

    public int Count => _owners.Count;

    public static FileIndex Build(IEnumerable<LoadedMod> loaded, CancellationToken cancellationToken = default)
    {
        var owners = new Dictionary<string, List<FileOwner>>(StringComparer.Ordinal);
        foreach (var mod in loaded.OrderBy(m => m.LoadIndex))
        {
            foreach (var baseDir in new[] { mod.Mod.CommonDirectory, mod.Mod.VersionDirectory })
            {
                foreach (var file in EnumerateFiles(baseDir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(baseDir, file).Replace('\\', '/').ToLowerInvariant();
                    if (!relative.StartsWith("media/", StringComparison.Ordinal))
                        continue;

                    var owner = new FileOwner(mod.Id, file, mod.LoadIndex);
                    if (!owners.TryGetValue(relative, out var list))
                        owners[relative] = [owner];
                    else if (list[^1].ModId == mod.Id)
                        list[^1] = owner; // version folder replacing common inside the same mod
                    else
                        list.Add(owner);
                }
            }
        }
        return new FileIndex(owners);
    }

    public IReadOnlyList<FileOwner> Owners(string relativePath) =>
        _owners.TryGetValue(relativePath.Replace('\\', '/').ToLowerInvariant(), out var list) ? list : [];

    /// <summary>For each copy of <paramref name="owners"/> (in load order), what the game does with it.</summary>
    public IReadOnlyList<CopyUse> CopyUses(string relativePath, IReadOnlyList<FileOwner> owners)
    {
        if (owners.Count == 0)
            return [];
        switch (GameFileRules.Classify(relativePath.Replace('\\', '/').ToLowerInvariant()))
        {
            case FileUse.Merged:
                return owners.Select(_ => CopyUse.Merged).ToList();
            case FileUse.Unused:
                return owners.Select(_ => CopyUse.Unused).ToList();
            default:
                var used = owners[^1];
                return owners.Select((o, i) => i == owners.Count - 1 ? CopyUse.Used
                    : SameContent(o.AbsolutePath, used.AbsolutePath) ? CopyUse.ReplacedBySame : CopyUse.Replaced).ToList();
        }
    }

    /// <summary>
    /// Byte by byte; for Lua files and scripts, also when only comments and whitespace differ (<see cref="CodeComparer"/>).
    /// Remembered for the life of the index. An unreadable file counts as different.
    /// </summary>
    public bool SameContent(string path, string otherPath) =>
        _same.GetOrAdd((path, otherPath), static key => CompareFiles(key.Item1, key.Item2)
            || (CodeComparer.IsComparable(key.Item1) && CodeComparer.SameCode(key.Item1, key.Item2)));

    private static bool CompareFiles(string a, string b)
    {
        try
        {
            var infoA = new FileInfo(a);
            var infoB = new FileInfo(b);
            if (infoA.Length != infoB.Length)
                return false;
            using var streamA = infoA.OpenRead();
            using var streamB = infoB.OpenRead();
            var bufferA = new byte[81920];
            var bufferB = new byte[81920];
            while (true)
            {
                var read = streamA.ReadAtLeast(bufferA, bufferA.Length, throwOnEndOfStream: false);
                if (streamB.ReadAtLeast(bufferB, bufferB.Length, throwOnEndOfStream: false) != read)
                    return false;
                if (read == 0)
                    return true;
                if (!bufferA.AsSpan(0, read).SequenceEqual(bufferB.AsSpan(0, read)))
                    return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Relative paths whose file name is <paramref name="fileName"/>, ignoring case.</summary>
    public IEnumerable<string> FindByName(string fileName)
    {
        _byName ??= _owners.Keys.ToLookup(FileName, StringComparer.Ordinal);
        return _byName[fileName.ToLowerInvariant()];
    }

    /// <summary>
    /// Relative paths matching <paramref name="query"/>, ignoring case and sorted: a file name (when no file has exactly that
    /// name, the names that contain it), a path or its end ("client/foo.lua"), or a pattern with * and ?, on the name or,
    /// when it has a "/", on the end of the path. A blank query matches nothing.
    /// </summary>
    public IReadOnlyList<string> MatchPaths(string query)
    {
        var q = query.Replace('\\', '/').Trim().ToLowerInvariant();
        if (q.Length == 0)
            return [];
        IEnumerable<string> found;
        if (q.Contains('*') || q.Contains('?'))
        {
            var pattern = Regex.Escape(q).Replace(@"\*", ".*").Replace(@"\?", ".");
            var regex = new Regex(q.Contains('/') ? "(?:^|/)" + pattern + "$" : "^" + pattern + "$", RegexOptions.CultureInvariant);
            found = _owners.Keys.Where(p => regex.IsMatch(q.Contains('/') ? p : FileName(p)));
        }
        else if (q.Contains('/'))
            found = _owners.Keys.Where(p => p.Contains(q, StringComparison.Ordinal));
        else
        {
            var byName = FindByName(q).ToList();
            found = byName.Count > 0 ? byName : _owners.Keys.Where(p => FileName(p).Contains(q, StringComparison.Ordinal));
        }
        return found.Order(StringComparer.Ordinal).ToList();
    }

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        if (!Directory.Exists(directory))
            return [];
        try
        {
            return Directory.EnumerateFiles(directory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System,
            }).ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }
}

/// <summary>
/// Files that more than one loaded mod has and that really change something in game: a copy replaced by a different one, or
/// a translation key given another text by a mod loaded later. Identical copies and the files the game merges or does not
/// read (<see cref="GameFileRules"/>) are left out.
/// </summary>
public static class FileConflictAnalyzer
{
    private static readonly JsonDocumentOptions LenientJson = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static IReadOnlyList<FileConflict> Analyze(IEnumerable<LoadedMod> loaded, CancellationToken cancellationToken = default) =>
        Analyze(FileIndex.Build(loaded, cancellationToken), cancellationToken);

    public static IReadOnlyList<FileConflict> Analyze(FileIndex index, CancellationToken cancellationToken = default)
    {
        var conflicts = new List<FileConflict>();
        foreach (var (path, owners) in index.Files)
        {
            if (owners.Count < 2)
                continue;
            cancellationToken.ThrowIfCancellationRequested();
            switch (GameFileRules.Classify(path))
            {
                case FileUse.Replaced:
                    var winner = owners[^1];
                    var different = owners.Take(owners.Count - 1).Where(o => !index.SameContent(o.AbsolutePath, winner.AbsolutePath)).ToList();
                    if (different.Count > 0)
                        conflicts.Add(new FileConflict(path, winner, different));
                    break;
                case FileUse.Merged when GameFileRules.IsTranslationTable(path):
                    conflicts.AddRange(TranslationConflicts(path, owners));
                    break;
            }
        }
        return conflicts
            .OrderBy(c => c.Winner.LoadIndex)
            .ThenBy(c => c.RelativePath, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Translator.tryFillMapFromFile, mod after mod: a key takes the text of the last mod that has it, unless that text is
    /// empty and the key already has one. A conflict is a mod whose text for a key is not the one the game ends up with.
    /// </summary>
    private static IEnumerable<FileConflict> TranslationConflicts(string path, IReadOnlyList<FileOwner> owners)
    {
        var final = new Dictionary<string, (FileOwner Owner, string Text)>(StringComparer.Ordinal);
        var texts = new List<(FileOwner Owner, Dictionary<string, string> Map)>();
        foreach (var owner in owners)
        {
            var map = ReadTranslation(owner.AbsolutePath);
            if (map is null)
                continue;
            texts.Add((owner, map));
            foreach (var (key, text) in map)
            {
                if (!final.ContainsKey(key) || text.Length > 0)
                    final[key] = (owner, text);
            }
        }

        var byWinner = new Dictionary<FileOwner, (List<string> Keys, List<FileOwner> Losers)>();
        foreach (var (key, (winner, text)) in final)
        {
            var losers = texts.Where(t => t.Owner != winner && t.Map.TryGetValue(key, out var other) && other.Length > 0 && other != text)
                .Select(t => t.Owner).ToList();
            if (losers.Count == 0)
                continue;
            if (!byWinner.TryGetValue(winner, out var entry))
                byWinner[winner] = entry = ([], []);
            entry.Keys.Add(key);
            foreach (var loser in losers.Where(l => !entry.Losers.Contains(l)))
                entry.Losers.Add(loser);
        }
        return byWinner.Select(kv => new FileConflict(path, kv.Key, kv.Value.Losers.OrderBy(o => o.LoadIndex).ToList(),
            kv.Value.Keys.Order(StringComparer.Ordinal).ToList()));
    }

    /// <summary>
    /// The game parses with org.json in lenient mode; trailing commas and comments are the usual liberties of the mods.
    /// A file that cannot be read here is left out of the comparison.
    /// </summary>
    private static Dictionary<string, string>? ReadTranslation(string file)
    {
        try
        {
            ReadOnlyMemory<byte> bytes = File.ReadAllBytes(file);
            if (bytes.Span.StartsWith("﻿"u8))
                bytes = bytes[3..];
            using var doc = JsonDocument.Parse(bytes, LenientJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in doc.RootElement.EnumerateObject())
                map[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText();
            return map;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
