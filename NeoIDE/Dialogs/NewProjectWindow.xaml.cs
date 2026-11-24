using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NeoIDE.Services;

namespace NeoIDE.Dialogs;

public partial class NewProjectWindow : DialogWindow
{
    public string? CreatedPath { get; private set; }
    private bool _packageEdited, _appNameEdited, _busy;
    private string _lastAutoPackage = "", _lastAutoAppName = "";

    public NewProjectWindow()
    {
        InitializeComponent();
        ResizeMode = ResizeMode.CanResize;
        var s = AppSettings.Current;
        LocationBox.Text = s.DefaultProjectsDirectory;
        AuthorBox.Text = s.AuthorName;
        EmailBox.Text = s.AuthorEmail;
        DescriptionBox.Text = "A NeoPHP application";
        TimezoneBox.Text = TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : "UTC";
        PackageBox.TextChanged += (_, _) => { if (PackageBox.Text != _lastAutoPackage) _packageEdited = true; };
        AppNameBox.TextChanged += (_, _) => { if (AppNameBox.Text != _lastAutoAppName) _appNameEdited = true; };
        Loaded += async (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
            UpdateDerived();
            await LoadInterpretersAsync();
            await LoadVersionsAsync();
            await LoadGitIdentityAsync();
        };
        Closing += (_, e) => { if (_busy && !MessageDialog.Confirm("Cancel", "The project is being created. Stop and close?")) e.Cancel = true; else if (_busy) Log.Stop(); };
    }

    private sealed record VersionItem(string Label, string? Constraint)
    {
        public override string ToString() => Label;
    }

    private async Task LoadInterpretersAsync(string? select = null)
    {
        StatusText.Text = "Looking for PHP interpreters…";
        var list = await PhpLocator.FindAllAsync(refresh: select != null);
        PhpCombo.ItemsSource = list;
        var def = select ?? AppSettings.Current.DefaultPhpPath;
        PhpCombo.SelectedItem = list.FirstOrDefault(p => string.Equals(p.Path, def, StringComparison.OrdinalIgnoreCase)) ?? list.FirstOrDefault();
        StatusText.Text = list.Count == 0 ? "No PHP interpreter found: click Add… to select php.exe" : $"{list.Count} PHP interpreter(s) found";
    }

    private async Task LoadVersionsAsync()
    {
        VersionCombo.ItemsSource = new[] { new VersionItem("Latest stable", null) };
        VersionCombo.SelectedIndex = 0;
        VersionHint.Text = "loading…";
        var php = (PhpCombo.SelectedItem as PhpInstall)?.Path;
        var versions = await ComposerService.GetPackageVersionsAsync(php, "neophp/skeleton");
        var items = new List<VersionItem>();
        var stable = versions.Where(v => !v.StartsWith("dev-")).ToList();
        items.Add(new VersionItem(stable.Count > 0 ? $"Latest stable ({stable[0]})" : "Latest stable", null));
        items.AddRange(stable.Select(v => new VersionItem(v, v)));
        items.AddRange(versions.Where(v => v.StartsWith("dev-")).Select(v => new VersionItem(v + " (unstable)", v)));
        VersionCombo.ItemsSource = items;
        VersionCombo.SelectedIndex = 0;
        VersionHint.Text = versions.Count == 0 ? "offline?" : "";
    }

    private async Task LoadGitIdentityAsync()
    {
        if (AuthorBox.Text.Length == 0)
        {
            var (c, o, _) = await ProcessRunner.RunAsync("git", "config --global user.name", null, 5000);
            if (c == 0) AuthorBox.Text = o.Trim();
        }
        if (EmailBox.Text.Length == 0)
        {
            var (c, o, _) = await ProcessRunner.RunAsync("git", "config --global user.email", null, 5000);
            if (c == 0) EmailBox.Text = o.Trim();
        }
    }

    private void Form_Changed(object sender, TextChangedEventArgs e) => UpdateDerived();

    private static string Slug(string s)
    {
        s = Regex.Replace(s.Trim().ToLowerInvariant(), @"[^a-z0-9_.-]+", "-");
        return s.Trim('-', '.');
    }

    private void UpdateDerived()
    {
        if (PathPreview == null) return;
        var name = NameBox.Text.Trim();
        PathPreview.Text = name.Length > 0 ? $"Project will be created in: {Path.Combine(LocationBox.Text.Trim(), name)}" : "";
        if (!_packageEdited)
        {
            var vendor = Slug(AppSettings.Current.VendorName.Length > 0 ? AppSettings.Current.VendorName : "app");
            _lastAutoPackage = $"{vendor}/{Slug(name)}";
            PackageBox.Text = _lastAutoPackage;
        }
        if (!_appNameEdited)
        {
            var words = Regex.Split(name, @"[-_. ]+").Where(w => w.Length > 0).Select(w => char.ToUpper(w[0]) + w[1..]);
            _lastAutoAppName = string.Join(" ", words);
            AppNameBox.Text = _lastAutoAppName;
        }
    }

    private void Php_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PhpCombo.SelectedItem is PhpInstall php) ConstraintBox.Text = ">=" + php.ShortVersion;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Location of the new project", InitialDirectory = Directory.Exists(LocationBox.Text) ? LocationBox.Text : null };
        if (dlg.ShowDialog(this) == true) LocationBox.Text = dlg.FolderName;
    }

    private async void AddPhp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Select php.exe", Filter = "PHP executable|php.exe|All executables|*.exe" };
        if (dlg.ShowDialog(this) != true) return;
        if (await PhpLocator.GetVersionAsync(dlg.FileName) == null)
        {
            MessageDialog.Show("PHP interpreter", "This file is not a working PHP interpreter.");
            return;
        }
        if (!AppSettings.Current.CustomPhpPaths.Contains(dlg.FileName, StringComparer.OrdinalIgnoreCase))
        {
            AppSettings.Current.CustomPhpPaths.Add(dlg.FileName);
            AppSettings.Current.Save();
        }
        await LoadInterpretersAsync(dlg.FileName);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private string? Validate()
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) return "Enter a project name.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "The project name contains invalid characters.";
        if (LocationBox.Text.Trim().Length == 0) return "Choose a location.";
        var dir = Path.Combine(LocationBox.Text.Trim(), name);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()) return $"The directory {dir} already exists and is not empty.";
        if (PhpCombo.SelectedItem is not PhpInstall php) return "Select a PHP interpreter.";
        if (PhpLocator.ParseVersion(php.Version) < new Version(8, 2)) return $"NeoPHP 2 requires PHP 8.2 or higher (selected: {php.Version}).";
        if (!Regex.IsMatch(PackageBox.Text.Trim(), @"^[a-z0-9]([_.-]?[a-z0-9]+)*/[a-z0-9](([_.]|-{1,2})?[a-z0-9]+)*$"))
            return "The package name must look like vendor/name (lowercase letters, digits, - _ .).";
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535) return "Invalid server port.";
        return null;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var error = Validate();
        if (error != null) { StatusText.Text = error; StatusText.Foreground = ThemeManager.Brush("ErrorFg"); return; }
        StatusText.Foreground = ThemeManager.Brush("FgSecondary");

        var php = (PhpInstall)PhpCombo.SelectedItem;
        var name = NameBox.Text.Trim();
        var location = LocationBox.Text.Trim();
        var dir = Path.Combine(location, name);
        var version = (VersionCombo.SelectedItem as VersionItem)?.Constraint;

        // remember the choices
        var s = AppSettings.Current;
        s.DefaultProjectsDirectory = location;
        s.DefaultPhpPath = php.Path;
        s.AuthorName = AuthorBox.Text.Trim();
        s.AuthorEmail = EmailBox.Text.Trim();
        s.VendorName = PackageBox.Text.Split('/')[0];
        s.Save();

        _busy = true;
        CreateButton.IsEnabled = false;
        FormScroll.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        Log.WorkingDirectory = location;
        Directory.CreateDirectory(location);

        bool ok = false;
        try
        {
            ok = await CreateAsync(php, dir, version);
        }
        catch (Exception ex)
        {
            Log.Append("Unexpected error: " + ex.Message, true);
        }
        _busy = false;

        if (ok)
        {
            ProgressTitle.Text = "Project created";
            Progress.Value = Progress.Maximum;
            CreatedPath = dir;
            CreateButton.Content = "Open project";
            CreateButton.IsEnabled = true;
            CreateButton.Click -= Create_Click;
            CreateButton.Click += (_, _) => DialogResult = true;
            CancelButton.Content = "Close";
            StatusText.Text = dir;
        }
        else
        {
            ProgressTitle.Text = "Project creation failed";
            ProgressTitle.Foreground = ThemeManager.Brush("ErrorFg");
            CancelButton.Content = "Close";
            CreateButton.Content = "Back";
            CreateButton.IsEnabled = true;
            CreateButton.Click -= Create_Click;
            CreateButton.Click += Back_Click;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        FormScroll.Visibility = Visibility.Visible;
        ProgressTitle.Foreground = ThemeManager.Brush("Fg");
        CreateButton.Content = "Create";
        CreateButton.Click -= Back_Click;
        CreateButton.Click += Create_Click;
        Log.Clear();
    }

    private void Step(int n, string title)
    {
        Progress.Value = n;
        ProgressTitle.Text = title;
        Log.Append("");
        Log.Append($"▶ {title}", isInfo: true);
    }

    private async Task<bool> CreateAsync(PhpInstall php, string dir, string? version)
    {
        if (ComposerService.FindComposerPhar() == null)
            Log.Append("composer.phar not found next to the composer command: the global 'composer' command will be used.", isInfo: true);

        // 1. skeleton
        Step(1, "Downloading neophp/skeleton");
        var create = $"create-project neophp/skeleton {ProcessRunner.Quote(dir)}{(version != null ? " " + ProcessRunner.Quote(version) : "")} --no-install --no-scripts --no-interaction";
        var (exe, args) = ComposerService.Command(php.Path, create);
        if (await Log.RunAsync(exe, args, Path.GetDirectoryName(dir), $"composer {create}") != 0) return false;

        // 2. composer.json
        Step(2, "Configuring composer.json");
        ComposerService.CustomizeComposerJson(dir, PackageBox.Text.Trim(), DescriptionBox.Text.Trim(),
            ConstraintBox.Text.Trim().Length > 0 ? ConstraintBox.Text.Trim() : ">=" + php.ShortVersion,
            php.Version.Split('-')[0], AuthorBox.Text.Trim(), EmailBox.Text.Trim());
        Log.Append($"name: {PackageBox.Text.Trim()}, require php: {ConstraintBox.Text.Trim()}, config.platform.php: {php.Version}");
        Log.WorkingDirectory = dir;

        // 3. dependencies resolved for the chosen PHP version
        Step(3, $"Installing dependencies for PHP {php.Version}");
        (exe, args) = ComposerService.Command(php.Path, "update --no-interaction --prefer-dist --optimize-autoloader");
        if (await Log.RunAsync(exe, args, dir, "composer update --prefer-dist --optimize-autoloader") != 0) return false;

        // 4. neo install (post-create-project-cmd)
        Step(4, "Generating the NeoPHP project files (neo install)");
        var neo = Path.Combine(dir, "vendor", "bin", "neo");
        if (File.Exists(neo))
        {
            if (await Log.RunAsync(php.Path, $"{ProcessRunner.Quote(neo)} install --no-interaction", dir, "php vendor/bin/neo install") != 0) return false;
        }
        else
        {
            (exe, args) = ComposerService.Command(php.Path, "run-script post-create-project-cmd --no-interaction");
            if (await Log.RunAsync(exe, args, dir, "composer run-script post-create-project-cmd") != 0) return false;
        }

        // 5. .env and IDE files
        Step(5, "Configuring .env");
        ConfigureEnv(dir);
        var gitignore = Path.Combine(dir, ".gitignore");
        if (File.Exists(gitignore) && !File.ReadAllText(gitignore).Contains(".neoide"))
            File.AppendAllText(gitignore, (File.ReadAllText(gitignore).EndsWith("\n") ? "" : "\n") + "/.neoide/\n");
        new ProjectSettings { PhpPath = php.Path, ServerPort = int.Parse(PortBox.Text.Trim()) }.Save(dir);

        // 6. git
        if (GitCheck.IsChecked == true)
        {
            Step(6, "Initializing Git repository");
            if (await Log.RunAsync("git", "init", dir, "git init") == 0 && CommitCheck.IsChecked == true)
            {
                await Log.RunAsync("git", "add -A", dir, "git add -A");
                var code = await Log.RunAsync("git", "commit -q -m \"Initial commit: NeoPHP skeleton\"", dir, "git commit -m \"Initial commit: NeoPHP skeleton\"");
                if (code != 0) Log.Append("The initial commit failed (is git user.name / user.email configured?). The repository is initialized anyway.", isInfo: true);
            }
        }

        // 7. doctor
        if (DoctorCheck.IsChecked == true && File.Exists(Path.Combine(dir, "bin", "neo")))
        {
            Step(7, "Checking the project (neo doctor)");
            await Log.RunAsync(php.Path, "bin/neo doctor --no-ansi", dir, "php bin/neo doctor");
        }
        Log.Append("");
        Log.Append($"✔ Project ready: {dir}", isInfo: false);
        Log.Append($"  Start the server with Shift+F10 (php bin/neo serve) then open http://127.0.0.1:{PortBox.Text}", isInfo: true);
        return true;
    }

    private void ConfigureEnv(string dir)
    {
        var env = Path.Combine(dir, ".env");
        if (!File.Exists(env)) { Log.Append(".env not found, skipped", isInfo: true); return; }
        var text = File.ReadAllText(env);
        string Set(string t, string key, string value)
        {
            var quoted = Regex.IsMatch(value, @"[\s#""']") ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;
            var re = new Regex(@"^" + key + @"=.*$", RegexOptions.Multiline);
            return re.IsMatch(t) ? re.Replace(t, $"{key}={quoted}", 1) : t.TrimEnd() + $"\n{key}={quoted}\n";
        }
        if (AppNameBox.Text.Trim().Length > 0) text = Set(text, "APP_NAME", AppNameBox.Text.Trim());
        if (TimezoneBox.Text.Trim().Length > 0) text = Set(text, "APP_TIMEZONE", TimezoneBox.Text.Trim());
        text = Set(text, "APP_URL", $"http://127.0.0.1:{PortBox.Text.Trim()}");
        File.WriteAllText(env, text);
        Log.Append($"APP_NAME={AppNameBox.Text.Trim()}  APP_TIMEZONE={TimezoneBox.Text.Trim()}  APP_URL=http://127.0.0.1:{PortBox.Text.Trim()}");
    }
}
