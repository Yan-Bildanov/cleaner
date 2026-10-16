using System.Text.Json;
using Cleaner.Core.Cleaning;
using Xunit.Abstractions;

namespace Cleaner.Core.Tests;

/// <summary>Диагностика реального карантина — только чтение. dotnet test --filter Category=Diag</summary>
public class DiagTests(ITestOutputHelper output)
{
    [Fact, Trait("Category", "Diag")]
    public void Read_real_quarantine()
    {
        var root = QuarantineStore.DefaultRoot;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            output.WriteLine($"dir {dir}");
            try { output.WriteLine("info: " + File.ReadAllText(Path.Combine(dir, "batch.json"))); }
            catch (Exception e) { output.WriteLine($"info FAIL {e.GetType().Name}: {e.Message}"); }
            var n = 0;
            try
            {
                foreach (var l in File.ReadLines(Path.Combine(dir, "manifest.jsonl")))
                {
                    n++;
                    if (l.Length == 0) continue;
                    try { JsonSerializer.Deserialize<QuarantineEntry>(l); }
                    catch (Exception e) { output.WriteLine($"line {n} FAIL {e.GetType().Name}: {e.Message} :: {l[..Math.Min(200, l.Length)]}"); break; }
                }
                output.WriteLine($"lines ok: {n}");
            }
            catch (Exception e) { output.WriteLine($"manifest FAIL {e.GetType().Name}: {e.Message}"); }
        }
        output.WriteLine($"List(): {new QuarantineStore(root, new Platform.WindowsSystemInfo()).List().Count}");
    }
}
