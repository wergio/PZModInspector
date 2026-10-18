using PZModInspector.Core;
using PZModInspector.Core.Environment;
using PZModInspector.Core.Localization;
using PZModInspector.Core.Settings;

namespace PZModInspector.Cli;

/// <summary>
/// PZModInspectorCLI: the analysis of PZ Mod Inspector from the command line, for a terminal or for Claude. English only, whatever the
/// language of the program. Read only on the game's files; only "refresh" sends requests to Steam.
/// </summary>
public static class Cli
{
    private static readonly string Usage = $"""
        PZModInspectorCLI {AppInfo.Version}: the analyses of PZ Mod Inspector from the command line.
        It reads the mods and the game's files read only. Workshop data comes from the program's cache:
        only "refresh" asks Steam.

        Usage: PZModInspectorCLI <command> [arguments] [options]

        Commands
          report                     summary and problems of the mod list (--info: notes too)
          mods                       the mods of the list in load order
          mod <id|Workshop id|name>  everything about a mod: data, dependencies, problems, overwritten files (--files)
          file <name|path>           which mods have a file and which copy the game uses; * and ? allowed (--all)
          overwritten                files a mod loaded later overwrites, by pair of mods
                                     (--lua, --text, --binary, --mod <mod>, --files; "conflicts" works too)
          log [file]                 errors and warnings of console.txt, grouped and linked to the mods (--errors, --mods)
          sources                    the mod lists: default.txt and the saves
          env                        paths found, game version, ignored folders
          refresh                    asks Steam for the Workshop data now (--force: recent data too)
          version                    the version of PZModInspectorCLI (also --version)

        Options
          --source <list>   default, latest, Mode/Save or just the save name
                            (without: the list opened last in the program)
          --json            result as JSON
        """;

    /// <returns>0 done, 1 wrong command line or nothing found, 2 the files or Steam could not be read.</returns>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CliHost? host = null)
    {
        // The texts shared with the window (problems, labels) come from its English strings.
        Loc.Instance.SetLanguage("en");
        try
        {
            var parsed = CliArgs.Parse(args);
            if (parsed.Command == "help")
            {
                output.WriteLine(Usage);
                return 0;
            }
            if (parsed.Command == "version")
            {
                output.WriteLine($"PZModInspectorCLI {AppInfo.Version}");
                return 0;
            }

            host ??= CliHost.Default();
            Session session;
            try
            {
                session = Session.Open(host, parsed.Source);
            }
            catch (GameCheckException ex)
            {
                // Like the window: without the installed game and its version (42 or later) nothing would be right.
                error.WriteLine(Text.GameProblem(ex.Environment));
                if (host.Settings.SteamDirectory is { } steam)
                    error.WriteLine(Loc.Instance.Format("Startup.CustomSteam", steam, AppSettings.SettingsPath));
                return 2;
            }
            var commands = new Commands(session, parsed, output);
            return parsed.Command switch
            {
                "sources" => commands.Sources(),
                "env" => commands.Env(),
                "report" => commands.Report(),
                "mods" => commands.ModList(),
                "mod" => commands.ModDetails(),
                "file" => commands.FindFile(),
                "overwritten" or "conflicts" => commands.Overwritten(),
                "log" => commands.Log(),
                "refresh" => await commands.RefreshAsync(),
                _ => throw new CliUsageException($"Unknown command: {parsed.Command}. See PZModInspectorCLI help."),
            };
        }
        catch (CliUsageException ex)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine(ex.Message);
            return 2;
        }
    }
}
