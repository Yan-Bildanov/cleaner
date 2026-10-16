using Cleaner.Core.Platform;
using Cleaner.Core.Rules;

namespace Cleaner.Core.Scanning;

public sealed record FoundFile(string Path, long Size);

public sealed class RuleScanResult
{
    public required CleanRule Rule { get; init; }
    public List<FoundFile> Files { get; } = [];
    public long TotalBytes { get; set; }
    public long ItemCount { get; set; }
    /// <summary>Файлы, пропущенные из-за возрастного фильтра (свежие).</summary>
    public int SkippedRecent { get; set; }
    /// <summary>Правило требует прав администратора, а программа запущена без них.</summary>
    public bool NeedsAdmin { get; set; }
    /// <summary>Запущено приложение, чьи файлы чистим, — часть файлов будет занята.</summary>
    public IReadOnlyList<string> RunningProcesses { get; set; } = [];
}

/// <summary>
/// Анализ: находит файлы по правилам и считает размер. Ничего не удаляет и не изменяет.
/// </summary>
public sealed class Scanner(ISystemInfo system)
{
    static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        // Пропускаем ссылки/junction (не выходим за белый список); скрытые и системные файлы — учитываем.
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    public RuleScanResult Scan(CleanRule rule, CancellationToken ct = default)
    {
        var result = new RuleScanResult
        {
            Rule = rule,
            RunningProcesses = rule.ProcessNames.Where(system.IsProcessRunning).ToList(),
        };

        if (rule.Kind == RuleKind.RecycleBin)
        {
            var (bytes, items) = system.QueryRecycleBin();
            result.TotalBytes = bytes;
            result.ItemCount = items;
            return result;
        }

        if (rule.RequiresAdmin && !system.IsElevated)
        {
            result.NeedsAdmin = true;
            return result;
        }

        var now = system.UtcNow;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pattern in rule.Paths)
        foreach (var root in PathExpander.ExpandDirectories(pattern))
        {
            IEnumerable<FileInfo> files;
            try { files = new DirectoryInfo(root).EnumerateFiles(rule.FilePattern, Options); }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException) { continue; }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!seen.Add(file.FullName)) continue;               // пути разных правил могут пересекаться

                long size;
                DateTime newest;
                try
                {
                    size = file.Length;
                    newest = file.LastWriteTimeUtc > file.CreationTimeUtc ? file.LastWriteTimeUtc : file.CreationTimeUtc;
                }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException) { continue; }

                if (rule.MinAge > TimeSpan.Zero && now - newest < rule.MinAge)
                {
                    result.SkippedRecent++;
                    continue;
                }

                result.Files.Add(new FoundFile(file.FullName, size));
                result.TotalBytes += size;
            }
        }

        result.ItemCount = result.Files.Count;
        return result;
    }
}
