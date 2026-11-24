# NeoIDE

The IDE of the [NeoPHP](https://github.com/NeoPHP-Framework) framework — a PhpStorm-like editor for Windows, built with WPF (.NET 11) and AvalonEdit.

- **New NeoPHP project** wizard (`composer create-project neophp/skeleton`, PHP version, Composer setup)
- **PHP intelligence**: completion through inheritance, traits, interfaces and abstract classes, automatic `use` imports,
  go to declaration, hover docs, missing method implementations, refactoring on rename / move
- **Syntax highlighting** for PHP, Twig, HTML, CSS, JS/TS, YAML, JSON, Markdown, SQL, XML, .env… + CSS completion and colour picker
- **Git**: changes, partial commits, log, branches, diff
- **Consoles**: NeoConsole (`php bin/neo` with completion and interactive questions), PowerShell, Cmd, Bash
- **Composer packages** manager, Markdown preview
- **Themes** in JSON, from the [NeoIDE Marketplace](https://github.com/NeoPHP-Framework/neo-ide-marketplace)
- **Automatic updates**: only the changed files are downloaded

## Install

Download `NeoIDE-win-Setup.exe` from the [latest release](https://github.com/NeoPHP-Framework/NeoIDE/releases/latest)
and run it (no administrator rights needed, .NET included). NeoIDE then updates itself:

1. at startup (then every 4 hours) it checks the GitHub releases;
2. a new version is downloaded in the background — **only the delta** between your version and the new one;
3. *NeoIDE x.y.z is available → Restart*; otherwise the update is applied at the next start.

Settings › Updates: stable / beta channel, automatic check and download. Help › Check for updates.

## Develop

Requirements: .NET 11 SDK, Windows. Open `NeoIDE.slnx` in Rider (or `dotnet run --project NeoIDE`).

| Project | |
|---|---|
| `NeoIDE/` | the application |
| `NeoIDE.Tests/` | test harness: `NeoIDE.Tests.exe <NeoPHP project>` (engine, completion, git, themes, marketplace…) |
| `build.ps1` | build / pack / release (see below) |

```powershell
.\build.ps1                          # standalone build: dist\NeoIDE\NeoIDE.exe + zip
.\build.ps1 -Install                 # ... installed in %LOCALAPPDATA%\Programs\NeoIDE (no auto-update)
.\build.ps1 -Pack                    # Velopack package like the CI (needs: dotnet tool install -g vpk)
```

## Release

```powershell
.\build.ps1 -Version 0.2.0 -Release          # stable
.\build.ps1 -Version 0.3.0-beta.1 -Release   # pre-release (beta channel)
```

This sets the version in `NeoIDE.csproj`, commits, tags `v0.2.0` and pushes. The **Release** workflow
(`.github/workflows/release.yml`) then publishes, packs with [Velopack](https://velopack.io) — full package + delta
against the previous release — and creates the GitHub release with the notes generated from the commits.
The installed NeoIDE pick it up automatically.

## Themes

Themes are JSON files: `Themes/Bundled/` (Neo Dark, Neo Light, shipped with the IDE) and `%APPDATA%\NeoIDE\themes\`
(installed from the marketplace or imported). Format and contribution guide:
[neo-ide-marketplace](https://github.com/NeoPHP-Framework/neo-ide-marketplace).
