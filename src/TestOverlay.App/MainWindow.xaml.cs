using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using TestOverlay.App.Models;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class MainWindow : Window
{
    private const int CandidateBorderPixels = 1;
    private static readonly int[] RefreshFpsOptions = [30, 60, 120, 144];

    private readonly WindowDiscoveryService _windowDiscovery = new();
    private readonly WindowCaptureService _captureService = new();
    private readonly WgcCaptureService _wgcCaptureService = new();
    private readonly SlotDetectionService _slotDetection = new();
    private readonly WgcSupportService _wgcSupport = new();
    private readonly WgcWindowSelectionService _wgcWindowSelection = new();
    private readonly ProfileStore _profileStore = new();
    private readonly AppLog _log = new();
    private readonly DispatcherTimer _liveOverlayTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly ObservableCollection<SlotCandidate> _candidates = new();
    private readonly ObservableCollection<QuickslotSection> _sections = new();
    private readonly List<OverlaySlot> _overlaySlots = new();
    private readonly Dictionary<SlotCandidate, Rectangle> _candidateRects = new();
    private readonly SectionSettings[] _sectionSettings =
    [
        new(1, 5, 16),
        new(1, 5, 0)
    ];
    private readonly Stack<CandidateEditSnapshot> _undoStack = new();
    private readonly Stack<CandidateEditSnapshot> _redoStack = new();
    private HotkeyService? _hotkeyService;
    private BitmapSource? _capturedImage;
    private GameWindowInfo? _selectedWindow;
    private WgcSelectionResult? _wgcSelection;
    private OverlayWindow? _overlayWindow;
    private SlotCandidate? _draggingCandidate;
    private Point _candidateDragStartPosition;
    private Dictionary<SlotCandidate, Point> _candidateDragOrigins = new();
    private Rectangle? _selectionRect;
    private Point _selectionStartPosition;
    private bool _isSelectingCandidates;
    private CandidateEditSnapshot? _candidateDragSnapshotBefore;
    private QuickslotSection? _selectedSection;
    private bool _isLiveRefreshInProgress;
    private bool _isUpdatingSectionControls;
    private bool _isUpdatingSectionSelection;
    private int _currentSectionIndex;
    private int _nextSectionId = 1;
    private double _layoutCanvasWidth = 360;
    private double _layoutCanvasHeight = 160;
    private double _overlayLeft = 120;
    private double _overlayTop = 120;
    private double _overlayOpacity = 0.8;
    private string _stopHotkey = "Ctrl+Shift+F8";
    private int _refreshFps = 60;
    private double _layoutSlotScale = 1.5;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new { Candidates = _candidates };
        SectionCombo.ItemsSource = _sections;
        SectionPatternCombo.SelectionChanged += (_, _) =>
        {
            if (_isUpdatingSectionControls)
            {
                return;
            }

            SaveCurrentSectionSettings();
            _currentSectionIndex = Math.Clamp(SectionPatternCombo.SelectedIndex, 0, _sectionSettings.Length - 1);
            UpdateSectionGapLabels();
        };
        SmallGapXSlider.ValueChanged += (_, _) =>
        {
            SaveCurrentSectionSettings();
            UpdateSectionGapLabels();
            RebuildSelectedSection();
        };
        SmallGapYSlider.ValueChanged += (_, _) =>
        {
            SaveCurrentSectionSettings();
            UpdateSectionGapLabels();
            RebuildSelectedSection();
        };
        LargeGapSlider.ValueChanged += (_, _) =>
        {
            SaveCurrentSectionSettings();
            UpdateSectionGapLabels();
            RebuildSelectedSection();
        };
        _liveOverlayTimer.Tick += LiveOverlayTimer_Tick;
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => StopOverlay(setStatus: false);
        ApplySectionSettingsToControls(_currentSectionIndex);
        UpdateSizeLabels();
        CaptureZoomText.Text = "100%";
        UpdateSectionGapLabels();
        UpdateLayoutSummary();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshWindows();
        RefreshProfileList();
        _log.Info("Application loaded.");
    }

    private void CaptureZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CaptureZoomText is not null)
        {
            CaptureZoomText.Text = $"{CaptureZoomSlider.Value * 100:0}%";
        }
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e) =>
        CaptureZoomSlider.Value = Math.Min(CaptureZoomSlider.Maximum, CaptureZoomSlider.Value + 0.25);

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) =>
        CaptureZoomSlider.Value = Math.Max(CaptureZoomSlider.Minimum, CaptureZoomSlider.Value - 0.25);

    private void SlotSizeBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSizeLabels();

    private void PreviewScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        var step = e.Delta > 0 ? 0.25 : -0.25;
        CaptureZoomSlider.Value = Math.Clamp(CaptureZoomSlider.Value + step, CaptureZoomSlider.Minimum, CaptureZoomSlider.Maximum);
        e.Handled = true;
    }

    private void RefreshWindowsButton_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private async void VerifyWgcButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await _wgcWindowSelection.PickWindowAsync(this);
            if (result is null)
            {
                SetStatus("WGC window selection was canceled or WGC is not supported.");
                _log.Info("WGC window selection returned null.");
                return;
            }

            _wgcSelection = result;
            _log.Info($"WGC selection: {result.DisplayName}, {result.Width}x{result.Height}, looksLikeMabinogi={result.LooksLikeMabinogi}");
            SetStatus(result.LooksLikeMabinogi
                ? $"WGC verified Mabinogi window: {result.DisplayName} ({result.Width}x{result.Height})"
                : $"WGC selected window is not recognized as Mabinogi: {result.DisplayName} ({result.Width}x{result.Height})");
        }
        catch (Exception ex)
        {
            _log.Error("WGC verification failed.", ex);
            SetStatus($"WGC verification failed: {ex.Message}");
        }
    }

    private async void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_wgcSelection is null)
        {
            SetStatus("Verify the Mabinogi window with WGC before capture.");
            return;
        }

        if (!_wgcSelection.LooksLikeMabinogi)
        {
            SetStatus("WGC verification selected a non-Mabinogi window. Verify WGC again or refresh the window list.");
            return;
        }

        try
        {
            CaptureButton.IsEnabled = false;
            _capturedImage = await _wgcCaptureService.CaptureOnceAsync(_wgcSelection.Item, TimeSpan.FromSeconds(3));
            _selectedWindow = WindowCombo.SelectedItem as GameWindowInfo;
            _log.Info($"WGC capture succeeded: {_capturedImage.PixelWidth}x{_capturedImage.PixelHeight}, item={_wgcSelection.DisplayName}");
            CaptureImage.Source = _capturedImage;
            CaptureCanvas.Width = _capturedImage.PixelWidth;
            CaptureCanvas.Height = _capturedImage.PixelHeight;
            CaptureInfoText.Text = $"{_capturedImage.PixelWidth}x{_capturedImage.PixelHeight}";
            _candidates.Clear();
            ClearCandidateRects();
            ClearSections();
            SetStatus("Captured the verified Mabinogi window with WGC. Run slot detection next.");
        }
        catch (Exception ex)
        {
            _log.Error("Capture failed.", ex);
            SetStatus($"Capture failed: {ex.Message}");
        }
        finally
        {
            CaptureButton.IsEnabled = true;
        }
    }

    private void DetectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturedImage is null)
        {
            SetStatus("No captured image is available.");
            return;
        }

        var before = CaptureCandidateSnapshot();
        _candidates.Clear();
        ClearCandidateRects();
        ClearSections();

        var slotWidth = ReadSlotInnerWidth();
        var slotHeight = ReadSlotInnerHeight();
        var detectionSize = Math.Min(slotWidth, slotHeight);
        foreach (var detected in _slotDetection.Detect(_capturedImage, detectionSize, detectionSize))
        {
            var candidate = new SlotCandidate(
                detected.Id,
                new Rect(detected.SourceRect.X, detected.SourceRect.Y, slotWidth, slotHeight),
                detected.Score);
            AddCandidate(candidate);
        }

        PushUndoIfChanged(before);
        _log.Info($"Slot detection completed: count={_candidates.Count}, innerSlotSize={slotWidth}x{slotHeight}, visualBoxSize={ReadCandidateBoxWidth()}x{ReadCandidateBoxHeight()}");
        SetStatus($"Detected {_candidates.Count} slot candidates. Check only the slots to use, then place them.");
    }

    private void AddSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturedImage is null)
        {
            SetStatus("Capture and detect candidates first.");
            return;
        }

        _overlaySlots.Clear();

        var selected = _candidates.Where(candidate => candidate.IsSelected).ToList();
        if (selected.Count == 0)
        {
            SetStatus("No checked candidates. Check slots to place first.");
            return;
        }

        var cursorX = 8.0;
        var cursorY = 8.0;
        var rowHeight = 0.0;
        foreach (var candidate in selected)
        {
            var crop = _captureService.Crop(_capturedImage, candidate.SourceRect);
            var width = Math.Max(16, candidate.SourceRect.Width * ReadLayoutSlotScale());
            var height = Math.Max(16, candidate.SourceRect.Height * ReadLayoutSlotScale());
            if (cursorX + width > _layoutCanvasWidth - 8)
            {
                cursorX = 8;
                cursorY += rowHeight + 8;
                rowHeight = 0;
            }

            var rect = new Rect(cursorX, cursorY, width, height);
            var slot = new OverlaySlot(candidate, rect, crop);
            _overlaySlots.Add(slot);
            cursorX += width + 8;
            rowHeight = Math.Max(rowHeight, height);
        }

        var requiredHeight = cursorY + rowHeight + 8;
        if (requiredHeight > _layoutCanvasHeight)
        {
            _layoutCanvasHeight = requiredHeight;
        }

        UpdateLayoutSummary();
        _log.Info($"Placed overlay slots: count={_overlaySlots.Count}, canvas={_layoutCanvasWidth}x{_layoutCanvasHeight}");
        SetStatus($"Placed {_overlaySlots.Count} slots. Open Manage Layout to arrange them.");
    }

    private void SelectAllCandidatesButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var candidate in _candidates)
        {
            candidate.IsSelected = true;
        }

        SetStatus($"Selected all {_candidates.Count} candidates.");
    }

    private void DeselectAllCandidatesButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var candidate in _candidates)
        {
            candidate.IsSelected = false;
        }

        SetStatus($"Deselected all {_candidates.Count} candidates.");
    }

    private void AddManualCandidateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturedImage is null)
        {
            SetStatus("Capture the game window before adding a manual candidate.");
            return;
        }

        var before = CaptureCandidateSnapshot();
        var width = ReadSlotInnerWidth();
        var height = ReadSlotInnerHeight();
        var source = CandidateList.SelectedItem is SlotCandidate selected
            ? new Rect(
                Math.Clamp(selected.SourceRect.X + selected.SourceRect.Width + 4, CandidateBorderPixels, Math.Max(CandidateBorderPixels, CaptureCanvas.Width - width - CandidateBorderPixels)),
                Math.Clamp(selected.SourceRect.Y, CandidateBorderPixels, Math.Max(CandidateBorderPixels, CaptureCanvas.Height - height - CandidateBorderPixels)),
                width,
                height)
            : new Rect(8 + CandidateBorderPixels, 8 + CandidateBorderPixels, width, height);

        var candidate = new SlotCandidate(NextCandidateId(), source, 100);
        AddCandidate(candidate);
        CandidateList.SelectedItem = candidate;
        PushUndoIfChanged(before);
        SetStatus("Manual candidate added. Drag it over the target quickslot.");
    }

    private void ApplySlotSizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturedImage is null)
        {
            SetStatus("Capture the game window before resizing a candidate.");
            return;
        }

        var selected = _candidates.Where(candidate => candidate.IsSelected).ToList();
        if (selected.Count == 0 && CandidateList.SelectedItem is SlotCandidate highlighted)
        {
            selected.Add(highlighted);
        }

        selected = selected.Distinct().ToList();
        if (selected.Count != 1)
        {
            SetStatus("Select exactly one candidate before applying slot size.");
            return;
        }

        var candidate = selected[0];
        var width = ReadSlotInnerWidth();
        var height = ReadSlotInnerHeight();
        var resizedRect = new Rect(candidate.SourceRect.X, candidate.SourceRect.Y, width, height);
        if (!IsRectInsideCapture(resizedRect))
        {
            SetStatus("The resized candidate would exceed the captured image bounds.");
            return;
        }

        var before = CaptureCandidateSnapshot();
        var oldWidth = Math.Max(1, candidate.SourceRect.Width);
        var oldHeight = Math.Max(1, candidate.SourceRect.Height);
        candidate.ResizeTo(width, height);
        UpdateCandidateVisualPosition(candidate);

        foreach (var slot in _overlaySlots.Where(slot => ReferenceEquals(slot.Source, candidate)))
        {
            slot.Preview = _captureService.Crop(_capturedImage, candidate.SourceRect);
            var overlayWidth = Math.Max(16, slot.OverlayRect.Width * width / oldWidth);
            var overlayHeight = Math.Max(16, slot.OverlayRect.Height * height / oldHeight);
            slot.OverlayRect = new Rect(slot.OverlayRect.X, slot.OverlayRect.Y, overlayWidth, overlayHeight);
        }

        PushUndoIfChanged(before);
        SetStatus($"Applied candidate size {width}x{height} to #{candidate.Id:000}.");
    }

    private void AddSectionFromSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturedImage is null)
        {
            SetStatus("Capture the game window before adding a quickslot section.");
            return;
        }

        var seed = CandidateList.SelectedItem as SlotCandidate ??
                   _candidates.FirstOrDefault(candidate => candidate.IsSelected);
        if (seed is null)
        {
            SetStatus("Select or add the top-left slot of the section first.");
            return;
        }

        var before = CaptureCandidateSnapshot();
        var pattern = ReadSectionPattern();
        var settings = new SectionSettings(ReadSmallGapX(), ReadSmallGapY(), ReadLargeGap());
        var added = AddSectionCandidates(seed, pattern, Math.Clamp(SectionPatternCombo.SelectedIndex, 0, _sectionSettings.Length - 1), settings);
        PushUndoIfChanged(before);
        _log.Info($"Quickslot section generated: pattern={pattern.Name}, seed={seed.Id}, added={added}");
        SetStatus($"Generated {pattern.Name} from #{seed.Id:000}. Adjust small/large gap sliders to align the section.");
    }

    private void DeleteSectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSection is null)
        {
            SetStatus("No section is selected.");
            return;
        }

        var before = CaptureCandidateSnapshot();
        var candidates = _selectedSection.Candidates.Where(candidate => _candidates.Contains(candidate)).ToList();
        foreach (var candidate in candidates)
        {
            if (_candidateRects.Remove(candidate, out var rect))
            {
                CaptureCanvas.Children.Remove(rect);
            }

            _candidates.Remove(candidate);
        }

        _overlaySlots.RemoveAll(slot => candidates.Contains(slot.Source));
        _sections.Remove(_selectedSection);
        _selectedSection = null;
        SectionCombo.SelectedItem = null;
        RefreshSectionLabels();
        UpdateLayoutSummary();
        PushUndoIfChanged(before);
        SetStatus($"Deleted section and {candidates.Count} candidate(s).");
    }

    private void SectionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSectionSelection)
        {
            return;
        }

        _selectedSection = SectionCombo.SelectedItem as QuickslotSection;
        if (_selectedSection is null)
        {
            return;
        }

        SelectSectionCandidates(_selectedSection);
        LoadSectionControls(_selectedSection);
        SetStatus($"Selected section {_selectedSection.Label}.");
    }

    private void DeleteSelectedCandidatesButton_Click(object sender, RoutedEventArgs e)
    {
        DeleteSelectedCandidates();
    }

    private void CandidateList_KeyDown(object sender, KeyEventArgs e)
    {
        if (TryHandleUndoRedo(e))
        {
            return;
        }

        if (e.Key != Key.Delete)
        {
            if (TryNudgeSelectedCandidates(e.Key))
            {
                e.Handled = true;
            }

            return;
        }

        DeleteSelectedCandidates();
        e.Handled = true;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is not TextBox && TryHandleUndoRedo(e))
        {
            return;
        }

        if (e.Key != Key.Delete || e.OriginalSource is TextBox)
        {
            if (e.OriginalSource is not TextBox && TryNudgeSelectedCandidates(e.Key))
            {
                e.Handled = true;
            }

            return;
        }

        DeleteSelectedCandidates();
        e.Handled = true;
    }

    private void DeleteSelectedCandidates()
    {
        var selected = GetCandidatesToDelete();
        if (selected.Count == 0)
        {
            SetStatus("No selected candidates to delete.");
            return;
        }

        var before = CaptureCandidateSnapshot();
        foreach (var candidate in selected)
        {
            if (_candidateRects.Remove(candidate, out var rect))
            {
                CaptureCanvas.Children.Remove(rect);
            }

            _candidates.Remove(candidate);
        }

        _overlaySlots.RemoveAll(slot => selected.Contains(slot.Source));
        RemoveSectionsContaining(selected);

        UpdateLayoutSummary();
        PushUndoIfChanged(before);
        SetStatus($"Deleted {selected.Count} selected candidates.");
    }

    private List<SlotCandidate> GetCandidatesToDelete()
    {
        var checkedCandidates = _candidates.Where(candidate => candidate.IsSelected).ToList();
        if (checkedCandidates.Count > 0)
        {
            return checkedCandidates;
        }

        return CandidateList.SelectedItem is SlotCandidate highlighted
            ? [highlighted]
            : [];
    }

    private void ClearCandidatesButton_Click(object sender, RoutedEventArgs e)
    {
        var before = CaptureCandidateSnapshot();
        _candidates.Clear();
        ClearCandidateRects();
        ClearSections();
        PushUndoIfChanged(before);
        SetStatus("Candidate list cleared.");
    }

    private void OpenLayoutEditorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overlaySlots.Count == 0)
        {
            SetStatus("No overlay slots are placed.");
            return;
        }

        var editor = new LayoutEditorWindow(
            _layoutCanvasWidth,
            _layoutCanvasHeight,
            _overlayLeft,
            _overlayTop,
            _overlayOpacity,
            _stopHotkey,
            _refreshFps,
            _layoutSlotScale,
            _overlaySlots)
        {
            Owner = this
        };
        editor.ShowDialog();
        _layoutCanvasWidth = editor.CanvasWidth;
        _layoutCanvasHeight = editor.CanvasHeight;
        _overlayLeft = editor.ScreenLeft;
        _overlayTop = editor.ScreenTop;
        _overlayOpacity = editor.OverlayOpacity;
        _stopHotkey = editor.StopHotkey;
        _refreshFps = editor.RefreshFps;
        _layoutSlotScale = editor.SlotScale;
        UpdateLayoutSummary();
        SetStatus("Layout editor closed. Overlay settings updated.");
    }

    private void ClearLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        ClearLayout();
        SetStatus("Overlay layout cleared.");
    }

    private void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentSectionSettings();
        var dialog = new ProfileNameDialog(ReadSelectedProfileName())
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            SetStatus("Profile save canceled.");
            return;
        }

        var profileName = dialog.ProfileName;
        var profile = new OverlayProfile
        {
            Name = profileName,
            CanvasWidth = _layoutCanvasWidth,
            CanvasHeight = _layoutCanvasHeight,
            ScreenLeft = _overlayLeft,
            ScreenTop = _overlayTop,
            Opacity = _overlayOpacity,
            StopHotkey = _stopHotkey,
            RefreshIntervalMs = RefreshIntervalFromFps(_refreshFps),
            RefreshFps = _refreshFps,
            LayoutSlotScale = ReadLayoutSlotScale(),
            SlotInnerSize = Math.Min(ReadSlotInnerWidth(), ReadSlotInnerHeight()),
            SlotInnerWidth = ReadSlotInnerWidth(),
            SlotInnerHeight = ReadSlotInnerHeight(),
            SelectedSectionPattern = Math.Clamp(SectionPatternCombo.SelectedIndex, 0, _sectionSettings.Length - 1),
            SectionSettings = _sectionSettings
                .Select((settings, index) => new OverlayProfileSectionSettings
                {
                    PatternIndex = index,
                    PatternName = GetSectionPatternName(index),
                    SmallGapX = settings.SmallGapX,
                    SmallGapY = settings.SmallGapY,
                    LargeGap = settings.LargeGap
                })
                .ToList(),
            Slots = _overlaySlots.Select(slot => new OverlayProfileSlot
            {
                SourceX = slot.Source.SourceRect.X,
                SourceY = slot.Source.SourceRect.Y,
                SourceWidth = slot.Source.SourceRect.Width,
                SourceHeight = slot.Source.SourceRect.Height,
                OverlayX = slot.OverlayRect.X,
                OverlayY = slot.OverlayRect.Y,
                OverlayWidth = slot.OverlayRect.Width,
                OverlayHeight = slot.OverlayRect.Height
            }).ToList()
        };

        var path = _profileStore.Save(profile, profileName);
        RefreshProfileList(System.IO.Path.GetFileNameWithoutExtension(path));
        _log.Info($"Profile saved: {path}, slots={profile.Slots.Count}");
        SetStatus($"Profile saved: {path}");
    }

    private void LoadProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturedImage is null)
        {
            SetStatus("Capture the game window before loading a profile.");
            return;
        }

        var profileName = ReadSelectedProfileName();
        var profile = _profileStore.Load(profileName);
        if (profile is null)
        {
            SetStatus($"No saved profile exists: {_profileStore.GetProfilePath(profileName)}");
            return;
        }

        RefreshProfileList(profileName);
        _layoutCanvasWidth = Math.Max(120, profile.CanvasWidth);
        _layoutCanvasHeight = Math.Max(80, profile.CanvasHeight);
        _overlayLeft = profile.ScreenLeft;
        _overlayTop = profile.ScreenTop;
        _overlayOpacity = Math.Clamp(profile.Opacity, 0.2, 1);
        _stopHotkey = profile.StopHotkey;
        _refreshFps = CoerceRefreshFps(profile.RefreshFps > 0
            ? profile.RefreshFps
            : FpsFromInterval(profile.RefreshIntervalMs));
        _layoutSlotScale = Math.Clamp(profile.LayoutSlotScale, 1, 3);
        var profileWidth = profile.SlotInnerWidth > 0 ? profile.SlotInnerWidth : profile.SlotInnerSize;
        var profileHeight = profile.SlotInnerHeight > 0 ? profile.SlotInnerHeight : profile.SlotInnerSize;
        SlotWidthBox.Text = ReadSlotDimensionText(profileWidth, ReadSlotInnerWidth());
        SlotHeightBox.Text = ReadSlotDimensionText(profileHeight, ReadSlotInnerHeight());
        ApplyProfileSectionSettings(profile);

        _overlaySlots.Clear();
        _candidates.Clear();
        ClearCandidateRects();
        ClearSections();

        var id = 1;
        foreach (var savedSlot in profile.Slots)
        {
            var candidate = new SlotCandidate(
                id++,
                new Rect(savedSlot.SourceX, savedSlot.SourceY, savedSlot.SourceWidth, savedSlot.SourceHeight),
                100);
            AddCandidate(candidate);

            var crop = _captureService.Crop(_capturedImage, candidate.SourceRect);
            var slot = new OverlaySlot(
                candidate,
                new Rect(savedSlot.OverlayX, savedSlot.OverlayY, savedSlot.OverlayWidth, savedSlot.OverlayHeight),
                crop);
            _overlaySlots.Add(slot);
        }

        UpdateLayoutSummary();
        _log.Info($"Profile loaded: {_profileStore.GetProfilePath(profileName)}, slots={profile.Slots.Count}");
        SetStatus($"Profile loaded: {_profileStore.GetProfilePath(profileName)} ({profile.Slots.Count} slots).");
    }

    private void StartOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overlaySlots.Count == 0)
        {
            SetStatus("No slots are placed on the overlay canvas.");
            return;
        }

        try
        {
            StopOverlay(setStatus: false);
            if (!RegisterStopHotkey())
            {
                return;
            }

            _liveOverlayTimer.Interval = TimeSpan.FromMilliseconds(RefreshIntervalFromFps(_refreshFps));
            if (_wgcSelection is not null)
            {
                _wgcCaptureService.StartLiveCapture(_wgcSelection.Item);
            }

            _overlayWindow = new OverlayWindow(_layoutCanvasWidth, _layoutCanvasHeight, _overlayOpacity, _overlaySlots)
            {
                Left = _overlayLeft,
                Top = _overlayTop
            };
            _overlayWindow.Show();
            _overlayWindow.UpdateLayout();

            if (_overlayWindow.ClickThroughConfigurationException is not null ||
                !_overlayWindow.IsClickThroughConfigured ||
                !_overlayWindow.IsNoActivateConfigured ||
                !_overlayWindow.IsTopmostConfigured ||
                !_overlayWindow.IsInputHookConfigured)
            {
                var detail = _overlayWindow.ClickThroughConfigurationException?.Message ??
                             $"exStyle=0x{_overlayWindow.AppliedExtendedStyle.ToInt64():X16}, " +
                             $"clickThrough={_overlayWindow.IsClickThroughConfigured}, " +
                             $"noActivate={_overlayWindow.IsNoActivateConfigured}, " +
                             $"topmost={_overlayWindow.IsTopmostConfigured}, " +
                             $"inputHook={_overlayWindow.IsInputHookConfigured}";

                _log.Error(
                    "Overlay click-through configuration failed.",
                    _overlayWindow.ClickThroughConfigurationException ?? new InvalidOperationException(detail));

                _overlayWindow.Close();
                _overlayWindow = null;
                _wgcCaptureService.StopLiveCapture();
                _liveOverlayTimer.Stop();

                SetStatus($"Overlay click-through configuration failed: {detail}");
                return;
            }

            _liveOverlayTimer.Start();
            var clickThroughStatus = _overlayWindow.IsClickThroughConfigured ? "click-through" : "not click-through";
            _log.Info(
                $"Overlay started: size={_layoutCanvasWidth}x{_layoutCanvasHeight}, " +
                $"left={_overlayWindow.Left}, top={_overlayWindow.Top}, opacity={_overlayOpacity}, " +
                $"slots={_overlaySlots.Count}, hotkey={_stopHotkey}, refreshFps={_refreshFps}, " +
                $"refreshMs={_liveOverlayTimer.Interval.TotalMilliseconds}, " +
                $"exStyle=0x{_overlayWindow.AppliedExtendedStyle.ToInt64():X16}, " +
                $"clickThrough={_overlayWindow.IsClickThroughConfigured}, " +
                $"noActivate={_overlayWindow.IsNoActivateConfigured}, " +
                $"topmost={_overlayWindow.IsTopmostConfigured}, " +
                $"inputHook={_overlayWindow.IsInputHookConfigured}");
            SetStatus($"Overlay started ({clickThroughStatus}). Stop hotkey: {_stopHotkey}");
        }
        catch (Exception ex)
        {
            _log.Error("Overlay start failed.", ex);
            StopOverlay(setStatus: false);
            SetStatus($"Overlay start failed: {ex.Message}");
        }
    }

    private void StopOverlayButton_Click(object sender, RoutedEventArgs e) => StopOverlay();

    private void CaptureCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(CaptureCanvas);
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
        if (_isSelectingCandidates)
        {
            EndCandidateBoxSelection();
            return;
        }

        if (_draggingCandidate is not null && _candidateRects.TryGetValue(_draggingCandidate, out var rect))
        {
            rect.ReleaseMouseCapture();
        }

        if (_candidateDragSnapshotBefore is not null)
        {
            PushUndoIfChanged(_candidateDragSnapshotBefore);
        }

        _draggingCandidate = null;
        _candidateDragOrigins.Clear();
        _candidateDragSnapshotBefore = null;
    }

    private void RefreshWindows()
    {
        var windows = _windowDiscovery.GetVisibleWindows();
        WindowCombo.ItemsSource = windows;
        WindowCombo.SelectedItem = windows.FirstOrDefault(window => window.LooksLikeMabinogi) ?? windows.FirstOrDefault();
        var captureStatus = _wgcSupport.IsSupported() ? "WGC supported" : "WGC unavailable";
        WindowStatusText.Text = WindowCombo.SelectedItem is GameWindowInfo selected
            ? selected.LooksLikeMabinogi
                ? $"Mabinogi window recognized. ({captureStatus})"
                : $"Selected window is not recognized as Mabinogi. ({captureStatus})"
            : "No selectable window found.";
    }

    private void AddCandidateVisual(SlotCandidate candidate)
    {
        var rect = new Rectangle
        {
            Width = GetCandidateVisualRect(candidate).Width,
            Height = GetCandidateVisualRect(candidate).Height,
            StrokeThickness = 2,
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
        _candidateRects[candidate] = rect;
        CaptureCanvas.Children.Add(rect);
        var visualRect = GetCandidateVisualRect(candidate);
        Canvas.SetLeft(rect, visualRect.X);
        Canvas.SetTop(rect, visualRect.Y);
        UpdateCandidateVisual(candidate);
    }

    private void BeginCandidateDrag(SlotCandidate candidate, Rectangle rect, MouseButtonEventArgs args)
    {
        var isShiftPressed = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (isShiftPressed)
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

    private void BeginCandidateBoxSelection(Point position)
    {
        _isSelectingCandidates = true;
        _selectionStartPosition = position;
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            ClearCandidateSelection();
        }

        _selectionRect = new Rectangle
        {
            Stroke = Brushes.DeepSkyBlue,
            StrokeThickness = 1,
            Fill = new SolidColorBrush(Color.FromArgb(35, 0, 191, 255)),
            IsHitTestVisible = false
        };
        CaptureCanvas.Children.Add(_selectionRect);
        Canvas.SetLeft(_selectionRect, position.X);
        Canvas.SetTop(_selectionRect, position.Y);
        CaptureCanvas.CaptureMouse();
    }

    private void UpdateCandidateBoxSelection(Point position)
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

            CaptureCanvas.Children.Remove(_selectionRect);
            _selectionRect = null;
        }

        CaptureCanvas.ReleaseMouseCapture();
        _isSelectingCandidates = false;
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
        var clampedX = Math.Clamp(x, CandidateBorderPixels, Math.Max(CandidateBorderPixels, CaptureCanvas.Width - candidate.SourceRect.Width - CandidateBorderPixels));
        var clampedY = Math.Clamp(y, CandidateBorderPixels, Math.Max(CandidateBorderPixels, CaptureCanvas.Height - candidate.SourceRect.Height - CandidateBorderPixels));
        candidate.MoveTo(clampedX, clampedY);
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
    {
        foreach (var candidate in _candidates)
        {
            candidate.IsSelected = ReferenceEquals(candidate, selected);
        }
    }

    private void ClearCandidateSelection()
    {
        foreach (var candidate in _candidates)
        {
            candidate.IsSelected = false;
        }
    }

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
        var section = new QuickslotSection(_nextSectionId++, seed, patternIndex, settings, sectionCandidates);
        _sections.Add(section);
        SelectSection(section);
        return added;
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
        rect.Inflate(CandidateBorderPixels, CandidateBorderPixels);
        return rect;
    }

    private SectionPattern ReadSectionPattern() =>
        GetSectionPattern(Math.Clamp(SectionPatternCombo.SelectedIndex, 0, _sectionSettings.Length - 1));

    private static SectionPattern GetSectionPattern(int index) =>
        index == 1 ? SectionPattern.LeftVertical() : SectionPattern.TopGrouped();

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
        SetStatus($"Adjusted {_selectedSection.Label}: gap X {ReadSmallGapX():0}px, gap Y {ReadSmallGapY():0}px, large gap {ReadLargeGap():0}px.");
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
        var selected = _candidates.Where(candidate => candidate.IsSelected).ToList();
        if (selected.Count == 0 && CandidateList.SelectedItem is SlotCandidate highlighted)
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

        SetStatus($"Nudged {selected.Count} candidate(s) by 1px.");
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
            }
        };
        AddCandidateVisual(candidate);
    }

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
        UpdateLayoutSummary();
    }

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
        _sections.Clear();
        _selectedSection = null;
        _nextSectionId = 1;
        SectionCombo.SelectedItem = null;
        RefreshSectionLabels();
    }

    private void RemoveSectionsContaining(IReadOnlyCollection<SlotCandidate> candidates)
    {
        var removed = _sections.Where(section => section.Candidates.Any(candidates.Contains)).ToList();
        foreach (var section in removed)
        {
            _sections.Remove(section);
        }

        if (_selectedSection is not null && removed.Contains(_selectedSection))
        {
            _selectedSection = null;
            SectionCombo.SelectedItem = null;
        }

        RefreshSectionLabels();
    }

    private void RefreshSectionLabels()
    {
        foreach (var candidate in _candidates)
        {
            candidate.SectionMembership = string.Empty;
        }

        foreach (var section in _sections)
        {
            foreach (var candidate in section.Candidates.Where(candidate => _candidates.Contains(candidate)))
            {
                candidate.SectionMembership = string.IsNullOrWhiteSpace(candidate.SectionMembership)
                    ? $"section {section.Id:00}"
                    : $"{candidate.SectionMembership},{section.Id:00}";
            }

            section.RefreshLabel();
        }

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
        var selectedSectionId = _selectedSection?.Id ?? 0;
        return new CandidateEditSnapshot(
            _candidates
                .Select(candidate => new CandidateState(
                    candidate.Id,
                    candidate.SourceRect.X,
                    candidate.SourceRect.Y,
                    candidate.SourceRect.Width,
                    candidate.SourceRect.Height,
                    candidate.Score,
                    candidate.IsSelected))
                .ToList(),
            _sections
                .Select(section => new SectionState(
                    section.Id,
                    section.Seed.Id,
                    section.PatternIndex,
                    section.Settings.SmallGapX,
                    section.Settings.SmallGapY,
                    section.Settings.LargeGap,
                    section.Candidates.Select(candidate => candidate.Id).ToList()))
                .ToList(),
            selectedSectionId,
            _nextSectionId,
            selectedId);
    }

    private void PushUndoIfChanged(CandidateEditSnapshot before)
    {
        if (CandidateSnapshotsEqual(before, CaptureCandidateSnapshot()))
        {
            return;
        }

        _undoStack.Push(before);
        _redoStack.Clear();
    }

    private void UndoCandidateEdit()
    {
        if (_undoStack.Count == 0)
        {
            SetStatus("No candidate edit to undo.");
            return;
        }

        var current = CaptureCandidateSnapshot();
        var previous = _undoStack.Pop();
        _redoStack.Push(current);
        RestoreCandidateSnapshot(previous);
        SetStatus("Candidate edit undone.");
    }

    private void RedoCandidateEdit()
    {
        if (_redoStack.Count == 0)
        {
            SetStatus("No candidate edit to redo.");
            return;
        }

        var current = CaptureCandidateSnapshot();
        var next = _redoStack.Pop();
        _undoStack.Push(current);
        RestoreCandidateSnapshot(next);
        SetStatus("Candidate edit redone.");
    }

    private void RestoreCandidateSnapshot(CandidateEditSnapshot snapshot)
    {
        _candidates.Clear();
        ClearCandidateRects();
        _sections.Clear();
        _selectedSection = null;

        SlotCandidate? selected = null;
        var restoredById = new Dictionary<int, SlotCandidate>();
        foreach (var saved in snapshot.Candidates)
        {
            var candidate = new SlotCandidate(
                saved.Id,
                new Rect(saved.X, saved.Y, saved.Width, saved.Height),
                saved.Score)
            {
                IsSelected = saved.IsSelected
            };
            AddCandidate(candidate);
            restoredById[candidate.Id] = candidate;
            if (saved.Id == snapshot.SelectedId)
            {
                selected = candidate;
            }
        }

        CandidateList.SelectedItem = selected;
        QuickslotSection? selectedSection = null;
        foreach (var savedSection in snapshot.Sections)
        {
            if (!restoredById.TryGetValue(savedSection.SeedId, out var seed))
            {
                continue;
            }

            var candidates = savedSection.CandidateIds
                .Select(id => restoredById.TryGetValue(id, out var candidate) ? candidate : null)
                .Where(candidate => candidate is not null)
                .Cast<SlotCandidate>()
                .ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            var section = new QuickslotSection(
                savedSection.Id,
                seed,
                savedSection.PatternIndex,
                new SectionSettings(savedSection.SmallGapX, savedSection.SmallGapY, savedSection.LargeGap),
                candidates);
            _sections.Add(section);
            if (savedSection.Id == snapshot.SelectedSectionId)
            {
                selectedSection = section;
            }
        }

        _nextSectionId = Math.Max(snapshot.NextSectionId, _sections.Count == 0 ? 1 : _sections.Max(section => section.Id) + 1);
        if (selectedSection is not null)
        {
            SelectSection(selectedSection);
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
            if (_capturedImage is not null)
            {
                slot.Preview = _captureService.Crop(_capturedImage, restoredSource.SourceRect);
            }
        }

        UpdateLayoutSummary();
    }

    private static bool CandidateSnapshotsEqual(CandidateEditSnapshot left, CandidateEditSnapshot right)
    {
        if (left.SelectedId != right.SelectedId ||
            left.SelectedSectionId != right.SelectedSectionId ||
            left.NextSectionId != right.NextSectionId ||
            left.Candidates.Count != right.Candidates.Count ||
            left.Sections.Count != right.Sections.Count)
        {
            return false;
        }

        return left.Candidates.SequenceEqual(right.Candidates) &&
               left.Sections.Zip(right.Sections).All(pair => SectionStatesEqual(pair.First, pair.Second));
    }

    private static bool SectionStatesEqual(SectionState left, SectionState right) =>
        left.Id == right.Id &&
        left.SeedId == right.SeedId &&
        left.PatternIndex == right.PatternIndex &&
        left.SmallGapX.Equals(right.SmallGapX) &&
        left.SmallGapY.Equals(right.SmallGapY) &&
        left.LargeGap.Equals(right.LargeGap) &&
        left.CandidateIds.SequenceEqual(right.CandidateIds);

    private int NextCandidateId() => _candidates.Count == 0 ? 1 : _candidates.Max(candidate => candidate.Id) + 1;

    private void StopOverlay(bool setStatus = true)
    {
        _liveOverlayTimer.Stop();
        _wgcCaptureService.StopLiveCapture();
        _overlayWindow?.Close();
        _overlayWindow = null;
        _hotkeyService?.Dispose();
        _hotkeyService = null;
        if (setStatus)
        {
            _log.Info("Overlay stopped.");
            SetStatus("Overlay stopped.");
        }
    }

    private void LiveOverlayTimer_Tick(object? sender, EventArgs e)
    {
        if ((_wgcSelection is null && _selectedWindow is null) ||
            _overlayWindow is null ||
            _overlaySlots.Count == 0 ||
            _isLiveRefreshInProgress)
        {
            return;
        }

        try
        {
            _isLiveRefreshInProgress = true;
            BitmapSource liveCapture;
            if (_wgcSelection is not null)
            {
                if (_wgcCaptureService.LastLiveCaptureException is not null)
                {
                    throw new InvalidOperationException("Live WGC capture failed.", _wgcCaptureService.LastLiveCaptureException);
                }

                if (!_wgcCaptureService.TryGetLatestFrame(out var latestFrame) || latestFrame is null)
                {
                    return;
                }

                liveCapture = latestFrame;
            }
            else
            {
                liveCapture = _captureService.CaptureClientArea(_selectedWindow!);
            }

            foreach (var slot in _overlaySlots)
            {
                slot.Preview = _captureService.Crop(liveCapture, slot.Source.SourceRect);
            }

            _overlayWindow.RenderSlots(_overlaySlots);
        }
        catch (Exception ex)
        {
            _log.Error("Live overlay refresh failed.", ex);
            StopOverlay(setStatus: false);
            SetStatus($"Live overlay refresh failed: {ex.Message}");
        }
        finally
        {
            _isLiveRefreshInProgress = false;
        }
    }

    private bool RegisterStopHotkey()
    {
        if (!HotkeyParser.TryParse(_stopHotkey, out var hotkey))
        {
            SetStatus("Invalid hotkey. Use a format like Ctrl+Shift+F8.");
            return false;
        }

        _hotkeyService ??= new HotkeyService();
        var registered = _hotkeyService.Register(new WindowInteropHelper(this).Handle, hotkey.Modifiers, hotkey.VirtualKey, () => StopOverlay());
        if (!registered)
        {
            _log.Info($"Stop hotkey registration failed: {hotkey.DisplayText}");
            SetStatus($"Stop hotkey registration failed: {hotkey.DisplayText}");
        }
        else
        {
            _log.Info($"Stop hotkey registered: {hotkey.DisplayText}");
        }

        return registered;
    }

    private int ReadSlotInnerWidth() => ReadSlotDimension(SlotWidthBox?.Text, 29);

    private int ReadSlotInnerHeight() => ReadSlotDimension(SlotHeightBox?.Text, 29);

    private static int ReadSlotDimension(string? text, int fallback) =>
        int.TryParse(text, out var value)
            ? Math.Clamp(value, 1, 300)
            : Math.Clamp(fallback, 1, 300);

    private static string ReadSlotDimensionText(int value, int fallback) =>
        Math.Clamp(value > 0 ? value : fallback, 1, 300).ToString();

    private int ReadCandidateBoxWidth() => ReadSlotInnerWidth() + CandidateBorderPixels * 2;

    private int ReadCandidateBoxHeight() => ReadSlotInnerHeight() + CandidateBorderPixels * 2;

    private double ReadLayoutSlotScale() => Math.Clamp(_layoutSlotScale, 1, 3);

    private double ReadSmallGapX() => Math.Clamp(SmallGapXSlider.Value, 0, 30);

    private double ReadSmallGapY() => Math.Clamp(SmallGapYSlider.Value, 0, 30);

    private double ReadLargeGap() => Math.Clamp(LargeGapSlider.Value, 0, 60);

    private static int CoerceRefreshFps(int fps) =>
        RefreshFpsOptions.OrderBy(option => Math.Abs(option - fps)).First();

    private static int RefreshIntervalFromFps(int fps) =>
        (int)Math.Max(1, Math.Round(1000.0 / CoerceRefreshFps(fps)));

    private static int FpsFromInterval(int intervalMs) =>
        intervalMs <= 0 ? 60 : (int)Math.Round(1000.0 / intervalMs);

    private void UpdateSizeLabels()
    {
        if (SlotSizeText is not null)
        {
            SlotSizeText.Text = $"inside {ReadSlotInnerWidth()}x{ReadSlotInnerHeight()}px, box {ReadCandidateBoxWidth()}x{ReadCandidateBoxHeight()}px";
        }
    }

    private void UpdateSectionGapLabels()
    {
        var usesLargeGap = SectionPatternCombo.SelectedIndex == 0;
        SmallGapXText.Text = $"Small gap X {ReadSmallGapX():0}px";
        SmallGapYText.Text = $"Small gap Y {ReadSmallGapY():0}px";
        LargeGapText.Text = usesLargeGap
            ? $"Large gap {ReadLargeGap():0}px"
            : "Large gap unused";
        LargeGapSlider.IsEnabled = usesLargeGap;
    }

    private void SaveCurrentSectionSettings()
    {
        if (_isUpdatingSectionControls)
        {
            return;
        }

        var index = Math.Clamp(_currentSectionIndex, 0, _sectionSettings.Length - 1);
        var settings = new SectionSettings(ReadSmallGapX(), ReadSmallGapY(), ReadLargeGap());
        _sectionSettings[index] = settings;
        if (_selectedSection is not null && _selectedSection.PatternIndex == index)
        {
            _selectedSection.Settings = settings;
        }
    }

    private void ApplySectionSettingsToControls(int patternIndex)
    {
        _isUpdatingSectionControls = true;
        try
        {
            var settings = _sectionSettings[Math.Clamp(patternIndex, 0, _sectionSettings.Length - 1)];
            SmallGapXSlider.Value = Math.Clamp(settings.SmallGapX, SmallGapXSlider.Minimum, SmallGapXSlider.Maximum);
            SmallGapYSlider.Value = Math.Clamp(settings.SmallGapY, SmallGapYSlider.Minimum, SmallGapYSlider.Maximum);
            LargeGapSlider.Value = Math.Clamp(settings.LargeGap, LargeGapSlider.Minimum, LargeGapSlider.Maximum);
        }
        finally
        {
            _isUpdatingSectionControls = false;
        }
    }

    private void ApplyProfileSectionSettings(OverlayProfile profile)
    {
        foreach (var saved in profile.SectionSettings)
        {
            if (saved.PatternIndex < 0 || saved.PatternIndex >= _sectionSettings.Length)
            {
                continue;
            }

            _sectionSettings[saved.PatternIndex] = new SectionSettings(
                Math.Clamp(saved.SmallGapX, 0, 30),
                Math.Clamp(saved.SmallGapY, 0, 30),
                Math.Clamp(saved.LargeGap, 0, 60));
        }

        _currentSectionIndex = Math.Clamp(profile.SelectedSectionPattern, 0, _sectionSettings.Length - 1);
        _isUpdatingSectionControls = true;
        try
        {
            SectionPatternCombo.SelectedIndex = _currentSectionIndex;
        }
        finally
        {
            _isUpdatingSectionControls = false;
        }

        ApplySectionSettingsToControls(_currentSectionIndex);
        UpdateSectionGapLabels();
    }

    private string ReadSelectedProfileName() =>
        ProfileCombo.SelectedItem is string selected && !string.IsNullOrWhiteSpace(selected)
            ? selected
            : "default";

    private void RefreshProfileList(string? selectedProfileName = null)
    {
        var names = _profileStore.ListProfileNames().ToList();
        if (names.Count == 0)
        {
            names.Add("default");
        }

        var selected = string.IsNullOrWhiteSpace(selectedProfileName)
            ? ReadSelectedProfileName()
            : selectedProfileName.Trim();
        if (!names.Contains(selected, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(selected);
            names = names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        ProfileCombo.ItemsSource = names;
        ProfileCombo.SelectedItem = names.FirstOrDefault(name => string.Equals(name, selected, StringComparison.OrdinalIgnoreCase))
                                    ?? names.FirstOrDefault();
    }

    private static string GetSectionPatternName(int index) =>
        index == 1 ? SectionPattern.LeftVertical().Name : SectionPattern.TopGrouped().Name;

    private void UpdateLayoutSummary()
    {
        LayoutSummaryText.Text =
            $"Slots: {_overlaySlots.Count} | Canvas: {_layoutCanvasWidth:0}x{_layoutCanvasHeight:0} | " +
            $"Screen: {_overlayLeft:0}, {_overlayTop:0} | Opacity: {_overlayOpacity:0.00} | " +
            $"Scale: {_layoutSlotScale:0.0}x | Hotkey: {_stopHotkey} | Max FPS: {_refreshFps}";
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        _log.Info($"Status: {message}");
    }

    private sealed class QuickslotSection
    {
        public QuickslotSection(int id, SlotCandidate seed, int patternIndex, SectionSettings settings, List<SlotCandidate> candidates)
        {
            Id = id;
            Seed = seed;
            PatternIndex = patternIndex;
            Settings = settings;
            Candidates = candidates;
        }

        public int Id { get; }

        public SlotCandidate Seed { get; }

        public int PatternIndex { get; }

        public SectionSettings Settings { get; set; }

        public List<SlotCandidate> Candidates { get; }

        public string Label => $"#{Id:00} {GetSectionPatternName(PatternIndex)} ({Candidates.Count})";

        public void RefreshLabel()
        {
        }
    }

    private sealed record SectionSettings(double SmallGapX, double SmallGapY, double LargeGap);

    private sealed record CandidateEditSnapshot(
        List<CandidateState> Candidates,
        List<SectionState> Sections,
        int SelectedSectionId,
        int NextSectionId,
        int SelectedId);

    private sealed record CandidateState(int Id, double X, double Y, double Width, double Height, double Score, bool IsSelected);

    private sealed record SectionState(
        int Id,
        int SeedId,
        int PatternIndex,
        double SmallGapX,
        double SmallGapY,
        double LargeGap,
        List<int> CandidateIds);

    private sealed record SectionPattern(
        string Name,
        int GroupColumns,
        int GroupRows,
        int GroupColumnsCount,
        int GroupRowsCount,
        Func<double, double> InnerGapX,
        Func<double, double> InnerGapY,
        Func<double, double> GroupGapX,
        Func<double, double> GroupGapY)
    {
        public static SectionPattern TopGrouped() => new(
            "top grouped 4x2 x3",
            4,
            2,
            3,
            1,
            smallGap => smallGap,
            smallGap => smallGap,
            largeGap => largeGap,
            _ => 0);

        public static SectionPattern LeftVertical() => new(
            "left vertical 2x8",
            2,
            8,
            1,
            1,
            smallGap => smallGap,
            smallGap => smallGap,
            _ => 0,
            _ => 0);
    }
}
