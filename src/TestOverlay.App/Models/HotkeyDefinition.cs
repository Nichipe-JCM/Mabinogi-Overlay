using System.Windows.Input;
using TestOverlay.App.Native;

namespace TestOverlay.App.Models;

public sealed record HotkeyDefinition(uint Modifiers, uint VirtualKey, string DisplayText)
{
    public static HotkeyDefinition Default { get; } = new(
        Win32Methods.ModControl | Win32Methods.ModShift,
        (uint)KeyInterop.VirtualKeyFromKey(Key.F8),
        "Ctrl+Shift+F8");
}
