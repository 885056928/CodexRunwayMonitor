using Microsoft.Win32;

namespace Monitor.App;

public static class StartupService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexRunwayMonitor";
    public static string? Read() { using var key = Registry.CurrentUser.OpenSubKey(KeyPath); return key?.GetValue(ValueName) as string; }
    public static void Restore(string? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        if (value is null) key.DeleteValue(ValueName, false); else key.SetValue(ValueName, value, RegistryValueKind.String);
    }
    public static void Set(bool enabled)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("无法确定程序路径");
        if (enabled && Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new IOException("请从发布的 exe 设置开机启动");
        Restore(enabled ? "\"" + executable + "\" --tray" : null);
    }
}
