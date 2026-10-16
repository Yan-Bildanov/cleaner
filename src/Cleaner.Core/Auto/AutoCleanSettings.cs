using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cleaner.Core.Auto;

public enum AutoFrequency
{
    /// <summary>Раз в неделю (воскресенье, 12:00).</summary>
    Weekly,
    /// <summary>Каждый день в 12:00.</summary>
    Daily,
    /// <summary>Проверка каждый день, очистка — только если мусора больше порога.</summary>
    Threshold,
}

public sealed record AutoCleanSettings
{
    public bool Enabled { get; init; }
    public AutoFrequency Frequency { get; init; } = AutoFrequency.Weekly;
    public long ThresholdBytes { get; init; } = 1L << 30;     // 1 ГБ
    /// <summary>
    /// Какие правила чистить автоматически. Корзина и пункты, требующие админа, сюда не попадают никогда.
    /// По умолчанию — то же, что отмечено в ручной очистке (включая личные правила из rules.local.json).
    /// </summary>
    public List<string> RuleIds { get; init; } =
        AutoCleaner.Eligible.Where(r => r.DefaultChecked).Select(r => r.Id).ToList();
}

/// <summary>Результат последней автоочистки — показываем на вкладке «Расписание».</summary>
public sealed record AutoRunResult
{
    public DateTime At { get; init; }
    public bool DryRun { get; init; }
    public long FoundBytes { get; init; }
    public long CleanedBytes { get; init; }
    public int CleanedFiles { get; init; }
    public int SkippedLocked { get; init; }
    /// <summary>Пункты, пропущенные целиком, потому что приложение было открыто.</summary>
    public List<string> SkippedOpenApps { get; init; } = [];
    /// <summary>Режим «по порогу»: мусора меньше порога — ничего не делали.</summary>
    public bool BelowThreshold { get; init; }
    public string? Error { get; init; }
}

/// <summary>Хранение настроек и последнего результата в %USERPROFILE%\.cleaner (см. AppPaths).</summary>
public sealed class AutoCleanStore(string dir)
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static AutoCleanStore Default { get; } = new(AppPaths.DataDir);

    string SettingsPath => Path.Combine(dir, "settings.json");
    string LastRunPath => Path.Combine(dir, "last-auto-run.json");

    public AutoCleanSettings LoadSettings() => Read<AutoCleanSettings>(SettingsPath) ?? new();
    public void SaveSettings(AutoCleanSettings s) => Write(SettingsPath, s);
    public AutoRunResult? LoadLastRun() => Read<AutoRunResult>(LastRunPath);
    public void SaveLastRun(AutoRunResult r) => Write(LastRunPath, r);

    static T? Read<T>(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default; }
        catch (JsonException) { return default; }   // испорченный файл — работаем с настройками по умолчанию
    }

    void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
