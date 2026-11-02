using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Forms;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Interop;
using Windows.Devices.Radios;
using ISLA.Media;
using ISLA.Notifications;
using ISLA.Pomodoro;
using ISLA.QuickSettings;
using ISLA.Settings;

namespace ISLA;

public partial class MainWindow : Window
{
    private const int GwlExstyle = -20;
    private const long WsExAppwindow = 0x00040000;
    private const long WsExToolwindow = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    // ---- Island geometry -------------------------------------------------------------
    private const double CompactWidth = 180;
    private const double CompactHeight = 30;
    private const double NotificationWidth = 306;
    private const double CompactHorizontalPadding = 18;
    private const double NotificationHorizontalPadding = 4;
    private const double IdleNotchWidth = CompactWidth * 0.3;
    private const double IdleNotchHeight = CompactHeight * 0.5;
    // Bottom corner radius of the idle notch (same value used for IslandSurface.CornerRadius).
    private const double IdleNotchRadius = IdleNotchHeight / 2;
    private const double IdleBatteryStroke = 1;
    private const double NormalTopInset = 6;
    private const double ExpandedWidth = 520;
    private const double MediaHeight = 280;
    private const double TimerHeight = 280;
    private const double SettingsHeight = MediaHeight;
    private const double ExpandedContentHeight = 280;
    private const double CompactRadius = 15;
    private const double ExpandedRadius = 28;

    // Width of one dock slot: button 30 + margins 1 + 1.
    private const double DockSlotWidth = 32;

    // ---- Easing (frozen = shared + cheap) ---------------------------------------------
    // Expand has a tiny overshoot so it feels springy; collapse is a clean, fast ease-out.
    private static readonly IEasingFunction ExpandEase =
        Frozen(new BackEase { Amplitude = 0.12, EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction CollapseEase =
        Frozen(new QuinticEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction TabEase =
        Frozen(new QuarticEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction ContentEase =
        Frozen(new QuarticEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction FadeEase =
        Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction PillEase =
        Frozen(new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut });

    // Animatable corner radius (WPF can't animate CornerRadius directly, a double DP is simplest).
    public static readonly DependencyProperty IslandRadiusProperty =
        DependencyProperty.Register(
            nameof(IslandRadius),
            typeof(double),
            typeof(MainWindow),
            new PropertyMetadata(CompactRadius, OnIslandRadiusChanged));

    public double IslandRadius
    {
        get => (double)GetValue(IslandRadiusProperty);
        set => SetValue(IslandRadiusProperty, value);
    }

    private static void OnIslandRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MainWindow window && window.IslandSurface is not null)
        {
            window.IslandSurface.CornerRadius = new CornerRadius((double)e.NewValue);
        }
    }

    // ---- Services / timers -----------------------------------------------------------
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _mediaRefreshTimer;
    private readonly DispatcherTimer _mediaProgressTimer;
    private readonly MediaService _mediaService = new();
    private readonly QuickSettingsService _quickSettingsService = new();
    private readonly PomodoroService _pomodoroService = new();
    private readonly AppSettingsService _settingsService = new();
    private readonly DispatcherTimer _settingsRefreshTimer;
    private readonly DispatcherTimer _pomodoroTimer;
    private readonly DispatcherTimer _systemStateTimer;
    private readonly DispatcherTimer _nodeNotificationTimer;
    private readonly DispatcherTimer _windowsNotificationPollTimer;
    private readonly DispatcherTimer _idleTimer;
    private readonly WindowsToastService _windowsToastService = new();
    private readonly WindowsNotificationService _windowsNotificationService = new();
    private readonly NotifyIcon _trayIcon;
    private readonly System.Drawing.Icon _trayIconImage;

    // ---- State -----------------------------------------------------------------------
    private string _activeTab = "Media";
    private bool _updatingVolumeSlider;
    private bool _isExpanded;
    private bool _isPinned;
    private int _expandVersion;
    private int _tabVersion;
    private bool? _lastPlaying;
    private bool? _lastPomodoroRunning;
    private string? _pomodoroNotice;
    private bool? _lastCapsLock;
    private bool? _lastCharging;
    private string? _lastWiFiStatus;
    private string? _lastBluetoothStatus;
    private bool _lowBatteryNotified;
    private DateTime _nodeNotificationUntil;
    private ISLA.Media.MediaState? _lastMediaState;
    // Local playback clock: the position reported by Windows is only refreshed occasionally
    // (and can be stale), so we run our own clock and only re-sync it when it makes sense.
    private TimeSpan _mediaAnchorPosition;
    private DateTime _mediaAnchorUtc;
    private TimeSpan _mediaLastReportedPosition;
    private bool _mediaAnchorPlaying;
    private string? _mediaTrackKey;
    private static readonly TimeSpan MediaSeekTolerance = TimeSpan.FromSeconds(1.5);
    private DateTime _lastInteractionUtc;
    private bool _isIdleNotch;
    private int _lastBatteryPercent = -1;
    private bool _refreshingMedia;
    private bool _refreshingSettings;
    private System.Windows.Media.Brush _normalBatteryBrush = System.Windows.Media.Brushes.White;
    private readonly System.Windows.Media.Brush _lowBatteryBrush = Frozen(new SolidColorBrush(Colors.IndianRed));

    public MainWindow()
    {
        InitializeComponent();
        _normalBatteryBrush = (System.Windows.Media.Brush)FindResource("IslandAccent");

        _trayIconImage = LoadTrayIcon();
        _trayIcon = CreateTrayIcon();
        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _collapseTimer.Tick += CollapseTimer_Tick;
        _mediaRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _mediaRefreshTimer.Tick += MediaRefreshTimer_Tick;
        _mediaProgressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(125) };
        _mediaProgressTimer.Tick += MediaProgressTimer_Tick;
        _settingsRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _settingsRefreshTimer.Tick += SettingsRefreshTimer_Tick;
        _pomodoroTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _pomodoroTimer.Tick += PomodoroTimer_Tick;
        _systemStateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _systemStateTimer.Tick += SystemStateTimer_Tick;
        _nodeNotificationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _nodeNotificationTimer.Tick += NodeNotificationTimer_Tick;
        _windowsNotificationPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _windowsNotificationPollTimer.Tick += WindowsNotificationPollTimer_Tick;
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _idleTimer.Tick += IdleTimer_Tick;
        Loaded += MainWindow_Loaded;
        Deactivated += MainWindow_Deactivated;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        PositionAtTopCenter();
        UpdateClock();
        _mediaRefreshTimer.Start();
        _mediaProgressTimer.Start();
        _settingsRefreshTimer.Start();
        _pomodoroTimer.Start();
        _systemStateTimer.Start();
        _lastInteractionUtc = DateTime.UtcNow;
        _idleTimer.Start();
        _windowsNotificationService.NotificationReceived += WindowsNotificationService_NotificationReceived;
        _windowsNotificationService.Error += WindowsNotificationService_Error;
        try
        {
            await _windowsNotificationService.StartAsync();
            _windowsNotificationPollTimer.Start();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                exception.Message,
                "ISLA notification access",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        await RefreshMediaAsync();
        await RefreshSettingsAsync();
        RefreshPomodoro();
    }

    private void WindowsNotificationService_NotificationReceived(
        object? sender,
        WindowsNotification notification)
    {
        var message = string.IsNullOrWhiteSpace(notification.Body)
            ? notification.Title
            : $"{notification.Title} — {notification.Body}";
        Dispatcher.BeginInvoke(() => ShowNodeNotification(notification.AppName, message, TimeSpan.FromSeconds(4)));
    }

    private void WindowsNotificationService_Error(object? sender, Exception exception)
    {
        Debug.WriteLine($"Windows notification listener error: {exception}");
    }

    private async void WindowsNotificationPollTimer_Tick(object? sender, EventArgs e)
    {
        await _windowsNotificationService.PollAsync();
    }

    private NotifyIcon CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show ISLA", null, (_, _) =>
        {
            Show();
            Activate();
            _isPinned = true;
            SetExpanded(true);
        });
        menu.Items.Add(new ToolStripSeparator());
        var startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = _settingsService.Settings.StartupWithWindows,
            CheckOnClick = true
        };
        startupItem.CheckedChanged += (_, _) =>
        {
            try
            {
                _settingsService.Settings.StartupWithWindows = startupItem.Checked;
                StartupManager.SetEnabled(startupItem.Checked);
                _settingsService.Save();
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or SecurityException)
            {
                startupItem.Checked = !startupItem.Checked;
                System.Windows.MessageBox.Show(
                    $"Startup setting could not be changed: {exception.Message}",
                    "ISLA",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        };
        menu.Items.Add(startupItem);
        var notificationItem = new ToolStripMenuItem("Allow Windows notifications")
        {
            Checked = _windowsNotificationService.AccessStatus ==
                Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed,
            CheckOnClick = false
        };
        menu.Opening += (_, _) =>
        {
            notificationItem.Checked = _windowsNotificationService.AccessStatus ==
                Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed;
        };
        notificationItem.Click += (_, _) =>
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:privacy-notifications",
                UseShellExecute = true
            });
        };
        menu.Items.Add(notificationItem);
        menu.Items.Add("Test Windows notification", null, (_, _) =>
        {
            _windowsToastService.Show("ISLA", "Windows toast notifications are enabled.");
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => System.Windows.Application.Current.Shutdown());

        return new NotifyIcon
        {
            Icon = _trayIconImage,
            Text = "ISLA",
            Visible = true,
            ContextMenuStrip = menu
        };
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        using var bitmap = new System.Drawing.Bitmap(Path.Combine(AppContext.BaseDirectory, "Icon.png"));
        return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(bitmap.GetHicon()).Clone();
    }

    // =================================================================================
    //  Refresh loops
    // =================================================================================

    private async void SettingsRefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshSettingsAsync();
    }

    private async Task RefreshSettingsAsync()
    {
        if (_refreshingSettings)
        {
            return;
        }

        _refreshingSettings = true;
        try
        {
            var state = await _quickSettingsService.GetStateAsync();

            // Don't fight the user while they are dragging the slider.
            if (!VolumeSlider.IsMouseCaptureWithin)
            {
                _updatingVolumeSlider = true;
                VolumeSlider.Value = state.Volume;
                _updatingVolumeSlider = false;
                SetText(VolumeValueText, $"{state.Volume:P0}{(state.IsMuted ? " (muted)" : string.Empty)}");
            }

            SetText(BatteryValueText, state.BatteryPercent < 0 ? "Unknown" : $"{state.BatteryPercent}%");
            SetText(CompactBatteryPercentText, state.BatteryPercent < 0 ? "--%" : $"{state.BatteryPercent}%");
            _lastBatteryPercent = state.BatteryPercent;
            SetCompactBatteryRing(state.BatteryPercent);
            SetIdleBatteryEdge(state.BatteryPercent);
            CompactChargingIcon.Visibility = state.IsPluggedIn ? Visibility.Visible : Visibility.Collapsed;
            if (_lastCharging is bool previousCharging && previousCharging != state.IsPluggedIn)
            {
                ShowNodeNotification(state.IsPluggedIn ? "Charging" : "Not charging");
            }
            _lastCharging = state.IsPluggedIn;
            NotifyWirelessChanges(state);
            NotifyLowBattery(state);
            SetText(PowerStatusText, state.IsPluggedIn ? "Plugged in" : "On battery");
            SetText(WiFiStatusText, state.WiFiStatus);
            SetText(BluetoothStatusText, state.BluetoothStatus);
            if (!BrightnessSlider.IsMouseCaptureWithin && state.BrightnessPercent >= 0)
            {
                BrightnessSlider.Value = state.BrightnessPercent;
            }

            SetText(BrightnessValueText, state.BrightnessPercent < 0 ? "--" : $"{state.BrightnessPercent}%");
            SetText(SettingsStatusText, state.BrightnessPercent < 0
                ? "Brightness is unavailable for this display"
                : "Click Wi-Fi or Bluetooth to toggle");
        }

        catch (Exception exception) when (exception is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            _updatingVolumeSlider = false;
            SetText(SettingsStatusText, $"Quick Settings unavailable: {exception.Message}");
        }
        finally
        {
            _refreshingSettings = false;
        }
    }

    private void SetCompactBatteryRing(int percent)
    {
        var progress = Math.Clamp(percent, 0, 100) / 100d;
        const double center = 11;
        const double radius = 9.5;
        if (progress <= 0)
        {
            CompactBatteryRing.Data = null;
            return;
        }

        if (progress >= 0.999)
        {
            CompactBatteryRing.Data = new EllipseGeometry(new System.Windows.Point(center, center), radius, radius);
            return;
        }

        var angle = progress * Math.PI * 2 - Math.PI / 2;
        var end = new System.Windows.Point(center + radius * Math.Cos(angle), center + radius * Math.Sin(angle));
        var figure = new PathFigure
        {
            StartPoint = new System.Windows.Point(center, center - radius),
            Segments =
            [
                new ArcSegment(end, new System.Windows.Size(radius, radius), 0, progress > 0.5,
                    SweepDirection.Clockwise, true)
            ]
        };
        CompactBatteryRing.Data = new PathGeometry([figure]);
    }

    private void SetIdleBatteryEdge(int percent)
    {
        // Fixed notch size: independent from the island's animated size, so never clipped.
        // The stroke is centred on a path inset by half the thickness so it stays fully
        // inside the notch bounds.
        const double inset = IdleBatteryStroke / 2;
        const double left = inset;
        const double right = IdleNotchWidth - inset;
        const double top = 0;                       // flush with the screen edge (no top line)
        const double bottom = IdleNotchHeight - inset;
        const double r = IdleNotchRadius - inset;   // follows the notch's bottom radius
        var size = new System.Windows.Size(r, r);

        IdleBatteryTrack.Width = IdleBatteryEdge.Width = IdleNotchWidth;
        IdleBatteryTrack.Height = IdleBatteryEdge.Height = IdleNotchHeight;

        // Track: left side -> bottom-left curve -> bottom line -> bottom-right curve -> right side.
        var track = new PathFigure { StartPoint = new System.Windows.Point(left, top), IsClosed = false };
        track.Segments.Add(new LineSegment(new System.Windows.Point(left, bottom - r), true));
        track.Segments.Add(new ArcSegment(new System.Windows.Point(left + r, bottom), size, 0, false, SweepDirection.Counterclockwise, true));
        track.Segments.Add(new LineSegment(new System.Windows.Point(right - r, bottom), true));
        track.Segments.Add(new ArcSegment(new System.Windows.Point(right, bottom - r), size, 0, false, SweepDirection.Counterclockwise, true));
        track.Segments.Add(new LineSegment(new System.Windows.Point(right, top), true));
        var trackGeometry = new PathGeometry();
        trackGeometry.Figures.Add(track);
        trackGeometry.Freeze();
        IdleBatteryTrack.Data = trackGeometry;

        // Progress runs along the WHOLE outline, left -> right:
        // left side, bottom-left curve, bottom line, bottom-right curve, right side.
        if (percent < 0)
        {
            IdleBatteryEdge.Data = Geometry.Empty;
            return;
        }

        var progress = Math.Clamp(percent, 0, 100) / 100d;
        var sideLength = bottom - r - top;
        var arcLength = Math.PI * r / 2;
        var lineLength = (right - r) - (left + r);
        var totalLength = sideLength * 2 + arcLength * 2 + lineLength;
        var remaining = totalLength * progress;
        if (remaining <= 0.01)
        {
            IdleBatteryEdge.Data = Geometry.Empty;
            return;
        }

        var figure = new PathFigure { StartPoint = new System.Windows.Point(left, top), IsClosed = false };

        // 1) left side (top -> down)
        var take = Math.Min(remaining, sideLength);
        figure.Segments.Add(new LineSegment(new System.Windows.Point(left, top + take), true));
        remaining -= take;

        // 2) bottom-left curve (9 o'clock -> 6 o'clock)
        if (remaining > 0)
        {
            take = Math.Min(remaining, arcLength);
            var t = take / arcLength;
            var angle = (180 - 90 * t) * Math.PI / 180;
            var cx = left + r;
            var cy = bottom - r;
            figure.Segments.Add(new ArcSegment(
                new System.Windows.Point(cx + r * Math.Cos(angle), cy + r * Math.Sin(angle)),
                size, 0, false, SweepDirection.Counterclockwise, true));
            remaining -= take;
        }

        // 3) bottom line (left -> right)
        if (remaining > 0)
        {
            take = Math.Min(remaining, lineLength);
            figure.Segments.Add(new LineSegment(new System.Windows.Point(left + r + take, bottom), true));
            remaining -= take;
        }

        // 4) bottom-right curve (6 o'clock -> 3 o'clock)
        if (remaining > 0)
        {
            take = Math.Min(remaining, arcLength);
            var t = take / arcLength;
            var angle = (90 - 90 * t) * Math.PI / 180;
            var cx = right - r;
            var cy = bottom - r;
            figure.Segments.Add(new ArcSegment(
                new System.Windows.Point(cx + r * Math.Cos(angle), cy + r * Math.Sin(angle)),
                size, 0, false, SweepDirection.Counterclockwise, true));
            remaining -= take;
        }

        // 5) right side (bottom -> up)
        if (remaining > 0)
        {
            take = Math.Min(remaining, sideLength);
            figure.Segments.Add(new LineSegment(new System.Windows.Point(right, bottom - r - take), true));
        }

        var progressGeometry = new PathGeometry();
        progressGeometry.Figures.Add(figure);
        progressGeometry.Freeze();
        IdleBatteryEdge.Data = progressGeometry;
    }

    private void NotifyWirelessChanges(QuickSettingsState state)
    {
        if (_lastWiFiStatus is not null && !string.Equals(_lastWiFiStatus, state.WiFiStatus, StringComparison.Ordinal))
        {
            if (state.WiFiStatus.StartsWith("On — ", StringComparison.Ordinal))
            {
                ShowNodeNotification($"Wi-Fi connected to {state.WiFiStatus[5..]}");
            }
            else if (state.WiFiStatus == "On")
            {
                ShowNodeNotification("Wi-Fi on");
            }
            else if (state.WiFiStatus == "Off")
            {
                ShowNodeNotification("Wi-Fi off");
            }
        }
        _lastWiFiStatus = state.WiFiStatus;

        if (_lastBluetoothStatus is not null && !string.Equals(_lastBluetoothStatus, state.BluetoothStatus, StringComparison.Ordinal))
        {
            if (state.BluetoothStatus == "On")
            {
                ShowNodeNotification("Bluetooth on");
            }
            else if (state.BluetoothStatus == "Off")
            {
                ShowNodeNotification("Bluetooth off");
            }
        }
        _lastBluetoothStatus = state.BluetoothStatus;
    }

    private void NotifyLowBattery(QuickSettingsState state)
    {
        if (state.IsPluggedIn || state.BatteryPercent < 0 || state.BatteryPercent > 20)
        {
            _lowBatteryNotified = false;
            return;
        }

        if (!_lowBatteryNotified)
        {
            _lowBatteryNotified = true;
            ShowNodeNotification("Battery low", $"{state.BatteryPercent}% remaining", TimeSpan.FromSeconds(4));
        }
    }

    private async void RadioToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not string tag)
        {
            return;
        }

        if (tag == "WiFi")
        {
            OpenAvailableNetworks();
            return;
        }

        var kind = tag == "WiFi" ? RadioKind.WiFi : RadioKind.Bluetooth;
        try
        {
            SetText(SettingsStatusText, $"Toggling {tag}...");
            await _quickSettingsService.ToggleRadioAsync(kind);
            await RefreshSettingsAsync();
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            SetText(SettingsStatusText, $"{tag} toggle unavailable: {exception.Message}");
        }
    }

    private void OpenAvailableNetworks()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-availablenetworks:",
                UseShellExecute = true
            });
            SetText(SettingsStatusText, "Opening available Wi-Fi networks...");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:network-wifi",
                    UseShellExecute = true
                });
                SetText(SettingsStatusText, "Opening Wi-Fi settings...");
            }
            catch (Exception fallbackException) when (fallbackException is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                SetText(SettingsStatusText, $"Unable to open Wi-Fi discovery: {fallbackException.Message}");
            }
        }
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || !BrightnessSlider.IsMouseCaptureWithin)
        {
            return;
        }

        try
        {
            _quickSettingsService.SetBrightness((int)Math.Round(e.NewValue));
            SetText(BrightnessValueText, $"{(int)Math.Round(e.NewValue)}%");
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or System.Management.ManagementException)
        {
            SetText(SettingsStatusText, $"Brightness unavailable: {exception.Message}");
        }
    }

    private void SystemStateTimer_Tick(object? sender, EventArgs e)
    {
        var capsLock = (GetKeyState(0x14) & 1) == 1;
        if (_lastCapsLock is bool previousCapsLock && previousCapsLock != capsLock)
        {
            ShowNodeNotification(capsLock ? "Caps Lock On" : "Caps Lock Off");
        }

        _lastCapsLock = capsLock;
    }

    private void ShowNodeNotification(string message, TimeSpan? duration = null) =>
        ShowNodeNotification("ISLA", message, duration);

    private void ShowNodeNotification(string appName, string message, TimeSpan? duration = null)
    {
        if (_isExpanded)
        {
            return;
        }

        ExitIdleNotch(animate: false);
        _lastInteractionUtc = DateTime.UtcNow;
        NodeNotificationAppText.Text = appName;
        NodeNotificationText.Text = message;
        IslandSurface.Padding = new Thickness(NotificationHorizontalPadding, 0, NotificationHorizontalPadding, 0);
        CompactContent.Width = NotificationWidth - (NotificationHorizontalPadding * 2);
        CompactContent.Visibility = Visibility.Visible;
        NodeNotificationContent.Visibility = Visibility.Visible;
        NodeNotificationContent.Opacity = 0;
        CompactBatteryContent.Visibility = Visibility.Collapsed;
        ClockText.Visibility = Visibility.Collapsed;
        _nodeNotificationUntil = DateTime.UtcNow.Add(duration ?? TimeSpan.FromSeconds(2.4));
        _nodeNotificationTimer.Start();

        var animation = new DoubleAnimation
        {
            From = IslandSurface.Width,
            To = Math.Max(IslandSurface.Width, NotificationWidth),
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) => IslandSurface.Width = NotificationWidth;
        IslandSurface.BeginAnimation(FrameworkElement.WidthProperty, animation, HandoffBehavior.SnapshotAndReplace);
        var heightAnimation = new DoubleAnimation
        {
            From = IslandSurface.Height,
            To = 58,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        heightAnimation.Completed += (_, _) => IslandSurface.Height = 58;
        IslandSurface.BeginAnimation(FrameworkElement.HeightProperty, heightAnimation, HandoffBehavior.SnapshotAndReplace);
        NodeNotificationContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
    }

    private void NodeNotificationTimer_Tick(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow < _nodeNotificationUntil)
        {
            return;
        }

        _nodeNotificationTimer.Stop();
        NodeNotificationContent.BeginAnimation(OpacityProperty, null);
        NodeNotificationContent.Visibility = Visibility.Collapsed;
        IslandSurface.Padding = new Thickness(CompactHorizontalPadding, 0, CompactHorizontalPadding, 0);
        CompactContent.Width = CompactWidth - (CompactHorizontalPadding * 2);
        CompactBatteryContent.Visibility = Visibility.Visible;
        ClockText.Visibility = Visibility.Visible;
        _lastInteractionUtc = DateTime.UtcNow;

        var animation = new DoubleAnimation
        {
            From = IslandSurface.Width,
            To = CompactWidth,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) => IslandSurface.Width = CompactWidth;
        IslandSurface.BeginAnimation(FrameworkElement.WidthProperty, animation, HandoffBehavior.SnapshotAndReplace);
        var heightAnimation = new DoubleAnimation
        {
            From = IslandSurface.Height,
            To = CompactHeight,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.Stop
        };
        heightAnimation.Completed += (_, _) => IslandSurface.Height = CompactHeight;
        IslandSurface.BeginAnimation(FrameworkElement.HeightProperty, heightAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private async void MediaRefreshTimer_Tick(object? sender, EventArgs e)
    {
        UpdateClock();
        await RefreshMediaAsync();
    }

    private void MediaProgressTimer_Tick(object? sender, EventArgs e)
    {
        if (_lastMediaState is not null)
        {
            UpdateMediaProgressDisplay(_lastMediaState);
        }
    }

    private void UpdateClock()
    {
        SetText(ClockText, DateTime.Now.ToString("HH:mm"));
    }

    private async Task RefreshMediaAsync()
    {
        if (_refreshingMedia)
        {
            return;
        }

        _refreshingMedia = true;
        try
        {
            var state = await _mediaService.GetCurrentStateAsync();
            SyncMediaAnchor(state);
            _lastMediaState = state;
            if (state.PlaybackStatus == "Playing")
            {
                _mediaProgressTimer.Start();
            }
            else
            {
                _mediaProgressTimer.Stop();
            }

            // Track info cross-fades in only when it actually changes.
            SetTextFade(MediaTitleText, state.Title);
            SetTextFade(MediaArtistText, state.Artist);
            SetTextFade(MediaAlbumText, state.Album);

            var status = state.PlaybackStatus == "None"
                ? "No compatible media session detected"
                : state.PlaybackStatus;
            SetText(MediaStatusText, status);
            MediaStatusText.ToolTip = status;

            UpdateMediaProgressDisplay(state);

            SetPlayingIcon(state.PlaybackStatus == "Playing");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException)
        {
            SetText(MediaStatusText, $"Media unavailable: {exception.Message}");
        }
        finally
        {
            _refreshingMedia = false;
        }
    }

    /// <summary>
    /// Re-syncs the local playback clock with what Windows reports. The clock is only moved
    /// when the track/play state changes, the user seeked, or the player is ahead of us.
    /// A stale or slightly-behind reported position is ignored, so the time never jumps
    /// backwards, repeats, or freezes between polls.
    /// </summary>
    private void SyncMediaAnchor(ISLA.Media.MediaState state)
    {
        var now = DateTime.UtcNow;
        var playing = state.PlaybackStatus == "Playing";
        var trackKey = $"{state.Title}|{state.Artist}|{state.Album}|{state.Duration.Ticks}";
        var reported = state.Position;
        if (reported < TimeSpan.Zero)
        {
            reported = TimeSpan.Zero;
        }

        if (state.Duration > TimeSpan.Zero && reported > state.Duration)
        {
            reported = state.Duration;
        }

        var trackChanged = !string.Equals(trackKey, _mediaTrackKey, StringComparison.Ordinal);
        var playingChanged = playing != _mediaAnchorPlaying;

        if (trackChanged || playingChanged || !playing)
        {
            // New song, play/pause toggled, or paused: the reported value is trustworthy.
            _mediaAnchorPosition = reported;
            _mediaAnchorUtc = now;
        }
        else if (reported != _mediaLastReportedPosition)
        {
            // Player published a new position while playing.
            var drift = reported - GetEstimatedMediaPosition(state);
            if (drift > TimeSpan.Zero || drift < -MediaSeekTolerance)
            {
                // Ahead of us, or a real seek backwards: follow the player.
                _mediaAnchorPosition = reported;
                _mediaAnchorUtc = now;
            }
            // Slightly behind: keep our own clock (prevents the time repeating).
        }
        // Unchanged reported value while playing = stale; keep extrapolating locally (no freeze).

        _mediaAnchorPlaying = playing;
        _mediaTrackKey = trackKey;
        _mediaLastReportedPosition = reported;
    }

    private TimeSpan GetEstimatedMediaPosition(ISLA.Media.MediaState state)
    {
        var position = _mediaAnchorPosition;
        if (_mediaAnchorPlaying)
        {
            position += DateTime.UtcNow - _mediaAnchorUtc;
        }

        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        if (state.Duration > TimeSpan.Zero && position > state.Duration)
        {
            position = state.Duration;
        }

        return position;
    }

    private void UpdateMediaProgressDisplay(ISLA.Media.MediaState state)
    {
        var position = GetEstimatedMediaPosition(state);
        var progress = state.Duration > TimeSpan.Zero
            ? Math.Clamp(position.TotalSeconds / state.Duration.TotalSeconds, 0, 1)
            : 0;
        SetMediaProgress(progress);
        SetText(MediaPositionText, FormatTime(position));
        SetText(MediaDurationText, FormatTime(state.Duration));
    }

    /// <summary>
    /// The progress timer ticks every 125 ms with an already-smooth position, so the value is
    /// set directly. (The old 950 ms glide was restarted every tick and made the bar lag.)
    /// </summary>
    private void SetMediaProgress(double progress)
    {
        var property = System.Windows.Controls.ProgressBar.ValueProperty;
        MediaProgress.BeginAnimation(property, null);
        MediaProgress.Value = progress;
    }

    private void SetPlayingIcon(bool isPlaying)
    {
        if (_lastPlaying == isPlaying)
        {
            return;
        }

        _lastPlaying = isPlaying;
        AnimateIcon(PlayIcon, !isPlaying);
        AnimateIcon(PauseIcon, isPlaying);
    }

    private void SetPomodoroIcon(bool isRunning)
    {
        if (_lastPomodoroRunning == isRunning)
        {
            return;
        }

        _lastPomodoroRunning = isRunning;
        AnimateIcon(PomodoroPlayIcon, !isRunning);
        AnimateIcon(PomodoroPauseIcon, isRunning);
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }

    // =================================================================================
    //  Window / hover behaviour
    // =================================================================================

    private void PositionAtTopCenter()
    {
        // The window is a fixed transparent canvas, so this only needs to run once.
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + NormalTopInset;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLongPtr(handle, GwlExstyle).ToInt64();
        extendedStyle = (extendedStyle & ~WsExAppwindow) | WsExToolwindow;
        SetWindowLongPtr(handle, GwlExstyle, new IntPtr(extendedStyle));
    }

    private void Island_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _collapseTimer.Stop();
        _lastInteractionUtc = DateTime.UtcNow;
        if (_isIdleNotch)
        {
            ExitIdleNotch();
            return;
        }

        if (!_isPinned)
        {
            SetExpanded(true);
        }
    }

    private void Island_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isPinned)
        {
            _collapseTimer.Stop();
            _collapseTimer.Interval = TimeSpan.FromMilliseconds(500);
            _collapseTimer.Start();
        }
    }

    private void IdleTimer_Tick(object? sender, EventArgs e)
    {
        if (_isIdleNotch || _isExpanded || NodeNotificationContent.Visibility == Visibility.Visible ||
            DateTime.UtcNow - _lastInteractionUtc < TimeSpan.FromSeconds(30))
        {
            return;
        }

        EnterIdleNotch();
    }

    private void EnterIdleNotch()
    {
        _isIdleNotch = true;
        CompactBatteryContent.Visibility = Visibility.Collapsed;
        // Hidden until the notch has reached its final size (shown in the Completed handler below).
        IdleBatteryEdge.Visibility = Visibility.Collapsed;
        IdleBatteryTrack.Visibility = Visibility.Collapsed;
        CompactChargingIcon.Visibility = Visibility.Collapsed;
        CompactContent.Width = IdleNotchWidth;
        CompactContent.Margin = new Thickness(0);
        ClockText.FontSize = 10;
        // A notch is flush with the top edge: no horizontal inset and no rounded top corners.
        IslandSurface.Padding = new Thickness(0);
        AnimateWindowTop(SystemParameters.WorkArea.Top, TimeSpan.FromMilliseconds(320), CollapseEase);
        IslandSurface.BeginAnimation(WidthProperty,
            MakeAnimation(IdleNotchWidth, TimeSpan.FromMilliseconds(320), CollapseEase), HandoffBehavior.SnapshotAndReplace);
        IslandSurface.BeginAnimation(HeightProperty,
            MakeAnimation(IdleNotchHeight, TimeSpan.FromMilliseconds(320), CollapseEase), HandoffBehavior.SnapshotAndReplace);
        var notchRadiusAnimation = MakeAnimation(IdleNotchRadius, TimeSpan.FromMilliseconds(320), CollapseEase);
        notchRadiusAnimation.Completed += (_, _) =>
        {
            if (_isIdleNotch)
            {
                IslandSurface.CornerRadius = new CornerRadius(0, 0, IdleNotchRadius, IdleNotchRadius);
                SetIdleBatteryEdge(_lastBatteryPercent);
                IdleBatteryTrack.Visibility = Visibility.Visible;
                IdleBatteryEdge.Visibility = Visibility.Visible;
            }
        };
        BeginAnimation(IslandRadiusProperty, notchRadiusAnimation, HandoffBehavior.SnapshotAndReplace);
        Fade(ClockText, 0, 90, 0, FadeEase, () => Fade(ClockText, 1, 150, 30, FadeEase));
    }

    private void ExitIdleNotch(bool animate = true)
    {
        if (!_isIdleNotch)
        {
            return;
        }

        _isIdleNotch = false;
        CompactBatteryContent.Visibility = Visibility.Visible;
        IdleBatteryEdge.Visibility = Visibility.Collapsed;
        IdleBatteryTrack.Visibility = Visibility.Collapsed;
        CompactContent.Width = CompactWidth - 36;
        CompactContent.Margin = new Thickness(0);
        ClockText.FontSize = 13;
        IslandSurface.Padding = new Thickness(18, 0, 18, 0);
        if (!animate)
        {
            IslandSurface.BeginAnimation(WidthProperty, null);
            IslandSurface.BeginAnimation(HeightProperty, null);
            BeginAnimation(IslandRadiusProperty, null);
            IslandSurface.Width = CompactWidth;
            IslandSurface.Height = CompactHeight;
            IslandRadius = CompactRadius;
            Top = SystemParameters.WorkArea.Top + NormalTopInset;
            return;
        }

        AnimateWindowTop(SystemParameters.WorkArea.Top + NormalTopInset,
            TimeSpan.FromMilliseconds(280), ExpandEase);
        IslandSurface.BeginAnimation(WidthProperty,
            MakeAnimation(CompactWidth, TimeSpan.FromMilliseconds(280), ExpandEase), HandoffBehavior.SnapshotAndReplace);
        IslandSurface.BeginAnimation(HeightProperty,
            MakeAnimation(CompactHeight, TimeSpan.FromMilliseconds(280), ExpandEase), HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(IslandRadiusProperty,
            MakeAnimation(CompactRadius, TimeSpan.FromMilliseconds(280), ExpandEase), HandoffBehavior.SnapshotAndReplace);
        Fade(ClockText, 0, 70, 0, FadeEase, () => Fade(ClockText, 1, 140, 60, FadeEase));
    }

    private void AnimateWindowTop(double target, TimeSpan duration, IEasingFunction ease)
    {
        BeginAnimation(TopProperty, null);
        var animation = new DoubleAnimation(Top, target, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) => Top = target;
        BeginAnimation(TopProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        if (!_settingsService.Settings.CloseOnOutsideClick)
        {
            return;
        }

        if (_isPinned)
        {
            _isPinned = false;
        }

        SetExpanded(false);
    }

    private void CollapseTimer_Tick(object? sender, EventArgs e)
    {
        _collapseTimer.Stop();
        if (IslandSurface.IsMouseOver || ExpandedContent.IsMouseOver)
        {
            return;
        }

        SetExpanded(false);
    }

    // =================================================================================
    //  Expand / collapse
    // =================================================================================

    private void SetExpanded(bool expanded)
    {
        if (_isExpanded == expanded)
        {
            return;
        }

        if (expanded)
        {
            ExitIdleNotch(animate: false);
        }

        _isExpanded = expanded;
        var version = ++_expandVersion;

        var targetWidth = expanded ? ExpandedWidth : CompactWidth;
        var targetHeight = expanded ? GetTabHeight(_activeTab) : CompactHeight;
        var targetRadius = expanded ? ExpandedRadius : CompactRadius;
        var duration = TimeSpan.FromMilliseconds(expanded ? 420 : 280);
        var ease = expanded ? ExpandEase : CollapseEase;

        if (expanded)
        {
            Fade(CompactContent, 0, 100, 0, FadeEase);
            // The content is laid out once at its final size; the island simply reveals it.
            ExpandedContent.BeginAnimation(HeightProperty, null);
            ExpandedContent.Height = ExpandedContentHeight;
        }

        // Animations start from the current (possibly mid-flight) value, so interrupting an
        // expand/collapse reverses smoothly instead of snapping.
        IslandSurface.BeginAnimation(WidthProperty, MakeAnimation(targetWidth, duration, ease), HandoffBehavior.SnapshotAndReplace);
        IslandSurface.BeginAnimation(HeightProperty, MakeAnimation(targetHeight, duration, ease), HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(IslandRadiusProperty, MakeAnimation(targetRadius, duration, ease), HandoffBehavior.SnapshotAndReplace);

        if (expanded)
        {
            // ...then the expanded content fades + rises in.
            if (ExpandedContent.Visibility != Visibility.Visible)
            {
                ExpandedContent.BeginAnimation(OpacityProperty, null);
                ExpandedContent.Opacity = 0;
                ExpandedTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                ExpandedTranslate.Y = 10;
                ExpandedContent.Visibility = Visibility.Visible;
            }

            Fade(ExpandedContent, 1, 220, 100, FadeEase);
            Slide(ExpandedTranslate, 0, 420, 40, ContentEase);
        }
        else
        {
            Fade(ExpandedContent, 0, 120, 0, FadeEase, () =>
            {
                if (version == _expandVersion)
                {
                    ExpandedContent.Visibility = Visibility.Collapsed;
                }
            });
            Fade(CompactContent, 1, 130, 140, FadeEase);
            _lastInteractionUtc = DateTime.UtcNow;

        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _isPinned = false;
        SetExpanded(false);
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        _isPinned = false;
        SetExpanded(false);
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _mediaRefreshTimer.Stop();
        _mediaProgressTimer.Stop();
        _settingsRefreshTimer.Stop();
        _pomodoroTimer.Stop();
        _systemStateTimer.Stop();
        _nodeNotificationTimer.Stop();
        _idleTimer.Stop();
        _windowsNotificationPollTimer.Stop();
        _windowsNotificationService.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayIconImage.Dispose();
    }

    // =================================================================================
    //  Media / volume
    // =================================================================================

    private async void MediaControl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not string command)
        {
            return;
        }

        var succeeded = command switch
        {
            "Previous" => await _mediaService.SkipPreviousAsync(),
            "Next" => await _mediaService.SkipNextAsync(),
            "PlayPause" => await _mediaService.TogglePlayPauseAsync(),
            _ => false
        };

        if (!succeeded)
        {
            SetText(MediaStatusText, "The active media session did not accept this command");
            return;
        }

        await RefreshMediaAsync();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingVolumeSlider || !IsLoaded)
        {
            return;
        }

        try
        {
            _quickSettingsService.SetVolume(e.NewValue);
            SetText(VolumeValueText, $"{e.NewValue:P0}");
        }
        catch (COMException exception)
        {
            SetText(SettingsStatusText, $"Volume unavailable: {exception.Message}");
        }
    }

    // =================================================================================
    //  Pomodoro
    // =================================================================================

    private void PomodoroTimer_Tick(object? sender, EventArgs e)
    {
        if (_pomodoroService.Update())
        {
            _pomodoroNotice = "Session complete. Next phase is ready.";
            System.Media.SystemSounds.Exclamation.Play();
        }

        RefreshPomodoro();
    }

    private void RefreshPomodoro()
    {
        var state = _pomodoroService.State;
        SetText(PomodoroTimeText, $"{(int)state.Remaining.TotalMinutes:00}:{state.Remaining.Seconds:00}");
        SetText(PomodoroPhaseText, state.Phase switch
        {
            PomodoroPhase.ShortBreak => "Short break",
            PomodoroPhase.LongBreak => "Long break",
            _ => "Focus session"
        });
        SetText(PomodoroSessionsText, $"Sessions: {state.CompletedFocusSessions}");
        SetPomodoroIcon(state.IsRunning);

        // The "session complete" notice stays visible until the user acts again.
        SetText(PomodoroStatusText, _pomodoroNotice ?? (state.IsRunning ? "Timer running" : "Ready"));
    }

    private void PomodoroStartButton_Click(object sender, RoutedEventArgs e)
    {
        _pomodoroNotice = null;

        if (_pomodoroService.State.IsRunning)
        {
            _pomodoroService.Pause();
        }
        else
        {
            _pomodoroService.StartOrResume();
        }

        RefreshPomodoro();
    }

    private void PomodoroResetButton_Click(object sender, RoutedEventArgs e)
    {
        _pomodoroNotice = null;
        _pomodoroService.Reset();
        RefreshPomodoro();
    }

    // =================================================================================
    //  Tabs
    // =================================================================================

    private void TabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ButtonBase button || button.Tag is not string tab)
        {
            return;
        }

        SwitchTab(tab);
    }

    private void SwitchTab(string tab)
    {
        if (tab == _activeTab)
        {
            return;
        }

        var previous = GetPanel(_activeTab);
        var next = GetPanel(tab);
        _activeTab = tab;
        var version = ++_tabVersion;

        // Highlight pill slides to the new tab.
        DockPillTransform.BeginAnimation(
            TranslateTransform.XProperty,
            MakeAnimation(GetTabIndex(tab) * DockSlotWidth, TimeSpan.FromMilliseconds(360), PillEase),
            HandoffBehavior.SnapshotAndReplace);

        // Island height (and the fixed content height) follow together.
        if (_isExpanded)
        {
            var target = GetTabHeight(tab);
            var duration = TimeSpan.FromMilliseconds(360);
            IslandSurface.BeginAnimation(HeightProperty, MakeAnimation(target, duration, TabEase), HandoffBehavior.SnapshotAndReplace);
        }

        // Anything that is neither leaving nor arriving is hidden right away.
        foreach (var panel in AllPanels())
        {
            if (!ReferenceEquals(panel, previous) && !ReferenceEquals(panel, next))
            {
                HidePanel(panel);
            }
        }

        if (previous.Visibility != Visibility.Visible)
        {
            ShowPanel(next);
            return;
        }

        // Cross-fade: old panel fades out, then the new one fades + rises in.
        Fade(previous, 0, 90, 0, FadeEase, () =>
        {
            if (version != _tabVersion)
            {
                return;
            }

            HidePanel(previous);
            ShowPanel(next);
        });
    }

    private IEnumerable<FrameworkElement> AllPanels()
    {
        yield return MediaPanel;
        yield return SettingsPanel;
        yield return TimerPanel;
    }

    private FrameworkElement GetPanel(string tab) => tab switch
    {
        "Settings" => SettingsPanel,
        "Timer" => TimerPanel,
        _ => MediaPanel
    };

    private static int GetTabIndex(string tab) => tab switch
    {
        "Settings" => 1,
        "Timer" => 2,
        _ => 0
    };

    private static double GetTabHeight(string tab) => tab switch
    {
        "Settings" => SettingsHeight,
        "Timer" => TimerHeight,
        _ => MediaHeight
    };

    private static void ShowPanel(FrameworkElement panel)
    {
        var translate = EnsureTranslate(panel);

        if (panel.Visibility != Visibility.Visible)
        {
            panel.BeginAnimation(OpacityProperty, null);
            panel.Opacity = 0;
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.Y = 8;
            panel.Visibility = Visibility.Visible;
        }

        Fade(panel, 1, 220, 0, FadeEase);
        Slide(translate, 0, 360, 0, ContentEase);
    }

    private static void HidePanel(FrameworkElement panel)
    {
        panel.BeginAnimation(OpacityProperty, null);
        panel.Opacity = 1;
        panel.Visibility = Visibility.Collapsed;
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Normal)
        {
            WindowState = WindowState.Normal;
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int keyCode);

    // =================================================================================
    //  Animation / text helpers
    // =================================================================================

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static DoubleAnimation MakeAnimation(double to, TimeSpan duration, IEasingFunction ease) =>
        new(to, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };

    /// <summary>Fades from the element's current opacity (even mid-animation) to <paramref name="to"/>.</summary>
    private static void Fade(UIElement element, double to, int milliseconds, int delayMilliseconds,
        IEasingFunction ease, Action? onCompleted = null)
    {
        var from = element.Opacity;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = from;

        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMilliseconds),
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };

        if (onCompleted is not null)
        {
            animation.Completed += (_, _) => onCompleted();
        }

        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    /// <summary>Slides a TranslateTransform on Y from where it currently is.</summary>
    private static void Slide(TranslateTransform transform, double to, int milliseconds, int delayMilliseconds,
        IEasingFunction ease)
    {
        var from = transform.Y;
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.Y = from;

        transform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
            {
                BeginTime = TimeSpan.FromMilliseconds(delayMilliseconds),
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            });
    }

    private static TranslateTransform EnsureTranslate(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform existing && !existing.IsFrozen)
        {
            return existing;
        }

        var created = new TranslateTransform();
        element.RenderTransform = created;
        return created;
    }

    /// <summary>Play/pause style icon swap: fade + little scale pop.</summary>
    private static void AnimateIcon(FrameworkElement icon, bool visible)
    {
        var duration = TimeSpan.FromMilliseconds(200);

        icon.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(visible ? 1 : 0, duration) { EasingFunction = ContentEase });

        if (icon.RenderTransform is ScaleTransform scale && !scale.IsFrozen)
        {
            var scaleAnimation = new DoubleAnimation(visible ? 1 : 0.6, duration) { EasingFunction = ContentEase };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
        }
    }

    /// <summary>Only touches the TextBlock when the text really changed (avoids needless re-layout).</summary>
    private static void SetText(TextBlock block, string? text)
    {
        text ??= string.Empty;
        if (!string.Equals(block.Text, text, StringComparison.Ordinal))
        {
            block.Text = text;
        }
    }

    /// <summary>Like <see cref="SetText"/> but with a short fade-in when the text changes.</summary>
    private static void SetTextFade(TextBlock block, string? text)
    {
        text ??= string.Empty;
        if (string.Equals(block.Text, text, StringComparison.Ordinal))
        {
            return;
        }

        block.Text = text;
        block.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = FadeEase });
    }
}