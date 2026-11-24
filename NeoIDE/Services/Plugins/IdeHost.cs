using System.Windows;
using System.Windows.Media;
using NeoIDE.Editor;
using NeoIDE.Sdk;

namespace NeoIDE.Services.Plugins;

/// <summary>What the main window does for the plugins (implemented by MainWindow).</summary>
public interface IIdeShell
{
    void AddPluginToolWindow(ToolWindow window);
    void AddPluginMenuItem(PluginMenuItem item);
    void ShowPluginToolWindow(string id);
    void OpenPluginDocument(string id, string title, Func<FrameworkElement> create, string? icon);
    void ClosePluginDocument(string id);
    void OpenFileAt(string path, int line);
    void RunNeoInConsole(string args);
    void ShowStatus(string message, NotificationKind kind);
}

public sealed record PluginMenuItem(string Header, Action OnClick, string? Icon);

/// <summary>Implementation of <see cref="IIde"/> shared by every plugin.</summary>
internal sealed class IdeHost : IIde
{
    public static readonly IdeHost Instance = new();

    private readonly List<ToolWindow> _toolWindows = new();
    private readonly List<PluginMenuItem> _menuItems = new();
    private ProjectHandle? _project;

    public IReadOnlyList<ToolWindow> ToolWindows => _toolWindows;
    public IReadOnlyList<PluginMenuItem> MenuItems => _menuItems;

    private IdeHost()
    {
        ThemeManager.ThemeChanged += () => Each(ThemeChanged, h => h());
    }

    private static IIdeShell? Shell => Application.Current?.MainWindow as IIdeShell;

    public string Version => UpdateService.CurrentVersion;
    public Window? MainWindow => Application.Current?.MainWindow as MainWindow;
    public IProject? Project => _project;
    public event Action<IProject>? ProjectOpened;
    public event Action? ProjectClosed;
    public event Action? ThemeChanged;
    public bool IsDarkTheme => ThemeManager.IsDark;

    // ---- called by MainWindow
    public void RaiseProjectOpened(string root)
    {
        _project = new ProjectHandle(root);
        var p = _project;
        Each(ProjectOpened, h => h(p));
    }

    public void RaiseProjectClosed()
    {
        if (_project == null) return;
        _project = null;
        Each(ProjectClosed, h => h());
    }

    /// <summary>One failing plugin must not break the others: each handler is invoked separately.</summary>
    private static void Each<T>(T? handler, Action<T> invoke) where T : Delegate
    {
        if (handler == null) return;
        foreach (var d in handler.GetInvocationList())
            try { invoke((T)d); } catch (Exception ex) { PluginManager.Log("event: " + ex); }
    }

    // ---- IIde
    public void AddToolWindow(ToolWindow window)
    {
        if (_toolWindows.Any(w => w.Id == window.Id)) return;
        _toolWindows.Add(window);
        Shell?.AddPluginToolWindow(window);
    }

    public void ShowToolWindow(string id) => Shell?.ShowPluginToolWindow(id);

    public void AddMenuItem(string header, Action onClick, string? icon = null)
    {
        var item = new PluginMenuItem(header, onClick, icon);
        _menuItems.Add(item);
        Shell?.AddPluginMenuItem(item);
    }

    public void OpenDocument(string id, string title, Func<FrameworkElement> create, string? icon = null) => Shell?.OpenPluginDocument(id, title, create, icon);
    public void CloseDocument(string id) => Shell?.ClosePluginDocument(id);
    public void OpenFile(string path, int line = 0) => Shell?.OpenFileAt(path, line);

    public bool GoToClass(string fqcn, string? member = null)
    {
        var cls = Workspace.Index.GetClass(fqcn.TrimStart('\\'));
        if (cls?.File == null) return false;
        var offset = cls.Offset;
        if (member != null && Workspace.Index.FindMember(cls.Fqn, member) is { } m && m.Member.File != null)
        {
            if (Application.Current.MainWindow is MainWindow mw) { mw.OpenFile(m.Member.File, m.Member.Offset); return true; }
        }
        if (Application.Current.MainWindow is MainWindow w) w.OpenFile(cls.File, offset);
        return true;
    }

    public ICodeEditor CreateCodeEditor(string language, string text = "") => new PluginCodeEditor(language, text);

    public void Notify(string message, NotificationKind kind = NotificationKind.Info) => Shell?.ShowStatus(message, kind);

    public bool Confirm(string title, string message, string ok = "OK", string cancel = "Cancel") => Dialogs.MessageDialog.Confirm(title, message, ok, cancel);
    public string? Prompt(string title, string label, string value = "") => Dialogs.InputDialog.Ask(title, label, value);
    public bool ShowDialog(string title, FrameworkElement content, string ok = "OK", string? cancel = "Cancel", Func<string?>? validate = null, double width = 460) =>
        Dialogs.ContentDialog.Show(title, content, ok, cancel, validate, width);

    public async Task<CommandResult> RunNeoAsync(string args, int timeoutMs = 120_000)
    {
        if (Workspace.Root == null) return new CommandResult(-1, "", "No project is open.");
        if (Workspace.PhpExe == null) return new CommandResult(-1, "", "No PHP interpreter is configured (NeoPHP › PHP interpreter…).");
        var (code, so, se) = await ProcessRunner.RunAsync(Workspace.PhpExe,
            $"{Workspace.NeoScript} {args} --no-ansi --no-interaction", Workspace.Root, timeoutMs);
        return new CommandResult(code, so, se);
    }

    public void RunNeoInConsole(string args) => Shell?.RunNeoInConsole(args);

    public Brush Brush(string key) => ThemeManager.Brush(key);
}

internal sealed class ProjectHandle : IProject
{
    private Dictionary<string, string> _env;

    public ProjectHandle(string root)
    {
        Root = root;
        _env = Load();
    }

    public string Root { get; }
    public string Name => System.IO.Path.GetFileName(Root);
    public string? PhpPath => Workspace.PhpExe;
    public IReadOnlyDictionary<string, string> Env => _env;
    public string? GetEnv(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } real ? real
        : _env.TryGetValue(name, out var v) ? v : null;
    public void ReloadEnv() => _env = Load();

    private Dictionary<string, string> Load()
    {
        try { return DotEnv.Load(Root); }
        catch (Exception ex) { PluginManager.Log(".env: " + ex.Message); return new(); }
    }
}

/// <summary>The IDE editor (highlighting, theme, font) wrapped for the plugins.</summary>
internal sealed class PluginCodeEditor : ICodeEditor
{
    private readonly CodeEditor _editor = new();

    public PluginCodeEditor(string language, string text)
    {
        var ext = language.ToLowerInvariant() switch
        {
            "sql" or "mysql" => ".sql", "json" => ".json", "php" => ".php", "yaml" or "yml" => ".yaml", "twig" => ".twig",
            "html" => ".html", "css" => ".css", "js" or "javascript" => ".js", "xml" => ".xml", "markdown" or "md" => ".md",
            var other => "." + other,
        };
        _editor.Load("untitled" + ext, text);
        _editor.TextChanged += (s, e) => TextChanged?.Invoke(this, EventArgs.Empty);
    }

    public FrameworkElement Element => _editor;
    public string Text { get => _editor.Text; set => _editor.Text = value; }
    public string SelectedText => _editor.SelectedText;
    public int CaretOffset { get => _editor.CaretOffset; set => _editor.CaretOffset = value; }
    public event EventHandler? TextChanged;
    public void Focus() => _editor.TextArea.Focus();
}
