using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TestOverlay.App.Models;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class MainWindow
{
    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingProfileSelection || ProfileCombo.SelectedItem is not string)
        {
            return;
        }

        FlushProfileAutoSave();
    }

    private void CreateProfileButton_Click(object sender, RoutedEventArgs e)
    {
        FlushProfileAutoSave();
        var dialog = new ProfileNameDialog(string.Empty) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            SetStatus("Profile creation canceled.");
            return;
        }

        var profileName = dialog.ProfileName;
        if (_profileStore.Exists(profileName) && MessageBox.Show(
                this,
                L.F("profile.exists.confirm", profileName),
                L.T("confirm.replace"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            SetStatus("Profile creation canceled.");
            return;
        }

        _selectedProfileName = profileName;
        SaveActiveProfile(showStatus: true);
        RefreshProfileList(profileName);
    }

    private OverlayProfile BuildCurrentProfile(string profileName)
    {
        SaveCurrentSectionSettings();
        CommitMonitorAlertThresholdInputs();
        CommitCustomTimerEditor();
        var profile = OverlayProfileMapper.CreateWorkspaceProfile(
            profileName,
            _workspace,
            ReadSlotInnerWidth(),
            ReadSlotInnerHeight(),
            RefreshIntervalFromFps(_refreshFps));

        profile.BuffMonitorEnabled = _buffMonitorEnabled;
        profile.TuairimMonitorEnabled = _tuairimMonitorEnabled;
        profile.BuffAlertsEnabled = _buffAlertsEnabled;
        profile.TuairimAlertsEnabled = _tuairimAlertsEnabled;
        profile.ShowInternalBuffTimer = !_hiddenMonitorElementKinds.Contains(OverlayElementKind.InternalBuffTimer);
        profile.ShowAlertNotification = !_hiddenMonitorElementKinds.Contains(OverlayElementKind.AlertNotification);
        profile.ShowTuairimGauge = !_hiddenMonitorElementKinds.Contains(OverlayElementKind.TuairimGauge);
        profile.ShowCustomTimer = !_hiddenMonitorElementKinds.Contains(OverlayElementKind.CustomTimer);
        profile.CustomTimers = _customTimerDefinitions.Select(timer => timer.Clone()).ToList();
        _alertAudio.WriteProfile(profile);
        profile.RecognizedBuffNameKeys = InternalBuffTimerPreviewRenderer.BuffNameKeys
            .Where(_recognizedBuffNameKeys.Contains)
            .ToList();
        profile.SelectedBuffNameKeys = InternalBuffTimerPreviewRenderer.BuffNameKeys
            .Where(_selectedBuffNameKeys.Contains)
            .ToList();
        profile.BuffMonitorRoi = ToProfileRect(_buffMonitorRoi);
        profile.BuffAnchors = _buffIconMatches.Values
            .OrderBy(match => match.Bounds.Y)
            .Select(match => new OverlayProfileBuffAnchor
            {
                NameKey = match.NameKey,
                Bounds = ToProfileRect(match.Bounds)!,
                StructureScore = match.StructureScore,
                IsActive = match.IsActive,
                StateConfidence = match.StateConfidence
            })
            .ToList();
        profile.TuairimMonitorRoi = ToProfileRect(_tuairimMonitorRoi);
        profile.TuairimAnchor = ToProfileRect(_tuairimAnchor);
        return profile;
    }

    private void ScheduleProfileAutoSave()
    {
        if (_isLoadingProfile || _monitorTestMode || !IsLoaded)
        {
            return;
        }

        _isProfileDirty = true;
        _profileAutoSaveTimer.Stop();
        _profileAutoSaveTimer.Start();
    }

    private void FlushProfileAutoSave()
    {
        _profileAutoSaveTimer.Stop();
        if (!_isLoadingProfile && IsLoaded && _isProfileDirty)
        {
            _isProfileDirty = false;
            SaveActiveProfile(showStatus: false);
        }
    }

    private void SaveActiveProfile(bool showStatus)
    {
        try
        {
            var profileName = ReadSelectedProfileName();
            var profile = BuildCurrentProfile(profileName);
            var path = _profileStore.Save(profile, profileName);
            _selectedProfileName = System.IO.Path.GetFileNameWithoutExtension(path);
            _isProfileDirty = false;
            _log.Info(
                $"Profile saved: path={path}, automatic={!showStatus}, " +
                $"buffEnabled={profile.BuffMonitorEnabled}, tuairimEnabled={profile.TuairimMonitorEnabled}, " +
                $"recognizedBuffs={profile.RecognizedBuffNameKeys.Count}, selectedBuffs={profile.SelectedBuffNameKeys.Count}, " +
                $"buffRoi={profile.BuffMonitorRoi is not null}, tuairimRoi={profile.TuairimMonitorRoi is not null}");
            if (showStatus)
            {
                _log.Info($"Profile created: {path}, candidates={profile.Candidates.Count}, slots={profile.Slots.Count}");
                SetStatus(L.F("Profile created: {0}", path));
            }
        }
        catch (Exception exception)
        {
            _isProfileDirty = true;
            _log.Error("Profile auto-save failed.", exception);
            SetStatus(L.F("Profile save failed: {0}", exception.Message));
        }
    }

    private void LoadProfileButton_Click(object sender, RoutedEventArgs e) => LoadSelectedProfile();

    private void LoadSelectedProfile()
    {
        FlushProfileAutoSave();
        var profileName = ReadProfileComboName();
        OverlayProfile? profile;
        try
        {
            profile = _profileStore.Load(profileName);
        }
        catch (Exception exception)
        {
            var path = _profileStore.GetProfilePath(profileName);
            _log.Error($"Profile load failed: {path}.", exception);
            SetStatus(L.F("profile.load.failed.arg", path, exception.Message));
            return;
        }

        if (profile is null)
        {
            SetStatus(L.F("No saved profile exists: {0}", _profileStore.GetProfilePath(profileName)));
            return;
        }

        var savedKinds = profile.Candidates.ToDictionary(candidate => candidate.Id, candidate => candidate.Kind);
        if (_capturedImage is null && profile.Slots.Any(slot =>
                !savedKinds.TryGetValue(slot.SourceCandidateId, out var kind) || kind == OverlayElementKind.Quickslot))
        {
            SetStatus("Capture the game window before loading a profile with quickslots.");
            return;
        }

        _isLoadingProfile = true;
        try
        {
            RefreshProfileList(profileName);
            var refreshFps = CoerceRefreshFps(profile.RefreshFps > 0
                ? profile.RefreshFps
                : FpsFromInterval(profile.RefreshIntervalMs));
            OverlayProfileMapper.ApplyLayoutAndSectionSettings(profile, _workspace, refreshFps);
        _buffMonitorEnabled = profile.BuffMonitorEnabled;
        _tuairimMonitorEnabled = profile.TuairimMonitorEnabled;
        _buffAlertsEnabled = profile.BuffAlertsEnabled;
        _tuairimAlertsEnabled = profile.TuairimAlertsEnabled;
        _hiddenMonitorElementKinds.Clear();
        if (!profile.ShowInternalBuffTimer)
        {
            _hiddenMonitorElementKinds.Add(OverlayElementKind.InternalBuffTimer);
        }
        if (!profile.ShowAlertNotification)
        {
            _hiddenMonitorElementKinds.Add(OverlayElementKind.AlertNotification);
        }
        if (!profile.ShowTuairimGauge)
        {
            _hiddenMonitorElementKinds.Add(OverlayElementKind.TuairimGauge);
        }
        if (!profile.ShowCustomTimer)
        {
            _hiddenMonitorElementKinds.Add(OverlayElementKind.CustomTimer);
        }
        _customTimerDefinitions.Clear();
        foreach (var timer in profile.CustomTimers ?? [])
        {
            _customTimerDefinitions.Add(timer.Clone());
        }
        _editingCustomTimer = null;
        CustomTimerList.SelectedItem = _customTimerDefinitions.FirstOrDefault();
        ApplyMonitorAlertSettings(profile);
        _recognizedBuffNameKeys.Clear();
        _selectedBuffNameKeys.Clear();
        _pendingInitialBuffMinuteValidation.Clear();
        _buffIconMatches.Clear();
        _buffMonitorRoi = FromProfileRect(profile.BuffMonitorRoi);
        _tuairimMonitorRoi = FromProfileRect(profile.TuairimMonitorRoi);
        _tuairimAnchor = FromProfileRect(profile.TuairimAnchor);
        ResetTuairimPercentRecognitionState();
        foreach (var key in (profile.RecognizedBuffNameKeys ?? []).Where(InternalBuffTimerPreviewRenderer.BuffNameKeys.Contains))
        {
            _recognizedBuffNameKeys.Add(key);
        }
        foreach (var key in InternalBuffTimerPreviewRenderer.BuffNameKeys.Where((profile.SelectedBuffNameKeys ?? []).Contains))
        {
            if (_recognizedBuffNameKeys.Contains(key) && CanAddBuffSelection(key))
            {
                _selectedBuffNameKeys.Add(key);
            }
        }
        foreach (var savedAnchor in profile.BuffAnchors ?? [])
        {
            var bounds = FromProfileRect(savedAnchor.Bounds);
            if (bounds is null || !InternalBuffTimerPreviewRenderer.BuffNameKeys.Contains(savedAnchor.NameKey))
            {
                continue;
            }

            _buffIconMatches[savedAnchor.NameKey] = new BuffIconMatch(
                savedAnchor.NameKey,
                bounds.Value,
                savedAnchor.StructureScore,
                savedAnchor.IsActive,
                savedAnchor.StateConfidence);
        }
        BuffMonitorEnabledCheckBox.IsChecked = _buffMonitorEnabled;
        TuairimMonitorEnabledCheckBox.IsChecked = _tuairimMonitorEnabled;
        RefreshMonitorDisplayControls();
        RefreshCustomTimerEditor();
        RefreshMonitorAlertSettingsControls();
        var profileWidth = profile.SlotInnerWidth > 0 ? profile.SlotInnerWidth : profile.SlotInnerSize;
        var profileHeight = profile.SlotInnerHeight > 0 ? profile.SlotInnerHeight : profile.SlotInnerSize;
        SlotWidthBox.Text = ReadSlotDimensionText(profileWidth, ReadSlotInnerWidth());
        SlotHeightBox.Text = ReadSlotDimensionText(profileHeight, ReadSlotInnerHeight());
        ApplyProfileSectionSettingsToControls();

        _overlaySlots.Clear();
        _candidates.Clear();
        ClearCandidateRects();
        ClearSections();

        var loadedCandidates = new Dictionary<int, SlotCandidate>();
        if (profile.Candidates.Count > 0)
        {
            foreach (var savedCandidate in profile.Candidates.OrderBy(candidate => candidate.Id))
            {
                if (savedCandidate.Kind == OverlayElementKind.TuairimGauge && _tuairimAnchor is null)
                {
                    continue;
                }

                var candidate = new SlotCandidate(
                    savedCandidate.Id,
                    new Rect(
                        savedCandidate.SourceX,
                        savedCandidate.SourceY,
                        savedCandidate.SourceWidth,
                        savedCandidate.SourceHeight),
                    savedCandidate.Score,
                    savedCandidate.Kind,
                    savedCandidate.DisplayNameKey,
                    savedCandidate.IsBuiltIn)
                {
                    IsSelected = savedCandidate.IsSelected
                };
                AddCandidate(candidate);
                loadedCandidates[candidate.Id] = candidate;
            }
        }

        if (_buffMonitorEnabled)
        {
            var internalTimerCandidate = EnsureInternalTimerCandidate();
            loadedCandidates[internalTimerCandidate.Id] = internalTimerCandidate;
        }
        if (_tuairimMonitorEnabled && _tuairimAnchor is not null)
        {
            var tuairimGaugeCandidate = EnsureTuairimGaugeCandidate();
            loadedCandidates[tuairimGaugeCandidate.Id] = tuairimGaugeCandidate;
        }
        if (_buffMonitorEnabled ||
            _tuairimMonitorEnabled ||
            _customTimerDefinitions.Any(timer => timer.VisualAlertEnabled))
        {
            var alertCandidate = EnsureAlertNotificationCandidate();
            loadedCandidates[alertCandidate.Id] = alertCandidate;
        }
        if (_customTimerDefinitions.Count > 0)
        {
            var customTimerCandidate = EnsureCustomTimerCandidate();
            loadedCandidates[customTimerCandidate.Id] = customTimerCandidate;
        }

        foreach (var savedSection in profile.Sections)
        {
            if (!loadedCandidates.TryGetValue(savedSection.SeedCandidateId, out var seed))
            {
                continue;
            }

            var sectionCandidates = savedSection.CandidateIds
                .Select(id => loadedCandidates.TryGetValue(id, out var candidate) ? candidate : null)
                .Where(candidate => candidate is not null)
                .Cast<SlotCandidate>()
                .ToList();
            if (sectionCandidates.Count == 0)
            {
                continue;
            }

            _sections.Add(new QuickslotSection(
                savedSection.Id,
                seed,
                savedSection.PatternIndex,
                new SectionSettings(savedSection.SmallGapX, savedSection.SmallGapY, savedSection.LargeGap),
                sectionCandidates));
        }

        var nextCandidateId = loadedCandidates.Keys.Where(id => id > 0).DefaultIfEmpty(0).Max() + 1;
        foreach (var savedSlot in profile.Slots)
        {
            if (savedSlot.SourceCandidateId == BuiltInOverlayElementIds.TuairimGauge && _tuairimAnchor is null)
            {
                continue;
            }

            var candidate = ResolveProfileSlotSource(savedSlot, loadedCandidates);
            if (candidate is null)
            {
                candidate = new SlotCandidate(
                    nextCandidateId++,
                    new Rect(savedSlot.SourceX, savedSlot.SourceY, savedSlot.SourceWidth, savedSlot.SourceHeight),
                    100);
                AddCandidate(candidate);
                loadedCandidates[candidate.Id] = candidate;
            }

            var crop = candidate.Kind == OverlayElementKind.Quickslot
                ? _captureSession.Crop(_capturedImage!, candidate.SourceRect)
                : RenderMonitorElementPreview(candidate.Kind);
            var hasOpacityOverride = savedSlot.HasOpacityOverride || Math.Abs(savedSlot.Opacity - 1) > 0.001;
            var slot = new OverlaySlot(
                candidate,
                new Rect(savedSlot.OverlayX, savedSlot.OverlayY, savedSlot.OverlayWidth, savedSlot.OverlayHeight),
                crop,
                savedSlot.Opacity > 0 ? savedSlot.Opacity : 1,
                savedSlot.Scale > 0 ? savedSlot.Scale : InferSlotScale(savedSlot),
                hasOpacityOverride);
            _overlaySlots.Add(slot);
        }

            EnsureEnabledMonitorElementsPlaced();

            _nextSectionId = _sections.Count == 0 ? 1 : _sections.Max(section => section.Id) + 1;
            RefreshSectionLabels();
            UpdateCandidateOverlayFlags();
            UpdateLayoutSummary();
            RefreshInternalTimerElementPreviews();
            UpdateMonitorControlAvailability();
            RefreshMonitorDetectionVisuals();
            var path = _profileStore.GetProfilePath(profileName);
            _log.Info(
                $"Profile loaded: {path}, candidates={_candidates.Count}, slots={profile.Slots.Count}, " +
                $"recoveredFromBackup={_profileStore.LastLoadRecoveredFromBackup}");
            SetStatus(_profileStore.LastLoadRecoveredFromBackup
                ? L.F("profile.loaded.from.backup.arg", path, _candidates.Count, profile.Slots.Count)
                : L.F("Profile loaded: {0} ({1} candidates, {2} slots).", path, _candidates.Count, profile.Slots.Count));
        }
        catch (Exception exception)
        {
            var path = _profileStore.GetProfilePath(profileName);
            _log.Error($"Validated profile could not be applied: {path}.", exception);
            SetStatus(L.F("profile.apply.failed.arg", path, exception.Message));
        }
        finally
        {
            _profileAutoSaveTimer.Stop();
            _isProfileDirty = false;
            _isLoadingProfile = false;
        }
    }
}
