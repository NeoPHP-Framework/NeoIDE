using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NeoIDE.Php;
using NeoIDE.Services;

namespace NeoIDE.Editor;

/// <summary>Unused symbols (greyed), undefined members (underlined, Alt+Enter), usages of the methods.</summary>
public sealed partial class CodeEditor
{
    private List<UnusedSymbol> _unusedSymbols = new();
    private List<UndefinedMember> _undefined = new();
    private readonly UsageLens _usageLens = new();
    private DispatcherTimer? _usageTimer;
    private CancellationTokenSource? _usageCts;

    public IReadOnlyList<UnusedSymbol> UnusedSymbols => _unusedSymbols;
    public IReadOnlyList<UndefinedMember> UndefinedMembers => _undefined;
    public IReadOnlyDictionary<string, List<Usage>> MethodUsages => _usageLens.Usages;

    private void InitInspections()
    {
        TextArea.TextView.BackgroundRenderers.Add(_usageLens);
        PreviewMouseLeftButtonDown += OnUsageLensClick;
        MouseMove += (_, e) =>
        {
            var p = e.GetPosition(TextArea.TextView);
            var over = _usageLens.Hits.Any(h => h.rect.Contains(p));
            if (over) TextArea.Cursor = Cursors.Hand;
            else if (TextArea.Cursor == Cursors.Hand && _link.Start < 0) TextArea.Cursor = null;
        };
    }

    /// <summary>Recomputes the usages of the methods of the file in the background (on open and when PHP files are saved).</summary>
    public void RefreshUsages()
    {
        if (!IsPhp || IsPreview || FilePath == null || !AppSettings.Current.ShowUsages) return;
        _usageTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(900), DispatcherPriority.Background, async (_, _) =>
        {
            _usageTimer!.Stop();
            await ComputeUsagesAsync();
        }, Dispatcher);
        _usageTimer.Stop();
        _usageTimer.Start();
    }

    public void ClearUsages()
    {
        _usageCts?.Cancel();
        _usageLens.Usages = new(StringComparer.OrdinalIgnoreCase);
        TextArea.TextView.InvalidateLayer(_usageLens.Layer);
    }

    /// <summary>Computes the usages now (also used by the tests).</summary>
    public async Task ComputeUsagesAsync()
    {
        if (FilePath == null || Workspace.Root == null || !Workspace.Index.IsReady) return;
        _usageCts?.Cancel();
        var cts = _usageCts = new CancellationTokenSource();
        var text = Text;
        var path = FilePath;
        Dictionary<string, List<Usage>> result;
        try
        {
            result = await Task.Run(() =>
            {
                var info = PhpParser.Parse(text, path);
                var map = new Dictionary<string, List<Usage>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (cls, method) in UsageLens.Methods(info, text))
                {
                    cts.Token.ThrowIfCancellationRequested();
                    // the declaration as indexed (the saved file): usages resolve to it
                    var indexed = Workspace.Index.GetClass(cls.Fqn)?.Members.FirstOrDefault(m => m.Kind == PhpMemberKind.Method && m.Name == method.Name) ?? method;
                    var owner = Workspace.Index.GetClass(cls.Fqn) ?? cls;
                    map[method.Name] = CodeInspector.MethodUsages(indexed, owner, cts.Token);
                }
                return map;
            }, cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch { return; }
        if (cts.IsCancellationRequested) return;
        _usageLens.Usages = result;
        TextArea.TextView.InvalidateLayer(_usageLens.Layer);
    }

    private void OnUsageLensClick(object sender, MouseButtonEventArgs e)
    {
        if (_usageLens.Hits.Count == 0) return;
        var p = e.GetPosition(TextArea.TextView);
        var hit = _usageLens.Hits.FirstOrDefault(h => h.rect.Contains(p));
        if (hit.method == null || !_usageLens.Usages.TryGetValue(hit.method, out var usages)) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = TextArea.TextView, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        if (usages.Count == 0) menu.Items.Add(new MenuItem { Header = $"{hit.method}() is not used in the project".Literal(), IsEnabled = false });
        foreach (var u in usages.Take(40))
        {
            var rel = Workspace.Root != null ? Path.GetRelativePath(Workspace.Root, u.File).Replace('\\', '/') : u.File;
            var preview = u.Preview.Length > 70 ? u.Preview[..70] + "…" : u.Preview;
            var mi = new MenuItem { Header = $"{rel}:{u.Line}   {preview}".Literal() };
            var usage = u;
            mi.Click += (_, _) => NavigateRequested?.Invoke(usage.File, usage.Offset);
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    /// <summary>Alt+Enter on an undefined member: the existing names close to it.</summary>
    private IEnumerable<(string label, Action action)> UndefinedQuickFixes(int caret)
    {
        if (!IsPhp) yield break;
        var u = _undefined.FirstOrDefault(x => caret >= x.Start && caret <= x.Start + x.Length);
        if (u == null) yield break;
        foreach (var name in u.Suggestions)
        {
            var n = name;
            yield return ($"Change to '{n}'", () => Replace(u.Start, u.Length, n));
        }
    }
}
