using System.Linq;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;

namespace ISLA.QuickSettings;

/// <summary>One row in the Wi-Fi / Bluetooth detail panel. Signal is 0-4 for Wi-Fi, -1 otherwise.</summary>
public sealed record RadioListItem(string Id, string Name, string Detail, bool IsConnected, int Signal);

/// <summary>
/// Lists nearby Wi-Fi networks and paired Bluetooth devices for the island's quick-settings
/// detail panel. Everything is best effort: failures come back as a message, never an exception.
/// </summary>
public sealed class WirelessListService
{
    private const int MaxItems = 8;

    public async Task<(IReadOnlyList<RadioListItem> Items, string? Error)> ScanWiFiAsync()
    {
        try
        {
            var access = await WiFiAdapter.RequestAccessAsync();
            if (access != WiFiAccessStatus.Allowed)
            {
                return (Array.Empty<RadioListItem>(),
                    "Windows blocked Wi-Fi scanning. Allow location access for desktop apps.");
            }

            var adapters = await WiFiAdapter.FindAllAdaptersAsync();
            if (adapters.Count == 0)
            {
                return (Array.Empty<RadioListItem>(), "No Wi-Fi adapter found");
            }

            var adapter = adapters[0];
            await adapter.ScanAsync();

            string? connectedSsid = null;
            try
            {
                var profile = await adapter.NetworkAdapter.GetConnectedProfileAsync();
                connectedSsid = profile?.WlanConnectionProfileDetails?.GetConnectedSsid();
            }
            catch (Exception)
            {
                // Not connected / not readable: the list simply has no "Connected" row.
            }

            var strongest = new Dictionary<string, RadioListItem>(StringComparer.Ordinal);
            foreach (var network in adapter.NetworkReport.AvailableNetworks)
            {
                if (string.IsNullOrWhiteSpace(network.Ssid))
                {
                    continue;
                }

                var connected = string.Equals(network.Ssid, connectedSsid, StringComparison.Ordinal);
                var secured = network.SecuritySettings.NetworkAuthenticationType != NetworkAuthenticationType.Open80211;
                var item = new RadioListItem(
                    network.Ssid,
                    network.Ssid,
                    connected ? "Connected" : secured ? "Secured" : "Open",
                    connected,
                    (int)network.SignalBars);

                if (!strongest.TryGetValue(network.Ssid, out var existing) || item.Signal > existing.Signal)
                {
                    strongest[network.Ssid] = item;
                }
            }

            var ordered = strongest.Values
                .OrderByDescending(i => i.IsConnected)
                .ThenByDescending(i => i.Signal)
                .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(MaxItems)
                .ToList();
            return (ordered, null);
        }
        catch (Exception exception)
        {
            return (Array.Empty<RadioListItem>(), exception.Message);
        }
    }

    /// <summary>
    /// Connects to a network Windows already knows (or an open one). Secured networks that
    /// have no saved profile report "Password required" so the caller can hand over to Windows.
    /// </summary>
    public async Task<(bool Success, string Message)> ConnectWiFiAsync(string ssid)
    {
        try
        {
            var adapters = await WiFiAdapter.FindAllAdaptersAsync();
            if (adapters.Count == 0)
            {
                return (false, "No Wi-Fi adapter found");
            }

            var adapter = adapters[0];
            var network = adapter.NetworkReport?.AvailableNetworks.FirstOrDefault(n => n.Ssid == ssid);
            if (network is null)
            {
                await adapter.ScanAsync();
                network = adapter.NetworkReport?.AvailableNetworks.FirstOrDefault(n => n.Ssid == ssid);
            }

            if (network is null)
            {
                return (false, "Network is no longer in range");
            }

            var result = await adapter.ConnectAsync(network, WiFiReconnectionKind.Automatic);
            if (result.ConnectionStatus == WiFiConnectionStatus.Success)
            {
                return (true, $"Connected to {ssid}");
            }

            return (false, result.ConnectionStatus switch
            {
                WiFiConnectionStatus.InvalidCredential => "Password required",
                WiFiConnectionStatus.UnsupportedAuthenticationProtocol => "Password required",
                WiFiConnectionStatus.AccessRevoked => "Access denied",
                WiFiConnectionStatus.Timeout => "Connection timed out",
                WiFiConnectionStatus.NetworkNotAvailable => "Network not available",
                _ => "Couldn't connect"
            });
        }
        catch (Exception exception)
        {
            return (false, exception.Message);
        }
    }

    /// <summary>Paired Bluetooth (classic + LE) devices, connected ones first.</summary>
    public async Task<IReadOnlyList<RadioListItem>> GetBluetoothDevicesAsync()
    {
        var byName = new Dictionary<string, RadioListItem>(StringComparer.CurrentCultureIgnoreCase);

        void Add(string id, string name, bool connected)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            if (byName.TryGetValue(name, out var existing) && (existing.IsConnected || !connected))
            {
                return;
            }

            byName[name] = new RadioListItem(id, name, connected ? "Connected" : "Paired", connected, -1);
        }

        try
        {
            var classic = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
            foreach (var info in classic)
            {
                var connected = false;
                try
                {
                    using var device = await BluetoothDevice.FromIdAsync(info.Id);
                    connected = device?.ConnectionStatus == BluetoothConnectionStatus.Connected;
                }
                catch (Exception)
                {
                    // Treat unreadable devices as "paired".
                }

                Add(info.Id, info.Name, connected);
            }
        }
        catch (Exception)
        {
        }

        try
        {
            var lowEnergy = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
            foreach (var info in lowEnergy)
            {
                var connected = false;
                try
                {
                    using var device = await BluetoothLEDevice.FromIdAsync(info.Id);
                    connected = device?.ConnectionStatus == BluetoothConnectionStatus.Connected;
                }
                catch (Exception)
                {
                }

                Add(info.Id, info.Name, connected);
            }
        }
        catch (Exception)
        {
        }

        return byName.Values
            .OrderByDescending(i => i.IsConnected)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxItems)
            .ToList();
    }
}
