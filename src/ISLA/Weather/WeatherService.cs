using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using ISLA.Settings;

namespace ISLA.Weather;

public sealed record WeatherDay(DateTime Date, int Code, double Max, double Min)
{
    public string DayLabel => Date.ToString("ddd", CultureInfo.InvariantCulture);
    public string Description => WeatherService.Describe(Code);
    public string IconKey => WeatherService.IconKey(Code);
}

public sealed record WeatherState(
    string City,
    string Country,
    string TimeZone,
    double Latitude,
    double Longitude,
    double Temperature,
    double ApparentTemperature,
    int Humidity,
    double WindSpeed,
    int Code,
    bool IsDay,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<WeatherDay> Days)
{
    public string Description => WeatherService.Describe(Code);
    public string IconKey => WeatherService.IconKey(Code);

    public static WeatherState Empty { get; } = new(
        string.Empty,
        string.Empty,
        string.Empty,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        true,
        DateTimeOffset.MinValue,
        Array.Empty<WeatherDay>());
}

public sealed class WeatherService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);
    private static readonly HttpClient Http = CreateClient();

    private readonly AppSettingsService _settingsService;
    private WeatherState? _cached;
    private DateTime _cachedUtc = DateTime.MinValue;

    public WeatherService(AppSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public WeatherState? Cached => _cached;

    public async Task<WeatherState> GetWeatherAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh &&
            _cached is not null &&
            DateTime.UtcNow - _cachedUtc < CacheDuration)
        {
            return _cached;
        }

        var location = await ResolveLocationAsync(cancellationToken);
        var forecastUrl = string.Format(
            CultureInfo.InvariantCulture,
            "https://api.open-meteo.com/v1/forecast?latitude={0:0.####}&longitude={1:0.####}" +
            "&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,weather_code,wind_speed_10m" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=4",
            location.Latitude,
            location.Longitude);

        using var response = await Http.GetAsync(forecastUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = document.RootElement;
        var current = root.GetProperty("current");
        var daily = root.GetProperty("daily");
        var dates = daily.GetProperty("time");
        var codes = daily.GetProperty("weather_code");
        var highs = daily.GetProperty("temperature_2m_max");
        var lows = daily.GetProperty("temperature_2m_min");

        var days = new List<WeatherDay>(dates.GetArrayLength());
        for (var index = 0; index < dates.GetArrayLength(); index++)
        {
            var date = DateTime.ParseExact(
                dates[index].GetString() ?? string.Empty,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture);
            days.Add(new WeatherDay(
                date,
                codes[index].GetInt32(),
                highs[index].GetDouble(),
                lows[index].GetDouble()));
        }

        var state = new WeatherState(
            location.City,
            location.Country,
            location.TimeZone,
            location.Latitude,
            location.Longitude,
            current.GetProperty("temperature_2m").GetDouble(),
            current.GetProperty("apparent_temperature").GetDouble(),
            current.GetProperty("relative_humidity_2m").GetInt32(),
            current.GetProperty("wind_speed_10m").GetDouble(),
            current.GetProperty("weather_code").GetInt32(),
            current.GetProperty("is_day").GetInt32() == 1,
            DateTimeOffset.Now,
            days);

        _cached = state;
        _cachedUtc = DateTime.UtcNow;
        return state;
    }

    private async Task<LocationInfo> ResolveLocationAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsService.Settings;
        if (settings.WeatherLatitude is { } latitude &&
            settings.WeatherLongitude is { } longitude &&
            !string.IsNullOrWhiteSpace(settings.WeatherCity))
        {
            return new LocationInfo(
                settings.WeatherCity,
                settings.WeatherCountry,
                settings.WeatherTimeZone,
                latitude,
                longitude);
        }

        var location = await LookupLocationAsync(cancellationToken);

        settings.WeatherCity = location.City;
        settings.WeatherCountry = location.Country;
        settings.WeatherTimeZone = location.TimeZone;
        settings.WeatherLatitude = location.Latitude;
        settings.WeatherLongitude = location.Longitude;
        _settingsService.Save();

        return location;
    }

    private static async Task<LocationInfo> LookupLocationAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(
                await Http.GetStringAsync("https://ipapi.co/json/", cancellationToken));
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var error) &&
                error.ValueKind is JsonValueKind.True)
            {
                throw new InvalidOperationException("ipapi.co reported an error.");
            }

            return new LocationInfo(
                root.GetProperty("city").GetString() ?? "Unknown",
                root.GetProperty("country_name").GetString() ?? string.Empty,
                root.GetProperty("timezone").GetString() ?? "auto",
                root.GetProperty("latitude").GetDouble(),
                root.GetProperty("longitude").GetDouble());
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                              JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // Fall through to the backup geolocation provider.
        }

        using var fallback = JsonDocument.Parse(
            await Http.GetStringAsync("https://ipwho.is/", cancellationToken));
        var fallbackRoot = fallback.RootElement;

        if (fallbackRoot.TryGetProperty("success", out var success) &&
            success.ValueKind is JsonValueKind.False)
        {
            throw new InvalidOperationException("Location lookup failed.");
        }

        return new LocationInfo(
            fallbackRoot.GetProperty("city").GetString() ?? "Unknown",
            fallbackRoot.GetProperty("country").GetString() ?? string.Empty,
            fallbackRoot.TryGetProperty("timezone", out var timezone) &&
            timezone.TryGetProperty("id", out var id)
                ? id.GetString() ?? "auto"
                : "auto",
            fallbackRoot.GetProperty("latitude").GetDouble(),
            fallbackRoot.GetProperty("longitude").GetDouble());
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ISLA/1.3");
        return client;
    }

    public static string Describe(int code) => code switch
    {
        0 => "Clear sky",
        1 => "Mainly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 => "Light drizzle",
        53 => "Drizzle",
        55 => "Dense drizzle",
        56 or 57 => "Freezing drizzle",
        61 => "Light rain",
        63 => "Rain",
        65 => "Heavy rain",
        66 or 67 => "Freezing rain",
        71 => "Light snow",
        73 => "Snow",
        75 => "Heavy snow",
        77 => "Snow grains",
        80 => "Light showers",
        81 => "Showers",
        82 => "Violent showers",
        85 => "Light snow showers",
        86 => "Heavy snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm with hail",
        _ => "Unknown conditions"
    };

    public static string IconKey(int code) => code switch
    {
        0 => "Sun",
        1 or 2 => "Partly",
        3 => "Cloud",
        45 or 48 => "Fog",
        51 or 53 or 55 or 56 or 57 => "Drizzle",
        61 or 63 or 65 or 66 or 67 => "Rain",
        80 or 81 or 82 => "Showers",
        71 or 73 or 75 or 77 or 85 or 86 => "Snow",
        95 or 96 or 99 => "Thunder",
        _ => "Cloud"
    };

    private sealed record LocationInfo(
        string City,
        string Country,
        string TimeZone,
        double Latitude,
        double Longitude);
}
