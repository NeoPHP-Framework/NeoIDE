using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NeoIDE.Controls;
using NeoIDE.Dialogs;
using NeoIDE.Services;
using NeoIDE.Services.Git;
using NeoIDE.ViewModels;

namespace NeoIDE;

/// <summary>Git integration of the main window: status, commit, push/pull, branches, diff, history.</summary>
public partial class MainWindow
{
    private bool _gitWired;

    private async Task InitGitAsync(string root)
    {
        if (!_gitWired)
        {
            _gitWired = true;
            GitService.Changed += () => Dispatcher.BeginInvoke(OnGitChanged);
            CommitView.DiffRequested += c => _ = ShowWorkingDiffAsync(c.FullPath, c);
            CommitView.OpenRequested += path => OpenFile(path);
            CommitView.RollbackRequested += changes => _ = RollbackAsync(changes);
            CommitView.CommitRequested += (msg, files, amend, push) => _ = CommitAsync(msg, files, amend, push);
            CommitView.InitRequested += () => GitInit_Click(this, new RoutedEventArgs());
            GitLogView.DiffRequested += (commit, path) => _ = ShowCommitDiffAsync(commit, path);
            GitLogView.CheckoutRequested += commit => _ = GitOp($"checkout {commit.Hash}", $"Checked out {commit.ShortHash} (detached HEAD)");
        }
        await GitService.OpenAsync(root);
        // editors opened before Git was detected get their change markers now
        await ReloadGitBasesAsync();
        if (GitTab.IsSelected && BottomRow.Height.Value > 0) await GitLogView.LoadAsync();
    }

    private void OnGitChanged()
    {
        var st = GitService.Status;
        if (!GitService.IsRepository || st == null)
        {
            BranchTitle.Text = GitService.IsGitInstalled ? "Version control" : "Git not installed";
            BranchSync.Text = "";
            StatusBranchButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            BranchTitle.Text = st.Branch;
            BranchSync.Text = (st.Ahead > 0 ? $"↑{st.Ahead} " : "") + (st.Behind > 0 ? $"↓{st.Behind}" : "");
            StatusBranch.Text = st.Branch + (st.Changes.Count > 0 ? $"  ({st.Changes.Count})" : "");
            StatusBranchButton.Visibility = Visibility.Visible;
        }
        _root?.RefreshGit(GitColorOf);
        foreach (var t in _tabs) UpdateTabTitle(t);
    }

    private static Brush? GitColorOf(FileNode node)
    {
        if (!GitService.IsRepository || node.FullPath.Length == 0) return null;
        if (node.IsDirectory)
            return GitService.DirectoryHasChanges(node.FullPath) ? CommitPanel.StatusBrush(GitChangeKind.Modified) : null;
        var c = GitService.ChangeFor(node.FullPath);
        return c == null ? null : CommitPanel.StatusBrush(c.Kind);
    }

    private async Task LoadGitBaseAsync(EditorTab tab)
    {
        if (tab.Editor == null || !GitService.IsRepository) return;
        var change = GitService.ChangeFor(tab.Path);
        string? baseText;
        if (change?.Kind == GitChangeKind.Untracked || change?.Kind == GitChangeKind.Added && change.X == 'A') baseText = change.Kind == GitChangeKind.Untracked ? "" : await GitService.ShowFileAsync(tab.Path) ?? "";
        else baseText = await GitService.ShowFileAsync(tab.Path);
        if (baseText == null && change == null) return; // ignored file
        tab.Editor.GitBaseText = baseText ?? "";
    }

    private async Task ReloadGitBasesAsync()
    {
        foreach (var t in _tabs.ToList()) await LoadGitBaseAsync(t);
    }

    // ------------------------------------------------------------------ running git

    /// <summary>Runs a git command, shows progress in the status bar and the error output in a dialog.</summary>
    private async Task<bool> GitOp(string args, string success, string? progress = null)
    {
        if (!GitService.IsRepository && !args.StartsWith("init") && !args.StartsWith("clone")) { MessageDialog.Show("Git", "This project is not a Git repository."); return false; }
        SaveAll();
        SetStatus(progress ?? $"git {args.Split(' ')[0]}…");
        var (code, stdout, stderr) = await GitService.Run(args, timeoutMs: 300000);
        await GitService.RefreshAsync();
        await ReloadGitBasesAsync();
        if (code != 0)
        {
            SetStatus($"git {args.Split(' ')[0]} failed");
            var output = (stderr + "\n" + stdout).Trim();
            MessageDialog.Show("Git", output.Length > 0 ? output : $"git {args} failed (exit code {code}).");
            return false;
        }
        SetStatus(success);
        if (GitTab.IsSelected) _ = GitLogView.LoadAsync();
        return true;
    }

    // ------------------------------------------------------------------ commit / push / pull

    private void CommitToggle_Click(object sender, RoutedEventArgs e) => ShowSidePanel(CommitToggle.IsChecked == true ? "commit" : null);

    private void GitCommitPanel_Click(object sender, RoutedEventArgs e)
    {
        ShowSidePanel("commit");
        Dispatcher.BeginInvoke(() => CommitView.FocusMessage(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private async Task CommitAsync(string message, List<GitChange> files, bool amend, bool push)
    {
        SaveAll();
        if (files.Count > 0)
        {
            var (code, _, err) = await GitService.Run($"add -A -- {GitService.PathArgs(files)}");
            if (code != 0) { MessageDialog.Show("Commit", err); return; }
        }
        var msgFile = GitService.MessageFile(message);
        var paths = files.Count > 0 ? " -- " + GitService.PathArgs(files) : "";
        if (await GitOp($"commit -F {GitService.Q(msgFile)}{(amend ? " --amend" : "")}{paths}", $"Committed {files.Count} file(s)", "Committing…"))
        {
            CommitView.CommitSucceeded();
            if (push) await PushAsync();
        }
    }

    private async void GitPush_Click(object sender, RoutedEventArgs e) => await PushAsync();

    private async Task PushAsync()
    {
        if (!GitService.IsRepository) return;
        await GitService.RefreshAsync();
        var st = GitService.Status!;
        var remotes = await GitService.RemotesAsync();
        if (remotes.Count == 0)
        {
            if (MessageDialog.Confirm("Push", "No remote is defined for this repository. Add one now (e.g. origin → https://github.com/you/project.git)?", "Add remote"))
                GitRemotes_Click(this, new RoutedEventArgs());
            return;
        }
        string args, text;
        if (st.Upstream == null)
        {
            args = $"push -u origin {GitService.Q(st.Branch)}";
            text = $"Push the new branch '{st.Branch}' to origin/{st.Branch}?";
        }
        else
        {
            var commits = await GitService.OutgoingAsync();
            if (commits.Count == 0) { SetStatus("Nothing to push"); MessageDialog.Show("Push", $"Everything is up to date with {st.Upstream}."); return; }
            args = "push";
            text = $"Push {commits.Count} commit(s) to {st.Upstream}?\n\n" + string.Join("\n", commits.Take(15)) + (commits.Count > 15 ? "\n…" : "");
        }
        if (!MessageDialog.Confirm("Push Commits", text, "Push")) return;
        await GitOp(args, "Pushed successfully", "Pushing…");
    }

    private async void GitPull_Click(object sender, RoutedEventArgs e)
    {
        if (!GitService.IsRepository) return;
        if (await GitOp("pull --no-edit", "Project updated", "Updating project (git pull)…"))
        {
            FlushFsChanges();
            await ReindexAsync();
        }
    }

    private async void GitFetch_Click(object sender, RoutedEventArgs e) => await GitOp("fetch --all --prune", "Fetched from all remotes", "Fetching…");

    // ------------------------------------------------------------------ branches

    private async void GitWidget_Click(object sender, RoutedEventArgs e)
    {
        var target = sender as UIElement ?? GitWidget;
        var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Item(string header, Action action, string gesture = "")
        {
            var mi = new MenuItem { Header = header.Literal(), InputGestureText = gesture };
            mi.Click += (_, _) => action();
            return mi;
        }
        if (!GitService.IsRepository)
        {
            menu.Items.Add(Item("Create Git Repository…", () => GitInit_Click(this, e)));
            menu.Items.Add(Item("Clone Repository…", () => GitClone_Click(this, e)));
            menu.IsOpen = true;
            return;
        }
        menu.Items.Add(Item("Update Project…", () => GitPull_Click(this, e), "Ctrl+T"));
        menu.Items.Add(Item("Commit…", () => GitCommitPanel_Click(this, e), "Ctrl+K"));
        menu.Items.Add(Item("Push…", () => GitPush_Click(this, e), "Ctrl+Shift+K"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("New Branch…", () => GitNewBranch_Click(this, e)));
        menu.Items.Add(new Separator());

        var branches = await GitService.BranchesAsync();
        var local = new MenuItem { Header = "Local", IsEnabled = false };
        menu.Items.Add(local);
        foreach (var b in branches.Where(b => !b.IsRemote))
        {
            var mi = new MenuItem { Header = b.Name.Literal(), IsChecked = b.IsCurrent, InputGestureText = b.Upstream ?? "" };
            if (!b.IsCurrent)
            {
                var name = b.Name;
                mi.Items.Add(Item("Checkout", () => _ = GitOp($"checkout {GitService.Q(name)}", $"Checked out {name}")));
                mi.Items.Add(Item($"Merge '{name}' into '{GitService.Status?.Branch}'", () => _ = GitOp($"merge --no-edit {GitService.Q(name)}", $"Merged {name}")));
                mi.Items.Add(Item($"Rebase '{GitService.Status?.Branch}' onto '{name}'", () => _ = GitOp($"rebase {GitService.Q(name)}", $"Rebased onto {name}")));
                mi.Items.Add(new Separator());
                mi.Items.Add(Item("Delete", () =>
                {
                    if (MessageDialog.Confirm("Delete branch", $"Delete the local branch '{name}'?", "Delete")) _ = GitOp($"branch -d {GitService.Q(name)}", $"Deleted {name}");
                }));
            }
            menu.Items.Add(mi);
        }
        var remote = branches.Where(b => b.IsRemote).ToList();
        if (remote.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Remote", IsEnabled = false });
            foreach (var b in remote)
            {
                var name = b.Name;
                var localName = name.Contains('/') ? name[(name.IndexOf('/') + 1)..] : name;
                var mi = new MenuItem { Header = name.Literal() };
                mi.Items.Add(Item($"Checkout as '{localName}'", () =>
                {
                    var exists = branches.Any(x => !x.IsRemote && x.Name == localName);
                    _ = GitOp(exists ? $"checkout {GitService.Q(localName)}" : $"checkout -b {GitService.Q(localName)} --track {GitService.Q(name)}", $"Checked out {localName}");
                }));
                mi.Items.Add(Item($"Merge into '{GitService.Status?.Branch}'", () => _ = GitOp($"merge --no-edit {GitService.Q(name)}", $"Merged {name}")));
                menu.Items.Add(mi);
            }
        }
        menu.IsOpen = true;
    }

    private async void GitNewBranch_Click(object sender, RoutedEventArgs e)
    {
        if (!GitService.IsRepository) return;
        var name = InputDialog.Ask("New Branch", "Name of the new branch (created from the current HEAD and checked out):", "",
            null, v => Regex.IsMatch(v.Trim(), @"^[\w./-]+$") && !v.Contains("..") ? null : "Invalid branch name");
        if (string.IsNullOrWhiteSpace(name)) return;
        await GitOp($"checkout -b {GitService.Q(name.Trim())}", $"Created branch {name.Trim()}");
    }

    private async void GitStash_Click(object sender, RoutedEventArgs e)
    {
        if (!GitService.IsRepository) return;
        var msg = InputDialog.Ask("Stash Changes", "Message (optional):", "");
        if (msg == null) return;
        await GitOp($"stash push -u{(msg.Length > 0 ? " -m " + GitService.Q(msg) : "")}", "Changes stashed");
    }

    private async void GitUnstash_Click(object sender, RoutedEventArgs e) => await GitOp("stash pop", "Stash applied");

    // ------------------------------------------------------------------ rollback / diff / history

    private async Task RollbackAsync(List<GitChange> changes)
    {
        if (changes.Count == 0) return;
        var untracked = changes.Where(c => c.Kind == GitChangeKind.Untracked).ToList();
        var tracked = changes.Except(untracked).ToList();
        var text = $"Rollback {changes.Count} file(s)? Local changes will be lost." + (untracked.Count > 0 ? $"\n{untracked.Count} unversioned file(s) will be deleted." : "");
        if (!MessageDialog.Confirm("Rollback Changes", text, "Rollback")) return;
        foreach (var t in _tabs.Where(t => changes.Any(c => string.Equals(c.FullPath, t.Path, StringComparison.OrdinalIgnoreCase))).ToList()) t.Dirty = false;
        if (tracked.Count > 0)
        {
            var added = tracked.Where(c => c.X == 'A').ToList();
            if (added.Count > 0) await GitService.Run($"rm --cached -q -- {GitService.PathArgs(added)}");
            var rest = tracked.Except(added).ToList();
            if (rest.Count > 0) await GitOp($"restore --staged --worktree --source=HEAD -- {GitService.PathArgs(rest)}", "Changes rolled back");
        }
        foreach (var u in untracked)
            try { RecycleBin.Send(u.FullPath); } catch { }
        await GitService.RefreshAsync();
        FlushFsChanges();
    }

    private async void GitRollbackCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentTab == null) return;
        var c = GitService.ChangeFor(CurrentTab.Path);
        if (c == null) { SetStatus("The file has no local changes"); return; }
        await RollbackAsync(new List<GitChange> { c });
    }

    private async Task ShowWorkingDiffAsync(string fullPath, GitChange? change = null)
    {
        change ??= GitService.ChangeFor(fullPath);
        var oldPath = change?.OldPath != null ? Path.Combine(GitService.Root!, change.OldPath) : fullPath;
        var head = change?.Kind == GitChangeKind.Untracked ? "" : await GitService.ShowFileAsync(oldPath) ?? "";
        string current;
        var tab = _tabs.FirstOrDefault(t => string.Equals(t.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        if (tab?.Editor != null) current = tab.Editor.Text;
        else current = File.Exists(fullPath) ? await File.ReadAllTextAsync(fullPath) : "";
        new DiffWindow($"{GitService.Relative(fullPath)}  —  HEAD ↔ Working tree", fullPath, head, current, "HEAD (last commit)", "Your version").Show();
    }

    private async Task ShowCommitDiffAsync(GitCommit commit, string relativePath)
    {
        var full = Path.Combine(GitService.Root!, relativePath.Replace('/', '\\'));
        var parent = commit.Parents.FirstOrDefault();
        var before = parent != null ? await GitService.ShowFileAsync(full, parent) ?? "" : "";
        var after = await GitService.ShowFileAsync(full, commit.Hash) ?? "";
        new DiffWindow($"{relativePath}  —  {commit.ShortHash} {commit.Subject}", full, before, after,
            parent != null ? $"{parent[..7]} (parent)" : "(no parent)", $"{commit.ShortHash}  {commit.Author}").Show();
    }

    private async void GitDiffCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentTab != null && GitService.IsRepository) await ShowWorkingDiffAsync(CurrentTab.Path);
    }

    private async void GitHistoryCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentTab == null || !GitService.IsRepository) return;
        ShowBottom("git");
        await GitLogView.LoadAsync(CurrentTab.Path);
    }

    private void GitLog_Click(object sender, RoutedEventArgs e) => ShowBottom("git");

    // ------------------------------------------------------------------ repository setup

    private async void GitInit_Click(object sender, RoutedEventArgs e)
    {
        if (Workspace.Root == null) return;
        if (GitService.IsRepository) { MessageDialog.Show("Git", $"The project is already in the Git repository {GitService.Root}."); return; }
        if (!MessageDialog.Confirm("Create Git Repository", $"Create a Git repository in {Workspace.Root}?", "Create")) return;
        var (code, _, err) = await ProcessRunner.RunAsync("git", "init", Workspace.Root);
        if (code != 0) { MessageDialog.Show("Git", err); return; }
        await GitService.OpenAsync(Workspace.Root);
        await ReloadGitBasesAsync();
        SetStatus("Git repository created");
        ShowSidePanel("commit");
    }

    private async void GitRemotes_Click(object sender, RoutedEventArgs e)
    {
        if (!GitService.IsRepository) { MessageDialog.Show("Git", "Create a Git repository first."); return; }
        var remotes = await GitService.RemotesAsync();
        var current = remotes.FirstOrDefault(r => r.StartsWith("origin"))?.Split('\t', ' ').ElementAtOrDefault(1) ?? "";
        var url = InputDialog.Ask("Remote 'origin'", "URL of the 'origin' remote:", current,
            remotes.Count > 0 ? "Current remotes:\n" + string.Join("\n", remotes) : "e.g. https://github.com/you/my-project.git or git@github.com:you/my-project.git");
        if (string.IsNullOrWhiteSpace(url)) return;
        await GitOp(current.Length > 0 ? $"remote set-url origin {GitService.Q(url)}" : $"remote add origin {GitService.Q(url)}", "Remote 'origin' saved");
    }

    /// <summary>Used by the offscreen render checks of NeoIDE.Tests.</summary>
    public void ShowToolWindowsForPreview(string? side, string? bottom)
    {
        ShowSidePanel(side);
        ShowBottom(bottom);
    }

    /// <summary>Render checks: the editor of the selected tab, a new terminal.</summary>
    public Editor.CodeEditor? EditorForPreview => CurrentEditor;
    public Controls.Terminal.TerminalView ShellForPreview(Controls.Terminal.ShellKind kind) => NewShell(kind);

    private void GitClone_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CloneDialog();
        if (dlg.ShowDialog() == true && dlg.ClonedPath != null) SwitchProject(dlg.ClonedPath);
    }
}
