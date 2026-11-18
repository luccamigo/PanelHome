using Microsoft.Win32;
using Strings = PanelOS.ControlCenter.Resources.ControlCenterStrings;

namespace PanelOS.ControlCenter.Services;

/// <summary>
/// A "start with Windows" toggle. The registry is the single source of truth:
/// nothing mirrors this into the settings file, so a value removed by hand
/// outside the app is reflected the next time the switch is read.
/// </summary>
public interface IStartupRegistration
{
    /// <summary>
    /// False when the target executable cannot be located; the UI must then
    /// show the switch as unavailable instead of pretending it works.
    /// </summary>
    bool IsSupported { get; }

    string? UnsupportedReason { get; }

    bool IsEnabled();

    void SetEnabled(bool enabled);
}

/// <summary>
/// Per-user Run key registration. It touches HKEY_CURRENT_USER only - never a
/// machine-wide hive, a service, or a scheduled task.
/// </summary>
public sealed class RegistryStartupRegistration : IStartupRegistration
{
    private const string RunKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _valueName;
    private readonly string? _executablePath;

    public RegistryStartupRegistration(
        string valueName,
        string? executablePath,
        string? unsupportedReason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        _valueName = valueName;
        _executablePath = executablePath;
        UnsupportedReason = executablePath is null
            ? unsupportedReason ?? Strings.Get("StartupExecutableNotFound")
            : null;
    }

    /// <summary>
    /// Deletes a Run value this application created. Used to retire the
    /// separate PanelHost entry: the Control Center now starts the agent
    /// itself, and a second logon entry would produce an agent it does not own.
    /// </summary>
    public static bool RemoveValue(string valueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(valueName) is null)
            {
                return false;
            }

            key.DeleteValue(valueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException or
                UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public bool IsSupported => _executablePath is not null;

    public string? UnsupportedReason { get; }

    public bool IsEnabled()
    {
        if (!IsSupported)
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(_valueName) is string value &&
                !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException or
                UnauthorizedAccessException or
                IOException)
        {
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (!IsSupported)
        {
            throw new InvalidOperationException(
                UnsupportedReason ?? Strings.Get("StartupUnavailable"));
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException(
                Strings.Get("StartupRunKeyUnavailable"));
        if (enabled)
        {
            key.SetValue(_valueName, $"\"{_executablePath}\"", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(_valueName, throwOnMissingValue: false);
        }
    }
}
