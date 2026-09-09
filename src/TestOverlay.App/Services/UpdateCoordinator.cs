using TestOverlay.Update;

namespace TestOverlay.App.Services;

public enum UpdateStatus { Unknown, Checking, Current, Available, Failed }

public sealed class UpdateCoordinator : IDisposable
{
    private readonly GitHubUpdateClient _client;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AppLog _log;
    private Task? _pending;
    private bool _disposed;
    public UpdateCoordinator(AppLog log) : this(log, new GitHubUpdateClient()) { }
    internal UpdateCoordinator(AppLog log, GitHubUpdateClient client) { _log = log; _client = client; }
    public UpdateStatus Status { get; private set; }
    public UpdateOffer? Offer { get; private set; }
    public event EventHandler? Changed;
    public Task CheckAsync()
    {
        if (_disposed) return Task.CompletedTask;
        if (_pending is { IsCompleted: false }) return _pending;
        return _pending = CheckCoreAsync();
    }
    private async Task CheckCoreAsync()
    {
        Status = UpdateStatus.Checking; Offer = null; Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            var latest = await _client.LatestAsync(_lifetime.Token);
            if (UpdateVersion.Parse(latest.Version).CompareTo(UpdateVersion.Parse(AppVersion.DisplayVersion)) <= 0)
                Status = UpdateStatus.Current;
            else
            {
                var language = LocalizationService.Instance.CurrentLanguage;
                Offer = await _client.OfferAsync(latest, language, _lifetime.Token);
                if (language != LocalizationService.Instance.CurrentLanguage) throw new OperationCanceledException("UI language changed during update check.");
                Status = UpdateStatus.Available;
            }
        }
        catch (Exception exception)
        {
            Offer = null; Status = UpdateStatus.Failed;
            if (!_disposed) _log.Error("Update/translated in-app notes check failed.", exception);
        }
        finally { if (!_disposed) Changed?.Invoke(this, EventArgs.Empty); }
    }
    public void InvalidateLanguage()
    {
        Offer = null; Status = UpdateStatus.Unknown; Changed?.Invoke(this, EventArgs.Empty);
    }
    public void MarkFailed() { Offer = null; Status = UpdateStatus.Failed; Changed?.Invoke(this, EventArgs.Empty); }
    public void Dispose()
    {
        _disposed = true; _lifetime.Cancel(); _client.Dispose(); _lifetime.Dispose();
    }
}
