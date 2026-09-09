using System.Windows;
using TestOverlay.App.Services;
using TestOverlay.Update;

namespace TestOverlay.App;

public partial class UpdateWindow : Window
{
    private readonly UpdateOffer _offer;
    private readonly AppLog _log;
    private CancellationTokenSource? _cancellation;
    private bool _busy;
    public UpdateLaunchSession? Session { get; private set; }
    public event Action? PreparationFailed;
    public UpdateWindow(UpdateOffer offer, AppLog log)
    {
        InitializeComponent(); _offer = offer; _log = log;
        VersionsText.Text = $"{AppVersion.DisplayVersion} → {offer.Release.Version}";
        NotesText.Text = offer.Notes;
        Closing += (_, e) => { if (_busy) { e.Cancel = true; _cancellation?.Cancel(); } };
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true; StartButton.IsEnabled = false;
        _cancellation = new CancellationTokenSource();
        ProgressText.Text = L.T("update.preparing");
        try
        {
            Session = await UpdateLaunchSession.PrepareAsync(_offer, new Progress<double>(value =>
            {
                DownloadProgress.Value = value;
                ProgressText.Text = value < 100 ? L.F("update.downloading", Math.Round(value)) : L.T("update.verifying");
            }), _cancellation.Token);
            _busy = false;
            DialogResult = true;
        }
        catch (OperationCanceledException) { ProgressText.Text = L.T("update.canceled"); }
        catch (Exception exception)
        {
            PreparationFailed?.Invoke();
            _log.Error("Update preparation failed.", exception);
            ProgressText.Text = L.T("update.failed") + Environment.NewLine + exception.Message;
        }
        finally { _busy = false; StartButton.IsEnabled = true; _cancellation.Dispose(); _cancellation = null; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) _cancellation?.Cancel(); else DialogResult = false;
    }
}
