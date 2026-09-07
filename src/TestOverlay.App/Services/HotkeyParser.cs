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

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1 && TryParseStandaloneKey(parts[0], out var standaloneKey))
        {
            hotkey = new HotkeyDefinition(
                0,
                (uint)KeyInterop.VirtualKeyFromKey(standaloneKey),
                text);
            return hotkey.VirtualKey != 0;
        }

        uint modifiers = 0;
        Key? key = null;
        foreach (var rawPart in parts)
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
                    if (!TryParseKeyName(part, out var parsed))
                    {
                        return false;
                    }

                    if (key is not null)
                    {
                        return false;
                    }

                    key = parsed;
                    break;
            }
        }

        if (key is null)
        {
            return false;
        }

        hotkey = new HotkeyDefinition(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key.Value), text);
        return true;
    }

    private static bool TryParseStandaloneKey(string text, out Key key)
    {
        key = text.Trim().ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => Key.LeftCtrl,
            "SHIFT" => Key.LeftShift,
            "ALT" => Key.LeftAlt,
            "WIN" or "WINDOWS" => Key.LWin,
            _ => Key.None
        };
        return key != Key.None || TryParseKeyName(text, out key);
    }

    private static bool TryParseKeyName(string text, out Key key)
    {
        var normalized = text.Trim();
        if (normalized.Length == 1 && char.IsDigit(normalized[0]))
        {
            key = (Key)((int)Key.D0 + (normalized[0] - '0'));
            return true;
        }

        return Enum.TryParse(normalized, true, out key) && key != Key.None;
    }
}
