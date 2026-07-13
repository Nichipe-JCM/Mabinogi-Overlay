using System.Diagnostics;
using System.IO;
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
    private void CaptureCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(CaptureCanvas);
        if (_monitorDetectionMode != MonitorDetectionMode.None)
        {
            BeginMonitorDetectionRoiSelection(position);
            e.Handled = true;
            return;
        }

        if (_isAwaitingDebugDetectionRoi)
        {
            BeginDebugDetectionRoiSelection(position);
            e.Handled = true;
            return;
        }

        if (_isAwaitingDetectionRoi)
        {
            BeginDetectionRoiSelection(position);
            e.Handled = true;
            return;
        }

        var hit = _candidates.FirstOrDefault(candidate => GetCandidateVisualRect(candidate).Contains(position));
        if (hit is null)
        {
            BeginCandidateBoxSelection(position);
        }
    }

    private void CaptureCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(CaptureCanvas);
        if (_isSelectingMonitorDetectionRoi)
        {
            UpdateDetectionRoiSelection(position);
            return;
        }

        if (_isSelectingDebugDetectionRoi)
        {
            UpdateDetectionRoiSelection(position);
            return;
        }

        if (_isSelectingDetectionRoi)
        {
            UpdateDetectionRoiSelection(position);
            return;
        }

        if (_isSelectingCandidates)
        {
            UpdateCandidateBoxSelection(position);
            return;
        }

        if (_draggingCandidate is null)
        {
            return;
        }

        if (Math.Abs(position.X - _candidateDragStartPosition.X) > 3 ||
            Math.Abs(position.Y - _candidateDragStartPosition.Y) > 3)
        {
            MoveSelectedCandidates(position);
        }
    }

    private void CaptureCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isSelectingMonitorDetectionRoi)
        {
            EndMonitorDetectionRoiSelection();
            return;
        }

        if (_isSelectingDebugDetectionRoi)
        {
            EndDebugDetectionRoiSelection();
            return;
        }

        if (_isSelectingDetectionRoi)
        {
            EndDetectionRoiSelection();
            return;
        }

        if (_isSelectingCandidates)
        {
            EndCandidateBoxSelection();
            return;
        }

        if (_draggingCandidate is not null && _candidateRects.TryGetValue(_draggingCandidate, out var rect))
        {
            ReleaseCaptureSafely(rect);
        }

        if (_candidateDragSnapshotBefore is not null)
        {
            PushUndoIfChanged(_candidateDragSnapshotBefore);
        }

        _draggingCandidate = null;
        _candidateDragOrigins.Clear();
        _candidateDragSnapshotBefore = null;
    }

    private void CaptureCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (CancelCapturePreviewInteraction(cancelAwaitingModes: true, restoreDragSnapshot: true))
        {
            e.Handled = true;
        }
    }

    private void RefreshWindows()
    {
        var windows = _captureSession.GetVisibleWindows();
        WindowCombo.ItemsSource = windows;
        WindowCombo.SelectedItem = windows.FirstOrDefault(window => window.LooksLikeMabinogi) ?? windows.FirstOrDefault();
        SetWindowStatusText(BuildWindowStatusText());
    }

    private void SetWindowStatusText(string text)
    {
        WindowStatusText.Text = text;
        WindowStatusText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private string BuildWindowStatusText()
    {
        var captureStatus = CurrentCaptureBackend switch
        {
            CaptureBackend.Wgc => _captureSession.IsWgcSupported ? "WGC supported" : "WGC unavailable",
            CaptureBackend.DxgiDesktopDuplication => "DXGI uses selected window monitor",
            CaptureBackend.GdiBitBlt => "GDI captures selected client area",
            _ => "Capture backend unknown"
        };
        return WindowCombo.SelectedItem is GameWindowInfo selected
            ? selected.LooksLikeMabinogi
                ? string.Empty
                : $"Selected window is not recognized as Mabinogi. ({captureStatus})"
            : "No selectable window found.";
    }

    private void AddCandidateVisual(SlotCandidate candidate)
    {
        var rect = new Rectangle
        {
            Width = GetCandidateVisualRect(candidate).Width,
            Height = GetCandidateVisualRect(candidate).Height,
            StrokeThickness = 1,
            Fill = new SolidColorBrush(Color.FromArgb(45, 30, 144, 255)),
            IsHitTestVisible = true,
            Cursor = Cursors.SizeAll
        };
        rect.MouseLeftButtonDown += (_, args) =>
        {
            BeginCandidateDrag(candidate, rect, args);
            SelectCandidateInList(candidate);
            args.Handled = true;
        };
        rect.LostMouseCapture += (_, _) => CancelInterruptedCaptureInteraction();
        _candidateRects[candidate] = rect;
        CaptureCanvas.Children.Add(rect);
        var visualRect = GetCandidateVisualRect(candidate);
        Canvas.SetLeft(rect, visualRect.X);
        Canvas.SetTop(rect, visualRect.Y);
        UpdateCandidateVisual(candidate);
    }

    private void BeginCandidateDrag(SlotCandidate candidate, Rectangle rect, MouseButtonEventArgs args)
    {
        var isMultiSelectModifierPressed =
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ||
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (isMultiSelectModifierPressed)
        {
            candidate.IsSelected = !candidate.IsSelected;
            if (!candidate.IsSelected)
            {
                _draggingCandidate = null;
                return;
            }
        }
        else if (!candidate.IsSelected)
        {
            SetOnlyCandidateSelected(candidate);
        }

        _draggingCandidate = candidate;
        _candidateDragStartPosition = args.GetPosition(CaptureCanvas);
        _candidateDragSnapshotBefore = CaptureCandidateSnapshot();
        _candidateDragOrigins = _candidates
            .Where(item => item.IsSelected)
            .ToDictionary(item => item, item => new Point(item.SourceRect.X, item.SourceRect.Y));
        rect.CaptureMouse();
    }

    private void CancelInterruptedCaptureInteraction()
    {
        if (_isReleasingCaptureIntentionally)
        {
            return;
        }

        CancelCapturePreviewInteraction(cancelAwaitingModes: false, restoreDragSnapshot: false);
    }

    private bool CancelCapturePreviewInteraction(bool cancelAwaitingModes, bool restoreDragSnapshot)
    {
        var hadDetection = _isSelectingDetectionRoi || (cancelAwaitingModes && _isAwaitingDetectionRoi);
        var hadDebugDetection = _isSelectingDebugDetectionRoi || (cancelAwaitingModes && _isAwaitingDebugDetectionRoi);
        var hadMonitorDetection = _isSelectingMonitorDetectionRoi ||
                                  cancelAwaitingModes && _monitorDetectionMode != MonitorDetectionMode.None;
        var hadSelection = _selectionRect is not null ||
                           _isSelectingCandidates ||
                           _isSelectingDetectionRoi ||
                           _isSelectingDebugDetectionRoi ||
                           _isSelectingMonitorDetectionRoi;
        var hadDrag = _draggingCandidate is not null;
        if (!hadSelection && !hadDrag && !hadDetection && !hadDebugDetection && !hadMonitorDetection)
        {
            return false;
        }

        var dragSnapshot = restoreDragSnapshot ? _candidateDragSnapshotBefore : null;

        RemoveSelectionRectangle();
        if (hadDetection)
        {
            _isSelectingDetectionRoi = false;
            SetDetectionMode(active: false);
        }

        if (hadDebugDetection)
        {
            _isSelectingDebugDetectionRoi = false;
            SetDebugDetectionMode(active: false);
        }

        if (hadMonitorDetection)
        {
            _isSelectingMonitorDetectionRoi = false;
            SetMonitorDetectionMode(MonitorDetectionMode.None);
        }

        _isSelectingCandidates = false;
        if (_draggingCandidate is not null && _candidateRects.TryGetValue(_draggingCandidate, out var draggingRect))
        {
            ReleaseCaptureSafely(draggingRect);
        }

        if (dragSnapshot is not null)
        {
            RestoreCandidateSnapshot(dragSnapshot);
        }
        else if (_candidateDragSnapshotBefore is not null)
        {
            PushUndoIfChanged(_candidateDragSnapshotBefore);
        }

        _draggingCandidate = null;
        _candidateDragOrigins.Clear();
        _candidateDragSnapshotBefore = null;
        ReleaseCaptureSafely(CaptureCanvas);
        SetStatus(hadMonitorDetection
            ? "monitor.detect.canceled"
            : hadDebugDetection
            ? "Debug detect canceled."
            : hadDetection
                ? "Section detection canceled."
                : "Interrupted preview drag was canceled.");
        return true;
    }

    private void RemoveSelectionRectangle()
    {
        if (_selectionRect is null)
        {
            return;
        }

        CaptureCanvas.Children.Remove(_selectionRect);
        _selectionRect = null;
    }

    private void ReleaseCaptureSafely(UIElement element)
    {
        try
        {
            _isReleasingCaptureIntentionally = true;
            if (element.IsMouseCaptured)
            {
                element.ReleaseMouseCapture();
            }
        }
        finally
        {
            _isReleasingCaptureIntentionally = false;
        }
    }
}
