using System.Windows.Input;

namespace NeoIDE.Services;

public static class KeyboardHelper
{
    /// <summary>
    /// AltGr (right Alt on AZERTY / QWERTZ / most European layouts) is sent by Windows as LeftCtrl + RightAlt,
    /// so WPF reports Ctrl+Alt. Shortcuts must ignore it, otherwise characters such as @ { } [ ] \ | ~ € can't be typed.
    /// Real Ctrl+Alt shortcuts are done with the left Alt key.
    /// </summary>
    public static bool IsAltGr => Keyboard.IsKeyDown(Key.RightAlt) && (Keyboard.Modifiers & ModifierKeys.Control) != 0;
}
