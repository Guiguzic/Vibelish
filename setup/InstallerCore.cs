using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace InputTranslateSetup;

static class InstallerCore
{
    public const string ProductName = "输入翻译";
    public const string Publisher = "InputTranslate";

    /// <summary>安装包自己的版本，由 build.ps1 的 -p:Version 写进程序集。</summary>
    public static string ProductVersion =>
        typeof(InstallerCore).Assembly.GetName().Version is { } version ? version.ToString(3) : "1.0.0";

    public static string TargetDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "InputTranslate");

    public static string ExePath => Path.Combine(TargetDirectory, "InputTranslate.exe");

    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\InputTranslate";

    /// <summary>已经装过的版本；没装过返回空字符串。</summary>
    public static string InstalledVersion
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath, false);
                var version = key?.GetValue("DisplayVersion") as string ?? "";
                return version.Length > 0 && File.Exists(ExePath) ? version : "";
            }
            catch
            {
                return "";
            }
        }
    }

    /// <summary>当前是否开机自动启动（更新时沿用用户原来的选择）。</summary>
    public static bool StartupEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                return key?.GetValue("InputTranslate") is string value && value.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>解压后大约占多少空间（MB）；读不到安装包时返回 0。</summary>
    public static int RequiredSpaceMb
    {
        get
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("InputTranslateSetup.payload.zip");
                if (stream == null)
                    return 0;
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                var bytes = archive.Entries.Sum(entry => entry.Length);
                return (int)Math.Ceiling(bytes / 1024.0 / 1024.0);
            }
            catch
            {
                return 0;
            }
        }
    }

    public static bool DesktopShortcutExists => File.Exists(DesktopShortcutPath);

    static string DesktopShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ProductName + ".lnk");

    public static void Install(bool startup, bool desktopShortcut, Action<string> progress)
    {
        progress("正在关闭已运行的输入翻译…");
        KillRunningInstances();

        // Remove any pending uninstall cleanup so it cannot delete the new install.
        try
        {
            using var runOnce = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\RunOnce", true);
            runOnce?.DeleteValue("InputTranslateCleanup", false);
        }
        catch
        {
            // Ignore.
        }

        progress("正在复制程序文件…");
        Directory.CreateDirectory(TargetDirectory);
        ExtractPayload(TargetDirectory);

        progress("正在创建快捷方式…");
        CreateShortcuts(desktopShortcut);

        progress("正在写入设置…");
        WriteStartup(startup);
        WriteUninstallEntry();
        progress("安装完成");
    }

    static void ExtractPayload(string destination)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("InputTranslateSetup.payload.zip")
            ?? throw new InvalidOperationException("安装包损坏：找不到 payload.zip");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relative))
                continue;
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("安装包包含非法路径：" + entry.FullName);

            if (relative.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) || entry.Name.Length == 0)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            ExtractWithRetry(entry, target);
        }
    }

    /// <summary>刚结束的进程可能还没完全放开文件，稍等再试几次。</summary>
    static void ExtractWithRetry(ZipArchiveEntry entry, string target)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                entry.ExtractToFile(target, true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(300);
            }
            catch (UnauthorizedAccessException) when (attempt < 10)
            {
                Thread.Sleep(300);
            }
        }
    }

    static void KillRunningInstances()
    {
        foreach (var process in Process.GetProcessesByName("InputTranslate"))
        {
            try
            {
                process.Kill();
                process.WaitForExit(3000);
            }
            catch
            {
                // Ignore.
            }
        }
    }

    static void CreateShortcuts(bool desktopShortcut)
    {
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ProductName);
        Directory.CreateDirectory(startMenu);
        SaveShortcut(Path.Combine(startMenu, ProductName + ".lnk"), ExePath, "", "中文输入 → 考研级英文");
        SaveShortcut(Path.Combine(startMenu, "卸载" + ProductName + ".lnk"), ExePath, "--uninstall", "卸载输入翻译");

        if (desktopShortcut)
        {
            SaveShortcut(DesktopShortcutPath, ExePath, "", "中文输入 → 考研级英文");
        }
        else if (File.Exists(DesktopShortcutPath))
        {
            try
            {
                File.Delete(DesktopShortcutPath);
            }
            catch
            {
                // Ignore.
            }
        }
    }

    /// <summary>直接用 IShellLinkW 写快捷方式。.NET Framework 里用 dynamic 调 WScript.Shell
    /// 会把中文路径变成问号（「输入翻译.lnk」存不下来）。</summary>
    static void SaveShortcut(string path, string target, string arguments, string description)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetArguments(arguments);
            link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? "");
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            ((IPersistFile)link).Save(path, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int size, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    static void WriteStartup(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
            ?? throw new InvalidOperationException("无法写入开机启动项");
        if (enabled)
            run.SetValue("InputTranslate", "\"" + ExePath + "\" --autostart", RegistryValueKind.String);
        else
            run.DeleteValue("InputTranslate", false);
    }

    static void WriteUninstallEntry()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath, true)
            ?? throw new InvalidOperationException("无法写入卸载信息");
        key.SetValue("DisplayName", ProductName, RegistryValueKind.String);
        key.SetValue("DisplayVersion", ProductVersion, RegistryValueKind.String);
        key.SetValue("Publisher", Publisher, RegistryValueKind.String);
        key.SetValue("InstallLocation", TargetDirectory, RegistryValueKind.String);
        key.SetValue("DisplayIcon", ExePath, RegistryValueKind.String);
        key.SetValue("UninstallString", "\"" + ExePath + "\" --uninstall", RegistryValueKind.String);
        key.SetValue("QuietUninstallString", "\"" + ExePath + "\" --uninstall --yes", RegistryValueKind.String);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);
        key.SetValue("EstimatedSize", EstimateSizeKb(), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    static int EstimateSizeKb()
    {
        try
        {
            var bytes = new DirectoryInfo(TargetDirectory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
            return (int)Math.Min(int.MaxValue, bytes / 1024);
        }
        catch
        {
            return 0;
        }
    }
}
