using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Windows.Forms;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Interop;
using Windows.Devices.Radios;
using ISLA.Infrastructure;
using ISLA.Media;
using ISLA.Notifications;
using ISLA.Pomodoro;
using ISLA.QuickSettings;
using ISLA.Settings;
using ISLA.Weather;

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
    // Single source of truth. MainWindow.xaml mirrors the initial sizes:
    // IslandSurface 180x30, CompactContent 170, NodeNotificationContent 160.
    private const double CompactWidth = 180;
    private const double CompactHeight = 30;
    // Notification pill: starts at the compact width and grows with the text, but never
    // wider than ~20% more than the old fixed banner (306 px).
    private const double NotificationMinWidth = CompactWidth;
    private const double NotificationMaxWidth = 368;
    private const double NotificationHorizontalPadding = 10;
    private const double NotificationIconSize = 18;
    private const double NotificationIconGap = 7;
    private const double NotificationTextGap = 7;
    private const double NotificationAppMaxWidth = 150;
    private const double CompactHorizontalPadding = 5;
    private const double IdleNotchWidth = CompactWidth * 0.3;
    // Wider notch used while media plays: clock on the left, spectrum on the right.
    private const double IdleMusicNotchWidth = 82;
    private const double IdlePomodoroNotchWidth = 72;   // tomato + clock
    private const double IdleBothNotchWidth = 104;      // tomato + clock + spectrum
    private const double IdleNotchHeight = CompactHeight * 0.5;
    // Bottom corner radius of the idle notch (same value used for IslandSurface.CornerRadius).
    private const double IdleNotchRadius = IdleNotchHeight / 2;
    // Top corners of the idle notch are concave: they flare outward into the screen edge
    // instead of rounding inward like a floating pill. Must match the flare geometry below.
    private const double IdleNotchFlareRadius = 7;
    private const double IdleBatteryStroke = 1;
    private const double NormalTopInset = 6;
    private const double ExpandedWidth = 520;
    // 244 px of menu content (the old 280 px minus the 36 px header) + an 8 px top inset.
    private const double MediaHeight = 252;
    private const double TimerHeight = 252;
    private const double WeatherHeight = 252;
    private const double SettingsHeight = MediaHeight;
    private const double ExpandedContentHeight = 252;
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
            var radius = (double)e.NewValue;
            // Once the notch has attached to the screen edge, its flare wings carry the
            // silhouette outward, so the top corners stay square and only the bottom
            // corners animate. Until then the pill keeps its rounded top while it shrinks.
            var flared = window._isIdleNotch &&
                window.IdleNotchFlareLayer.Visibility == Visibility.Visible;
            window.IslandSurface.CornerRadius = flared
                ? new CornerRadius(0, 0, radius, radius)
                : new CornerRadius(radius);
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
    private readonly WeatherService _weatherService;
    private readonly DispatcherTimer _settingsRefreshTimer;
    private readonly DispatcherTimer _pomodoroTimer;
    private readonly DispatcherTimer _weatherRefreshTimer;
    private readonly DispatcherTimer _systemStateTimer;
    private readonly DispatcherTimer _nodeNotificationTimer;
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
    private bool _refreshingWeather;
    private int _expandVersion;
    private int _tabVersion;
    private bool? _lastPlaying;
    private bool? _lastPomodoroRunning;

    // Pomodoro indicator (compact island + idle notch), duration chips and focus mode
    private sealed record DurationPick(PomodoroPhase Phase, int Minutes);
    private static readonly int[] FocusPresets = [15, 25, 30, 45, 50];
    private static readonly int[] ShortBreakPresets = [3, 5, 10];
    private static readonly int[] LongBreakPresets = [10, 15, 20, 30];
    private bool _pomodoroActive;
    private double _lastPomodoroFraction = -1;
    private ScaleTransform? _compactTomatoPulse;
    private ScaleTransform? _idleTomatoPulse;
    private FrameworkElement? _compactTomatoView;
    private FrameworkElement? _compactCupView;
    private FrameworkElement? _idleTomatoView;
    private FrameworkElement? _idleCupView;
    private FrameworkElement[]? _compactSteam;
    private FrameworkElement[]? _idleSteam;
    private bool _pomodoroIsBreak;
    private static readonly System.Windows.Media.Brush PomodoroFocusBrush =
        Frozen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x4D, 0x42)));
    private static readonly System.Windows.Media.Brush PomodoroBreakBrush =
        Frozen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4D, 0xA3, 0xFF)));
    private string _durationChipSignature = string.Empty;
    private PomodoroPhase? _chipsPhase;
    private bool _focusModeHolding;
    private readonly List<string> _heldNotificationApps = new();
    private string? _pomodoroNotice;
    private bool? _lastCapsLock;
    private bool? _lastCharging;
    private string? _lastWiFiStatus;
    private string? _lastBluetoothStatus;
    private bool _lowBatteryNotified;
    private bool _criticalBatteryNotified;
    private bool _fullBatteryNotified;
    private bool _powerStatusUsable;
    private int _lastRemainingMinutes = -2;
    private double _idleNotchWidth = IdleNotchWidth;

    // Notification queue: nothing is dropped or overwritten any more.
    private sealed record QueuedNotification(string App, string Message, byte[]? Icon, TimeSpan Duration, DateTime QueuedUtc);
    private readonly Queue<QueuedNotification> _notificationQueue = new();
    private bool _nodeNotificationActive;
    private DateTime _nodeNotificationShownUtc;
    private const int MaxQueuedNotifications = 8;
    private static readonly TimeSpan MinNotificationTime = TimeSpan.FromMilliseconds(1600);
    private static readonly TimeSpan NotificationMaxAge = TimeSpan.FromMinutes(2);

    // Media: event-driven refresh, seeking, album art
    private readonly MediaWatcher _mediaWatcher = new();
    private readonly DispatcherTimer _mediaDebounceTimer;
    private bool _mediaRefreshPending;
    private bool _seeking;
    private double _seekRatio;
    private string? _coverKey;
    private int _coverAttempts;
    private bool _coverLoaded;
    private bool _coverLoading;
    private System.Windows.Media.ImageSource? _islaNotificationIcon;

    // Wi-Fi / Bluetooth detail panel
    private readonly WirelessListService _wirelessService = new();
    private bool _radioViewOpen;
    private string _radioTag = "WiFi";
    private int _radioLoadVersion;
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
    private int _clockMinuteKey = -1;
    private bool _refreshingMedia;
    private bool _refreshingSettings;
    private System.Windows.Media.Brush _normalBatteryBrush = System.Windows.Media.Brushes.White;
    private readonly System.Windows.Media.Brush _lowBatteryBrush = Frozen(new SolidColorBrush(Colors.IndianRed));

    // Battery colour by level: <=20 red, 21-29 yellow, >=30 green.
    private static readonly System.Windows.Media.Brush BatteryRedBrush = Frozen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x45, 0x3A)));
    private static readonly System.Windows.Media.Brush BatteryYellowBrush = Frozen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD6, 0x0A)));
    private static readonly System.Windows.Media.Brush BatteryGreenBrush = Frozen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x38, 0xD4, 0x6A)));

    private static System.Windows.Media.Brush GetBatteryBrush(int percent) =>
        percent < 0 ? BatteryGreenBrush
        : percent <= 20 ? BatteryRedBrush
        : percent < 30 ? BatteryYellowBrush
        : BatteryGreenBrush;

    // Track announcements + mini spectrum
    private string? _announcedTrackKey;
    private bool _spectrumActive;

    public MainWindow()
    {
        InitializeComponent();
        _weatherService = new WeatherService(_settingsService);
        InitializeIdleNotchFlare();
        InitializePomodoroUi();
        _normalBatteryBrush = (System.Windows.Media.Brush)FindResource("IslandAccent");

        _trayIconImage = LoadTrayIcon();
        _trayIcon = CreateTrayIcon();
        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _collapseTimer.Tick += CollapseTimer_Tick;
        _mediaRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _mediaRefreshTimer.Tick += MediaRefreshTimer_Tick;
        _mediaProgressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(125) };
        _mediaProgressTimer.Tick += MediaProgressTimer_Tick;
        _mediaDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _mediaDebounceTimer.Tick += MediaDebounceTimer_Tick;
        _settingsRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _settingsRefreshTimer.Tick += SettingsRefreshTimer_Tick;
        _weatherRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _weatherRefreshTimer.Tick += WeatherRefreshTimer_Tick;
        _pomodoroTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _pomodoroTimer.Tick += PomodoroTimer_Tick;
        _systemStateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _systemStateTimer.Tick += SystemStateTimer_Tick;
        _nodeNotificationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _nodeNotificationTimer.Tick += NodeNotificationTimer_Tick;
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
        _settingsRefreshTimer.Start();
        _weatherRefreshTimer.Start();
        _systemStateTimer.Start();
        _lastInteractionUtc = DateTime.UtcNow;
        _idleTimer.Start();
        CheckBatteryFast();
        Microsoft.Win32.SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
        _ = StartMediaWatcherAsync();
        _windowsNotificationService.NotificationReceived += WindowsNotificationService_NotificationReceived;
        _windowsNotificationService.Error += WindowsNotificationService_Error;
        try
        {
            // The service polls itself (1 s loop) and wakes on NotificationChanged.
            await _windowsNotificationService.StartAsync();
        }
        catch (Exception exception)
        {
            AppLog.Warn("Windows notification listener could not start.", exception);
            System.Windows.MessageBox.Show(
                exception.Message,
                "ISLA notification access",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        await RefreshMediaAsync();
        await RefreshSettingsAsync();
        _ = RefreshWeatherAsync();
        RefreshPomodoro();
    }

    private void WindowsNotificationService_NotificationReceived(
        object? sender,
        WindowsNotification notification)
    {
        var message = string.IsNullOrWhiteSpace(notification.Body)
            ? notification.Title
            : $"{notification.Title}: {notification.Body}";
        Dispatcher.BeginInvoke(() =>
        {
            if (IsFocusModeActive())
            {
                HoldNotification(notification.AppName);
                return;
            }

            ShowNodeNotification(notification.AppName, message, TimeSpan.FromSeconds(4), icon: notification.Icon);
        });
    }

    private void WindowsNotificationService_Error(object? sender, Exception exception)
    {
        AppLog.Error("Windows notification listener error.", exception);
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
        // The hardware panels are only visible in Settings; elsewhere only wireless changes
        // matter, and that path skips COM/WMI entirely.
        if (_isExpanded && _activeTab == "Settings")
        {
            await RefreshSettingsAsync();
            return;
        }

        await RefreshWirelessAsync();
    }

    // =================================================================================
    //  Weather
    // =================================================================================

    private async void WeatherRefreshTimer_Tick(object? sender, EventArgs e)
    {
        // Keep the cache warm so opening the tab is instant.
        await RefreshWeatherAsync();
    }

    private async void WeatherRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshWeatherAsync(forceRefresh: true);
    }

    private async Task RefreshWeatherAsync(bool forceRefresh = false)
    {
        if (_refreshingWeather)
        {
            return;
        }

        _refreshingWeather = true;
        if (forceRefresh)
        {
            SetText(WeatherUpdatedText, "Refreshing...");
        }

        try
        {
            var state = await _weatherService.GetWeatherAsync(forceRefresh);
            UpdateWeatherUi(state);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                              JsonException or InvalidOperationException or KeyNotFoundException)
        {
            AppLog.Warn("Weather refresh failed.", exception);
            if (_weatherService.Cached is null)
            {
                SetText(WeatherLocationText, "Weather unavailable");
                SetText(WeatherTemperatureText, "--°");
                SetText(WeatherDescriptionText, "Could not load the forecast");
                SetText(WeatherDetailText, "Check your internet connection and try again");
                SetText(WeatherUpdatedText, string.Empty);
            }
            else
            {
                SetText(WeatherUpdatedText, "Refresh failed");
            }
        }
        finally
        {
            _refreshingWeather = false;
        }
    }

    private void UpdateWeatherUi(WeatherState state)
    {
        var location = string.IsNullOrWhiteSpace(state.Country)
            ? state.City
            : $"{state.City}, {state.Country}";

        SetText(WeatherLocationText, location);
        SetText(WeatherTemperatureText, $"{Math.Round(state.Temperature)}°");
        SetText(WeatherDescriptionText, state.Description);
        SetText(
            WeatherDetailText,
            $"Feels like {Math.Round(state.ApparentTemperature)}°  ·  Humidity {state.Humidity}%  ·  Wind {Math.Round(state.WindSpeed)} km/h");

        WeatherIconPath.Data = FindWeatherIcon(state.IconKey) ?? WeatherIconPath.Data;

        var dayViews = new (TextBlock Label, System.Windows.Shapes.Path Icon, TextBlock Temperature)[]
        {
            (WeatherDay0Label, WeatherDay0Icon, WeatherDay0Temp),
            (WeatherDay1Label, WeatherDay1Icon, WeatherDay1Temp),
            (WeatherDay2Label, WeatherDay2Icon, WeatherDay2Temp),
            (WeatherDay3Label, WeatherDay3Icon, WeatherDay3Temp)
        };

        for (var index = 0; index < dayViews.Length; index++)
        {
            var view = dayViews[index];
            if (index >= state.Days.Count)
            {
                SetText(view.Label, "---");
                SetText(view.Temperature, "--° / --°");
                continue;
            }

            var day = state.Days[index];
            SetText(view.Label, index == 0 ? "Today" : day.DayLabel);
            view.Icon.Data = FindWeatherIcon(day.IconKey) ?? view.Icon.Data;
            SetText(view.Temperature, $"{Math.Round(day.Max)}° / {Math.Round(day.Min)}°");
        }

        SetText(WeatherUpdatedText, $"Updated {state.UpdatedAt:HH:mm}");
    }

    private Geometry? FindWeatherIcon(string key) =>
        TryFindResource($"WeatherIcon{key}") as Geometry;

    private async Task RefreshWirelessAsync()
    {
        if (_refreshingSettings)
        {
            return;
        }

        _refreshingSettings = true;
        try
        {
            var state = await _quickSettingsService.GetStateAsync(includeHardware: false);
            NotifyWirelessChanges(state);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Wireless change notifications are best effort.
        }
        finally
        {
            _refreshingSettings = false;
        }
    }

    private DateTime _lastSettingsRefreshUtc = DateTime.MinValue;

    /// <summary>Refresh when opening the island / Settings tab, but not on every hover.</summary>
    private void RefreshSettingsIfStale()
    {
        if (DateTime.UtcNow - _lastSettingsRefreshUtc > TimeSpan.FromSeconds(3))
        {
            _ = RefreshSettingsAsync();
        }
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
            _lastSettingsRefreshUtc = DateTime.UtcNow;

            // Don't fight the user while they are dragging the slider.
            if (!VolumeSlider.IsMouseCaptureWithin)
            {
                if (state.Volume is double volume)
                {
                    _updatingVolumeSlider = true;
                    VolumeSlider.Value = volume;
                    _updatingVolumeSlider = false;
                    SetText(VolumeValueText, $"{volume:P0}{(state.IsMuted ? " (muted)" : string.Empty)}");
                }
                else
                {
                    // No audio output device: keep the slider where it is and show "--".
                    SetText(VolumeValueText, "--");
                }
            }

            // The fast power poll owns the battery state; this is only the fallback when
            // Windows reports no usable battery information.
            if (!_powerStatusUsable)
            {
                ApplyBatteryState(state.BatteryPercent, state.IsPluggedIn,
                    state.IsPluggedIn ? "Plugged in" : "On battery");
            }

            NotifyWirelessChanges(state);
            SetText(WiFiStatusText, state.WiFiStatus);
            SetText(BluetoothStatusText, state.BluetoothStatus);
            if (!BrightnessSlider.IsMouseCaptureWithin && state.BrightnessPercent >= 0)
            {
                BrightnessSlider.Value = state.BrightnessPercent;
            }

            SetText(BrightnessValueText, state.BrightnessPercent < 0 ? "--" : $"{state.BrightnessPercent}%");
            SetText(SettingsStatusText, state.BrightnessPercent < 0
                ? "Brightness is unavailable for this display"
                : "Tap Wi-Fi or Bluetooth to see details");
            UpdateRadioPanel();
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
        CompactBatteryRing.Stroke = GetBatteryBrush(percent);
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
        // The stroke is inset by half the thickness so the body outline stays fully inside
        // the notch; the top corners then flare outward into the screen edge.
        var brush = GetBatteryBrush(percent);
        IdleBatteryEdge.Stroke = brush;
        // The unfilled part of the ring keeps the same colour, only faded.
        IdleBatteryTrack.Stroke = brush;
        IdleBatteryTrack.Opacity = 0.35;

        const double inset = IdleBatteryStroke / 2;
        const double left = inset;
        var right = _idleNotchWidth - inset;
        const double bottom = IdleNotchHeight - inset;
        var radius = IdleNotchRadius - inset;
        var bottomArcSize = new System.Windows.Size(radius, radius);

        // Concave top corners: the outline begins on the screen edge a little outside the
        // body and curves down into the vertical side, so the notch widens toward the screen
        // edge (flare) instead of narrowing (rounded pill corner).
        var flare = IdleNotchFlareRadius;
        var flareRadius = flare + inset;
        var flareSideY = flare;
        var flareTopX = flare - (2 * Math.Sqrt(flare * inset));
        var flareAngle = Math.Asin((flare - inset) / flareRadius);
        var flareArcSize = new System.Windows.Size(flareRadius, flareRadius);
        var leftFlareCenter = new System.Windows.Point(-flare, flare);
        var rightFlareCenter = new System.Windows.Point(_idleNotchWidth + flare, flare);

        IdleBatteryTrack.Width = IdleBatteryEdge.Width = _idleNotchWidth;
        IdleBatteryTrack.Height = IdleBatteryEdge.Height = IdleNotchHeight;

        // Track: top-left flare -> left side -> bottom-left curve -> bottom line ->
        // bottom-right curve -> right side -> top-right flare.
        var track = new PathFigure
        {
            StartPoint = new System.Windows.Point(-flareTopX, inset),
            IsClosed = false
        };
        track.Segments.Add(new ArcSegment(
            new System.Windows.Point(left, flareSideY), flareArcSize, 0, false, SweepDirection.Clockwise, true));
        track.Segments.Add(new LineSegment(new System.Windows.Point(left, bottom - radius), true));
        track.Segments.Add(new ArcSegment(
            new System.Windows.Point(left + radius, bottom), bottomArcSize, 0, false, SweepDirection.Counterclockwise, true));
        track.Segments.Add(new LineSegment(new System.Windows.Point(right - radius, bottom), true));
        track.Segments.Add(new ArcSegment(
            new System.Windows.Point(right, bottom - radius), bottomArcSize, 0, false, SweepDirection.Counterclockwise, true));
        track.Segments.Add(new LineSegment(new System.Windows.Point(right, flareSideY), true));
        track.Segments.Add(new ArcSegment(
            new System.Windows.Point(_idleNotchWidth + flareTopX, inset), flareArcSize, 0, false, SweepDirection.Clockwise, true));
        var trackGeometry = new PathGeometry();
        trackGeometry.Figures.Add(track);
        trackGeometry.Freeze();
        IdleBatteryTrack.Data = trackGeometry;

        // Progress runs along the whole visible outline, top-left -> bottom -> top-right.
        if (percent < 0)
        {
            IdleBatteryEdge.Data = Geometry.Empty;
            return;
        }

        var progress = Math.Clamp(percent, 0, 100) / 100d;
        var flareLength = flareRadius * flareAngle;
        var sideLength = Math.Max(0, (bottom - radius) - flareSideY);
        var arcLength = Math.PI * radius / 2;
        var lineLength = (right - radius) - (left + radius);
        var totalLength = (flareLength * 2) + (sideLength * 2) + (arcLength * 2) + lineLength;
        var remaining = totalLength * progress;
        if (remaining <= 0.01)
        {
            IdleBatteryEdge.Data = Geometry.Empty;
            return;
        }

        var figure = new PathFigure
        {
            StartPoint = new System.Windows.Point(-flareTopX, inset),
            IsClosed = false
        };
        var take = remaining;

        // 1) top-left flare (screen edge -> 9 o'clock)
        if (remaining > 0)
        {
            take = Math.Min(remaining, flareLength);
            var angle = -flareAngle + (flareAngle * (take / flareLength));
            figure.Segments.Add(new ArcSegment(
                new System.Windows.Point(
                    leftFlareCenter.X + flareRadius * Math.Cos(angle),
                    leftFlareCenter.Y + flareRadius * Math.Sin(angle)),
                flareArcSize, 0, false, SweepDirection.Clockwise, true));
            remaining -= take;
        }

        // 2) left side (top -> down)
        if (remaining > 0)
        {
            take = Math.Min(remaining, sideLength);
            figure.Segments.Add(new LineSegment(new System.Windows.Point(left, flareSideY + take), true));
            remaining -= take;
        }

        // 3) bottom-left curve (9 o'clock -> 6 o'clock)
        if (remaining > 0)
        {
            take = Math.Min(remaining, arcLength);
            var t = take / arcLength;
            var angle = (180 - 90 * t) * Math.PI / 180;
            var cx = left + radius;
            var cy = bottom - radius;
            figure.Segments.Add(new ArcSegment(
                new System.Windows.Point(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle)),
                bottomArcSize, 0, false, SweepDirection.Counterclockwise, true));
            remaining -= take;
        }

        // 4) bottom line (left -> right)
        if (remaining > 0)
        {
            take = Math.Min(remaining, lineLength);
            figure.Segments.Add(new LineSegment(new System.Windows.Point(left + radius + take, bottom), true));
            remaining -= take;
        }

        // 5) bottom-right curve (6 o'clock -> 3 o'clock)
        if (remaining > 0)
        {
            take = Math.Min(remaining, arcLength);
            var t = take / arcLength;
            var angle = (90 - 90 * t) * Math.PI / 180;
            var cx = right - radius;
            var cy = bottom - radius;
            figure.Segments.Add(new ArcSegment(
                new System.Windows.Point(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle)),
                bottomArcSize, 0, false, SweepDirection.Counterclockwise, true));
            remaining -= take;
        }

        // 6) right side (bottom -> up)
        if (remaining > 0)
        {
            take = Math.Min(remaining, sideLength);
            figure.Segments.Add(new LineSegment(new System.Windows.Point(right, bottom - radius - take), true));
            remaining -= take;
        }

        // 7) top-right flare (3 o'clock -> screen edge)
        if (remaining > 0)
        {
            take = Math.Min(remaining, flareLength);
            var angle = Math.PI + (flareAngle * (take / flareLength));
            figure.Segments.Add(new ArcSegment(
                new System.Windows.Point(
                    rightFlareCenter.X + flareRadius * Math.Cos(angle),
                    rightFlareCenter.Y + flareRadius * Math.Sin(angle)),
                flareArcSize, 0, false, SweepDirection.Clockwise, true));
        }

        var progressGeometry = new PathGeometry();
        progressGeometry.Figures.Add(figure);
        progressGeometry.Freeze();
        IdleBatteryEdge.Data = progressGeometry;
    }

    /// <summary>
    /// Builds the two concave corner wings that join the idle notch to the screen edge.
    /// They are anchored to the island's left/right edges, so they follow the width animation.
    /// </summary>
    private void InitializeIdleNotchFlare()
    {
        var radius = IdleNotchFlareRadius;
        // Overlap the wing 1 px into the body so the anti-aliased edges cannot leave a
        // faint seam where the two fills meet (the overlap paints over the body).
        var overlap = 1d;

        // Left wing: filled area between the outward arc and the body's top-left corner.
        var left = new PathFigure
        {
            StartPoint = new System.Windows.Point(radius, radius),
            IsClosed = true
        };
        left.Segments.Add(new ArcSegment(
            new System.Windows.Point(0, 0), new System.Windows.Size(radius, radius),
            0, false, SweepDirection.Counterclockwise, true));
        left.Segments.Add(new LineSegment(new System.Windows.Point(radius + overlap, 0), true));
        left.Segments.Add(new LineSegment(new System.Windows.Point(radius + overlap, radius), true));
        var leftGeometry = new PathGeometry();
        leftGeometry.Figures.Add(left);
        leftGeometry.Freeze();
        IdleNotchFlareLeft.Width = IdleNotchFlareLeft.Height = radius;
        IdleNotchFlareLeft.Margin = new Thickness(-radius, 0, 0, 0);
        IdleNotchFlareLeft.Data = leftGeometry;

        // Right wing: mirrored.
        var right = new PathFigure
        {
            StartPoint = new System.Windows.Point(0, radius),
            IsClosed = true
        };
        right.Segments.Add(new ArcSegment(
            new System.Windows.Point(radius, 0), new System.Windows.Size(radius, radius),
            0, false, SweepDirection.Clockwise, true));
        right.Segments.Add(new LineSegment(new System.Windows.Point(-overlap, 0), true));
        right.Segments.Add(new LineSegment(new System.Windows.Point(-overlap, radius), true));
        var rightGeometry = new PathGeometry();
        rightGeometry.Figures.Add(right);
        rightGeometry.Freeze();
        IdleNotchFlareRight.Width = IdleNotchFlareRight.Height = radius;
        IdleNotchFlareRight.Margin = new Thickness(0, 0, -radius, 0);
        IdleNotchFlareRight.Data = rightGeometry;
    }

    private void SetIdleNotchFlareVisibility(Visibility visibility)
    {
        IdleNotchFlareLayer.Visibility = visibility;
    }

    private void NotifyWirelessChanges(QuickSettingsState state)
    {
        var wifiConnected = IsWifiConnected(state.WiFiStatus);
        var wifiSsid = GetWifiSsid(state.WiFiStatus);
        if (_lastWiFiStatus is not null)
        {
            var wasConnected = IsWifiConnected(_lastWiFiStatus);
            var previousSsid = GetWifiSsid(_lastWiFiStatus);

            if (wifiConnected != wasConnected)
            {
                if (wifiConnected)
                {
                    ShowNodeNotification(
                        wifiSsid is null ? "Wi-Fi on" : $"Wi-Fi connected to {wifiSsid}",
                        dropIfExpanded: true);
                }
                else if (state.WiFiStatus == "Off")
                {
                    ShowNodeNotification("Wi-Fi off", dropIfExpanded: true);
                }
            }
            else if (wifiConnected &&
                     wifiSsid is not null &&
                     previousSsid is not null &&
                     !string.Equals(previousSsid, wifiSsid, StringComparison.Ordinal))
            {
                // Roamed to another network while staying connected.
                ShowNodeNotification($"Wi-Fi connected to {wifiSsid}", dropIfExpanded: true);
            }
        }
        _lastWiFiStatus = state.WiFiStatus;

        if (_lastBluetoothStatus is not null && !string.Equals(_lastBluetoothStatus, state.BluetoothStatus, StringComparison.Ordinal))
        {
            if (state.BluetoothStatus == "On")
            {
                ShowNodeNotification("Bluetooth on", dropIfExpanded: true);
            }
            else if (state.BluetoothStatus == "Off")
            {
                ShowNodeNotification("Bluetooth off", dropIfExpanded: true);
            }
        }
        _lastBluetoothStatus = state.BluetoothStatus;
    }

    private static bool IsWifiConnected(string status) =>
        status == "On" || status.StartsWith("On — ", StringComparison.Ordinal);

    private static string? GetWifiSsid(string status) =>
        status.StartsWith("On — ", StringComparison.Ordinal) ? status[5..] : null;

    // =================================================================================
    //  Battery (fast path: reacts within ~0.5 s of plugging/unplugging)
    // =================================================================================

    private void SystemEvents_PowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            CheckBatteryFast();
            if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            {
                await RefreshSettingsAsync();
                await RefreshMediaAsync();
            }
        });
    }

    /// <summary>
    /// Cheap (a single GetSystemPowerStatus call), so it runs on the 500 ms system timer.
    /// UI work only happens when the level, charger state or remaining time actually changed.
    /// </summary>
    private void CheckBatteryFast()
    {
        try
        {
            var power = SystemInformation.PowerStatus;
            var flags = power.BatteryChargeStatus;
            var usable = !flags.HasFlag(BatteryChargeStatus.NoSystemBattery) && flags != BatteryChargeStatus.Unknown;
            _powerStatusUsable = usable;
            if (!usable)
            {
                return;
            }

            var percent = (int)Math.Round(Math.Clamp(power.BatteryLifePercent, 0f, 1f) * 100);
            var plugged = power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
            var charging = flags.HasFlag(BatteryChargeStatus.Charging);
            var seconds = power.BatteryLifeRemaining;
            // Rounded to 5 minutes so the "time left" text doesn't flicker.
            var remainingMinutes = !plugged && seconds > 0 ? (int)Math.Round(seconds / 60.0 / 5) * 5 : -1;

            if (percent == _lastBatteryPercent && _lastCharging == plugged && remainingMinutes == _lastRemainingMinutes)
            {
                return;
            }

            _lastRemainingMinutes = remainingMinutes;
            string status;
            if (plugged)
            {
                status = percent >= 100 ? "Fully charged" : charging ? "Charging" : "Plugged in, not charging";
            }
            else
            {
                status = remainingMinutes > 0 ? $"On battery · {FormatRemaining(remainingMinutes)} left" : "On battery";
            }

            ApplyBatteryState(percent, plugged, status);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _powerStatusUsable = false;
        }
    }

    private static string FormatRemaining(int minutes) =>
        minutes >= 60 ? $"{minutes / 60}h {minutes % 60:00}m" : $"{minutes}m";

    /// <summary>Single place that updates every battery readout (island, notch, settings card).</summary>
    private void ApplyBatteryState(int percent, bool plugged, string statusText)
    {
        var firstRun = _lastCharging is null;
        var levelChanged = firstRun || percent != _lastBatteryPercent;
        var plugChanged = !firstRun && _lastCharging != plugged;

        if (levelChanged)
        {
            _lastBatteryPercent = percent;
            var text = percent < 0 ? "--%" : $"{percent}%";
            SetText(BatteryValueText, percent < 0 ? "Unknown" : text);
            SetText(CompactBatteryPercentText, text);
            SetCompactBatteryRing(percent);
            SetIdleBatteryEdge(percent);
            BatteryCardIcon.Stroke = GetBatteryBrush(percent);
        }

        if (firstRun || plugChanged)
        {
            CompactChargingIcon.Visibility = plugged ? Visibility.Visible : Visibility.Collapsed;
        }

        SetText(PowerStatusText, statusText);
        _lastCharging = plugged;

        if (plugChanged)
        {
            if (percent >= 0)
            {
                ShowNodeNotification(plugged ? "Charging" : "On battery", $"{percent}%", dropIfExpanded: true);
            }
            else
            {
                ShowNodeNotification(plugged ? "Charging" : "Not charging", dropIfExpanded: true);
            }
        }

        NotifyBatteryLevels(percent, plugged, firstRun);
    }

    private void NotifyBatteryLevels(int percent, bool plugged, bool firstRun)
    {
        if (percent < 0)
        {
            return;
        }

        if (plugged || percent > 20)
        {
            _lowBatteryNotified = false;
        }

        if (plugged || percent > 10)
        {
            _criticalBatteryNotified = false;
        }

        if (!plugged || percent < 95)
        {
            _fullBatteryNotified = false;
        }

        if (plugged)
        {
            if (percent >= 100 && !_fullBatteryNotified)
            {
                _fullBatteryNotified = true;
                if (!firstRun)
                {
                    ShowNodeNotification("Fully charged", "Battery is at 100%, you can unplug", TimeSpan.FromSeconds(4));
                }
            }

            return;
        }

        if (percent <= 10)
        {
            if (!_criticalBatteryNotified)
            {
                _criticalBatteryNotified = true;
                _lowBatteryNotified = true;
                ShowNodeNotification("Battery critical", $"{percent}% left, plug in now", TimeSpan.FromSeconds(5));
            }
        }
        else if (percent <= 20 && !_lowBatteryNotified)
        {
            _lowBatteryNotified = true;
            ShowNodeNotification("Battery low", $"{percent}% remaining", TimeSpan.FromSeconds(4));
        }
    }

    // =================================================================================
    //  Wi-Fi / Bluetooth detail panel (tap a card in Quick Settings)
    // =================================================================================

    private void RadioToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not string tag)
        {
            return;
        }

        OpenRadioPanel(tag);
    }

    private void OpenRadioPanel(string tag)
    {
        _radioTag = tag;
        _radioViewOpen = true;
        var isWifi = tag == "WiFi";
        RadioTitleText.Text = isWifi ? "Wi-Fi" : "Bluetooth";
        RadioSettingsLinkText.Text = isWifi ? "Network settings" : "Bluetooth settings";
        RadioList.Children.Clear();
        SetText(RadioSubtitleText, string.Empty);
        UpdateRadioSwitch();

        Fade(SettingsPanel, 0, 90, 0, FadeEase, () =>
        {
            if (!_radioViewOpen)
            {
                return;
            }

            HidePanel(SettingsPanel);
            ShowPanel(RadioPanel);
        });
        _ = RefreshRadioListAsync();
    }

    private void RadioBack_Click(object sender, RoutedEventArgs e)
    {
        _radioViewOpen = false;
        _radioLoadVersion++;
        Fade(RadioPanel, 0, 90, 0, FadeEase, () =>
        {
            if (_radioViewOpen)
            {
                return;
            }

            HidePanel(RadioPanel);
            ShowPanel(SettingsPanel);
        });
    }

    /// <summary>Instantly returns to the main settings view (used when the island collapses).</summary>
    private void ResetRadioView()
    {
        if (!_radioViewOpen)
        {
            return;
        }

        _radioViewOpen = false;
        _radioLoadVersion++;
        HidePanel(RadioPanel);
        if (_activeTab == "Settings")
        {
            SettingsPanel.BeginAnimation(OpacityProperty, null);
            SettingsPanel.Opacity = 1;
            SettingsPanel.Visibility = Visibility.Visible;
        }
    }

    private void UpdateRadioPanel()
    {
        if (_radioViewOpen)
        {
            UpdateRadioSwitch();
        }
    }

    private bool IsRadioOn()
    {
        var status = _radioTag == "WiFi" ? _lastWiFiStatus : _lastBluetoothStatus;
        return status is not null && status.StartsWith("On", StringComparison.Ordinal);
    }

    private void UpdateRadioSwitch() => RadioSwitch.IsChecked = IsRadioOn();

    private async void RadioSwitch_Click(object sender, RoutedEventArgs e)
    {
        var kind = _radioTag == "WiFi" ? RadioKind.WiFi : RadioKind.Bluetooth;
        RadioSwitch.IsEnabled = false;
        SetText(RadioSubtitleText, RadioSwitch.IsChecked == true ? "Turning on..." : "Turning off...");
        try
        {
            await _quickSettingsService.ToggleRadioAsync(kind);
            await Task.Delay(500); // let the radio settle before reading its state back
            await RefreshSettingsAsync();
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            SetText(RadioSubtitleText, $"Unavailable: {exception.Message}");
        }
        finally
        {
            RadioSwitch.IsEnabled = true;
            UpdateRadioSwitch();
        }

        await RefreshRadioListAsync();
    }

    private async Task RefreshRadioListAsync()
    {
        var version = ++_radioLoadVersion;
        var isWifi = _radioTag == "WiFi";
        UpdateRadioSwitch();

        if (!IsRadioOn())
        {
            RadioList.Children.Clear();
            AddRadioMessage(isWifi ? "Wi-Fi is off" : "Bluetooth is off");
            SetText(RadioSubtitleText, "Turn it on to see what's available");
            return;
        }

        SetText(RadioSubtitleText, isWifi ? "Scanning for networks..." : "Looking for paired devices...");
        if (RadioList.Children.Count == 0)
        {
            AddRadioMessage("Searching...");
        }

        IReadOnlyList<RadioListItem> items;
        string? error = null;
        if (isWifi)
        {
            (items, error) = await _wirelessService.ScanWiFiAsync();
        }
        else
        {
            items = await _wirelessService.GetBluetoothDevicesAsync();
        }

        if (version != _radioLoadVersion || !_radioViewOpen)
        {
            return;
        }

        RadioList.Children.Clear();
        foreach (var item in items)
        {
            RadioList.Children.Add(CreateRadioRow(item, isWifi));
        }

        if (items.Count == 0)
        {
            AddRadioMessage(error ?? (isWifi ? "No networks found" : "No paired devices"));
        }

        UpdateRadioSubtitle(items, isWifi, error);
    }

    private void UpdateRadioSubtitle(IReadOnlyList<RadioListItem> items, bool isWifi, string? error)
    {
        RadioListItem? connected = null;
        foreach (var item in items)
        {
            if (item.IsConnected)
            {
                connected = item;
                break;
            }
        }

        if (connected is not null)
        {
            SetText(RadioSubtitleText, $"Connected to {connected.Name}");
        }
        else if (error is not null)
        {
            SetText(RadioSubtitleText, isWifi ? "Couldn't scan networks" : "Couldn't read devices");
        }
        else
        {
            SetText(RadioSubtitleText, isWifi ? "Not connected" : "No device connected");
        }
    }

    private void AddRadioMessage(string text)
    {
        RadioList.Children.Add(new TextBlock
        {
            Text = text,
            Style = (Style)FindResource("MetadataStyle"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(8, 30, 8, 0)
        });
    }

    private System.Windows.Controls.Button CreateRadioRow(RadioListItem item, bool isWifi)
    {
        var text = (System.Windows.Media.Brush)FindResource("IslandText");
        var secondary = (System.Windows.Media.Brush)FindResource("IslandTextSecondary");
        var muted = (System.Windows.Media.Brush)FindResource("IslandMutedText");
        var accent = (System.Windows.Media.Brush)FindResource("IslandAccent");
        var track = (System.Windows.Media.Brush)FindResource("IslandTrack");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement icon;
        if (isWifi)
        {
            var bars = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Height = 12, Margin = new Thickness(0, 0, 10, 0) };
            for (var i = 1; i <= 4; i++)
            {
                bars.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Width = 2.5,
                    Height = 3 + i * 2.2,
                    RadiusX = 1,
                    RadiusY = 1,
                    Margin = new Thickness(0, 0, 1.5, 0),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Fill = i <= item.Signal ? (item.IsConnected ? accent : text) : track
                });
            }

            icon = bars;
        }
        else
        {
            icon = new System.Windows.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                Margin = new Thickness(3, 0, 13, 0),
                Fill = item.IsConnected ? accent : track
            };
        }

        grid.Children.Add(icon);

        var name = new TextBlock
        {
            Text = item.Name,
            Style = (Style)FindResource("CardLabelStyle"),
            Foreground = item.IsConnected ? text : secondary,
            FontWeight = item.IsConnected ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var detail = new TextBlock
        {
            Text = item.Detail,
            Style = (Style)FindResource("CardHintStyle"),
            Foreground = item.IsConnected ? accent : muted,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(detail, 2);
        grid.Children.Add(detail);

        var button = new System.Windows.Controls.Button
        {
            Style = (Style)FindResource("RadioRowButtonStyle"),
            Content = grid,
            Tag = item,
            ToolTip = item.Name
        };
        button.Click += RadioRow_Click;
        return button;
    }

    private async void RadioRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: RadioListItem item })
        {
            return;
        }

        if (_radioTag != "WiFi")
        {
            // Windows has no public API to connect a paired Bluetooth device: hand over to Settings.
            SetText(RadioSubtitleText, "Opening Bluetooth settings...");
            OpenShellUri("ms-settings:bluetooth");
            return;
        }

        if (item.IsConnected)
        {
            return;
        }

        SetText(RadioSubtitleText, $"Connecting to {item.Name}...");
        var (success, message) = await _wirelessService.ConnectWiFiAsync(item.Id);
        SetText(RadioSubtitleText, message);
        if (!success && message == "Password required")
        {
            OpenShellUri("ms-availablenetworks:");
            return;
        }

        await Task.Delay(600);
        await RefreshSettingsAsync();
        if (_radioViewOpen)
        {
            await RefreshRadioListAsync();
        }
    }

    private void RadioSettingsLink_Click(object sender, RoutedEventArgs e)
    {
        if (_radioTag == "WiFi")
        {
            if (!OpenShellUri("ms-availablenetworks:"))
            {
                OpenShellUri("ms-settings:network-wifi");
            }
        }
        else
        {
            OpenShellUri("ms-settings:bluetooth");
        }
    }

    private bool OpenShellUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SetText(RadioSubtitleText, $"Couldn't open Windows settings: {exception.Message}");
            return false;
        }
    }

    private async void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || !BrightnessSlider.IsMouseCaptureWithin)
        {
            return;
        }

        var value = (int)Math.Round(e.NewValue);
        SetText(BrightnessValueText, $"{value}%");

        try
        {
            // WMI runs on the thread pool, so the slider stays responsive.
            await _quickSettingsService.SetBrightnessAsync(value);
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
            ShowNodeNotification(capsLock ? "Caps Lock On" : "Caps Lock Off", dropIfExpanded: true);
        }

        _lastCapsLock = capsLock;
        CheckBatteryFast();
    }

    private void ShowNodeNotification(string message, TimeSpan? duration = null, bool dropIfExpanded = false) =>
        ShowNodeNotification("ISLA", message, duration, dropIfExpanded);

    /// <summary>
    /// Queues a notification. Nothing is overwritten or lost: they are shown one after another
    /// (while the island is expanded they wait and play once it closes). Identical pending
    /// notifications are merged. <paramref name="dropIfExpanded"/> is for things the person can
    /// already see in the open panel (their own Wi-Fi toggle, Caps Lock...).
    /// </summary>
    private void ShowNodeNotification(string appName, string message, TimeSpan? duration = null, bool dropIfExpanded = false, byte[]? icon = null)
    {
        if (dropIfExpanded && _isExpanded)
        {
            return;
        }

        foreach (var pending in _notificationQueue)
        {
            if (pending.App == appName && pending.Message == message)
            {
                return;
            }
        }

        while (_notificationQueue.Count >= MaxQueuedNotifications)
        {
            _notificationQueue.Dequeue();
        }

        _notificationQueue.Enqueue(new QueuedNotification(
            appName, message, icon, duration ?? TimeSpan.FromSeconds(2.4), DateTime.UtcNow));

        if (_nodeNotificationActive)
        {
            // Someone is waiting: the current one keeps its minimum time, then makes way.
            var now = DateTime.UtcNow;
            var earliest = _nodeNotificationShownUtc + MinNotificationTime;
            var cutoff = earliest > now ? earliest : now;
            if (_nodeNotificationUntil > cutoff)
            {
                _nodeNotificationUntil = cutoff;
            }

            return;
        }

        TryShowNextNotification();
    }

    private void TryShowNextNotification()
    {
        if (_nodeNotificationActive || _isExpanded)
        {
            return;
        }

        while (_notificationQueue.Count > 0)
        {
            var next = _notificationQueue.Dequeue();
            if (DateTime.UtcNow - next.QueuedUtc > NotificationMaxAge)
            {
                continue;
            }

            DisplayNotification(next);
            return;
        }
    }

    private async Task PumpNotificationsAfterCollapseAsync(int version)
    {
        await Task.Delay(450);
        if (version == _expandVersion && !_isExpanded)
        {
            TryShowNextNotification();
        }
    }

    private void DisplayNotification(QueuedNotification notification)
    {
        ExitIdleNotch(animate: false);
        _lastInteractionUtc = DateTime.UtcNow;
        _nodeNotificationActive = true;
        _nodeNotificationShownUtc = DateTime.UtcNow;

        var hasMessage = !string.IsNullOrWhiteSpace(notification.Message);
        NodeNotificationAppText.Text = notification.App;
        NodeNotificationText.Text = notification.Message;
        NodeNotificationText.Visibility = hasMessage ? Visibility.Visible : Visibility.Collapsed;
        ApplyNotificationIcon(notification.Icon, notification.App);

        var targetWidth = GetNotificationWidth(notification.App, hasMessage ? notification.Message : string.Empty);
        var contentWidth = targetWidth - (NotificationHorizontalPadding * 2);
        NodeNotificationContent.Width = contentWidth;
        CompactContent.Width = contentWidth;
        IslandSurface.Padding = new Thickness(NotificationHorizontalPadding, 0, NotificationHorizontalPadding, 0);
        CompactContent.Visibility = Visibility.Visible;
        NodeNotificationContent.Visibility = Visibility.Visible;
        NodeNotificationContent.Opacity = 0;
        CompactBatteryContent.Visibility = Visibility.Collapsed;
        ClockText.Visibility = Visibility.Collapsed;
        _nodeNotificationUntil = DateTime.UtcNow.Add(notification.Duration);
        _nodeNotificationTimer.Start();

        // Only the width animates: the pill stays 30 px tall and the content stays on one line.
        var animation = new DoubleAnimation
        {
            From = IslandSurface.Width,
            To = targetWidth,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) => IslandSurface.Width = targetWidth;
        IslandSurface.BeginAnimation(FrameworkElement.WidthProperty, animation, HandoffBehavior.SnapshotAndReplace);
        NodeNotificationContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
    }

    /// <summary>
    /// Measures the real text so the pill only takes the room it needs: compact width as the
    /// minimum, ~20% above the old banner width as the maximum. Long messages are trimmed.
    /// </summary>
    private double GetNotificationWidth(string app, string message)
    {
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var maxContent = NotificationMaxWidth - (NotificationHorizontalPadding * 2);

        var appWidth = Math.Min(
            MeasureNotificationText(app, NodeNotificationAppText, pixelsPerDip),
            NotificationAppMaxWidth);
        NodeNotificationAppText.MaxWidth = NotificationAppMaxWidth;

        var textGap = string.IsNullOrEmpty(message) ? 0 : NotificationTextGap;
        var availableForMessage = Math.Max(
            0,
            maxContent - NotificationIconSize - NotificationIconGap - appWidth - textGap);
        var messageWidth = Math.Min(
            MeasureNotificationText(message, NodeNotificationText, pixelsPerDip),
            availableForMessage);
        NodeNotificationText.MaxWidth = Math.Max(0, availableForMessage);

        var content = NotificationIconSize + NotificationIconGap + appWidth + textGap + messageWidth;
        return Math.Clamp(
            content + (NotificationHorizontalPadding * 2),
            NotificationMinWidth,
            NotificationMaxWidth);
    }

    private static double MeasureNotificationText(string text, TextBlock reference, double pixelsPerDip)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var typeface = new Typeface(
            reference.FontFamily,
            reference.FontStyle,
            reference.FontWeight,
            reference.FontStretch);
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            typeface,
            reference.FontSize,
            reference.Foreground,
            pixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    /// <summary>App logo in a circle, or a coloured initial when Windows cannot provide one.</summary>
    private void ApplyNotificationIcon(byte[]? iconBytes, string appName)
    {
        if (iconBytes is { Length: > 0 })
        {
            try
            {
                using var stream = new MemoryStream(iconBytes);
                var image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit();
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = (int)(NotificationIconSize * 2);
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                ShowNotificationIcon(image);
                return;
            }
            catch (Exception exception) when (exception is NotSupportedException or IOException or
                                               ArgumentException or FormatException)
            {
                // Fall back to the initial below.
            }
        }

        // ISLA's own banners (Caps Lock, battery, Wi-Fi...) use the app icon.
        if (string.Equals(appName, "ISLA", StringComparison.OrdinalIgnoreCase) &&
            GetIslaNotificationIcon() is { } ownIcon)
        {
            ShowNotificationIcon(ownIcon);
            return;
        }

        NodeNotificationIconImage.Source = null;
        NodeNotificationIconImage.Visibility = Visibility.Collapsed;
        NodeNotificationIconLetter.Text = GetAppInitial(appName);
        NodeNotificationIconLetter.Visibility = Visibility.Visible;
        NodeNotificationIconBackground.Fill = PickNotificationIconBrush(appName);
    }

    private static readonly System.Windows.Media.Brush NotificationIconNeutralBrush =
        Frozen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x40)));

    /// <summary>Fallback circle colours for apps without an accessible logo.</summary>
    private static readonly System.Windows.Media.Brush[] NotificationIconBrushes =
    [
        Frozen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x58, 0x65, 0xF2))),
        Frozen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1D, 0xB9, 0x54))),
        Frozen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x4A))),
        Frozen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4D, 0xA3, 0xFF))),
        Frozen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA8, 0x55, 0xF7))),
        Frozen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B))),
        Frozen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0xB8, 0xA6))),
        Frozen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEC, 0x48, 0x99)))
    ];

    private void ShowNotificationIcon(System.Windows.Media.ImageSource image)
    {
        NodeNotificationIconImage.Source = image;
        NodeNotificationIconImage.Visibility = Visibility.Visible;
        NodeNotificationIconLetter.Visibility = Visibility.Collapsed;
        // Transparent corners of the logo show the neutral circle, not the previous app colour.
        NodeNotificationIconBackground.Fill = NotificationIconNeutralBrush;
    }

    private System.Windows.Media.ImageSource? GetIslaNotificationIcon()
    {
        if (_islaNotificationIcon is not null)
        {
            return _islaNotificationIcon;
        }

        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = (int)(NotificationIconSize * 2);
            image.UriSource = new Uri(Path.Combine(AppContext.BaseDirectory, "Icon.png"), UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            _islaNotificationIcon = image;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or
                                           ArgumentException or FormatException)
        {
            return null;
        }

        return _islaNotificationIcon;
    }

    private static string GetAppInitial(string appName)
    {
        foreach (var character in appName)
        {
            if (char.IsLetterOrDigit(character))
            {
                return char.ToUpperInvariant(character).ToString();
            }
        }

        return "•";
    }

    private static System.Windows.Media.Brush PickNotificationIconBrush(string appName)
    {
        var hash = 0;
        foreach (var character in appName)
        {
            hash = ((hash * 31) + character) & 0x7FFFFFFF;
        }

        return NotificationIconBrushes[hash % NotificationIconBrushes.Length];
    }

    private void NodeNotificationTimer_Tick(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow < _nodeNotificationUntil)
        {
            return;
        }

        // More waiting? Swap straight to the next one: the island stays open, no collapse in between.
        if (_notificationQueue.Count > 0 && !_isExpanded)
        {
            _nodeNotificationActive = false;
            TryShowNextNotification();
            if (_nodeNotificationActive)
            {
                return;
            }
        }

        EndActiveNotification(restoreSize: !_isExpanded);
    }

    private void EndActiveNotification(bool restoreSize)
    {
        _nodeNotificationTimer.Stop();
        _nodeNotificationActive = false;
        NodeNotificationContent.BeginAnimation(OpacityProperty, null);
        NodeNotificationContent.Visibility = Visibility.Collapsed;
        IslandSurface.Padding = new Thickness(CompactHorizontalPadding, 0, CompactHorizontalPadding, 0);
        CompactContent.Width = CompactWidth - (CompactHorizontalPadding * 2);
        CompactBatteryContent.Visibility = Visibility.Visible;
        ClockText.Visibility = Visibility.Visible;
        _lastInteractionUtc = DateTime.UtcNow;

        if (!restoreSize)
        {
            // The island is open: it manages its own size.
            return;
        }

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

    /// <summary>
    /// The 125 ms progress clock only matters while the Media panel is on screen. Keeping it
    /// stopped everywhere else avoids needless string formatting, layout, and render passes.
    /// </summary>
    private void UpdateMediaProgressTimer()
    {
        var shouldRun = _isExpanded &&
                        _activeTab == "Media" &&
                        _lastMediaState?.PlaybackStatus == "Playing";
        if (shouldRun)
        {
            _mediaProgressTimer.Start();
        }
        else
        {
            _mediaProgressTimer.Stop();
        }
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var minuteKey = (now.Hour * 60) + now.Minute;
        if (minuteKey == _clockMinuteKey)
        {
            return;
        }

        _clockMinuteKey = minuteKey;
        SetText(ClockText, now.ToString("HH:mm"));
    }

    private async Task RefreshMediaAsync()
    {
        if (_refreshingMedia)
        {
            _mediaRefreshPending = true;
            return;
        }

        _refreshingMedia = true;
        try
        {
            var state = await _mediaService.GetCurrentStateAsync();
            SyncMediaAnchor(state);
            _lastMediaState = state;

            // Poll quickly while a player is active; fall back to a slower heartbeat otherwise.
            // The MediaWatcher still wakes us immediately on track/session changes.
            _mediaRefreshTimer.Interval = state.PlaybackStatus == "Playing"
                ? TimeSpan.FromSeconds(2)
                : TimeSpan.FromSeconds(5);

            UpdateMediaProgressTimer();

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
            SetSpectrumActive(state.PlaybackStatus == "Playing");
            AnnounceTrackChange(state);
            UpdateCoverFor(state);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException)
        {
            SetText(MediaStatusText, $"Media unavailable: {exception.Message}");
        }
        finally
        {
            _refreshingMedia = false;
        }

        if (_mediaRefreshPending)
        {
            _mediaRefreshPending = false;
            await RefreshMediaAsync();
        }
    }

    /// <summary>
    /// Shows a "Now playing" notification when the track changes. The first track seen after
    /// launch is recorded silently; a track that changes while paused is announced on play.
    /// </summary>
    private void AnnounceTrackChange(ISLA.Media.MediaState state)
    {
        // MediaService never returns an empty title: "None" means there is no session at all.
        if (state.PlaybackStatus == "None" || string.IsNullOrWhiteSpace(state.Title))
        {
            return;
        }

        var key = $"{state.Title}|{state.Artist}";
        if (_announcedTrackKey is null)
        {
            _announcedTrackKey = key;
            return;
        }

        if (state.PlaybackStatus != "Playing" || string.Equals(key, _announcedTrackKey, StringComparison.Ordinal))
        {
            return;
        }

        _announcedTrackKey = key;
        if (IsFocusModeActive())
        {
            return;
        }

        var message = string.IsNullOrWhiteSpace(state.Artist) ? state.Title : $"{state.Title} — {state.Artist}";
        ShowNodeNotification("Now playing", message, TimeSpan.FromSeconds(3.5));
    }

    /// <summary>
    /// While media is playing, the battery percentage on the right is swapped for a small
    /// animated spectrum. In the idle notch the spectrum sits to the right of the clock.
    /// </summary>
    private void SetSpectrumActive(bool active)
    {
        if (active == _spectrumActive)
        {
            return;
        }

        _spectrumActive = active;
        CompactBatteryPercentText.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        CompactSpectrum.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        UpdateSpectrumAnimations();
        if (_isIdleNotch)
        {
            RetargetIdleNotch();
        }
    }

    /// <summary>Animations only run for the spectrum that is actually on screen.</summary>
    private void UpdateSpectrumAnimations()
    {
        AnimateSpectrum(CompactSpectrum, _spectrumActive && !_isIdleNotch);
        AnimateSpectrum(IdleSpectrum, _spectrumActive && _isIdleNotch);
    }

    // Spectrum bar timing: shared static data, so starting the animation allocates no arrays.
    private static readonly double[] SpectrumSeconds = [0.46, 0.62, 0.38, 0.56, 0.50];
    private static readonly double[] SpectrumLows = [0.25, 0.20, 0.30, 0.22, 0.28];
    private static readonly IEasingFunction SpectrumEase =
        Frozen(new SineEase { EasingMode = EasingMode.EaseInOut });
    private const int SpectrumFrameRate = 15;

    private static void AnimateSpectrum(StackPanel panel, bool run)
    {
        for (var i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i].RenderTransform is not ScaleTransform scale)
            {
                continue;
            }

            if (run)
            {
                var animation = new DoubleAnimation
                {
                    From = SpectrumLows[i % SpectrumLows.Length],
                    To = 1,
                    Duration = TimeSpan.FromSeconds(SpectrumSeconds[i % SpectrumSeconds.Length]),
                    BeginTime = TimeSpan.FromMilliseconds(i * 70),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = SpectrumEase
                };

                // A 60 fps bar chart is wasted work for five tiny rectangles.
                Timeline.SetDesiredFrameRate(animation, SpectrumFrameRate);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
            }
            else
            {
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleY = 0.4;
            }
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

    // =================================================================================
    //  Media: event-driven refresh, seeking, album art
    // =================================================================================

    private async Task StartMediaWatcherAsync()
    {
        try
        {
            _mediaWatcher.Changed += MediaWatcher_Changed;
            await _mediaWatcher.StartAsync();
        }
        catch (Exception exception)
        {
            // Without the watcher everything still works through the 2 s poll.
            AppLog.Warn("Media watcher unavailable; falling back to polling.", exception);
        }
    }

    private void MediaWatcher_Changed(object? sender, EventArgs e)
    {
        // Raised on a background thread, and usually several times in a burst: debounce on the UI thread.
        Dispatcher.BeginInvoke(() =>
        {
            _mediaDebounceTimer.Stop();
            _mediaDebounceTimer.Start();
        });
    }

    private async void MediaDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _mediaDebounceTimer.Stop();
        await RefreshMediaAsync();
    }

    private double GetSeekRatio(System.Windows.Input.MouseEventArgs e)
    {
        var width = MediaSeekArea.ActualWidth;
        return width <= 0 ? 0 : Math.Clamp(e.GetPosition(MediaSeekArea).X / width, 0, 1);
    }

    private void AnimateSeekBar(double height) =>
        MediaProgress.BeginAnimation(HeightProperty,
            new DoubleAnimation(height, TimeSpan.FromMilliseconds(120)) { EasingFunction = FadeEase });

    private void MediaSeek_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => AnimateSeekBar(6);

    private void MediaSeek_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_seeking)
        {
            AnimateSeekBar(4);
        }
    }

    private void MediaSeek_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_lastMediaState is not { } state || state.Duration <= TimeSpan.Zero)
        {
            return;
        }

        _seeking = true;
        MediaSeekArea.CaptureMouse();
        UpdateSeekPreview(GetSeekRatio(e), state);
        e.Handled = true;
    }

    private void MediaSeek_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_seeking && _lastMediaState is { } state)
        {
            UpdateSeekPreview(GetSeekRatio(e), state);
        }
    }

    private void UpdateSeekPreview(double ratio, ISLA.Media.MediaState state)
    {
        _seekRatio = ratio;
        SetMediaProgress(ratio);
        SetText(MediaPositionText, FormatTime(TimeSpan.FromSeconds(ratio * state.Duration.TotalSeconds)));
    }

    private async void MediaSeek_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_seeking)
        {
            return;
        }

        _seeking = false;
        MediaSeekArea.ReleaseMouseCapture();
        if (!MediaSeekArea.IsMouseOver)
        {
            AnimateSeekBar(4);
        }

        if (_lastMediaState is not { } state || state.Duration <= TimeSpan.Zero)
        {
            return;
        }

        var target = TimeSpan.FromSeconds(_seekRatio * state.Duration.TotalSeconds);
        var accepted = await _mediaWatcher.SeekAsync(target);
        if (!accepted)
        {
            SetText(MediaStatusText, "This player doesn't support seeking");
            UpdateMediaProgressDisplay(state);
            return;
        }

        // Move the local clock right away; the next poll just confirms it.
        _mediaAnchorPosition = target;
        _mediaAnchorUtc = DateTime.UtcNow;
        UpdateMediaProgressDisplay(state);
    }

    private void UpdateCoverFor(ISLA.Media.MediaState state)
    {
        var key = state.PlaybackStatus == "None" ? string.Empty : $"{state.Title}|{state.Artist}|{state.Album}";
        if (!string.Equals(key, _coverKey, StringComparison.Ordinal))
        {
            _coverKey = key;
            _coverAttempts = 0;
            _coverLoaded = false;
            MediaCoverImage.Source = null;
        }

        if (_coverLoaded || _coverLoading || _coverAttempts >= 4 || key.Length == 0)
        {
            return;
        }

        _ = LoadCoverAsync(key);
    }

    private async Task LoadCoverAsync(string key)
    {
        _coverLoading = true;
        _coverAttempts++;
        try
        {
            // Players often publish the artwork a moment after the title: a few retries cover that.
            var bytes = await _mediaWatcher.GetThumbnailAsync();
            if (bytes is null || !string.Equals(key, _coverKey, StringComparison.Ordinal))
            {
                return;
            }

            using var stream = new MemoryStream(bytes);
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 160;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            MediaCoverImage.Source = image;
            _coverLoaded = true;
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or IOException or ArgumentException)
        {
            _coverAttempts = 4; // undecodable artwork: stop retrying for this track
        }
        finally
        {
            _coverLoading = false;
        }
    }

    private void UpdateMediaProgressDisplay(ISLA.Media.MediaState state)
    {
        if (_seeking)
        {
            return;
        }

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
            // First hover returns to the compact dynamic island. The menu only opens on a
            // later hover of that island, so the notch never jumps straight into the menu.
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
        _idleNotchWidth = GetIdleNotchWidth();
        CompactBatteryContent.Visibility = Visibility.Collapsed;
        // Hidden until the notch has reached its final size (shown in the Completed handler below).
        IdleBatteryEdge.Visibility = Visibility.Collapsed;
        IdleBatteryTrack.Visibility = Visibility.Collapsed;
        CompactChargingIcon.Visibility = Visibility.Collapsed;
        CompactContent.Width = _idleNotchWidth;
        ApplyIdleClockLayout();
        CompactContent.Margin = new Thickness(0);
        ClockText.FontSize = 10;
        // A notch is flush with the top edge: no horizontal inset. Its top corners stay
        // square; the flare wings below carry the silhouette outward into the screen edge.
        IslandSurface.Margin = new Thickness(0);
        IslandSurface.Padding = new Thickness(0);
        // The wings only appear once the notch has reached the screen edge (see the
        // Completed handler).
        SetIdleNotchFlareVisibility(Visibility.Collapsed);
        AnimateWindowTop(SystemParameters.WorkArea.Top, TimeSpan.FromMilliseconds(320), CollapseEase);
        IslandSurface.BeginAnimation(WidthProperty,
            MakeAnimation(_idleNotchWidth, TimeSpan.FromMilliseconds(320), CollapseEase), HandoffBehavior.SnapshotAndReplace);
        IslandSurface.BeginAnimation(HeightProperty,
            MakeAnimation(IdleNotchHeight, TimeSpan.FromMilliseconds(320), CollapseEase), HandoffBehavior.SnapshotAndReplace);
        var notchRadiusAnimation = MakeAnimation(IdleNotchRadius, TimeSpan.FromMilliseconds(320), CollapseEase);
        notchRadiusAnimation.Completed += (_, _) =>
        {
            if (_isIdleNotch)
            {
                // Attach: the wings now bridge the square top corners into the screen edge.
                SetIdleNotchFlareVisibility(Visibility.Visible);
                IslandSurface.CornerRadius = new CornerRadius(0, 0, IdleNotchRadius, IdleNotchRadius);
                SetIdleBatteryEdge(_lastBatteryPercent);
                var edgeVisibility = _pomodoroActive ? Visibility.Collapsed : Visibility.Visible;
                IdleBatteryTrack.Visibility = edgeVisibility;
                IdleBatteryEdge.Visibility = edgeVisibility;
            }
        };
        BeginAnimation(IslandRadiusProperty, notchRadiusAnimation, HandoffBehavior.SnapshotAndReplace);
        Fade(ClockText, 0, 90, 0, FadeEase, () => Fade(ClockText, 1, 150, 30, FadeEase));
        UpdateSpectrumAnimations();
    }

    private double GetIdleNotchWidth() => (_pomodoroActive, _spectrumActive) switch
    {
        (true, true) => IdleBothNotchWidth,
        (true, false) => IdlePomodoroNotchWidth,
        (false, true) => IdleMusicNotchWidth,
        _ => IdleNotchWidth
    };

    /// <summary>
    /// Idle notch layout: centred clock normally; while media plays the clock moves to the
    /// left edge and the spectrum takes the right side.
    /// </summary>
    private void ApplyIdleClockLayout()
    {
        var music = _isIdleNotch && _spectrumActive;
        var pomodoro = _isIdleNotch && _pomodoroActive;
        if (pomodoro && !music)
        {
            // tomato on the left, clock on the right
            ClockText.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            ClockText.Margin = new Thickness(0, 0, 10, 0);
        }
        else if (music && !pomodoro)
        {
            // clock on the left, spectrum on the right
            ClockText.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            ClockText.Margin = new Thickness(12, 0, 0, 0);
        }
        else
        {
            // nothing extra, or both: tomato left, clock centred, spectrum right
            ClockText.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            ClockText.Margin = new Thickness(0);
        }

        IdleSpectrum.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
        IdlePomodoro.Visibility = pomodoro ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Media started/stopped while the notch is idle: grow or shrink it to fit.</summary>
    private void RetargetIdleNotch()
    {
        var target = GetIdleNotchWidth();
        if (Math.Abs(target - _idleNotchWidth) < 0.5)
        {
            ApplyIdleClockLayout();
            return;
        }

        _idleNotchWidth = target;
        IdleBatteryEdge.Visibility = Visibility.Collapsed;
        IdleBatteryTrack.Visibility = Visibility.Collapsed;
        CompactContent.Width = target;
        ApplyIdleClockLayout();
        Fade(ClockText, 0, 90, 0, FadeEase, () => Fade(ClockText, 1, 160, 120, FadeEase));

        var animation = MakeAnimation(target, TimeSpan.FromMilliseconds(320), CollapseEase);
        animation.Completed += (_, _) =>
        {
            if (_isIdleNotch && Math.Abs(_idleNotchWidth - target) < 0.5)
            {
                SetIdleBatteryEdge(_lastBatteryPercent);
                var edgeVisibility = _pomodoroActive ? Visibility.Collapsed : Visibility.Visible;
                IdleBatteryTrack.Visibility = edgeVisibility;
                IdleBatteryEdge.Visibility = edgeVisibility;
            }
        };
        IslandSurface.BeginAnimation(WidthProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void ExitIdleNotch(bool animate = true, bool resetTop = true)
    {
        if (!_isIdleNotch)
        {
            return;
        }

        _isIdleNotch = false;
        _idleNotchWidth = IdleNotchWidth;
        ApplyIdleClockLayout();
        UpdateSpectrumAnimations();
        CompactBatteryContent.Visibility = Visibility.Visible;
        IdleBatteryEdge.Visibility = Visibility.Collapsed;
        IdleBatteryTrack.Visibility = Visibility.Collapsed;
        CompactContent.Width = CompactWidth - (CompactHorizontalPadding * 2);
        CompactContent.Margin = new Thickness(0);
        ClockText.FontSize = 13;
        IslandSurface.Margin = new Thickness(0);
        IslandSurface.Padding = new Thickness(CompactHorizontalPadding, 0, CompactHorizontalPadding, 0);
        SetIdleNotchFlareVisibility(Visibility.Collapsed);
        if (!animate)
        {
            IslandSurface.BeginAnimation(WidthProperty, null);
            IslandSurface.BeginAnimation(HeightProperty, null);
            BeginAnimation(IslandRadiusProperty, null);
            IslandSurface.Width = CompactWidth;
            IslandSurface.Height = CompactHeight;
            IslandRadius = CompactRadius;
            if (resetTop)
            {
                Top = SystemParameters.WorkArea.Top + NormalTopInset;
            }

            return;
        }

        if (resetTop)
        {
            AnimateWindowTop(SystemParameters.WorkArea.Top + NormalTopInset,
                TimeSpan.FromMilliseconds(280), ExpandEase);
        }
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

        var expandingFromNotch = expanded && _isIdleNotch;
        if (expanded)
        {
            // Keep the window where the notch left it: expanding happens in place so the
            // pointer stays over the island, and the slide back to the normal inset waits
            // for the collapse.
            ExitIdleNotch(animate: false, resetTop: false);
            if (_nodeNotificationActive)
            {
                // Opening the island dismisses the banner; whatever is still queued waits for the close.
                EndActiveNotification(restoreSize: false);
            }
        }

        _isExpanded = expanded;
        var version = ++_expandVersion;
        UpdateMediaProgressTimer();
        if (expanded && _activeTab == "Media" && _lastMediaState is { } media)
        {
            // Show the current position immediately: the fast progress timer was stopped.
            UpdateMediaProgressDisplay(media);
        }

        var targetWidth = expanded ? ExpandedWidth : CompactWidth;
        var targetHeight = expanded ? GetTabHeight(_activeTab) : CompactHeight;
        var targetRadius = expanded ? ExpandedRadius : CompactRadius;
        var duration = TimeSpan.FromMilliseconds(expanded ? 420 : 280);
        var ease = expanded ? ExpandEase : CollapseEase;
        var targetTop = SystemParameters.WorkArea.Top + NormalTopInset;
        if (!expandingFromNotch && Math.Abs(Top - targetTop) > 0.5)
        {
            // Coming out of the idle notch: expand in place first (so the pointer stays over
            // the island), and slide back to the normal inset only when collapsing.
            AnimateWindowTop(targetTop, duration, ease);
        }

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
            if (_activeTab == "Settings")
            {
                RefreshSettingsIfStale();
            }
        }
        else
        {
            Fade(ExpandedContent, 0, 120, 0, FadeEase, () =>
            {
                if (version == _expandVersion)
                {
                    ExpandedContent.Visibility = Visibility.Collapsed;
                    ResetRadioView();
                }
            });
            Fade(CompactContent, 1, 130, 140, FadeEase);
            _lastInteractionUtc = DateTime.UtcNow;
            _ = PumpNotificationsAfterCollapseAsync(version);

        }
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
        Microsoft.Win32.SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
        _mediaDebounceTimer.Stop();
        _mediaWatcher.Dispose();
        _mediaRefreshTimer.Stop();
        _mediaProgressTimer.Stop();
        _settingsRefreshTimer.Stop();
        _pomodoroTimer.Stop();
        _systemStateTimer.Stop();
        _nodeNotificationTimer.Stop();
        _idleTimer.Stop();
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

    private async void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingVolumeSlider || !IsLoaded)
        {
            return;
        }

        SetText(VolumeValueText, $"{e.NewValue:P0}");

        try
        {
            // The audio endpoint is COM; it runs on the thread pool.
            await _quickSettingsService.SetVolumeAsync(e.NewValue);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or UnauthorizedAccessException)
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
            var phaseName = _pomodoroService.LastCompletedPhase switch
            {
                PomodoroPhase.ShortBreak => "short break",
                PomodoroPhase.LongBreak => "long break",
                _ => "focus"
            };
            ShowNodeNotification("Pomodoro", $"{_pomodoroService.LastCompletedMinutes} min {phaseName} complete",
                TimeSpan.FromSeconds(5));
        }

        RefreshPomodoro();
    }

    private void RefreshPomodoro()
    {
        var state = _pomodoroService.State;

        // Tick only while a session is running; an idle timer never changes on its own.
        if (state.IsRunning)
        {
            _pomodoroTimer.Start();
        }
        else
        {
            _pomodoroTimer.Stop();
        }

        SetText(PomodoroTimeText, $"{(int)state.Remaining.TotalMinutes:00}:{state.Remaining.Seconds:00}");
        SetText(PomodoroPhaseText, state.Phase switch
        {
            PomodoroPhase.ShortBreak => "Short break",
            PomodoroPhase.LongBreak => "Long break",
            _ => "Focus session"
        });
        SetText(PomodoroSessionsText, $"Sessions: {state.CompletedFocusSessions}");
        SetPomodoroIcon(state.IsRunning);

        SetPomodoroActive(state.IsRunning);
        SetPomodoroBreak(state.Phase != PomodoroPhase.Focus);
        if (state.IsRunning)
        {
            var total = _pomodoroService.PhaseDuration(state.Phase).TotalSeconds;
            SetPomodoroRing(total <= 0 ? 0 : state.Remaining.TotalSeconds / total);
        }

        RefreshDurationChips(state);
        if (FocusModeSwitch.IsChecked != state.FocusModeEnabled)
        {
            FocusModeSwitch.IsChecked = state.FocusModeEnabled;
        }

        UpdateFocusMode();

        // The "session complete" notice stays visible until the user acts again.
        var running = IsFocusModeActive()
            ? (_heldNotificationApps.Count > 0 ? $"Focus mode on, {_heldNotificationApps.Count} held" : "Focus mode on")
            : "Timer running";
        SetText(PomodoroStatusText, _pomodoroNotice ?? (state.IsRunning ? running : "Ready"));
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

    // ---- indicator: red tomato replaces the battery indicator while the timer runs ----

    private void InitializePomodoroUi()
    {
        // Each indicator holds both icons: tomato for work, steaming cup for breaks.
        _compactTomatoView = CreateTomatoIcon(12, out _compactTomatoPulse);
        _compactCupView = CreateCupIcon(12, out _compactSteam);
        _compactCupView.Visibility = Visibility.Collapsed;
        CompactPomodoroIconHost.Child = new Grid { Children = { _compactTomatoView, _compactCupView } };

        _idleTomatoView = CreateTomatoIcon(11, out _idleTomatoPulse);
        _idleCupView = CreateCupIcon(11, out _idleSteam);
        _idleCupView.Visibility = Visibility.Collapsed;
        IdlePomodoro.Child = new Grid { Children = { _idleTomatoView, _idleCupView } };
    }

    private static FrameworkElement CreateTomatoIcon(double size, out ScaleTransform? pulse)
    {
        var red = new RadialGradientBrush
        {
            GradientOrigin = new System.Windows.Point(0.35, 0.3),
            Center = new System.Windows.Point(0.45, 0.45),
            RadiusX = 0.75,
            RadiusY = 0.75
        };
        red.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromRgb(0xFF, 0x7A, 0x6E), 0));
        red.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromRgb(0xFF, 0x45, 0x3A), 0.55));
        red.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromRgb(0xD9, 0x2D, 0x24), 1));
        var green = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5D, 0xDB, 0x6F));

        var body = new System.Windows.Shapes.Ellipse { Width = 17, Height = 15.5, Fill = red };
        Canvas.SetLeft(body, 1.5);
        Canvas.SetTop(body, 4);

        var shine = new System.Windows.Shapes.Ellipse
        {
            Width = 4,
            Height = 2.4,
            Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)),
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new RotateTransform(-30)
        };
        Canvas.SetLeft(shine, 4.8);
        Canvas.SetTop(shine, 8);

        var leaves = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 10,5.8 L 7.4,4 L 7.9,6.4 L 5,6.6 L 7.6,8 L 10,7.4 L 12.4,8 L 15,6.6 L 12.1,6.4 L 12.6,4 Z"),
            Fill = green
        };
        var stem = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 10,6 L 10.8,2.4"),
            Stroke = green,
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };

        var canvas = new Canvas { Width = 20, Height = 20 };
        canvas.Children.Add(body);
        canvas.Children.Add(shine);
        canvas.Children.Add(stem);
        canvas.Children.Add(leaves);

        var scale = new ScaleTransform(1, 1);
        pulse = scale;
        return new Viewbox
        {
            Width = size,
            Height = size,
            Child = canvas,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = scale
        };
    }

    /// <summary>Work (Focus) = red tomato; rest (short or long break) = blue steaming cup.</summary>
    private void SetPomodoroBreak(bool isBreak)
    {
        if (isBreak == _pomodoroIsBreak)
        {
            return;
        }

        _pomodoroIsBreak = isBreak;
        CompactPomodoroRing.Stroke = isBreak ? PomodoroBreakBrush : PomodoroFocusBrush;
        SetIconVisibility(_compactTomatoView, _compactCupView, isBreak);
        SetIconVisibility(_idleTomatoView, _idleCupView, isBreak);
        UpdatePomodoroAnimations();
    }

    private static void SetIconVisibility(FrameworkElement? tomato, FrameworkElement? cup, bool isBreak)
    {
        if (tomato is not null)
        {
            tomato.Visibility = isBreak ? Visibility.Collapsed : Visibility.Visible;
        }

        if (cup is not null)
        {
            cup.Visibility = isBreak ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>The tomato pulses during focus, the steam rises during breaks. Nothing runs when the timer is idle.</summary>
    private void UpdatePomodoroAnimations()
    {
        var focus = _pomodoroActive && !_pomodoroIsBreak;
        var rest = _pomodoroActive && _pomodoroIsBreak;
        PulseTomato(_compactTomatoPulse, focus);
        PulseTomato(_idleTomatoPulse, focus);
        AnimateSteam(_compactSteam, rest);
        AnimateSteam(_idleSteam, rest);
    }

    private static FrameworkElement CreateCupIcon(double size, out FrameworkElement[]? steam)
    {
        var cupBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEA, 0xF3, 0xFF));
        var steamBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8E, 0xC5, 0xFF));

        var body = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 4,9 L 4,13.5 C 4,16 6,17.5 8.5,17.5 L 10.5,17.5 C 13,17.5 15,16 15,13.5 L 15,9 Z"),
            Fill = cupBrush
        };
        var handle = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 15,10.2 C 18.2,10.2 18.2,14.8 15,14.8"),
            Stroke = cupBrush,
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        var saucer = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 3,19 L 16,19"),
            Stroke = cupBrush,
            StrokeThickness = 1.3,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };

        var canvas = new Canvas { Width = 20, Height = 20 };
        canvas.Children.Add(body);
        canvas.Children.Add(handle);
        canvas.Children.Add(saucer);

        string[] steamShapes =
        [
            "M 6.5,7.5 C 5.3,6 7.7,4.8 6.5,3.2",
            "M 9.5,7.5 C 8.3,6 10.7,4.8 9.5,3.2",
            "M 12.5,7.5 C 11.3,6 13.7,4.8 12.5,3.2"
        ];
        var lines = new FrameworkElement[steamShapes.Length];
        for (var i = 0; i < steamShapes.Length; i++)
        {
            lines[i] = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(steamShapes[i]),
                Stroke = steamBrush,
                StrokeThickness = 1.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Opacity = 0.85,
                RenderTransform = new TranslateTransform(0, 0)
            };
            canvas.Children.Add(lines[i]);
        }

        steam = lines;
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }

    /// <summary>Three wisps rise and fade one after another, like the spectrum bars bounce.</summary>
    private static void AnimateSteam(FrameworkElement[]? steam, bool run)
    {
        if (steam is null)
        {
            return;
        }

        for (var i = 0; i < steam.Length; i++)
        {
            var line = steam[i];
            if (line.RenderTransform is not TranslateTransform shift)
            {
                continue;
            }

            if (run)
            {
                var begin = TimeSpan.FromMilliseconds(i * 420);
                var duration = TimeSpan.FromMilliseconds(1300);
                shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
                {
                    From = 1.5,
                    To = -2.5,
                    Duration = duration,
                    BeginTime = begin,
                    RepeatBehavior = RepeatBehavior.Forever
                });

                var fade = new DoubleAnimationUsingKeyFrames
                {
                    Duration = duration,
                    BeginTime = begin,
                    RepeatBehavior = RepeatBehavior.Forever
                };
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.95, KeyTime.FromPercent(0.4)));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
                line.BeginAnimation(OpacityProperty, fade);
            }
            else
            {
                shift.BeginAnimation(TranslateTransform.YProperty, null);
                line.BeginAnimation(OpacityProperty, null);
                shift.Y = 0;
                line.Opacity = 0.85;
            }
        }
    }

    private void SetPomodoroActive(bool active)
    {
        if (active == _pomodoroActive)
        {
            return;
        }

        _pomodoroActive = active;
        CompactBatteryRingHost.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        CompactPomodoro.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        UpdatePomodoroAnimations();
        if (!active)
        {
            _lastPomodoroFraction = -1;
        }

        if (_isIdleNotch)
        {
            RetargetIdleNotch();
        }
    }

    /// <summary>A soft heartbeat, in the same spirit as the music spectrum. Only runs while the timer runs.</summary>
    private static void PulseTomato(ScaleTransform? scale, bool run)
    {
        if (scale is null)
        {
            return;
        }

        if (run)
        {
            var pulse = new DoubleAnimation
            {
                From = 0.86,
                To = 1.1,
                Duration = TimeSpan.FromMilliseconds(650),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        }
        else
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = 1;
            scale.ScaleY = 1;
        }
    }

    /// <summary>Ring around the compact tomato: the share of the current phase that is still left.</summary>
    private void SetPomodoroRing(double remainingFraction)
    {
        var progress = Math.Clamp(remainingFraction, 0, 1);
        if (Math.Abs(progress - _lastPomodoroFraction) < 0.002)
        {
            return;
        }

        _lastPomodoroFraction = progress;
        const double center = 11;
        const double radius = 9.5;
        if (progress <= 0.001)
        {
            CompactPomodoroRing.Data = null;
            return;
        }

        if (progress >= 0.999)
        {
            CompactPomodoroRing.Data = new EllipseGeometry(new System.Windows.Point(center, center), radius, radius);
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
        CompactPomodoroRing.Data = new PathGeometry([figure]);
    }

    // ---- mode selector (Focus / Short / Long) + one row of duration chips for the selected mode ----

    private static int[] PresetsFor(PomodoroPhase phase) => phase switch
    {
        PomodoroPhase.ShortBreak => ShortBreakPresets,
        PomodoroPhase.LongBreak => LongBreakPresets,
        _ => FocusPresets
    };

    private static int MinutesFor(PomodoroState state, PomodoroPhase phase) => phase switch
    {
        PomodoroPhase.ShortBreak => state.ShortBreakMinutes,
        PomodoroPhase.LongBreak => state.LongBreakMinutes,
        _ => state.FocusMinutes
    };

    private void RebuildDurationChips(PomodoroPhase phase)
    {
        DurationChips.Children.Clear();
        var style = (Style)FindResource("DurationChipStyle");
        foreach (var minutes in PresetsFor(phase))
        {
            var chip = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = minutes.ToString(),
                Style = style,
                Tag = new DurationPick(phase, minutes),
                ToolTip = $"{minutes} minutes"
            };
            chip.Click += DurationChip_Click;
            DurationChips.Children.Add(chip);
        }

        _chipsPhase = phase;
    }

    private void PhaseButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { Tag: string tag }
            || !Enum.TryParse<PomodoroPhase>(tag, out var phase))
        {
            return;
        }

        _pomodoroNotice = null;
        _pomodoroService.SelectPhase(phase);
        _durationChipSignature = string.Empty; // re-sync the toggle state even if the phase did not change
        RefreshPomodoro();
    }

    private void DurationChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { Tag: DurationPick pick })
        {
            return;
        }

        _pomodoroService.SetDuration(pick.Phase, pick.Minutes);
        _durationChipSignature = string.Empty; // force the chips to re-sync with the saved values
        RefreshPomodoro();
    }

    private void RefreshDurationChips(PomodoroState state)
    {
        if (_chipsPhase != state.Phase)
        {
            RebuildDurationChips(state.Phase);
            _durationChipSignature = string.Empty;
        }

        var minutes = MinutesFor(state, state.Phase);
        var signature = $"{state.Phase}/{minutes}/{state.IsRunning}";
        if (signature == _durationChipSignature)
        {
            return;
        }

        _durationChipSignature = signature;
        PhaseFocusButton.IsChecked = state.Phase == PomodoroPhase.Focus;
        PhaseShortButton.IsChecked = state.Phase == PomodoroPhase.ShortBreak;
        PhaseLongButton.IsChecked = state.Phase == PomodoroPhase.LongBreak;

        // Mode and length can only be changed while the timer is not running.
        var editable = !state.IsRunning;
        PhaseFocusButton.IsEnabled = editable;
        PhaseShortButton.IsEnabled = editable;
        PhaseLongButton.IsEnabled = editable;
        foreach (var child in DurationChips.Children)
        {
            if (child is System.Windows.Controls.Primitives.ToggleButton { Tag: DurationPick pick } chip)
            {
                chip.IsChecked = pick.Minutes == minutes;
                chip.IsEnabled = editable;
            }
        }
    }

    // ---- focus mode: hold Windows notifications during a running focus session ----

    private bool IsFocusModeActive()
    {
        var state = _pomodoroService.State;
        return state.FocusModeEnabled && state.IsRunning && state.Phase == PomodoroPhase.Focus;
    }

    private void FocusModeSwitch_Click(object sender, RoutedEventArgs e)
    {
        _pomodoroService.SetFocusMode(FocusModeSwitch.IsChecked == true);
        RefreshPomodoro();
    }

    private void HoldNotification(string appName)
    {
        if (_heldNotificationApps.Count >= 200)
        {
            _heldNotificationApps.RemoveAt(0);
        }

        _heldNotificationApps.Add(string.IsNullOrWhiteSpace(appName) ? "App" : appName);
    }

    /// <summary>When focus mode stops holding (break, pause, reset or switched off), show one summary.</summary>
    private void UpdateFocusMode()
    {
        var active = IsFocusModeActive();
        if (_focusModeHolding && !active)
        {
            FlushHeldNotifications();
        }

        _focusModeHolding = active;
    }

    private void FlushHeldNotifications()
    {
        var count = _heldNotificationApps.Count;
        if (count == 0)
        {
            return;
        }

        var groups = _heldNotificationApps
            .GroupBy(app => app)
            .OrderByDescending(group => group.Count())
            .ToList();
        var shown = groups.Take(3)
            .Select(group => group.Count() > 1 ? $"{group.Key} ×{group.Count()}" : group.Key);
        var summary = string.Join(", ", shown) + (groups.Count > 3 ? $" +{groups.Count - 3}" : string.Empty);
        _heldNotificationApps.Clear();
        ShowNodeNotification("Focus mode", $"{count} held · {summary}", TimeSpan.FromSeconds(5));
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
        if (_activeTab == "Settings")
        {
            // Leaving Settings also leaves the Wi-Fi / Bluetooth detail view.
            _radioViewOpen = false;
            _radioLoadVersion++;
        }

        _activeTab = tab;
        UpdateMediaProgressTimer();
        if (tab == "Media" && _lastMediaState is { } media)
        {
            UpdateMediaProgressDisplay(media);
        }
        if (tab == "Settings")
        {
            RefreshSettingsIfStale();
        }
        if (tab == "Weather")
        {
            _ = RefreshWeatherAsync();
        }
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
        yield return RadioPanel;
        yield return TimerPanel;
        yield return WeatherPanel;
    }

    private FrameworkElement GetPanel(string tab) => tab switch
    {
        "Settings" => _radioViewOpen ? RadioPanel : SettingsPanel,
        "Timer" => TimerPanel,
        "Weather" => WeatherPanel,
        _ => MediaPanel
    };

    private static int GetTabIndex(string tab) => tab switch
    {
        "Settings" => 1,
        "Timer" => 2,
        "Weather" => 3,
        _ => 0
    };

    private static double GetTabHeight(string tab) => tab switch
    {
        "Settings" => SettingsHeight,
        "Timer" => TimerHeight,
        "Weather" => WeatherHeight,
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