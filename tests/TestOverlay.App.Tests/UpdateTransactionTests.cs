using System.IO;
using System.IO.Compression;
using TestOverlay.Update;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class UpdateTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "overlay-update-test-" + Guid.NewGuid().ToString("N"));
    private static readonly string[] BaseFiles = [GitHubUpdateClient.MainExecutable, "Updater/" + GitHubUpdateClient.HelperExecutable];
    private string Target => Path.Combine(_root, "target");
    private string Stage => Path.Combine(_root, "stage");
    private void Write(string root, string name, string value)
    {
        var path = Path.Combine(root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value);
    }
    private void Seed()
    {
        foreach (var name in BaseFiles) { Write(Target, name, "old"); Write(Stage, name, "new"); }
    }
    [Fact]
    public void AppliesOnlyProductFilesAndRestoresRemovedFilesOnRollback()
    {
        Seed(); Write(Target, "old.dll", "old library"); Write(Target, "my-sound.mp3", "personal"); Write(Stage, "new.dll", "new library");
        var transaction = new UpdateTransaction(Target);
        transaction.Apply(Stage, [..BaseFiles, "old.dll"], [..BaseFiles, "new.dll"]);
        Assert.False(File.Exists(Path.Combine(Target, "old.dll")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(Target, BaseFiles[0])));
        UpdateTransaction.Rollback(transaction.WorkDirectory, Target);
        UpdateTransaction.Rollback(transaction.WorkDirectory, Target);
        Assert.Equal("old", File.ReadAllText(Path.Combine(Target, BaseFiles[0])));
        Assert.Equal("old library", File.ReadAllText(Path.Combine(Target, "old.dll")));
        Assert.False(File.Exists(Path.Combine(Target, "new.dll")));
        Assert.Equal("personal", File.ReadAllText(Path.Combine(Target, "my-sound.mp3")));
        Assert.Empty(UpdateTransaction.Pending(Target));
    }
    [Fact]
    public void FileLockDuringApplyRestoresAlreadyReplacedFiles()
    {
        Seed(); Write(Target, "locked.dll", "original"); Write(Stage, "locked.dll", "replacement");
        using var locked = new FileStream(Path.Combine(Target, "locked.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var transaction = new UpdateTransaction(Target);
        Assert.ThrowsAny<IOException>(() => transaction.Apply(Stage, [..BaseFiles, "locked.dll"], [..BaseFiles, "locked.dll"]));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Target, BaseFiles[0])));
        Assert.Empty(UpdateTransaction.Pending(Target));
    }
    [Fact]
    public void NewProductFileCannotOverwritePersonalFile()
    {
        Seed(); Write(Target, "custom.dll", "personal"); Write(Stage, "custom.dll", "product");
        var transaction = new UpdateTransaction(Target);
        Assert.Throws<IOException>(() => transaction.Apply(Stage, BaseFiles, [..BaseFiles, "custom.dll"]));
        Assert.Equal("personal", File.ReadAllText(Path.Combine(Target, "custom.dll")));
    }
    [Theory]
    [InlineData("../outside.exe")]
    [InlineData("C:/outside.exe")]
    [InlineData("Updater/../outside.exe")]
    [InlineData("Localization/en-US.lang:stream")]
    [InlineData("Profiles/user.json")]
    [InlineData("save/custom.mp3")]
    [InlineData("settings.json")]
    [InlineData("docs/updates/0.0.7/en-US.md")]
    [InlineData("NUL.txt")]
    [InlineData("folder./file")]
    public void RejectsUnsafeOrUserOwnedPaths(string name) => Assert.Throws<InvalidDataException>(() => PackageFiles.ValidateName(name));
    [Fact]
    public void PackageMustExactlyMatchPublishedList()
    {
        Directory.CreateDirectory(_root);
        var zip = Path.Combine(_root, "bad.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var name in BaseFiles.Append("unexpected.dll")) { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write("content"); }
        }
        Assert.Throws<InvalidDataException>(() => PackageFiles.Extract(zip, Stage, BaseFiles));
    }
    [Fact]
    public void CommittedTransactionIsNotRolledBack()
    {
        Seed(); var transaction = new UpdateTransaction(Target); transaction.Apply(Stage, BaseFiles, BaseFiles); transaction.Complete();
        UpdateTransaction.Rollback(transaction.WorkDirectory, Target);
        Assert.Equal("new", File.ReadAllText(Path.Combine(Target, BaseFiles[0])));
        Assert.Empty(UpdateTransaction.Pending(Target));
    }
    [Fact]
    public void CleanupRequiresCommitAndRemovesOnlyTransactionFiles()
    {
        Seed(); var transaction = new UpdateTransaction(Target);
        transaction.Apply(Stage, BaseFiles, BaseFiles);
        Assert.Throws<InvalidOperationException>(transaction.CleanupCompleted);
        Write(transaction.WorkDirectory, "package.zip", "download");
        Write(Target, "personal.mp3", "personal");
        transaction.Complete(); transaction.CleanupCompleted();
        Assert.False(Directory.Exists(transaction.WorkDirectory));
        Assert.Equal("new", File.ReadAllText(Path.Combine(Target, BaseFiles[0])));
        Assert.Equal("personal", File.ReadAllText(Path.Combine(Target, "personal.mp3")));
    }
    [Fact]
    public void TemporaryCleanupPreservesFailedSessionsAndUnexpectedFiles()
    {
        var completed = Guid.NewGuid().ToString("N"); var failed = Guid.NewGuid().ToString("N");
        Write(_root, completed + "/" + UpdateTemporaryFiles.CompleteMarker, "Complete");
        Write(_root, completed + "/" + GitHubUpdateClient.HelperExecutable, "helper");
        Write(_root, completed + "/personal.txt", "personal");
        Write(_root, failed + "/package.zip", "failed download");
        UpdateTemporaryFiles.Cleanup(_root);
        Assert.False(File.Exists(Path.Combine(_root, completed, GitHubUpdateClient.HelperExecutable)));
        Assert.True(File.Exists(Path.Combine(_root, completed, "personal.txt")));
        Assert.True(File.Exists(Path.Combine(_root, failed, "package.zip")));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
