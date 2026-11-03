using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace InputTranslateSetup;

static class InstallerCore
{
    public const string ProductName = "输入翻译";
    public const string ProductVersion = "1.0.0";
    public const string Publisher = "InputTranslate";

    public static string TargetDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "InputTranslate");

    public static string ExePath => Path.Combine(TargetDirectory, "InputTranslate.exe");

    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\InputTranslate";

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
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;

        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relative))
                continue;
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("安装包包含非法路径：" + entry.FullName);

            if (relative.EndsWith(Path.DirectorySeparatorChar) || entry.Name.Length == 0)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
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
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("无法创建快捷方式（WScript.Shell 不可用）");
        dynamic shell = Activator.CreateInstance(shellType)!;

        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ProductName);
        Directory.CreateDirectory(startMenu);
        SaveShortcut(shell, Path.Combine(startMenu, ProductName + ".lnk"), ExePath, "", TargetDirectory, "中文输入 → 考研级英文");
        SaveShortcut(shell, Path.Combine(startMenu, "卸载" + ProductName + ".lnk"), ExePath, "--uninstall", TargetDirectory, "卸载输入翻译");

        if (!desktopShortcut)
            return;

        var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ProductName + ".lnk");
        SaveShortcut(shell, desktop, ExePath, "", TargetDirectory, "中文输入 → 考研级英文");
    }

    static void SaveShortcut(dynamic shell, string path, string target, string arguments, string workingDirectory, string description)
    {
        var shortcut = shell.CreateShortcut(path);
        shortcut.TargetPath = target;
        shortcut.Arguments = arguments;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.Description = description;
        shortcut.IconLocation = target + ",0";
        shortcut.Save();
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
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }
}
