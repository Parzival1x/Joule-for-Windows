using System.Text.Json;
using Joule.Core;
using Microsoft.Win32;

namespace Joule;

public enum TrayStyle { Watts, Percent, Icon, Battery }

/// <summary>Preferences, stored as JSON next to the history.</summary>
public sealed class AppSettings
{
    public WarningSettings Warnings { get; set; } = new();
    public TrayStyle Tray { get; set; } = TrayStyle.Battery;
    public bool Notifications { get; set; } = true;
    public string Page { get; set; } = "now";
    public Period HistoryPeriod { get; set; } = Period.Week;
    public Period AppsPeriod { get; set; } = Period.Today;
    public bool BatteryOnly { get; set; }
    public double Baseline { get; set; } = 6.0;
    public int BaselineSamples { get; set; }
    /// <summary>Devices already reported as low in their current discharge (survives restarts, so a login doesn't repeat it).</summary>
    public HashSet<string> DeviceLowFired { get; set; } = new();
    /// <summary>Devices the user chose not to see on the Devices page.</summary>
    public HashSet<string> HiddenDevices { get; set; } = new();
    /// <summary>Notify when a device connects or disconnects.</summary>
    public bool DeviceNotifications { get; set; } = true;
    /// <summary>Devices that ran low and haven't been charged yet: the tray shows the low-battery icon meanwhile.</summary>
    public HashSet<string> LowDevices { get; set; } = new();
    /// <summary>Settings-file version, for one-time changes of defaults.</summary>
    public int Version { get; set; }
    /// <summary>Set just before restarting with administrator rights: "main" or "settings" to reopen.</summary>
    public string? ReopenAfterRestart { get; set; }

    private static string FilePath => Path.Combine(Store.DefaultDirectory, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
                // Version 1: the battery icon (average of all devices) became the tray default.
                if (settings.Version < 1) { settings.Tray = TrayStyle.Battery; settings.Version = 1; settings.Save(); }
                return settings;
            }
        }
        catch { }
        return new AppSettings { Version = 1 };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Store.DefaultDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch { }
    }
}

/// <summary>Start with Windows: a per-user Run entry, or an elevated scheduled task when sleep blockers should be visible.</summary>
public static class LoginItem
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Joule";
    public const string TaskName = "Joule (elevated)";

    public static string ExePath => Environment.ProcessPath ?? "";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(Name, $"\"{ExePath}\" --background");
            else key.DeleteValue(Name, false);
        }
    }

    public static bool ElevatedTaskExists()
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = System.Diagnostics.Process.Start(info)!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Creates (or removes) a logon task that starts Joule with administrator rights; Windows asks once for consent
    /// (not at all when Joule already has those rights). Written from XML because schtasks' own defaults would keep a
    /// laptop on battery from starting it, stop it when unplugged, and end it after three days.
    /// </summary>
    public static bool SetElevatedTask(bool enabled)
    {
        string? xmlFile = null;
        string args;
        if (enabled)
        {
            xmlFile = Path.Combine(Path.GetTempPath(), $"joule-task-{Environment.ProcessId}.xml");
            File.WriteAllText(xmlFile, TaskXml(), System.Text.Encoding.Unicode);
            args = $"/Create /F /TN \"{TaskName}\" /XML \"{xmlFile}\"";
        }
        else args = $"/Delete /F /TN \"{TaskName}\"";
        try
        {
            var elevated = Joule.Core.PowerRequestProbe.Elevated;
            var info = elevated
                ? new System.Diagnostics.ProcessStartInfo("schtasks.exe", args) { UseShellExecute = false, CreateNoWindow = true }
                : new System.Diagnostics.ProcessStartInfo("schtasks.exe", args) { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
            using var p = System.Diagnostics.Process.Start(info)!;
            p.WaitForExit(15000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
        finally { try { if (xmlFile != null) File.Delete(xmlFile); } catch { } }
    }

    private static string TaskXml()
    {
        var user = System.Security.SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
        var exe = System.Security.SecurityElement.Escape($"\"{ExePath}\"");
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts Joule with administrator rights at login, so it can see what keeps the PC awake.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{exe}</Command><Arguments>--background</Arguments></Exec></Actions>
            </Task>
            """;
    }

    /// <summary>The task exists with the current settings and points at this copy of Joule.</summary>
    public static bool ElevatedTaskIsCurrent()
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\" /XML") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = System.Diagnostics.Process.Start(info)!;
            var xml = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0
                && xml.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>")
                && xml.Contains("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>")
                && xml.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>")
                && xml.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Starts the elevated task now (no prompt), so the running copy can hand over.</summary>
    public static bool RunElevatedTask()
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("schtasks.exe", $"/Run /TN \"{TaskName}\"") { CreateNoWindow = true, UseShellExecute = false };
            using var p = System.Diagnostics.Process.Start(info)!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
