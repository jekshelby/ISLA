using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace ISLAInstaller;

public partial class MainWindow : Window
{
    private const string AppVersion = "1.2.1";
    private const string Publisher = "ISLA Development";
    private const string UninstallRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ISLA";

    // Program Files (all users). The setup runs elevated (see app.manifest), so it can write here.
    private readonly string _installDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ISLA");

    // All-users Start Menu: this is what makes ISLA show up in Windows Search / the Start list.
    private readonly string _shortcutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "ISLA.lnk");

    // Where older versions of this installer put things (per-user). Cleaned up automatically.
    private readonly string _legacyDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ISLA");
    private readonly string _legacyShortcut = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "ISLA.lnk");

    private string AppExecutable => Path.Combine(_installDirectory, "ISLA.exe");
    private string UninstallerPath => Path.Combine(_installDirectory, "ISLA-Uninstall.exe");

    private readonly bool _isUninstalling;
    private bool _started;
    private bool _finished;
    private bool _installed;

    public MainWindow()
    {
        InitializeComponent();
        _isUninstalling = Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, "--uninstall", StringComparison.OrdinalIgnoreCase));
        if (_isUninstalling)
        {
            Title = "Uninstall ISLA";
            TitleText.Text = "Uninstall ISLA";
            DescriptionText.Text = "Remove ISLA, its Windows notification identity, and its Start Menu shortcut.";
            NextButton.Content = "Uninstall";
        }

        StatusText.Text = $"Installation folder: {_installDirectory}";
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_finished)
        {
            if (_installed && LaunchCheck.IsChecked == true)
            {
                LaunchApp();
            }

            Close();
            return;
        }

        if (_started)
        {
            return;
        }

        _started = true;
        NextButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        InstallProgress.Visibility = Visibility.Visible;
        if (_isUninstalling)
        {
            TitleText.Text = "Removing ISLA";
            DescriptionText.Text = "Please wait while ISLA is removed.";
            UninstallAsync();
        }
        else
        {
            TitleText.Text = "Installing ISLA";
            DescriptionText.Text = "Please wait while ISLA is installed.";
            InstallAsync();
        }
    }

    // ---------------------------------------------------------------------------------
    //  Install
    // ---------------------------------------------------------------------------------

    private async void InstallAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                StopRunningApp();
                UnregisterIdentityPackage();
                RemoveLegacyInstall();

                if (Directory.Exists(_installDirectory))
                {
                    Directory.Delete(_installDirectory, true);
                }

                Directory.CreateDirectory(_installDirectory);
                var resourceName = Assembly.GetExecutingAssembly().GetManifestResourceNames()
                    .SingleOrDefault(name => name.EndsWith("ISLA-payload.zip", StringComparison.OrdinalIgnoreCase));
                using var payload = resourceName is null
                    ? throw new InvalidOperationException("The installer payload is missing.")
                    : Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
                        ?? throw new InvalidOperationException("The installer payload is missing.");
                using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
                archive.ExtractToDirectory(_installDirectory, true);

                RegisterIdentityPackage();
                CreateShortcut(_shortcutPath);
                File.Copy(Environment.ProcessPath
                    ?? throw new InvalidOperationException("Could not locate the setup executable."),
                    UninstallerPath, true);
                WriteUninstallRegistration();
            });

            TitleText.Text = "ISLA installed";
            DescriptionText.Text = "ISLA 1.2.1 was installed successfully.";
            StatusText.Text = "Find ISLA in the Start Menu or Windows Search. It is also listed under Settings > Apps.";
            InstallProgress.Visibility = Visibility.Collapsed;
            LaunchCheck.Visibility = Visibility.Visible;
            NextButton.Content = "Finish";
            NextButton.IsEnabled = true;
            _installed = true;
            _finished = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "ISLA Setup", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    // ---------------------------------------------------------------------------------
    //  Uninstall
    // ---------------------------------------------------------------------------------

    private async void UninstallAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                StopRunningApp();
                UnregisterIdentityPackage();
                DeleteFileQuietly(_shortcutPath);
                DeleteFileQuietly(_legacyShortcut);
                Registry.LocalMachine.DeleteSubKeyTree(UninstallRegistryKey, false);
                ScheduleDirectoryRemoval(_installDirectory);
                ScheduleDirectoryRemoval(_legacyDirectory);
            });

            TitleText.Text = "ISLA removed";
            DescriptionText.Text = "ISLA will be fully removed when this window closes.";
            StatusText.Text = "Click Finish to close the uninstaller.";
            InstallProgress.Visibility = Visibility.Collapsed;
            NextButton.Content = "Finish";
            NextButton.IsEnabled = true;
            _finished = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "ISLA Uninstall", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    /// <summary>
    /// The uninstaller runs from inside the folder it removes, so a small script keeps retrying
    /// until the setup process has exited and the folder can really be deleted.
    /// </summary>
    private static void ScheduleDirectoryRemoval(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var script = Path.Combine(Path.GetTempPath(), $"ISLA-Uninstall-{Guid.NewGuid():N}.cmd");
        var escaped = directory.Replace("\"", "\"\"");
        var lines = new[]
        {
            "@echo off",
            "for /l %%i in (1,1,60) do (",
            $"  rmdir /s /q \"{escaped}\" 2>nul",
            $"  if not exist \"{escaped}\" goto done",
            "  timeout /t 1 /nobreak >nul",
            ")",
            ":done",
            "del \"%~f0\""
        };
        File.WriteAllText(script, string.Join("\r\n", lines) + "\r\n");
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c start \"\" /b \"{script}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    // ---------------------------------------------------------------------------------
    //  Helpers
    // ---------------------------------------------------------------------------------

    private static void StopRunningApp()
    {
        foreach (var process in Process.GetProcessesByName("ISLA"))
        {
            try
            {
                process.Kill(true);
                process.WaitForExit(5000);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone or not ours to stop: the file copy below will report a real problem.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private void RemoveLegacyInstall()
    {
        DeleteFileQuietly(_legacyShortcut);
        try
        {
            if (Directory.Exists(_legacyDirectory))
            {
                Directory.Delete(_legacyDirectory, true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Old per-user copy is locked: harmless, the new install does not depend on it.
        }
    }

    private static void DeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Adds ISLA to Settings > Apps (with icon, version, publisher and an Uninstall button).</summary>
    private void WriteUninstallRegistration()
    {
        long bytes = 0;
        foreach (var file in new DirectoryInfo(_installDirectory).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            bytes += file.Length;
        }

        using var key = Registry.LocalMachine.CreateSubKey(UninstallRegistryKey, true)
            ?? throw new InvalidOperationException("Could not register ISLA in Windows Apps.");
        key.SetValue("DisplayName", "ISLA");
        key.SetValue("DisplayVersion", AppVersion);
        key.SetValue("Publisher", Publisher);
        key.SetValue("InstallLocation", _installDirectory);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("DisplayIcon", $"\"{AppExecutable}\",0");
        key.SetValue("UninstallString", $"\"{UninstallerPath}\" --uninstall");
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, bytes / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    /// <summary>Starts ISLA through Explorer so it runs as the normal user, not elevated like this setup.</summary>
    private void LaunchApp()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{AppExecutable}\"",
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show($"ISLA was installed but could not be started: {exception.Message}",
                "ISLA Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CreateShortcut(string shortcutPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is unavailable, so the Start Menu shortcut could not be created.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(shortcutPath);
            try
            {
                link.TargetPath = AppExecutable;
                link.WorkingDirectory = _installDirectory;
                link.Description = "ISLA Dynamic Island";
                link.IconLocation = AppExecutable + ",0";
                link.Save();
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }

        if (!File.Exists(shortcutPath))
        {
            throw new InvalidOperationException("The Start Menu shortcut could not be created.");
        }
    }

    private void RegisterIdentityPackage()
    {
        var packagePath = Path.Combine(_installDirectory, "ISLA.Identity.msix");
        var certificatePath = Path.Combine(_installDirectory, "ISLA.Identity.cer");
        ExtractResource("ISLA.Identity.msix", packagePath);
        ExtractResource("ISLA.Identity.cer", certificatePath);

        var escapedPackage = packagePath.Replace("'", "''");
        var escapedCertificate = certificatePath.Replace("'", "''");
        var command =
            "$ErrorActionPreference='Stop';" +
            "Get-AppxPackage -Name 'ISLA.Identity' | Remove-AppxPackage -ErrorAction SilentlyContinue;" +
            $"certutil.exe -addstore -f TrustedPeople '{escapedCertificate}' | Out-Null;" +
            $"certutil.exe -addstore -f Root '{escapedCertificate}' | Out-Null;" +
            $"Add-AppxPackage -Path '{escapedPackage}' -ExternalLocation '{_installDirectory.Replace("'", "''")}'";

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"{command}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true
        });
        if (process is null)
        {
            throw new InvalidOperationException("Could not start Windows package registration.");
        }

        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Windows notification identity registration failed: {error.Trim()}");
        }
    }

    private static void UnregisterIdentityPackage()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"Get-AppxPackage -Name 'ISLA.Identity' | Remove-AppxPackage -ErrorAction SilentlyContinue\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });
        process?.WaitForExit();
    }

    private static void ExtractResource(string fileName, string destination)
    {
        var resourceName = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        using var resource = resourceName is null
            ? throw new InvalidOperationException($"Installer resource is missing: {fileName}")
            : Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Installer resource is missing: {fileName}");
        using var output = File.Create(destination);
        resource.CopyTo(output);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
}
