using System.Text.Json;
using Cleaner.Core.Platform;

namespace Cleaner.Core.Cleaning;

public sealed record QuarantineEntry(string Original, string Stored, long Size, string RuleId);

public sealed class QuarantineBatch
{
    public required string Id { get; init; }
    public required string Dir { get; init; }
    public DateTime CreatedUtc { get; init; }
    public List<QuarantineEntry> Entries { get; init; } = [];
    public long TotalBytes => Entries.Sum(e => e.Size);
    public DateTime ExpiresUtc => CreatedUtc + QuarantineStore.Retention;
}

public sealed record RestoreReport(int Restored, int SkippedExisting, int Failed);
public sealed record DeleteReport(long FreedBytes, int InUse, int Denied);

/// <summary>
/// Карантин: каждая очистка — отдельная папка-«партия» с файлами и манифестом (откуда взят каждый файл).
/// Через 7 дней партия стирается навсегда. До этого всё можно вернуть на место.
/// Структура: root\{id}\batch.json, root\{id}\manifest.jsonl, root\{id}\files\000001 ...
/// </summary>
public sealed class QuarantineStore(string root, ISystemInfo system)
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    public static string DefaultRoot => AppPaths.QuarantineDir;

    internal const string InfoFile = "batch.json";
    internal const string ManifestFile = "manifest.jsonl";
    internal const string FilesDir = "files";

    public string Root { get; } = root;

    /// <summary>Куда сообщать о пропущенных партиях (приложение пишет это в журнал ошибок).</summary>
    public static Action<Exception>? OnError { get; set; }

    sealed record BatchInfo(DateTime CreatedUtc);

    public BatchWriter Begin()
    {
        var now = system.UtcNow;
        var id = now.ToString("yyyyMMdd-HHmmss-fff");
        var dir = Path.Combine(Root, id);
        for (var n = 2; Directory.Exists(dir); n++) dir = Path.Combine(Root, $"{id}-{n}");

        Directory.CreateDirectory(Path.Combine(dir, FilesDir));
        File.WriteAllText(Path.Combine(dir, InfoFile), JsonSerializer.Serialize(new BatchInfo(now)));
        return new BatchWriter(dir);
    }

    public IReadOnlyList<QuarantineBatch> List()
    {
        if (!Directory.Exists(Root)) return [];
        var batches = new List<QuarantineBatch>();
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            try
            {
                var info = JsonSerializer.Deserialize<BatchInfo>(File.ReadAllText(Path.Combine(dir, InfoFile)))!;
                var manifest = Path.Combine(dir, ManifestFile);
                var entries = new List<QuarantineEntry>();
                if (File.Exists(manifest))
                {
                    // Читаем, даже если файл открыт другим экземпляром программы.
                    using var reader = new StreamReader(new FileStream(manifest, FileMode.Open, FileAccess.Read,
                                                                       FileShare.ReadWrite | FileShare.Delete));
                    while (reader.ReadLine() is { } line)
                        if (line.Length > 0 && JsonSerializer.Deserialize<QuarantineEntry>(line) is { } entry)
                            entries.Add(entry);
                }
                batches.Add(new QuarantineBatch { Id = Path.GetFileName(dir), Dir = dir, CreatedUtc = info.CreatedUtc, Entries = entries });
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                // Повреждённая/недоступная партия — пропускаем, но не удаляем, и пишем причину в журнал.
                OnError?.Invoke(new IOException($"Карантин: не удалось прочитать партию {dir}", e));
            }
        }
        return batches.OrderByDescending(b => b.CreatedUtc).ToList();
    }

    /// <summary>Вернуть файлы на исходные места. Если там уже появился новый файл — не перезаписываем.</summary>
    public RestoreReport Restore(QuarantineBatch batch)
    {
        int restored = 0, existing = 0, failed = 0;
        var remaining = new List<QuarantineEntry>();
        foreach (var e in batch.Entries)
        {
            var stored = Path.Combine(batch.Dir, FilesDir, e.Stored);
            try
            {
                if (File.Exists(e.Original)) { existing++; remaining.Add(e); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(e.Original)!);
                File.Move(stored, e.Original);
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                remaining.Add(e);
            }
        }

        if (remaining.Count == 0) Directory.Delete(batch.Dir, recursive: true);
        else File.WriteAllLines(Path.Combine(batch.Dir, ManifestFile), remaining.Select(x => JsonSerializer.Serialize(x)));
        return new RestoreReport(restored, existing, failed);
    }

    /// <summary>
    /// Стереть партию навсегда. Что удалить не удалось, остаётся в карантине:
    /// InUse — файл всё ещё открыт программой (Chrome, Проводник) — удалится после её закрытия или перезагрузки;
    /// Denied — нет прав (файлы из C:\Windows\Temp и т.п.) — нужны права администратора.
    /// </summary>
    public DeleteReport Delete(QuarantineBatch batch)
    {
        var left = new List<QuarantineEntry>();
        int inUse = 0, denied = 0;
        foreach (var e in batch.Entries)
        {
            var stored = Path.Combine(batch.Dir, FilesDir, e.Stored);
            try { if (File.Exists(stored)) { File.SetAttributes(stored, FileAttributes.Normal); File.Delete(stored); } }
            catch (UnauthorizedAccessException) { left.Add(e); denied++; }
            catch (IOException) { left.Add(e); inUse++; }
        }
        if (left.Count == 0) Directory.Delete(batch.Dir, recursive: true);
        else File.WriteAllLines(Path.Combine(batch.Dir, ManifestFile), left.Select(x => JsonSerializer.Serialize(x)));
        return new DeleteReport(batch.TotalBytes - left.Sum(x => x.Size), inUse, denied);
    }

    /// <summary>Стереть партии старше 7 дней. Возвращает число стёртых партий.</summary>
    public int PurgeExpired()
    {
        var now = system.UtcNow;
        var count = 0;
        foreach (var b in List().Where(b => now >= b.ExpiresUtc))
        {
            try { Delete(b); count++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return count;
    }
}
