using System.Linq;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace ISLA.Media;

/// <summary>
/// Event-driven companion to <see cref="MediaService"/>. It raises <see cref="Changed"/> the
/// moment the track, playback state or active session changes (so the island doesn't have to
/// wait for the 2 s poll), and adds seeking and album-art support.
/// Events are raised on a background thread: marshal to the UI thread in the handler.
/// </summary>
public sealed class MediaWatcher : IDisposable
{
    private readonly object _gate = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _disposed;

    public event EventHandler? Changed;

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += Manager_CurrentSessionChanged;
        _manager.SessionsChanged += Manager_SessionsChanged;
        Attach();
    }

    /// <summary>Jumps to <paramref name="position"/>. Returns false if the player refuses.</summary>
    public async Task<bool> SeekAsync(TimeSpan position)
    {
        try
        {
            var session = GetSession();
            if (session is null)
            {
                return false;
            }

            return await session.TryChangePlaybackPositionAsync(position.Ticks);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Album art of the current session as encoded image bytes (png/jpg), or null.</summary>
    public async Task<byte[]?> GetThumbnailAsync()
    {
        try
        {
            var session = GetSession();
            if (session is null)
            {
                return null;
            }

            var properties = await session.TryGetMediaPropertiesAsync();
            var thumbnail = properties?.Thumbnail;
            if (thumbnail is null)
            {
                return null;
            }

            using var stream = await thumbnail.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > 8 * 1024 * 1024)
            {
                return null;
            }

            var buffer = new byte[stream.Size];
            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(buffer);
            return buffer;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private GlobalSystemMediaTransportControlsSession? GetSession()
    {
        lock (_gate)
        {
            return _session ?? _manager?.GetCurrentSession();
        }
    }

    private void Attach()
    {
        lock (_gate)
        {
            Detach();
            if (_disposed)
            {
                return;
            }

            _session = _manager?.GetCurrentSession();
            if (_session is not null)
            {
                _session.MediaPropertiesChanged += Session_MediaPropertiesChanged;
                _session.PlaybackInfoChanged += Session_PlaybackInfoChanged;
            }
        }
    }

    private void Detach()
    {
        if (_session is null)
        {
            return;
        }

        _session.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
        _session.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
        _session = null;
    }

    private void Manager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        Attach();
        Raise();
    }

    private void Manager_SessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args)
    {
        Attach();
        Raise();
    }

    private void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args) => Raise();

    private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args) => Raise();

    private void Raise()
    {
        if (!_disposed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Detach();
            if (_manager is not null)
            {
                _manager.CurrentSessionChanged -= Manager_CurrentSessionChanged;
                _manager.SessionsChanged -= Manager_SessionsChanged;
                _manager = null;
            }
        }
    }
}
