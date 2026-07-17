using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using TestOverlay.App.Models;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class MainWindow
{
    private void BeginDetectionRoiSelection(Point position)
    {
        _isSelectingDetectionRoi = true;
        _selectionStartPosition = position;
        _selectionRect = new Rectangle
        {
            Stroke = CreateProjectAccentBrush(),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 6, 3 },
            Fill = CreateProjectAccentBrush(30),
            IsHitTestVisible = false
        };
        CaptureCanvas.Children.Add(_selectionRect);
        Canvas.SetLeft(_selectionRect, position.X);
        Canvas.SetTop(_selectionRect, position.Y);
        CaptureCanvas.CaptureMouse();
    }

    private void BeginDebugDetectionRoiSelection(Point position)
    {
        _isSelectingDebugDetectionRoi = true;
        _selectionStartPosition = position;
        _selectionRect = new Rectangle
        {
            Stroke = CreateProjectAccentBrush(),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 2 },
            Fill = CreateProjectAccentBrush(26),
            IsHitTestVisible = false
        };
        CaptureCanvas.Children.Add(_selectionRect);
        Canvas.SetLeft(_selectionRect, position.X);
        Canvas.SetTop(_selectionRect, position.Y);
        CaptureCanvas.CaptureMouse();
    }

    private void BeginMonitorDetectionRoiSelection(Point position)
    {
        _isSelectingMonitorDetectionRoi = true;
        _selectionStartPosition = position;
        _selectionRect = new Rectangle
        {
            Stroke = CreateProjectAccentBrush(),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 6, 3 },
            Fill = CreateProjectAccentBrush(30),
            IsHitTestVisible = false
        };
        CaptureCanvas.Children.Add(_selectionRect);
        Canvas.SetLeft(_selectionRect, position.X);
        Canvas.SetTop(_selectionRect, position.Y);
        CaptureCanvas.CaptureMouse();
    }

    private void UpdateDetectionRoiSelection(Point position) => UpdateSelectionRectangle(position);

    private void EndDetectionRoiSelection()
    {
        var roi = Rect.Empty;
        if (_selectionRect is not null)
        {
            roi = new Rect(
                Canvas.GetLeft(_selectionRect),
                Canvas.GetTop(_selectionRect),
                _selectionRect.Width,
                _selectionRect.Height);
            RemoveSelectionRectangle();
        }

        _isSelectingDetectionRoi = false;
        ReleaseCaptureSafely(CaptureCanvas);
        SetDetectionMode(active: false);

        if (roi.Width < 24 || roi.Height < 24)
        {
            SetStatus("Detection area is too small. Click Detect and drag a full quickslot section area.");
            return;
        }

        var patternKind = ResolveDetectionPatternKind(roi);
        _log.Info(
            $"ROI section detection auto-routed: pattern={patternKind}, roi={roi.X:0},{roi.Y:0},{roi.Width:0}x{roi.Height:0}");
        DetectSectionInRoi(roi, patternKind);
    }

    private void EndDebugDetectionRoiSelection()
    {
        var roi = Rect.Empty;
        if (_selectionRect is not null)
        {
            roi = new Rect(
                Canvas.GetLeft(_selectionRect),
                Canvas.GetTop(_selectionRect),
                _selectionRect.Width,
                _selectionRect.Height);
            RemoveSelectionRectangle();
        }

        _isSelectingDebugDetectionRoi = false;
        ReleaseCaptureSafely(CaptureCanvas);
        SetDebugDetectionMode(active: false);

        if (roi.Width < 24 || roi.Height < 24)
        {
            SetStatus("Debug detection area is too small.");
            return;
        }

        RunDebugDetection(roi, _debugDetectionExpectation);
    }

    private async void EndMonitorDetectionRoiSelection()
    {
        var mode = _monitorDetectionMode;
        var roi = Rect.Empty;
        if (_selectionRect is not null)
        {
            roi = new Rect(
                Canvas.GetLeft(_selectionRect),
                Canvas.GetTop(_selectionRect),
                _selectionRect.Width,
                _selectionRect.Height);
            RemoveSelectionRectangle();
        }

        _isSelectingMonitorDetectionRoi = false;
        ReleaseCaptureSafely(CaptureCanvas);
        SetMonitorDetectionMode(MonitorDetectionMode.None);
        var capturedImage = _capturedImage;
        if (capturedImage is null || roi.Width < 16 || roi.Height < 16)
        {
            SetStatus("monitor.detect.area.too.small");
            return;
        }

        _isMonitorDetectionBusy = true;
        UpdateMonitorControlAvailability();
        SetStatus("monitor.detect.processing");
        try
        {
            if (mode == MonitorDetectionMode.BuffWindow)
            {
                var result = await Task.Run(() => _monitorTemplateDetection.DetectBuffs(capturedImage, roi));
                if (!ReferenceEquals(_capturedImage, capturedImage))
                {
                    SetStatus("monitor.detect.capture.changed");
                    return;
                }
                _buffMonitorRoi = result.Roi;
                _buffIconMatches.Clear();
                foreach (var match in result.Matches)
                {
                    _buffIconMatches[match.NameKey] = match;
                }

                ApplyRecognizedBuffs(result.Matches.Select(match => match.NameKey));
                RefreshMonitorDetectionVisuals();
                foreach (var match in result.Matches)
                {
                    _log.Info(
                        $"Buff template match: key={match.NameKey}, bounds={FormatRect(match.Bounds)}, " +
                        $"structure={match.StructureScore:0.0000}, active={match.IsActive}, stateConfidence={match.StateConfidence:0.0000}");
                }

                SetStatus(result.Matches.Count == 0
                    ? "monitor.buff.detect.none"
                    : L.F("monitor.buff.detect.result", result.Matches.Count));
            }
            else if (mode == MonitorDetectionMode.Tuairim)
            {
                var result = await Task.Run(() => _monitorTemplateDetection.DetectTuairim(capturedImage, roi));
                if (!ReferenceEquals(_capturedImage, capturedImage))
                {
                    SetStatus("monitor.detect.capture.changed");
                    return;
                }
                _tuairimMonitorRoi = roi;
                _tuairimAnchor = result?.Bounds;
                ResetTuairimPercentRecognitionState();
                RefreshMonitorDetectionVisuals();
                if (result is null)
                {
                    SetMonitorElementEnabled(OverlayElementKind.TuairimGauge, enabled: false, scheduleAutoSave: false);
                    SetStatus("monitor.tuairim.detect.none");
                }
                else
                {
                    SetMonitorElementEnabled(OverlayElementKind.TuairimGauge, enabled: true, scheduleAutoSave: false);
                    _log.Info(
                        $"Tuairim template match: bounds={FormatRect(result.Bounds)}, score={result.Score:0.0000}, roi={FormatRect(result.Roi)}");
                    SetStatus(L.F("monitor.tuairim.detect.result", result.Score.ToString("0.000")));
                }

                ScheduleProfileAutoSave();
            }
        }
        catch (Exception exception)
        {
            _log.Error("Monitor template detection failed.", exception);
            SetStatus(L.F("monitor.detect.failed", exception.Message));
        }
        finally
        {
            _isMonitorDetectionBusy = false;
            UpdateMonitorControlAvailability();
        }
    }

    private void ShowMonitorDetectionBounds(IEnumerable<Rect> bounds)
    {
        ClearMonitorDetectionVisuals();
        foreach (var bound in bounds)
        {
            var rectangle = new Rectangle
            {
                Width = bound.Width,
                Height = bound.Height,
                Stroke = CreateProjectAccentBrush(),
                StrokeThickness = 2,
                Fill = CreateProjectAccentBrush(24),
                IsHitTestVisible = false
            };
            _monitorDetectionRects.Add(rectangle);
            CaptureCanvas.Children.Add(rectangle);
            Canvas.SetLeft(rectangle, bound.X);
            Canvas.SetTop(rectangle, bound.Y);
        }
    }

    private void RefreshMonitorDetectionVisuals()
    {
        if (_capturedImage is null)
        {
            ClearMonitorDetectionVisuals();
            return;
        }

        var bounds = _buffIconMatches.Values.Select(match => match.Bounds).ToList();
        if (_tuairimAnchor is Rect tuairimBounds)
        {
            bounds.Add(tuairimBounds);
        }
        ShowMonitorDetectionBounds(bounds);
    }

    private void ClearMonitorDetectionVisuals()
    {
        foreach (var rectangle in _monitorDetectionRects)
        {
            CaptureCanvas.Children.Remove(rectangle);
        }
        _monitorDetectionRects.Clear();
    }

    private static string FormatRect(Rect rect) =>
        $"{rect.X:0},{rect.Y:0},{rect.Width:0}x{rect.Height:0}";

    private static OverlayProfileRect? ToProfileRect(Rect? rect) => rect is null
        ? null
        : new OverlayProfileRect
        {
            X = rect.Value.X,
            Y = rect.Value.Y,
            Width = rect.Value.Width,
            Height = rect.Value.Height
        };

    private static Rect? FromProfileRect(OverlayProfileRect? rect) =>
        rect is null || rect.Width <= 0 || rect.Height <= 0
            ? null
            : new Rect(rect.X, rect.Y, rect.Width, rect.Height);

    private void SetDetectionMode(bool active)
    {
        if (active)
        {
            SetDebugDetectionMode(active: false);
            SetMonitorDetectionMode(MonitorDetectionMode.None);
        }

        _isAwaitingDetectionRoi = active;
        if (DetectModeText is not null)
        {
            DetectModeText.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        }

        if (DetectButton is not null)
        {
            DetectButton.Content = active ? L.T("Drag ROI...") : L.T("Auto detect section");
            ApplyDetectModeButtonStyle(DetectButton, active);
        }
    }

    private void SetDebugDetectionMode(bool active)
    {
        if (active)
        {
            SetDetectionMode(active: false);
            SetMonitorDetectionMode(MonitorDetectionMode.None);
        }

        _isAwaitingDebugDetectionRoi = active;
        if (DebugDetectButton is not null)
        {
            DebugDetectButton.Content = active ? L.T("Drag debug ROI...") : L.T("Debug detect");
            ApplyDetectModeButtonStyle(DebugDetectButton, active);
        }
    }

    private void SetMonitorDetectionMode(MonitorDetectionMode mode)
    {
        if (mode != MonitorDetectionMode.None)
        {
            SetDetectionMode(active: false);
            SetDebugDetectionMode(active: false);
        }

        _monitorDetectionMode = mode;
        UpdateMonitorDetectionButtonPresentation();
        UpdateMonitorControlAvailability();
    }

    private void UpdateMonitorDetectionButtonPresentation()
    {
        if (DetectBuffWindowButton is not null)
        {
            var active = _monitorDetectionMode == MonitorDetectionMode.BuffWindow;
            DetectBuffWindowButton.Content = active ? L.T("Drag ROI...") : L.T("monitor.buff.detect");
            ApplyDetectModeButtonStyle(DetectBuffWindowButton, active);
        }

        if (DetectTuairimUiButton is not null)
        {
            var active = _monitorDetectionMode == MonitorDetectionMode.Tuairim;
            DetectTuairimUiButton.Content = active ? L.T("Drag ROI...") : L.T("monitor.tuairim.detect");
            ApplyDetectModeButtonStyle(DetectTuairimUiButton, active);
        }
    }

    private static SolidColorBrush CreateProjectAccentBrush(byte alpha = 255) =>
        new(Color.FromArgb(alpha, ProjectAccentColor.R, ProjectAccentColor.G, ProjectAccentColor.B));

    private static void ApplyDetectModeButtonStyle(Button button, bool active)
    {
        if (!active)
        {
            button.ClearValue(Control.BackgroundProperty);
            button.ClearValue(Control.BorderBrushProperty);
            return;
        }

        button.Background = CreateProjectAccentBrush(52);
        button.BorderBrush = CreateProjectAccentBrush();
    }

    private static QuickslotSectionPatternKind ResolveDetectionPatternKind(Rect roi) =>
        roi.Width >= roi.Height
            ? QuickslotSectionPatternKind.TopGrouped
            : QuickslotSectionPatternKind.Vertical;

    private DebugDetectionExpectation? ChooseDebugDetectionExpectation()
    {
        var sectionResult = MessageBox.Show(
            this,
            $"{L.T("Choose the debug detect target.")}\n\n{L.T("Yes: Top grouped 4x2 x3")}\n{L.T("No: Vertical 2x8")}\n{L.T("Cancel: cancel debug detection")}",
            L.T("Debug Detect Target"),
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (sectionResult == MessageBoxResult.No)
        {
            return DebugDetectionExpectation.Vertical();
        }

        if (sectionResult != MessageBoxResult.Yes)
        {
            return null;
        }

        var topResult = MessageBox.Show(
            this,
            $"{L.T("Choose the top grouped debug target.")}\n\nYes: Top grouped 1 (x=7, y=18)\nNo: Top grouped 2 (x=627, y=18)\n{L.T("Cancel: cancel debug detection")}",
            L.T("Top Grouped Debug Target"),
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        return topResult switch
        {
            MessageBoxResult.Yes => DebugDetectionExpectation.TopGrouped1(),
            MessageBoxResult.No => DebugDetectionExpectation.TopGrouped2(),
            _ => null
        };
    }

    private void DetectSectionInRoi(Rect roi, QuickslotSectionPatternKind patternKind)
    {
        if (_capturedImage is null)
        {
            SetStatus("No captured image is available.");
            return;
        }

        var before = CaptureCandidateSnapshot();
        var diagnostics = new List<string>();
        var result = _roiSectionDetection.Detect(
            _capturedImage,
            roi,
            patternKind,
            diagnostics);
        var logPath = SaveDetectLog(roi, patternKind, result, diagnostics);

        if (result is null)
        {
            _log.Info($"ROI section detection failed. Log: {logPath}");
            SetStatus(L.F("No matching quickslot section pattern was found in the selected area. Log: {0}", logPath));
            return;
        }

        AddDetectedSection(result);
        PushUndoIfChanged(before);

        _log.Info(
            $"ROI section detection completed: pattern={patternKind}, roi={roi.X:0},{roi.Y:0},{roi.Width:0}x{roi.Height:0}, " +
            $"slots={result.Slots.Count}, gapX={result.SmallGapX:0}, gapY={result.SmallGapY:0}, largeGap={result.LargeGap:0}, score={result.Score:0.00}, log={logPath}");
        SetStatus(L.F(
            "Added {0}: {1} slots, slot {2}x{3}px, gap X {4}px, gap Y {5}px, large gap {6}px. Log: {7}",
            L.T(GetSectionPatternName(PatternIndexFromKind(patternKind))),
            result.Slots.Count,
            result.Slots[0].Width.ToString("0"),
            result.Slots[0].Height.ToString("0"),
            result.SmallGapX.ToString("0"),
            result.SmallGapY.ToString("0"),
            result.LargeGap.ToString("0"),
            logPath));
    }

    private void RunDebugDetection(Rect roi, DebugDetectionExpectation expected)
    {
        if (_capturedImage is null)
        {
            SetStatus("No captured image is available.");
            return;
        }

        var expectedGapText = expected.LargeGap is int expectedLargeGap
            ? $"gapX={expected.SmallGapX}, gapY={expected.SmallGapY}, large={expectedLargeGap}"
            : $"gapX={expected.SmallGapX}, gapY={expected.SmallGapY}";

        var lines = new List<string>
        {
            $"Debug {expected.Label} ROI detect started {DateTimeOffset.Now:O}",
            $"roi absolute x={roi.X:0.###}, y={roi.Y:0.###}, w={roi.Width:0.###}, h={roi.Height:0.###}",
            $"expected absolute x={expected.AbsoluteX}, y={expected.AbsoluteY}, {expected.Size}x{expected.Size}, {expectedGapText}",
            $"runs={DebugDetectRuns}"
        };

        var exactMatches = 0;
        var detections = 0;
        for (var run = 1; run <= DebugDetectRuns; run++)
        {
            var diagnostics = new List<string>();
            var result = _roiSectionDetection.Detect(
                _capturedImage,
                roi,
                expected.PatternKind,
                diagnostics);

            if (result is null)
            {
                lines.Add($"RUN {run:000}: FAIL no result");
                lines.AddRange(diagnostics.Select(line => $"  {line}"));
                continue;
            }

            detections++;
            var first = result.Slots[0];
            var absoluteX = (int)Math.Round(first.X);
            var absoluteY = (int)Math.Round(first.Y);
            var relativeX = (int)Math.Round(first.X - roi.X);
            var relativeY = (int)Math.Round(first.Y - roi.Y);
            var width = (int)Math.Round(first.Width);
            var height = (int)Math.Round(first.Height);
            var gapX = (int)Math.Round(result.SmallGapX);
            var gapY = (int)Math.Round(result.SmallGapY);
            var largeGap = (int)Math.Round(result.LargeGap);
            var isExact =
                absoluteX == expected.AbsoluteX &&
                absoluteY == expected.AbsoluteY &&
                width == expected.Size &&
                height == expected.Size &&
                gapX == expected.SmallGapX &&
                gapY == expected.SmallGapY &&
                (expected.LargeGap is null || largeGap == expected.LargeGap.Value);

            if (isExact)
            {
                exactMatches++;
                continue;
            }

            lines.Add(
                $"RUN {run:000}: WRONG abs x={absoluteX}, y={absoluteY}, rel x={relativeX}, y={relativeY}, {width}x{height}, gapX={gapX}, gapY={gapY}, large={largeGap}, score={result.Score:0.000}");
            lines.Add(
                $"  abs x={first.X:0.###}, y={first.Y:0.###}, w={first.Width:0.###}, h={first.Height:0.###}");
            lines.AddRange(diagnostics.Select(line => $"  {line}"));
        }

        lines.Insert(4, $"summary exact={exactMatches}/{DebugDetectRuns}, detected={detections}/{DebugDetectRuns}, failed={DebugDetectRuns - detections}");
        var logPath = SaveDebugDetectLog(lines);
        _log.Info($"Debug detect log saved: {logPath}");
        SetStatus(L.F(
            "Debug detect finished: exact {0}/{1}, detected {2}/{3}. Log: {4}",
            exactMatches,
            DebugDetectRuns,
            detections,
            DebugDetectRuns,
            logPath));
    }

    private sealed record DebugDetectionExpectation(
        string Label,
        QuickslotSectionPatternKind PatternKind,
        int AbsoluteX,
        int AbsoluteY,
        int Size,
        int SmallGapX,
        int SmallGapY,
        int? LargeGap)
    {
        public static DebugDetectionExpectation TopGrouped1() =>
            new("top grouped 1 4x2 x3", QuickslotSectionPatternKind.TopGrouped, 7, 18, 29, 3, 8, 15);

        public static DebugDetectionExpectation TopGrouped2() =>
            new("top grouped 2 4x2 x3", QuickslotSectionPatternKind.TopGrouped, 627, 18, 29, 3, 8, 15);

        public static DebugDetectionExpectation Vertical() =>
            new("vertical 2x8", QuickslotSectionPatternKind.Vertical, 91, 417, 29, 3, 3, null);
    }

    private string SaveDebugDetectLog(IReadOnlyCollection<string> lines)
    {
        var path = System.IO.Path.Combine(
            _log.LogDirectory,
            $"detect-debug-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log");
        System.IO.Directory.CreateDirectory(_log.LogDirectory);
        System.IO.File.WriteAllLines(path, lines);
        return path;
    }

    private string SaveDetectLog(
        Rect roi,
        QuickslotSectionPatternKind patternKind,
        SectionDetectionResult? result,
        IReadOnlyCollection<string> diagnostics)
    {
        var lines = new List<string>
        {
            $"Detect ROI started {DateTimeOffset.Now:O}",
            $"pattern={patternKind}",
            $"roi absolute x={roi.X:0.###}, y={roi.Y:0.###}, w={roi.Width:0.###}, h={roi.Height:0.###}"
        };

        if (_capturedImage is not null)
        {
            lines.Add($"capture image {_capturedImage.PixelWidth}x{_capturedImage.PixelHeight}");
        }

        if (result is null)
        {
            lines.Add("result=FAIL no matching quickslot section pattern");
        }
        else
        {
            var first = result.Slots[0];
            lines.Add(
                $"result=OK slots={result.Slots.Count}, first x={first.X:0.###}, y={first.Y:0.###}, w={first.Width:0.###}, h={first.Height:0.###}, " +
                $"gapX={result.SmallGapX:0.###}, gapY={result.SmallGapY:0.###}, large={result.LargeGap:0.###}, score={result.Score:0.000}");
        }

        lines.AddRange(diagnostics.Select(line => $"  {line}"));
        lines.Add(string.Empty);

        lock (_detectLogSync)
        {
            System.IO.Directory.CreateDirectory(_log.LogDirectory);
            var isNewLog = !System.IO.File.Exists(_detectSessionLogPath);
            var output = new List<string>();
            if (isNewLog)
            {
                output.Add($"Detect session log started {DateTimeOffset.Now:O}");
                output.Add(string.Empty);
            }

            output.Add("----");
            output.AddRange(lines);
            System.IO.File.AppendAllLines(_detectSessionLogPath, output);
        }

        return _detectSessionLogPath;
    }
}
