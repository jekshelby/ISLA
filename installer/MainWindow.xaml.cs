using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;

namespace ISLAInstaller;

public partial class MainWindow : Window
{
    private readonly string _installDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "ISLA");
    private int _page;
    private readonly bool _isUninstalling;
    private bool _uninstallFinished;

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
        if (_isUninstalling)
        {
            if (_uninstallFinished)
            {
                Close();
                return;
            }

            StartUninstall();
            return;
        }

        if (_page == 0)
        {
            _page = 1;
            TitleText.Text = "Installing ISLA";
            DescriptionText.Text = "Please wait while ISLA is installed.";
            NextButton.IsEnabled = false;
            BackButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            InstallProgress.Visibility = Visibility.Visible;
            InstallAsync();
            return;
        }

        Close();
    }

    private async void InstallAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                UnregisterIdentityPackage();
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
                CreateShortcut(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "ISLA.lnk"));
                var uninstaller = Path.Combine(_installDirectory, "ISLA-Uninstall.exe");
                File.Copy(Environment.ProcessPath
                    ?? throw new InvalidOperationException("Could not locate the setup executable."), uninstaller, true);
            });

            TitleText.Text = "ISLA installed";
            DescriptionText.Text = "ISLA 1.0 was installed successfully.";
            StatusText.Text = "Start ISLA from the Start Menu. An uninstaller is available in the installation folder.";
            InstallProgress.Visibility = Visibility.Collapsed;
            NextButton.Content = "Finish";
            NextButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            _uninstallFinished = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "ISLA Setup", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private void StartUninstall()
    {
        TitleText.Text = "Removing ISLA";
        DescriptionText.Text = "Please wait while ISLA is removed.";
        NextButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        InstallProgress.Visibility = Visibility.Visible;
        UninstallAsync();
    }

    private async void UninstallAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                UnregisterIdentityPackage();
                var shortcut = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "ISLA.lnk");
                if (File.Exists(shortcut))
                {
                    File.Delete(shortcut);
                }

                ScheduleInstallDirectoryRemoval();
            });

            TitleText.Text = "ISLA removed";
            DescriptionText.Text = "ISLA will be fully removed when this window closes.";
            StatusText.Text = "Click Finish to close the uninstaller.";
            InstallProgress.Visibility = Visibility.Collapsed;
            NextButton.Content = "Finish";
            NextButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "ISLA Uninstall", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private void ScheduleInstallDirectoryRemoval()
    {
        if (!Directory.Exists(_installDirectory))
        {
            return;
        }

        var script = Path.Combine(Path.GetTempPath(), $"ISLA-Uninstall-{Guid.NewGuid():N}.cmd");
        var escapedDirectory = _installDirectory.Replace("\"", "\"\"");
        File.WriteAllText(script, $"@echo off\r\ntimeout /t 2 /nobreak > nul\r\nrmdir /s /q \"{escapedDirectory}\"\r\ndel \"%~f0\"\r\n");
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c start \"\" /b \"{script}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });
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

    private void CreateShortcut(string shortcutPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var target = Path.Combine(_installDirectory, "ISLA.exe").Replace("'", "''");
        var link = shortcutPath.Replace("'", "''");
        var command = "$s=New-Object -ComObject WScript.Shell;" +
            "$l=$s.CreateShortcut('" + link + "');" +
            "$l.TargetPath='" + target + "';" +
            "$l.WorkingDirectory='" + _installDirectory.Replace("'", "''") + "';" +
            "$l.Description='ISLA Dynamic Island';$l.Save()";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });
        process?.WaitForExit();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => _page = 0;
    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
}
