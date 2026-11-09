using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using ISLA.Infrastructure;

namespace ISLA.GameStats;

/// <summary>A one-second snapshot of the game and the machine while it runs.</summary>
public sealed record GameStatsSnapshot(
    string ProcessName,
    string Title,
    double? Fps,
    double CpuPercent,
    double RamUsedGb,
    double RamTotalGb,
    double RamPercent,
    double? GpuPercent,
    double? VramUsedGb);

/// <summary>
/// Detects a full-screen game in the foreground, samples CPU / RAM / GPU / VRAM, and reads
/// per-frame presentation timings from PresentMon when it is installed. PresentMon runs as a
/// short-lived child process only while a game is detected, so idle cost is zero.
/// </summary>
public sealed class GameStatsService : IDisposable
{
    private sealed record GameTarget(int ProcessId, string ProcessName, string Title, IntPtr Window);

    private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "isla", "shellexperiencehost", "startmenuexperiencehost", "searchhost",
        "applicationframehost", "textinputhost", "dwm", "csrss", "winlogon", "lockapp",
        "taskmgr", "systemsettings", "gamebar", "xboxgamebar", "xboxgamebarwidgets",
        "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "iexplore",
        "vlc", "mpv", "potplayermini64", "wmplayer", "spotify",
        "devenv", "code", "rider64", "winword", "excel", "powerpnt", "acrobat", "acrord32",
        "windowsterminal", "conhost", "cmd", "powershell", "pwsh", "wt", "notepad", "sublime_text",
        "idea64", "pycharm64", "webstorm64", "goland64", "clion64",
        "obs64", "obs32", "slack", "teams", "ms-teams", "zoom", "telegram",
        "photoshop", "illustrator", "notion", "obsidian", "sumatrapdf"
    };

    private const int DwmwaExtendedFrameBounds = 9;
    private const uint MonitorDefaultToNearest = 2;
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoCap100 = 0x00008000;
    private const uint PdhMoreData = 0x800007D2;
    private const int MaxFrameSamples = 240;
    private const double FrameWindowSeconds = 2.0;

    private readonly object _frameGate = new();
    private readonly Queue<double> _frameTimes = new();

    private Process? _presentMon;
    private string? _presentMonPath;
    private bool _presentMonUnavailable;
    private int _gameProcessId;
    private string _gameProcessName = string.Empty;
    private GameTarget? _lastGame;
    private bool _lastSampleWasGame;
    private DateTime _lastPresentMonAttemptUtc;
    private double _lastFrameTime;

    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;
    private bool _cpuPrimed;

    private IntPtr _pdhQuery;
    private IntPtr _gpuCounter;
    private IntPtr _vramCounter;
    private bool _pdhInitialized;

    /// <summary>Returns null when no game is in the foreground.</summary>
    public GameStatsSnapshot? Sample()
    {
        var target = FindForegroundGame();
        _lastSampleWasGame = target is not null;
        if (target is null)
        {
            StopPresentMon();
            return null;
        }

        var processChanged = target.ProcessId != _gameProcessId ||
                             !string.Equals(target.ProcessName, _gameProcessName, StringComparison.OrdinalIgnoreCase);
        if (processChanged || PresentMonExited())
        {
            StopPresentMon();
            _gameProcessId = target.ProcessId;
            _gameProcessName = target.ProcessName;
            StartPresentMon(target);
        }
        else if (_presentMon is null && !_presentMonUnavailable)
        {
            // A previous start was skipped or failed; retry on a later sample.
            StartPresentMon(target);
        }

        var cpu = SampleCpu();
        var (ramUsed, ramTotal, ramPercent) = SampleRam();
        var (gpu, vram) = SampleGpu(_gameProcessId);

        return new GameStatsSnapshot(
            target.ProcessName,
            target.Title,
            GetFps(),
            cpu,
            ramUsed,
            ramTotal,
            ramPercent,
            gpu,
            vram);
    }

    public void Dispose()
    {
        StopPresentMon();
        ClosePdh();
    }

    // =================================================================================
    //  Game detection
    // =================================================================================

    private GameTarget? FindForegroundGame()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return null;
        }

        GetWindowThreadProcessId(foreground, out var processId);
        if (processId == 0)
        {
            return null;
        }

        // Opening the ISLA menu makes our own window the foreground window. That must not end
        // the session: the game is still full screen on the same monitor behind the island.
        if (processId == (uint)Environment.ProcessId)
        {
            return _lastSampleWasGame ? ReuseLastGame() : null;
        }

        if (IsIconic(foreground) || !CoversMonitor(foreground))
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var name = process.ProcessName;
            if (string.IsNullOrWhiteSpace(name) || ExcludedProcesses.Contains(name))
            {
                return null;
            }

            var title = process.MainWindowTitle;
            var target = new GameTarget(
                (int)processId,
                name,
                string.IsNullOrWhiteSpace(title) ? name : title,
                foreground);
            _lastGame = target;
            return target;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                              System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The game is still the window behind the island when ISLA has focus. Reuse it only while
    /// it is alive and still covering its monitor, so closing the game ends the session.
    /// </summary>
    private GameTarget? ReuseLastGame()
    {
        if (_lastGame is not { } game)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(game.ProcessId);
            if (process.HasExited || game.Window == IntPtr.Zero ||
                !IsWindow(game.Window) || IsIconic(game.Window) ||
                !CoversMonitor(game.Window))
            {
                _lastGame = null;
                return null;
            }

            return game;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                              System.ComponentModel.Win32Exception)
        {
            _lastGame = null;
            return null;
        }
    }

    private static bool CoversMonitor(IntPtr window)
    {
        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var bounds = new Rect();
        if (DwmGetWindowAttribute(window, DwmwaExtendedFrameBounds, out bounds, Marshal.SizeOf<Rect>()) != 0 &&
            !GetWindowRect(window, out bounds))
        {
            return false;
        }

        const int tolerance = 8;
        return bounds.Left <= info.Monitor.Left + tolerance &&
               bounds.Top <= info.Monitor.Top + tolerance &&
               bounds.Right >= info.Monitor.Right - tolerance &&
               bounds.Bottom >= info.Monitor.Bottom - tolerance;
    }

    // =================================================================================
    //  FPS via PresentMon
    // =================================================================================

    private void StartPresentMon(GameTarget target)
    {
        if (_presentMonUnavailable)
        {
            return;
        }

        // A restarted game or a crashed PresentMon must not spawn a process every second.
        var now = DateTime.UtcNow;
        if (now - _lastPresentMonAttemptUtc < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastPresentMonAttemptUtc = now;

        _presentMonPath ??= FindPresentMon();
        if (_presentMonPath is null)
        {
            _presentMonUnavailable = true;
            AppLog.Info("PresentMon was not found; game FPS will be unavailable.");
            return;
        }

        lock (_frameGate)
        {
            _frameTimes.Clear();
            _lastFrameTime = 0;
        }

        var executable = target.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? target.ProcessName
            : $"{target.ProcessName}.exe";

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _presentMonPath,
                Arguments = $"-process_name \"{executable}\" -output_stdout -no_top " +
                            "-session_name ISLA-GameStats -stop_existing_session -terminate_on_proc_exit",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += PresentMon_OutputDataReceived;
            process.ErrorDataReceived += (_, _) => { };
            if (!process.Start())
            {
                process.Dispose();
                _presentMonUnavailable = true;
                return;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _presentMon = process;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or
                                              InvalidOperationException or IOException)
        {
            AppLog.Warn("PresentMon could not start; game FPS will be unavailable.", exception);
            _presentMonUnavailable = true;
        }
    }

    private void PresentMon_OutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        var line = e.Data;
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("Application,", StringComparison.Ordinal))
        {
            return;
        }

        var fields = line.Split(',');
        if (fields.Length < 10 ||
            !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var processId) ||
            processId != _gameProcessId ||
            !double.TryParse(fields[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var time))
        {
            return;
        }

        lock (_frameGate)
        {
            _frameTimes.Enqueue(time);
            _lastFrameTime = time;
            while (_frameTimes.Count > MaxFrameSamples ||
                   (_frameTimes.Count > 0 && _frameTimes.Peek() < time - FrameWindowSeconds))
            {
                _frameTimes.Dequeue();
            }
        }
    }

    private double? GetFps()
    {
        if (_presentMon is null)
        {
            return null;
        }

        try
        {
            if (_presentMon.HasExited)
            {
                return null;
            }
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        lock (_frameGate)
        {
            if (_frameTimes.Count < 4)
            {
                return null;
            }

            var first = _frameTimes.Peek();
            var span = _lastFrameTime - first;
            if (span < 0.5)
            {
                return null;
            }

            // The newest sample is included, so the interval count is one less than the samples.
            return (_frameTimes.Count - 1) / span;
        }
    }

    private bool PresentMonExited()
    {
        if (_presentMon is null)
        {
            return false;
        }

        try
        {
            return _presentMon.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private void StopPresentMon()
    {
        var process = _presentMon;
        _presentMon = null;
        _gameProcessId = 0;
        _gameProcessName = string.Empty;

        lock (_frameGate)
        {
            _frameTimes.Clear();
            _lastFrameTime = 0;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                              System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The process may have already exited.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string? FindPresentMon()
    {
        string[] candidates =
        [
            @"C:\Program Files\AMD\CNext\CNext\PresentMon-x64.exe",
            @"C:\Program Files\AMD\CNext\CNext\PresentMon.exe",
            @"C:\Program Files\PresentMon\PresentMon-x64.exe",
            @"C:\Program Files\PresentMon\PresentMon.exe",
            @"C:\Program Files\Intel\PresentMon\PresentMon.exe",
            @"C:\Program Files (x86)\PresentMon\PresentMon.exe"
        ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "PresentMon.exe", "PresentMon-x64.exe" })
            {
                try
                {
                    var candidate = Path.Combine(folder.Trim(), name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        return null;
    }

    // =================================================================================
    //  CPU / RAM
    // =================================================================================

    private double SampleCpu()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return 0;
        }

        var idle = ToUInt64(idleTime);
        var kernel = ToUInt64(kernelTime);
        var user = ToUInt64(userTime);

        if (!_cpuPrimed)
        {
            _lastIdle = idle;
            _lastKernel = kernel;
            _lastUser = user;
            _cpuPrimed = true;
            return 0;
        }

        var idleDelta = idle - _lastIdle;
        var totalDelta = (kernel - _lastKernel) + (user - _lastUser);
        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;

        if (totalDelta == 0)
        {
            return 0;
        }

        // Kernel time includes idle time, so subtract it from the busy share.
        var busy = totalDelta > idleDelta ? totalDelta - idleDelta : 0;
        return Math.Clamp(busy * 100.0 / totalDelta, 0, 100);
    }

    private static (double UsedGb, double TotalGb, double Percent) SampleRam()
    {
        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref memory))
        {
            return (0, 0, 0);
        }

        const double bytesPerGb = 1024.0 * 1024.0 * 1024.0;
        var total = memory.TotalPhysical / bytesPerGb;
        var used = (memory.TotalPhysical - memory.AvailablePhysical) / bytesPerGb;
        return (used, total, memory.MemoryLoad);
    }

    // =================================================================================
    //  GPU / VRAM via PDH
    // =================================================================================

    private (double? Gpu, double? Vram) SampleGpu(int processId)
    {
        EnsurePdh();
        if (_pdhQuery == IntPtr.Zero || PdhCollectQueryData(_pdhQuery) != 0)
        {
            return (null, null);
        }

        double? gpu = null;
        if (_gpuCounter != IntPtr.Zero)
        {
            var (sum, found3D) = SumPdhCounter(_gpuCounter, processId, "engtype_3d");
            if (!found3D)
            {
                (sum, _) = SumPdhCounter(_gpuCounter, processId, null);
            }

            if (found3D || sum > 0)
            {
                gpu = Math.Clamp(sum, 0, 100);
            }
        }

        double? vram = null;
        if (_vramCounter != IntPtr.Zero)
        {
            var (bytes, found) = SumPdhCounter(_vramCounter, processId, null);
            if (found)
            {
                vram = bytes / (1024.0 * 1024.0 * 1024.0);
            }
        }

        return (gpu, vram);
    }

    private void EnsurePdh()
    {
        if (_pdhInitialized)
        {
            return;
        }

        _pdhInitialized = true;
        if (PdhOpenQuery(null, IntPtr.Zero, out _pdhQuery) != 0)
        {
            _pdhQuery = IntPtr.Zero;
            return;
        }

        if (PdhAddEnglishCounter(_pdhQuery, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpuCounter) != 0)
        {
            _gpuCounter = IntPtr.Zero;
        }

        if (PdhAddEnglishCounter(_pdhQuery, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out _vramCounter) != 0)
        {
            _vramCounter = IntPtr.Zero;
        }

        if (_gpuCounter == IntPtr.Zero && _vramCounter == IntPtr.Zero)
        {
            ClosePdh();
        }
    }

    private (double Sum, bool Found) SumPdhCounter(IntPtr counter, int processId, string? requiredNamePart)
    {
        uint bufferSize = 0;
        uint itemCount = 0;
        var status = PdhGetFormattedCounterArray(
            counter,
            PdhFmtDouble | PdhFmtNoCap100,
            ref bufferSize,
            ref itemCount,
            IntPtr.Zero);

        if (status != PdhMoreData || bufferSize == 0 || itemCount == 0)
        {
            return (0, false);
        }

        var buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            status = PdhGetFormattedCounterArray(
                counter,
                PdhFmtDouble | PdhFmtNoCap100,
                ref bufferSize,
                ref itemCount,
                buffer);

            if (status != 0)
            {
                return (0, false);
            }

            var itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
            var processToken = $"pid_{processId}_";
            double sum = 0;
            var found = false;

            for (var index = 0u; index < itemCount; index++)
            {
                var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(
                    IntPtr.Add(buffer, (int)(index * (uint)itemSize)));
                var name = Marshal.PtrToStringUni(item.Name) ?? string.Empty;

                if (!name.Contains(processToken, StringComparison.OrdinalIgnoreCase) ||
                    (requiredNamePart is not null &&
                     !name.Contains(requiredNamePart, StringComparison.OrdinalIgnoreCase)) ||
                    item.Value.Status is not (0 or 1))
                {
                    continue;
                }

                sum += item.Value.Value;
                found = true;
            }

            return (sum, found);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void ClosePdh()
    {
        if (_pdhQuery != IntPtr.Zero)
        {
            PdhCloseQuery(_pdhQuery);
            _pdhQuery = IntPtr.Zero;
        }

        _gpuCounter = IntPtr.Zero;
        _vramCounter = IntPtr.Zero;
    }

    private static ulong ToUInt64(FileTime time) => ((ulong)time.High << 32) | time.Low;

    // =================================================================================
    //  Interop
    // =================================================================================

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint Status;
        public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public IntPtr Name;
        public PdhFmtCounterValue Value;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(
        IntPtr counter,
        uint format,
        ref uint bufferSize,
        ref uint itemCount,
        IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
