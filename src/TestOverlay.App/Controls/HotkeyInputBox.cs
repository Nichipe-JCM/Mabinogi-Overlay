using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TestOverlay.App.Services;

namespace TestOverlay.App.Controls;

public sealed class HotkeyInputBox : TextBox
{
    private readonly HashSet<Key> _pressedModifierKeys = [];
    private bool _capturing;
    private bool _chordCompleted;

    public HotkeyInputBox()
    {
        ToolTip = L.T("hotkey.capture.hint");
        WeakEventManager<LocalizationService, EventArgs>.AddHandler(
            LocalizationService.Instance,
            nameof(LocalizationService.LanguageChanged),
            LocalizationService_LanguageChanged);
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e) =>
        ToolTip = L.T("hotkey.capture.hint");

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        BeginCapture();
        Focus();
        SelectAll();
        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        BeginCapture();
        SelectAll();
        base.OnGotKeyboardFocus(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_capturing)
        {
            BeginCapture();
        }

        var key = NormalizeKey(e);
        e.Handled = true;
        if (IsModifierKey(key))
        {
            _pressedModifierKeys.Add(key);
            return;
        }

        Text = FormatChord(Keyboard.Modifiers, key);
        CaretIndex = Text.Length;
        _chordCompleted = true;
        _capturing = false;
        _pressedModifierKeys.Clear();
        MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        var key = NormalizeKey(e);
        if (!_capturing || !IsModifierKey(key))
        {
            base.OnPreviewKeyUp(e);
            return;
        }

        e.Handled = true;
        if (!_chordCompleted && _pressedModifierKeys.Count == 1 && _pressedModifierKeys.Contains(key))
        {
            Text = ModifierName(key);
            CaretIndex = Text.Length;
            _capturing = false;
            MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
        _pressedModifierKeys.Remove(key);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        _capturing = false;
        _chordCompleted = false;
        _pressedModifierKeys.Clear();
        base.OnLostKeyboardFocus(e);
    }

    private void BeginCapture()
    {
        _capturing = true;
        _chordCompleted = false;
        _pressedModifierKeys.Clear();
    }

    private static Key NormalizeKey(KeyEventArgs e) => e.Key == Key.System ? e.SystemKey : e.Key;

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or
        Key.LWin or Key.RWin;

    private static string FormatChord(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>(5);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Windows");
        parts.Add(KeyName(key));
        return string.Join('+', parts);
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => $"NumPad{(int)key - (int)Key.NumPad0}",
        _ => key.ToString()
    };

    private static string ModifierName(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl => "Ctrl",
        Key.LeftShift or Key.RightShift => "Shift",
        Key.LeftAlt or Key.RightAlt => "Alt",
        Key.LWin or Key.RWin => "Windows",
        _ => key.ToString()
    };
}
