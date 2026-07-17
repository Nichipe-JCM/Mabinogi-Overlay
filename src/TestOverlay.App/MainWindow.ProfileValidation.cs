using System.Windows;
using System.Windows.Media.Imaging;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class MainWindow
{
    private async void ValidateSavedSlotsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturedImage is null)
        {
            ShowInAppNotice(L.T("profile.validation.capture.required"));
            SetStatus("profile.validation.capture.required");
            return;
        }

        FlushProfileAutoSave();
        var profileName = ReadSelectedProfileName();
        var profile = _profileStore.Load(profileName);
        if (profile is null)
        {
            ShowInAppNotice(L.T("profile.validation.profile.required"));
            SetStatus("profile.validation.profile.required");
            return;
        }

        BitmapSource image = _capturedImage;
        if (!image.IsFrozen)
        {
            image = image.Clone();
            image.Freeze();
        }

        ValidateSavedSlotsButton.IsEnabled = false;
        try
        {
            var validator = new ProfileSlotValidationService(_roiSectionDetection, _monitorTemplateDetection);
            var report = await Task.Run(() => validator.Validate(profile, image));
            _log.Info(
                $"Saved slot validation completed: profile={profileName}, valid={report.ValidCount}, " +
                $"warnings={report.WarningCount}, invalid={report.InvalidCount}");
            var shouldApply = report.InvalidCount == 0 && report.ValidCount > 0;
            var applied = shouldApply && LoadSelectedProfile();
            if (applied)
            {
                _log.Info($"Validated profile applied: profile={profileName}.");
            }
            new ProfileSlotValidationWindow(report, applied) { Owner = this }.ShowDialog();
            SetStatus(applied
                ? "profile.validation.status.applied"
                : report.InvalidCount > 0
                    ? "profile.validation.status.invalid"
                    : "profile.validation.status.valid");
        }
        catch (Exception exception)
        {
            _log.Error("Saved slot validation failed.", exception);
            ShowInAppNotice(L.F("profile.validation.failed", exception.Message));
            SetStatus(L.F("profile.validation.failed", exception.Message));
        }
        finally
        {
            ValidateSavedSlotsButton.IsEnabled = true;
        }
    }
}
