using System.IO;
using System.Text.Json;

namespace TestOverlay.App.Services;

internal sealed class UpdateCheckThrottle
{
    private readonly string? _path;
    private readonly TimeProvider _clock;
    private DateTimeOffset _next;
    internal sealed record State(DateTimeOffset Next);
    public UpdateCheckThrottle(string? path = null, TimeProvider? clock = null)
    {
        _path = path; _clock = clock ?? TimeProvider.System;
        if (path is null) return;
        try { _next = AtomicJsonFile.Load<State>(path, new JsonSerializerOptions())?.Value.Next ?? default; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { _next = _clock.GetUtcNow().AddMinutes(1); }
        // A clock correction or invalid future timestamp must not lock checks indefinitely.
        if (_next > _clock.GetUtcNow().AddMinutes(1)) _next = _clock.GetUtcNow().AddMinutes(1);
    }
    public int RemainingSeconds => Math.Max(0, (int)Math.Ceiling((_next - _clock.GetUtcNow()).TotalSeconds));
    public bool TryAcquire()
    {
        if (RemainingSeconds != 0) return false;
        _next = _clock.GetUtcNow().AddMinutes(1);
        if (_path is not null) AtomicJsonFile.Save(_path, new State(_next), new JsonSerializerOptions());
        return true;
    }
}
