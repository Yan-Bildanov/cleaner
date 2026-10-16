using System.Text.Json;

namespace Cleaner.Core.Cleaning;

public enum MoveOutcome { Moved, Locked, Missing }

/// <summary>Переносит файлы в одну партию карантина и сразу дописывает манифест (по строке на файл).</summary>
public sealed class BatchWriter : IDisposable
{
    readonly StreamWriter _manifest;
    int _counter;

    internal BatchWriter(string dir)
    {
        Dir = dir;
        _manifest = new StreamWriter(Path.Combine(dir, QuarantineStore.ManifestFile), append: true) { AutoFlush = true };
    }

    public string Dir { get; }
    public string Id => Path.GetFileName(Dir);
    public int MovedCount { get; private set; }

    public MoveOutcome Move(string source, long size, string ruleId)
    {
        try
        {
            var attrs = File.GetAttributes(source);
            if (attrs.HasFlag(FileAttributes.ReparsePoint) || attrs.HasFlag(FileAttributes.Directory)) return MoveOutcome.Missing;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return MoveOutcome.Missing; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return MoveOutcome.Locked; }

        // Файл, открытый программой (Chrome, Проводник), Windows всё равно даёт переместить, если программа
        // открыла его с разрешением на удаление. Но программа продолжит им пользоваться уже в карантине,
        // и удалить его не получится, пока она не закроется. Поэтому открытые файлы не трогаем вовсе.
        try { using var probe = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return MoveOutcome.Missing; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return MoveOutcome.Locked; }

        var stored = (++_counter).ToString("D6");
        try
        {
            File.Move(source, Path.Combine(Dir, QuarantineStore.FilesDir, stored));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return MoveOutcome.Missing; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return MoveOutcome.Locked; }

        _manifest.WriteLine(JsonSerializer.Serialize(new QuarantineEntry(source, stored, size, ruleId)));
        MovedCount++;
        return MoveOutcome.Moved;
    }

    public void Dispose()
    {
        _manifest.Dispose();
        // Если ничего не перенесли — пустая партия не нужна.
        if (MovedCount == 0) try { Directory.Delete(Dir, recursive: true); } catch { }
    }
}
