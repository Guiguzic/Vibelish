using Microsoft.Win32;

namespace Vibelish;

/// <summary>Per-user "start with Windows" entry in HKCU\...\Run.</summary>
static class StartupRegistration
{
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Vibelish";

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Application.ExecutablePath;

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool PointsToCurrentExecutable
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                var value = key?.GetValue(ValueName) as string ?? "";
                return value.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool SetEnabled(bool enabled, out string error)
    {
        error = "";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            if (key == null)
            {
                error = "无法写入注册表";
                return false;
            }

            if (enabled)
                key.SetValue(ValueName, "\"" + ExecutablePath + "\" --autostart", RegistryValueKind.String);
            else
                key.DeleteValue(ValueName, false);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>登记的 exe 路径（去掉引号和参数）。</summary>
    public static string RegisteredPath
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                var value = (key?.GetValue(ValueName) as string ?? "").Trim();
                if (value.StartsWith('"'))
                {
                    var end = value.IndexOf('"', 1);
                    return end > 1 ? value[1..end] : value.Trim('"');
                }

                var space = value.IndexOf(" --", StringComparison.Ordinal);
                return space > 0 ? value[..space] : value;
            }
            catch
            {
                return "";
            }
        }
    }

    /// <summary>登记的 exe 已经不在了（程序被挪走）才改成当前路径。
    /// 只是从别处运行一份（比如开发时的 bin\Release）不能把已安装版本的自启动抢过来。</summary>
    public static void Refresh()
    {
        if (IsEnabled && !PointsToCurrentExecutable)
        {
            var registered = RegisteredPath;
            if (registered.Length == 0 || !File.Exists(registered))
                _ = SetEnabled(true, out _);
        }

        // A previous uninstall may have scheduled "rmdir" at next logon. Once the app is
        // running again that cleanup must not delete the fresh installation.
        try
        {
            using var runOnce = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\RunOnce", true);
            runOnce?.DeleteValue("VibelishCleanup", false);
        }
        catch
        {
            // Ignore.
        }
    }
}
