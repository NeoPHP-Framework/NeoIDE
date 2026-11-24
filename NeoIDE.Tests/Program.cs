// Headless checks of NeoIDE's grammars and PHP engine: dotnet run -- <neophp-project-dir>
using System.IO;
using NeoIDE.Editor;
using NeoIDE.Php;
using NeoIDE.Services;

if (args.Length == 3 && args[0] == "--render")
{
    // Renders the IDE windows offscreen to PNG in both themes: --render <out-dir> <project-dir>
    var outDir = args[1];
    var project = args[2];
    Directory.CreateDirectory(outDir);
    // the windows persist placement / recent projects: restore the user's settings file afterwards
    var settingsFile = Path.Combine(AppSettings.Directory, "settings.json");
    var savedSettings = File.Exists(settingsFile) ? File.ReadAllText(settingsFile) : null;
    var t = new Thread(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
        // a plain Application with NeoIDE's resources (NeoIDE.App would run its startup and show the welcome screen)
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("pack://application:,,,/NeoIDE;component/Themes/Dark.xaml") });
        app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("pack://application:,,,/NeoIDE;component/Themes/Styles.xaml") });
        SyntaxRegistry.EnsureLoaded();
        DisplayManager.ApplyResources();
        void Snap(System.Windows.Window w, string name, int width, int height, Action? afterShow = null)
        {
            w.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            w.SizeToContent = System.Windows.SizeToContent.Manual;
            w.Left = -30000; w.Top = -30000; w.Width = width; w.Height = height; w.ShowActivated = false;
            w.Closed += (_, _) => Console.Error.WriteLine($"{name}: CLOSED\n{Environment.StackTrace}");
            try { w.Show(); } catch (Exception ex) { Console.Error.WriteLine($"{name}: Show threw {ex}"); }
            w.Left = -30000; w.Top = -30000; w.Width = width; w.Height = height;
            afterShow?.Invoke();
            var until = DateTime.Now.AddSeconds(name.StartsWith("main") ? 6 : 1);
            while (DateTime.Now < until)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                Thread.Sleep(30);
            }
            w.UpdateLayout();
            System.Windows.Media.Visual target = w;
            double aw = w.ActualWidth, ah = w.ActualHeight;
            Console.Error.WriteLine($"{name}: visible={w.IsVisible} loaded={w.IsLoaded} actual={aw}x{ah} children={System.Windows.Media.VisualTreeHelper.GetChildrenCount(w)} template={w.Template != null} style={w.Style != null}");
            if (aw < 1 || ah < 1)
            {
                w.ApplyTemplate();
                // fallback: lay the window template out manually
                var root = (System.Windows.FrameworkElement)System.Windows.Media.VisualTreeHelper.GetChild(w, 0);
                root.Measure(new System.Windows.Size(width, height));
                root.Arrange(new System.Windows.Rect(0, 0, width, height));
                target = root; aw = width; ah = height;
            }
            var dpi = 1.25;
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(aw * dpi), (int)(ah * dpi), 96 * dpi, 96 * dpi, System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(target);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var fs = File.Create(Path.Combine(outDir, name + ".png"))) enc.Save(fs);
            w.Hide();
        }
        var panels = new Dictionary<string, (string side, string bottom)>
        {
            ["Neo Light"] = ("project", "terminal"), ["Neo Dark"] = ("commit", "git"),
        };
        var onlyTheme = Environment.GetEnvironmentVariable("NEOIDE_RENDER_THEME");
        var onlyWhat = Environment.GetEnvironmentVariable("NEOIDE_RENDER_WHAT") ?? "main";
        foreach (var theme in panels.Keys.Where(k => onlyTheme == null || k == onlyTheme))
        {
            ThemeManager.Apply(theme);
            var tn0 = theme.Replace(" ", "").ToLowerInvariant();
            if (onlyWhat == "settings") { Snap(new NeoIDE.Dialogs.SettingsDialog("Themes"), $"settings-{tn0}", 900, 640); continue; }
            if (onlyWhat == "packages")
            {
                Workspace.Open(project);
                Workspace.PhpExe = Task.Run(() => PhpLocator.GetDefaultAsync()).GetAwaiter().GetResult()?.Path;
                Snap(new NeoIDE.Dialogs.PackagesDialog(project), $"main-packages-{tn0}", 980, 680);
                continue;
            }
            if (onlyWhat == "markdown")
            {
                var mdPath = Path.Combine(project, "README.md");
                var mdEditor = new CodeEditor();
                mdEditor.Load(mdPath, File.ReadAllText(mdPath) + "\n\n- [x] done task\n- [ ] todo task\n\n> A quote with **bold** and `code`\n\nVisit https://github.com/NeoPHP-Framework\n");
                var mdWindow = new System.Windows.Window { Content = new NeoIDE.Controls.MarkdownHost(mdEditor, mdPath), Background = ThemeManager.Brush("EditorBg") };
                Snap(mdWindow, $"main-markdown-{tn0}", 1400, 800);
                continue;
            }
            if (onlyWhat == "colors")
            {
                var cssPath = Path.Combine(project, "assets", "css", "app.css");
                var cEditor = new CodeEditor();
                cEditor.Load(cssPath, "html, css {\n    background: #FFFFFF;\n    color: #1e1f22;\n    border: 1px solid rgba(53, 116, 240, 0.6);\n}\n\na:hover {\n    color: hsl(160, 70%, 45%);\n    outline-color: tomato;\n}\n");
                var grid = new System.Windows.Controls.Grid { Background = ThemeManager.Brush("EditorBg") };
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
                grid.Children.Add(cEditor);
                var picker = new NeoIDE.Controls.ColorPicker(System.Windows.Media.Color.FromArgb(153, 53, 116, 240), new[] { System.Windows.Media.Colors.White, System.Windows.Media.Color.FromRgb(0x1e, 0x1f, 0x22) }) { VerticalAlignment = System.Windows.VerticalAlignment.Top };
                System.Windows.Controls.Grid.SetColumn(picker, 1);
                grid.Children.Add(picker);
                Snap(new System.Windows.Window { Content = grid }, $"main-colors-{tn0}", 1000, 560);
                continue;
            }
            if (onlyWhat == "search")
            {
                var sPath = Path.Combine(project, "src", "Controller", "HomeController.php");
                var sEditor = new CodeEditor();
                sEditor.Load(sPath, File.ReadAllText(sPath));
                var sWindow = new System.Windows.Window { Content = sEditor, Background = ThemeManager.Brush("EditorBg") };
                Snap(sWindow, $"main-search-{tn0}", 1000, 420, () =>
                {
                    sEditor.TextArea.Focus();
                    System.Windows.Input.ApplicationCommands.Find.Execute(null, sEditor.TextArea);
                    var panel = sEditor.TextArea.TextView.Layers.OfType<System.Windows.FrameworkElement>().SelectMany(l => new[] { l }).FirstOrDefault();
                    var sp = FindChild<ICSharpCode.AvalonEdit.Search.SearchPanel>(sWindow);
                    if (sp != null) sp.SearchPattern = "render";
                });
                continue;
            }
            if (onlyWhat == "settingsswitch")
            {
                // opened in another theme, switched while open (live preview)
                ThemeManager.Apply(theme == "Neo Dark" ? "Neo Light" : "Neo Dark");
                var dlg = new NeoIDE.Dialogs.SettingsDialog("Themes");
                Snap(dlg, $"settingsswitch-{tn0}", 900, 640, () => ThemeManager.Apply(theme));
                continue;
            }
            if (onlyWhat == "doc")
            {
                var card0 = DocPopup.Build("NeoPHP\\Package\\Security\\Helper\\Controller\\SecurityController\nprotected function getUser(): ?UserInterface\n\nReturns the user of the current request, or null when nobody is logged in.", false);
                var bg0 = new System.Windows.Controls.Border { Background = ThemeManager.Brush("EditorBg"), Padding = new System.Windows.Thickness(10), Child = card0 };
                bg0.Measure(new System.Windows.Size(720, 400));
                bg0.Arrange(new System.Windows.Rect(bg0.DesiredSize));
                bg0.UpdateLayout();
                var cb0 = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(bg0.ActualWidth * 1.25), (int)(bg0.ActualHeight * 1.25), 120, 120, System.Windows.Media.PixelFormats.Pbgra32);
                cb0.Render(bg0);
                var ce0 = new System.Windows.Media.Imaging.PngBitmapEncoder();
                ce0.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(cb0));
                using (var fs0 = File.Create(Path.Combine(outDir, $"doc-{tn0}.png"))) ce0.Save(fs0);
                continue;
            }
            var tn = theme.Replace(" ", "").ToLowerInvariant();
            Console.Error.WriteLine($"shutdown mode: {System.Windows.Application.Current.ShutdownMode}, app type {System.Windows.Application.Current.GetType().Name}");
            var main = new NeoIDE.MainWindow();
            Snap(main, $"main-{tn}", 1500, 820, () =>
            {
                main.OpenProject(project);
                main.ShowToolWindowsForPreview(panels[theme].side, panels[theme].bottom);
            });
            var rootNode0 = (main.FindName("ProjectTree") as System.Windows.Controls.TreeView)?.Items.OfType<NeoIDE.ViewModels.FileNode>().FirstOrDefault();
            var src0 = rootNode0?.Children.FirstOrDefault(c => c.Name == "src");
            Console.Error.WriteLine($"git: repo={NeoIDE.Services.Git.GitService.IsRepository} root={NeoIDE.Services.Git.GitService.Root} changes={NeoIDE.Services.Git.GitService.Status?.Changes.Count} src.HasGitBrush={src0?.HasGitBrush} srcPath={src0?.FullPath} changesPaths={string.Join("|", NeoIDE.Services.Git.GitService.Status?.Changes.Select(c => c.FullPath) ?? Array.Empty<string>())}");
            continue;
            // documentation card
            var card = DocPopup.Build("NeoPHP\\Package\\Security\\Helper\\Controller\\SecurityController\nprotected function getUser(): ?UserInterface\n\nReturns the user of the current request, or null when nobody is logged in.", false);
            var bg = new System.Windows.Controls.Border { Background = ThemeManager.Brush("EditorBg"), Padding = new System.Windows.Thickness(10), Child = card };
            bg.Measure(new System.Windows.Size(720, 400));
            bg.Arrange(new System.Windows.Rect(bg.DesiredSize));
            bg.UpdateLayout();
            var cb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(bg.ActualWidth * 1.25), (int)(bg.ActualHeight * 1.25), 120, 120, System.Windows.Media.PixelFormats.Pbgra32);
            cb.Render(bg);
            var ce = new System.Windows.Media.Imaging.PngBitmapEncoder();
            ce.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(cb));
            using (var fs = File.Create(Path.Combine(outDir, $"doc-{tn}.png"))) ce.Save(fs);
            // git colours of the tree
            var rootNode = (main.FindName("ProjectTree") as System.Windows.Controls.TreeView)?.Items.OfType<NeoIDE.ViewModels.FileNode>().FirstOrDefault();
            var src = rootNode?.Children.FirstOrDefault(c => c.Name == "src");
            Console.Error.WriteLine($"git: repo={NeoIDE.Services.Git.GitService.IsRepository} root={NeoIDE.Services.Git.GitService.Root} changes={NeoIDE.Services.Git.GitService.Status?.Changes.Count} src.HasGitBrush={src?.HasGitBrush} srcPath={src?.FullPath}");
            Snap(new NeoIDE.Dialogs.SettingsDialog("Themes"), $"settings-{tn}", 900, 640);
        }
    });
    t.SetApartmentState(ApartmentState.STA);
    t.Start();
    t.Join();
    if (savedSettings != null) File.WriteAllText(settingsFile, savedSettings);
    Console.WriteLine("rendered to " + outDir);
    return 0;
}

static T? FindChild<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
{
    var q = new Queue<System.Windows.DependencyObject>();
    q.Enqueue(root);
    while (q.Count > 0)
    {
        var d = q.Dequeue();
        if (d is T t) return t;
        foreach (var c in System.Windows.LogicalTreeHelper.GetChildren(d).OfType<System.Windows.DependencyObject>()) q.Enqueue(c);
        if (d is System.Windows.Media.Visual)
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++) q.Enqueue(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
    }
    return null;
}

if (args.Length == 2 && args[0] == "--export-themes")
{
    var descriptions = new Dictionary<string, string>
    {
        ["Neo Dark"] = "PhpStorm New UI dark colours — the default NeoIDE theme.",
        ["Neo Light"] = "PhpStorm New UI light colours.",
        ["Darcula"] = "The classic JetBrains dark theme.",
        ["Dracula"] = "Dark theme with vivid purple, pink and green accents.",
        ["One Dark"] = "Atom's One Dark palette.",
        ["Monokai"] = "The iconic Monokai colours on a warm dark background.",
        ["Nord"] = "Arctic, north-bluish colours.",
        ["GitHub Light"] = "The light colours of github.com.",
        ["Solarized Light"] = "Ethan Schoonover's Solarized, light variant.",
    };
    Directory.CreateDirectory(args[1]);
    foreach (var th in ThemeCatalog.All)
    {
        var id = string.Concat(th.Name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        var def = th with { Id = id, Version = "1.0.0", Author = "NeoPHP", Description = descriptions.GetValueOrDefault(th.Name, "") };
        File.WriteAllText(Path.Combine(args[1], id + ".json"), ThemeJson.ToJson(def));
        Console.WriteLine(id);
    }
    return 0;
}

if (args.Length == 2 && args[0] == "--create")
{
    // Mirrors the steps of the New Project wizard
    var dir = args[1];
    var phpI = (await PhpLocator.FindAllAsync()).First();
    async Task<int> Run(string what, (string exe, string a) cmd, string cwd)
    {
        var (code, so, se) = await ProcessRunner.RunAsync(cmd.exe, cmd.a, cwd, 600000);
        Console.WriteLine($"[{code}] {what}\n{se.Split('\n').TakeLast(4).Aggregate("", (x, y) => x + "   " + y + "\n")}");
        return code;
    }
    Console.WriteLine($"PHP {phpI.Version} composer.phar={ComposerService.FindComposerPhar()}");
    if (await Run("create-project", ComposerService.Command(phpI.Path, $"create-project neophp/skeleton {ProcessRunner.Quote(dir)} --no-install --no-scripts --no-interaction"), Path.GetDirectoryName(dir)!) != 0) return 1;
    ComposerService.CustomizeComposerJson(dir, "app/e2e-test", "A NeoPHP application", ">=" + phpI.ShortVersion, phpI.Version, "Test", "test@example.com");
    Console.WriteLine(File.ReadAllText(Path.Combine(dir, "composer.json")));
    if (await Run("update", ComposerService.Command(phpI.Path, "update --no-interaction --prefer-dist --optimize-autoloader"), dir) != 0) return 1;
    if (await Run("neo install", (phpI.Path, "vendor/bin/neo install --no-interaction"), dir) != 0) return 1;
    return await Run("neo doctor", (phpI.Path, "bin/neo doctor --no-ansi"), dir);
}

int failures = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail.Length == 0 ? "" : "  → " + detail)}");
    if (!ok) failures++;
}

string? uiError = null;
var thread = new Thread(() =>
{
    _ = new System.Windows.Application();
    System.Windows.Application.Current.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("pack://application:,,,/NeoIDE;component/Themes/Dark.xaml") });
    SyntaxRegistry.EnsureLoaded();
    UiSmoke();
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
Check("all grammars load", SyntaxRegistry.Errors.Count == 0, string.Join(" | ", SyntaxRegistry.Errors));
Check("UI windows, settings dialog, logo, zoom", uiError == null, uiError ?? "");

// ---- JSON themes + marketplace + updates
ThemeCatalog.Reload();
Check("bundled themes neo-dark / neo-light", ThemeCatalog.Find("neo-dark") is { IsDark: true, IsBundled: true } && ThemeCatalog.Find("neo-light") is { IsDark: false });
{
    var nd = ThemeCatalog.Find("neo-dark")!;
    var back = ThemeJson.FromJson(ThemeJson.ToJson(nd), nd, ThemeCatalog.Find("neo-light"), "installed", null);
    Check("theme JSON round-trip", back.Ui.All(kv => nd.Ui[kv.Key] == kv.Value) && back.Syntax.Count == nd.Syntax.Count && back.Editor == nd.Editor);
    var partial = ThemeJson.FromJson("""{"id":"zz-test-partial","name":"ZZ partial","version":"1.0.0","dark":false,"ui":{"Accent":"#FF0000"},"syntax":{"Keyword":{"color":"#00FF00","bold":true}}}""",
        nd, ThemeCatalog.Find("neo-light"), "installed", null);
    Check("partial theme falls back on Neo Light", partial.Ui["Accent"] == System.Windows.Media.Color.FromRgb(255, 0, 0)
        && partial.Ui["PanelBg"] == ThemeCatalog.Find("neo-light")!.Ui["PanelBg"] && partial.Syntax["Keyword"].Bold);
}
try
{
    var index = await Marketplace.FetchIndexAsync();
    Check("marketplace index online", index.Themes.Count >= 9 && index.Themes.Any(t => t.Id == "dracula"), $"{index.Themes.Count} themes");
    var item = index.Themes.First(t => t.Id == "dracula");
    var json = await Marketplace.DownloadAsync(item);
    var installed = ThemeCatalog.Install(json);
    var ok = installed.Id == "dracula" && ThemeCatalog.Find("dracula") is { IsBundled: false } && File.Exists(Path.Combine(ThemeCatalog.InstallDirectory, "dracula.json"));
    ThemeCatalog.Uninstall("dracula");
    Check("download + install + uninstall marketplace theme", ok && ThemeCatalog.Find("dracula") == null);
}
catch (Exception ex) { Check("marketplace reachable", false, ex.Message); }
Check("update service: dev build is not installed", !NeoIDE.Services.UpdateService.IsInstalled && NeoIDE.Services.UpdateService.CurrentVersion.Length > 0);

// UI smoke test (same STA thread as the Application): windows and dialogs build with the real resources
void UiSmoke()
{
    try
    {
        System.Windows.Application.Current.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("pack://application:,,,/NeoIDE;component/Themes/Styles.xaml") });
        DisplayManager.ApplyResources();
        var main = new NeoIDE.MainWindow();
        var welcome = new NeoIDE.WelcomeWindow();
        var settings = new NeoIDE.Dialogs.SettingsDialog("Editor");
        var before = AppSettings.Current.UiScale;
        DisplayManager.Zoom(1);
        DisplayManager.Zoom(0);
        if (Math.Abs(AppSettings.Current.UiScale - 1.0) > 0.001) uiError = "zoom reset failed";
        AppSettings.Current.UiScale = before;
        if (System.Windows.Application.Current.TryFindResource("NeoLogo") == null) uiError = "NeoLogo resource missing";
        if (System.Windows.Application.Current.TryFindResource("NeoIcon") is not System.Windows.Media.ImageSource) uiError = "NeoIcon resource missing";
        if (DisplayManager.MonospaceFonts().Count == 0) uiError = "no monospace fonts found";
    }
    catch (Exception ex) { uiError = ex.ToString(); }
}

var root = args.Length > 0 ? args[0] : throw new ArgumentException("project dir");
var php = (await PhpLocator.GetDefaultAsync())?.Path;
Workspace.Open(root);
Workspace.PhpExe = php;
var sw = System.Diagnostics.Stopwatch.StartNew();
await Workspace.Index.BuildAsync(root, php);
Console.WriteLine($"index: {Workspace.Index.AllClasses().Count} classes, {Workspace.Index.AllFunctions().Count} functions in {sw.ElapsedMilliseconds} ms (php {Workspace.Index.RuntimeVersion})");
Check("indexed AbstractController", Workspace.Index.GetClass(@"NeoPHP\Component\Controller\Contract\AbstractController") != null);
Check("built-in DateTime", Workspace.Index.GetClass("DateTime") != null);

var completion = new PhpCompletion(Workspace.Index, root);
var ctrlPath = Path.Combine(root, "src", "Controller", "TestController.php");
List<string> Labels(string code)
{
    var caret = code.IndexOf('|');
    var text = code.Remove(caret, 1);
    return completion.Complete(text, caret, ctrlPath, true).Entries.Select(e => e.Label).ToList();
}

const string head = "<?php\nnamespace App\\Controller;\nuse NeoPHP\\Component\\Controller\\Contract\\AbstractController;\nuse NeoPHP\\Component\\Http\\Response\\Response;\n";

var l = Labels(head + "class TestController extends AbstractController {\n public function index(): Response { $this->| } }");
Check("$this-> lists inherited render()", l.Contains("render"), string.Join(",", l.Take(30)));
Check("$this-> own method index()", l.Contains("index"));

l = Labels(head + "trait Greets { protected function hello(): string { return ''; } private function secret() {} }\n" +
    "class Base { protected int $count = 0; private $hidden; public static function make(): static { return new static(); } }\n" +
    "class Child extends Base { use Greets; public function run() { $this->| } }");
Check("trait methods via $this->", l.Contains("hello") && l.Contains("secret"), string.Join(",", l));
Check("protected parent property", l.Contains("count"));
Check("private parent property hidden", !l.Contains("hidden"));

l = Labels(head + "class Base { public static function make(): static { return new static(); } public function name(): string {return '';} }\n" +
    "class Child extends Base { public function extra() {} }\nfunction t() { $c = Child::make(); $c->| }");
Check("static return type bound to Child", l.Contains("extra") && l.Contains("name"), string.Join(",", l));

l = Labels(head + "class Repo { public function find(): ?Item {return null;} }\nclass Item { public string $title; public function save(): void {} }\n" +
    "class S { public function __construct(private Repo $repo) {} public function go() { $this->repo->find()->| } }");
Check("promoted property + chained call", l.Contains("title") && l.Contains("save"), string.Join(",", l));

l = Labels(head + "class Item { public function save(): void {} }\nfunction t(array $items) { /** @var Item[] $items */ foreach ($items as $it) { $it->| } }");
Check("foreach over @var Item[]", l.Contains("save"), string.Join(",", l));

l = Labels(head + "enum Status: string { case Draft = 'd'; case Live = 'l'; const X = 1; }\nfunction t() { Status::| }");
Check("enum cases via ::", l.Contains("Draft") && l.Contains("Live") && l.Contains("from") && l.Contains("X"), string.Join(",", l));

l = Labels(head + "function t() { $d = new \\DateTimeImmutable(); $d->| }");
Check("built-in class members", l.Contains("format") && l.Contains("modify"), string.Join(",", l.Take(20)));

var src = head + "class X { public function f() { new Kern";
var res = completion.Complete(src, src.Length, Path.Combine(root, "src", "Controller", "X.php"), false);
var kernel = res.Entries.FirstOrDefault(e => e.Label == "Kernel");
Check("class completion after new + auto-import flag", kernel != null && kernel.ImportFqn == "App\\Kernel", kernel?.ImportFqn ?? "not found");

var code = head + "class X {}";
var (name, edit) = ImportManager.PrepareClassReference(code, PhpParser.Parse(code), "App\\Kernel");
Check("use statement inserted sorted", edit != null && name == "Kernel" && code.Insert(edit.Value.Offset, edit.Value.Text).Contains("use App\\Kernel;\nuse NeoPHP"), edit?.Text ?? "null");

var c2 = head + "class X { public function a(): Response {} }";
var unused = ImportManager.FindUnused(c2, PhpParser.Parse(c2));
Check("unused import detection", unused.Count == 1 && unused[0].Alias == "AbstractController", string.Join(",", unused.Select(u => u.Alias)));

var opt = ImportManager.Optimize(c2, PhpParser.Parse(c2));
Check("optimize imports removes unused", opt != null && !c2.Remove(opt.Value.Offset, opt.Value.Length).Insert(opt.Value.Offset, opt.Value.Text).Contains("AbstractController"));

var c3 = head + "class X { public function a(Request $r) { new Kernel(); } }";
var unres = new PhpInsight(Workspace.Index).FindUnresolvedClasses(c3, PhpParser.Parse(c3));
Check("unresolved class + candidates", unres.Any(u => u.name == "Kernel" && u.candidates.Any(c => c.Fqn == "App\\Kernel")), string.Join(",", unres.Select(u => u.name)));

var c4 = head + "class X extends AbstractController { function i() { return $this->render('";
var tpl = completion.Complete(c4, c4.Length, null, false);
Check("template names in render()", tpl.Entries.Any(e => e.Label == "home/index"), string.Join(",", tpl.Entries.Select(e => e.Label)));

var c5 = head + "class X extends AbstractController { function i() { $this->render('a'); } }";
var target = new PhpInsight(Workspace.Index).Resolve(c5, c5.IndexOf("render") + 2, null);
Check("go to declaration of inherited (trait) method", target?.File?.Contains(@"\vendor\neophp\framework\") == true, target?.File ?? "null");

l = Labels(head + "class P { public function __construct(int $a) {} public static function create(): static {} }\nclass C extends P { public function __construct() { parent::| } }");
Check("parent:: offers __construct and static methods", l.Contains("__construct") && l.Contains("create"), string.Join(",", l));

var lex = PhpLexer.Tokenize("<?php $a = <<<EOT\nhello {$x}\nEOT;\n$b = 'x';");
Check("lexer heredoc", lex.Any(t => t.Kind == TokKind.String && t.Text.StartsWith("<<<")) && lex.Any(t => t.Text == "$b"));

// ---------------- strict completion, declarations, overrides, attributes, named arguments, missing methods
Check("matcher prefix / camel humps", PhpCompletion.Matches("getUser", "gU") && PhpCompletion.Matches("HomeController", "HoCo")
    && PhpCompletion.Matches("render", "ren") && !PhpCompletion.Matches("unserialize", "ser") && !PhpCompletion.Matches("array_map", "map"));

l = Labels(head + "class Plain { public function getDe| }");
Check("no completion for a method name being declared", l.Count == 0, string.Join(",", l.Take(10)));

var ovr = completion.Complete(head + "abstract class Shape { abstract protected function area(int $scale = 1): float; }\nclass Square extends Shape { protected function ar",
    (head + "abstract class Shape { abstract protected function area(int $scale = 1): float; }\nclass Square extends Shape { protected function ar").Length, ctrlPath, false);
var area = ovr.Entries.FirstOrDefault(e => e.Label == "area");
Check("function in a class offers methods to implement with signature", area?.Insert == "area(int $scale = 1): float", area?.Insert ?? string.Join(",", ovr.Entries.Select(e => e.Label)));

const string ormHead = "<?php\nnamespace App\\Entity;\nuse NeoPHP\\Package\\Orm\\Mapping as ORM;\nclass Post {\n";
var colSrc = ormHead + "    #[ORM\\Col";
var col = completion.Complete(colSrc, colSrc.Length, Path.Combine(root, "src", "Entity", "Post.php"), false);
Check("#[ORM\\Col → ORM\\Column through the alias", col.Entries.Any(e => e.Label == "ORM\\Column"), string.Join(",", col.Entries.Select(e => e.Label).Take(15)));
var argSrc = ormHead + "    #[ORM\\Column(len";
var args0 = completion.Complete(argSrc, argSrc.Length, Path.Combine(root, "src", "Entity", "Post.php"), false);
Check("named arguments of the attribute (length:)", args0.Entries.Any(e => e.Label == "length:"), string.Join(",", args0.Entries.Select(e => e.Label).Take(15)));
var args1Src = ormHead + "    #[ORM\\Column(length: 80, nu";
var args1 = completion.Complete(args1Src, args1Src.Length, Path.Combine(root, "src", "Entity", "Post.php"), false);
Check("named arguments after another one (nullable:, not length:)", args1.Entries.Any(e => e.Label == "nullable:") && !args1.Entries.Any(e => e.Label == "length:"),
    string.Join(",", args1.Entries.Select(e => e.Label).Take(15)));

var miss = head + "interface Shaped { public function area(): float; public function name(): string; }\nclass Circle implements Shaped { public function name(): string { return 'c'; } }";
var missInfo = PhpParser.Parse(miss, ctrlPath);
Workspace.Index.UpdateFromBuffer(missInfo, ctrlPath);
var missing = new PhpInsight(Workspace.Index).FindMissingImplementations(missInfo);
Check("missing interface method detected", missing.Count == 1 && missing[0].Methods.Count == 1 && missing[0].Methods[0].Name == "area",
    string.Join(",", missing.SelectMany(m => m.Methods.Select(x => x.Name))));
var stub = PhpInsight.BuildStubs(missing[0].Methods, f => f.Split('\\').Last(), "    ", "\n");
Check("generated stub", stub.Contains("public function area(): float") && stub.Contains("// TODO: Implement area() method."), stub);
var ctrlMiss = head + "class HomeController extends AbstractController { }";
var ctrlInfo = PhpParser.Parse(ctrlMiss, ctrlPath);
Workspace.Index.UpdateFromBuffer(ctrlInfo, ctrlPath);
Check("no false positive on NeoPHP's AbstractController", new PhpInsight(Workspace.Index).FindMissingImplementations(ctrlInfo).Count == 0,
    string.Join(",", new PhpInsight(Workspace.Index).FindMissingImplementations(ctrlInfo).SelectMany(m => m.Methods.Select(x => x.DeclaringClass + "::" + x.Name))));

// the reported case: last class of the file, method required by the parent interface (ViewFunctionInterface → ViewHelperInterface::getName)
var wcv = "<?php\n\nnamespace App\\Website;\n\nuse App\\Website\\WebsiteConfig;\nuse NeoPHP\\Component\\View\\Contract\\ViewFunctionInterface;\n\nclass WebsiteConfigView implements ViewFunctionInterface\n{\n    public function __construct(\n        protected WebsiteConfig $config,\n    ) {\n    }\n\n    public function __invoke(string $key): array\n    {\n        return $this->config->getConfig($key);\n    }\n}\n";
var wcvPath = Path.Combine(root, "src", "Website", "WebsiteConfigView.php");
var wcvInfo = PhpParser.Parse(wcv, wcvPath);
Workspace.Index.UpdateFromBuffer(wcvInfo, wcvPath);
var wcvMissing = new PhpInsight(Workspace.Index).FindMissingImplementations(wcvInfo);
var wcvClass = wcvInfo.Classes[0];
Check("last class of a file: body end is its closing brace", wcvClass.BodyEnd < wcv.Length && wcv[wcvClass.BodyEnd] == '}', $"BodyEnd={wcvClass.BodyEnd} len={wcv.Length}");
Check("missing getName() from the parent interface", wcvMissing.Count == 1 && wcvMissing[0].Methods.Select(m => m.Name).SequenceEqual(new[] { "getName" }),
    string.Join(",", wcvMissing.SelectMany(m => m.Methods.Select(x => x.DeclaringClass + "::" + x.Name))));
var wcvStub = PhpInsight.BuildStubs(wcvMissing[0].Methods, f => f.Split('\\').Last(), "    ", "\n");
var wcvFixed = wcv.Insert(wcvClass.BodyEnd, wcvStub);
var fixedInfo = PhpParser.Parse(wcvFixed, wcvPath);
Workspace.Index.UpdateFromBuffer(fixedInfo, wcvPath);
Check("after implementing: no missing method left", new PhpInsight(Workspace.Index).FindMissingImplementations(fixedInfo).Count == 0 && wcvFixed.Contains("public function getName(): string"), wcvFixed);
Workspace.Index.RemoveFile(wcvPath);

var flagged = new List<string>();
foreach (var file in PhpIndex.EnumeratePhpFiles(Path.Combine(root, "vendor", "neophp")))
{
    var fi = PhpParser.Parse(File.ReadAllText(file), file);
    foreach (var mm in new PhpInsight(Workspace.Index).FindMissingImplementations(fi))
        flagged.Add($"{mm.ClassFqn}: {string.Join(",", mm.Methods.Select(x => x.Name))}");
}
Check("no missing-method false positive in the NeoPHP framework", flagged.Count == 0, $"{flagged.Count}: " + string.Join(" | ", flagged.Take(8)));

// ---------------- interactive NeoConsole: NeoPHP asks the controller name, we answer on stdin
var qa = new ProcessRunner();
var transcript = new System.Text.StringBuilder();
var answered = false;
var prompts = 0;
var answers = new[] { "QaTest", "" }; // controller name, then the default response type
qa.RawOutput += (chunk, _) =>
{
    string all;
    lock (transcript) { transcript.Append(chunk); all = transcript.ToString(); }
    var count = System.Text.RegularExpressions.Regex.Matches(all, @"(^|\n) > ").Count;
    while (prompts < count && prompts < answers.Length) { qa.SendLine(answers[prompts]); prompts++; answered = true; }
};
var qaTask = qa.StartInteractiveAsync(php!, "bin/neo make:controller --no-ansi", root);
var qaCode = await Task.WhenAny(qaTask, Task.Delay(30000)) == qaTask ? qaTask.Result : -99;
if (qaCode == -99) qa.Stop();
var qaFile = Path.Combine(root, "src", "Controller", "QaTestController.php");
Check("NeoConsole answers NeoPHP questions (make:controller without name)", qaCode == 0 && answered && File.Exists(qaFile),
    $"code={qaCode} answered={answered} output={transcript.ToString().Replace("\n", " | ")[..Math.Min(300, transcript.Length)]}");
try
{
    if (File.Exists(qaFile)) File.Delete(qaFile);
    foreach (var t in Directory.GetFiles(Path.Combine(root, "templates"), "*", SearchOption.AllDirectories).Where(f => f.Contains("qa_test") || f.Contains("qa-test") || f.Contains("QaTest")))
        File.Delete(t);
    foreach (var d in Directory.GetDirectories(Path.Combine(root, "templates")).Where(d => Path.GetFileName(d) is "qa_test" or "qa-test" or "qaTest") ) Directory.Delete(d, true);
}
catch { }

// ---------------- rename refactorings
var rp = Path.Combine(Path.GetTempPath(), "neoide-refactor-" + Guid.NewGuid().ToString("N")[..8]);
void W(string rel, string content) { var f = Path.Combine(rp, rel); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllText(f, content); }
W("src/Service/Mailer.php", "<?php\nnamespace App\\Service;\n\nclass Mailer\n{\n}\n");
W("src/Service/Newsletter.php", "<?php\nnamespace App\\Service;\n\nclass Newsletter\n{\n    public function f(Mailer $m): Mailer { return new Mailer(); }\n}\n");
W("src/Controller/HomeController.php", "<?php\nnamespace App\\Controller;\n\nuse App\\Service\\Mailer;\n\nclass HomeController\n{\n    /** @var Mailer */\n    private $x;\n    public function __construct(private Mailer $mailer) {}\n    public function index() { return $this->render('home/index'); }\n}\n");
W("config/services.yaml", "services:\n    App\\Service\\Mailer: ~\n");
W("templates/home/index.php", "<h1>Home</h1>\n");
string ReadR(string p) => File.ReadAllText(p);
var rc = RefactorService.RenameClasses(rp, new Dictionary<string, string> { ["App\\Service\\Mailer"] = "App\\Service\\Postman" }, new(), ReadR);
string NewOf(string rel) => rc.FirstOrDefault(c => c.Path.EndsWith(rel.Replace('/', '\\')))?.NewText ?? "";
Check("rename class: declaration", NewOf("src/Service/Mailer.php").Contains("class Postman"), NewOf("src/Service/Mailer.php"));
Check("rename class: use, type hints and docblock", NewOf("src/Controller/HomeController.php") is var hc && hc.Contains("use App\\Service\\Postman;") && hc.Contains("private Postman $mailer") && hc.Contains("@var Postman") && !hc.Contains("Mailer;"), hc);
Check("rename class: same namespace references", NewOf("src/Service/Newsletter.php") is var nl && nl.Contains("f(Postman $m): Postman") && nl.Contains("new Postman()"), nl);
Check("rename class: services.yaml", NewOf("config/services.yaml").Contains("App\\Service\\Postman"), NewOf("config/services.yaml"));

var mv = RefactorService.RenameClasses(rp,
    new Dictionary<string, string> { ["App\\Service\\Mailer"] = "App\\Mail\\Mailer", ["App\\Service\\Newsletter"] = "App\\Mail\\Newsletter" },
    new Dictionary<string, string> { [Path.Combine(rp, "src", "Service", "Mailer.php")] = "App\\Mail", [Path.Combine(rp, "src", "Service", "Newsletter.php")] = "App\\Mail" }, ReadR);
string MvOf(string rel) => mv.FirstOrDefault(c => c.Path.EndsWith(rel.Replace('/', '\\')))?.NewText ?? "";
Check("move namespace: namespace declarations", MvOf("src/Service/Newsletter.php").Contains("namespace App\\Mail;") && MvOf("src/Service/Mailer.php").Contains("namespace App\\Mail;"), MvOf("src/Service/Newsletter.php"));
Check("move namespace: imports elsewhere", MvOf("src/Controller/HomeController.php").Contains("use App\\Mail\\Mailer;"), MvOf("src/Controller/HomeController.php"));
Check("move namespace: no needless import between moved classes", !MvOf("src/Service/Newsletter.php").Contains("use "), MvOf("src/Service/Newsletter.php"));
var tr = RefactorService.RenameTemplate(rp, "home/index", "home/welcome", ReadR);
Check("rename template: render() calls", tr.Count == 1 && tr[0].NewText.Contains("render('home/welcome')"), string.Join(",", tr.Select(t => t.Path)));
try { Directory.Delete(rp, true); } catch { }

// ---------------- colours
var cl = ColorLiterals.Find("html, css { background: #FFFFFF; }", "CSS").ToList();
Check("colour: #FFFFFF in a CSS value", cl.Count == 1 && cl[0].Color == System.Windows.Media.Colors.White && cl[0].Format == ColorFormat.Hex, string.Join(",", cl));
Check("colour: no swatch on #id selectors", !ColorLiterals.Find("#header .a:hover #add {", "CSS").Any());
var rgba = ColorLiterals.Find("color: rgba(255, 0, 0, 0.5);", "CSS").FirstOrDefault();
Check("colour: rgba with alpha", rgba.Length > 0 && rgba.Color.R == 255 && rgba.Color.A == 128, rgba.ToString());
Check("colour: named colour in a value", ColorLiterals.Find("  color: red;", "CSS").Any(c => c.Format == ColorFormat.Named && c.Color == System.Windows.Media.Colors.Red));
Check("colour: no false positives (PHP vars, anchors, JS #private)", !ColorLiterals.Find("$red = 1; $x = 'border-red';", "PHP").Any()
    && !ColorLiterals.Find("<a href=\"#face\">x</a>", "HTML").Any() && !ColorLiterals.Find("this.#cafe = 1;", "JavaScript").Any());
Check("colour: hsl parsed", ColorLiterals.Find("fill: hsl(120, 100%, 50%);", "CSS").FirstOrDefault().Color == System.Windows.Media.Color.FromRgb(0, 255, 0));
Check("colour: format keeps the notation", ColorLiterals.Format(System.Windows.Media.Color.FromRgb(0x11, 0x22, 0x33), ColorFormat.Hex, "#abc") == "#123"
    && ColorLiterals.Format(System.Windows.Media.Colors.White, ColorFormat.Hex, "#FFFFFF") == "#FFFFFF"
    && ColorLiterals.Format(System.Windows.Media.Color.FromArgb(128, 255, 0, 0), ColorFormat.Rgb, "rgb(1,2,3)") == "rgba(255, 0, 0, 0.5)"
    && ColorLiterals.Format(System.Windows.Media.Color.FromRgb(0, 255, 0), ColorFormat.Hsl, "hsl(1,1%,1%)") == "hsl(120, 100%, 50%)",
    ColorLiterals.Format(System.Windows.Media.Color.FromRgb(0x11, 0x22, 0x33), ColorFormat.Hex, "#abc"));

// ---------------- CSS completion
List<CompletionEntry> Css(string t) => CssCompletion.Complete(t, t.Length, 0, false, null).Entries;
Check("css: property names in a block", Css("a {\n    dis").Any(e => e.Label == "display" && e.Insert == "display: ;"));
Check("css: values of the property", Css("a { display: f").Any(e => e.Label == "flex") && !Css("a { display: f").Any(e => e.Label == "display"));
Check("css: colour values", Css("a { color: to").Any(e => e.Label == "tomato") && Css("a { color: ").Any(e => e.Label == "var()"));
Check("css: pseudo-classes / elements", Css("a:ho").Any(e => e.Label == "hover") && Css("p::bef").Any(e => e.Label == "before"));
Check("css: at-rules", Css("@me").Any(e => e.Label == "media"));
Check("css: CSS variables", Css(":root { --brand-color: red; }\na { color: var(--b").Any(e => e.Label == "--brand-color"));
Check("css: SCSS nested &:hover", Css(".btn {\n  &:ho").Any(e => e.Label == "hover"));
Check("css: no popup while typing a hex colour", Css("a { color: #").Count == 0);
var styleAttr = "<div style=\"col";
var emb = CssCompletion.EmbeddedCss(styleAttr, styleAttr.Length);
Check("css: style=\"\" attribute of a template", emb != null && emb.Value.declarations
    && CssCompletion.Complete(emb.Value.css, styleAttr.Length - emb.Value.start, emb.Value.start, true, null).Entries.Any(e => e.Label == "color"));
var styleBlock = "<style>\nbody { marg";
var emb2 = CssCompletion.EmbeddedCss(styleBlock, styleBlock.Length);
Check("css: <style> block of a template", emb2 != null && CssCompletion.Complete(emb2.Value.css, styleBlock.Length - emb2.Value.start, emb2.Value.start, false, null).Entries.Any(e => e.Label == "margin"));

// ---------------- line diff
var hunks = LineDiff.Compute("a\nb\nc\nd\ne", "a\nB\nc\nd\nx\ne\nf");
Check("line diff hunks", hunks.Count == 3 && hunks[0] == new DiffHunk(1, 1, 1, 1) && hunks[1] == new DiffHunk(4, 0, 4, 1) && hunks[2] == new DiffHunk(5, 0, 6, 1),
    string.Join(" ", hunks));
Check("line diff identical", LineDiff.Compute("x\ny", "x\ny").Count == 0);

// ---------------- git
var repo = Path.Combine(Path.GetTempPath(), "neoide-git-test-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(repo);
async Task<int> G(string a) => (await ProcessRunner.RunAsync("git", a, repo)).code;
await G("init -q -b main");
await G("config user.email test@example.com");
await G("config user.name Test");
File.WriteAllText(Path.Combine(repo, "a.php"), "<?php\necho 1;\n");
File.WriteAllText(Path.Combine(repo, "b.txt"), "keep\n");
await G("add -A");
await G("commit -q -m \"first commit\"");
File.WriteAllText(Path.Combine(repo, "a.php"), "<?php\necho 2;\n");
File.WriteAllText(Path.Combine(repo, "new.php"), "<?php\n");
File.Delete(Path.Combine(repo, "b.txt"));
await NeoIDE.Services.Git.GitService.OpenAsync(repo);
var gst = NeoIDE.Services.Git.GitService.Status;
Check("git repository detected", NeoIDE.Services.Git.GitService.IsRepository && gst?.Branch == "main", gst?.Branch ?? "null");
Check("git status kinds", gst != null
    && gst.Changes.Any(c => c.Path == "a.php" && c.Kind == NeoIDE.Services.Git.GitChangeKind.Modified)
    && gst.Changes.Any(c => c.Path == "new.php" && c.Kind == NeoIDE.Services.Git.GitChangeKind.Untracked)
    && gst.Changes.Any(c => c.Path == "b.txt" && c.Kind == NeoIDE.Services.Git.GitChangeKind.Deleted),
    string.Join(", ", gst?.Changes.Select(c => $"{c.Path}:{c.Kind}") ?? Array.Empty<string>()));
var headA = await NeoIDE.Services.Git.GitService.ShowFileAsync(Path.Combine(repo, "a.php"));
Check("git show HEAD content byte-exact", headA == "<?php\necho 1;\n", headA ?? "null");
var log = await NeoIDE.Services.Git.GitService.LogAsync();
Check("git log", log.Count == 1 && log[0].Subject == "first commit" && log[0].Author == "Test", log.FirstOrDefault()?.Subject ?? "none");
var files = await NeoIDE.Services.Git.GitService.CommitFilesAsync(log[0].Hash);
Check("git commit files", files.Count == 2 && files.Any(f => f.path == "a.php" && f.status == 'A'), string.Join(",", files));
var branches = await NeoIDE.Services.Git.GitService.BranchesAsync();
Check("git branches", branches.Any(b => b.Name == "main" && b.IsCurrent), string.Join(",", branches.Select(b => b.Name)));
var msg = NeoIDE.Services.Git.GitService.MessageFile("feat: second\n\nwith body");
var toCommit = gst!.Changes.Where(c => c.Path != "new.php").ToList();
await G($"add -A -- {NeoIDE.Services.Git.GitService.PathArgs(toCommit)}");
var commitCode = await G($"commit -q -F {ProcessRunner.Quote(msg)} -- {NeoIDE.Services.Git.GitService.PathArgs(toCommit)}");
await NeoIDE.Services.Git.GitService.RefreshAsync();
Check("partial commit leaves unselected files", commitCode == 0 && NeoIDE.Services.Git.GitService.Status!.Changes.Count == 1 && NeoIDE.Services.Git.GitService.Status.Changes[0].Path == "new.php",
    string.Join(",", NeoIDE.Services.Git.GitService.Status!.Changes.Select(c => c.Path)));
try { Directory.Delete(repo, true); } catch { }

Console.WriteLine(failures == 0 ? "\nALL PASSED" : $"\n{failures} FAILURE(S)");
return failures;
