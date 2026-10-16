namespace Cleaner.Core.Rules;

public enum RuleKind
{
    /// <summary>Файлы по путям из белого списка.</summary>
    Files,
    /// <summary>Корзина Windows (удаляется безвозвратно, только с отдельным подтверждением).</summary>
    RecycleBin,
}

/// <summary>
/// Одно правило очистки = одна галочка в интерфейсе.
/// Пути — только из белого списка, с переменными окружения (%LOCALAPPDATA%)
/// и '*' в сегментах папок (напр. "...\User Data\*\Cache").
/// </summary>
public sealed record CleanRule
{
    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string Name { get; init; }
    public string? Note { get; init; }
    public RuleKind Kind { get; init; } = RuleKind.Files;
    public IReadOnlyList<string> Paths { get; init; } = [];
    /// <summary>Маска файлов внутри путей (по умолчанию все файлы, рекурсивно).</summary>
    public string FilePattern { get; init; } = "*";
    /// <summary>Файлы моложе этого возраста не трогаем.</summary>
    public TimeSpan MinAge { get; init; } = TimeSpan.Zero;
    public bool RequiresAdmin { get; init; }
    public bool DefaultChecked { get; init; } = true;
    /// <summary>Процессы, которые должны быть закрыты, иначе часть файлов будет занята.</summary>
    public IReadOnlyList<string> ProcessNames { get; init; } = [];
}
