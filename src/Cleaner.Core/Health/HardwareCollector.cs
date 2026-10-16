using System.Management;
using Microsoft.Win32;

namespace Cleaner.Core.Health;

/// <summary>Информация о железе (аналог Speccy) — только чтение, без прав администратора.</summary>
public static class HardwareCollector
{
    public static HardwareInfo Read()
    {
        var cs = First("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem");
        var os = First("SELECT Caption, BuildNumber FROM Win32_OperatingSystem");
        var cpu = First("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
        var bios = First("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");

        var displayVersion = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", null);
        var memory = All("SELECT Capacity, Speed, Manufacturer FROM Win32_PhysicalMemory");
        var installed = memory.Sum(m => Convert.ToInt64(m["Capacity"]));
        var modules = memory
            .Select(m => $"{Format.Bytes(Convert.ToInt64(m["Capacity"]))} · {m["Speed"]} МГц · {m["Manufacturer"]?.ToString()?.Trim()}").ToList();
        var gpus = All("SELECT Name, DriverVersion, DriverDate FROM Win32_VideoController")
            .Select(g => $"{g["Name"]} · драйвер {g["DriverVersion"]} от {CimDate(g["DriverDate"]):d MMMM yyyy}").ToList();
        var disks = DiskModels();

        return new HardwareInfo(
            Model: $"{cs?["Manufacturer"]} {cs?["Model"]}".Trim(),
            Os: $"{os?["Caption"]} {displayVersion} (сборка {os?["BuildNumber"]})".Replace("Майкрософт ", "").Trim(),
            Cpu: $"{cpu?["Name"]?.ToString()?.Trim()} · {cpu?["NumberOfCores"]} ядер / {cpu?["NumberOfLogicalProcessors"]} потоков",
            Ram: $"{Format.Bytes(installed)} ({modules.Count} {(modules.Count == 1 ? "планка" : "планки")}), " +
                 $"доступно системе {Format.Bytes(Convert.ToInt64(cs?["TotalPhysicalMemory"] ?? 0L))} — часть отдана встроенной графике",
            RamModules: modules,
            Gpus: gpus,
            Disks: disks,
            Bios: $"{bios?["SMBIOSBIOSVersion"]} от {CimDate(bios?["ReleaseDate"]):d MMMM yyyy}");
    }

    static List<string> DiskModels()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT FriendlyName, Size, MediaType, BusType FROM MSFT_PhysicalDisk");
            return s.Get().Cast<ManagementObject>().Select(d =>
            {
                using (d)
                {
                    var media = Convert.ToInt32(d["MediaType"]) switch { 3 => "HDD", 4 => "SSD", _ => "" };
                    var bus = Convert.ToInt32(d["BusType"]) switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", _ => "" };
                    return $"{d["FriendlyName"]?.ToString()?.Trim()} · {Format.Bytes(Convert.ToInt64(d["Size"]))} · {string.Join(" ", new[] { media, bus }.Where(x => x != ""))}".TrimEnd(' ', '·');
                }
            }).ToList();
        }
        catch (ManagementException) { return []; }
    }

    static DateTime? CimDate(object? value) =>
        value?.ToString() is { Length: >= 8 } s ? ManagementDateTimeConverter.ToDateTime(s) : null;

    static ManagementObject? First(string query) => All(query).FirstOrDefault();

    static List<ManagementObject> All(string query)
    {
        try
        {
            using var s = new ManagementObjectSearcher(query);
            return s.Get().Cast<ManagementObject>().ToList();
        }
        catch (ManagementException) { return []; }
    }
}
