using Microsoft.Win32;

namespace InputTranslate;

/// <summary>Per-user "start with Windows" entry in HKCU\...\Run.</summary>
static class StartupRegistration
{
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "InputTranslate";

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

    /// <summary>Repairs a stale path (e.g. the app was moved) while keeping autostart on.</summary>
    public static void Refresh()
    {
        if (IsEnabled && !PointsToCurrentExecutable)
            _ = SetEnabled(true, out _);

        // A previous uninstall may have scheduled "rmdir" at next logon. Once the app is
        // running again that cleanup must not delete the fresh installation.
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
    }
}
