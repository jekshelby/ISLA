using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace ISLA.Infrastructure;

/// <summary>
/// Best-effort app icon lookup for notification banners.
/// Packaged apps expose a logo through WinRT; unpackaged apps (Discord, Spotify, ...) are
/// matched through their running process or Start Menu shortcut. When nothing is found the
/// caller draws a coloured initial instead.
/// </summary>
public static class AppIconResolver
{
    private const int MaxCachedIcons = 32;
    private const int ReadChunkSize = 4096;
    private static readonly ConcurrentDictionary<string, byte[]?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<byte[]?> TryGetIconAsync(AppInfo? info, string appName)
    {
        var key = string.IsNullOrWhiteSpace(appName) ? "unknown" : appName;
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var icon = await ResolveAsync(info, appName);

        // A handful of apps is enough; never let the cache grow without a bound.
        if (Cache.Count >= MaxCachedIcons)
        {
            Cache.Clear();
        }

        Cache[key] = icon;
        return icon;
    }

    private static async Task<byte[]?> ResolveAsync(AppInfo? info, string appName)
    {
        if (string.Equals(appName, "ISLA", StringComparison.OrdinalIgnoreCase) &&
            Environment.ProcessPath is { } ownPath)
        {
            return ExtractIconBytes(ownPath);
        }

        if (info is not null)
        {
            try
            {
                var logo = info.DisplayInfo.GetLogo(new Windows.Foundation.Size(32, 32));
                if (logo is not null)
                {
                    var bytes = await ReadLogoAsync(logo);
                    if (bytes is { Length: > 0 })
                    {
                        return bytes;
                    }
                }
            }
            catch (Exception)
            {
                // Unpackaged apps have no package logo; fall through to the shell icon.
            }

            var processPath = FindProcessPath(info.AppUserModelId, appName);
            if (processPath is not null)
            {
                return ExtractIconBytes(processPath);
            }
        }

        var shortcut = FindStartMenuShortcut(appName);
        return shortcut is null ? null : ExtractIconBytes(shortcut);
    }

    private static async Task<byte[]?> ReadLogoAsync(RandomAccessStreamReference logo)
    {
        using var stream = await logo.OpenReadAsync();
        using var reader = new DataReader(stream);
        using var memory = new MemoryStream();
        var remaining = stream.Size;
        var chunk = new byte[ReadChunkSize];

        while (remaining > 0)
        {
            var loaded = await reader.LoadAsync((uint)Math.Min(remaining, ReadChunkSize));
            if (loaded == 0)
            {
                break;
            }

            reader.ReadBytes(chunk);
            memory.Write(chunk, 0, (int)loaded);
            remaining -= loaded;
        }

        return memory.ToArray();
    }

    private static string? FindProcessPath(string? appUserModelId, string appName)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(appUserModelId))
        {
            // e.g. com.squirrel.Discord.Discord -> Discord
            candidates.AddRange(appUserModelId
                .Split(['.', '!', '_', '-'], StringSplitOptions.RemoveEmptyEntries)
                .Where(part => part.Length >= 3)
                .Reverse());
        }

        candidates.Add(appName.Replace(" ", string.Empty, StringComparison.Ordinal));
        candidates.Add(appName);

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(candidate))
                {
                    using (process)
                    {
                        try
                        {
                            var path = process.MainModule?.FileName;
                            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                            {
                                return path;
                            }
                        }
                        catch (Exception)
                        {
                            // Protected processes do not expose their module path.
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Ignore names that are not valid process names.
            }
        }

        return null;
    }

    private static string? FindStartMenuShortcut(string appName)
    {
        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
                 })
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories))
                {
                    if (string.Equals(Path.GetFileNameWithoutExtension(file), appName, StringComparison.OrdinalIgnoreCase))
                    {
                        return file;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip folders we cannot read.
            }
        }

        return null;
    }

    private static byte[]? ExtractIconBytes(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                return null;
            }

            using var bitmap = icon.ToBitmap();
            using var memory = new MemoryStream();
            bitmap.Save(memory, ImageFormat.Png);
            return memory.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or
                                   System.Runtime.InteropServices.ExternalException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
