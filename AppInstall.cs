using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MeetMicSync;

/// <summary>
/// Copies the app into a per-user Programs folder and manages the Startup shortcut.
/// No admin rights required.
/// </summary>
internal static class AppInstall
{
    public const string AppFolderName = "MeetMicSync";
    public const string ExeName = "MeetMicSync.exe";
    public const string ShortcutName = "Meet Mic Sync.lnk";

    public static string InstallDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            AppFolderName);

    public static string InstalledExePath => Path.Combine(InstallDirectory, ExeName);

    public static string StartupShortcutPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            ShortcutName);

    public static string CurrentExePath =>
        Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? Path.Combine(AppContext.BaseDirectory, ExeName);

    public static bool IsRunningFromInstallDir()
    {
        try
        {
            return PathsEqual(CurrentExePath, InstalledExePath);
        }
        catch
        {
            return false;
        }
    }

    public static bool StartupShortcutExists() => File.Exists(StartupShortcutPath);

    /// <summary>
    /// Plain-language explanation shown before any changes are made.
    /// </summary>
    public static string BuildConfirmMessage()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Meet Mic Sync will do the following:");
        sb.AppendLine();

        if (!IsRunningFromInstallDir())
        {
            sb.AppendLine("1. Copy this program to your user Programs folder:");
            sb.AppendLine($"   {InstallDirectory}");
            sb.AppendLine("   (no administrator rights needed; your Downloads copy can stay where it is)");
            sb.AppendLine();
            sb.AppendLine("2. Create a Startup shortcut so the copied program starts when you sign in to Windows:");
        }
        else
        {
            sb.AppendLine("1. Create a Startup shortcut so this program starts when you sign in to Windows:");
        }

        sb.AppendLine($"   {StartupShortcutPath}");
        sb.AppendLine();

        if (!IsRunningFromInstallDir())
        {
            sb.AppendLine("3. Restart Meet Mic Sync from the new folder.");
            sb.AppendLine();
        }

        sb.AppendLine("You can remove the Startup shortcut later from the tray menu.");
        sb.AppendLine();
        sb.Append("Continue?");
        return sb.ToString();
    }

    public static string BuildRemoveConfirmMessage()
    {
        return
            "Remove Meet Mic Sync from Windows Startup?" + Environment.NewLine +
            Environment.NewLine +
            "This will delete only the Startup shortcut:" + Environment.NewLine +
            StartupShortcutPath + Environment.NewLine +
            Environment.NewLine +
            "The program files will stay on disk. Meet Mic Sync will not start automatically after you sign in.";
    }

    /// <summary>
    /// Install/copy + create Startup shortcut. May request a restart from the install path.
    /// </summary>
    public static InstallResult EnableStartup()
    {
        try
        {
            Directory.CreateDirectory(InstallDirectory);

            var source = CurrentExePath;
            var needsRestart = !PathsEqual(source, InstalledExePath);

            if (needsRestart)
            {
                // A running .exe can be copied; replacing the file we are running from is avoided.
                File.Copy(source, InstalledExePath, overwrite: true);
                Log.Write($"install: copied '{source}' → '{InstalledExePath}'");
            }
            else
            {
                Log.Write("install: already running from install directory");
            }

            CreateShortcut(StartupShortcutPath, InstalledExePath, InstallDirectory);
            Log.Write($"install: startup shortcut → '{StartupShortcutPath}'");

            return new InstallResult(
                Success: true,
                NeedsRestart: needsRestart,
                Message: needsRestart
                    ? "Installed and added to Startup. Restarting from the user Programs folder…"
                    : "Added to Windows Startup.");
        }
        catch (Exception ex)
        {
            Log.Write($"install failed: {ex}");
            return new InstallResult(false, false, $"Could not set up Startup: {ex.Message}");
        }
    }

    public static InstallResult DisableStartup()
    {
        try
        {
            if (File.Exists(StartupShortcutPath))
            {
                File.Delete(StartupShortcutPath);
                Log.Write($"install: removed startup shortcut '{StartupShortcutPath}'");
            }

            return new InstallResult(true, false, "Removed from Windows Startup.");
        }
        catch (Exception ex)
        {
            Log.Write($"uninstall startup failed: {ex}");
            return new InstallResult(false, false, $"Could not remove Startup shortcut: {ex.Message}");
        }
    }

    public static void RestartFromInstalledCopy()
    {
        var psi = new ProcessStartInfo
        {
            FileName = InstalledExePath,
            WorkingDirectory = InstallDirectory,
            UseShellExecute = true
        };
        Process.Start(psi);
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory)
    {
        // IWshRuntimeLibrary via ProgID — available on all desktop Windows, no extra package.
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell is not available on this PC.");

        dynamic shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Could not create WScript.Shell.");

        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.WindowStyle = 1; // normal
        shortcut.Description = "Meet Mic Sync — Lenovo mic mute ↔ Google Meet";
        shortcut.Save();

        Marshal.FinalReleaseComObject(shortcut);
        Marshal.FinalReleaseComObject(shell);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}

internal readonly record struct InstallResult(bool Success, bool NeedsRestart, string Message);
