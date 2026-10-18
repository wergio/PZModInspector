using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PZModInspector.Core.Model;

namespace PZModInspector.Core.Parsing;

/// <summary>
/// Reads mod.info line by line exactly like ChooseGameInfo.readModInfoAux (build 42.21): the keys are matched
/// with case-sensitive "contains" checks in a fixed order, so some odd lines are read the same odd way the game does.
/// </summary>
public static partial class ModInfoParser
{
    public static ModInfoData ParseFile(string path, string versionDirectory, string commonDirectory)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var failed = new ModInfoData();
            failed.Errors.Add(new ModInfoError(ModInfoErrorKind.ReadError, ex.Message));
            return failed;
        }
        return Parse(lines, versionDirectory, commonDirectory, File.Exists);
    }

    public static ModInfoData Parse(IEnumerable<string> lines, string versionDirectory, string commonDirectory, Func<string, bool> fileExists)
    {
        var info = new ModInfoData();
        foreach (var line in lines)
        {
            AddRawEntry(info, line);
            if (!ParseLine(info, line, versionDirectory, commonDirectory, fileExists))
                break;
        }
        return info;
    }

    /// <returns>false when the game would stop and reject the mod.info.</returns>
    private static bool ParseLine(ModInfoData info, string line, string versionDir, string commonDir, Func<string, bool> fileExists)
    {
        if (line.Contains("name="))
        {
            var name = line.Replace("name=", "");
            if (!string.IsNullOrWhiteSpace(name))
                info.Name = name;
        }
        else if (line.Contains("poster="))
        {
            var texture = line.Replace("poster=", "");
            if (!string.IsNullOrWhiteSpace(texture))
                info.PosterPaths.Add(ResolveTexture(texture, versionDir, commonDir, fileExists));
        }
        else if (line.Contains("description="))
        {
            info.Description += line.Replace("description=", "");
        }
        else if (line.Contains("require="))
        {
            info.Require = SplitIds(line.Replace("require=", ""));
        }
        else if (line.Contains("incompatible="))
        {
            info.Incompatible = SplitIds(line.Replace("incompatible=", ""));
        }
        else if (line.Contains("loadModAfter="))
        {
            info.LoadAfter = SplitIds(line.Replace("loadModAfter=", ""));
        }
        else if (line.Contains("loadModBefore="))
        {
            info.LoadBefore = SplitIds(line.Replace("loadModBefore=", ""));
        }
        else if (line.StartsWith("id=", StringComparison.Ordinal))
        {
            var id = line.Replace("id=", "").Trim();
            if (!string.IsNullOrWhiteSpace(id))
                info.Id = id;
        }
        else if (line.Contains("author="))
        {
            info.Author = line.Replace("author=", "");
        }
        else if (line.Contains("modversion="))
        {
            info.ModVersion = line.Replace("modversion=", "");
        }
        else if (line.Contains("icon="))
        {
            var texture = line.Replace("icon=", "");
            if (!string.IsNullOrWhiteSpace(texture))
                info.IconPath = ResolveTexture(texture, versionDir, commonDir, fileExists);
        }
        else if (line.Contains("category="))
        {
            info.Category = line.Replace("category=", "");
        }
        else if (line.Contains("url="))
        {
            info.Url = line.Replace("url=", "");
        }
        else if (line.Contains("pack="))
        {
            return ParsePack(info, line.Replace("pack=", "").Trim());
        }
        else if (line.Contains("tiledef="))
        {
            return ParseTileDef(info, line.Replace("tiledef=", "").Trim());
        }
        else if (line.StartsWith("versionMax=", StringComparison.Ordinal))
        {
            var text = line.Replace("versionMax=", "").Trim();
            if (text.Length > 0)
            {
                if (!GameVersion.TryParse(text, out var max))
                {
                    info.Errors.Add(new ModInfoError(ModInfoErrorKind.InvalidVersionMax, text));
                    return false;
                }
                info.VersionMax = max;
            }
        }
        else if (line.StartsWith("versionMin=", StringComparison.Ordinal))
        {
            var text = line.Replace("versionMin=", "").Trim();
            if (text.Length > 0)
            {
                if (!GameVersion.TryParse(text, out var min))
                {
                    info.Errors.Add(new ModInfoError(ModInfoErrorKind.InvalidVersionMin, text));
                    return false;
                }
                info.VersionMin = min;
            }
        }
        return true;
    }

    private static bool ParsePack(ModInfoData info, string pack)
    {
        if (pack.Length == 0)
        {
            info.Errors.Add(new ModInfoError(ModInfoErrorKind.EmptyPack, ""));
            return false;
        }
        var typeIndex = pack.IndexOf("type=", StringComparison.Ordinal);
        if (typeIndex != -1)
        {
            // The game cuts the name at the first space; without a space it throws and drops the mod.
            var space = pack.IndexOf(' ');
            if (space < 0)
            {
                info.Errors.Add(new ModInfoError(ModInfoErrorKind.InvalidPack, pack));
                return false;
            }
            pack = pack[..space].Trim();
        }
        info.Packs.Add(pack);
        return true;
    }

    private static bool ParseTileDef(ModInfoData info, string value)
    {
        var parts = WhitespaceRegex().Split(value);
        if (parts.Length != 2
            || !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            || number < 100 || number > 8189)
        {
            info.Errors.Add(new ModInfoError(ModInfoErrorKind.InvalidTileDef, value));
            return false;
        }
        info.TileDefs.Add($"{parts[0]} {number}");
        return true;
    }

    /// <summary>replace("\\", "").split(","): backslashes removed, items not trimmed, trailing empties dropped.</summary>
    private static List<string> SplitIds(string value) => GameVersion.JavaSplit(value.Replace("\\", ""), ',');

    private static string ResolveTexture(string name, string versionDir, string commonDir, Func<string, bool> fileExists)
    {
        var inVersion = versionDir + Path.DirectorySeparatorChar + name;
        return fileExists(inVersion) ? inVersion : commonDir + Path.DirectorySeparatorChar + name;
    }

    private static void AddRawEntry(ModInfoData info, string line)
    {
        var eq = line.IndexOf('=');
        if (eq <= 0)
            return;
        var key = line[..eq].Trim();
        if (key.Length > 0)
            info.RawEntries.Add(new(key, line[(eq + 1)..]));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
