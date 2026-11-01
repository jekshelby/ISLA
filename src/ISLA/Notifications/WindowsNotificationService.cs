using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace ISLA.Notifications;

public sealed record WindowsNotification(string AppName, string Title, string Body);

public sealed class WindowsNotificationService : IDisposable
{
    private readonly UserNotificationListener _listener = UserNotificationListener.Current;
    private readonly HashSet<uint> _seenIds = [];          // hanya diakses di dalam _pollGate
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private int _pollRequested;
    private bool _started;

    public event EventHandler<WindowsNotification>? NotificationReceived;
    public event EventHandler<Exception>? Error;
    public UserNotificationListenerAccessStatus AccessStatus => _listener.GetAccessStatus();

    public async Task StartAsync()
    {
        var access = AccessStatus;
        if (access == UserNotificationListenerAccessStatus.Unspecified)
        {
            access = await _listener.RequestAccessAsync();
        }

        if (access != UserNotificationListenerAccessStatus.Allowed)
        {
            throw new InvalidOperationException(
                $"Windows notification access was not granted ({access}). Enable notification access for ISLA in Windows Settings.");
        }

        if (_started)
        {
            return;
        }

        _started = true;

        // Tandai notifikasi yang sudah ada sebagai "sudah dilihat"
        await _pollGate.WaitAsync();
        try
        {
            var existing = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            foreach (var n in existing)
            {
                _seenIds.Add(n.Id);
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex);
        }
        finally
        {
            _pollGate.Release();
        }

        // Event hanya sebagai pemicu tambahan (bisa saja tidak pernah terpanggil)
        try
        {
            _listener.NotificationChanged += Listener_NotificationChanged;
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex);
        }

        // Polling utama, tidak bergantung pada event
        _cts = new CancellationTokenSource();
        _ = RunPollLoopAsync(_cts.Token);
    }

    private async Task RunPollLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                await PollAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task PollAsync()
    {
        if (!_started || AccessStatus != UserNotificationListenerAccessStatus.Allowed)
        {
            return;
        }

        if (!await _pollGate.WaitAsync(0))
        {
            // Jangan dibuang: minta poll yang sedang berjalan untuk mengulang
            Interlocked.Exchange(ref _pollRequested, 1);
            return;
        }

        try
        {
            do
            {
                Interlocked.Exchange(ref _pollRequested, 0);

                var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                var currentIds = new HashSet<uint>();

                foreach (var notification in notifications.OrderBy(n => n.CreationTime))
                {
                    currentIds.Add(notification.Id);
                    if (!_seenIds.Add(notification.Id))
                    {
                        continue;
                    }

                    try
                    {
                        Publish(notification);
                    }
                    catch (Exception ex)
                    {
                        Error?.Invoke(this, ex);
                    }
                }

                // Buang ID notifikasi yang sudah tidak ada, supaya set tidak membengkak
                _seenIds.IntersectWith(currentIds);
            }
            while (Volatile.Read(ref _pollRequested) == 1);
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private void Listener_NotificationChanged(
        UserNotificationListener sender,
        UserNotificationChangedEventArgs args)
    {
        if (args.ChangeKind == UserNotificationChangedKind.Added)
        {
            _ = PollAsync();
        }
    }

    private void Publish(UserNotification notification)
    {
        var binding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
        if (binding is null)
        {
            return;
        }

        var text = binding.GetTextElements()
            .Select(e => e.Text?.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToArray();
        if (text.Length == 0)
        {
            return;
        }

        var appName = notification.AppInfo.DisplayInfo.DisplayName;
        var title = text[0] ?? appName;
        var body = string.Join(" ", text.Skip(1));
        NotificationReceived?.Invoke(this, new WindowsNotification(appName, title, body));
    }

    public void Dispose()
    {
        if (_started)
        {
            try { _listener.NotificationChanged -= Listener_NotificationChanged; } catch { }
            _cts?.Cancel();
            _cts?.Dispose();
            _started = false;
        }

        _pollGate.Dispose();
    }
}