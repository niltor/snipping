using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Snipping.App;

internal enum StartupRegistrationState
{
    Disabled,
    Enabled,
    DisabledByUser,
    DisabledByPolicy
}

internal readonly record struct StartupRegistration(StartupRegistrationState State)
{
    public bool IsEnabled => State == StartupRegistrationState.Enabled;
}

internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Snipping";
    private const string StartupTaskId = "SnippingStartup";

    /// <summary>
    /// Reads the system startup state and repairs the registration when the
    /// stored preference predates the current registration mechanism.
    ///
    /// Packaged apps use the Windows startup-task registration. Unpackaged
    /// development runs use the per-user Run key as a compatibility fallback.
    /// </summary>
    public static async Task<StartupRegistration> ReconcileAsync(bool preferredEnabled)
    {
        if (!TryGetCurrentPackage(out _))
        {
            ApplyRunRegistration(preferredEnabled);
            return new StartupRegistration(
                preferredEnabled
                    ? StartupRegistrationState.Enabled
                    : StartupRegistrationState.Disabled);
        }

        var startupTask = await GetStartupTaskAsync();
        var state = ToRegistrationState(startupTask.State);

        // Preserve the old setting when upgrading to the manifest-based
        // registration, but never override a decision made in Task Manager
        // or by Group Policy.
        if (state == StartupRegistrationState.Disabled && preferredEnabled)
        {
            state = ToRegistrationState(await startupTask.RequestEnableAsync());
        }

        // Older versions registered packaged activation through HKCU\Run.
        // Remove that entry after the native task has been found so both
        // mechanisms cannot start the app on the same logon.
        TryRemoveLegacyRunEntry();
        return new StartupRegistration(state);
    }

    public static async Task<StartupRegistration> ApplyAsync(bool enabled)
    {
        if (!TryGetCurrentPackage(out _))
        {
            ApplyRunRegistration(enabled);
            return new StartupRegistration(
                enabled
                    ? StartupRegistrationState.Enabled
                    : StartupRegistrationState.Disabled);
        }

        var startupTask = await GetStartupTaskAsync();
        var state = ToRegistrationState(startupTask.State);

        if (enabled)
        {
            if (state == StartupRegistrationState.Disabled)
            {
                state = ToRegistrationState(await startupTask.RequestEnableAsync());
            }
        }
        else if (state == StartupRegistrationState.Enabled)
        {
            startupTask.Disable();
            state = StartupRegistrationState.Disabled;
        }

        TryRemoveLegacyRunEntry();
        return new StartupRegistration(state);
    }

    private static async Task<StartupTask> GetStartupTaskAsync()
    {
        try
        {
            return await StartupTask.GetAsync(StartupTaskId);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException(
                "当前 MSIX 未注册开机自启任务，请重新安装或升级到最新版本。",
                ex);
        }
    }

    private static StartupRegistrationState ToRegistrationState(StartupTaskState state) =>
        state switch
        {
            StartupTaskState.Enabled => StartupRegistrationState.Enabled,
            StartupTaskState.DisabledByUser => StartupRegistrationState.DisabledByUser,
            StartupTaskState.DisabledByPolicy => StartupRegistrationState.DisabledByPolicy,
            _ => StartupRegistrationState.Disabled
        };

    private static bool TryGetCurrentPackage(out Package? package)
    {
        try
        {
            package = Package.Current;
            return package is not null;
        }
        catch (COMException)
        {
            package = null;
            return false;
        }
        catch (InvalidOperationException)
        {
            package = null;
            return false;
        }
    }

    private static void ApplyRunRegistration(bool enabled)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的 Windows 启动项注册表。");

        if (!enabled)
        {
            runKey.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            executablePath = Application.ExecutablePath;
        }

        var command = $"\"{executablePath}\"";
        if (command.Length > 260)
        {
            throw new InvalidOperationException("应用路径过长，无法注册 Windows 登录启动项。");
        }

        runKey.SetValue(ValueName, command, RegistryValueKind.String);
    }

    private static void TryRemoveLegacyRunEntry()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            runKey?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (UnauthorizedAccessException)
        {
            // The native packaged task is already authoritative. A stale
            // legacy entry is harmless because the app's single-instance
            // mutex prevents duplicate UI processes.
        }
        catch (SecurityException)
        {
            // See the UnauthorizedAccessException comment above.
        }
    }
}
