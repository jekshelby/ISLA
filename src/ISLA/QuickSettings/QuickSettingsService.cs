using System.Runtime.InteropServices;
using System.Management;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace ISLA.QuickSettings;

public sealed record QuickSettingsState(
    double? Volume,
    bool IsMuted,
    int BatteryPercent,
    bool IsPluggedIn,
    string WiFiStatus,
    string BluetoothStatus,
    string BrightnessStatus,
    int BrightnessPercent);

public sealed class QuickSettingsService
{
    private static Guid AudioEndpointVolumeId => new("5CDF2C82-841E-4546-9722-0CF74078229A");

    // Slider drags fire many updates; the gates let only one background write run at a time
    // and collapse the queue to the newest value.
    private readonly SemaphoreSlim _volumeGate = new(1, 1);
    private readonly SemaphoreSlim _brightnessGate = new(1, 1);
    private double _requestedVolume = double.NaN;
    private int _requestedBrightness = -1;

    public async Task<QuickSettingsState> GetStateAsync(bool includeHardware = true)
    {
        if (!includeHardware)
        {
            // Lightweight path for the periodic wireless watcher: no COM/WMI and no SSID
            // lookup, just the radio on/off state.
            var radioState = await GetRadioStatesAsync(includeNetworkName: false).ConfigureAwait(false);
            return new QuickSettingsState(
                null,
                false,
                -1,
                false,
                radioState.WiFi,
                radioState.Bluetooth,
                "Unavailable",
                -1);
        }

        // COM (audio endpoint) and WMI (brightness/battery) calls can block for a long time.
        // Run them on the thread pool so the island never freezes while they are queried.
        var audioTask = Task.Run(TryGetVolume);
        var brightnessTask = Task.Run(GetBrightness);
        var powerTask = Task.Run(ReadPowerState);
        var radiosTask = GetRadioStatesAsync();

        var audio = await audioTask.ConfigureAwait(false);
        var brightness = await brightnessTask.ConfigureAwait(false);
        var power = await powerTask.ConfigureAwait(false);
        var radios = await radiosTask.ConfigureAwait(false);

        return new QuickSettingsState(
            audio?.Volume,
            audio?.IsMuted ?? false,
            power.Percent,
            power.PluggedIn,
            radios.WiFi,
            radios.Bluetooth,
            brightness < 0 ? "Unavailable" : $"{brightness}%",
            brightness);
    }

    private static (int Percent, bool PluggedIn) ReadPowerState()
    {
        var power = System.Windows.Forms.SystemInformation.PowerStatus;
        return (GetBatteryPercent(power.BatteryLifePercent),
            power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online);
    }

    private static int GetBatteryPercent(float systemPercent)
    {
        if (systemPercent >= 0 && systemPercent <= 1)
        {
            return Math.Clamp((int)Math.Round(systemPercent * 100), 0, 100);
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT EstimatedChargeRemaining FROM Win32_Battery");
            using var batteries = searcher.Get();
            var battery = batteries.Cast<ManagementObject>().FirstOrDefault();
            if (battery?["EstimatedChargeRemaining"] is not null &&
                int.TryParse(battery["EstimatedChargeRemaining"].ToString(), out var percent))
            {
                return Math.Clamp(percent, 0, 100);
            }
        }
        catch (ManagementException)
        {
            return -1;
        }
        catch (UnauthorizedAccessException)
        {
            return -1;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }

        return -1;
    }

    /// <summary>
    /// Applies the master volume on the thread pool, coalescing rapid slider updates so only
    /// the newest value reaches the audio endpoint.
    /// </summary>
    public async Task SetVolumeAsync(double value)
    {
        Volatile.Write(ref _requestedVolume, Math.Clamp(value, 0, 1));

        await _volumeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                while (true)
                {
                    var target = Interlocked.Exchange(ref _requestedVolume, double.NaN);
                    if (double.IsNaN(target))
                    {
                        break;
                    }

                    SetVolumeCore(target);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            _volumeGate.Release();
        }
    }

    private static void SetVolumeCore(double value)
    {
        IAudioEndpointVolume? volume = null;
        try
        {
            volume = TryOpenEndpointVolume()
                ?? throw new InvalidOperationException("No audio output device is available.");

            var result = volume.SetMasterVolumeLevelScalar((float)Math.Clamp(value, 0, 1), Guid.Empty);
            if (result < 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }
        }
        finally
        {
            ReleaseComObject(volume);
        }
    }

    public async Task ToggleRadioAsync(RadioKind kind)
    {
        var radios = await Radio.GetRadiosAsync();
        var radio = radios.FirstOrDefault(candidate => candidate.Kind == kind);
        if (radio is null)
        {
            throw new InvalidOperationException($"{kind} radio is unavailable.");
        }

        var targetState = radio.State == RadioState.On ? RadioState.Off : RadioState.On;
        await radio.SetStateAsync(targetState);
    }

    /// <summary>
    /// Applies brightness on the thread pool. WMI is slow on many machines, and the gate keeps
    /// a fast slider drag from queueing one WMI call per pixel.
    /// </summary>
    public async Task SetBrightnessAsync(int value)
    {
        Volatile.Write(ref _requestedBrightness, Math.Clamp(value, 0, 100));

        await _brightnessGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                while (true)
                {
                    var target = Interlocked.Exchange(ref _requestedBrightness, -1);
                    if (target < 0)
                    {
                        break;
                    }

                    SetBrightnessCore(target);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            _brightnessGate.Release();
        }
    }

    private static void SetBrightnessCore(int value)
    {
        var brightness = Math.Clamp(value, 0, 100);
        using var searcher = new ManagementObjectSearcher(
            "root\\WMI",
            "SELECT InstanceName FROM WmiMonitorBrightnessMethods");
        using var monitors = searcher.Get();
        var foundMonitor = false;
        foreach (ManagementObject monitor in monitors)
        {
            monitor.InvokeMethod("WmiSetBrightness", new object[] { 1, brightness });
            foundMonitor = true;
        }

        if (!foundMonitor)
        {
            throw new InvalidOperationException("No internal monitor brightness controller was found.");
        }
    }

    private static int GetBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\WMI",
                "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            using var monitors = searcher.Get();
            foreach (ManagementObject monitor in monitors)
            {
                return Convert.ToInt32(monitor["CurrentBrightness"]);
            }
        }
        catch (ManagementException)
        {
        }

        return -1;
    }

    /// <summary>
    /// Reads the master volume of the default multimedia endpoint. Returns null when no audio
    /// output is available, so the rest of the quick-settings state can still be shown.
    /// </summary>
    private static (double Volume, bool IsMuted)? TryGetVolume()
    {
        IAudioEndpointVolume? volume = null;
        try
        {
            volume = TryOpenEndpointVolume();
            if (volume is null)
            {
                return null;
            }

            var scalarResult = volume.GetMasterVolumeLevelScalar(out var scalar);
            var muteResult = volume.GetMute(out var muted);
            if (scalarResult < 0 || muteResult < 0)
            {
                return null;
            }

            return (Math.Clamp(scalar, 0f, 1f), muted);
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            ReleaseComObject(volume);
        }
    }

    /// <summary>
    /// Opens the default multimedia endpoint. The returned interface owns its own COM
    /// reference; the device and enumerator are released before returning.
    /// </summary>
    private static IAudioEndpointVolume? TryOpenEndpointVolume()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            var result = enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device);
            if (result < 0 || device is null)
            {
                return null;
            }

            var interfaceId = AudioEndpointVolumeId;
            result = device.Activate(ref interfaceId, ClsCtx.All, IntPtr.Zero, out var endpoint);
            if (result < 0)
            {
                return null;
            }

            return endpoint as IAudioEndpointVolume;
        }
        catch (COMException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
        finally
        {
            ReleaseComObject(device);
            ReleaseComObject(enumerator);
        }
    }

    private static void ReleaseComObject(object? comObject)
    {
        if (comObject is null || !Marshal.IsComObject(comObject))
        {
            return;
        }

        try
        {
            Marshal.ReleaseComObject(comObject);
        }
        catch (ArgumentException)
        {
            // Already released: there is nothing left to do.
        }
    }

    private static async Task<(string WiFi, string Bluetooth)> GetRadioStatesAsync(
        bool includeNetworkName = true)
    {
        try
        {
            var radios = await Radio.GetRadiosAsync().AsTask().ConfigureAwait(false);
            var wifi = radios.FirstOrDefault(radio => radio.Kind == RadioKind.WiFi);
            var bluetooth = radios.FirstOrDefault(radio => radio.Kind == RadioKind.Bluetooth);
            var wifiStatus = wifi is null ? "Unavailable" : wifi.State.ToString();
            if (wifiStatus == "On" && includeNetworkName)
            {
                var networkName = await GetConnectedWifiNameAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(networkName))
                {
                    wifiStatus = $"On — {networkName}";
                }
            }

            return (wifiStatus, bluetooth is null ? "Unavailable" : bluetooth.State.ToString());
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or COMException)
        {
            return ("Permission unavailable", "Permission unavailable");
        }
    }

    /// <summary>
    /// Fast path: asks Windows for the connected WLAN profile (no process spawn, ~instant).
    /// Returns null when it can't tell, in which case the netsh fallback is used.
    /// </summary>
    private static string? GetConnectedSsidFromProfiles()
    {
        try
        {
            // Only the active profile matters; enumerating every profile is much slower.
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null ||
                !profile.IsWlanConnectionProfile ||
                profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None)
            {
                return null;
            }

            var ssid = profile.WlanConnectionProfileDetails?.GetConnectedSsid();
            return string.IsNullOrWhiteSpace(ssid) ? null : ssid;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            // fall through to netsh
        }

        return null;
    }

    private static async Task<string?> GetConnectedWifiNameAsync()
    {
        var fastName = GetConnectedSsidFromProfiles();
        if (!string.IsNullOrWhiteSpace(fastName))
        {
            return fastName;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = "wlan show interfaces",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            });
            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return null;
            }

            var match = Regex.Match(output, @"(?m)^\s*SSID\s*:\s*(?!$)(.+)$");
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private enum EDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2
    }

    private enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2
    }

    [Flags]
    private enum ClsCtx
    {
        InprocServer = 0x1,
        All = 0x17
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, ClsCtx clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.Interface)] out object interfacePointer);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float level, Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channelNumber, float level, Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channelNumber, float level, Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channelNumber, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channelNumber, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
