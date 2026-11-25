using System.IO;
using System.Text.Json;

namespace NeoIDE.Services;

public sealed class RecentProject
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime LastOpened { get; set; }
}

/// <summary>Global IDE settings, persisted in %APPDATA%\NeoIDE\settings.json.</summary>
public sealed class AppSettings
{
    public string Theme { get; set; } = "Neo Dark";
    public string LastDarkTheme { get; set; } = "Neo Dark";
    public string LastLightTheme { get; set; } = "Neo Light";
    public string DefaultProjectsDirectory { get; set; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "NeoPhpProjects");
    public string? DefaultPhpPath { get; set; }
    public List<string> CustomPhpPaths { get; set; } = new();
    public List<RecentProject> RecentProjects { get; set; } = new();
    public double EditorFontSize { get; set; } = 13.5;

    // ---- Appearance & display
    public double UiScale { get; set; } = 1.0;            // IDE zoom (0.8 – 2.0)
    public double UiFontSize { get; set; } = 13;
    public bool CompactMode { get; set; }
    public string EditorFontFamily { get; set; } = "JetBrains Mono";
    public bool EditorWordWrap { get; set; }
    public bool EditorShowLineNumbers { get; set; } = true;
    public bool EditorShowWhitespace { get; set; }
    public bool EditorHighlightCurrentLine { get; set; } = true;
    public bool EditorShowFolding { get; set; } = true;
    public bool ShowStatusBar { get; set; } = true;
    public bool ShowToolStrip { get; set; } = true;

    // ---- Main window placement
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public double SidePanelWidth { get; set; } = 300;
    public string MarkdownMode { get; set; } = "Split";
    public double BottomPanelHeight { get; set; } = 280;
    public string AuthorName { get; set; } = "";
    public string AuthorEmail { get; set; } = "";
    public string VendorName { get; set; } = "app";

    // ---- Updates
    public string UpdateChannel { get; set; } = "stable";   // stable | beta (pre-releases)
    public bool AutoCheckUpdates { get; set; } = true;
    public bool AutoDownloadUpdates { get; set; } = true;
    public string? DismissedUpdateVersion { get; set; }

    // ---- Plugins (ids)
    public List<string> DisabledPlugins { get; set; } = new();

    // code analysis
    public bool LintWhileTyping { get; set; } = true;     // php -l on the buffer
    public bool FormatOnSave { get; set; }               // Reformat Code on Ctrl+S
    public bool PhpStanOnSave { get; set; } = true;       // when vendor/phpstan is installed
    public int PhpStanLevel { get; set; } = 5;            // used when the project has no phpstan.neon
    public bool NeoChecks { get; set; } = true;           // unknown routes / templates / env variables

    // debugger
    public int XdebugPort { get; set; } = 9003;
    public bool BreakAtFirstLine { get; set; }

    public static string Directory
    {
        get
        {
            // NEOIDE_DATA_DIR: isolated profile (tests, portable use)
            var dir = Environment.GetEnvironmentVariable("NEOIDE_DATA_DIR") is { Length: > 0 } custom ? custom
                : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NeoIDE");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string FilePath => System.IO.Path.Combine(Directory, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Current { get; private set; } = Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* corrupted settings: start fresh */ }
        return new AppSettings();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions)); } catch { }
    }

    public void AddRecent(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        RecentProjects.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, new RecentProject { Path = path, Name = System.IO.Path.GetFileName(path), LastOpened = DateTime.Now });
        if (RecentProjects.Count > 20) RecentProjects.RemoveRange(20, RecentProjects.Count - 20);
        Save();
    }
}

/// <summary>Per-project settings stored in &lt;project&gt;/.neoide/project.json.</summary>
public sealed class ProjectSettings
{
    public string? PhpPath { get; set; }
    public int ServerPort { get; set; } = 8000;
    public List<string> OpenFiles { get; set; } = new();
    public string? ActiveFile { get; set; }
    public List<BreakpointDef> Breakpoints { get; set; } = new();
    public List<string> Watches { get; set; } = new();
    /// <summary>Path of the project on the server (Docker, VM), mapped to the project root; null = same machine.</summary>
    public string? DebugServerRoot { get; set; }

    public static ProjectSettings Load(string projectRoot)
    {
        var file = System.IO.Path.Combine(projectRoot, ".neoide", "project.json");
        try
        {
            if (File.Exists(file))
                return JsonSerializer.Deserialize<ProjectSettings>(File.ReadAllText(file)) ?? new ProjectSettings();
        }
        catch { }
        return new ProjectSettings();
    }

    public void Save(string projectRoot)
    {
        try
        {
            var dir = System.IO.Path.Combine(projectRoot, ".neoide");
            System.IO.Directory.CreateDirectory(dir);
            File.WriteAllText(System.IO.Path.Combine(dir, "project.json"),
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

/// <summary>A line breakpoint of the project (1-based line), with an optional PHP condition.</summary>
public sealed class BreakpointDef
{
    public string File { get; set; } = "";
    public int Line { get; set; }
    public string? Condition { get; set; }
    public bool Enabled { get; set; } = true;
}
