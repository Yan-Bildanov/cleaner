using Cleaner.Core.Cleaning;
using Cleaner.Core.Platform;
using Cleaner.Core.Rules;
using Cleaner.Core.Scanning;

namespace Cleaner.Core.Auto;

/// <summary>
/// Автоочистка без участия пользователя. Правила безопасности жёстче, чем у ручной:
///  • корзина и пункты, требующие прав администратора, — никогда;
///  • если приложение открыто (Chrome, VS Code…), его пункт пропускается целиком, а не «частично»;
///  • всё уходит в тот же карантин на 7 дней.
/// </summary>
public sealed class AutoCleaner(Scanner scanner, FileCleaner cleaner, QuarantineStore quarantine, ISystemInfo system)
{
    /// <summary>Какие правила вообще можно выбрать для автоочистки.</summary>
    public static IReadOnlyList<CleanRule> Eligible { get; } =
        BuiltInRules.All.Where(r => r.Kind == RuleKind.Files && !r.RequiresAdmin).ToList();

    /// <param name="force">«Запустить сейчас»: игнорировать порог.</param>
    /// <param name="dryRun">Только посчитать, ничего не перемещать.</param>
    public AutoRunResult Run(AutoCleanSettings settings, bool force = false, bool dryRun = false)
    {
        if (!dryRun) quarantine.PurgeExpired();

        var selected = Eligible.Where(r => settings.RuleIds.Contains(r.Id)).ToList();
        var skippedApps = new List<string>();
        var results = new List<RuleScanResult>();

        foreach (var rule in selected)
        {
            if (rule.ProcessNames.Any(system.IsProcessRunning)) { skippedApps.Add(rule.Name); continue; }
            results.Add(scanner.Scan(rule));
        }

        var found = results.Sum(r => r.TotalBytes);
        var below = !force && settings.Frequency == AutoFrequency.Threshold && found < settings.ThresholdBytes;
        var baseResult = new AutoRunResult
        {
            At = system.UtcNow, DryRun = dryRun, FoundBytes = found, SkippedOpenApps = skippedApps, BelowThreshold = below,
        };
        if (below || dryRun || found == 0) return baseResult;

        var report = cleaner.Clean(results, emptyRecycleBin: false);
        return baseResult with
        {
            CleanedBytes = report.MovedBytes, CleanedFiles = report.Moved, SkippedLocked = report.SkippedLocked,
        };
    }

    /// <summary>Короткий текст для уведомления и для вкладки «Расписание».</summary>
    public static string Describe(AutoRunResult r)
    {
        if (r.Error is { } e) return $"Ошибка: {e}";
        string text;
        if (r.DryRun) text = $"Пробный прогон: можно очистить {Format.Bytes(r.FoundBytes)}";
        else if (r.BelowThreshold) text = $"Мусора {Format.Bytes(r.FoundBytes)} — меньше порога, очистка не нужна";
        else if (r.CleanedFiles == 0) text = "Чистить нечего";
        else text = $"Очищено {Format.Bytes(r.CleanedBytes)} ({r.CleanedFiles:N0} файлов) — в карантин на 7 дней";
        if (r.SkippedOpenApps.Count > 0) text += $". Пропущено, потому что было открыто: {string.Join(", ", r.SkippedOpenApps)}";
        return text;
    }
}
