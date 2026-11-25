namespace NeoIDE.Services;

public static class MenuText
{
    /// <summary>
    /// A text shown as is in a menu: WPF takes "_" as the access key mark ("HOME_ROUTE" would show "HOMEROUTE"
    /// with an underlined R), so it is doubled.
    /// </summary>
    public static string Literal(this string text) => text.Replace("_", "__");
}
