using Windows.Media.Control;

namespace ISLA.Media;

public sealed record MediaState(
    string Title,
    string Artist,
    string Album,
    string PlaybackStatus,
    TimeSpan Position,
    TimeSpan Duration)
{
    public static MediaState Empty { get; } = new(
        "No active media session",
        "Play media in Spotify or a supported browser",
        string.Empty,
        "None",
        TimeSpan.Zero,
        TimeSpan.Zero);
}

public sealed class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    public async Task<MediaState> GetCurrentStateAsync()
    {
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _session = _manager.GetCurrentSession();

        if (_session is null)
        {
            return MediaState.Empty;
        }

        var properties = await _session.TryGetMediaPropertiesAsync();
        var timeline = _session.GetTimelineProperties();
        var playbackInfo = _session.GetPlaybackInfo();

        return new MediaState(
            string.IsNullOrWhiteSpace(properties.Title) ? "Unknown title" : properties.Title,
            string.IsNullOrWhiteSpace(properties.Artist) ? "Unknown artist" : properties.Artist,
            properties.AlbumTitle ?? string.Empty,
            playbackInfo.PlaybackStatus.ToString(),
            timeline.Position,
            timeline.EndTime);
    }

    public async Task<bool> TogglePlayPauseAsync()
    {
        return _session is not null && await _session.TryTogglePlayPauseAsync();
    }

    public async Task<bool> SkipNextAsync()
    {
        return _session is not null && await _session.TrySkipNextAsync();
    }

    public async Task<bool> SkipPreviousAsync()
    {
        return _session is not null && await _session.TrySkipPreviousAsync();
    }
}
