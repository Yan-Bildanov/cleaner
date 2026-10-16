namespace Cleaner.Core.Health;

/// <summary>
/// Чистая логика оценки: сырые данные → статус и подсказка. Без обращений к системе — покрыто тестами.
/// Пороги подобраны консервативно: «Внимание» = стоит посмотреть, «Проблема» = нужно действовать.
/// </summary>
public static class HealthRules
{
    public static HealthCheck Disk(DiskSample? system, IReadOnlyList<DiskSample> all)
    {
        if (system is null) return new("disk", "Диск", HealthStatus.Unknown, "—", "Не удалось получить данные о диске");

        var freePct = system.SizeBytes > 0 ? 100.0 * system.FreeBytes / system.SizeBytes : 100;
        var status = HealthStatus.Ok;
        var hints = new List<string>();

        if (freePct < 10) { status = HealthStatus.Problem; hints.Add("Почти нет места — запусти очистку или перенеси большие файлы"); }
        else if (freePct < 20) { status = Max(status, HealthStatus.Warning); hints.Add("Места становится мало"); }

        foreach (var d in all)
        {
            if (!d.Health.Equals("Healthy", StringComparison.OrdinalIgnoreCase) && d.Health != "")
            { status = HealthStatus.Problem; hints.Add($"{d.Name}: Windows сообщает «{d.Health}» — сделай резервную копию"); }
            if (d.WearPercent is >= 80) { status = HealthStatus.Problem; hints.Add($"{d.Name}: ресурс SSD израсходован на {d.WearPercent} %"); }
            else if (d.WearPercent is >= 50) { status = Max(status, HealthStatus.Warning); hints.Add($"{d.Name}: ресурс SSD израсходован на {d.WearPercent} %"); }
            if (d.TemperatureC is >= 70) { status = Max(status, HealthStatus.Warning); hints.Add($"{d.Name}: горячий — {d.TemperatureC} °C"); }
        }
        if (hints.Count == 0) hints.Add("Диск исправен");
        if (all.All(d => d.WearPercent is null)) hints.Add("Износ и температура SSD видны только с правами администратора");

        var details = all.Select(d =>
            $"{d.Name}: {(d.Health.Equals("Healthy", StringComparison.OrdinalIgnoreCase) ? "исправен" : d.Health == "" ? "состояние неизвестно" : d.Health)}" +
            (d.WearPercent is { } w ? $", износ {w} %" : "") +
            (d.TemperatureC is { } t ? $", {t} °C" : "")).ToList();
        return new("disk", "Диск", status, $"{Format.Bytes(system.FreeBytes)} свободно из {Format.Bytes(system.SizeBytes)}",
                   string.Join(". ", hints), details);
    }

    public static HealthCheck Battery(BatterySample? b)
    {
        if (b is null) return new("battery", "Батарея", HealthStatus.Unknown, "—", "Батарея не найдена или Windows не отдаёт данные");
        if (b.DesignCapacity <= 0) return new("battery", "Батарея", HealthStatus.Unknown, "—", "Windows не сообщает заводскую ёмкость");

        var pct = (int)Math.Round(100.0 * b.FullChargeCapacity / b.DesignCapacity);
        var (status, hint) = pct switch
        {
            >= 80 => (HealthStatus.Ok, "Батарея в хорошем состоянии"),
            >= 65 => (HealthStatus.Ok, "Обычный износ для ноутбука нескольких лет. Если он почти всегда на зарядке — включи ограничение заряда до 80 % в фирменной утилите производителя ноутбука"),
            >= 50 => (HealthStatus.Warning, "Заметный износ: время работы от батареи сократилось. Ограничь заряд до 80 %, если ноутбук стоит на зарядке"),
            _ => (HealthStatus.Problem, "Батарея сильно изношена — стоит подумать о замене"),
        };
        var details = new List<string>
        {
            $"Заводская ёмкость: {b.DesignCapacity / 1000.0:0.#} Вт·ч",
            $"Текущая полная ёмкость: {b.FullChargeCapacity / 1000.0:0.#} Вт·ч",
        };
        if (b.CycleCount is > 0) details.Add($"Циклов зарядки: {b.CycleCount}");
        return new("battery", "Батарея", status, $"{pct} % от заводской ёмкости", hint, details);
    }

    /// <param name="cpuC">Температура процессора (null — не удалось узнать, обычно нет прав администратора).</param>
    /// <param name="gpuC">Температура видеокарты NVIDIA (null — нет nvidia-smi).</param>
    public static HealthCheck Temperature(double? cpuC, double? gpuC, bool elevated)
    {
        if (cpuC is null && gpuC is null)
            return new("temp", "Температура", HealthStatus.Unknown, "—",
                elevated ? "Windows не отдаёт данные о температуре на этом ноутбуке" : "Температура процессора видна только с правами администратора");

        var status = HealthStatus.Ok;
        var hints = new List<string>();
        if (cpuC is >= 90) { status = HealthStatus.Problem; hints.Add("Процессор перегревается — проверь, не забиты ли вентиляционные решётки"); }
        else if (cpuC is >= 80) { status = HealthStatus.Warning; hints.Add("Процессор горячий. Под нагрузкой это нормально, в простое — нет"); }
        if (gpuC is >= 87) { status = HealthStatus.Problem; hints.Add("Видеокарта перегревается"); }
        else if (gpuC is >= 80) { status = Max(status, HealthStatus.Warning); hints.Add("Видеокарта горячая"); }
        if (hints.Count == 0) hints.Add("В норме");
        if (cpuC is null) hints.Add(elevated ? "Процессор: Windows не отдаёт данные" : "Процессор: нужны права администратора");

        var parts = new List<string>();
        if (cpuC is { } c) parts.Add($"ЦП {c:0} °C");
        if (gpuC is { } g) parts.Add($"ГП {g:0} °C");
        return new("temp", "Температура", status, string.Join(" · ", parts), string.Join(". ", hints));
    }

    public static HealthCheck Updates(DateTime? lastUpdate, TimeSpan uptime, bool rebootPending, DateTime now)
    {
        var status = HealthStatus.Ok;
        var hints = new List<string>();
        var days = lastUpdate is { } lu ? (int)(now - lu).TotalDays : (int?)null;

        if (days is null) { status = HealthStatus.Unknown; hints.Add("Не удалось узнать дату последнего обновления"); }
        else if (days > 60) { status = HealthStatus.Problem; hints.Add("Обновлений больше двух месяцев — открой Центр обновления Windows"); }
        else if (days > 35) { status = HealthStatus.Warning; hints.Add("Похоже, пропущено ежемесячное обновление — проверь Центр обновления"); }

        if (rebootPending) { status = Max(status, HealthStatus.Warning); hints.Add("Обновление ждёт перезагрузки"); }
        if (uptime.TotalDays >= 14) { status = Max(status, HealthStatus.Warning); hints.Add($"Ноутбук не перезагружался {(int)uptime.TotalDays} дн. — обновления ставятся только после перезагрузки"); }
        if (hints.Count == 0) hints.Add("Система обновлена");

        var value = days is { } d ? (d == 0 ? "Последнее обновление сегодня" : $"Последнее обновление {d} дн. назад") : "—";
        return new("updates", "Обновления", status, value, string.Join(". ", hints),
                   [$"Последнее обновление: {lastUpdate:d MMMM yyyy}", $"Без перезагрузки: {(int)uptime.TotalDays} дн. {uptime.Hours} ч"]);
    }

    /// <summary>productState из Центра безопасности: 0xAABBCC, BB = 0x10/0x11 — включён, CC = 0x00 — базы актуальны.</summary>
    public static (bool Enabled, bool UpToDate) DecodeAvState(uint state) =>
        ((((state >> 8) & 0xFF) & 0x10) != 0, (state & 0xFF) == 0x00);

    public static HealthCheck Protection(IReadOnlyList<AntivirusSample> av, IReadOnlyDictionary<string, bool?> firewall)
    {
        var products = av.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var states = g.Select(a => DecodeAvState(a.ProductState)).ToList();
                return (Name: g.Key, Enabled: states.Any(s => s.Enabled), UpToDate: states.Any(s => s.Enabled && s.UpToDate), Count: g.Count());
            }).ToList();

        var active = products.Where(p => p.Enabled).ToList();
        var status = HealthStatus.Ok;
        var hints = new List<string>();

        if (active.Count == 0) { status = HealthStatus.Problem; hints.Add("Ни один антивирус не включён"); }
        else if (active.Any(p => !p.UpToDate)) { status = HealthStatus.Warning; hints.Add("Антивирусные базы устарели"); }

        foreach (var p in products.Where(p => !p.Enabled))
            hints.Add($"{p.Name} установлен, но выключен" + (p.Count > 1 ? $" (и зарегистрирован {p.Count} раза — следы старых установок)" : "") +
                      ". Если лицензия закончилась — продли или удали, чтобы не держать лишний процесс");

        var offProfiles = firewall.Where(f => f.Value == false).Select(f => f.Key).ToList();
        if (offProfiles.Count > 0) { status = Max(status, HealthStatus.Warning); hints.Add($"Брандмауэр выключен: {string.Join(", ", offProfiles)}"); }

        if (hints.Count == 0) hints.Add("Антивирус и брандмауэр работают");
        var value = active.Count > 0 ? string.Join(", ", active.Select(p => p.Name)) : "Нет активного антивируса";

        var details = products.Select(p => $"{p.Name}: {(p.Enabled ? "включён" : "выключен")}{(p.Enabled ? (p.UpToDate ? ", базы актуальны" : ", базы устарели") : "")}")
            .Concat(firewall.Select(f => $"Брандмауэр ({f.Key}): {(f.Value switch { true => "включён", false => "выключен", _ => "неизвестно" })}"))
            .ToList();
        return new("protection", "Защита", status, value, string.Join(". ", hints), details);
    }

    /// <summary>Программы удалённого доступа: в автозагрузке без нужды — лишний риск (ими часто пользуются мошенники).</summary>
    static readonly string[] RemoteAccess = ["anydesk", "teamviewer", "rustdesk", "ammyy", "remote manipulator", "litemanager", "aeroadmin", "supremo"];

    public static bool IsRemoteAccess(string name) =>
        RemoteAccess.Any(r => name.Contains(r, StringComparison.OrdinalIgnoreCase));

    public static HealthCheck Startup(IReadOnlyList<StartupEntry> entries)
    {
        var enabled = entries.Where(e => e.Enabled).ToList();
        var status = HealthStatus.Ok;
        var hints = new List<string>();

        var remote = enabled.Where(e => RemoteAccess.Any(r => e.Name.Contains(r, StringComparison.OrdinalIgnoreCase))).ToList();
        if (remote.Count > 0)
        {
            status = HealthStatus.Warning;
            var names = string.Join(", ", remote.Select(r => r.Location == "служба Windows" ? $"{r.Name} (служба)" : r.Name).Distinct());
            hints.Add($"{names} — программа удалённого доступа запускается вместе с Windows. " +
                      "Если не пользуешься постоянно — отключи автозапуск (для службы: тип запуска «Вручную») или удали программу");
        }
        if (enabled.Count > 12) { status = Max(status, HealthStatus.Warning); hints.Add("Много программ стартует вместе с Windows — это замедляет включение"); }
        if (hints.Count == 0) hints.Add("Ничего подозрительного");

        var details = entries.OrderByDescending(e => e.Enabled).ThenBy(e => e.Name)
            .Select(e => $"{(e.Enabled ? "●" : "○")} {e.Name}  ({e.Location})").ToList();
        return new("startup", "Автозагрузка", status, $"{enabled.Count} программ включено, {entries.Count - enabled.Count} выключено",
                   string.Join(". ", hints), details);
    }

    static HealthStatus Max(HealthStatus a, HealthStatus b) =>
        Rank(a) >= Rank(b) ? a : b;

    static int Rank(HealthStatus s) => s switch
    {
        HealthStatus.Problem => 3, HealthStatus.Warning => 2, HealthStatus.Unknown => 1, _ => 0,
    };
}
