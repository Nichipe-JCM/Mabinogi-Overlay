using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using TestOverlay.Update;

namespace TestOverlay.App.Services;

public sealed class UpdateLaunchSession : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly UpdateChannel _channel;
    private readonly Process _helper;
    private UpdateLaunchSession(NamedPipeServerStream pipe, UpdateChannel channel, Process helper) { _pipe = pipe; _channel = channel; _helper = helper; }
    public static async Task<UpdateLaunchSession> PrepareAsync(UpdateOffer offer, IProgress<double> progress, CancellationToken token)
    {
        var root = AppContext.BaseDirectory;
        try { UpdateTemporaryFiles.Cleanup(Path.Combine(AppDataPaths.RootDirectory, "Updates")); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        var originalHelper = Path.Combine(root, "Updater", GitHubUpdateClient.HelperExecutable);
        if (!File.Exists(originalHelper)) throw new FileNotFoundException(L.T("update.helper.missing"));
        UpdateSignature.Verify(originalHelper);
        var directory = Path.Combine(AppDataPaths.RootDirectory, "Updates", Guid.NewGuid().ToString("N"));
        PackageFiles.RejectLinks(directory);
        Directory.CreateDirectory(directory);
        var helperFile = Path.Combine(directory, GitHubUpdateClient.HelperExecutable);
        File.Copy(originalHelper, helperFile);
        UpdateSignature.Verify(helperFile);
        using var client = new GitHubUpdateClient();
        var release = await client.ReleaseAsync(offer.Release.Tag, token);
        if (release != offer.Release) throw new InvalidDataException(L.T("update.release.changed"));
        // Do not shut down if translated notes or either version's ownership list is unavailable.
        _ = await client.OfferAsync(release, offer.Language, token);
        _ = await client.FilesAsync(release, token);
        _ = await client.FilesAsync(await client.InstalledReleaseAsync(AppVersion.DisplayVersion, token), token);
        var zip = Path.Combine(directory, "package.zip");
        await client.DownloadAsync(release, zip, progress, token);
        var pipeName = "MabinogiOverlay.Update." + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? helper = null;
        UpdateChannel? channel = null;
        try
        {
            var start = new ProcessStartInfo(helperFile) { UseShellExecute = false, WorkingDirectory = directory };
            start.ArgumentList.Add("--connect"); start.ArgumentList.Add(pipeName); start.ArgumentList.Add(Environment.ProcessId.ToString());
            helper = Process.Start(start) ?? throw new IOException(L.T("update.helper.failed"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            await pipe.WaitForConnectionAsync(timeout.Token);
            UpdateChannel.VerifyClient(pipe, helper);
            channel = new UpdateChannel(pipe);
            using var current = Process.GetCurrentProcess();
            await channel.SendAsync(new UpdateMessage("Prepare", Request: new UpdateRequest(root, zip, AppVersion.DisplayVersion, release.Tag,
                Environment.ProcessId, current.StartTime.ToUniversalTime().Ticks, offer.Language)), timeout.Token);
            var response = await channel.ReadAsync(timeout.Token);
            if (response.Kind != "Ready") throw new IOException(response.Text ?? L.T("update.helper.failed"));
            return new UpdateLaunchSession(pipe, channel, helper);
        }
        catch { channel?.Dispose(); pipe.Dispose(); helper?.Dispose(); throw; }
    }
    public void Proceed()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _channel.SendAsync(new UpdateMessage("Proceed"), timeout.Token).GetAwaiter().GetResult();
    }
    public void Dispose() { _channel.Dispose(); _pipe.Dispose(); _helper.Dispose(); }
}
