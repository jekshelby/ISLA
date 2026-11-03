using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace ISLA.Notifications;

public sealed record WindowsNotification(string AppName, string Title, string Body);

public sealed class WindowsNotificationService : IDisposable
{
    private readonly UserNotificationListener _listener = UserNotificationListener.Current;
    private readonly HashSet<uint> _seenIds = [];          // hanya diakses di dalam _pollGate
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _cts;
    private int _pollRequested;
    private int _started;
    private int _disposed;

    public event EventHandler<WindowsNotification>? NotificationReceived;
    public event EventHandler<Exception>? Error;
    public UserNotificationListenerAccessStatus AccessStatus => _listener.GetAccessStatus();

    public async Task StartAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(WindowsNotificationService));
        }

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

        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

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

        // Satu-satunya polling: loop internal ini menggantikan timer di MainWindow.
        lock (_lifecycleLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            _ = RunPollLoopAsync(cts);
        }
    }

    private async Task RunPollLoopAsync(CancellationTokenSource cts)
    {
        // Polling is only a fallback: NotificationChanged wakes this immediately when it fires.
        // GetNotificationsAsync is a comparatively expensive WinRT call (~130 ms), so the
        // fallback runs rarely instead of once per second.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(cts.Token))
            {
                await PollAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex);
        }
        finally
        {
            // Clear the field before disposing so Dispose() never cancels a dead source.
            Interlocked.CompareExchange(ref _cts, null, cts);
            cts.Dispose();
        }
    }

    public async Task PollAsync()
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _started) == 0)
        {
            return;
        }

        var gateTaken = false;
        try
        {
            if (AccessStatus != UserNotificationListenerAccessStatus.Allowed)
            {
                return;
            }

            gateTaken = await _pollGate.WaitAsync(0);
            if (!gateTaken)
            {
                // Jangan dibuang: minta poll yang sedang berjalan untuk mengulang
                Interlocked.Exchange(ref _pollRequested, 1);
                return;
            }

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

                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        break;
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
            while (Volatile.Read(ref _pollRequested) == 1 && Volatile.Read(ref _disposed) == 0);
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex);
        }
        finally
        {
            if (gateTaken)
            {
                _pollGate.Release();
            }
        }
    }

    private void Listener_NotificationChanged(
        UserNotificationListener sender,
        UserNotificationChangedEventArgs args)
    {
        if (args.ChangeKind == UserNotificationChangedKind.Added &&
            Volatile.Read(ref _disposed) == 0)
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
        lock (_lifecycleLock)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _listener.NotificationChanged -= Listener_NotificationChanged;
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, ex);
            }

            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The poll loop already finished and disposed its own token source.
            }
        }

        // _pollGate sengaja tidak di-dispose: poll yang dimulai sebelum Dispose bisa masih
        // berjalan dan akan melempar ObjectDisposedException. Semaphore tanpa wait handle
        // tidak memegang resource unmanaged, jadi cukup dibiarkan di-GC.
    }
}
