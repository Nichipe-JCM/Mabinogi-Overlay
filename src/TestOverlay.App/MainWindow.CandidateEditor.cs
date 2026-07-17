using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using TestOverlay.App.Models;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class MainWindow
{
    private void BeginCandidateBoxSelection(Point position)
    {
        _isSelectingCandidates = true;
        _selectionStartPosition = position;
        var isMultiSelectModifierPressed =
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ||
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (!isMultiSelectModifierPressed)
        {
            ClearCandidateSelection();
        }

        _selectionRect = new Rectangle
        {
            Stroke = CreateProjectAccentBrush(),
            StrokeThickness = 1,
            Fill = CreateProjectAccentBrush(35),
            IsHitTestVisible = false
        };
        CaptureCanvas.Children.Add(_selectionRect);
        Canvas.SetLeft(_selectionRect, position.X);
        Canvas.SetTop(_selectionRect, position.Y);
        CaptureCanvas.CaptureMouse();
    }

    private void UpdateCandidateBoxSelection(Point position)
    {
        UpdateSelectionRectangle(position);
    }

    private void UpdateSelectionRectangle(Point position)
    {
        if (_selectionRect is null)
        {
            return;
        }

        var left = Math.Min(_selectionStartPosition.X, position.X);
        var top = Math.Min(_selectionStartPosition.Y, position.Y);
        var width = Math.Abs(position.X - _selectionStartPosition.X);
        var height = Math.Abs(position.Y - _selectionStartPosition.Y);
        Canvas.SetLeft(_selectionRect, left);
        Canvas.SetTop(_selectionRect, top);
        _selectionRect.Width = width;
        _selectionRect.Height = height;
    }

    private void EndCandidateBoxSelection()
    {
        if (_selectionRect is not null)
        {
            var selection = new Rect(
                Canvas.GetLeft(_selectionRect),
                Canvas.GetTop(_selectionRect),
                _selectionRect.Width,
                _selectionRect.Height);
            foreach (var candidate in _candidates)
            {
                if (selection.IntersectsWith(GetCandidateVisualRect(candidate)))
                {
                    candidate.IsSelected = true;
                }
            }

            RemoveSelectionRectangle();
        }

        _isSelectingCandidates = false;
        ReleaseCaptureSafely(CaptureCanvas);
    }

    private void MoveSelectedCandidates(Point position)
    {
        if (_candidateDragOrigins.Count == 0)
        {
            return;
        }

        var requestedDeltaX = position.X - _candidateDragStartPosition.X;
        var requestedDeltaY = position.Y - _candidateDragStartPosition.Y;
        var minDeltaX = _candidateDragOrigins.Max(item => CandidateBorderPixels - item.Value.X);
        var minDeltaY = _candidateDragOrigins.Max(item => CandidateBorderPixels - item.Value.Y);
        var maxDeltaX = _candidateDragOrigins.Min(item => CaptureCanvas.Width - CandidateBorderPixels - item.Value.X - item.Key.SourceRect.Width);
        var maxDeltaY = _candidateDragOrigins.Min(item => CaptureCanvas.Height - CandidateBorderPixels - item.Value.Y - item.Key.SourceRect.Height);
        var deltaX = Math.Clamp(requestedDeltaX, minDeltaX, maxDeltaX);
        var deltaY = Math.Clamp(requestedDeltaY, minDeltaY, maxDeltaY);

        foreach (var (candidate, origin) in _candidateDragOrigins)
        {
            var x = origin.X + deltaX;
            var y = origin.Y + deltaY;
            MoveCandidate(candidate, x, y);
        }
    }

    private void MoveCandidate(SlotCandidate candidate, double x, double y)
    {
        _candidateWorkspace.MoveCandidate(
            candidate,
            x,
            y,
            CaptureCanvas.Width,
            CaptureCanvas.Height,
            CandidateBorderPixels);
        UpdateCandidateVisualPosition(candidate);
    }

    private void UpdateCandidateVisualPosition(SlotCandidate candidate)
    {
        if (_candidateRects.TryGetValue(candidate, out var candidateRect))
        {
            var visualRect = GetCandidateVisualRect(candidate);
            candidateRect.Width = visualRect.Width;
            candidateRect.Height = visualRect.Height;
            Canvas.SetLeft(candidateRect, visualRect.X);
            Canvas.SetTop(candidateRect, visualRect.Y);
        }
    }

    private void SetOnlyCandidateSelected(SlotCandidate selected)
        => _candidateWorkspace.SelectOnly(selected);

    private void ClearCandidateSelection()
        => _candidateWorkspace.ClearSelection();

    private int AddSectionCandidates(SlotCandidate seed, SectionPattern pattern, int patternIndex, SectionSettings settings)
    {
        var added = 0;
        var sectionCandidates = new List<SlotCandidate>();

        ClearCandidateSelection();
        foreach (var offset in BuildSectionOffsets(seed, pattern, settings.SmallGapX, settings.SmallGapY, settings.LargeGap))
        {
            var rect = new Rect(
                seed.SourceRect.X + offset.X,
                seed.SourceRect.Y + offset.Y,
                seed.SourceRect.Width,
                seed.SourceRect.Height);
            if (!IsRectInsideCapture(rect))
            {
                continue;
            }

            var existing = FindMatchingCandidate(rect);
            if (existing is not null)
            {
                existing.IsSelected = true;
                sectionCandidates.Add(existing);
                continue;
            }

            var candidate = new SlotCandidate(NextCandidateId(), rect, 200);
            candidate.IsSelected = true;
            AddCandidate(candidate);
            sectionCandidates.Add(candidate);
            added++;
        }

        CandidateList.SelectedItem = seed;
        var section = _candidateWorkspace.AddSection(seed, patternIndex, settings, sectionCandidates);
        SelectSection(section);
        return added;
    }

    private void AddDetectedSection(SectionDetectionResult result)
    {
        var patternIndex = PatternIndexFromKind(result.PatternKind);
        var settings = new SectionSettings(result.SmallGapX, result.SmallGapY, result.LargeGap);
        var sectionCandidates = new List<SlotCandidate>();

        ClearCandidateSelection();
        foreach (var rect in result.Slots)
        {
            var candidate = FindMatchingCandidate(rect);
            if (candidate is null)
            {
                candidate = new SlotCandidate(NextCandidateId(), rect, result.Score);
                AddCandidate(candidate);
            }

            candidate.IsSelected = true;
            sectionCandidates.Add(candidate);
        }

        if (sectionCandidates.Count == 0)
        {
            return;
        }

        var seed = sectionCandidates
            .OrderBy(candidate => candidate.SourceRect.Y)
            .ThenBy(candidate => candidate.SourceRect.X)
            .First();
        CandidateList.SelectedItem = seed;
        _sectionSettings[patternIndex] = settings;
        var section = _candidateWorkspace.AddSection(seed, patternIndex, settings, sectionCandidates);
        SelectSection(section);
    }

    private static IEnumerable<Point> BuildSectionOffsets(
        SlotCandidate seed,
        SectionPattern pattern,
        double smallGap,
        double smallGapY,
        double largeGap)
    {
        var slotWidth = seed.SourceRect.Width;
        var slotHeight = seed.SourceRect.Height;
        var smallGapX = pattern.InnerGapX(smallGap);
        var smallGapYValue = pattern.InnerGapY(smallGapY);
        var innerPitchX = slotWidth + smallGapX;
        var innerPitchY = slotHeight + smallGapYValue;
        var groupPitchX = (pattern.GroupColumns * slotWidth) +
                          (Math.Max(0, pattern.GroupColumns - 1) * smallGapX) +
                          pattern.GroupGapX(largeGap);
        var groupPitchY = (pattern.GroupRows * slotHeight) +
                          (Math.Max(0, pattern.GroupRows - 1) * smallGapYValue) +
                          pattern.GroupGapY(largeGap);

        for (var groupY = 0; groupY < pattern.GroupRowsCount; groupY++)
        {
            for (var groupX = 0; groupX < pattern.GroupColumnsCount; groupX++)
            {
                for (var row = 0; row < pattern.GroupRows; row++)
                {
                    for (var column = 0; column < pattern.GroupColumns; column++)
                    {
                        yield return new Point(
                            groupX * groupPitchX + column * innerPitchX,
                            groupY * groupPitchY + row * innerPitchY);
                    }
                }
            }
        }
    }

    private SlotCandidate? FindMatchingCandidate(Rect rect)
    {
        var tolerance = Math.Max(2, rect.Width * 0.18);
        return _candidates.FirstOrDefault(candidate =>
            Math.Abs(candidate.SourceRect.X - rect.X) <= tolerance &&
            Math.Abs(candidate.SourceRect.Y - rect.Y) <= tolerance &&
            Math.Abs(candidate.SourceRect.Width - rect.Width) <= tolerance &&
            Math.Abs(candidate.SourceRect.Height - rect.Height) <= tolerance);
    }

    private bool IsRectInsideCapture(Rect rect) =>
        rect.X >= CandidateBorderPixels &&
        rect.Y >= CandidateBorderPixels &&
        rect.Right <= CaptureCanvas.Width - CandidateBorderPixels &&
        rect.Bottom <= CaptureCanvas.Height - CandidateBorderPixels;

    private static Rect GetCandidateVisualRect(SlotCandidate candidate)
    {
        var rect = candidate.SourceRect;
        rect.Inflate(CandidateVisualPaddingPixels, CandidateVisualPaddingPixels);
        return rect;
    }

    private SectionPattern ReadSectionPattern() =>
        GetSectionPattern(Math.Clamp(SectionPatternCombo.SelectedIndex, 0, _sectionSettings.Length - 1));

    private static SectionPattern GetSectionPattern(int index) =>
        index == 1 ? SectionPattern.Vertical() : SectionPattern.TopGrouped();

    private static int PatternIndexFromKind(QuickslotSectionPatternKind kind) =>
        kind == QuickslotSectionPatternKind.Vertical ? 1 : 0;

    private void RebuildSelectedSection()
    {
        if (_selectedSection is null || _capturedImage is null || _isUpdatingSectionControls)
        {
            return;
        }

        _selectedSection.Settings = new SectionSettings(ReadSmallGapX(), ReadSmallGapY(), ReadLargeGap());
        var pattern = GetSectionPattern(_selectedSection.PatternIndex);
        var offsets = BuildSectionOffsets(
                _selectedSection.Seed,
                pattern,
                _selectedSection.Settings.SmallGapX,
                _selectedSection.Settings.SmallGapY,
                _selectedSection.Settings.LargeGap)
            .ToList();
        var count = Math.Min(offsets.Count, _selectedSection.Candidates.Count);
        for (var i = 0; i < count; i++)
        {
            var candidate = _selectedSection.Candidates[i];
            var offset = offsets[i];
            var x = _selectedSection.Seed.SourceRect.X + offset.X;
            var y = _selectedSection.Seed.SourceRect.Y + offset.Y;
            MoveCandidate(candidate, x, y);
        }

        RefreshSectionLabels();
        SetStatus(L.F(
            "Adjusted {0}: gap X {1}px, gap Y {2}px, large gap {3}px.",
            _selectedSection.Label,
            ReadSmallGapX().ToString("0"),
            ReadSmallGapY().ToString("0"),
            ReadLargeGap().ToString("0")));
    }

    private bool TryNudgeSelectedCandidates(Key key)
    {
        var delta = key switch
        {
            Key.Left => new Vector(-1, 0),
            Key.Right => new Vector(1, 0),
            Key.Up => new Vector(0, -1),
            Key.Down => new Vector(0, 1),
            _ => default
        };
        if (delta == default)
        {
            return false;
        }

        Keyboard.ClearFocus();
        CaptureCanvas.Focus();
        var selected = _candidates.Where(candidate => candidate.IsSelected && !candidate.IsBuiltIn).ToList();
        if (selected.Count == 0 && CandidateList.SelectedItem is SlotCandidate { IsBuiltIn: false } highlighted)
        {
            selected.Add(highlighted);
        }

        if (selected.Count == 0)
        {
            return false;
        }

        var before = CaptureCandidateSnapshot();
        foreach (var candidate in selected)
        {
            MoveCandidate(
                candidate,
                candidate.SourceRect.X + delta.X,
                candidate.SourceRect.Y + delta.Y);
        }
        PushUndoIfChanged(before);

        SetStatus(L.F("Nudged {0} candidate(s) by 1px.", selected.Count));
        return true;
    }

    private void AddCandidate(SlotCandidate candidate)
    {
        _candidates.Add(candidate);
        candidate.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SlotCandidate.IsSelected))
            {
                UpdateCandidateVisual(candidate);
                ScheduleProfileAutoSave();
            }
        };
        if (!candidate.IsBuiltIn)
        {
            AddCandidateVisual(candidate);
        }
    }

    private SlotCandidate EnsureInternalTimerCandidate()
    {
        var existing = _candidates.FirstOrDefault(candidate => candidate.Kind == OverlayElementKind.InternalBuffTimer);
        if (existing is not null)
        {
            ResizeMonitorElement(
                existing,
                InternalBuffTimerPreviewRenderer.BaseWidth,
                InternalBuffTimerPreviewRenderer.GetBaseHeight(_selectedBuffNameKeys.Count));
            return existing;
        }

        var baseHeight = InternalBuffTimerPreviewRenderer.GetBaseHeight(_selectedBuffNameKeys.Count);
        var candidate = new SlotCandidate(
            BuiltInOverlayElementIds.InternalBuffTimer,
            new Rect(0, 0, InternalBuffTimerPreviewRenderer.BaseWidth, baseHeight),
            100,
            OverlayElementKind.InternalBuffTimer,
            "monitor.timer.element",
            isBuiltIn: true);
        AddCandidate(candidate);
        return candidate;
    }

    private SlotCandidate EnsureTuairimGaugeCandidate()
    {
        var existing = _candidates.FirstOrDefault(candidate => candidate.Kind == OverlayElementKind.TuairimGauge);
        if (existing is not null)
        {
            ResizeMonitorElement(
                existing,
                TuairimGaugePreviewRenderer.BaseWidth,
                TuairimGaugePreviewRenderer.BaseHeight);
            return existing;
        }

        var candidate = new SlotCandidate(
            BuiltInOverlayElementIds.TuairimGauge,
            new Rect(0, 0, TuairimGaugePreviewRenderer.BaseWidth, TuairimGaugePreviewRenderer.BaseHeight),
            100,
            OverlayElementKind.TuairimGauge,
            "monitor.tuairim.element",
            isBuiltIn: true);
        AddCandidate(candidate);
        return candidate;
    }

    private SlotCandidate EnsureAlertNotificationCandidate()
    {
        var existing = _candidates.FirstOrDefault(candidate => candidate.Kind == OverlayElementKind.AlertNotification);
        if (existing is not null)
        {
            ResizeMonitorElement(
                existing,
                AlertNotificationPreviewRenderer.BaseWidth,
                AlertNotificationPreviewRenderer.GetBaseHeight(_alertPreviewRows));
            return existing;
        }

        var candidate = new SlotCandidate(
            BuiltInOverlayElementIds.AlertNotification,
            new Rect(0, 0, AlertNotificationPreviewRenderer.BaseWidth, AlertNotificationPreviewRenderer.GetBaseHeight(_alertPreviewRows)),
            100,
            OverlayElementKind.AlertNotification,
            "monitor.alert.element",
            isBuiltIn: true);
        AddCandidate(candidate);
        return candidate;
    }

    private SlotCandidate EnsureCustomTimerCandidate()
    {
        var existing = _candidates.FirstOrDefault(candidate => candidate.Kind == OverlayElementKind.CustomTimer);
        if (existing is not null)
        {
            ResizeMonitorElement(
                existing,
                CustomTimerPreviewRenderer.BaseWidth,
                CustomTimerPreviewRenderer.GetBaseHeight(_customTimerDefinitions.Count));
            return existing;
        }

        var candidate = new SlotCandidate(
            BuiltInOverlayElementIds.CustomTimer,
            new Rect(
                0,
                0,
                CustomTimerPreviewRenderer.BaseWidth,
                CustomTimerPreviewRenderer.GetBaseHeight(_customTimerDefinitions.Count)),
            100,
            OverlayElementKind.CustomTimer,
            "custom.timer.overlay.element",
            isBuiltIn: true);
        AddCandidate(candidate);
        return candidate;
    }

    private void SynchronizeMonitorElementDimensions()
    {
        var timerCandidate = _candidates.FirstOrDefault(candidate => candidate.Kind == OverlayElementKind.InternalBuffTimer);
        if (timerCandidate is not null)
        {
            ResizeMonitorElement(
                timerCandidate,
                InternalBuffTimerPreviewRenderer.BaseWidth,
                InternalBuffTimerPreviewRenderer.GetBaseHeight(_selectedBuffNameKeys.Count));
        }

        var tuairimCandidate = _candidates.FirstOrDefault(candidate => candidate.Kind == OverlayElementKind.TuairimGauge);
        if (tuairimCandidate is not null)
        {
            ResizeMonitorElement(
                tuairimCandidate,
                TuairimGaugePreviewRenderer.BaseWidth,
                TuairimGaugePreviewRenderer.BaseHeight);
        }

        var alertCandidate = _candidates.FirstOrDefault(candidate => candidate.Kind == OverlayElementKind.AlertNotification);
        if (alertCandidate is not null)
        {
            ResizeMonitorElement(
                alertCandidate,
                AlertNotificationPreviewRenderer.BaseWidth,
                AlertNotificationPreviewRenderer.GetBaseHeight(_alertPreviewRows));
        }

        var customTimerCandidate = _candidates.FirstOrDefault(candidate => candidate.Kind == OverlayElementKind.CustomTimer);
        if (customTimerCandidate is not null)
        {
            ResizeMonitorElement(
                customTimerCandidate,
                CustomTimerPreviewRenderer.BaseWidth,
                CustomTimerPreviewRenderer.GetBaseHeight(_customTimerDefinitions.Count));
        }
    }

    private void ResizeMonitorElement(SlotCandidate candidate, double width, double height)
    {
        var oldWidth = candidate.SourceRect.Width;
        var oldHeight = candidate.SourceRect.Height;
        if (Math.Abs(oldWidth - width) < 0.01 && Math.Abs(oldHeight - height) < 0.01)
        {
            return;
        }

        var slot = _overlaySlots.FirstOrDefault(item => item.Kind == candidate.Kind);
        if (slot is not null)
        {
            var scale = oldWidth > 0 && oldHeight > 0
                ? Math.Min(slot.OverlayRect.Width / oldWidth, slot.OverlayRect.Height / oldHeight)
                : Math.Max(0.1, slot.Scale);
            if (candidate.Kind == OverlayElementKind.InternalBuffTimer && Math.Abs(oldWidth - width) < 0.01)
            {
                slot.OverlayRect = new Rect(
                    slot.OverlayRect.X,
                    slot.OverlayRect.Y,
                    slot.OverlayRect.Width,
                    Math.Max(MinimumOverlaySlotSize, height * slot.OverlayRect.Width / width));
            }
            else
            {
                slot.OverlayRect = new Rect(
                    slot.OverlayRect.X,
                    slot.OverlayRect.Y,
                    Math.Max(MinimumOverlaySlotSize, width * scale),
                    Math.Max(MinimumOverlaySlotSize, height * scale));
            }
        }

        candidate.ResizeTo(width, height);
        if (slot is not null)
        {
            _layoutCanvasWidth = Math.Max(_layoutCanvasWidth, slot.OverlayRect.Width + 16);
            _layoutCanvasHeight = Math.Max(_layoutCanvasHeight, slot.OverlayRect.Height + 16);
            slot.OverlayRect = new Rect(
                Math.Clamp(slot.OverlayRect.X, 0, Math.Max(0, _layoutCanvasWidth - slot.OverlayRect.Width)),
                Math.Clamp(slot.OverlayRect.Y, 0, Math.Max(0, _layoutCanvasHeight - slot.OverlayRect.Height)),
                slot.OverlayRect.Width,
                slot.OverlayRect.Height);
        }
    }

    private void SetMonitorElementEnabled(OverlayElementKind kind, bool enabled, bool scheduleAutoSave = true)
    {
        if (enabled)
        {
            EnsureMonitorElementPlaced(kind, scheduleAutoSave: false);
        }
        else
        {
            var candidates = _candidates.Where(candidate => candidate.Kind == kind).ToList();
            _overlaySlots.RemoveAll(slot => slot.Kind == kind);
            foreach (var candidate in candidates)
            {
                if (ReferenceEquals(CandidateList.SelectedItem, candidate))
                {
                    CandidateList.SelectedItem = null;
                }
                _candidates.Remove(candidate);
            }

            UpdateCandidateOverlayFlags();
            UpdateLayoutSummary();
        }

        if (scheduleAutoSave)
        {
            ScheduleProfileAutoSave();
        }
    }

    private void SetMonitorElementVisibility(OverlayElementKind kind, bool visible)
    {
        if (visible)
        {
            _hiddenMonitorElementKinds.Remove(kind);
            EnsureMonitorElementPlaced(kind, scheduleAutoSave: false);
        }
        else
        {
            _hiddenMonitorElementKinds.Add(kind);
            SetMonitorElementEnabled(kind, enabled: false, scheduleAutoSave: false);
        }

        RefreshMonitorDisplayControls();
        ScheduleProfileAutoSave();
    }

    private void EnsureEnabledMonitorElementsPlaced(bool scheduleAutoSave = false)
    {
        if (_buffMonitorEnabled)
        {
            EnsureMonitorElementPlaced(OverlayElementKind.InternalBuffTimer, scheduleAutoSave);
        }
        if (_tuairimMonitorEnabled && (_tuairimAnchor is not null || _monitorTestMode))
        {
            EnsureMonitorElementPlaced(OverlayElementKind.TuairimGauge, scheduleAutoSave);
        }
        if (_buffMonitorEnabled || _tuairimMonitorEnabled || _customTimerDefinitions.Any(timer => timer.VisualAlertEnabled))
        {
            EnsureMonitorElementPlaced(OverlayElementKind.AlertNotification, scheduleAutoSave);
        }
        if (_customTimerDefinitions.Count > 0)
        {
            EnsureMonitorElementPlaced(OverlayElementKind.CustomTimer, scheduleAutoSave);
        }
    }

    private void SynchronizeAlertNotificationElement(bool scheduleAutoSave = true) =>
        SetMonitorElementEnabled(
            OverlayElementKind.AlertNotification,
            _buffMonitorEnabled ||
            _tuairimMonitorEnabled ||
            _customTimerDefinitions.Any(timer => timer.VisualAlertEnabled),
            scheduleAutoSave);

    private bool IsMonitorElementEnabled(OverlayElementKind kind) => kind switch
    {
        OverlayElementKind.InternalBuffTimer => _buffMonitorEnabled,
        OverlayElementKind.TuairimGauge => _tuairimMonitorEnabled && (_tuairimAnchor is not null || _monitorTestMode),
        OverlayElementKind.AlertNotification =>
            _buffMonitorEnabled || _tuairimMonitorEnabled || _customTimerDefinitions.Any(timer => timer.VisualAlertEnabled),
        OverlayElementKind.CustomTimer => _customTimerDefinitions.Count > 0,
        _ => false
    };

    private void EnsureMonitorElementPlaced(OverlayElementKind kind, bool scheduleAutoSave = true)
    {
        if (kind == OverlayElementKind.TuairimGauge && _tuairimAnchor is null && !_monitorTestMode)
        {
            return;
        }

        if (_hiddenMonitorElementKinds.Contains(kind))
        {
            UpdateCandidateOverlayFlags();
            return;
        }

        var candidate = kind switch
        {
            OverlayElementKind.InternalBuffTimer => EnsureInternalTimerCandidate(),
            OverlayElementKind.TuairimGauge => EnsureTuairimGaugeCandidate(),
            OverlayElementKind.AlertNotification => EnsureAlertNotificationCandidate(),
            OverlayElementKind.CustomTimer => EnsureCustomTimerCandidate(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only monitor elements can be auto-placed.")
        };
        var existingSlot = _overlaySlots.FirstOrDefault(slot => slot.Kind == kind);
        if (existingSlot is not null)
        {
            existingSlot.Source = candidate;
            existingSlot.Preview = RenderMonitorElementPreview(kind);
            UpdateCandidateOverlayFlags();
            return;
        }

        var scale = ReadLayoutSlotScale();
        var width = Math.Max(MinimumOverlaySlotSize, candidate.SourceRect.Width * scale);
        var height = Math.Max(MinimumOverlaySlotSize, candidate.SourceRect.Height * scale);
        var placement = FindMonitorElementPlacement(width, height);
        _overlaySlots.Add(new OverlaySlot(
            candidate,
            new Rect(placement.X, placement.Y, width, height),
            RenderMonitorElementPreview(kind),
            scale: scale));
        UpdateCandidateOverlayFlags();
        UpdateLayoutSummary();
        if (scheduleAutoSave)
        {
            ScheduleProfileAutoSave();
        }
    }

    private Point FindMonitorElementPlacement(double width, double height)
    {
        const double margin = 8;
        const double step = 8;
        _layoutCanvasWidth = Math.Max(_layoutCanvasWidth, width + margin * 2);
        _layoutCanvasHeight = Math.Max(_layoutCanvasHeight, height + margin * 2);

        for (var y = margin; y + height <= _layoutCanvasHeight - margin + 0.01; y += step)
        {
            for (var x = margin; x + width <= _layoutCanvasWidth - margin + 0.01; x += step)
            {
                var candidate = new Rect(x, y, width, height);
                if (_overlaySlots.Any(slot => !Rect.Intersect(
                        new Rect(
                            slot.OverlayRect.X - margin,
                            slot.OverlayRect.Y - margin,
                            slot.OverlayRect.Width + margin * 2,
                            slot.OverlayRect.Height + margin * 2),
                        candidate).IsEmpty))
                {
                    continue;
                }

                return new Point(x, y);
            }
        }

        var nextY = _overlaySlots.Count == 0 ? margin : _overlaySlots.Max(slot => slot.OverlayRect.Bottom) + margin;
        _layoutCanvasHeight = Math.Max(_layoutCanvasHeight, nextY + height + margin);
        return new Point(margin, nextY);
    }

    private BitmapSource RenderMonitorElementPreview(OverlayElementKind kind) => kind switch
    {
        OverlayElementKind.InternalBuffTimer => InternalBuffTimerPreviewRenderer.Render(
            _internalBuffTimers,
            _selectedBuffNameKeys),
        OverlayElementKind.TuairimGauge => TuairimGaugePreviewRenderer.Render(_statusObservations.TuairimPercent),
        OverlayElementKind.AlertNotification => AlertNotificationPreviewRenderer.Render(_alertPreviewRows),
        OverlayElementKind.CustomTimer => CustomTimerPreviewRenderer.Render(_customTimerDefinitions.Count),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A quickslot requires a captured image crop.")
    };

    private void UpdateCandidateVisual(SlotCandidate candidate)
    {
        if (!_candidateRects.TryGetValue(candidate, out var rect))
        {
            return;
        }

        rect.Stroke = candidate.IsSelected ? Brushes.LimeGreen : Brushes.OrangeRed;
        rect.Fill = candidate.IsSelected
            ? new SolidColorBrush(Color.FromArgb(55, 50, 205, 50))
            : new SolidColorBrush(Color.FromArgb(35, 255, 80, 80));
    }

    private void SelectCandidateInList(SlotCandidate candidate)
    {
        CandidateList.SelectedItem = candidate;
        CandidateList.ScrollIntoView(candidate);
        CandidateList.Focus();
    }

    private void ClearLayout()
    {
        _overlaySlots.Clear();
        HideAllMonitorElements();
        UpdateLayoutSummary();
        UpdateCandidateOverlayFlags();
        RefreshMonitorDisplayControls();
        RefreshCustomTimerEditor();
    }

    private void HideAllMonitorElements()
    {
        _hiddenMonitorElementKinds.Add(OverlayElementKind.InternalBuffTimer);
        _hiddenMonitorElementKinds.Add(OverlayElementKind.AlertNotification);
        _hiddenMonitorElementKinds.Add(OverlayElementKind.TuairimGauge);
        _hiddenMonitorElementKinds.Add(OverlayElementKind.CustomTimer);
    }

    private void SynchronizeHiddenMonitorElementsFromLayout()
    {
        foreach (var kind in new[]
                 {
                     OverlayElementKind.InternalBuffTimer,
                     OverlayElementKind.AlertNotification,
                     OverlayElementKind.TuairimGauge,
                     OverlayElementKind.CustomTimer
                 })
        {
            if (_overlaySlots.Any(slot => slot.Kind == kind))
            {
                _hiddenMonitorElementKinds.Remove(kind);
            }
            else
            {
                _hiddenMonitorElementKinds.Add(kind);
            }
        }
    }

    private int RemoveOverlaySlotsForCandidates(IEnumerable<SlotCandidate> candidates)
        => _candidateWorkspace.RemoveOverlaySlotsForCandidates(candidates);

    private void UpdateCandidateOverlayFlags()
        => _candidateWorkspace.UpdateCandidateOverlayFlags();

    private void SelectSection(QuickslotSection section)
    {
        _selectedSection = section;
        _isUpdatingSectionSelection = true;
        try
        {
            SectionCombo.SelectedItem = section;
        }
        finally
        {
            _isUpdatingSectionSelection = false;
        }

        SelectSectionCandidates(section);
        LoadSectionControls(section);
        RefreshSectionLabels();
    }

    private void SelectSectionCandidates(QuickslotSection section)
    {
        foreach (var candidate in _candidates)
        {
            candidate.IsSelected = section.Candidates.Contains(candidate);
        }

        CandidateList.SelectedItem = section.Seed;
    }

    private void LoadSectionControls(QuickslotSection section)
    {
        _currentSectionIndex = Math.Clamp(section.PatternIndex, 0, _sectionSettings.Length - 1);
        _sectionSettings[_currentSectionIndex] = section.Settings;
        _isUpdatingSectionControls = true;
        try
        {
            SectionPatternCombo.SelectedIndex = _currentSectionIndex;
            SmallGapXSlider.Value = Math.Clamp(section.Settings.SmallGapX, SmallGapXSlider.Minimum, SmallGapXSlider.Maximum);
            SmallGapYSlider.Value = Math.Clamp(section.Settings.SmallGapY, SmallGapYSlider.Minimum, SmallGapYSlider.Maximum);
            LargeGapSlider.Value = Math.Clamp(section.Settings.LargeGap, LargeGapSlider.Minimum, LargeGapSlider.Maximum);
        }
        finally
        {
            _isUpdatingSectionControls = false;
        }

        UpdateSectionGapLabels();
    }

    private void ClearSections()
    {
        _candidateWorkspace.ClearSections();
        SectionCombo.SelectedItem = null;
        RefreshSectionLabels();
    }

    private void RemoveSectionsContaining(IReadOnlyCollection<SlotCandidate> candidates)
    {
        if (_candidateWorkspace.RemoveSectionsContaining(candidates))
        {
            SectionCombo.SelectedItem = null;
        }

        RefreshSectionLabels();
    }

    private void RefreshSectionLabels()
    {
        _candidateWorkspace.RefreshSectionMemberships();

        SectionCombo.Items.Refresh();
    }

    private void ClearCandidateRects()
    {
        foreach (var rect in _candidateRects.Values)
        {
            CaptureCanvas.Children.Remove(rect);
        }

        _candidateRects.Clear();
    }

    private bool TryHandleUndoRedo(KeyEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return false;
        }

        if (e.Key == Key.Z)
        {
            UndoCandidateEdit();
            e.Handled = true;
            return true;
        }

        if (e.Key == Key.Y)
        {
            RedoCandidateEdit();
            e.Handled = true;
            return true;
        }

        return false;
    }

    private CandidateEditSnapshot CaptureCandidateSnapshot()
    {
        var selectedId = CandidateList.SelectedItem is SlotCandidate selected ? selected.Id : 0;
        return _candidateWorkspace.CaptureSnapshot(selectedId);
    }

    private void PushUndoIfChanged(CandidateEditSnapshot before)
    {
        var selectedId = CandidateList.SelectedItem is SlotCandidate selected ? selected.Id : 0;
        if (!_candidateWorkspace.PushUndoIfChanged(before, selectedId))
        {
            return;
        }

        ScheduleProfileAutoSave();
    }

    private void UndoCandidateEdit()
    {
        var selectedId = CandidateList.SelectedItem is SlotCandidate selected ? selected.Id : 0;
        if (!_candidateWorkspace.TryUndo(selectedId, out var previous))
        {
            SetStatus("No candidate edit to undo.");
            return;
        }

        RestoreCandidateSnapshot(previous);
        ScheduleProfileAutoSave();
        SetStatus("Candidate edit undone.");
    }

    private void RedoCandidateEdit()
    {
        var selectedId = CandidateList.SelectedItem is SlotCandidate selected ? selected.Id : 0;
        if (!_candidateWorkspace.TryRedo(selectedId, out var next))
        {
            SetStatus("No candidate edit to redo.");
            return;
        }

        RestoreCandidateSnapshot(next);
        ScheduleProfileAutoSave();
        SetStatus("Candidate edit redone.");
    }

    private void RestoreCandidateSnapshot(CandidateEditSnapshot snapshot)
    {
        ClearCandidateRects();
        var restored = _candidateWorkspace.RestoreSnapshot(
            snapshot,
            kind => kind == OverlayElementKind.Quickslot || IsMonitorElementEnabled(kind));
        foreach (var candidate in _candidates)
        {
            AddCandidateVisual(candidate);
        }

        var restoredById = restored.CandidatesById.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (_buffMonitorEnabled)
        {
            var internalTimerCandidate = EnsureInternalTimerCandidate();
            restoredById[internalTimerCandidate.Id] = internalTimerCandidate;
        }
        if (_tuairimMonitorEnabled)
        {
            var tuairimGaugeCandidate = EnsureTuairimGaugeCandidate();
            restoredById[tuairimGaugeCandidate.Id] = tuairimGaugeCandidate;
        }
        if (_buffMonitorEnabled || _tuairimMonitorEnabled)
        {
            var alertCandidate = EnsureAlertNotificationCandidate();
            restoredById[alertCandidate.Id] = alertCandidate;
        }

        CandidateList.SelectedItem = restored.SelectedCandidate;
        if (restored.SelectedSection is not null)
        {
            SelectSection(restored.SelectedSection);
        }
        else
        {
            SectionCombo.SelectedItem = null;
        }

        foreach (var slot in _overlaySlots.ToList())
        {
            if (!restoredById.TryGetValue(slot.Source.Id, out var restoredSource))
            {
                _overlaySlots.Remove(slot);
                continue;
            }

            slot.Source = restoredSource;
            if (restoredSource.Kind != OverlayElementKind.Quickslot)
            {
                slot.Preview = RenderMonitorElementPreview(restoredSource.Kind);
            }
            else if (_capturedImage is not null)
            {
                slot.Preview = _captureSession.Crop(_capturedImage, restoredSource.SourceRect);
            }
        }

        UpdateLayoutSummary();
    }

    private int NextCandidateId() => _candidateWorkspace.NextCandidateId();
}
