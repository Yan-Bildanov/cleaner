using System.Diagnostics;
using Cleaner.Core.Health;
using Cleaner.Core.Platform;
using Xunit.Abstractions;

namespace Cleaner.Core.Tests;

/// <summary>Проверка здоровья и железа на реальной системе — ТОЛЬКО ЧТЕНИЕ. dotnet test --filter Category=RealSystem</summary>
public class RealSystemHealthTests(ITestOutputHelper output)
{
    [Fact, Trait("Category", "RealSystem")]
    public void Health_checks_read_only()
    {
        HealthCollector.OnError = e => output.WriteLine("ERROR " + e);
        var sw = Stopwatch.StartNew();
        foreach (var c in new HealthCollector(new WindowsSystemInfo()).RunAll())
        {
            output.WriteLine($"[{c.Status}] {c.Title}: {c.Value}");
            output.WriteLine($"    {c.Hint}");
            foreach (var d in c.Details ?? []) output.WriteLine($"      - {d}");
        }
        output.WriteLine($"health took {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        var hw = HardwareCollector.Read();
        output.WriteLine($"HW Model: {hw.Model}\nHW OS: {hw.Os}\nHW CPU: {hw.Cpu}\nHW RAM: {hw.Ram}\nHW BIOS: {hw.Bios}");
        foreach (var x in hw.RamModules.Concat(hw.Gpus).Concat(hw.Disks)) output.WriteLine($"HW   {x}");
        output.WriteLine($"hardware took {sw.ElapsedMilliseconds} ms");
    }
}
