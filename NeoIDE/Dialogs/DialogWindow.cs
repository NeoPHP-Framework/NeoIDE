using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using NeoIDE.Services;

namespace NeoIDE.Dialogs;

/// <summary>Borderless dialog with the IDE look (title bar + close button).</summary>
public class DialogWindow : Window
{
    public DialogWindow()
    {
        Style = (Style)Application.Current.FindResource("DialogWindowStyle");
        if (Application.Current.TryFindResource("NeoIcon") is System.Windows.Media.ImageSource icon) Icon = icon;
        DisplayManager.Attach(this, 38);
        WindowPlacement.Attach(this);
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 38, ResizeBorderThickness = new Thickness(5), GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false, CornerRadius = new CornerRadius(0),
        });
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w != this)
                    ?? Application.Current.MainWindow;
        if (owner != null && owner != this && owner.IsLoaded) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Close, (_, _) => Close()));
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    // ---------- small UI helpers used by code-built dialogs

    protected static TextBlock Label(string text, double top = 10) => new()
    {
        Text = text, Margin = new Thickness(0, top, 0, 4), Foreground = ThemeManager.Brush("Fg"),
    };

    protected static TextBlock Hint(string text) => new()
    {
        Text = text, Margin = new Thickness(0, 4, 0, 0), Foreground = ThemeManager.Brush("FgSecondary"), FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
    };

    protected static StackPanel Buttons(params Button[] buttons)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        foreach (var b in buttons) { b.Margin = new Thickness(8, 0, 0, 0); p.Children.Add(b); }
        return p;
    }

    protected static Button Primary(string text) => new() { Content = text, Style = (Style)Application.Current.FindResource("PrimaryButton"), IsDefault = true };
    protected static Button Secondary(string text) => new() { Content = text, IsCancel = true };
}

/// <summary>Single line text prompt.</summary>
public sealed class InputDialog : DialogWindow
{
    private readonly TextBox _box;
    public string Value => _box.Text;

    private InputDialog(string title, string label, string value, string? hint, Func<string, string?>? validate)
    {
        Title = title;
        var panel = new StackPanel { Margin = new Thickness(18, 4, 18, 18), Width = 420 };
        panel.Children.Add(Label(label, 0));
        _box = new TextBox { Text = value };
        panel.Children.Add(_box);
        var error = new TextBlock { Foreground = ThemeManager.Brush("ErrorFg"), Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(error);
        if (hint != null) panel.Children.Add(Hint(hint));
        var ok = Primary("OK");
        var cancel = Secondary("Cancel");
        ok.Click += (_, _) =>
        {
            var msg = validate?.Invoke(_box.Text);
            if (msg != null) { error.Text = msg; error.Visibility = Visibility.Visible; return; }
            DialogResult = true;
        };
        panel.Children.Add(Buttons(ok, cancel));
        Content = panel;
        Loaded += (_, _) =>
        {
            _box.Focus();
            var dot = value.IndexOf('.');
            if (dot > 0 && !value.StartsWith(".")) _box.Select(0, dot); else _box.SelectAll();
        };
    }

    public static string? Ask(string title, string label, string value = "", string? hint = null, Func<string, string?>? validate = null)
    {
        var d = new InputDialog(title, label, value, hint, validate);
        return d.ShowDialog() == true ? d.Value.Trim() : null;
    }
}

/// <summary>Dialog around any content (used by the plugins).</summary>
public sealed class ContentDialog : DialogWindow
{
    private ContentDialog(string title, FrameworkElement content, string ok, string? cancel, Func<string?>? validate, double width)
    {
        Title = title;
        var panel = new StackPanel { Margin = new Thickness(18, 4, 18, 18), Width = width };
        panel.Children.Add(content);
        var error = new TextBlock { Foreground = ThemeManager.Brush("ErrorFg"), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(error);
        var okB = Primary(ok);
        okB.Click += (_, _) =>
        {
            var msg = validate?.Invoke();
            if (msg != null) { error.Text = msg; error.Visibility = Visibility.Visible; return; }
            DialogResult = true;
        };
        panel.Children.Add(cancel != null ? Buttons(okB, Secondary(cancel)) : Buttons(okB));
        Content = panel;
    }

    public static bool Show(string title, FrameworkElement content, string ok, string? cancel, Func<string?>? validate, double width) =>
        new ContentDialog(title, content, ok, cancel, validate, width).ShowDialog() == true;
}

/// <summary>Simple themed message box.</summary>
public sealed class MessageDialog : DialogWindow
{
    private MessageDialog(string title, string message, string ok, string? cancel)
    {
        Title = title;
        var panel = new StackPanel { Margin = new Thickness(18, 4, 18, 18), MaxWidth = 520, MinWidth = 340 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = ThemeManager.Brush("Fg") });
        var okB = Primary(ok);
        okB.Click += (_, _) => DialogResult = true;
        panel.Children.Add(cancel != null ? Buttons(okB, Secondary(cancel)) : Buttons(okB));
        Content = panel;
    }

    public static bool Confirm(string title, string message, string ok = "OK", string cancel = "Cancel") =>
        new MessageDialog(title, message, ok, cancel).ShowDialog() == true;

    public static void Show(string title, string message) => new MessageDialog(title, message, "OK", null).ShowDialog();
}
