using System.Diagnostics;
using Cleaner.Core;
using Cleaner.Core.Platform;
using Cleaner.Core.Rules;
using Cleaner.Core.Scanning;
using Xunit.Abstractions;

namespace Cleaner.Core.Tests;

/// <summary>
/// Анализ реальной системы по всем правилам — ТОЛЬКО ЧТЕНИЕ, ничего не удаляет.
/// Запуск: dotnet test --filter Category=RealSystem --logger "console;verbosity=detailed"
/// </summary>
public class RealSystemScanTests(ITestOutputHelper output)
{
    [Fact, Trait("Category", "RealSystem")]
    public void Scan_all_builtin_rules_read_only()
    {
        var scanner = new Scanner(new WindowsSystemInfo());
        long total = 0;
        foreach (var rule in BuiltInRules.All)
        {
            var sw = Stopwatch.StartNew();
            var r = scanner.Scan(rule);
            total += r.TotalBytes;
            output.WriteLine($"{rule.Id,-15} {Format.Bytes(r.TotalBytes),10} {r.ItemCount,7} files  " +
                             $"recentSkipped={r.SkippedRecent} admin={r.NeedsAdmin} running=[{string.Join(",", r.RunningProcesses)}] {sw.ElapsedMilliseconds} ms");
        }
        output.WriteLine($"TOTAL (all rules) {Format.Bytes(total)}");
    }
}
