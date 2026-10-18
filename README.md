# PZ Mod Inspector

A Windows program that checks your **Project Zomboid (Build 42 and later)** mods without launching the game.

Ever wanted to debug your mod problems without losing your mind? Calmly, outside the game: PZ Mod Inspector helps
you do exactly that!

It reads the game's files **read-only**: it never changes your saves or your mod folders.

## What it does

- **Finds everything by itself**: Steam, the game folder and version, and the `Zomboid` folder with your user data.
- **Sources**: shows the mods of any single player save, or the ones loaded at startup.
- **Mod list**: sortable and filterable by load order or by other columns.
- **Ignored mods**: the mod folders the game skips (for example mods for older versions of the game), with a link to
  the Workshop item so you can unsubscribe.
- **Details for each mod**: links to the Workshop, Steam and the change notes, author, versions, version folder in
  use, dependencies and their status, Workshop data (dates, size, subscribers, tags, visibility), description,
  overwritten files and the raw content of `mod.info`.
- **Problems detected**, among others:
  - mods in the list but not installed;
  - mods removed, made private or banned on the Workshop;
  - updates published but not yet downloaded by Steam (Workshop date newer than the installed one);
  - missing or unloadable dependencies, circular dependencies;
  - incompatible mods active together, `loadModAfter` / `loadModBefore` not respected;
  - `versionMin` / `versionMax` outside the game version, `mod.info` files the game rejects, mods only for older
    versions of the game;
  - the same ID in several folders (and which one wins);
  - Java mods without ZombieBuddy;
  - Workshop "Required items" you are not subscribed to;
  - **overwritten files**: when a mod loaded later overwrites a file of another mod (e.g. a `.lua`) with a different
    copy, and which copy the game actually uses.
- **Diff**: with an external compare tool, compares the copy the game uses with the one that overwrites it.

## Workshop data

It comes from the **official Steam Web API**, with a key you request from Steam (it's free) at
<https://steamcommunity.com/dev/apikey>. The key is stored encrypted and gives you all the dependency information, just
like inside the game.

**Without a key**: it works anyway, but without the dependencies (not recommended).

Settings and cache are in `%USERPROFILE%\PZModInspector` (`C:\Users\<name>\PZModInspector`).

## Command line (PZModInspectorCLI)

There is also a command line version that runs the same analyses as the program and writes the result as text (or
JSON with `--json`): use it to check your mods from a terminal **or to let AI agents check them, very handy to debug
mod problems quickly and automatically.**

| Command | What it does |
|---|---|
| `PZModInspectorCLI report` | summary and problems of the list (`--info`: notes too) |
| `PZModInspectorCLI mods` | the mods of the list in load order |
| `PZModInspectorCLI mod <id\|Workshop ID\|name>` | everything about a mod: data, dependencies, problems, overwritten files (`--files`) |
| `PZModInspectorCLI file <name\|path>` | which mods have a file and which copy the game uses (`*` and `?` allowed, `--all`) |
| `PZModInspectorCLI overwritten` | files overwritten by a mod loaded later, by pair of mods (`--lua`, `--text`, `--binary`, `--mod <mod>`, `--files`); alias: `conflicts` |
| `PZModInspectorCLI log [file]` | errors and warnings of `console.txt`, grouped and linked to the mods (`--errors`, `--mods`) |
| `PZModInspectorCLI sources` / `PZModInspectorCLI env` | available mod lists / paths, game version, ignored mods with their Workshop link |
| `PZModInspectorCLI refresh` | asks Steam for the Workshop data right away, with the key from the Settings (`--force`) |
| `PZModInspectorCLI version` | the program version (also `--version`) |

Options: `--source default|latest|<Mode>/<Save>` (without it: the list opened last in the program) and `--json`.
Exit code 0 when everything went fine, 1 for a wrong command or a mod not found, 2 when files or Steam cannot be read.

With `log`, each error is linked to the mod that caused it, and you see which copy of the Lua file actually ran and
which ones it overwrites. For `require("…") failed` errors it tells you whether the file exists but was not loaded yet
at that point, the path is wrong, or the file is missing altogether.

## Building and running it yourself

You need the .NET 10 SDK.

```bash
dotnet build
dotnet test
dotnet run --project src/PZModInspector.App
```

A single `publish\PZModInspector.exe` (it needs the .NET 10 Desktop Runtime installed) and
`publish\PZModInspectorCLI.exe` for the command line:

```bash
dotnet publish src/PZModInspector.App -p:PublishProfile=win-x64
dotnet publish src/PZModInspector.Cli -p:PublishProfile=win-x64
```

Adding `-p:SelfContained=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true`
the exe also includes the runtime and runs on any PC (about 60 MB).

## Languages

The program is in English and Italian, chosen from Windows or in the Settings. The command line is English only.

## License

The code is released under the [MIT](LICENSE) license.

Project Zomboid, Spiffo and the related trademarks belong to The Indie Stone; Steam and Steam Workshop to Valve.
This is an unofficial project, not affiliated with them, for personal use.

I'm a software engineer, but this application is made with AI, otherwise it would have taken too much time :-)