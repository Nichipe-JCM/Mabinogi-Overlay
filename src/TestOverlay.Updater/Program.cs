using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using TestOverlay.Update;

namespace TestOverlay.Updater;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--worker")
        {
            try { Worker.RunAsync(args[1], int.Parse(args[2])).GetAwaiter().GetResult(); }
            catch { Environment.ExitCode = 1; }
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new UpdaterForm(args));
    }
}

internal sealed class UpdaterForm : Form
{
    private readonly string[] _args;
    private readonly Label _status = new() { Dock = DockStyle.Fill, Padding = new Padding(20), AutoSize = false };
    private readonly Button _close = new() { Dock = DockStyle.Bottom, Height = 36, Enabled = false };
    private bool _busy = true;
    private string _language = "ko-KR";
    public UpdaterForm(string[] args)
    {
        _args = args; Width = 520; Height = 210; StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_status); Controls.Add(_close);
        Text = T("update.title"); _status.Text = T("update.preparing"); _close.Text = T("update.close");
        _close.Click += (_, _) => Close();
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
        Shown += async (_, _) => await RunAsync();
    }
    private string T(string key)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(_language + ".lang", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        while (reader.ReadLine() is { } line)
        {
            var index = line.IndexOf('=');
            if (index > 0 && line[..index].Trim() == key) return line[(index + 1)..].Trim().Replace("\\n", Environment.NewLine);
        }
        return key;
    }
    private async Task RunAsync()
    {
        UpdateChannel? app = null;
        try
        {
            if (_args.Length != 3 || _args[0] != "--connect") throw new InvalidOperationException(T("update.launch.from.app"));
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var appPipe = new NamedPipeClientStream(".", _args[1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await appPipe.ConnectAsync(connectTimeout.Token);
            var appPid = int.Parse(_args[2]);
            UpdateChannel.VerifyServer(appPipe, appPid);
            using var parent = Process.GetProcessById(appPid);
            UpdateSignature.Verify(parent.MainModule!.FileName);
            using var appChannel = new UpdateChannel(appPipe); app = appChannel;
            var message = await app.ReadAsync(connectTimeout.Token);
            var request = message.Request ?? throw new InvalidDataException("Missing update request.");
            if (request.ParentPid != appPid) throw new InvalidDataException("Parent identity mismatch.");
            _language = request.Language is "en-US" ? "en-US" : "ko-KR";
            Text = T("update.title"); _close.Text = T("update.close"); _status.Text = T("update.preparing");
            var pipeName = "MabinogiOverlay.Worker." + Guid.NewGuid().ToString("N");
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            using var workerPipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
            var needElevation = !CanWrite(request.Target);
            if (needElevation) _status.Text = T("update.elevation");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = needElevation, Verb = needElevation ? "runas" : "", CreateNoWindow = true };
            start.ArgumentList.Add("--worker"); start.ArgumentList.Add(pipeName); start.ArgumentList.Add(Environment.ProcessId.ToString());
            using var worker = Process.Start(start) ?? throw new IOException("Updater worker did not start.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            await workerPipe.WaitForConnectionAsync(timeout.Token);
            UpdateChannel.VerifyClient(workerPipe, worker);
            using var channel = new UpdateChannel(workerPipe);
            await channel.SendAsync(new UpdateMessage("Prepare", Request: request), timeout.Token);
            var ready = await channel.ReadAsync(timeout.Token);
            if (ready.Kind != "Ready") throw new IOException(ready.Text ?? "Worker preparation failed.");
            await app.SendAsync(new UpdateMessage("Ready"), timeout.Token);
            var proceed = await app.ReadAsync(timeout.Token);
            if (proceed.Kind != "Proceed") throw new OperationCanceledException("Update canceled before application exit.");
            await channel.SendAsync(new UpdateMessage("Proceed"), timeout.Token);
            _status.Text = T("update.applying");
            UpdateMessage applied;
            do { applied = await channel.ReadAsync(timeout.Token); } while (applied.Kind == "Progress");
            if (applied.Kind != "Applied") throw new IOException(applied.Text ?? "Update failed.");
            _status.Text = T("update.restarting");
            var startupPipeName = "MabinogiOverlay.Startup." + Guid.NewGuid().ToString("N");
            using var startupPipe = new NamedPipeServerStream(startupPipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var appStart = new ProcessStartInfo(Path.Combine(request.Target, GitHubUpdateClient.MainExecutable)) { UseShellExecute = false, WorkingDirectory = request.Target };
            appStart.ArgumentList.Add("--update-startup"); appStart.ArgumentList.Add(startupPipeName); appStart.ArgumentList.Add(Environment.ProcessId.ToString());
            Process? updated = null;
            try
            {
                updated = Process.Start(appStart) ?? throw new IOException("Updated app did not start.");
                using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var connected = startupPipe.WaitForConnectionAsync(startupTimeout.Token);
                if (await Task.WhenAny(connected, updated.WaitForExitAsync(startupTimeout.Token)) != connected) throw new IOException("Updated app exited during startup.");
                await connected;
                UpdateChannel.VerifyClient(startupPipe, updated);
                using var startup = new UpdateChannel(startupPipe);
                var started = await startup.ReadAsync(startupTimeout.Token);
                if (started.Kind != "StartupReady") throw new IOException("Updated app startup failed.");
                await channel.SendAsync(new UpdateMessage("Commit"), timeout.Token);
                var committed = await channel.ReadAsync(timeout.Token);
                if (committed.Kind != "Complete") throw new IOException("Update could not be finalized.");
                await startup.SendAsync(new UpdateMessage("Complete"), timeout.Token);
                _status.Text = T("update.complete");
                try
                {
                    var temporaryDirectory = Path.GetDirectoryName(Environment.ProcessPath!)!;
                    if (Path.GetDirectoryName(Path.GetFullPath(request.ZipPath)) == temporaryDirectory)
                    {
                        File.Delete(PackageFiles.Resolve(temporaryDirectory, "package.zip"));
                        File.WriteAllText(PackageFiles.Resolve(temporaryDirectory, UpdateTemporaryFiles.CompleteMarker), "Complete");
                    }
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            catch
            {
                // Never kill a responsive new app or replace files while it is running.
                if (updated is null || updated.HasExited)
                {
                    await channel.SendAsync(new UpdateMessage("Rollback"), timeout.Token);
                    var restored = await channel.ReadAsync(timeout.Token);
                    if (restored.Kind == "RolledBack") Process.Start(new ProcessStartInfo(Path.Combine(request.Target, GitHubUpdateClient.MainExecutable)) { UseShellExecute = false, WorkingDirectory = request.Target });
                }
                else await channel.SendAsync(new UpdateMessage("Keep"), timeout.Token);
                throw;
            }
            finally { updated?.Dispose(); }
        }
        catch (Exception exception)
        {
            _status.Text = T("update.failed") + Environment.NewLine + exception.Message;
            if (app is not null) try { await app.SendAsync(new UpdateMessage("Failed", exception.Message), CancellationToken.None); } catch { }
        }
        finally { _busy = false; _close.Enabled = true; }
    }
    private static bool CanWrite(string target)
    {
        var probe = Path.Combine(target, ".overlay-write-" + Guid.NewGuid().ToString("N"));
        try { using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose); return true; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
