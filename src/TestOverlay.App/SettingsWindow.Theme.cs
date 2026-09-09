using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using TestOverlay.App.Models;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class SettingsWindow
{
    public ThemeSettings SelectedTheme { get; private set; } = new();
    private bool _themeInitializing;
    private string _appliedThemeMode = "Default";
    private sealed record ThemeOption(string Mode, string Label);
    private void InitializeTheme(ThemeSettings? saved)
    {
        _themeInitializing = true;
        SelectedTheme = ThemeService.Normalize(saved);
        ThemeCombo.ItemsSource = new[] { "Default", "White", "Black", "Custom" }.Select(mode => new ThemeOption(mode, mode == _appliedThemeMode
            ? L.F("theme.current", L.T("theme." + mode.ToLowerInvariant()))
            : L.T("theme." + mode.ToLowerInvariant()))).ToArray();
        ThemeCombo.SelectedItem = ThemeCombo.Items.OfType<ThemeOption>().Single(option => option.Mode == SelectedTheme.Mode);
        ThemeBackgroundBox.Text = SelectedTheme.Background;
        ThemeAccentBox.Text = SelectedTheme.Accent;
        ThemeForegroundBox.Text = SelectedTheme.Foreground;
        _themeInitializing = false;
        RefreshThemePreview();
    }
    private bool TryReadTheme(out ThemeSettings theme)
    {
        theme = new ThemeSettings { Mode = (ThemeCombo.SelectedItem as ThemeOption)?.Mode ?? "Default", Background = ThemeBackgroundBox.Text.Trim(), Accent = ThemeAccentBox.Text.Trim(), Foreground = ThemeForegroundBox.Text.Trim() };
        var valid = ThemeService.TryColor(theme.Background, out _) && ThemeService.TryColor(theme.Accent, out _) && ThemeService.TryColor(theme.Foreground, out _);
        ThemeValidationText.Text = valid || theme.Mode != "Custom" ? "" : L.T("theme.invalid");
        if (theme.Mode == "Custom" && !valid) return false;
        theme = ThemeService.Normalize(theme); return true;
    }
    private void RefreshThemePreview()
    {
        if (_themeInitializing || ThemeCombo is null || ThemePreview is null) return;
        ThemeCustomPanel.Visibility = (ThemeCombo.SelectedItem as ThemeOption)?.Mode == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        if (!TryReadTheme(out var theme)) return;
        var (background, accent, foreground) = ThemeService.Colors(theme);
        ThemePreview.Background = new SolidColorBrush(background);
        ThemePreview.BorderBrush = new SolidColorBrush(accent);
        ThemePreviewTitle.Foreground = new SolidColorBrush(accent);
        ThemePreviewText.Foreground = new SolidColorBrush(foreground);
        ThemeBackgroundSwatch.Background = new SolidColorBrush(ThemeService.Parse(theme.Background));
        ThemeAccentSwatch.Background = new SolidColorBrush(ThemeService.Parse(theme.Accent));
        ThemeForegroundSwatch.Background = new SolidColorBrush(ThemeService.Parse(theme.Foreground));
    }
    private void ThemeChanged(object sender, RoutedEventArgs e) => RefreshThemePreview();
    private void ResetCustomTheme_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new ThemeSettings();
        ThemeBackgroundBox.Text = defaults.Background;
        ThemeAccentBox.Text = defaults.Accent;
        ThemeForegroundBox.Text = defaults.Foreground;
        RefreshThemePreview();
    }
    private void ChooseThemeColor_Click(object sender, RoutedEventArgs e)
    {
        var box = ((Button)sender).Tag?.ToString() switch { "Background" => ThemeBackgroundBox, "Accent" => ThemeAccentBox, _ => ThemeForegroundBox };
        using var picker = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        if (ThemeService.TryColor(box.Text.Trim(), out var color)) picker.Color = System.Drawing.Color.FromArgb(color.R, color.G, color.B);
        var owner = new System.Windows.Forms.NativeWindow();
        owner.AssignHandle(new WindowInteropHelper(this).Handle);
        try
        {
            if (picker.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK) box.Text = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
        }
        finally { owner.ReleaseHandle(); }
    }
}
