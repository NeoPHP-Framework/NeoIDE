using System.Collections.Specialized;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using NeoIDE.Dialogs;
using NeoIDE.Php;
using NeoIDE.Services;
using NeoIDE.ViewModels;

namespace NeoIDE;

/// <summary>Project tree: drag and drop (move, Ctrl: copy, files from the Explorer), Ctrl+C / Ctrl+X / Ctrl+V.</summary>
public partial class MainWindow
{
    private const string TreeFormat = "NeoIDE.ProjectPaths";
    private Point _dragStart;
    private FileNode? _dragNode, _dropTarget;
    private DateTime _dropHoverSince;
    private readonly List<string> _cutPaths = new();

    private void InitProjectFiles()
    {
        ProjectTree.AllowDrop = true;
        ProjectTree.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.GetPosition(ProjectTree);
            _dragNode = FindParent<ToggleButton>(e.OriginalSource as DependencyObject) == null
                ? FindParent<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as FileNode : null;
        };
        ProjectTree.PreviewMouseLeftButtonUp += (_, _) => _dragNode = null;
        ProjectTree.PreviewMouseMove += OnTreeMouseMove;
        ProjectTree.DragEnter += OnTreeDragOver;
        ProjectTree.DragOver += OnTreeDragOver;
        ProjectTree.DragLeave += (_, _) => SetDropTarget(null);
        ProjectTree.Drop += OnTreeDrop;
    }

    // ------------------------------------------------------------------ drag

    private void OnTreeMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragNode == null || _dragNode.IsRoot || Workspace.Root == null) return;
        var p = e.GetPosition(ProjectTree);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance * 2 && Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
        var selected = SelectedNodes();
        var nodes = selected.Contains(_dragNode) ? selected.Where(n => !n.IsRoot).ToList() : new List<FileNode> { _dragNode };
        _dragNode = null;
        var data = FilesData(nodes.Select(n => n.FullPath), cut: false);
        try { DragDrop.DoDragDrop(ProjectTree, data, DragDropEffects.Move | DragDropEffects.Copy); }
        finally { SetDropTarget(null); }
    }

    /// <summary>Paths for the clipboard / a drag: the Explorer gets the files, NeoIDE knows they come from the tree.</summary>
    private static DataObject FilesData(IEnumerable<string> paths, bool cut)
    {
        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        var data = new DataObject();
        data.SetFileDropList(list);
        data.SetData(TreeFormat, string.Join("\n", list.Cast<string>()));
        // "Preferred DropEffect": 2 = move (cut), 5 = copy — what the Explorer does on paste
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
        return data;
    }

    private void SetDropTarget(FileNode? node)
    {
        if (_dropTarget == node) return;
        if (_dropTarget != null) _dropTarget.IsDropTarget = false;
        _dropTarget = node;
        _dropHoverSince = DateTime.Now;
        if (node != null) node.IsDropTarget = true;
    }

    /// <summary>The directory a drop at this place goes to (a file: its directory).</summary>
    private FileNode? DropDirectory(DragEventArgs e)
    {
        var node = FindParent<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as FileNode ?? _root;
        if (node == null) return null;
        return node.IsDirectory ? node : node.Parent ?? _root;
    }

    private static List<string> DroppedPaths(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files ? files.ToList() : new List<string>();

    /// <summary>Why the files cannot go to this directory (null when they can).</summary>
    private static string? CannotMove(IEnumerable<string> sources, string directory)
    {
        foreach (var s in sources)
        {
            if (string.Equals(s, directory, StringComparison.OrdinalIgnoreCase) || directory.StartsWith(s + "\\", StringComparison.OrdinalIgnoreCase))
                return "A directory cannot go into itself";
            if (string.Equals(Path.GetDirectoryName(s), directory, StringComparison.OrdinalIgnoreCase))
                return "Already in this directory";
        }
        return null;
    }

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var paths = DroppedPaths(e.Data);
        var dir = DropDirectory(e);
        if (paths.Count == 0 || dir == null) { e.Effects = DragDropEffects.None; SetDropTarget(null); return; }
        var fromTree = e.Data.GetDataPresent(TreeFormat);
        var copy = !fromTree || (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        if (!copy && CannotMove(paths, dir.FullPath) != null) { e.Effects = DragDropEffects.None; SetDropTarget(null); return; }
        e.Effects = copy ? DragDropEffects.Copy : DragDropEffects.Move;
        SetDropTarget(dir);
        // hovering a closed directory opens it
        if (!dir.IsExpanded && (DateTime.Now - _dropHoverSince).TotalMilliseconds > 800) dir.IsExpanded = true;
    }

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var paths = DroppedPaths(e.Data);
        var dir = DropDirectory(e);
        SetDropTarget(null);
        if (paths.Count == 0 || dir == null) return;
        var fromTree = e.Data.GetDataPresent(TreeFormat);
        var copy = !fromTree || (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        if (copy) CopyInto(paths, dir.FullPath);
        else MoveInto(paths, dir.FullPath);
    }

    // ------------------------------------------------------------------ move / copy

    /// <summary>Moves files / directories of the project into a directory (references refactored, see MovePath).</summary>
    private void MoveInto(IReadOnlyList<string> paths, string directory)
    {
        paths = paths.Select(Path.GetFullPath).ToList();
        directory = Path.GetFullPath(directory);
        if (CannotMove(paths, directory) is { } why) { SetStatus(why); return; }
        foreach (var src in paths)
        {
            var node = _root?.Find(src) ?? _root?.Find(Path.GetDirectoryName(src)!)?.Children.FirstOrDefault(c => string.Equals(c.FullPath, src, StringComparison.OrdinalIgnoreCase));
            var target = Path.Combine(directory, Path.GetFileName(src));
            if (File.Exists(target) || Directory.Exists(target)) { MessageDialog.Show("Move", $"{Path.GetFileName(src)} already exists in {Rel(directory)}."); continue; }
            if (node == null)
            {
                // not loaded in the tree yet (collapsed directory): a plain move
                if (Directory.Exists(src)) Directory.Move(src, target); else File.Move(src, target);
                continue;
            }
            MovePath(node, target, "Move");
        }
    }

    private string Rel(string path) => Workspace.Root != null ? Path.GetRelativePath(Workspace.Root, path).Replace('\\', '/') : path;

    /// <summary>
    /// Copies files / directories into a directory. A copy next to the original gets a new name (asked for one file);
    /// a PHP class copied gets the namespace of its new directory and, when renamed, the new class name.
    /// </summary>
    private void CopyInto(IReadOnlyList<string> paths, string directory)
    {
        paths = paths.Select(Path.GetFullPath).ToList();
        directory = Path.GetFullPath(directory);
        var created = new List<string>();
        foreach (var src in paths)
        {
            if (!File.Exists(src) && !Directory.Exists(src)) continue;
            if (Directory.Exists(src) && (string.Equals(src, directory, StringComparison.OrdinalIgnoreCase) || directory.StartsWith(src + "\\", StringComparison.OrdinalIgnoreCase)))
            {
                SetStatus("A directory cannot be copied into itself");
                continue;
            }
            var name = Path.GetFileName(src);
            var target = Path.Combine(directory, name);
            if (File.Exists(target) || Directory.Exists(target))
            {
                var suggestion = UniqueName(directory, name);
                if (paths.Count == 1)
                {
                    var asked = InputDialog.Ask("Copy", $"{name} already exists in {Rel(directory)}. Name of the copy:", suggestion, null, ValidateNewPath(directory));
                    if (asked == null) return;
                    suggestion = asked;
                }
                target = Path.Combine(directory, suggestion);
            }
            try
            {
                if (Directory.Exists(src)) CopyDirectory(src, target);
                else File.Copy(src, target);
                FixCopiedPhp(src, target);
                created.Add(target);
            }
            catch (Exception ex) { MessageDialog.Show("Copy", ex.Message); }
        }
        if (created.Count == 0) return;
        _root?.Find(directory)?.Reload();
        NeoProject.Invalidate(created[0]);
        SelectInProject(created[0]);
        SetStatus(created.Count == 1 ? $"Copied to {Rel(created[0])}" : $"{created.Count} items copied to {Rel(directory)}/");
    }

    /// <summary>"Home.php" → "Home2.php", "Home (copy)" for directories.</summary>
    public static string UniqueName(string directory, string name)
    {
        var isDir = Directory.Exists(Path.Combine(directory, name)) && !File.Exists(Path.Combine(directory, name));
        var ext = isDir ? "" : name.EndsWith(".html.twig", StringComparison.OrdinalIgnoreCase) ? ".html.twig" : Path.GetExtension(name);
        var stem = name[..^ext.Length];
        for (int i = 2; i < 1000; i++)
        {
            var candidate = $"{stem}{i}{ext}";
            if (!File.Exists(Path.Combine(directory, candidate)) && !Directory.Exists(Path.Combine(directory, candidate))) return candidate;
        }
        return $"{stem}-{Guid.NewGuid():N}{ext}";
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var f in Directory.EnumerateFiles(source)) File.Copy(f, Path.Combine(target, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(source)) CopyDirectory(d, Path.Combine(target, Path.GetFileName(d)));
    }

    /// <summary>The PHP classes of a copy: namespace of the new directory (PSR-4), class named like the new file.</summary>
    private static void FixCopiedPhp(string source, string target)
    {
        var files = Directory.Exists(target) ? PhpIndex.EnumeratePhpFiles(target).ToList() : target.EndsWith(".php", StringComparison.OrdinalIgnoreCase) ? new List<string> { target } : new();
        foreach (var file in files)
        {
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }
            var updated = text;
            if (Workspace.NamespaceForDirectory(Path.GetDirectoryName(file)!) is { Length: > 0 } ns)
                updated = Regex.Replace(updated, @"^namespace\s+[\w\\]+\s*;", $"namespace {ns};", RegexOptions.Multiline);
            if (File.Exists(source))
            {
                var oldName = Path.GetFileNameWithoutExtension(source);
                var newName = Path.GetFileNameWithoutExtension(target);
                if (oldName != newName && Regex.IsMatch(newName, @"^[A-Za-z_]\w*$"))
                    updated = Regex.Replace(updated, @"\b(class|interface|trait|enum)\s+" + Regex.Escape(oldName) + @"\b", "$1 " + newName);
            }
            if (updated != text) File.WriteAllText(file, updated);
            Workspace.Index.UpdateFile(file);
        }
    }

    // ------------------------------------------------------------------ clipboard

    private void CopyNodes(bool cut)
    {
        var nodes = SelectedNodes().Where(n => !n.IsRoot).ToList();
        if (nodes.Count == 0) return;
        ClearCut();
        try { Clipboard.SetDataObject(FilesData(nodes.Select(n => n.FullPath), cut), true); }
        catch (Exception ex) { SetStatus("Clipboard: " + ex.Message); return; }
        if (cut)
        {
            _cutPaths.AddRange(nodes.Select(n => n.FullPath));
            foreach (var n in nodes) n.IsCut = true;
        }
        var what = nodes.Count == 1 ? nodes[0].Name : $"{nodes.Count} items";
        SetStatus(cut ? $"Cut {what}: Ctrl+V in a directory to move" : $"Copied {what}: Ctrl+V in a directory (or in the Explorer) to paste");
    }

    private void ClearCut()
    {
        foreach (var p in _cutPaths) if (_root?.Find(p) is { } n) n.IsCut = false;
        _cutPaths.Clear();
    }

    private void PasteNodes()
    {
        StringCollection files;
        try
        {
            if (!Clipboard.ContainsFileDropList()) { SetStatus("Nothing to paste: copy files first (Ctrl+C)"); return; }
            files = Clipboard.GetFileDropList();
        }
        catch (Exception ex) { SetStatus("Clipboard: " + ex.Message); return; }
        var paths = files.Cast<string>().ToList();
        var directory = SelectedDirectory();
        // cut in the tree: a move (once)
        if (_cutPaths.Count > 0 && paths.All(p => _cutPaths.Contains(p, StringComparer.OrdinalIgnoreCase)))
        {
            ClearCut();
            MoveInto(paths, directory);
            try { Clipboard.Clear(); } catch { }
            return;
        }
        CopyInto(paths, directory);
    }

    /// <summary>Ctrl+C / Ctrl+X / Ctrl+V / Escape in the tree.</summary>
    private bool TreeClipboardKey(Key key)
    {
        if (KeyboardHelper.IsAltGr) return false;
        var mods = Keyboard.Modifiers;
        switch (key)
        {
            case Key.C when mods == ModifierKeys.Control: CopyNodes(cut: false); return true;
            case Key.X when mods == ModifierKeys.Control: CopyNodes(cut: true); return true;
            case Key.V when mods == ModifierKeys.Control: PasteNodes(); return true;
            case Key.Escape when _cutPaths.Count > 0: ClearCut(); return true;
        }
        return false;
    }

    public void SelectInProjectForPreview(string path) => SelectInProject(path);

    /// <summary>Test harness: a drop (the refactoring of a move is applied without asking).</summary>
    public void DropForPreview(IReadOnlyList<string> paths, string directory, bool copy)
    {
        if (copy) { CopyInto(paths, directory); return; }
        foreach (var src in paths.Select(Path.GetFullPath))
            if (_root?.Find(src) is { } node) MovePath(node, Path.Combine(directory, Path.GetFileName(src)), "Move", confirmRefactoring: false);
    }

    private void TreeCopy_Click(object sender, RoutedEventArgs e) => CopyNodes(cut: false);
    private void TreeCut_Click(object sender, RoutedEventArgs e) => CopyNodes(cut: true);
    private void TreePaste_Click(object sender, RoutedEventArgs e) => PasteNodes();
}
