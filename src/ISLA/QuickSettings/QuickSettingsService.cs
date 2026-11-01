using System.Runtime.InteropServices;
using System.Management;
using Windows.Devices.Radios;

namespace ISLA.QuickSettings;

public sealed record QuickSettingsState(
    double Volume,
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

    public async Task<QuickSettingsState> GetStateAsync()
    {
        var (volume, muted) = GetVolume();
        var power = System.Windows.Forms.SystemInformation.PowerStatus;
        var radios = await GetRadioStatesAsync();
        var brightness = GetBrightness();

        return new QuickSettingsState(
            volume,
            muted,
            power.BatteryLifePercent >= 0 ? (int)Math.Round(power.BatteryLifePercent * 100) : -1,
            power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online,
            radios.WiFi,
            radios.Bluetooth,
            brightness < 0 ? "Unavailable" : $"{brightness}%",
            brightness);
    }

    public void SetVolume(double value)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device);
        var interfaceId = AudioEndpointVolumeId;
        device.Activate(ref interfaceId, ClsCtx.All, IntPtr.Zero, out var endpoint);
        var volume = (IAudioEndpointVolume)endpoint;
        volume.SetMasterVolumeLevelScalar((float)Math.Clamp(value, 0, 1), Guid.Empty);
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

    public void SetBrightness(int value)
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

    private static (double Volume, bool IsMuted) GetVolume()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device);
        var interfaceId = AudioEndpointVolumeId;
        device.Activate(ref interfaceId, ClsCtx.All, IntPtr.Zero, out var endpoint);
        var volume = (IAudioEndpointVolume)endpoint;
        volume.GetMasterVolumeLevelScalar(out var scalar);
        volume.GetMute(out var muted);
        return (scalar, muted);
    }

    private static async Task<(string WiFi, string Bluetooth)> GetRadioStatesAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            var wifi = radios.FirstOrDefault(radio => radio.Kind == RadioKind.WiFi);
            var bluetooth = radios.FirstOrDefault(radio => radio.Kind == RadioKind.Bluetooth);
            return (
                wifi is null ? "Unavailable" : wifi.State.ToString(),
                bluetooth is null ? "Unavailable" : bluetooth.State.ToString());
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or COMException)
        {
            return ("Permission unavailable", "Permission unavailable");
        }
    }

    private enum EDataFlow
    {
        Render
    }

    private enum ERole
    {
        Multimedia
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
        int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, ClsCtx clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.Interface)] out object interfacePointer);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out uint channelCount);
        int SetMasterVolumeLevel(float level, Guid eventContext);
        int SetMasterVolumeLevelScalar(float level, Guid eventContext);
        int GetMasterVolumeLevel(out float level);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channelNumber, float level, Guid eventContext);
        int SetChannelVolumeLevelScalar(uint channelNumber, float level, Guid eventContext);
        int GetChannelVolumeLevel(uint channelNumber, out float level);
        int GetChannelVolumeLevelScalar(uint channelNumber, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, Guid eventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
