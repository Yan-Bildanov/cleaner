using Cleaner.Core.Cleaning;

namespace Cleaner.Core.Programs;

public sealed record LeftoverRemoveReport(
    int Removed, long RemovedBytes, int Locked, int Denied, string? BatchId, IReadOnlyList<Leftover> NeedAdmin);

/// <summary>
/// Переносит выбранные хвосты в карантин (7 дней, можно вернуть): файлы — переносом, папки — запоминаются и
/// удаляются, когда опустеют, ключи реестра — экспорт в .reg, затем удаление.
/// Пункты, которым нужны права администратора, без этих прав не трогает — возвращает их в NeedAdmin.
/// </summary>
public static class LeftoverRemover
{
    /// <summary>Порядок: сначала то, что запускает программу (задачи, службы), потом её процессы, потом файлы и реестр.</summary>
    static int Order(LeftoverKind k) => k switch
    {
        LeftoverKind.ScheduledTask => 0,
        LeftoverKind.Service => 1,
        LeftoverKind.Shortcut => 2,
        LeftoverKind.Folder => 3,
        _ => 4,
    };

    /// <param name="killProcesses">Принудительное удаление: закрыть процессы, запущенные из удаляемых папок.</param>
    public static LeftoverRemoveReport Remove(QuarantineStore store, IEnumerable<Leftover> items, bool isElevated, string label,
                                              bool killProcesses = false)
    {
        int removed = 0, locked = 0, denied = 0;
        long bytes = 0;
        var needAdmin = new List<Leftover>();
        string? batchId = null;
        var ruleId = "Хвосты: " + label;
        var killed = false;

        var ordered = items.OrderBy(i => Order(i.Kind)).ToList();
        var folders = ordered.Where(i => i.Kind == LeftoverKind.Folder && (!i.NeedsAdmin || isElevated)).Select(i => i.Path).ToList();

        using (var batch = store.Begin())
        {
            foreach (var item in ordered)
            {
                if (item.NeedsAdmin && !isElevated) { needAdmin.Add(item); continue; }

                // Последняя проверка перед действием: никаких системных веток и общих папок, что бы ни пришло на вход.
                if (item.Kind == LeftoverKind.RegistryKey && !LeftoverPlan.IsSoftwareSubkey(item.Path)) { denied++; continue; }
                if (item.Kind == LeftoverKind.Folder && Directory.Exists(item.Path) && !ProgramInventory.IsProgramOwnFolder(item.Path)) { denied++; continue; }
                if (item.Kind == LeftoverKind.Service && !SystemServices.IsSafeToRemove(item.Path)) { denied++; continue; }
                if (item.Kind == LeftoverKind.ScheduledTask && item.Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) { denied++; continue; }

                if (killProcesses && !killed && Order(item.Kind) >= 2)
                {
                    ProgramProcesses.KillInside(folders);
                    killed = true;
                }

                switch (item.Kind)
                {
                    case LeftoverKind.ScheduledTask:
                        if (batch.MoveScheduledTask(item.Path, ruleId)) removed++; else denied++;
                        break;

                    case LeftoverKind.Service:
                        if (batch.MoveService(item.Path, ruleId)) removed++; else denied++;
                        break;

                    case LeftoverKind.Shortcut:
                        switch (batch.Move(item.Path, item.Size, ruleId))
                        {
                            case MoveOutcome.Moved: removed++; bytes += item.Size; break;
                            case MoveOutcome.Locked: locked++; break;
                        }
                        break;

                    case LeftoverKind.Folder:
                        var (ok, movedBytes, lockedFiles) = MoveFolder(batch, item.Path, ruleId);
                        bytes += movedBytes;
                        if (ok) removed++;
                        else if (lockedFiles > 0) locked++;
                        else denied++;
                        break;

                    case LeftoverKind.RegistryKey:
                        switch (batch.MoveRegistryKey(item.Path, item.View, ruleId))
                        {
                            case RegistryMoveOutcome.Moved: removed++; break;
                            case RegistryMoveOutcome.Missing: break;
                            default: denied++; break;
                        }
                        break;
                }
            }
            if (batch.MovedCount > 0) batchId = batch.Id;
        }
        return new LeftoverRemoveReport(removed, bytes, locked, denied, batchId, needAdmin);
    }

    /// <summary>Файлы папки — в карантин, затем пустые папки снизу вверх удаляются и запоминаются для возврата.</summary>
    static (bool FullyRemoved, long Bytes, int Locked) MoveFolder(BatchWriter batch, string root, string ruleId)
    {
        if (!Directory.Exists(root)) return (true, 0, 0);
        long bytes = 0; var locked = 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };

        List<FileInfo> files;
        try { files = new DirectoryInfo(root).EnumerateFiles("*", opts).ToList(); }
        catch (UnauthorizedAccessException) { return (false, 0, 0); }

        foreach (var f in files)
        {
            long size = 0;
            try { size = f.Length; if (f.IsReadOnly) f.IsReadOnly = false; } catch { }
            switch (batch.Move(f.FullName, size, ruleId))
            {
                case MoveOutcome.Moved: bytes += size; break;
                case MoveOutcome.Locked: locked++; break;
            }
        }

        // Папки — от самых глубоких к корню. Ссылки (junction) не обходим и не удаляем.
        var dirs = new List<string>();
        try { dirs = Directory.EnumerateDirectories(root, "*", opts).ToList(); } catch { }
        dirs.Add(root);
        foreach (var d in dirs.OrderByDescending(x => x.Length))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(d).Any()) continue;
                Directory.Delete(d);
                batch.RecordDirectory(d, ruleId);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return (!Directory.Exists(root), bytes, locked);
    }
}
