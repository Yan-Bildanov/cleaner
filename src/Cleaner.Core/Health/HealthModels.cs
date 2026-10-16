namespace Cleaner.Core.Health;

public enum HealthStatus { Ok, Warning, Problem, Unknown }

/// <summary>Одна плитка на вкладке «Система».</summary>
public sealed record HealthCheck(
    string Id,
    string Title,
    HealthStatus Status,
    /// <summary>Короткое значение на плитке: «74 %», «100 ГБ свободно».</summary>
    string Value,
    /// <summary>Пояснение и что делать — показывается под значением.</summary>
    string? Hint = null,
    /// <summary>Подробности (по строке) — для раскрывающегося списка.</summary>
    IReadOnlyList<string>? Details = null);

public sealed record HardwareInfo(
    string Model,
    string Os,
    string Cpu,
    string Ram,
    IReadOnlyList<string> RamModules,
    IReadOnlyList<string> Gpus,
    IReadOnlyList<string> Disks,
    string Bios);

// ---------- Сырые данные, из которых считаются статусы (подставляются в тестах) ----------

public sealed record DiskSample(string Name, long SizeBytes, long FreeBytes, string Health, int? WearPercent, int? TemperatureC);
public sealed record BatterySample(long DesignCapacity, long FullChargeCapacity, int? CycleCount);
public sealed record AntivirusSample(string Name, uint ProductState);
public sealed record StartupEntry(string Name, string Location, bool Enabled);
