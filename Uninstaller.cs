using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace InputTranslate;

static class Uninstaller
{
    const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\InputTranslate";
    const string RunOnceKeyPath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool MoveFileEx(string existingFile, string? newFile, uint flags);

    const uint MoveFileDelayUntilReboot = 0x4;

    public static bool ConfirmAndRun(bool quiet)
    {
        if (!quiet)
        {
            var answer = MessageBox.Show(
                "确定要卸载「输入翻译」吗？\n\n将删除程序文件、开始菜单/桌面快捷方式和开机自启动设置。",
                "卸载输入翻译",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
                return false;
        }

        Run();
        return true;
    }

    static void Run()
    {
        var exe = StartupRegistration.ExecutablePath;
        var directory = Path.GetDirectoryName(exe) ?? "";
        var localAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InputTranslate");

        KillOtherInstances();

        _ = StartupRegistration.SetEnabled(false, out _);
        RemoveUninstallEntry();
        RemoveShortcuts();

        // The hook DLL is injected into other processes, so some files may be locked.
        // Delete what we can now and let Windows remove the rest on reboot / next logon.
        var locked = new List<string>();
        foreach (var file in SafeEnumerateFiles(directory))
        {
            if (string.Equals(file, exe, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                File.Delete(file);
            }
            catch
            {
                locked.Add(file);
            }
        }

        foreach (var file in locked)
        {
            try
            {
                _ = MoveFileEx(file, null, MoveFileDelayUntilReboot);
            }
            catch
            {
                // Best effort only.
            }
        }

        DeleteDirectoryIfEmpty(directory);
        TryDeleteDirectory(Path.Combine(directory, "hook"));
        TryDeleteDirectory(localAppData);

        ScheduleFolderCleanup(directory);
        TraceLog.Note("uninstall");
    }

    static void KillOtherInstances()
    {
        foreach (var process in Process.GetProcessesByName("InputTranslate"))
        {
            if (process.Id == Environment.ProcessId)
                continue;
            try
            {
                process.Kill();
                process.WaitForExit(2000);
            }
            catch
            {
                // Ignore processes we cannot stop.
            }
        }
    }

    static IEnumerable<string> SafeEnumerateFiles(string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            yield break;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories);
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
            yield return file;
    }

    static void RemoveUninstallEntry()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false);
        }
        catch
        {
            // Ignore.
        }
    }

    static void RemoveShortcuts()
    {
        try
        {
            var startMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                "输入翻译");
            if (Directory.Exists(startMenu))
                Directory.Delete(startMenu, true);
        }
        catch
        {
            // Ignore.
        }

        try
        {
            var desktop = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "输入翻译.lnk");
            if (File.Exists(desktop))
                File.Delete(desktop);
        }
        catch
        {
            // Ignore.
        }
    }

    static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
        catch
        {
            // Locked files are handled by the reboot / logon cleanup.
        }
    }

    static void DeleteDirectoryIfEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch
        {
            // Ignore.
        }
    }

    static void ScheduleFolderCleanup(string directory)
    {
        if (string.IsNullOrEmpty(directory))
            return;

        try
        {
            var start = new ProcessStartInfo("cmd.exe",
                "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + directory + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process.Start(start);
        }
        catch
        {
            // Ignore.
        }

        try
        {
            using var runOnce = Registry.CurrentUser.CreateSubKey(RunOnceKeyPath, true);
            runOnce?.SetValue(
                "InputTranslateCleanup",
                "cmd.exe /c rmdir /s /q \"" + directory + "\"",
                RegistryValueKind.String);
        }
        catch
        {
            // Ignore.
        }
    }
}
