using System.Windows;
using System.Windows.Media;

namespace NeoIDE.Sdk;

/// <summary>Version of the plugin API. A plugin declares the one it was built for in plugin.json ("sdk").</summary>
public static class SdkInfo
{
    public const int Version = 1;
}

/// <summary>Entry point of a plugin: the class named by "entry" in plugin.json (or the only IPlugin of the assembly).</summary>
public interface IPlugin
{
    /// <summary>Called once, on the UI thread, when NeoIDE starts (or right after the installation).</summary>
    void Activate(IPluginContext context);

    /// <summary>Called when NeoIDE exits.</summary>
    void Deactivate() { }
}

public interface IPluginContext
{
    string Id { get; }
    /// <summary>Folder of the installed plugin (its DLLs and resources).</summary>
    string Directory { get; }
    IIde Ide { get; }
    /// <summary>Settings of this plugin, persisted in %APPDATA%\NeoIDE\plugin-data\&lt;id&gt;.json.</summary>
    IPluginSettings Settings { get; }
    void Log(string message);
}

public interface IPluginSettings
{
    T? Get<T>(string key, T? fallback = default);
    void Set<T>(string key, T value);
    /// <summary>Encrypts with the Windows account (DPAPI): for passwords.</summary>
    string? GetSecret(string key);
    void SetSecret(string key, string? value);
}

public enum ToolWindowLocation { Left, Bottom }

public enum NotificationKind { Info, Success, Warning, Error }

/// <summary>A panel of the IDE (left side like Project / Commit, or bottom like Terminal / Git).</summary>
public sealed class ToolWindow
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    /// <summary>Segoe Fluent Icons glyph, e.g. "".</summary>
    public string Icon { get; init; } = "";
    public ToolWindowLocation Location { get; init; } = ToolWindowLocation.Left;
    /// <summary>Builds the content. Called once per IDE window, the first time the panel is shown.</summary>
    public required Func<FrameworkElement> Create { get; init; }
    /// <summary>Only shown when a project is open (default) — false for global tools.</summary>
    public bool RequiresProject { get; init; } = true;
}

public sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Code editor of NeoIDE (highlighting, theme, font) to embed in a plugin view.</summary>
public interface ICodeEditor
{
    FrameworkElement Element { get; }
    string Text { get; set; }
    string SelectedText { get; }
    int CaretOffset { get; set; }
    event EventHandler? TextChanged;
    void Focus();
}

/// <summary>The current NeoPHP project.</summary>
public interface IProject
{
    string Root { get; }
    string Name { get; }
    /// <summary>php.exe of the project, null if none is configured.</summary>
    string? PhpPath { get; }
    /// <summary>Environment of the application: .env → .env.local → .env.{APP_ENV} → .env.{APP_ENV}.local, %kernel.root_path% resolved.</summary>
    IReadOnlyDictionary<string, string> Env { get; }
    string? GetEnv(string name);
    /// <summary>Reloads Env after a change of the .env files.</summary>
    void ReloadEnv();
}

/// <summary>Services of NeoIDE available to the plugins.</summary>
public interface IIde
{
    string Version { get; }
    Window? MainWindow { get; }
    IProject? Project { get; }
    event Action<IProject>? ProjectOpened;
    event Action? ProjectClosed;
    event Action? ThemeChanged;
    bool IsDarkTheme { get; }

    void AddToolWindow(ToolWindow window);
    void ShowToolWindow(string id);
    /// <summary>Adds an entry to the Tools menu.</summary>
    void AddMenuItem(string header, Action onClick, string? icon = null);

    /// <summary>Opens a view in a tab of the editor area. Reuses the tab when <paramref name="id"/> is already open.</summary>
    void OpenDocument(string id, string title, Func<FrameworkElement> create, string? icon = null);
    void CloseDocument(string id);
    void OpenFile(string path, int line = 0);
    /// <summary>Opens the declaration of a PHP class (and of one of its members). False when the class is not indexed.</summary>
    bool GoToClass(string fqcn, string? member = null);

    ICodeEditor CreateCodeEditor(string language, string text = "");
    void Notify(string message, NotificationKind kind = NotificationKind.Info);

    /// <summary>Themed yes / no question.</summary>
    bool Confirm(string title, string message, string ok = "OK", string cancel = "Cancel");
    /// <summary>Themed single-line prompt; null when cancelled.</summary>
    string? Prompt(string title, string label, string value = "");
    /// <summary>Themed modal dialog around <paramref name="content"/>; <paramref name="validate"/> returns an error message or null.</summary>
    bool ShowDialog(string title, FrameworkElement content, string ok = "OK", string? cancel = "Cancel", Func<string?>? validate = null, double width = 460);

    /// <summary>php bin/neo &lt;args&gt; --no-ansi --no-interaction in the project, output captured.</summary>
    Task<CommandResult> RunNeoAsync(string args, int timeoutMs = 120_000);
    /// <summary>Runs a bin/neo command in the interactive NeoConsole (bottom panel), visible to the user.</summary>
    void RunNeoInConsole(string args);

    Brush Brush(string key);
}
