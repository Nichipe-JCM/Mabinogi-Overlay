using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using TestOverlay.Update;

namespace TestOverlay.Updater;

internal static class Worker
{
    public static async Task RunAsync(string pipeName, int coordinatorPid)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var token = timeout.Token;
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(30000, token);
        UpdateChannel.VerifyServer(pipe, coordinatorPid);
        using var coordinator = Process.GetProcessById(coordinatorPid);
        UpdateSignature.Verify(coordinator.MainModule!.FileName);
        using var channel = new UpdateChannel(pipe);
        UpdateTransaction? transaction = null;
        var applied = false;
        try
        {
            var request = (await channel.ReadAsync(token)).Request ?? throw new InvalidDataException("Missing update request.");
            using var parent = Process.GetProcessById(request.ParentPid);
            if (parent.StartTime.ToUniversalTime().Ticks != request.ParentStartTicks) throw new InvalidDataException("Original process changed.");
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Target));
            var main = PackageFiles.Resolve(target, GitHubUpdateClient.MainExecutable);
            if (!string.Equals(parent.MainModule!.FileName, main, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Installation path does not match running app.");
            UpdateSignature.Verify(main);
            var currentVersion = FileVersionInfo.GetVersionInfo(main).ProductVersion ?? throw new InvalidDataException("Missing current version.");
            if (UpdateVersion.Parse(currentVersion).CompareTo(UpdateVersion.Parse(request.CurrentVersion)) != 0) throw new InvalidDataException("Current version mismatch.");
            if (UpdateTransaction.Pending(target).Length != 0) throw new IOException("An unfinished update needs recovery before another update can start.");
            // Elevated worker independently gets trusted metadata: caller-supplied hashes,
            // file lists, executable paths and release download URLs are not trusted.
            using var client = new GitHubUpdateClient();
            var release = await client.ReleaseAsync(request.Tag, token);
            if (UpdateVersion.Parse(release.Version).CompareTo(UpdateVersion.Parse(currentVersion)) <= 0) throw new InvalidDataException("Downgrade or same-version update rejected.");
            var previous = await client.InstalledReleaseAsync(request.CurrentVersion, token);
            var oldFiles = await client.FilesAsync(previous, token);
            var newFiles = await client.FilesAsync(release, token);
            _ = await client.OfferAsync(release, request.Language, token);
            transaction = new UpdateTransaction(target);
            var verifiedZip = Path.Combine(transaction.WorkDirectory, "package.zip");
            PackageFiles.RejectLinks(request.ZipPath);
            using (var source = new FileStream(request.ZipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (source.Length != release.Size) throw new InvalidDataException("Package size mismatch.");
                using var destination = new FileStream(verifiedZip, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                await source.CopyToAsync(destination, token);
                destination.Position = 0;
                if (Convert.ToHexString(await SHA256.HashDataAsync(destination, token)) != release.Sha256) throw new InvalidDataException("Package hash mismatch.");
                destination.Flush(true);
            }
            var staged = Path.Combine(transaction.WorkDirectory, "staged");
            PackageFiles.Extract(verifiedZip, staged, newFiles);
            var stagedMain = PackageFiles.Resolve(staged, GitHubUpdateClient.MainExecutable);
            UpdateSignature.Verify(stagedMain);
            UpdateSignature.Verify(PackageFiles.Resolve(staged, "Updater/" + GitHubUpdateClient.HelperExecutable));
            if (UpdateVersion.Parse(FileVersionInfo.GetVersionInfo(stagedMain).ProductVersion!).CompareTo(UpdateVersion.Parse(release.Version)) != 0)
                throw new InvalidDataException("Package version does not match release.");
            await channel.SendAsync(new UpdateMessage("Ready"), token);
            if ((await channel.ReadAsync(token)).Kind != "Proceed") throw new OperationCanceledException("Update not authorized to apply.");
            using (var exitTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                exitTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                await parent.WaitForExitAsync(exitTimeout.Token);
            }
            transaction.Apply(staged, oldFiles, newFiles);
            applied = true;
            await channel.SendAsync(new UpdateMessage("Applied"), token);
            var outcome = await channel.ReadAsync(token);
            if (outcome.Kind == "Commit")
            {
                transaction.Complete();
                await channel.SendAsync(new UpdateMessage("Complete"), token);
                try { transaction.CleanupCompleted(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            else if (outcome.Kind == "Rollback")
            {
                UpdateTransaction.Rollback(transaction.WorkDirectory, target);
                await channel.SendAsync(new UpdateMessage("RolledBack"), token);
            }
            // Keep or a lost connection retains the recovery journal/backups. Never
            // roll back underneath a possibly running new application.
        }
        catch (Exception exception)
        {
            if (transaction is not null && !applied)
                try { UpdateTransaction.Rollback(transaction.WorkDirectory, Path.GetDirectoryName(transaction.WorkDirectory)!); } catch { }
            try { await channel.SendAsync(new UpdateMessage("Failed", exception.Message), CancellationToken.None); } catch { }
            throw;
        }
    }
}
