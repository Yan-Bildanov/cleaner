namespace Cleaner.Core.Scanning;

/// <summary>
/// Раскрывает путь из правила в список реально существующих папок:
/// подставляет переменные окружения и поддерживает '*' в сегментах папок.
/// Пример: "%LOCALAPPDATA%\Google\Chrome\User Data\*\Cache".
/// </summary>
public static class PathExpander
{
    public static IEnumerable<string> ExpandDirectories(string pattern)
    {
        var expanded = Environment.ExpandEnvironmentVariables(pattern);
        if (expanded.Contains('%')) yield break;               // переменная не определена — пропускаем

        var segments = expanded.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) yield break;

        foreach (var dir in Walk(segments[0] + "\\", segments, 1))
            yield return dir;
    }

    static IEnumerable<string> Walk(string current, string[] segments, int index)
    {
        if (index == segments.Length)
        {
            if (Directory.Exists(current)) yield return Path.TrimEndingDirectorySeparator(current);
            yield break;
        }

        var segment = segments[index];
        if (!segment.Contains('*') && !segment.Contains('?'))
        {
            foreach (var d in Walk(Path.Join(current, segment), segments, index + 1)) yield return d;
            yield break;
        }

        if (!Directory.Exists(current)) yield break;
        IEnumerable<string> matches;
        try { matches = Directory.EnumerateDirectories(current, segment).ToList(); }
        catch (UnauthorizedAccessException) { yield break; }
        catch (IOException) { yield break; }

        foreach (var match in matches)
        {
            // Не ходим по ссылкам/junction — чтобы не выйти за пределы белого списка.
            if (new DirectoryInfo(match).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            foreach (var d in Walk(match, segments, index + 1)) yield return d;
        }
    }
}
