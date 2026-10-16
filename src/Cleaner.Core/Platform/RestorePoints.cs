using System.Management;
using Microsoft.Win32;

namespace Cleaner.Core.Platform;

public enum RestorePointStatus
{
    Created,
    /// <summary>Нужны права администратора.</summary>
    NotElevated,
    /// <summary>«Защита системы» выключена для диска C:.</summary>
    ProtectionOff,
    /// <summary>Windows приняла запрос, но точку не создала (обычно — лимит «раз в сутки»).</summary>
    Skipped,
    Failed,
}

public sealed record RestorePointResult(RestorePointStatus Status, string? Error = null);

/// <summary>
/// Точки восстановления Windows через WMI (root\default:SystemRestore). Требует прав администратора.
/// Защищают системные файлы и реестр; файлы кэшей не возвращают (для них — карантин).
/// </summary>
public sealed class RestorePoints(ISystemInfo system)
{
    const int ModifySettings = 12, BeginSystemChange = 100;

    /// <summary>Включена ли «Защита системы» для диска C:. null — не удалось узнать (нет прав).</summary>
    public bool? IsProtectionEnabledForSystemDrive()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SPP\Clients");
            if (key is null) return false;
            var volumeId = SystemDriveVolumeId();
            if (volumeId is null) return null;
            // Значения ключа — списки защищённых томов вида "\\?\Volume{GUID}\:..." 
            return key.GetValueNames()
                .SelectMany(n => key.GetValue(n) as string[] ?? [])
                .Any(v => v.Contains(volumeId, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { return null; }
    }

    static string? SystemDriveVolumeId()
    {
        var drive = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');   // "C:"
        using var searcher = new ManagementObjectSearcher($"SELECT DeviceID FROM Win32_Volume WHERE DriveLetter = '{drive}'");
        foreach (ManagementObject v in searcher.Get())
            using (v) return (v["DeviceID"] as string)?.TrimEnd('\\');                // "\\?\Volume{...}"
        return null;
    }

    /// <summary>Создать точку восстановления. Может занять от нескольких секунд до минуты.</summary>
    public RestorePointResult Create(string description)
    {
        if (!system.IsElevated) return new(RestorePointStatus.NotElevated);
        if (IsProtectionEnabledForSystemDrive() == false) return new(RestorePointStatus.ProtectionOff);

        try
        {
            var before = LatestSequenceNumber();
            using var cls = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
            using var input = cls.GetMethodParameters("CreateRestorePoint");
            input["Description"] = description;
            input["RestorePointType"] = ModifySettings;
            input["EventType"] = BeginSystemChange;
            using var output = cls.InvokeMethod("CreateRestorePoint", input, null);
            var code = Convert.ToUInt32(output["ReturnValue"]);
            if (code != 0) return new(RestorePointStatus.Failed, $"код Windows {code}");

            return LatestSequenceNumber() > before
                ? new(RestorePointStatus.Created)
                : new(RestorePointStatus.Skipped);
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return new(RestorePointStatus.Failed, e.Message);
        }
    }

    static long LatestSequenceNumber()
    {
        using var searcher = new ManagementObjectSearcher(@"root\default", "SELECT SequenceNumber FROM SystemRestore");
        long max = 0;
        foreach (ManagementObject p in searcher.Get())
            using (p) max = Math.Max(max, Convert.ToInt64(p["SequenceNumber"]));
        return max;
    }
}
