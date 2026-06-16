using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TestOverlay.App.Services;

public sealed partial class LocalizationService
{
    public const string English = "en-US";
    public const string Korean = "ko-KR";
    private const string LocalizationDirectoryName = "Localization";
    private const char KeyValueSeparator = '=';

    public static LocalizationService Instance { get; } = new();

    private readonly object _sync = new();
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _languageCache = new(StringComparer.OrdinalIgnoreCase);

    private LocalizationService()
    {
    }

    public event EventHandler? LanguageChanged;

    public string CurrentLanguage { get; private set; } = English;

    public void SetLanguage(string? language)
    {
        var normalized = NormalizeLanguage(language);
        if (string.Equals(CurrentLanguage, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CurrentLanguage = normalized;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(normalized);
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Translate(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        var normalizedKey = NormalizeKey(key);
        var currentStrings = GetLanguageStrings(CurrentLanguage);
        if (currentStrings.TryGetValue(normalizedKey, out var translated))
        {
            return translated;
        }

        if (!string.Equals(CurrentLanguage, English, StringComparison.OrdinalIgnoreCase))
        {
            var englishStrings = GetLanguageStrings(English);
            if (englishStrings.TryGetValue(normalizedKey, out var english))
            {
                return english;
            }
        }

        return key;
    }

    public string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Translate(key), args);

    public static string NormalizeLanguage(string? language) =>
        !string.IsNullOrWhiteSpace(language) &&
        language.Trim().StartsWith("ko", StringComparison.OrdinalIgnoreCase)
            ? Korean
            : English;

    private static string NormalizeKey(string key) =>
        KeyAliases.TryGetValue(key, out var alias)
            ? alias
            : key;

    private IReadOnlyDictionary<string, string> GetLanguageStrings(string language)
    {
        var normalized = NormalizeLanguage(language);
        lock (_sync)
        {
            if (_languageCache.TryGetValue(normalized, out var strings))
            {
                return strings;
            }

            strings = LoadLanguageFile(normalized);
            _languageCache[normalized] = strings;
            return strings;
        }
    }

    private static IReadOnlyDictionary<string, string> LoadLanguageFile(string language)
    {
        var path = GetLanguageFilePath(language);
        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return strings;
        }

        foreach (var rawLine in File.ReadLines(path, System.Text.Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf(KeyValueSeparator);
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (key.Length > 0)
            {
                strings[key] = value;
            }
        }

        return strings;
    }

    private static string GetLanguageFilePath(string language) =>
        Path.Combine(AppContext.BaseDirectory, LocalizationDirectoryName, $"{NormalizeLanguage(language)}.lang");
}

public static class L
{
    public static string T(string key) => LocalizationService.Instance.Translate(key);

    public static string F(string key, params object?[] args) =>
        LocalizationService.Instance.Format(key, args);
}

public static class Localize
{
    private static readonly List<WeakReference<DependencyObject>> Targets = [];
    private static readonly object Sync = new();

    static Localize()
    {
        LocalizationService.Instance.LanguageChanged += (_, _) => RefreshAll();
    }

    public static readonly DependencyProperty TextKeyProperty = DependencyProperty.RegisterAttached(
        "TextKey",
        typeof(string),
        typeof(Localize),
        new PropertyMetadata(null, OnKeyChanged));

    public static readonly DependencyProperty ContentKeyProperty = DependencyProperty.RegisterAttached(
        "ContentKey",
        typeof(string),
        typeof(Localize),
        new PropertyMetadata(null, OnKeyChanged));

    public static readonly DependencyProperty HeaderKeyProperty = DependencyProperty.RegisterAttached(
        "HeaderKey",
        typeof(string),
        typeof(Localize),
        new PropertyMetadata(null, OnKeyChanged));

    public static readonly DependencyProperty TitleKeyProperty = DependencyProperty.RegisterAttached(
        "TitleKey",
        typeof(string),
        typeof(Localize),
        new PropertyMetadata(null, OnKeyChanged));

    public static string? GetTextKey(DependencyObject target) => (string?)target.GetValue(TextKeyProperty);

    public static void SetTextKey(DependencyObject target, string? value) => target.SetValue(TextKeyProperty, value);

    public static string? GetContentKey(DependencyObject target) => (string?)target.GetValue(ContentKeyProperty);

    public static void SetContentKey(DependencyObject target, string? value) => target.SetValue(ContentKeyProperty, value);

    public static string? GetHeaderKey(DependencyObject target) => (string?)target.GetValue(HeaderKeyProperty);

    public static void SetHeaderKey(DependencyObject target, string? value) => target.SetValue(HeaderKeyProperty, value);

    public static string? GetTitleKey(DependencyObject target) => (string?)target.GetValue(TitleKeyProperty);

    public static void SetTitleKey(DependencyObject target, string? value) => target.SetValue(TitleKeyProperty, value);

    private static void OnKeyChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        Register(target);
        Apply(target);
    }

    private static void Register(DependencyObject target)
    {
        lock (Sync)
        {
            if (Targets.Any(reference => reference.TryGetTarget(out var existing) && ReferenceEquals(existing, target)))
            {
                return;
            }

            Targets.Add(new WeakReference<DependencyObject>(target));
        }
    }

    private static void RefreshAll()
    {
        List<WeakReference<DependencyObject>> snapshot;
        lock (Sync)
        {
            snapshot = Targets.ToList();
        }

        foreach (var reference in snapshot)
        {
            if (reference.TryGetTarget(out var target))
            {
                if (target is DispatcherObject dispatcherObject && !dispatcherObject.Dispatcher.CheckAccess())
                {
                    dispatcherObject.Dispatcher.Invoke(() => Apply(target));
                }
                else
                {
                    Apply(target);
                }
            }
        }

        lock (Sync)
        {
            Targets.RemoveAll(reference => !reference.TryGetTarget(out _));
        }
    }

    private static void Apply(DependencyObject target)
    {
        var localizer = LocalizationService.Instance;

        if (target is TextBlock textBlock && GetTextKey(target) is { } textKey)
        {
            textBlock.Text = localizer.Translate(textKey);
        }

        if (target is ContentControl contentControl && GetContentKey(target) is { } contentKey)
        {
            contentControl.Content = localizer.Translate(contentKey);
        }

        if (target is HeaderedContentControl headeredContentControl && GetHeaderKey(target) is { } headerKey)
        {
            headeredContentControl.Header = localizer.Translate(headerKey);
        }

        if (target is Window window && GetTitleKey(target) is { } titleKey)
        {
            window.Title = localizer.Translate(titleKey);
        }
    }
}
