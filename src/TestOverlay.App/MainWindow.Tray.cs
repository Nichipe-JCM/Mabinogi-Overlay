using System.ComponentModel;
using System.Drawing;
using System.Windows;
using TestOverlay.App.Models;
using TestOverlay.App.Services;
using Forms = System.Windows.Forms;

namespace TestOverlay.App;

public partial class MainWindow
{
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _trayOpenMenuItem;
    private Forms.ToolStripMenuItem? _trayOverlayMenuItem;
    private Forms.ToolStripMenuItem? _trayExitMenuItem;
    private bool _isAppExitRequested;

    private void InitializeTrayBehavior()
    {
        _trayOpenMenuItem = new Forms.ToolStripMenuItem();
        _trayOverlayMenuItem = new Forms.ToolStripMenuItem();
        _trayExitMenuItem = new Forms.ToolStripMenuItem();
        _trayOpenMenuItem.Click += (_, _) => Dispatcher.BeginInvoke(RestoreFromTray);
        _trayOverlayMenuItem.Click += (_, _) => Dispatcher.BeginInvoke(ToggleOverlayFromTrayAsync);
        _trayExitMenuItem.Click += (_, _) => Dispatcher.BeginInvoke(ExitFromTray);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_trayOpenMenuItem);
        menu.Items.Add(_trayOverlayMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_trayExitMenuItem);
        menu.Opening += (_, _) => RefreshTrayMenuText();

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "Mabinogi Overlay",
            Icon = LoadTrayIcon(),
            ContextMenuStrip = menu,
            Visible = false
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(RestoreFromTray);
        RefreshTrayMenuText();
    }

    private static Icon LoadTrayIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Image/AppIcon.ico"));
        if (resource is null)
        {
            return (Icon)SystemIcons.Application.Clone();
        }

        using (resource.Stream)
        using (var icon = new Icon(resource.Stream))
        {
            return (Icon)icon.Clone();
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_updateExitCommitted) return;
        if (_updateDialogOpen) { e.Cancel = true; return; }
        var profileSaved = FlushProfileAutoSave();
        Window exitOwner = _compactControlWindow is { IsVisible: true } visibleCompact ? visibleCompact : this;
        if (_isAppExitRequested)
        {
            if ((!profileSaved && !ResolveProfileSaveFailureBeforeExit()) ||
                !ErinTimerPanel.ConfirmExitWithUnsavedSettings(exitOwner))
            {
                e.Cancel = true;
                CancelPendingExit();
            }
            return;
        }

        var behavior = _appSettings.CloseBehavior;
        if (behavior == AppCloseBehavior.Ask)
        {
            var dialog = new CloseBehaviorDialog
            {
                Owner = _compactControlWindow is { IsVisible: true } compact ? compact : this
            };
            if (dialog.ShowDialog() != true || dialog.Choice == AppCloseBehavior.Ask)
            {
                e.Cancel = true;
                return;
            }

            behavior = dialog.Choice;
            if (dialog.RememberChoice)
            {
                _appSettings.CloseBehavior = behavior;
                SaveCloseBehaviorPreference();
            }
        }

        if (behavior == AppCloseBehavior.MinimizeToTray)
        {
            e.Cancel = true;
            MinimizeToTray();
            return;
        }

        if ((!profileSaved && !ResolveProfileSaveFailureBeforeExit()) ||
                !ErinTimerPanel.ConfirmExitWithUnsavedSettings(exitOwner))
        {
            e.Cancel = true;
            return;
        }

        _isAppExitRequested = true;
    }

    private bool ResolveProfileSaveFailureBeforeExit()
    {
        while (true)
        {
            var dialog = new ProfileSaveFailureDialog(_lastProfileSaveError);
            Window? owner = _compactControlWindow is { IsVisible: true } compact
                ? compact
                : IsVisible
                    ? this
                    : null;
            if (owner is not null)
            {
                dialog.Owner = owner;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            dialog.ShowDialog();
            if (dialog.Choice == ProfileSaveFailureChoice.DiscardAndExit)
            {
                _log.Info("Application exit continued after the user discarded unsaved profile changes.");
                return true;
            }

            if (dialog.Choice != ProfileSaveFailureChoice.Retry)
            {
                return false;
            }

            if (FlushProfileAutoSave())
            {
                return true;
            }
        }
    }

    private void CancelPendingExit()
    {
        (Application.Current as App)?.CancelRestart();
        _isAppExitRequested = false;
        if (_appSettings.CompactModeEnabled)
        {
            EnterCompactMode(savePreference: false);
        }
        else
        {
            RestoreFromTray();
        }
    }

    private void MinimizeToTray()
    {
        _compactControlWindow?.Hide();
        Hide();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = true;
        }
        _log.Info("Application minimized to notification area.");
    }

    private void RestoreFromTray()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
        }

        if (_appSettings.CompactModeEnabled)
        {
            EnterCompactMode(savePreference: false);
        }
        else
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }
        _log.Info("Application restored from notification area.");
    }

    internal void ActivateFromSecondInstance()
    {
        RestoreFromTray();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    private void ExitFromTray()
    {
        _isAppExitRequested = true;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
        }
        Close();
    }

    private void RestartAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateDialogOpen || _updateExitCommitted || Application.Current is not App app) return;
        try
        {
            app.PrepareRestart();
            ExitFromTray();
        }
        catch (Exception exception)
        {
            app.CancelRestart();
            CancelPendingExit();
            _log.Error("Could not request application restart.", exception);
            ShowInAppNotice(L.F("debug.restart.error", exception.Message));
        }
    }

    private async void ToggleOverlayFromTrayAsync()
    {
        if (_overlayRuntime.IsRunning)
        {
            StopOverlay();
        }
        else
        {
            await StartOverlayAsync();
        }

        RefreshTrayMenuText();
    }

    private void SaveCloseBehaviorPreference()
    {
        try
        {
            _settingsStore.Save(_appSettings);
        }
        catch (Exception exception)
        {
            _log.Error("Close behavior preference could not be saved.", exception);
        }
    }

    private void RefreshTrayMenuText()
    {
        if (_trayOpenMenuItem is not null)
        {
            _trayOpenMenuItem.Text = L.T("tray.open");
        }
        if (_trayExitMenuItem is not null)
        {
            _trayExitMenuItem.Text = L.T("tray.exit");
        }
        if (_trayOverlayMenuItem is not null)
        {
            _trayOverlayMenuItem.Text = _overlayRuntime.IsRunning
                ? L.T("tray.overlay.stop")
                : L.T("tray.overlay.start");
        }
    }

    private void DisposeTrayBehavior()
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.Visible = false;
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Icon?.Dispose();
        _trayIcon.Dispose();
        _trayIcon = null;
    }
}
