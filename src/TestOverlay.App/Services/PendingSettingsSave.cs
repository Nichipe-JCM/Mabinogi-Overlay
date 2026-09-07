namespace TestOverlay.App.Services;

internal sealed class PendingSettingsSave
{
    public bool IsDirty { get; private set; }
    public Exception? LastError { get; private set; }

    public void MarkDirty() => IsDirty = true;

    public bool TryFlush(Action save)
    {
        if (!IsDirty) return true;
        try
        {
            save();
            IsDirty = false;
            LastError = null;
            return true;
        }
        catch (Exception exception)
        {
            LastError = exception;
            return false;
        }
    }
}
