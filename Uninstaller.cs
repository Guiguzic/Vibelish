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

    /// <summary>安装程序放程序文件的位置。</summary>
    public static string DefaultInstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "InputTranslate");

    public static bool ConfirmAndRun(bool quiet)
    {
        var exe = StartupRegistration.ExecutablePath;
        var directory = Path.GetDirectoryName(exe) ?? "";
        var installed = IsInstallDirectory(directory);
        if (!quiet)
        {
            var detail = installed
                ? "将删除程序文件、开始菜单/桌面快捷方式和开机自动启动。\n你的设置（%APPDATA%\\InputTranslate）会保留。"
                : "将删除开始菜单/桌面快捷方式和开机自动启动。\n\n这一份不在安装目录里（" + directory + "），程序文件不会动，需要的话请手动删除。";
            var answer = MessageBox.Show(
                "确定要卸载「输入翻译」吗？\n\n" + detail,
                "卸载输入翻译",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
                return false;
        }

        Run(exe, directory, installed);
        if (!quiet)
        {
            MessageBox.Show("「输入翻译」已卸载。", "卸载输入翻译", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        return true;
    }

    /// <summary>只有确认是安装目录时才删整个文件夹。
    /// 以前这里直接删 exe 所在的目录，把 exe 拷到桌面再运行 --uninstall 会把整个桌面删掉。</summary>
    static bool IsInstallDirectory(string directory)
    {
        if (directory.Length == 0)
            return false;
        var full = Normalize(directory);
        if (string.Equals(full, Normalize(DefaultInstallDirectory), StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath, false);
            var location = key?.GetValue("InstallLocation") as string ?? "";
            return location.Length > 0
                && string.Equals(full, Normalize(location), StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(full, "InputTranslate.exe"));
        }
        catch
        {
            return false;
        }
    }

    static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path;
        }
    }

    static void Run(string exe, string directory, bool installed)
    {
        var localAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InputTranslate");

        KillOtherInstances();

        _ = StartupRegistration.SetEnabled(false, out _);
        RemoveUninstallEntry();
        RemoveShortcuts();
        TryDeleteDirectory(localAppData);

        if (installed)
        {
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

            TryDeleteDirectory(Path.Combine(directory, "hook"));
            ScheduleFolderCleanup(directory);
        }

        TraceLog.Note("uninstall installed=" + installed);
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
