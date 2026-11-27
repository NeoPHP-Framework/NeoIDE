// Headless checks of NeoIDE's grammars and PHP engine: dotnet run -- <neophp-project-dir>
using System.IO;
using NeoIDE.Editor;
using NeoIDE.Php;
using NeoIDE.Services;

if (args.Length == 2 && args[0] == "--inspect")
{
    // read-only: the inspections of every PHP file of src/ of a project (false positive hunting)
    Workspace.Open(args[1]);
    Workspace.PhpExe = (await PhpLocator.GetDefaultAsync())?.Path;
    await Workspace.Index.BuildAsync(args[1], Workspace.PhpExe);
    foreach (var f in Directory.EnumerateFiles(Path.Combine(args[1], "src"), "*.php", SearchOption.AllDirectories))
    {
        var text = File.ReadAllText(f);
        var info = PhpParser.Parse(text, f);
        var rel = Path.GetRelativePath(args[1], f);
        foreach (var u in CodeInspector.FindUndefined(text, info)) Console.WriteLine($"UNDEFINED {rel}:{text[..u.Start].Count(c => c == '\n') + 1}  {u.Message}");
        foreach (var u in CodeInspector.FindUnused(text, info)) Console.WriteLine($"UNUSED    {rel}:{text[..u.Start].Count(c => c == '\n') + 1}  {u.Message}");
    }
    return 0;
}

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
            var until = DateTime.Now.AddSeconds(name.StartsWith("main") ? 6 : name.StartsWith("plugins") ? 10 : name.StartsWith("v2") ? 9 : name.StartsWith("settings") ? 4 : 1);
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
        if (onlyWhat == "plugins")
        {
            // installs the zips of NEOIDE_PLUGINS_DIST (run with NEOIDE_DATA_DIR = an isolated profile!) and renders their tool windows
            if (Environment.GetEnvironmentVariable("NEOIDE_DATA_DIR") is not { Length: > 0 }) throw new InvalidOperationException("Set NEOIDE_DATA_DIR");
            NeoIDE.Services.Plugins.PluginManager.ApplyPending();
            NeoIDE.Services.Plugins.PluginManager.LoadAll();
            foreach (var zip in Directory.GetFiles(Environment.GetEnvironmentVariable("NEOIDE_PLUGINS_DIST")!, "*.zip"))
            {
                var tmp = Path.Combine(Path.GetTempPath(), "neoide-test-" + Guid.NewGuid().ToString("N"));
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, tmp);
                var p = NeoIDE.Services.Plugins.PluginManager.InstallFromFolder(tmp);
                Directory.Delete(tmp, true);
                Console.WriteLine($"PLUGIN {p.Id} {p.Manifest.Version}: {p.State} {p.Error}");
            }
            Console.WriteLine("TOOLWINDOWS " + string.Join(",", NeoIDE.Services.Plugins.IdeHost.Instance.ToolWindows.Select(w => w.Id)));
            Console.WriteLine("MENU " + string.Join(" | ", NeoIDE.Services.Plugins.IdeHost.Instance.MenuItems.Select(m => m.Header)));
            ThemeManager.Apply(onlyTheme ?? "Neo Dark");
            var combos = (Environment.GetEnvironmentVariable("NEOIDE_PLUGINS_COMBOS") ?? "database:logs,routes:mercure,toolbox:logs").Split(',');
            foreach (var combo in combos)
            {
                var parts = combo.Split(':');
                var main = new NeoIDE.MainWindow();
                app.MainWindow = main;
                Snap(main, $"plugins-{parts[0]}-{parts[1]}", 1500, 860, () =>
                {
                    main.OpenProject(project);
                    main.ShowPluginToolWindow(parts[0]);
                    main.ShowPluginToolWindow(parts[1]);
                    if (parts.Length > 2)
                        NeoIDE.Services.Plugins.IdeHost.Instance.MenuItems.First(m => m.Header.Contains(parts[2] == "console" ? "SQL" : parts[2])).OnClick();
                });
                main.Close();
            }
            File.WriteAllText(Path.Combine(outDir, "plugins.log.txt"), File.Exists(Path.Combine(AppSettings.Directory, "plugins.log")) ? File.ReadAllText(Path.Combine(AppSettings.Directory, "plugins.log")) : "");
            return;
        }
        foreach (var theme in panels.Keys.Where(k => onlyTheme == null || k == onlyTheme))
        {
            ThemeManager.Apply(theme);
            var tn0 = theme.Replace(" ", "").ToLowerInvariant();
            if (onlyWhat == "settings")
            {
                var pg = Environment.GetEnvironmentVariable("NEOIDE_RENDER_PAGE") ?? "Themes";
                var market = Environment.GetEnvironmentVariable("NEOIDE_RENDER_TAB") == "market";
                var sd = new NeoIDE.Dialogs.SettingsDialog(pg);
                Snap(sd, $"settings-{pg.ToLowerInvariant()}{(market ? "-market" : "")}-{tn0}", 900, 640, market ? sd.ShowMarketplaceTabsForPreview : null);
                continue;
            }
            if (onlyWhat == "v2")
            {
                var homeFile = Path.Combine(project, "src", "Controller", "HomeController.php");
                void After(double seconds, Action a)
                {
                    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
                    timer.Tick += (_, _) => { timer.Stop(); try { a(); } catch (Exception ex) { Console.Error.WriteLine("v2: " + ex); } };
                    timer.Start();
                }
                // 1. problems: php -l, NeoPHP checks, error stripe, Problems tool window, breakpoint marker
                var homeOriginal = File.ReadAllText(homeFile);
                var w1 = new NeoIDE.MainWindow();
                Snap(w1, $"v2-problems-{tn0}", 1500, 860, () =>
                {
                    w1.OpenProject(project);
                    After(3, () =>
                    {
                        w1.OpenFile(homeFile);
                        var ed = w1.EditorForPreview!;
                        var at = ed.Text.IndexOf("return $this->render");
                        ed.Document.Insert(at, "$url = $this->generateUrl('homee');\n        $x = 1\n        ");
                        NeoIDE.Services.Debug.Breakpoints.Add(new BreakpointDef { File = homeFile, Line = 15 });
                        w1.ShowToolWindowsForPreview("project", "problems");
                    });
                });
                foreach (var b in NeoIDE.Services.Debug.Breakpoints.All.ToList()) NeoIDE.Services.Debug.Breakpoints.Remove(b);
                w1.EditorForPreview!.Document.Text = homeOriginal;
                w1.Close();
                File.WriteAllText(homeFile, homeOriginal);

                // 2. terminal (ConPTY)
                var w2 = new NeoIDE.MainWindow();
                Snap(w2, $"v2-terminal-{tn0}", 1500, 860, () =>
                {
                    w2.OpenProject(project);
                    w2.ShowToolWindowsForPreview("project", "terminal");
                    After(2, () =>
                    {
                        var sh = w2.ShellForPreview(NeoIDE.Controls.Terminal.ShellKind.PowerShell);
                        After(2.5, () => sh.SendText("Get-ChildItem src | Select-Object Mode, Name | Format-Table -AutoSize; Write-Host 'NeoIDE terminal' -ForegroundColor Green; php -v\r"));
                    });
                });
                w2.Close();

                // 3. debugger paused by a (fake) Xdebug
                var w3 = new NeoIDE.MainWindow();
                var port = 19600 + Random.Shared.Next(300);
                var dbgpCommands = new List<string>();
                Snap(w3, $"v2-debug-{tn0}", 1500, 860, () =>
                {
                    w3.OpenProject(project);
                    After(2.5, () =>
                    {
                        NeoIDE.Services.Debug.Breakpoints.Add(new BreakpointDef { File = homeFile, Line = 16 });
                        NeoIDE.Services.Debug.DebugService.StartListening(port, out _);
                        _ = Task.Run(() => FakeXdebug.ServeAsync(port, project, homeFile, 16, dbgpCommands));
                    });
                });
                NeoIDE.Services.Debug.DebugService.StopListening();
                foreach (var b in NeoIDE.Services.Debug.Breakpoints.All.ToList()) NeoIDE.Services.Debug.Breakpoints.Remove(b);
                w3.Close();
                Console.WriteLine("DBGP " + string.Join(" | ", dbgpCommands.Select(c => c.Split(" -- ")[0])));
                continue;
            }
            if (onlyWhat == "v21")
            {
                void Later(double seconds, Action a)
                {
                    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
                    timer.Tick += (_, _) => { timer.Stop(); try { a(); } catch (Exception ex) { Console.WriteLine("FAIL  v21 exception: " + ex); } };
                    timer.Start();
                }
                void Verify(string name, bool ok, string detail = "") => Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail.Length == 0 ? "" : "  → " + detail)}");
                var fx = new Dictionary<string, string>
                {
                    ["src/Entity/V3Post.php"] = "<?php\nnamespace App\\Entity;\n\nclass V3Post\n{\n    public string $slug = '';\n    private string $title = '';\n    public function getTitle(): string { return $this->title; }\n    public function isPublished(): bool { return true; }\n    public function getAuthor(): V3Author { return new V3Author(); }\n}\n\nclass V3Author\n{\n    public function getName(): string { return ''; }\n}\n",
                    ["src/Controller/V3TestController.php"] = "<?php\nnamespace App\\Controller;\n\nuse App\\Entity\\V3Post;\nuse NeoPHP\\Component\\Controller\\Contract\\AbstractController;\n\nclass V3TestController extends AbstractController\n{\n    public function show(V3Post $post)\n    {\n        return $this->render('v3/show.html.twig', ['post' => $post, 'count' => 3]);\n    }\n\n    public function list()\n    {\n        /** @var V3Post[] $posts */\n        $posts = [];\n        return $this->render('v3/list', ['posts' => $posts, 'title' => 'Posts']);\n    }\n\n    public function created()\n    {\n        return $this->render('v3/created');\n    }\n}\n",
                    ["templates/v3/show.html.twig"] = "<article>\n    <h1>{{ post.title }}</h1>\n    <p class=\"meta\">by {{ post.author.name|upper }}{% if post.published %} · published{% endif %}</p>\n    <p>{{ post.titel }}</p>\n    <small>{{ count }} comments</small>\n</article>\n",
                    ["templates/v3/list.php"] = "<?php $this->extend('base') ?>\n<?php $this->start('content') ?>\n<h1><?= $this->e($title) ?></h1>\n<ul>\n<?php foreach ($posts as $post): ?>\n<li>\n<a href=\"/posts/<?= $post->slug ?>\"><?= $this->e($post->getTitle()) ?></a>\n</li>\n<?php endforeach ?>\n</ul>\n<?php $this->stop() ?>\n",
                };
                var envFile = Path.Combine(project, ".env");
                var envOriginal = File.ReadAllText(envFile);
                foreach (var (rel, content) in fx)
                {
                    var full = Path.Combine(project, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllText(full, content);
                }
                string P(string rel) => Path.GetFullPath(Path.Combine(project, rel));
                try
                {
                    // 1. Twig: attribute check, quick fix, navigation
                    var w1 = new NeoIDE.MainWindow();
                    Snap(w1, $"v21-twig-{tn0}", 1500, 860, () =>
                    {
                        w1.OpenProject(project);
                        Later(4, () =>
                        {
                            NeoIDE.Php.TemplateVariables.Invalidate();
                            w1.OpenFile(P("templates/v3/show.html.twig"));
                            w1.ShowToolWindowsForPreview("project", "problems");
                        });
                        Later(6.5, () =>
                        {
                            var ed = w1.EditorForPreview!;
                            Verify("v21: Twig unknown attribute underlined in the editor", ed.Problems.Any(p => p.Message.Contains("Unknown attribute 'titel'")), string.Join(" | ", ed.Problems.Select(p => p.Message)));
                            var at = ed.Text.IndexOf("titel") + 2;
                            var fixes = ed.TemplateQuickFixes(at);
                            Verify("v21: Alt+Enter on a Twig typo offers the attribute", fixes.Any(f => f.label == "Change to 'title'"), string.Join(", ", fixes.Select(f => f.label)));
                            fixes.FirstOrDefault(f => f.label == "Change to 'title'").action?.Invoke();
                            Verify("v21: the quick fix replaces the name", ed.Text.Contains("{{ post.title }}</p>") && !ed.Text.Contains("titel"));
                            var sym = ed.TemplateSymbolAt(ed.Text.IndexOf("author") + 1);
                            Verify("v21: Ctrl+click target of a Twig attribute", sym?.File?.EndsWith("V3Post.php") == true, sym?.Description ?? "null");
                            // reintroduce the typo for the screenshot
                            var i = ed.Text.IndexOf("{{ post.title }}</p>");
                            ed.Document.Replace(i, "{{ post.title }}".Length, "{{ post.titel }}");
                        });
                    });
                    w1.EditorForPreview?.Document.UndoStack.ClearAll();
                    if (w1.EditorForPreview is { } e1) e1.Document.Text = fx["templates/v3/show.html.twig"];
                    w1.Close();

                    // 2. PHP view: reformat + variables; controller: create template quick fix
                    var w2 = new NeoIDE.MainWindow();
                    Snap(w2, $"v21-view-{tn0}", 1500, 860, () =>
                    {
                        w2.OpenProject(project);
                        Later(4, () =>
                        {
                            NeoIDE.Php.TemplateVariables.Invalidate();
                            w2.OpenFile(P("src/Controller/V3TestController.php"));
                            var ctl = w2.EditorForPreview!;
                            var fixes = ctl.TemplateQuickFixes(ctl.Text.IndexOf("v3/created") + 3);
                            Verify("v21: Alt+Enter on an unknown template offers to create it", fixes.Any(f => f.label == "Create template 'v3/created'"), string.Join(", ", fixes.Select(f => f.label)));
                            fixes.FirstOrDefault(f => f.label.StartsWith("Create template")).action?.Invoke();
                            var created = P("templates/v3/created.php");
                            Verify("v21: the template is created extending the layout", File.Exists(created) && File.ReadAllText(created).Contains("$this->extend('base')") && File.ReadAllText(created).Contains("start('content')"),
                                File.Exists(created) ? File.ReadAllText(created) : "missing");
                            var envName = "V21_TEST_VAR";
                            ctl.AddEnvVariable(envName);
                            Verify("v21: Add to .env appends the variable", File.ReadAllText(envFile).EndsWith(envName + "=\n") || File.ReadAllText(envFile).EndsWith(envName + "=\r\n"));
                            File.WriteAllText(envFile, envOriginal);

                            w2.OpenFile(P("templates/v3/list.php"));
                            var view = w2.EditorForPreview!;
                            var ok = view.ReformatAsync().GetAwaiter().GetResult();
                            Verify("v21: Reformat Code re-indents the PHP view", ok && view.Text.Contains("\n    <?php foreach ($posts as $post): ?>\n        <li>\n            <a href") && view.Text.Contains("\n    <?php endforeach ?>\n</ul>"), view.Text);
                            view.Document.UndoStack.Undo();
                            Verify("v21: the reformat is one undo step", view.Text == fx["templates/v3/list.php"]);
                            view.ReformatAsync().GetAwaiter().GetResult();
                            var titleVar = view.TemplateSymbolAt(view.Text.IndexOf("$title") + 2);
                            Verify("v21: $variable of a PHP view → render() of the controller", titleVar?.Description.Contains("V3TestController::list()") == true, titleVar?.Description ?? "null");
                        });
                    });
                    if (w2.EditorForPreview is { } e2) { e2.Document.Text = fx["templates/v3/list.php"]; }
                    w2.Close();
                }
                finally
                {
                    foreach (var rel in fx.Keys) try { File.Delete(Path.Combine(project, rel)); } catch { }
                    try { File.Delete(P("templates/v3/created.php")); } catch { }
                    try { Directory.Delete(Path.Combine(project, "templates", "v3")); } catch { }
                    File.WriteAllText(envFile, envOriginal);
                }
                continue;
            }
            if (onlyWhat == "tree")
            {
                // project tree: move a PHP class to another directory (namespace + use updated), copy files
                void Ok(string name, bool ok, string detail = "") => Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail.Length == 0 ? "" : "  → " + detail.Replace("\n", "⏎"))}");
                void Later2(double seconds, Action a)
                {
                    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
                    timer.Tick += (_, _) => { timer.Stop(); try { a(); } catch (Exception ex) { Console.WriteLine("FAIL  tree exception: " + ex); } };
                    timer.Start();
                }
                var treeProject = Path.GetFullPath(project);
                var v5 = Path.Combine(treeProject, "src", "V5");
                Directory.CreateDirectory(Path.Combine(v5, "A"));
                Directory.CreateDirectory(Path.Combine(v5, "B"));
                File.WriteAllText(Path.Combine(v5, "A", "Foo.php"), "<?php\nnamespace App\\V5\\A;\n\nclass Foo\n{\n}\n");
                File.WriteAllText(Path.Combine(v5, "User.php"), "<?php\nnamespace App\\V5;\n\nuse App\\V5\\A\\Foo;\n\nclass User\n{\n    public function f(Foo $foo): void {}\n}\n");
                var tw = new NeoIDE.MainWindow();
                try
                {
                    Snap(tw, $"v2-tree-{tn0}", 1200, 760, () =>
                    {
                        tw.OpenProject(treeProject);
                        Later2(4, () =>
                        {
                            tw.SelectInProjectForPreview(Path.Combine(v5, "A", "Foo.php"));
                            tw.DropForPreview(new[] { Path.Combine(v5, "A", "Foo.php") }, Path.Combine(v5, "B"), copy: false);
                            var moved = Path.Combine(v5, "B", "Foo.php");
                            Ok("drag and drop: the file is moved", File.Exists(moved) && !File.Exists(Path.Combine(v5, "A", "Foo.php")));
                            Ok("drag and drop: the class gets the namespace of its new directory", File.Exists(moved) && File.ReadAllText(moved).Contains("namespace App\\V5\\B;"), File.Exists(moved) ? File.ReadAllText(moved) : "");
                            Ok("drag and drop: the use statements are updated", File.ReadAllText(Path.Combine(v5, "User.php")).Contains("use App\\V5\\B\\Foo;"), File.ReadAllText(Path.Combine(v5, "User.php")));
                            tw.DropForPreview(new[] { moved, Path.Combine(v5, "User.php") }, Path.Combine(v5, "B"), copy: true);
                            var copy = Path.Combine(v5, "B", "Foo2.php");
                            Ok("copy next to the original: new name, class renamed", File.Exists(copy) && File.ReadAllText(copy).Contains("class Foo2") && File.Exists(moved), File.Exists(copy) ? File.ReadAllText(copy) : string.Join(",", Directory.GetFiles(Path.Combine(v5, "B"))));
                            var userCopy = Path.Combine(v5, "B", "User.php");
                            Ok("copy into another directory: namespace of the directory", File.Exists(userCopy) && File.ReadAllText(userCopy).Contains("namespace App\\V5\\B;") && File.ReadAllText(Path.Combine(v5, "User.php")).Contains("namespace App\\V5;"));
                            tw.DropForPreview(new[] { Path.Combine(v5, "A") }, Path.Combine(v5, "B"), copy: false);
                            Ok("drag and drop: a directory is moved", Directory.Exists(Path.Combine(v5, "B", "A")) && !Directory.Exists(Path.Combine(v5, "A")));
                            tw.SelectInProjectForPreview(Path.Combine(v5, "B", "Foo2.php"));
                        });
                    });
                    Ok("unique names", NeoIDE.MainWindow.UniqueName(Path.Combine(v5, "B"), "Foo.php") == "Foo3.php");
                }
                finally
                {
                    tw.Close();
                    try { Directory.Delete(v5, true); } catch { }
                }
                continue;
            }
            if (onlyWhat == "inspections")
            {
                // greyed unused symbols, undefined members underlined, usages after the methods
                Workspace.Open(project);
                Workspace.PhpExe = Task.Run(() => PhpLocator.GetDefaultAsync()).GetAwaiter().GetResult()?.Path;
                var dir = Path.Combine(project, "src", "Service");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "V4Inspect.php");
                var caller = Path.Combine(dir, "V4Caller.php");
                File.WriteAllText(file, "<?php\nnamespace App\\Service;\n\nclass V4Inspect\n{\n    private const HOME_ROUTE = 'home_index';\n    private const UNUSED_CONST = 1;\n    private string $unusedProp = '';\n\n    public function run(int $a, int $b): string\n    {\n        $this->helper();\n        return self::HOME_INDEX . $a;\n    }\n\n    public function never(): void\n    {\n    }\n\n    private function helper(): void\n    {\n    }\n}\n");
                File.WriteAllText(caller, "<?php\nnamespace App\\Service;\n\nclass V4Caller\n{\n    public function go(V4Inspect $i): void\n    {\n        $i->run(1, 2);\n        $i->run(3, 4);\n    }\n}\n");
                Task.Run(() => Workspace.Index.BuildAsync(project, Workspace.PhpExe)).GetAwaiter().GetResult();
                var ied = new CodeEditor();
                ied.Load(file, File.ReadAllText(file));
                var iw = new System.Windows.Window { Content = ied, Background = ThemeManager.Brush("EditorBg") };
                try
                {
                    Snap(iw, $"inspections-{tn0}", 900, 520, () =>
                    {
                        _ = ied.ComputeUsagesAsync();
                    });
                    Console.WriteLine("USAGES " + string.Join(", ", ied.MethodUsages.Select(kv => $"{kv.Key}={kv.Value.Count}")));
                    Console.WriteLine("UNUSED " + string.Join(", ", ied.UnusedSymbols.Select(u => u.Message)));
                    Console.WriteLine("UNDEFINED " + string.Join(", ", ied.UndefinedMembers.Select(u => u.Message)));
                    Console.WriteLine("FIXES " + string.Join(", ", ied.TemplateQuickFixes(ied.Text.IndexOf("HOME_INDEX") + 2).Select(f => f.label)));
                }
                finally { File.Delete(file); File.Delete(caller); iw.Close(); }
                continue;
            }
            if (onlyWhat == "html")
            {
                // typing in a Twig template: Emmet with Tab, end tags on ">", "</", completion of attributes
                void Ok(string name, bool ok, string detail = "") => Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail.Length == 0 ? "" : "  → " + detail.Replace("\n", "⏎"))}");
                var page = Path.Combine(project, "templates", "emmet-test.html.twig");
                File.WriteAllText(page, "{% block body %}\n{% endblock %}\n");
                var ed = new CodeEditor();
                ed.Load(page, File.ReadAllText(page));
                var hw = new System.Windows.Window { Content = ed, Background = ThemeManager.Brush("EditorBg") };
                try
                {
                    Snap(hw, $"html-{tn0}", 900, 420, () =>
                    {
                        ed.TextArea.Focus();
                        void Type(string s) { foreach (var ch in s) ed.TextArea.PerformTextInput(ch.ToString()); }
                        var body = ed.Text.IndexOf("\n{% endblock") ;
                        ed.Document.Insert(body, "\n    ");
                        ed.CaretOffset = body + 5;
                        Type("section#posts.list>h2{Posts}+ul>li.post$*2>a");
                        var expanded = ed.TryExpandAbbreviation();
                        Ok("Tab expands an abbreviation in a Twig template", expanded && ed.Text.Contains("<section id=\"posts\" class=\"list\">\n        <h2>Posts</h2>\n        <ul>\n            <li class=\"post1\"><a href=\"\"></a></li>"), ed.Text);
                        Ok("the caret goes to the first empty attribute", ed.Text.Substring(ed.CaretOffset - 9, 9) == "<a href=\"", ed.Text.Substring(Math.Max(0, ed.CaretOffset - 12), Math.Min(12, ed.CaretOffset)));
                        ed.CaretOffset = ed.Text.IndexOf("{% endblock");
                        ed.Document.Insert(ed.CaretOffset, "    \n");
                        ed.CaretOffset -= 1;
                        Type("<p class=\"lead\">");
                        Ok("typing the > of a start tag adds its end tag", ed.Text.Contains("<p class=\"lead\"></p>") && ed.Text[ed.CaretOffset..].StartsWith("</p>"), ed.Text);
                        Type("Hi <strong>x");
                        ed.CaretOffset = ed.Text.IndexOf("</strong>") + "</strong>".Length;
                        ed.Document.Remove(ed.CaretOffset, "</p>".Length);
                        Type("</");
                        Ok("</ closes the innermost open element", ed.Text.Contains("<p class=\"lead\">Hi <strong>x</strong></p>"), ed.Text);
                        Ok("p.test + Tab", Emmet.Expand("p.test")?.Replace(Emmet.Caret.ToString(), "") == "<p class=\"test\"></p>");
                        // live templates in the same Twig file: "for" + Tab, the first field selected
                        ed.CaretOffset = ed.Text.IndexOf("{% endblock");
                        ed.Document.Insert(ed.CaretOffset, "    \n");
                        ed.CaretOffset -= 1;
                        Type("for");
                        var lt = ed.TryExpandLiveTemplate();
                        Type("post");
                        ed.TextArea.Caret.Offset = ed.TextArea.Caret.Offset; // keep the snippet mode
                        Ok("live template 'for' + Tab in Twig: the fields, indented", lt && ed.Text.Contains("    {% for post in items %}\n        \n    {% endfor %}"), ed.Text);
                        // lines selected in the body + Tab: they are indented, the caret does not jump to the field "post"
                        var inSnippet = ed.InSnippet;
                        var bodyAt = ed.Text.IndexOf("{% for post in items %}\n") + "{% for post in items %}\n".Length;
                        ed.Document.Replace(bodyAt, ed.Document.GetLineByOffset(bodyAt).Length, "        <li>\n        <a>x</a>\n        </li>");
                        ed.TextArea.Selection = ICSharpCode.AvalonEdit.Editing.Selection.Create(ed.TextArea, bodyAt, bodyAt + "        <li>\n        <a>x</a>\n        </li>".Length);
                        ed.EndSnippetOnLineSelection();
                        System.Windows.Documents.EditingCommands.TabForward.Execute(null, ed.TextArea);
                        Ok("Tab on lines selected in a live template: indents them", inSnippet && !ed.InSnippet
                            && ed.Text.Contains("{% for post in items %}\n            <li>\n            <a>x</a>\n            </li>\n    {% endfor %}"), ed.Text);
                        ed.CaretOffset = ed.Text.IndexOf("{% endblock");
                        // PHP: one field used three times is typed once
                        var phpEd = new CodeEditor();
                        phpEd.Load(Path.Combine(project, "src", "X.php"), "<?php\nfunction f() {\n    fori\n}\n");
                        phpEd.CaretOffset = phpEd.Text.IndexOf("fori") + 4;
                        var fori = phpEd.TryExpandLiveTemplate();
                        phpEd.TextArea.PerformTextInput("j");
                        Ok("live template with a field used three times (PHP fori)", fori && phpEd.Text.Contains("for ($j = 0; $j < count; $j++) {"), phpEd.Text);
                        var str = new CodeEditor();
                        str.Load(Path.Combine(project, "src", "X.php"), "<?php\n$a = 'fore';\n");
                        str.CaretOffset = str.Text.IndexOf("fore'") + 4;
                        Ok("no live template in a PHP string", !str.TryExpandLiveTemplate());
                        var view = new CodeEditor();
                        view.Load(Path.Combine(project, "templates", "home", "index.php"), "<ul>\n    for\n</ul>\n");
                        view.CaretOffset = view.Text.IndexOf("for") + 3;
                        Ok("live template of a NeoPHP view (HTML of a .php file)", view.TryExpandLiveTemplate() && view.Text.Contains("    <?php foreach ($items as $item): ?>\n        \n    <?php endforeach ?>"), view.Text);
                        // CSS: "var(" types the var() function, not one of the var(--x) proposals ranked above it
                        var cssEd = new CodeEditor();
                        hw.Content = cssEd;
                        cssEd.Load(Path.Combine(project, "assets", "css", "test.css"), ":root {\n    --gap: 4px;\n    --accent: #000;\n}\np {\n    padding: \n}\n");
                        cssEd.UpdateLayout();
                        cssEd.TextArea.Focus();
                        cssEd.CaretOffset = cssEd.Text.IndexOf("padding: ") + "padding: ".Length;
                        foreach (var ch in "var") cssEd.TextArea.PerformTextInput(ch.ToString());
                        var listBefore = cssEd.CompletionLabels;
                        Console.WriteLine("CSS LIST " + (listBefore == null ? "closed" : string.Join(",", listBefore.Take(6))));
                        cssEd.TextArea.PerformTextInput("(");
                        var afterVar = cssEd.Text[(cssEd.Text.IndexOf("padding: ") + 9)..cssEd.Text.IndexOf("\n}\n", cssEd.Text.IndexOf("padding: "))];
                        Ok("CSS: var( gives var() with the caret inside, no variable inserted", afterVar == "var()" && cssEd.Text[cssEd.CaretOffset - 1] == '(' , afterVar);
                        hw.Content = ed;
                        // indentation of the file: detected tabs, then chosen in the status bar
                        var tabEd = new CodeEditor();
                        tabEd.Load(Path.Combine(project, "src", "Tabs.php"), "<?php\nclass A\n{\n\tpublic function f()\n\t{\n\t\treturn 1;\n\t}\n}\n");
                        var detectedTabs = tabEd.Indent.UseTabs && !tabEd.Options.ConvertTabsToSpaces && tabEd.IndentUnit == "\t";
                        tabEd.SetIndent(new NeoIDE.Services.IndentResolved(2, false, "manual"));
                        Ok("indentation: tabs detected in the file, then 2 spaces chosen for it", detectedTabs && tabEd.Options.ConvertTabsToSpaces && tabEd.Options.IndentationString == "  ",
                            $"{detectedTabs} {tabEd.Indent.Label}");
                        var noExpand = new CodeEditor();
                        noExpand.Load(page, "{{ div }}");
                        noExpand.CaretOffset = 6;
                        Ok("no expansion inside a Twig expression", !noExpand.TryExpandAbbreviation());
                    });
                }
                finally { File.Delete(page); hw.Close(); }
                continue;
            }
            if (onlyWhat == "newclass")
            {
                // the dialog with the completion of "extends" open; the popup has its own window: rendered apart
                Workspace.Open(project);
                Task.Run(() => Workspace.Index.BuildAsync(project, null)).GetAwaiter().GetResult();
                var dlg = new NeoIDE.Dialogs.NewClassDialog(Path.Combine(project, "src", "Website", "Exception"), "WebsiteConfigNotFoundException");
                Snap(dlg, $"newclass-{tn0}", 580, 560, () =>
                {
                    var boxes = new List<System.Windows.Controls.TextBox>();
                    void Walk(System.Windows.DependencyObject d)
                    {
                        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
                        {
                            var ch = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
                            if (ch is System.Windows.Controls.TextBox tb) boxes.Add(tb);
                            Walk(ch);
                        }
                    }
                    dlg.UpdateLayout();
                    Walk(dlg);
                    var ext = boxes[2];
                    ext.Focus();
                    System.Windows.Input.Keyboard.Focus(ext);
                    ext.Text = "FrameEx";
                    ext.CaretIndex = ext.Text.Length;
                });
                dlg.Close();
                var preview = NeoIDE.Controls.ClassAutoComplete.Preview("FrameEx", c => c.Kind == PhpClassKind.Class && !c.IsFinal);
                var pw = new System.Windows.Window { Content = preview, Background = ThemeManager.Brush("PanelBg") };
                Snap(pw, $"newclass-popup-{tn0}", 520, 120);
                pw.Close();
                continue;
            }
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
// ---- plugin SDK helpers
{
    // the plugins reference NeoIDE.Sdk 1.0.0.0: a release build (-p:Version=x.y.z) must not change it
    Check("NeoIDE.Sdk assembly version pinned to 1.0.0.0", typeof(NeoIDE.Sdk.SdkInfo).Assembly.GetName().Version == new Version(1, 0, 0, 0),
        typeof(NeoIDE.Sdk.SdkInfo).Assembly.GetName().Version?.ToString() ?? "");
    var env = NeoIDE.Sdk.DotEnv.Load(args[0]);
    Check("DotEnv: DATABASE_URL with %kernel.root_path% resolved", env.TryGetValue("DATABASE_URL", out var dbUrl) && dbUrl.StartsWith("sqlite:///") && !dbUrl.Contains('%') && dbUrl.EndsWith("/var/data.db"), dbUrl ?? "missing");
    Check("DotEnv: quoted / inline comment / interpolation",
        NeoIDE.Sdk.DotEnv.Parse("A=\"x y\" \nB=plain # comment\nC='${A}'\nD=${A}-z\nexport E=1").Select(kv => kv.value).SequenceEqual(new[] { "x y", "plain", "${A}", "x y-z", "1" }));
    var box = NeoIDE.Sdk.ConsoleTable.Parse("Title\n+------+--------+\n| Name | Method |\n+------+--------+\n| home | GET    |\n| api  | POST   |\n+------+--------+\n");
    var ruler = NeoIDE.Sdk.ConsoleTable.Parse(" ------ -------- \n  Task   Cron   \n ------ -------- \n  clean  * * * * *\n ------ -------- \n");
    Check("ConsoleTable: box and ruler tables", box.Count == 2 && box[1]["Method"] == "POST" && ruler.Count == 1 && ruler[0]["Task"] == "clean" && ruler[0]["Cron"] == "* * * * *",
        $"box={box.Count} ruler={ruler.Count}");
}
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

// same short name as the class of the file: use ... as alias (a plain "use" would be a fatal error), never the FQN
var clash = "<?php\nnamespace App\\Website;\n\nuse NeoPHP\\Package\\Orm\\Contract\\EntityManagerInterface;\n\nclass Kernel\n{\n    public function f() { return $this->em->getRepository(Ker";
var clashRes = completion.Complete(clash, clash.Length, Path.Combine(root, "src", "Website", "Kernel.php"), false);
var clashEntry = clashRes.Entries.FirstOrDefault(e => e.Label == "Kernel" && e.ImportFqn == "App\\Kernel");
var (clashName, clashEdit) = ImportManager.PrepareClassReference(clash, PhpParser.Parse(clash), "App\\Kernel");
Check("import of a class with the same name as the file's class: aliased use",
    clashEntry?.Insert == "KernelApp" && clashName == "KernelApp" && clashEdit?.Text.Contains("use App\\Kernel as KernelApp;") == true,
    $"{clashEntry?.Insert} {clashName} {clashEdit?.Text}");
var clash2 = "<?php\nnamespace App\\Website;\n\nuse App\\Other\\Kernel;\n\nclass X {}";
var (clash2Name, clash2Edit) = ImportManager.PrepareClassReference(clash2, PhpParser.Parse(clash2), "App\\Kernel");
Check("import clashing with another import: aliased use", clash2Name == "KernelApp" && clash2Edit?.Text.Contains("use App\\Kernel as KernelApp;") == true, $"{clash2Name} {clash2Edit?.Text}");

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

// ---- 2.0: NeoPHP names, diagnostics, terminal screen, debugger protocol
{
    NeoProject.Build(args[0]);
    var home = NeoProject.Route("home");
    Check("NeoPHP routes from #[Route] attributes", home != null && home.Path == "/" && home.Methods == "GET" && home.Controller == "App\\Controller\\HomeController::index",
        home == null ? string.Join(",", NeoProject.RouteNames) : $"{home.Path} {home.Methods} {home.Controller}");
    Check("NeoPHP templates (name with and without extension)", NeoProject.Template("home/index") is { } t1 && t1.EndsWith("index.php") && NeoProject.Template("base") != null && NeoProject.Template("home/index.php") == t1);
    Check("NeoPHP .env variables", NeoProject.Env("APP_ENV") is { Count: > 0 } envDefs && envDefs[^1].Value.Length > 0 && NeoProject.EnvNames.Contains("APP_SECRET"));

    var neoPhp = "<?php\nclass X extends AbstractController {\n  function a() {\n    $this->redirectToRoute('home');\n    $this->generateUrl('homee');\n    return $this->render('home/index', []);\n  }\n  function b() { return $this->render('nope/missing'); }\n  function c() { return $this->render('a/' . $x); }\n  function d() { return $_ENV['APP_ENV'] . getenv('NOPE_VAR'); }\n}\n";
    var refs = NeoInsight.All(neoPhp, "PHP");
    Check("NeoInsight finds routes, templates, env", refs.Count(r => r.Kind == NeoRefKind.Route) == 2 && refs.Count(r => r.Kind == NeoRefKind.Template) == 3 && refs.Count(r => r.Kind == NeoRefKind.Env) == 2,
        string.Join(" | ", refs.Select(r => $"{r.Kind}:{r.Value}")));
    var problems = NeoInsight.Check(neoPhp, "PHP");
    Check("NeoPHP checks: unknown route (with suggestion) and template, dynamic name skipped",
        problems.Count == 2 && problems.Any(p => p.Message.Contains("Unknown route 'homee'") && p.Message.Contains("did you mean 'home'")) && problems.Any(p => p.Message.Contains("nope/missing")),
        string.Join(" | ", problems.Select(p => p.Message)));
    var at = NeoInsight.At(neoPhp, neoPhp.IndexOf("home/index") + 3, "PHP");
    Check("NeoInsight resolves a template under the caret", at != null && NeoInsight.Resolve(at)?.File.EndsWith("index.php") == true);
    var comp = NeoInsight.Complete("$this->redirectToRoute('')", "$this->redirectToRoute('".Length, "PHP");
    Check("NeoPHP completion of route names", comp != null && comp.Value.entries.Any(e => e.Label == "home"));
    var yaml = "app:\n  secret: '%env(APP_SECRET)%'\n  other: '%env(bool:MISSING_FLAG)%'\n  root: '%kernel.root_path%/x'\n  bad: '%unknown.param%'\n";
    var yamlProblems = NeoInsight.Check(yaml, "YAML");
    Check("YAML checks: %env()% and %parameters%", yamlProblems.Count == 2 && yamlProblems.Any(p => p.Message.Contains("MISSING_FLAG")) && yamlProblems.Any(p => p.Message.Contains("unknown.param")),
        string.Join(" | ", yamlProblems.Select(p => p.Message)));
    var twig = "{% extends 'base.html.twig' %}\n<a href=\"{{ path('home') }}\">{{ include('partials/nope') }}</a>";
    var twigRefs = NeoInsight.All(twig, "Twig");
    Check("Twig references", twigRefs.Count(r => r.Kind == NeoRefKind.Template) == 2 && twigRefs.Any(r => r.Kind == NeoRefKind.Route && r.Value == "home"), string.Join(" | ", twigRefs.Select(r => $"{r.Kind}:{r.Value}")));

    // php -l on a buffer
    Workspace.PhpExe ??= (await PhpLocator.GetDefaultAsync())?.Path;
    var lint = await NeoIDE.Services.Diagnostics.Checkers.LintAsync(Path.Combine(args[0], "src", "Broken.php"), "<?php\nfunction a() {\n  echo 1\n}\n");
    Check("php -l reports the syntax error and its line", lint.Count == 1 && lint[0].Line == 4 && lint[0].Severity == NeoIDE.Services.Diagnostics.Severity.Error, string.Join(" | ", lint.Select(d => $"{d.Line}: {d.Message}")));
    var lintOk = await NeoIDE.Services.Diagnostics.Checkers.LintAsync(Path.Combine(args[0], "src", "Ok.php"), "<?php\necho 1;\n");
    Check("php -l: valid file has no problem", lintOk.Count == 0);

    // diagnostics store
    var f = Path.Combine(args[0], "src", "X.php");
    NeoIDE.Services.Diagnostics.DiagnosticsStore.Set(f, "phpstan", new[] { new NeoIDE.Services.Diagnostics.Diagnostic(f, 3, 0, NeoIDE.Services.Diagnostics.Severity.Error, "m", "phpstan") });
    NeoIDE.Services.Diagnostics.DiagnosticsStore.Set(f, "php", new[] { new NeoIDE.Services.Diagnostics.Diagnostic(f, 2, 0, NeoIDE.Services.Diagnostics.Severity.Warning, "w", "php") });
    var cnt = NeoIDE.Services.Diagnostics.DiagnosticsStore.Count(f);
    NeoIDE.Services.Diagnostics.DiagnosticsStore.SetProject("phpstan", Array.Empty<NeoIDE.Services.Diagnostics.Diagnostic>());
    Check("diagnostics store by source", cnt == (1, 1) && NeoIDE.Services.Diagnostics.DiagnosticsStore.Count(f) == (0, 1));
    NeoIDE.Services.Diagnostics.DiagnosticsStore.Clear();

    // VT screen
    var vt = new NeoIDE.Controls.Terminal.VtScreen(20, 4);
    vt.Feed("\x1b[31mred\x1b[0m ok\r\nline2\r\n\x1b[2;3Hx\x1b[K");
    Check("VT: colours, CR/LF, cursor position, erase line",
        NeoIDE.Controls.Terminal.VtScreen.TextOf(vt.Line(0)) == "red ok" && vt.Line(0)[0].Fg == 1 && vt.Line(0)[4].Fg == -1 && NeoIDE.Controls.Terminal.VtScreen.TextOf(vt.Line(1)) == "lix",
        NeoIDE.Controls.Terminal.VtScreen.TextOf(vt.Line(0)) + "|" + NeoIDE.Controls.Terminal.VtScreen.TextOf(vt.Line(1)));
    vt.Feed("\x1b[4;1H" + "a\r\nb\r\nc\r\nd");
    Check("VT: scrolling feeds the scrollback", vt.Scrollback.Count == 3 && NeoIDE.Controls.Terminal.VtScreen.TextOf(vt.Line(3)) == "d", $"scrollback={vt.Scrollback.Count}");
    vt.Feed("\x1b[?1049h\x1b[2J\x1b[Hfull\x1b[?1049l");
    Check("VT: alternate screen restores the main screen", NeoIDE.Controls.Terminal.VtScreen.TextOf(vt.Line(3)) == "d" && !vt.AltScreen);
    string? reply = null;
    vt.Reply += r => reply = r;
    vt.Feed("\x1b[2;5H\x1b[6n\x1b]0;my title\x07\x1b[38;2;10;20;30mT");
    Check("VT: cursor report, OSC title, true colour", reply == "\x1b[2;5R" && vt.Title == "my title" && vt.Line(1)[4].Fg == (0x1000000 | (10 << 16) | (20 << 8) | 30), reply ?? "no reply");
    vt.Feed("\x1b[1;1H\x1b[2J" + new string('w', 25));
    Check("VT: auto-wrap at the last column", NeoIDE.Controls.Terminal.VtScreen.TextOf(vt.Line(1)) == "wwwww");

    // ConPTY: a real shell
    if (NeoIDE.Controls.Terminal.PseudoConsole.IsSupported)
    {
        var output = new System.Text.StringBuilder();
        var exited = new TaskCompletionSource<int>();
        using var pty = new NeoIDE.Controls.Terminal.PseudoConsole("cmd.exe /c echo NEO_PTY_%COMPUTERNAME:~0,0%OK & exit 3", args[0], 80, 25);
        pty.Output += s => { lock (output) output.Append(s); };
        pty.Exited += code => exited.TrySetResult(code);
        var done = await Task.WhenAny(exited.Task, Task.Delay(10000));
        await Task.Delay(300);
        string all;
        lock (output) all = output.ToString();
        Check("ConPTY runs a shell, streams its VT output and exit code", done == exited.Task && exited.Task.Result == 3 && all.Contains("NEO_PTY_OK"), all.Replace("\x1b", "^[").Trim());
    }

    // debugger: a fake Xdebug client speaks DBGp to the IDE
    await DbgpFakeClientTest();
}

// ---- 2.1: reformat, template variables, Twig intelligence, quick fix suggestions
{
    string Show(string s) => "\n" + s.Replace("\r", "");
    var messyClass = "<?php\nnamespace App;\nclass A\n{\npublic function b(array $x): void\n{\nif ($x) {\nforeach ($x as $y) {\necho $y;\n}\n}\n$q = $this->db\n->where('a')\n->get();\nswitch ($x) {\ncase 1:\necho 1;\nbreak;\ndefault:\necho 2;\n}\n$s = \"multi\nline\";\n/**\n* doc\n*/\n$a = [\n'k' => [\n1,\n],\n];\n}\n}\n\n\n";
    var expectedClass = "<?php\nnamespace App;\nclass A\n{\n    public function b(array $x): void\n    {\n        if ($x) {\n            foreach ($x as $y) {\n                echo $y;\n            }\n        }\n        $q = $this->db\n            ->where('a')\n            ->get();\n        switch ($x) {\n            case 1:\n                echo 1;\n                break;\n            default:\n                echo 2;\n        }\n        $s = \"multi\nline\";\n        /**\n         * doc\n         */\n        $a = [\n            'k' => [\n                1,\n            ],\n        ];\n    }\n}\n";
    var gotClass = Reindenter.Reformat(messyClass, "PHP", "    ");
    Check("reformat PHP: blocks, chains, switch cases, multi-line string, doc comment, arrays", gotClass == expectedClass, Show(gotClass));
    var messyView = "<?php $this->extend('base') ?>\n<?php $this->start('content') ?>\n<ul>\n<?php foreach ($posts as $post): ?>\n<li>\n<?= $post->getTitle() ?>\n</li>\n<?php endforeach ?>\n</ul>\n<?php if ($x): ?>\n<p>a</p>\n<?php else: ?>\n<p>b</p>\n<?php endif ?>\n<img src=\"a.png\">\n<?php $this->stop() ?>\n";
    var expectedView = "<?php $this->extend('base') ?>\n<?php $this->start('content') ?>\n<ul>\n    <?php foreach ($posts as $post): ?>\n        <li>\n            <?= $post->getTitle() ?>\n        </li>\n    <?php endforeach ?>\n</ul>\n<?php if ($x): ?>\n    <p>a</p>\n<?php else: ?>\n    <p>b</p>\n<?php endif ?>\n<img src=\"a.png\">\n<?php $this->stop() ?>\n";
    var gotView = Reindenter.Reformat(messyView, "PHP", "    ");
    Check("reformat PHP view: HTML elements, alternative syntax, else, void tags", gotView == expectedView, Show(gotView));
    var messyTwig = "{% extends 'base.html.twig' %}\n{% block body %}\n<ul>\n{% for p in posts %}\n<li>{{ p.title }}</li>\n{% else %}\n<li>none</li>\n{% endfor %}\n</ul>\n{% set x = 1 %}\n{% block title 'Short' %}\n{# a\ncomment #}\n{% endblock %}\n";
    var expectedTwig = "{% extends 'base.html.twig' %}\n{% block body %}\n    <ul>\n        {% for p in posts %}\n            <li>{{ p.title }}</li>\n        {% else %}\n            <li>none</li>\n        {% endfor %}\n    </ul>\n    {% set x = 1 %}\n    {% block title 'Short' %}\n    {# a\n    comment #}\n{% endblock %}\n";
    var gotTwig = Reindenter.Reformat(messyTwig, "Twig", "    ");
    Check("reformat Twig: blocks, for / else, short block, set, comment", gotTwig == expectedTwig, Show(gotTwig));
    var gotCss = Reindenter.Reformat("a {\ncolor: red;   \n}\n@media (x) {\nb { c: d; }\n}", "CSS", "    ");
    Check("reformat CSS", gotCss == "a {\n    color: red;\n}\n@media (x) {\n    b { c: d; }\n}\n", Show(gotCss));
    var gotYaml = Reindenter.Reformat("a:\r\n  b: 1   \r\n", "YAML", "    ");
    Check("reformat YAML: whitespace only, CRLF kept", gotYaml == "a:\r\n  b: 1\r\n", Show(gotYaml));
    var gotRange = Reindenter.Reformat("<?php\nif ($a) {\necho 1;\necho 2;\n}\n", "PHP", "    ", 3, 3);
    Check("reformat selected lines only", gotRange == "<?php\nif ($a) {\n    echo 1;\necho 2;\n}\n", Show(gotRange));
    var json = CodeFormatter.FormatJson("{\"a\":[1,2],\"b\":{\"c\":\"/x é\"}}", 4);
    Check("reformat JSON (order, unescaped / and accents)", json == "{\n    \"a\": [\n        1,\n        2\n    ],\n    \"b\": {\n        \"c\": \"/x é\"\n    }\n}\n", json ?? "null");
    Check("reformat JSON: invalid JSON is left alone", CodeFormatter.FormatJson("{\"a\":", 4) == null);

    // indentation: detection, .editorconfig, per language
    var two = "a:\n  b: 1\n  c:\n    d: 2\n  e: 3\n";
    var fourPhp = "<?php\nclass A\n{\n    /**\n     * Doc\n     */\n    public function f()\n    {\n        return 1;\n    }\n}\n";
    var tabbed = "<?php\nclass A\n{\n\tpublic function f()\n\t{\n\t\treturn 1;\n\t}\n}\n";
    Check("indentation detected: 2 spaces, 4 spaces (doc comments ignored), tabs",
        IndentStyle.Detect(two)?.Size == 2 && IndentStyle.Detect(fourPhp) is { Size: 4, UseTabs: false } && IndentStyle.Detect(tabbed)?.UseTabs == true && IndentStyle.Detect("a\nb\n") == null,
        $"{IndentStyle.Detect(two)?.Label} {IndentStyle.Detect(fourPhp)?.Label} {IndentStyle.Detect(tabbed)?.Label}");
    Check(".editorconfig globs", EditorConfig.Matches("*", "src/A.php") && EditorConfig.Matches("*.{yml,yaml}", "config/a.yaml") && !EditorConfig.Matches("*.{yml,yaml}", "a.php")
        && EditorConfig.Matches("src/**.php", "src/x/y.php") && !EditorConfig.Matches("src/*.php", "src/x/y.php") && EditorConfig.Matches("Makefile", "sub/Makefile") && EditorConfig.Matches("/composer.json", "composer.json"));
    var ecDir = Path.Combine(Path.GetTempPath(), "neoide-ec-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(ecDir, "config"));
    File.WriteAllText(Path.Combine(ecDir, ".editorconfig"), "root = true\n\n[*]\nindent_style = space\nindent_size = 4\n\n[*.{yml,yaml}]\nindent_size = 2\n\n[Makefile]\nindent_style = tab\n");
    File.WriteAllText(Path.Combine(ecDir, "config", ".editorconfig"), "[special.yaml]\nindent_size = 3\n");
    try
    {
        var ecYaml = EditorConfig.IndentFor(Path.Combine(ecDir, "config", "a.yaml"));
        var ecSpecial = EditorConfig.IndentFor(Path.Combine(ecDir, "config", "special.yaml"));
        var ecMake = EditorConfig.IndentFor(Path.Combine(ecDir, "Makefile"));
        Check(".editorconfig: sections, the closest file wins, tabs", ecYaml is { Size: 2, UseTabs: false } && ecSpecial?.Size == 3 && ecMake?.UseTabs == true,
            $"{ecYaml?.Label} {ecSpecial?.Label} {ecMake?.Label}");
        var st = new AppSettings { IndentSize = 4, DetectIndent = true, UseEditorConfig = true, LanguageIndent = new() { ["YAML"] = new IndentSetting { Size = 8 } } };
        var r1 = IndentStyle.For("YAML", Path.Combine(ecDir, "config", "a.yaml"), "a:\n    b: 1\n    c: 2\n    d: 3\n", st);
        st.UseEditorConfig = false;
        var r2 = IndentStyle.For("YAML", Path.Combine(ecDir, "config", "a.yaml"), "a:\n    b: 1\n    c: 2\n    d: 3\n", st);
        st.DetectIndent = false;
        var r3 = IndentStyle.For("YAML", Path.Combine(ecDir, "config", "a.yaml"), "a:\n    b: 1\n", st);
        var r4 = IndentStyle.For("PHP", Path.Combine(ecDir, "a.php"), "", st);
        Check("indentation priority: .editorconfig > detected > language > general",
            r1 is { Size: 2, Source: ".editorconfig" } && r2 is { Size: 4, Source: "detected" } && r3 is { Size: 8, Source: "YAML" } && r4 is { Size: 4, Source: "general" },
            $"{r1.Label}/{r1.Source} {r2.Label}/{r2.Source} {r3.Label}/{r3.Source} {r4.Label}/{r4.Source}");
    }
    finally { try { Directory.Delete(ecDir, true); } catch { } }

    // live templates: defaults, body syntax, contexts
    var defaults = LiveTemplates.Defaults();
    Check("live templates: defaults are unique per context", defaults.GroupBy(t => t.Context + "|" + t.Abbreviation).All(g => g.Count() == 1),
        string.Join(", ", defaults.GroupBy(t => t.Context + "|" + t.Abbreviation).Where(g => g.Count() > 1).Select(g => g.Key)));
    string Pv(string context, string abbr) => LiveTemplates.Preview(defaults.First(t => t.Context == context && t.Abbreviation == abbr).Body);
    Check("live templates: Twig for / block / ext", Pv("Twig", "for") == "{% for item in items %}\n    \n{% endfor %}" && Pv("Twig", "ext").StartsWith("{% extends 'base.html.twig' %}"),
        Pv("Twig", "for"));
    Check("live templates: $$ is a $ (PHP foreach, NeoPHP view)", Pv("PHP", "fore") == "foreach ($items as $item) {\n    \n}" && Pv("PHP view", "e") == "<?= $this->e() ?>",
        Pv("PHP", "fore") + " | " + Pv("PHP view", "e"));
    var parts = LiveTemplates.Parse("a $x$ b $y:def$ $x$ $END$", "  ", "    ", "\n");
    Check("live templates: fields, defaults, end", parts.Count(p => p is TemplateField) == 3 && parts.OfType<TemplateField>().ElementAt(1).Default == "def" && parts.Last() is TemplateEnd);
    Check("live templates: lines keep the indentation", LiveTemplates.Parse("a\n\tb", "    ", "  ", "\n").OfType<TemplateText>().Single().Text == "a\n      b");

    // Emmet abbreviations + HTML completion
    string E(string abbr) => Emmet.Expand(abbr) is { } x ? x.Replace(Emmet.Caret, '|') : "null";
    var emmetCases = new (string abbr, string html)[]
    {
        ("h1", "<h1>|</h1>"),
        ("p.test", "<p class=\"test\">|</p>"),
        ("div#main.box.big", "<div id=\"main\" class=\"box big\">|</div>"),
        (".card", "<div class=\"card\">|</div>"),
        ("a", "<a href=\"|\">|</a>"),
        ("a.btn[target=_blank]{Home}", "<a href=\"|\" class=\"btn\" target=\"_blank\">Home</a>"),
        ("img", "<img src=\"|\" alt=\"|\">"),
        ("input[type=email name=mail]", "<input type=\"email\" name=\"mail\">"),
        ("ul>li.item$*3", "<ul>\n    <li class=\"item1\">|</li>\n    <li class=\"item2\">|</li>\n    <li class=\"item3\">|</li>\n</ul>"),
        ("ul>.x", "<ul>\n    <li class=\"x\">|</li>\n</ul>"),
        ("div+p", "<div>|</div>\n<p>|</p>"),
        ("nav>a*2^p", "<nav><a href=\"|\">|</a><a href=\"|\">|</a></nav>\n<p>|</p>"),
        ("nav>a*3", "<nav>\n    <a href=\"|\">|</a>\n    <a href=\"|\">|</a>\n    <a href=\"|\">|</a>\n</nav>"),
        ("table>(tr>td*2)*2", "<table>\n    <tr>\n        <td>|</td>\n        <td>|</td>\n    </tr>\n    <tr>\n        <td>|</td>\n        <td>|</td>\n    </tr>\n</table>"),
        ("p>span{a}+strong{b}", "<p><span>a</span><strong>b</strong></p>"),
        ("hello", "null"),
        ("my-card.x", "<my-card class=\"x\">|</my-card>"),
    };
    var emmetFailures = emmetCases.Where(c => E(c.abbr) != c.html).Select(c => $"{c.abbr} → {E(c.abbr)}").ToList();
    Check("Emmet: tags, id / classes, attributes, text, children, siblings, climb, groups, multipliers, numbering", emmetFailures.Count == 0, string.Join("\n   ", emmetFailures));
    Check("Emmet: ! is an HTML5 document", E("!").StartsWith("<!DOCTYPE html>\n<html lang=\"en\">") && E("!").Contains("<title>|</title>"));
    Check("Emmet: indentation of the line kept", Emmet.Expand("ul>li", "  ", "\n", "    ")!.Replace(Emmet.Caret.ToString(), "") == "<ul>\n      <li></li>\n    </ul>");
    var abbrCases = new (string line, string? abbr)[]
    {
        ("    h1", "h1"), ("<div>p.x", "p.x"), ("text ul>li*2", "ul>li*2"), ("a[title=\"a b\"]", "a[title=\"a b\"]"), ("<div", null), ("<div>", null), ("p{Hello world}", "p{Hello world}"),
    };
    var abbrFailures = abbrCases.Where(c => Emmet.AbbreviationBefore(c.line)?.abbreviation != c.abbr).Select(c => $"{c.line} → {Emmet.AbbreviationBefore(c.line)?.abbreviation ?? "null"}").ToList();
    Check("Emmet: abbreviation before the caret", abbrFailures.Count == 0, string.Join(" | ", abbrFailures));

    var htmlDoc = "<div class=\"a\">\n  <ul>\n    <li><a href=\"#\" target=\"\">x</a></li>\n    <img src=\"a.png\">\n    <\n";
    var lastLt = htmlDoc.LastIndexOf('<') + 1;
    Check("HTML context: tag name, attribute name, attribute value, text",
        HtmlCompletion.ContextAt(htmlDoc, lastLt, "HTML")?.Kind == HtmlContextKind.TagName
        && HtmlCompletion.ContextAt("<a hr", 5, "HTML") is { Kind: HtmlContextKind.AttributeName, Tag: "a", Start: 3 }
        && HtmlCompletion.ContextAt("<a target=\"_b", 13, "HTML") is { Kind: HtmlContextKind.AttributeValue, Attribute: "target" }
        && HtmlCompletion.ContextAt("<p>hello", 8, "HTML")?.Kind == HtmlContextKind.Text);
    Check("HTML context: not in PHP code nor in a Twig expression",
        HtmlCompletion.ContextAt("<?php $a = '<b';", 14, "PHP") == null && HtmlCompletion.ContextAt("<p><?= $x ?> <b", 15, "PHP")?.Kind == HtmlContextKind.TagName
        && HtmlCompletion.ContextAt("{{ a < b }}", 6, "Twig") == null);
    Check("HTML: innermost open element for </", HtmlCompletion.InnermostOpenTag(htmlDoc, htmlDoc.Length) == "ul");
    Check("HTML: end tag inserted after the > of a start tag (not void / self-closed / end tag)",
        HtmlCompletion.EndTagFor("<div class=\"a>b\">", 16, "HTML") == "</div>" && HtmlCompletion.EndTagFor("<br>", 3, "HTML") == null
        && HtmlCompletion.EndTagFor("<x/>", 3, "HTML") == null && HtmlCompletion.EndTagFor("</p>", 3, "HTML") == null
        && HtmlCompletion.EndTagFor("<p>", 2, "PHP") == "</p>" && HtmlCompletion.EndTagFor("<?php $a->", 9, "PHP") == null);
    var attrs = HtmlCompletion.Complete("<a cl", 5, "HTML");
    var vals = HtmlCompletion.Complete("<input type=\"", 13, "HTML");
    Check("HTML completion: tag attributes (name=\"\" with the caret inside) and known values",
        attrs?.entries.Any(x => x.Label == "href" && x.Insert == "href=\"\"" && x.CaretBack == 1) == true && attrs.Value.entries.Any(x => x.Label == "class")
        && vals?.entries.Any(x => x.Label == "email") == true);

    // template variables: fixture controller / entity / templates in the e2e project (removed afterwards)
    var fixtures = new Dictionary<string, string>
    {
        ["src/Entity/V3Post.php"] = "<?php\nnamespace App\\Entity;\n\nclass V3Post\n{\n    public string $slug = '';\n    private string $title = '';\n    public function getTitle(): string { return $this->title; }\n    public function isPublished(): bool { return true; }\n    public function getAuthor(): V3Author { return new V3Author(); }\n}\n\nclass V3Author\n{\n    public function getName(): string { return ''; }\n}\n",
        ["src/Controller/V3TestController.php"] = "<?php\nnamespace App\\Controller;\n\nuse App\\Entity\\V3Post;\nuse NeoPHP\\Component\\Controller\\Contract\\AbstractController;\n\nclass V3TestController extends AbstractController\n{\n    public function show(V3Post $post)\n    {\n        return $this->render('v3/show.html.twig', ['post' => $post, 'count' => 3]);\n    }\n\n    public function list()\n    {\n        /** @var V3Post[] $posts */\n        $posts = [];\n        $title = 'All';\n        return $this->render('v3/list', compact('posts', 'title'));\n    }\n\n    public function loop()\n    {\n        /** @var V3Post[] $posts */\n        $posts = [];\n        return $this->render('v3/loop.html.twig', ['posts' => $posts]);\n    }\n}\n",
        ["templates/v3/show.html.twig"] = "<h1>{{ post.title }}</h1>\n<p>{{ post.titel }} {{ post.author.name|upper }} {{ post.slug }} {{ count }}</p>\n{{ post. }}\n",
        ["templates/v3/loop.html.twig"] = "{% for p in posts %}\n  {{ p.author.nam }}\n{% endfor %}\n",
        ["templates/v3/list.php"] = "<?php $this->extend('base') ?>\n<?php foreach ($posts as $post): ?>\n<h2><?= $this->e($post->getTitle()) ?></h2>\n<?php endforeach ?>\n<p><?= $title ?> <?= $missing ?? '' ?> <?= $unknown ?></p>\n",
    };
    var e2e = args[0];
    await Workspace.Index.BuildAsync(e2e, Workspace.PhpExe); // the debugger test reopened the project (empty index)
    try
    {
        foreach (var (rel, content) in fixtures)
        {
            var full = Path.Combine(e2e, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            if (full.EndsWith(".php") && rel.StartsWith("src")) Workspace.Index.UpdateFile(full);
        }
        NeoProject.Build(e2e);
        TemplateVariables.Invalidate();
        string F(string rel) => Path.GetFullPath(Path.Combine(e2e, rel));
        var showVars = TemplateVariables.For(F("templates/v3/show.html.twig"));
        Check("template variables of render(): names, types and source",
            showVars.Any(v => v.Name == "post" && v.Type == "\\App\\Entity\\V3Post" && v.Source == "V3TestController::show()") && showVars.Any(v => v.Name == "count"),
            string.Join(", ", showVars.Select(v => $"{v.Name}:{v.Type}@{v.Source}")));
        var listVars = TemplateVariables.For(F("templates/v3/list.php"));
        Check("template variables of compact()", listVars.Any(v => v.Name == "posts" && v.Type == "\\App\\Entity\\V3Post[]") && listVars.Any(v => v.Name == "title"),
            string.Join(", ", listVars.Select(v => $"{v.Name}:{v.Type}")));
        var baseVars = TemplateVariables.For(F("templates/base.php"));
        Check("a layout receives the variables of the views extending it", baseVars.Any(v => v.Name == "posts") && baseVars.Any(v => v.Name == "title"), string.Join(", ", baseVars.Select(v => v.Name)));

        var show = fixtures["templates/v3/show.html.twig"];
        var showFile = F("templates/v3/show.html.twig");
        var dot = show.IndexOf("{{ post. }}") + "{{ post.".Length;
        var members = TwigInsight.Complete(show, dot, showFile);
        var labels = members?.entries.Select(e => e.Label).ToList() ?? new();
        Check("Twig completion of attributes (getters as names)", labels.Contains("title") && labels.Contains("published") && labels.Contains("slug") && labels.Contains("author") && !labels.Contains("getTitle"),
            string.Join(",", labels));
        var varsAt = TwigInsight.Complete(show, show.IndexOf("{{ count") + 3, showFile);
        Check("Twig completion of the template variables", varsAt?.entries.Any(e => e.Label == "post" && e.TypeText == "V3Post") == true && varsAt.Value.entries.Any(e => e.Label == "count"),
            string.Join(",", varsAt?.entries.Take(10).Select(e => e.Label + ":" + e.TypeText) ?? Array.Empty<string>()));
        var filters = TwigInsight.Complete(show, show.IndexOf("|upper") + 1, showFile);
        Check("Twig completion of filters after |", filters?.entries.Any(e => e.Label == "upper") == true && filters.Value.entries.All(e => e.TypeText == "filter"));
        var twigProblems = TwigInsight.Problems(show, showFile);
        Check("Twig check: unknown attribute with suggestion, valid chains accepted",
            twigProblems.Count == 1 && twigProblems[0].Message.Contains("Unknown attribute 'titel' of V3Post") && twigProblems[0].Suggestions.FirstOrDefault() == "title",
            string.Join(" | ", twigProblems.Select(p => p.Message)));
        var symbol = TwigInsight.SymbolAt(show, show.IndexOf("post.title") + 6, showFile);
        Check("Twig navigation: attribute → getter of the entity", symbol?.File?.EndsWith("V3Post.php") == true && symbol.Description.Contains("getTitle"), symbol?.Description ?? "null");
        var varSymbol = TwigInsight.SymbolAt(show, show.IndexOf("post.title") + 1, showFile);
        Check("Twig navigation: variable → render() of the controller", varSymbol?.File?.EndsWith("V3TestController.php") == true && varSymbol.Description.Contains("V3TestController::show()"), varSymbol?.Description ?? "null");
        var loop = fixtures["templates/v3/loop.html.twig"];
        var loopProblems = TwigInsight.Problems(loop, F("templates/v3/loop.html.twig"));
        Check("Twig {% for %}: the element type of the collection", loopProblems.Count == 1 && loopProblems[0].Message.Contains("'nam' of V3Author") && loopProblems[0].Suggestions.Contains("name"),
            string.Join(" | ", loopProblems.Select(p => p.Message)));

        var list = fixtures["templates/v3/list.php"];
        var listFile = F("templates/v3/list.php");
        var hints = TemplateVariables.CheckPhpView(list, listFile);
        Check("PHP view check: variable not passed (guarded / loop / passed ones accepted)",
            hints.Count == 1 && hints[0].Message.Contains("'$unknown'") && hints[0].Severity == NeoIDE.Services.Diagnostics.Severity.Info, string.Join(" | ", hints.Select(h => h.Message)));
        var prelude = TemplateVariables.PhpViewPrelude(listFile);
        var arrow = list.IndexOf("$post->getTitle") + "$post->".Length;
        var viewComp = Workspace.Completion.Complete(prelude + list, arrow + prelude.Length, listFile, true);
        Check("PHP view completion: $post-> of a foreach over a render() variable", viewComp.Entries.Any(e => e.Label.StartsWith("getTitle")), string.Join(",", viewComp.Entries.Take(12).Select(e => e.Label)));
        var thisArrow = list.IndexOf("$this->e(") + "$this->".Length;
        var thisComp = Workspace.Completion.Complete(prelude + list, thisArrow + prelude.Length, listFile, true);
        Check("PHP view completion: $this-> is the Template", thisComp.Entries.Any(e => e.Label.StartsWith("extend")) && thisComp.Entries.Any(e => e.Label.StartsWith("section")), string.Join(",", thisComp.Entries.Take(12).Select(e => e.Label)));
        Check("PHP view: $variable resolves to the render() call", TemplateVariables.PhpViewVariableAt(list, list.IndexOf("$title") + 2, listFile)?.Source == "V3TestController::list()");

        // arrays given to render(): keys completed / checked / navigated in Twig
        var shapeCtl = Path.Combine(e2e, "src", "Controller", "V3ShapeController.php");
        var shapeTpl = Path.Combine(e2e, "templates", "v3", "shape.html.twig");
        File.WriteAllText(shapeCtl, "<?php\nnamespace App\\Controller;\n\nuse NeoPHP\\Component\\Controller\\Contract\\AbstractController;\n\nclass V3ShapeController extends AbstractController\n{\n    public function index()\n    {\n        return $this->render('v3/shape.html.twig', [\n            'controller' => [\n                'url' => $this->generateUrl('home'),\n                'route' => 'home_index',\n                'controller' => self::class,\n                'meta' => ['count' => 3],\n            ],\n            'datas' => [],\n        ]);\n    }\n}\n");
        var shapeText = "{{ controller.ro }}\n{{ controller.meta.count }} {{ controller.rout }} {{ controller. }}\n";
        File.WriteAllText(shapeTpl, shapeText);
        try
        {
            Workspace.Index.UpdateFile(shapeCtl);
            NeoProject.Build(e2e);
            TemplateVariables.Invalidate();
            var shapeVar = TemplateVariables.For(shapeTpl).FirstOrDefault(v => v.Name == "controller");
            Check("render() array literal: its keys are recorded (nested, with types)",
                shapeVar?.Shape is { } sh && sh.Keys.Select(k => k.Name).SequenceEqual(new[] { "url", "route", "controller", "meta" }) && sh.Keys[1].Type == "string" && sh.Keys[3].Shape?.Keys[0].Name == "count",
                shapeVar?.Shape == null ? "no shape" : string.Join(", ", shapeVar.Shape.Keys.Select(k => $"{k.Name}:{k.Type}")));
            var keyComp = TwigInsight.Complete(shapeText, "{{ controller.ro".Length, shapeTpl);
            var keyComp2 = TwigInsight.Complete(shapeText, shapeText.IndexOf("{{ controller. }}") + "{{ controller.".Length, shapeTpl);
            Check("Twig completion of the keys of an array given to render()",
                keyComp?.entries.Any(e => e.Label == "route") == true && keyComp2?.entries.Select(e => e.Label).ToList() is { } kl && kl.Contains("url") && kl.Contains("meta") && kl.Contains("controller"),
                string.Join(",", keyComp?.entries.Select(e => e.Label) ?? Array.Empty<string>()));
            var keyProblems = TwigInsight.Problems(shapeText, shapeTpl);
            Check("Twig check: unknown key of the array (nested keys accepted)",
                keyProblems.Count == 2 && keyProblems.All(p => p.Message.StartsWith("Unknown key")) && keyProblems.Any(p => p.Message.Contains("'rout'") && p.Suggestions.FirstOrDefault() == "route"),
                string.Join(" | ", keyProblems.Select(p => p.Message)));
            var keySym = TwigInsight.SymbolAt(shapeText, shapeText.IndexOf("meta") + 1, shapeTpl);
            Check("Twig navigation: key → the array of render()", keySym?.File != null && string.Equals(Path.GetFullPath(keySym.File), Path.GetFullPath(shapeCtl), StringComparison.OrdinalIgnoreCase) && File.ReadAllText(shapeCtl).Substring(keySym.Offset, 4) == "meta", keySym?.Description ?? "null");
        }
        finally
        {
            File.Delete(shapeCtl);
            File.Delete(shapeTpl);
            Workspace.Index.RemoveFile(shapeCtl);
        }

        // inspections: unused members / parameters, undefined members, usages of the methods
        var inspFile = Path.Combine(e2e, "src", "Service", "V4Inspect.php");
        var callerFile = Path.Combine(e2e, "src", "Service", "V4Caller.php");
        Directory.CreateDirectory(Path.GetDirectoryName(inspFile)!);
        var insp = "<?php\nnamespace App\\Service;\n\nclass V4Inspect\n{\n    private const HOME_ROUTE = 'home_index';\n    private const UNUSED_CONST = 1;\n    private string $used = '';\n    private string $unusedProp = '';\n\n    public function __construct(private int $promotedUnused, private int $promotedUsed) {}\n\n    public function run(int $a, int $b): string\n    {\n        $x = self::HOME_INDEX;\n        $this->helper();\n        $this->nope();\n        $f = function (int $c) use ($x) { return $x; };\n        echo $this->used . $this->promotedUsed . $this->missing . \"{$a}\";\n        return self::HOME_ROUTE;\n    }\n\n    private function helper(): void {}\n    private function neverCalled(): void {}\n}\n";
        File.WriteAllText(inspFile, insp);
        File.WriteAllText(callerFile, "<?php\nnamespace App\\Service;\n\nclass V4Caller\n{\n    public function go(V4Inspect $i): void\n    {\n        $i->run(1, 2);\n        $i->run(3, 4);\n    }\n}\n");
        try
        {
            Workspace.Index.UpdateFile(inspFile);
            Workspace.Index.UpdateFile(callerFile);
            var inspInfo = PhpParser.Parse(insp, inspFile);
            var unusedSyms = CodeInspector.FindUnused(insp, inspInfo).Select(u => insp.Substring(u.Start, u.Length)).ToList();
            var expectedUnused = new[] { "UNUSED_CONST", "$unusedProp", "$promotedUnused", "neverCalled", "$b" };
            Check("unused: private constant / property / promoted property / method, parameters (used ones kept)",
                expectedUnused.All(unusedSyms.Contains) && unusedSyms.Count == expectedUnused.Length, string.Join(", ", unusedSyms));
            var undefinedMembers = CodeInspector.FindUndefined(insp, inspInfo);
            Check("undefined: self::CONST (did you mean), $this->method(), $this->property",
                undefinedMembers.Count == 3 && undefinedMembers.Any(u => u.Message.StartsWith("Constant 'HOME_INDEX' not found in V4Inspect") && u.Suggestions.FirstOrDefault() == "HOME_ROUTE" && u.Severity == NeoIDE.Services.Diagnostics.Severity.Error)
                && undefinedMembers.Any(u => u.Message.StartsWith("Method 'nope()'")) && undefinedMembers.Any(u => u.Message.StartsWith("Property 'missing'") && u.Severity == NeoIDE.Services.Diagnostics.Severity.Warning),
                string.Join(" | ", undefinedMembers.Select(u => u.Message)));
            var ownerCls = Workspace.Index.GetClass("App\\Service\\V4Inspect")!;
            var runUsages = CodeInspector.MethodUsages(ownerCls.Members.First(m => m.Name == "run"), ownerCls);
            var helperUsages = CodeInspector.MethodUsages(ownerCls.Members.First(m => m.Name == "helper"), ownerCls);
            Check("usages of a method in the project (resolved receivers)", runUsages.Count == 2 && runUsages.All(u => u.File.EndsWith("V4Caller.php")) && helperUsages.Count == 1,
                $"run={runUsages.Count} helper={helperUsages.Count}");
            var lensMethods = UsageLens.Methods(PhpParser.Parse(File.ReadAllText(Path.Combine(e2e, "src", "Controller", "HomeController.php"))), File.ReadAllText(Path.Combine(e2e, "src", "Controller", "HomeController.php")));
            Check("usages: controller actions with a #[Route] are not counted", lensMethods.Count == 0, string.Join(",", lensMethods.Select(m => m.method.Name)));
        }
        finally
        {
            File.Delete(inspFile);
            File.Delete(callerFile);
            Workspace.Index.RemoveFile(inspFile);
            Workspace.Index.RemoveFile(callerFile);
        }

        // New PHP Class: completion of extends / implements
        var parents = NeoIDE.Controls.ClassAutoComplete.Candidates("FrameEx", c => c.Kind == PhpClassKind.Class && !c.IsFinal);
        Check("New class dialog: parent class completion (camel humps)", parents.FirstOrDefault()?.Fqn.EndsWith("\\FrameworkException") == true, string.Join(", ", parents.Take(5).Select(c => c.Fqn)));
        var interfaces = NeoIDE.Controls.ClassAutoComplete.Candidates("", c => c.Kind == PhpClassKind.Interface);
        var qualified = NeoIDE.Controls.ClassAutoComplete.Candidates("App\\Entity\\V3", _ => true);
        Check("New class dialog: interfaces only for implements, namespace prefix", interfaces.Count > 0 && interfaces.All(c => c.Kind == PhpClassKind.Interface) && qualified.Any(c => c.Fqn == "App\\Entity\\V3Post"),
            string.Join(", ", qualified.Select(c => c.Fqn)));

        var neoRef = NeoInsight.All("<?php $this->redirectToRoute('hom');", "PHP")[0];
        Check("quick fix suggestions for NeoPHP names", NeoInsight.Suggestions(neoRef).FirstOrDefault() == "home", string.Join(",", NeoInsight.Suggestions(neoRef)));
    }
    finally
    {
        foreach (var rel in fixtures.Keys)
        {
            var full = Path.Combine(e2e, rel);
            try { File.Delete(full); } catch { }
            if (rel.StartsWith("src")) Workspace.Index.RemoveFile(full);
        }
        try { Directory.Delete(Path.Combine(e2e, "templates", "v3")); } catch { }
        NeoProject.Build(e2e);
    }
}

Console.WriteLine(failures == 0 ? "\nALL PASSED" : $"\n{failures} FAILURE(S)");
return failures;

// A fake Xdebug: connects to the IDE like PHP does, answers the DBGp commands, stops at the breakpoint.
async Task DbgpFakeClientTest()
{
    var port = 19003 + Random.Shared.Next(500);
    Workspace.Open(args[0]);
    var bpFile = Path.GetFullPath(Path.Combine(args[0], "src", "Controller", "HomeController.php"));
    var bp = new BreakpointDef { File = bpFile, Line = 16 };
    NeoIDE.Services.Debug.Breakpoints.Add(bp);
    var listening = NeoIDE.Services.Debug.DebugService.StartListening(port, out var listenError);
    Check("debugger listens for Xdebug", listening, listenError ?? "");
    if (!listening) return;
    var uri = new Uri(bpFile).AbsoluteUri;
    var commands = new List<string>();
    using var client = new System.Net.Sockets.TcpClient();
    await client.ConnectAsync("127.0.0.1", port);
    var s = client.GetStream();
    const string NS = "xmlns=\"urn:debugger_protocol_v1\" xmlns:xdebug=\"https://xdebug.org/dbgp/xdebug\"";
    async Task Send(string xml)
    {
        var body = System.Text.Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"iso-8859-1\"?>\n" + xml);
        await s.WriteAsync(System.Text.Encoding.ASCII.GetBytes(body.Length.ToString()));
        s.WriteByte(0);
        await s.WriteAsync(body);
        s.WriteByte(0);
    }
    async Task<string?> Read()
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await s.ReadAsync(one) == 0) return null;
            if (one[0] == 0) return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(one[0]);
        }
    }
    string B64(string v) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(v));
    var runs = 0;
    var server = Task.Run(async () =>
    {
        await Send($"<init {NS} fileuri=\"{new Uri(Path.Combine(args[0], "public", "index.php")).AbsoluteUri}\" language=\"PHP\" protocol_version=\"1.0\" appid=\"1\" idekey=\"NEOIDE\"><engine version=\"3.4.0\"><![CDATA[Xdebug]]></engine></init>");
        while (true)
        {
            var cmd = await Read();
            if (cmd == null) return;
            lock (commands) commands.Add(cmd);
            var name = cmd.Split(' ')[0];
            var id = System.Text.RegularExpressions.Regex.Match(cmd, @"-i (\d+)").Groups[1].Value;
            switch (name)
            {
                case "breakpoint_set": await Send($"<response {NS} command=\"breakpoint_set\" transaction_id=\"{id}\" id=\"1000\"/>"); break;
                case "run" when ++runs == 1:
                    await Send($"<response {NS} command=\"run\" transaction_id=\"{id}\" status=\"break\" reason=\"ok\"><xdebug:message filename=\"{uri}\" lineno=\"16\"></xdebug:message></response>");
                    break;
                case "run": await Send($"<response {NS} command=\"run\" transaction_id=\"{id}\" status=\"stopping\" reason=\"ok\"/>"); break;
                case "stop": await Send($"<response {NS} command=\"stop\" transaction_id=\"{id}\" status=\"stopped\" reason=\"ok\"/>"); client.Close(); return;
                case "stack_get":
                    await Send($"<response {NS} command=\"stack_get\" transaction_id=\"{id}\"><stack where=\"App\\Controller\\HomeController-&gt;index\" level=\"0\" type=\"file\" filename=\"{uri}\" lineno=\"16\"></stack><stack where=\"{{main}}\" level=\"1\" type=\"file\" filename=\"file:///C:/x/public/index.php\" lineno=\"5\"></stack></response>");
                    break;
                case "context_get" when cmd.Contains("-c 0"):
                    await Send($"<response {NS} command=\"context_get\" transaction_id=\"{id}\" context=\"0\">" +
                               $"<property name=\"$title\" fullname=\"$title\" type=\"string\" size=\"7\" encoding=\"base64\"><![CDATA[{B64("Welcome")}]]></property>" +
                               "<property name=\"$items\" fullname=\"$items\" type=\"array\" children=\"1\" numchildren=\"2\" page=\"0\" pagesize=\"200\"></property>" +
                               "<property name=\"$n\" fullname=\"$n\" type=\"int\"><![CDATA[3]]></property></response>");
                    break;
                case "context_get": await Send($"<response {NS} command=\"context_get\" transaction_id=\"{id}\" context=\"1\"></response>"); break;
                case "property_get":
                    await Send($"<response {NS} command=\"property_get\" transaction_id=\"{id}\"><property name=\"$items\" fullname=\"$items\" type=\"array\" children=\"1\" numchildren=\"2\" page=\"0\" pagesize=\"200\">" +
                               "<property name=\"0\" fullname=\"$items[0]\" type=\"string\" encoding=\"base64\"><![CDATA[" + B64("a") + "]]></property>" +
                               "<property name=\"1\" fullname=\"$items[1]\" type=\"bool\"><![CDATA[1]]></property></property></response>");
                    break;
                case "eval":
                    var expr = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cmd[(cmd.IndexOf(" -- ") + 4)..]));
                    await Send($"<response {NS} command=\"eval\" transaction_id=\"{id}\"><property type=\"int\"><![CDATA[{(expr == "6*7" ? 42 : 0)}]]></property></response>");
                    break;
                default: await Send($"<response {NS} command=\"{name}\" transaction_id=\"{id}\" success=\"1\"/>"); break;
            }
        }
    });
    async Task<bool> WaitFor(Func<bool> cond)
    {
        for (int i = 0; i < 100 && !cond(); i++) await Task.Delay(50);
        return cond();
    }
    var paused = await WaitFor(() => NeoIDE.Services.Debug.DebugService.State == NeoIDE.Services.Debug.DebugState.Paused);
    var pos = NeoIDE.Services.Debug.DebugService.Position;
    string bpCmd;
    lock (commands) bpCmd = commands.FirstOrDefault(c => c.StartsWith("breakpoint_set")) ?? "";
    Check("DBGp: breakpoint sent with the file URI and the line", bpCmd.Contains($"-f {uri}") && bpCmd.Contains("-n 16"), bpCmd);
    Check("DBGp: paused at the breakpoint, stack read", paused && pos?.file == bpFile && pos?.line == 16 && NeoIDE.Services.Debug.DebugService.Stack.Count == 2
        && NeoIDE.Services.Debug.DebugService.Stack[0].Where == "App\\Controller\\HomeController->index", $"{NeoIDE.Services.Debug.DebugService.State} {pos}");
    var vars = await NeoIDE.Services.Debug.DebugService.Variables(0);
    Check("DBGp: variables (base64 string, array, int)", vars.Count == 3 && vars[0].Display == "\"Welcome\"" && vars[1].Display == "array(2)" && vars[1].HasChildren && vars[2].Display == "3",
        string.Join(", ", vars.Select(v => $"{v.Name}={v.Display}")));
    var children = vars.Count > 1 ? await NeoIDE.Services.Debug.DebugService.Children(vars[1], 0) : new();
    Check("DBGp: children of an array", children.Count == 2 && children[0].Display == "\"a\"" && children[1].Display == "true", string.Join(", ", children.Select(v => $"{v.Name}={v.Display}")));
    var (value, evalError) = await NeoIDE.Services.Debug.DebugService.Evaluate("6*7");
    Check("DBGp: evaluate an expression", value?.Display == "42", evalError ?? value?.Display ?? "");
    await NeoIDE.Services.Debug.DebugService.Resume();
    var ended = await WaitFor(() => NeoIDE.Services.Debug.DebugService.State == NeoIDE.Services.Debug.DebugState.Listening);
    Check("DBGp: end of the script closes the session", ended && server.IsCompleted, NeoIDE.Services.Debug.DebugService.State.ToString());
    NeoIDE.Services.Debug.DebugService.StopListening();
    NeoIDE.Services.Debug.Breakpoints.Remove(bp);
}
