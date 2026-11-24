using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using NeoIDE.Services;

namespace NeoIDE.ViewModels;

/// <summary>Lazy loaded node of the project tree.</summary>
public sealed class FileNode : INotifyPropertyChanged
{
    private bool _isExpanded, _isSelected, _loaded;
    private string? _phpKind;

    public string FullPath { get; private set; }
    public string Name { get; private set; }
    public bool IsDirectory { get; }
    public int Depth { get; }
    public FileNode? Parent { get; }
    public bool IsRoot => Parent == null && Depth == 0 && FullPath.Length > 0;
    public ObservableCollection<FileNode> Children { get; } = new();

    public Thickness Indent => new(Depth * 16, 0, 0, 0);

    private System.Windows.Media.Brush? _gitBrush;

    /// <summary>Name colour from the Git status (modified, added, unversioned...), null = default.</summary>
    public System.Windows.Media.Brush? GitBrush
    {
        get => _gitBrush;
        set { _gitBrush = value; OnChanged(); OnChanged(nameof(HasGitBrush)); }
    }

    public bool HasGitBrush => _gitBrush != null;

    /// <summary>Recomputes the Git colours of the loaded nodes.</summary>
    public void RefreshGit(Func<FileNode, System.Windows.Media.Brush?> colorOf)
    {
        GitBrush = colorOf(this);
        if (!_loaded) return;
        foreach (var c in Children) if (c.FullPath.Length > 0) c.RefreshGit(colorOf);
    }

    public string? PhpKind
    {
        get => _phpKind;
        set { _phpKind = value; OnChanged(); }
    }

    /// <summary>Gray text after the name (root path, "library root"...).</summary>
    public string? Hint => IsRoot ? FullPath : Name == "vendor" && Depth == 1 ? "library root" : null;

    public bool IsExcluded => IsDirectory && Depth == 1 && Name is "vendor" or "var" or "node_modules";

    public FileNode(string path, bool isDirectory, int depth, FileNode? parent)
    {
        FullPath = path;
        Name = depth == 0 ? Path.GetFileName(path.TrimEnd('\\')) : Path.GetFileName(path);
        IsDirectory = isDirectory;
        Depth = depth;
        Parent = parent;
        if (isDirectory && path.Length > 0) Children.Add(new FileNode("", false, depth + 1, this));
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            if (value) EnsureLoaded();
            OnChanged();
        }
    }

    private bool _isMultiSelected;

    /// <summary>Part of a Ctrl/Shift+click multi-selection.</summary>
    public bool IsMultiSelected
    {
        get => _isMultiSelected;
        set { if (_isMultiSelected != value) { _isMultiSelected = value; OnChanged(); } }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnChanged(); } }
    }

    public void EnsureLoaded()
    {
        if (_loaded || !IsDirectory) return;
        _loaded = true;
        Reload();
    }

    public void Reload()
    {
        if (!IsDirectory) return;
        _loaded = true;
        var expanded = Children.Where(c => c.IsDirectory && c.IsExpanded).Select(c => c.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Children.Clear();
        try
        {
            var dirs = Directory.GetDirectories(FullPath)
                .Where(d => !Path.GetFileName(d).Equals(".git", StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirs)
            {
                var node = new FileNode(d, true, Depth + 1, this);
                Children.Add(node);
                if (expanded.Contains(d)) node.IsExpanded = true;
            }
            var files = Directory.GetFiles(FullPath).OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                var node = new FileNode(f, false, Depth + 1, this);
                if (f.EndsWith(".php", StringComparison.OrdinalIgnoreCase)) node.PhpKind = KindFromIndex(f);
                Children.Add(node);
            }
        }
        catch { }
    }

    private static string? KindFromIndex(string file)
    {
        var info = Workspace.Index.GetFile(file);
        var cls = info?.Classes.FirstOrDefault();
        if (cls == null) return null;
        return cls.Kind switch
        {
            Php.PhpClassKind.Interface => "interface",
            Php.PhpClassKind.Trait => "trait",
            Php.PhpClassKind.Enum => "enum",
            _ => cls.IsAbstract ? "abstract" : "class",
        };
    }

    public void RefreshKinds()
    {
        foreach (var c in Children)
        {
            if (!c.IsDirectory && c.FullPath.EndsWith(".php", StringComparison.OrdinalIgnoreCase)) c.PhpKind = KindFromIndex(c.FullPath);
            else if (c.IsDirectory && c._loaded) c.RefreshKinds();
        }
    }

    public FileNode? Find(string path)
    {
        if (string.Equals(FullPath, path, StringComparison.OrdinalIgnoreCase)) return this;
        if (!IsDirectory || !path.StartsWith(FullPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return null;
        EnsureLoaded();
        foreach (var c in Children)
        {
            var r = c.Find(path);
            if (r != null) return r;
        }
        return null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
