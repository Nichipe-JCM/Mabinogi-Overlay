using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using TestOverlay.Update;

namespace TestOverlay.App.Services;

internal sealed class UpdateStartupGate : IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly UpdateChannel _channel;
    private UpdateStartupGate(NamedPipeClientStream pipe) { _pipe = pipe; _channel = new UpdateChannel(pipe); }
    public static async Task<UpdateStartupGate?> ConnectAsync(string[] args)
    {
        if (args.Length != 3 || args[0] != "--update-startup") return null;
        var pid = int.Parse(args[2]);
        using var helper = Process.GetProcessById(pid);
        UpdateSignature.Verify(helper.MainModule!.FileName);
        var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await pipe.ConnectAsync(timeout.Token);
            UpdateChannel.VerifyServer(pipe, pid);
            return new UpdateStartupGate(pipe);
        }
        catch { pipe.Dispose(); throw; }
    }
    public async Task CompleteAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await _channel.SendAsync(new UpdateMessage("StartupReady"), timeout.Token);
        if ((await _channel.ReadAsync(timeout.Token)).Kind != "Complete") throw new IOException("Update transaction was not finalized.");
    }
    public void Dispose() { _channel.Dispose(); _pipe.Dispose(); }
}
