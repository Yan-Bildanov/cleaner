using Cleaner.Core.Platform;
using Cleaner.Core.Rules;
using Cleaner.Core.Scanning;

namespace Cleaner.Core.Cleaning;

public sealed record CleanReport(
    int Moved, long MovedBytes, int SkippedLocked, int SkippedMissing,
    bool RecycleBinEmptied, long RecycleBinBytes, string? BatchId);

/// <summary>
/// Очистка: переносит найденные анализом файлы в карантин. Занятые файлы пропускает.
/// Корзину очищает (безвозвратно) ТОЛЬКО если вызывающий явно передал emptyRecycleBin = true
/// после отдельного подтверждения пользователя.
/// </summary>
public sealed class FileCleaner(QuarantineStore store, ISystemInfo system)
{
    public CleanReport Clean(IEnumerable<RuleScanResult> results, bool emptyRecycleBin,
                             IProgress<int>? progress = null, CancellationToken ct = default)
    {
        int moved = 0, locked = 0, missing = 0, done = 0;
        long movedBytes = 0, binBytes = 0;
        var binEmptied = false;
        string? batchId = null;

        using (var batch = store.Begin())
        {
            foreach (var result in results)
            {
                if (result.Rule.Kind == RuleKind.RecycleBin)
                {
                    if (emptyRecycleBin && result.ItemCount > 0 && system.EmptyRecycleBin())
                    {
                        binEmptied = true;
                        binBytes = result.TotalBytes;
                    }
                    continue;
                }

                foreach (var file in result.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    switch (batch.Move(file.Path, file.Size, result.Rule.Id))
                    {
                        case MoveOutcome.Moved: moved++; movedBytes += file.Size; break;
                        case MoveOutcome.Locked: locked++; break;
                        default: missing++; break;
                    }
                    if (++done % 200 == 0) progress?.Report(done);
                }
            }
            if (batch.MovedCount > 0) batchId = batch.Id;
        }

        progress?.Report(done);
        return new CleanReport(moved, movedBytes, locked, missing, binEmptied, binBytes, batchId);
    }
}
