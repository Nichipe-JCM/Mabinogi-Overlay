using System.IO;
using TestOverlay.App.Models;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"TestOverlay.App.Tests-{Guid.NewGuid():N}");

    [Fact]
    public void Save_RejectsDuplicateCandidateIds()
    {
        var store = new ProfileStore(_directory);
        var profile = CreateValidProfile();
        profile.Candidates.Add(CreateCandidate(1));
        profile.Candidates.Add(CreateCandidate(1));

        var exception = Assert.Throws<InvalidDataException>(() => store.Save(profile, "invalid"));

        Assert.Contains("unique", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(store.GetProfilePath("invalid")));
    }

    [Fact]
    public void Load_CorruptPrimary_RecoversFromBackupAndRepairsPrimary()
    {
        var store = new ProfileStore(_directory);
        var profile = CreateValidProfile();
        profile.Name = "first";
        var path = store.Save(profile, "recover");
        profile.Name = "second";
        store.Save(profile, "recover");
        File.WriteAllText(path, "{corrupt-json");

        var recovered = store.Load("recover");

        Assert.NotNull(recovered);
        Assert.Equal("first", recovered.Name);
        Assert.True(store.LastLoadRecoveredFromBackup);
        Assert.Contains("\"Name\": \"first\"", File.ReadAllText(path));
    }

    [Fact]
    public void Load_NewerBackup_RecoversMostRecentSnapshotAndRepairsPrimary()
    {
        var store = new ProfileStore(_directory);
        var profile = CreateValidProfile();
        profile.Name = "older-primary";
        var path = store.Save(profile, "recover-newer");
        var backupPath = $"{path}.bak";
        File.Copy(path, backupPath);
        profile.Name = "newer-backup";
        File.WriteAllText(backupPath, System.Text.Json.JsonSerializer.Serialize(profile));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-2));
        File.SetLastWriteTimeUtc(backupPath, DateTime.UtcNow.AddMinutes(-1));

        var recovered = store.Load("recover-newer");

        Assert.NotNull(recovered);
        Assert.Equal("newer-backup", recovered.Name);
        Assert.True(store.LastLoadRecoveredFromBackup);
        Assert.Contains("newer-backup", File.ReadAllText(path));
    }

    [Fact]
    public void ListProfileNames_IncludesBackupOnlyProfile()
    {
        var store = new ProfileStore(_directory);
        var profile = CreateValidProfile();
        var path = store.Save(profile, "backup-only");
        store.Save(profile, "backup-only");
        File.Delete(path);

        var names = store.ListProfileNames();

        Assert.Contains("backup-only", names);
    }

    [Fact]
    public void Rename_MovesProfileAndUpdatesEmbeddedName()
    {
        var store = new ProfileStore(_directory);
        var profile = CreateValidProfile();
        store.Save(profile, "before");
        store.Save(profile, "before");

        var renamed = store.Rename("before", "after");

        Assert.Equal("after", renamed);
        Assert.False(store.Exists("before"));
        Assert.True(store.Exists("after"));
        Assert.Equal("after", store.Load("after")!.Name);
        Assert.DoesNotContain("before", store.ListProfileNames());
        Assert.Equal("after", Assert.Single(store.ListProfileNames()));
    }

    [Fact]
    public void ExportAndImport_RoundTripsValidatedProfile()
    {
        var sourceStore = new ProfileStore(Path.Combine(_directory, "source"));
        var targetStore = new ProfileStore(Path.Combine(_directory, "target"));
        var profile = CreateValidProfile();
        profile.CanvasWidth = 777;
        var audioPath = Path.Combine(_directory, "custom-alert.mp3");
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(audioPath, [(byte)'I', (byte)'D', (byte)'3', 1, 2, 3, 4, 5]);
        profile.BuffAlertSoundPath = audioPath;
        profile.CustomTimers.Add(new CustomTimerDefinition
        {
            Id = 1,
            Name = "Portable timer",
            DurationSeconds = 60,
            AlertBeforeSeconds = 10,
            SoundPath = audioPath
        });
        sourceStore.Save(profile, "portable");
        var exportPath = Path.Combine(_directory, $"exported{ProfileStore.ProfilePackageExtension}");

        sourceStore.Export("portable", exportPath);
        var importedName = targetStore.Import(exportPath, "imported");
        var imported = targetStore.Load("imported");

        Assert.Equal("imported", importedName);
        Assert.NotNull(imported);
        Assert.Equal(777, imported.CanvasWidth);
        Assert.NotEqual(audioPath, imported.BuffAlertSoundPath);
        Assert.True(File.Exists(imported.BuffAlertSoundPath));
        Assert.Equal(
            [(byte)'I', (byte)'D', (byte)'3', 1, 2, 3, 4, 5],
            File.ReadAllBytes(imported.BuffAlertSoundPath));
        Assert.Equal(imported.BuffAlertSoundPath, Assert.Single(imported.CustomTimers).SoundPath);

        var importedAssetDirectory = Path.GetDirectoryName(imported.BuffAlertSoundPath)!;
        targetStore.Delete("imported");
        Assert.False(Directory.Exists(importedAssetDirectory));
    }

    [Fact]
    public void Import_LegacyJsonProfile_RemainsSupported()
    {
        var store = new ProfileStore(_directory);
        var sourcePath = Path.Combine(_directory, "legacy.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            sourcePath,
            """
            {
              "Name": "legacy",
              "CanvasWidth": 720,
              "CanvasHeight": 320
            }
            """);

        var importedName = store.Import(sourcePath, "legacy-import");

        Assert.Equal("legacy-import", importedName);
        Assert.Equal(720, store.Load(importedName)!.CanvasWidth);
    }

    [Fact]
    public void Save_RejectsCanvasThatCouldCauseExcessiveAllocation()
    {
        var store = new ProfileStore(_directory);
        var profile = CreateValidProfile();
        profile.CanvasWidth = OverlayProfileValidator.MaximumCanvasDimension;
        profile.CanvasHeight = OverlayProfileValidator.MaximumCanvasDimension;

        var exception = Assert.Throws<InvalidDataException>(() => store.Save(profile, "too-large"));

        Assert.Contains("Canvas area", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(store.Exists("too-large"));
    }

    [Fact]
    public void Delete_RemovesPrimaryAndBackupFromProfileList()
    {
        var store = new ProfileStore(_directory);
        var profile = CreateValidProfile();
        store.Save(profile, "remove-me");
        store.Save(profile, "remove-me");

        store.Delete("remove-me");

        Assert.False(store.Exists("remove-me"));
        Assert.DoesNotContain("remove-me", store.ListProfileNames());
    }

    [Fact]
    public void SaveAndLoad_RoundTripsRepresentativeWorkspaceState()
    {
        var store = new ProfileStore(_directory);
        var profile = CreateValidProfile();
        profile.Name = "representative";
        profile.CanvasWidth = 960;
        profile.CanvasHeight = 420;
        profile.AlertPreviewRows = 4;
        profile.BuffMonitorEnabled = true;
        profile.SelectedBuffNameKeys.Add("battlefield");
        profile.BuffMonitorRoi = new OverlayProfileRect { X = 10, Y = 20, Width = 300, Height = 80 };
        profile.BuffAnchors.Add(new OverlayProfileBuffAnchor
        {
            NameKey = "battlefield",
            TemplateId = "primary-template",
            Bounds = new OverlayProfileRect { X = 18, Y = 27, Width = 18, Height = 18 },
            StructureScore = 0.91,
            IsActive = true,
            StateConfidence = 0.88
        });
        profile.Candidates.Add(CreateCandidate(7));
        profile.Candidates.Add(CreateBuiltInCandidate(
            -1,
            OverlayElementKind.InternalBuffTimer,
            width: 250,
            height: 76));
        profile.Candidates.Add(CreateBuiltInCandidate(
            -4,
            OverlayElementKind.CustomTimer,
            width: 180,
            height: 60));
        profile.CustomTimers.Add(new CustomTimerDefinition
        {
            Id = 1,
            Name = "Mechanic",
            DurationSeconds = 90,
            AlertBeforeSeconds = 10,
            StartHotkey = "Ctrl+Shift+F6",
            CancelHotkey = "Ctrl+Shift+F7",
            SoundPath = "alert.wav",
            Volume = 75
        });
        profile.Sections.Add(new OverlayProfileSection
        {
            Id = 3,
            SeedCandidateId = 7,
            CandidateIds = [7],
            SmallGapX = 2,
            SmallGapY = 5,
            LargeGap = 16
        });
        profile.Slots.Add(new OverlayProfileSlot
        {
            SourceCandidateId = 7,
            SourceWidth = 32,
            SourceHeight = 32,
            OverlayWidth = 48,
            OverlayHeight = 48,
            Opacity = 0.8,
            Scale = 1.5,
            HasOpacityOverride = true
        });
        profile.Slots.Add(new OverlayProfileSlot
        {
            SourceCandidateId = -1,
            SourceWidth = 250,
            SourceHeight = 76,
            OverlayWidth = 250,
            OverlayHeight = 76,
            Scale = 1
        });

        store.Save(profile, profile.Name);
        var loaded = store.Load(profile.Name);

        Assert.NotNull(loaded);
        Assert.Equal(960, loaded.CanvasWidth);
        Assert.Equal(4, loaded.AlertPreviewRows);
        Assert.True(loaded.BuffMonitorEnabled);
        Assert.Equal("battlefield", Assert.Single(loaded.SelectedBuffNameKeys));
        Assert.Contains(loaded.Candidates, candidate => candidate.Id == 7);
        Assert.Contains(loaded.Candidates, candidate =>
            candidate.Id == -1 && candidate.Kind == OverlayElementKind.InternalBuffTimer && candidate.IsBuiltIn);
        Assert.Contains(loaded.Candidates, candidate =>
            candidate.Id == -4 && candidate.Kind == OverlayElementKind.CustomTimer && candidate.IsBuiltIn);
        Assert.Equal(3, Assert.Single(loaded.Sections).Id);
        Assert.Contains(loaded.Slots, slot => slot.SourceCandidateId == 7 && slot.Opacity == 0.8);
        Assert.Contains(loaded.Slots, slot => slot.SourceCandidateId == -1);
        Assert.Equal(10, loaded.BuffMonitorRoi!.X);
        var anchor = Assert.Single(loaded.BuffAnchors);
        Assert.Equal("battlefield", anchor.NameKey);
        Assert.Equal("primary-template", anchor.TemplateId);
        Assert.Equal(18, anchor.Bounds.X);
        var customTimer = Assert.Single(loaded.CustomTimers);
        Assert.Equal("Mechanic", customTimer.Name);
        Assert.Equal(90, customTimer.DurationSeconds);
        Assert.Equal(75, customTimer.Volume);
    }

    [Fact]
    public void Save_RejectsBuiltInCandidateUsingWrongReservedId()
    {
        var store = new ProfileStore(_directory);
        var profile = CreateValidProfile();
        profile.Candidates.Add(CreateBuiltInCandidate(
            -2,
            OverlayElementKind.InternalBuffTimer,
            width: 250,
            height: 76));

        var exception = Assert.Throws<InvalidDataException>(() => store.Save(profile, "invalid-built-in"));

        Assert.Contains("reserved ID -1", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(store.GetProfilePath("invalid-built-in")));
    }

    [Fact]
    public void Load_LegacySlotsOnlyProfile_AppliesModernDefaults()
    {
        var store = new ProfileStore(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            store.GetProfilePath("legacy-slots"),
            """
            {
              "Name": "legacy-slots",
              "CanvasWidth": 360,
              "CanvasHeight": 160,
              "ScreenLeft": 120,
              "ScreenTop": 120,
              "Opacity": 0.8,
              "StopHotkey": "Ctrl+Shift+F8",
              "Slots": [
                {
                  "SourceX": 10,
                  "SourceY": 20,
                  "SourceWidth": 29,
                  "SourceHeight": 29,
                  "OverlayX": 0,
                  "OverlayY": 0,
                  "OverlayWidth": 44,
                  "OverlayHeight": 44
                }
              ]
            }
            """);

        var loaded = store.Load("legacy-slots");

        Assert.NotNull(loaded);
        Assert.Equal(30, loaded.RefreshFps);
        Assert.Equal(29, loaded.SlotInnerWidth);
        Assert.Empty(loaded.Candidates);
        Assert.Equal(1, Assert.Single(loaded.Slots).Scale);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static OverlayProfile CreateValidProfile() => new();

    private static OverlayProfileCandidate CreateCandidate(int id) => new()
    {
        Id = id,
        SourceWidth = 32,
        SourceHeight = 32,
        Kind = OverlayElementKind.Quickslot
    };

    private static OverlayProfileCandidate CreateBuiltInCandidate(
        int id,
        OverlayElementKind kind,
        double width,
        double height) => new()
    {
        Id = id,
        SourceWidth = width,
        SourceHeight = height,
        Kind = kind,
        IsBuiltIn = true
    };
}
