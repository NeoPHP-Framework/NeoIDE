# NeoIDE

The IDE of the [NeoPHP](https://github.com/NeoPHP-Framework) framework — a PhpStorm-like editor for Windows, built with WPF (.NET 11) and AvalonEdit.

- **New NeoPHP project** wizard (`composer create-project neophp/skeleton`, PHP version, Composer setup)
- **PHP intelligence**: completion through inheritance, traits, interfaces and abstract classes, automatic `use` imports,
  go to declaration, hover docs, missing method implementations, refactoring on rename / move
- **NeoPHP intelligence**: Ctrl+click / completion / checks of route names (`generateUrl`, `redirectToRoute`, `path()`),
  templates (`render`, `extend`, `include`, Twig `extends`), `.env` variables (`%env()%`, `$_ENV`), parameters (`%name%`)
  and services; gutter icons: route of an action (open in the browser), template rendered, controllers rendering a template,
  event of a listener
- **Template variables** (PHP views and Twig): the variables given by `render()` / `compact()` with their type — completion of
  `$post->` in a PHP view and of `post.` in Twig (getters as attribute names), filters, functions and tags, `{% for %}` /
  `{% set %}` variables, Ctrl+click to the controller or the getter, hover, unknown attribute / variable not passed checks
- **Quick fixes (Alt+Enter)**: import a class, implement methods, *did you mean* for routes, templates, .env variables,
  parameters and Twig attributes, create a missing template, add a variable to `.env` / a parameter to `services.yaml`
- **Reformat Code (Ctrl+Alt+L)**: PHP-CS-Fixer for PHP when installed (the project's `.php-cs-fixer.dist.php`, else PER-CS),
  built-in re-indentation of PHP views, Twig, HTML, CSS, JS (alternative syntax, Twig blocks…), JSON; selection only, on save
- **Live problems**: syntax check while typing (`php -l`), PHPStan on save and on the whole project (Code › Inspect Code),
  NeoPHP checks — underlined in the editor, error stripe, F2 / Shift+F2, Problems tool window (Alt+6)
- **Xdebug debugger**: breakpoints (conditional), Run › Debug `bin/neo serve` / a neo command / a script, step over / into / out,
  call stack, variables, watches, evaluate, set value, Docker path mapping (Debug tool window, Alt+5)
- **HTML** (HTML, Twig, PHP views): Emmet abbreviations with Tab (`h1`, `p.test`, `ul>li.item$*3`, `a[href=#]{Home}`, `!`),
  tag / attribute / value completion, end tag added on `>`, `</` closes the open element
- **Syntax highlighting** for PHP, Twig, HTML, CSS, JS/TS, YAML, JSON, Markdown, SQL, XML, .env… + CSS completion and colour picker
- **Git**: changes, partial commits, log, branches, diff
- **Terminal**: real terminals (ConPTY) — PowerShell, PowerShell 7, Cmd, Git Bash, WSL — with colours, history, full-screen
  programs, Ctrl+click on file paths; NeoConsole (`php bin/neo` with completion and interactive questions)
- **Composer packages** manager, Markdown preview
- **Themes** in JSON and **plugins** (Database MySQL / SQLite, Mercure, Routes, Logs, NeoPHP Toolbox) from the
  [NeoIDE Marketplace](https://github.com/NeoPHP-Framework/neo-ide-marketplace)
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
| `NeoIDE.Sdk/` | plugin API (`IPlugin`, `IIde`, UI helpers): the marketplace plugins are built against it |
| `NeoIDE.Tests/` | test harness: `NeoIDE.Tests.exe <NeoPHP project>` (engine, completion, git, themes, marketplace, NeoPHP names, php -l, terminal, DBGp with a fake Xdebug…) |
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

## Plugins

Settings › Plugins installs the plugins of the marketplace into `%APPDATA%\NeoIDE\plugins\<id>`. Each one is loaded in
its own `AssemblyLoadContext` and talks to the IDE through `NeoIDE.Sdk` (tool windows, editor tabs, Tools menu, `bin/neo`
commands, project `.env`, theme). Updating or removing an active plugin is applied at the next start.
The environment variable `NEOIDE_DATA_DIR` points NeoIDE to another profile folder (tests).

## Themes

Themes are JSON files: `Themes/Bundled/` (Neo Dark, Neo Light, shipped with the IDE) and `%APPDATA%\NeoIDE\themes\`
(installed from the marketplace or imported). Format and contribution guide:
[neo-ide-marketplace](https://github.com/NeoPHP-Framework/neo-ide-marketplace).
