using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SonosDesktopBridge;

/// <summary>Per-user install: copies the exe, adds a Start menu shortcut and an "Installed apps" entry. No admin rights.</summary>
internal static class Installer
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SonosDesktopBridge";
    private const string AppName = "Sonos Desktop Bridge";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
    public static string InstalledExe => Path.Combine(InstallDir, "SonosDesktopBridge.exe");
    private static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

    public static bool IsInstalledCopy =>
        string.Equals(Path.GetDirectoryName(Environment.ProcessPath), InstallDir, StringComparison.OrdinalIgnoreCase);

    private static void StopOtherInstances()
    {
        foreach (var p in Process.GetProcessesByName("SonosDesktopBridge"))
        {
            if (p.Id == Environment.ProcessId) continue;
            // Only stop the installed copy; portable copies elsewhere are left alone.
            try
            {
                if (!string.Equals(p.MainModule?.FileName, InstalledExe, StringComparison.OrdinalIgnoreCase)) continue;
                p.Kill();
                p.WaitForExit(3000);
            }
            catch { }
        }
    }

    public static void Install()
    {
        StopOtherInstances();
        Directory.CreateDirectory(InstallDir);
        File.Copy(Environment.ProcessPath!, InstalledExe, true);

        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic shortcut = ((dynamic)shell).CreateShortcut(ShortcutPath);
        shortcut.TargetPath = InstalledExe;
        shortcut.WorkingDirectory = InstallDir;
        shortcut.IconLocation = InstalledExe + ",0";
        shortcut.Description = "Pilote le volume des enceintes Sonos depuis le clavier";
        shortcut.Save();

        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "");
            key.SetValue("Publisher", "Sonos Desktop Bridge");
            key.SetValue("DisplayIcon", InstalledExe);
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
        }
        if (Autostart.IsEnabled) Autostart.Set(true, InstalledExe);
    }

    public static void Uninstall()
    {
        StopOtherInstances();
        try { File.Delete(ShortcutPath); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        try { Autostart.Set(false); } catch { }
        try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SonosDesktopBridge"), true); } catch { }
        // The running exe cannot delete itself: let a short-lived cmd remove the folder once we have exited.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{InstallDir}\"")
        { CreateNoWindow = true, UseShellExecute = false });
    }
}
