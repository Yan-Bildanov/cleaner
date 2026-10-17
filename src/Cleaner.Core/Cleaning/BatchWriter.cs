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

    /// <summary>Запомнить удалённую (пустую) папку — при возврате она будет создана заново.</summary>
    public void RecordDirectory(string path, string ruleId)
    {
        _manifest.WriteLine(JsonSerializer.Serialize(new QuarantineEntry(path, "", 0, ruleId, QuarantineEntry.KindDir)));
        MovedCount++;
    }

    /// <summary>
    /// Ключ реестра: экспорт в .reg внутри партии, затем удаление ключа. Ключ удаляется, только если экспорт удался.
    /// </summary>
    public RegistryMoveOutcome MoveRegistryKey(string keyPath, Microsoft.Win32.RegistryView view, string ruleId)
    {
        var stored = (++_counter).ToString("D6") + ".reg";
        var file = Path.Combine(Dir, QuarantineStore.FilesDir, stored);
        var kind = RegistryBackup.KindFor(keyPath, view);
        var outcome = RegistryBackup.ExportAndDelete(keyPath, view, file);
        if (outcome != RegistryMoveOutcome.Moved)
        {
            try { File.Delete(file); } catch { }
            return outcome;
        }
        long size = 0;
        try { size = new FileInfo(file).Length; } catch { }
        _manifest.WriteLine(JsonSerializer.Serialize(new QuarantineEntry(keyPath, stored, size, ruleId, kind)));
        MovedCount++;
        return outcome;
    }

    public void Dispose()
    {
        _manifest.Dispose();
        // Если ничего не перенесли — пустая партия не нужна.
        if (MovedCount == 0) try { Directory.Delete(Dir, recursive: true); } catch { }
    }
}
