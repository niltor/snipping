using Microsoft.Win32;

namespace Snipping.App;

internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Snipping";

    public static void Apply(bool enabled)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的 Windows 启动项注册表。");

        if (enabled)
        {
            runKey.SetValue(ValueName, GetStartupCommand(), RegistryValueKind.String);
        }
        else
        {
            runKey.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static string GetStartupCommand()
    {
        // A packaged app's installation directory changes on update. The
        // AppsFolder activation ID remains stable, so prefer it for MSIX.
        try
        {
            var package = Windows.ApplicationModel.Package.Current;
            return $"explorer.exe shell:AppsFolder\\{package.Id.FamilyName}!App";
        }
        catch
        {
            return $"\"{Application.ExecutablePath}\"";
        }
    }
}
