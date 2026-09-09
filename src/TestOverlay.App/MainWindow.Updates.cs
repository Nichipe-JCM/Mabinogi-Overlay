using System.Windows;
using System.Windows.Media;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class MainWindow
{
    private UpdateCoordinator? _updates;
    private bool _updateDialogOpen;
    private bool _updateExitCommitted;
    private void InitializeUpdates()
    {
        if (_updates is not null) return;
        _updates = new UpdateCoordinator(_log);
        _updates.Changed += (_, _) => RefreshUpdateStatus();
        Closed += (_, _) => _updates.Dispose();
        RefreshUpdateStatus();
        _ = _updates.CheckAsync();
    }
    private void RefreshUpdateStatus()
    {
        var state = _updates?.Status ?? UpdateStatus.Unknown;
        UpdateStatusLamp.Fill = state switch { UpdateStatus.Current => Brushes.MediumSeaGreen, UpdateStatus.Available => Brushes.Goldenrod, _ => Brushes.Gray };
        UpdateStatusText.Text = L.T(UpdateStatusKey(state));
    }
    internal static string UpdateStatusKey(UpdateStatus state) => state switch
    {
        UpdateStatus.Checking => "update.checking", UpdateStatus.Current => "update.current", UpdateStatus.Available => "update.available", UpdateStatus.Failed => "update.check.failed", _ => "update.unknown"
    };
    private async void OpenUpdateDialog()
    {
        if (_updates is null || _updateDialogOpen) return;
        _updateDialogOpen = true;
        try
        {
            await _updates.CheckAsync();
            if (_updates.Offer is not { } offer) return;
            var dialog = new UpdateWindow(offer, _log) { Owner = this };
            dialog.PreparationFailed += _updates.MarkFailed;
            if (dialog.ShowDialog() != true || dialog.Session is not { } session) return;
            using (session)
            {
                // No discard option: an update must never silently lose pending changes.
                if (!FlushProfileAutoSave() || !ErinTimerPanel.TryFlushSettings())
                {
                    ShowInAppNotice(L.T("update.save.failed"));
                    return;
                }
                _settingsStore.Save(_appSettings);
                session.Proceed();
                _updateExitCommitted = true;
                Application.Current.Shutdown();
            }
        }
        catch (Exception exception) { _log.Error("Update could not start.", exception); ShowInAppNotice(L.T("update.failed")); }
        finally { _updateDialogOpen = false; }
    }
}
