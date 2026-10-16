using System.Text.Json;

namespace Cleaner.Core.Rules;

/// <summary>
/// Личные правила очистки из %USERPROFILE%\.cleaner\rules.local.json — для программ, которые не стоит
/// упоминать в общем коде (например, рабочие). Файл не входит в репозиторий. Пример:
/// <code>
/// [
///   { "id": "local.myapp", "name": "MyApp — кэш", "electronApp": "%APPDATA%\\MyApp", "processNames": ["MyApp"] },
///   { "id": "local.logs", "group": "Разработка", "name": "Логи", "paths": ["%LOCALAPPDATA%\\MyTool\\logs"],
///     "minAgeDays": 7, "defaultChecked": false }
/// ]
/// </code>
/// electronApp — корень приложения на Electron: чистятся его Cache, Code Cache и GPUCache.
/// Те же правила безопасности, что и у встроенных: только перечисленные папки, карантин на 7 дней.
/// </summary>
public static class LocalRules
{
    public static string FilePath => Path.Combine(AppPaths.DataDir, "rules.local.json");
    public static Action<Exception>? OnError { get; set; }

    sealed record Entry(
        string? Id, string? Name, string? Group, string? Note, string? ElectronApp, string[]? Paths,
        string? FilePattern, double? MinAgeDays, bool? DefaultChecked, string[]? ProcessNames);

    public static IReadOnlyList<CleanRule> Load() => Load(FilePath);

    public static IReadOnlyList<CleanRule> Load(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            var entries = JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) ?? [];
            var rules = new List<CleanRule>();
            foreach (var e in entries)
            {
                if (string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.Name)) continue;
                var paths = (e.ElectronApp is { } app ? BuiltInRules.Electron(app) : []).Concat(e.Paths ?? []).ToList();
                if (paths.Count == 0 || !paths.All(IsSafe)) { OnError?.Invoke(new InvalidDataException($"rules.local.json: правило «{e.Id}» пропущено — небезопасный или пустой путь")); continue; }
                rules.Add(new CleanRule
                {
                    Id = e.Id, Name = e.Name, Group = e.Group ?? BuiltInRules.GroupApps, Note = e.Note,
                    Paths = paths, FilePattern = e.FilePattern ?? "*",
                    MinAge = TimeSpan.FromDays(e.MinAgeDays ?? 0), DefaultChecked = e.DefaultChecked ?? true,
                    ProcessNames = e.ProcessNames ?? [],
                });
            }
            return rules;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            OnError?.Invoke(new InvalidDataException("rules.local.json не прочитан", ex));
            return [];
        }
    }

    /// <summary>
    /// Защита от опечатки вроде "%USERPROFILE%" или "C:\": путь должен быть глубже папки профиля
    /// и не указывать на корень диска, Windows или Program Files.
    /// </summary>
    public static bool IsSafe(string pattern)
    {
        var p = Environment.ExpandEnvironmentVariables(pattern).TrimEnd('\\');
        if (p.Contains('%') || !Path.IsPathFullyQualified(p)) return false;
        var depth = p.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;     // C:\Users\name\X → 4
        string[] roots = [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                          Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                          Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                          Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)];
        return depth >= 4 && !roots.Any(r => string.Equals(r.TrimEnd('\\'), p, StringComparison.OrdinalIgnoreCase));
    }
}
