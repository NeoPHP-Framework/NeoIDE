using System.Windows;
using NeoIDE.Controls;

namespace NeoIDE.Dialogs;

/// <summary>NeoPHP › Composer › Manage packages…</summary>
public sealed class PackagesDialog : DialogWindow
{
    public PackagesDialog(string root)
    {
        Title = "Composer packages";
        SizeToContent = SizeToContent.Manual;
        ResizeMode = ResizeMode.CanResize;
        Width = 980;
        Height = 680;
        Content = new PackagesPanel(root) { Margin = new Thickness(14, 0, 14, 12) };
    }
}
