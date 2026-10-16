using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Xml.Linq;
using Cleaner.Core.Platform;
using Microsoft.Win32;

namespace Cleaner.Core.Health;

/// <summary>
/// Собирает данные о системе (только чтение) и прогоняет их через HealthRules.
/// Каждая проверка изолирована: сбой одной даёт «Неизвестно», остальные работают.
/// </summary>
public sealed class HealthCollector(ISystemInfo system)
{
    public static Action<Exception>? OnError { get; set; }

    public IReadOnlyList<HealthCheck> RunAll() =>
    [
        Safe("disk", "Диск", CheckDisk),
        Safe("battery", "Батарея", () => HealthRules.Battery(ReadBattery())),
        Safe("temp", "Температура", () => HealthRules.Temperature(ReadCpuTemp(), ReadGpuTemp(), system.IsElevated)),
        Safe("updates", "Обновления", () => HealthRules.Updates(ReadLastUpdate(), TimeSpan.FromMilliseconds(Environment.TickCount64), RebootPending(), DateTime.Now)),
        Safe("protection", "Защита", () => HealthRules.Protection(ReadAntivirus(), ReadFirewall())),
        Safe("startup", "Автозагрузка", () => HealthRules.Startup(ReadStartup())),
    ];

    static HealthCheck Safe(string id, string title, Func<HealthCheck> check)
    {
        try { return check(); }
        catch (Exception e)
        {
            OnError?.Invoke(new InvalidOperationException($"Проверка «{title}» не удалась", e));
            return new(id, title, HealthStatus.Unknown, "—", "Не удалось проверить: " + e.Message);
        }
    }

    // ---------- Диск ----------

    HealthCheck CheckDisk()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory)!;           // "C:\"
        var drive = new DriveInfo(root);
        var physical = ReadPhysicalDisks();
        var systemDisk = new DiskSample(root.TrimEnd('\\'), drive.TotalSize, drive.AvailableFreeSpace, "", null, null);
        return HealthRules.Disk(systemDisk, physical);
    }

    static List<DiskSample> ReadPhysicalDisks()
    {
        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
        var reliability = new Dictionary<string, (int? Wear, int? Temp)>();
        try   // счётчики надёжности (износ, температура) доступны только администратору
        {
            using var rs = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, Wear, Temperature FROM MSFT_StorageReliabilityCounter"));
            foreach (ManagementObject r in rs.Get())
                using (r)
                    reliability[r["DeviceId"]?.ToString() ?? ""] =
                        (r["Wear"] is { } w ? Convert.ToInt32(w) : null,
                         r["Temperature"] is { } t && Convert.ToInt32(t) > 0 ? Convert.ToInt32(t) : null);
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }

        var disks = new List<DiskSample>();
        using var ps = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, FriendlyName, Size, HealthStatus FROM MSFT_PhysicalDisk"));
        foreach (ManagementObject d in ps.Get())
            using (d)
            {
                var health = Convert.ToInt32(d["HealthStatus"]) switch { 0 => "Healthy", 1 => "Предупреждение", 2 => "Неисправен", _ => "" };
                reliability.TryGetValue(d["DeviceId"]?.ToString() ?? "", out var rel);
                disks.Add(new DiskSample(d["FriendlyName"]?.ToString()?.Trim() ?? "Диск", Convert.ToInt64(d["Size"]), 0, health, rel.Wear, rel.Temp));
            }
        return disks;
    }

    // ---------- Батарея (через powercfg — работает без прав администратора) ----------

    static BatterySample? ReadBattery()
    {
        var xml = Path.Combine(Path.GetTempPath(), $"cleaner-battery-{Guid.NewGuid():N}.xml");
        try
        {
            if (Run("powercfg.exe", $"/batteryreport /xml /output \"{xml}\"", 20_000) is null || !File.Exists(xml)) return null;
            var battery = XDocument.Load(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == "Battery");
            if (battery is null) return null;
            long Num(string name) => long.TryParse(battery.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value, out var v) ? v : 0;
            var cycles = (int)Num("CycleCount");
            return new BatterySample(Num("DesignCapacity"), Num("FullChargeCapacity"), cycles > 0 ? cycles : null);
        }
        finally { try { File.Delete(xml); } catch { } }
    }

    // ---------- Температура ----------

    static double? ReadCpuTemp()
    {
        try   // ACPI-зоны: в десятых долях кельвина; доступны только администратору
        {
            using var s = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            double? max = null;
            foreach (ManagementObject z in s.Get())
                using (z)
                {
                    var c = Convert.ToDouble(z["CurrentTemperature"]) / 10 - 273.15;
                    if (c is > 5 and < 125) max = Math.Max(max ?? c, c);       // отбрасываем заведомо фальшивые значения
                }
            return max;
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException) { return null; }
    }

    static double? ReadGpuTemp()
    {
        var smi = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        if (!File.Exists(smi)) return null;
        var output = Run(smi, "--query-gpu=temperature.gpu --format=csv,noheader,nounits", 10_000);
        return double.TryParse(output?.Split('\n')[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : null;
    }

    // ---------- Обновления ----------

    static DateTime? ReadLastUpdate()
    {
        using var s = new ManagementObjectSearcher("SELECT InstalledOn FROM Win32_QuickFixEngineering");
        DateTime? last = null;
        foreach (ManagementObject q in s.Get())
            using (q)
                if (DateTime.TryParseExact(q["InstalledOn"]?.ToString(), ["M/d/yyyy", "MM/dd/yyyy", "yyyyMMdd", "d.M.yyyy"],
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d <= DateTime.Now.AddDays(1))
                    last = last is null || d > last ? d : last;
        return last;
    }

    static bool RebootPending() =>
        KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") ||
        KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");

    static bool KeyExists(string path)
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(path); return k is not null; }
        catch (System.Security.SecurityException) { return false; }
    }

    // ---------- Защита ----------

    static List<AntivirusSample> ReadAntivirus()
    {
        var list = new List<AntivirusSample>();
        using var s = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct");
        foreach (ManagementObject a in s.Get())
            using (a) list.Add(new AntivirusSample(a["displayName"]?.ToString() ?? "?", Convert.ToUInt32(a["productState"])));
        return list;
    }

    static Dictionary<string, bool?> ReadFirewall()
    {
        var result = new Dictionary<string, bool?>();
        foreach (var (key, name) in new[] { ("DomainProfile", "доменная сеть"), ("StandardProfile", "частная сеть"), ("PublicProfile", "общественная сеть") })
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{key}");
                result[name] = k?.GetValue("EnableFirewall") is int v ? v != 0 : null;
            }
            catch (System.Security.SecurityException) { result[name] = null; }
        }
        return result;
    }

    // ---------- Автозагрузка ----------

    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    static List<StartupEntry> ReadStartup()
    {
        var entries = new List<StartupEntry>();
        void FromRun(RegistryKey hive, string path, string approvedKey, string where)
        {
            using var k = hive.OpenSubKey(path);
            if (k is null) return;
            foreach (var name in k.GetValueNames().Where(n => n.Length > 0))
                entries.Add(new StartupEntry(name, where, IsEnabled(hive, approvedKey, name)));
        }
        void FromFolder(string folder, RegistryKey hive, string where)
        {
            if (!Directory.Exists(folder)) return;
            foreach (var f in Directory.EnumerateFiles(folder).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
                entries.Add(new StartupEntry(Path.GetFileNameWithoutExtension(f), where, IsEnabled(hive, "StartupFolder", Path.GetFileName(f))));
        }

        FromRun(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", "пользователь");
        FromRun(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Run", "все пользователи");
        FromRun(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", "все пользователи");
        FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, "папка автозагрузки");
        FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine, "общая папка автозагрузки");

        // Программы удалённого доступа часто стартуют как служба Windows — ярлык может быть выключен, а служба работает.
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, DisplayName, StartMode FROM Win32_Service");
            foreach (ManagementObject svc in s.Get())
                using (svc)
                {
                    var name = svc["DisplayName"]?.ToString() ?? svc["Name"]?.ToString() ?? "";
                    if (HealthRules.IsRemoteAccess(name))
                        entries.Add(new StartupEntry(name, "служба Windows", svc["StartMode"]?.ToString() == "Auto"));
                }
        }
        catch (ManagementException) { }
        return entries;
    }

    /// <summary>StartupApproved: первый байт чётный (02, 06) — включено, нечётный (03) — выключено в Диспетчере задач.</summary>
    static bool IsEnabled(RegistryKey hive, string approvedKey, string name)
    {
        try
        {
            using var k = hive.OpenSubKey(Approved + approvedKey);
            return k?.GetValue(name) is not byte[] { Length: > 0 } b || (b[0] & 1) == 0;
        }
        catch (System.Security.SecurityException) { return true; }
    }

    // ---------- Вспомогательное ----------

    static string? Run(string exe, string args, int timeoutMs)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            })!;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return null; }
            return p.ExitCode == 0 ? output.Result : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
