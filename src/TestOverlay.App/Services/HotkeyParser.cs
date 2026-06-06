using System.Windows.Input;
using TestOverlay.App.Models;
using TestOverlay.App.Native;

namespace TestOverlay.App.Services;

public static class HotkeyParser
{
    public static bool TryParse(string text, out HotkeyDefinition hotkey)
    {
        hotkey = HotkeyDefinition.Default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        uint modifiers = 0;
        Key? key = null;
        foreach (var rawPart in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = rawPart.ToUpperInvariant();
            switch (part)
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= Win32Methods.ModControl;
                    break;
                case "SHIFT":
                    modifiers |= Win32Methods.ModShift;
                    break;
                case "ALT":
                    modifiers |= Win32Methods.ModAlt;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= Win32Methods.ModWin;
                    break;
                default:
                    if (!Enum.TryParse<Key>(part, true, out var parsed))
                    {
                        return false;
                    }

                    key = parsed;
                    break;
            }
        }

        if (key is null || modifiers == 0)
        {
            return false;
        }

        hotkey = new HotkeyDefinition(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key.Value), text);
        return true;
    }
}
