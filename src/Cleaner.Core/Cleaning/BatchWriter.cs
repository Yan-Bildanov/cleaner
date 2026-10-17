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

    /// <summary>Служба: копия ключа → .reg, затем остановка и sc delete.</summary>
    public bool MoveService(string serviceName, string ruleId)
    {
        var stored = (++_counter).ToString("D6") + ".reg";
        var file = Path.Combine(Dir, QuarantineStore.FilesDir, stored);
        if (!Programs.SystemServices.StopAndDelete(serviceName, file))
        {
            try { File.Delete(file); } catch { }
            return false;
        }
        Record(serviceName, stored, file, ruleId, QuarantineEntry.KindService);
        return true;
    }

    /// <summary>Задача Планировщика: XML → файл, затем удаление задачи.</summary>
    public bool MoveScheduledTask(string taskPath, string ruleId)
    {
        var stored = (++_counter).ToString("D6") + ".xml";
        var file = Path.Combine(Dir, QuarantineStore.FilesDir, stored);
        if (!Programs.ScheduledTasks.ExportAndDelete(taskPath, file)) return false;
        Record(taskPath, stored, file, ruleId, QuarantineEntry.KindTask);
        return true;
    }

    /// <summary>Значение автозагрузки: тип и данные → JSON, затем удаление значения.</summary>
    public RegistryMoveOutcome MoveRegistryValue(string keyPath, Microsoft.Win32.RegistryView view, string name, string ruleId)
    {
        var stored = (++_counter).ToString("D6") + ".json";
        var file = Path.Combine(Dir, QuarantineStore.FilesDir, stored);
        var outcome = RegistryBackup.SaveValueAndDelete(keyPath, view, name, file);
        if (outcome != RegistryMoveOutcome.Moved) { try { File.Delete(file); } catch { } return outcome; }
        Record(keyPath + "|" + name, stored, file, ruleId, QuarantineEntry.KindRegValue);
        return outcome;
    }

    void Record(string original, string stored, string file, string ruleId, string kind)
    {
        long size = 0;
        try { size = new FileInfo(file).Length; } catch { }
        _manifest.WriteLine(JsonSerializer.Serialize(new QuarantineEntry(original, stored, size, ruleId, kind)));
        MovedCount++;
    }

    public void Dispose()
    {
        _manifest.Dispose();
        // Если ничего не перенесли — пустая партия не нужна.
        if (MovedCount == 0) try { Directory.Delete(Dir, recursive: true); } catch { }
        // Перенесённое процессом с правами администратора (из Program Files, C:\Windows\Temp) сохраняет права доступа
        // системной папки — потом его не удалить ни кнопкой без прав администратора, ни автоочисткой через 7 дней.
        // Сбрасываем права партии до унаследованных от папки профиля — в любом повышенном процессе, каким бы путём он ни шёл.
        else if (IsElevated) QuarantineStore.ResetPermissions(Dir);
    }

    static bool IsElevated
    {
        get
        {
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }
}
