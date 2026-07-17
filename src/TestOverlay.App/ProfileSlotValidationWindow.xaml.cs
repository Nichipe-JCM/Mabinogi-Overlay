using System.Windows;
using System.Windows.Media;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class ProfileSlotValidationWindow : Window
{
    public ProfileSlotValidationWindow(ProfileSlotValidationReport report, bool profileApplied = false)
    {
        InitializeComponent();
        var hasInvalid = report.InvalidCount > 0;
        Summary = profileApplied
            ? L.F("profile.validation.summary.applied", report.ValidCount, report.WarningCount)
            : hasInvalid
            ? L.F("profile.validation.summary.invalid", report.InvalidCount, report.WarningCount)
            : L.F("profile.validation.summary.valid", report.ValidCount, report.WarningCount);
        SummaryBrush = hasInvalid ? Brushes.IndianRed : Brushes.LimeGreen;
        Items = report.Items.Select(ToDisplayItem).ToArray();
        DataContext = this;
    }

    public string Summary { get; }

    public Brush SummaryBrush { get; }

    public IReadOnlyList<ProfileSlotValidationDisplayItem> Items { get; }

    private static ProfileSlotValidationDisplayItem ToDisplayItem(ProfileSlotValidationItem item)
    {
        var title = item.TitleArguments is { Length: > 0 }
            ? L.F(item.TitleKey, item.TitleArguments)
            : L.T(item.TitleKey);
        var detail = item.DetailArguments.Length > 0
            ? L.F(item.DetailKey, item.DetailArguments)
            : L.T(item.DetailKey);
        return item.State switch
        {
            ProfileSlotValidationState.Valid => new("O", Brushes.LimeGreen, title, detail),
            ProfileSlotValidationState.Warning => new("!", Brushes.Goldenrod, title, detail),
            _ => new("X", Brushes.IndianRed, title, detail)
        };
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed record ProfileSlotValidationDisplayItem(string Glyph, Brush Brush, string Title, string Detail);
