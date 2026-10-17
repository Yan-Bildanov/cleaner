using Cleaner.Core.Programs;
using Xunit.Abstractions;

namespace Cleaner.Core.Tests;

/// <summary>
/// Список программ реальной системы — ТОЛЬКО ЧТЕНИЕ, ничего не запускает и не удаляет.
/// Запуск: dotnet test --filter Category=RealSystem --logger "console;verbosity=detailed"
/// </summary>
public class RealSystemProgramsTests(ITestOutputHelper output)
{
    [Fact, Trait("Category", "RealSystem")]
    public void List_installed_programs_read_only()
    {
        var programs = ProgramInventory.ReadAll();
        foreach (var p in programs)
        {
            var cmd = UninstallCommand.For(p);
            output.WriteLine($"{p.Name,-55} | {p.Version,-16} | {p.InstallDate:yyyy-MM-dd} | " +
                             $"{(p.SizeBytes is long b ? Format.Bytes(b) : "—"),9} | {p.Scope,-9} | " +
                             $"{(p.CanUninstall ? "" : "NO-REMOVE ")}{cmd?.FileName} {cmd?.Arguments}");
        }
        output.WriteLine($"TOTAL: {programs.Count}");
        Assert.NotEmpty(programs);
    }

    /// <summary>
    /// «Что нашлось бы, если удалить программу X» — для каждой установленной программы. ТОЛЬКО ЧТЕНИЕ.
    /// Пока программа установлена, её папки — не хвосты; смысл прогона — увидеть ложные срабатывания на чужое.
    /// </summary>
    [Fact, Trait("Category", "RealSystem")]
    public void Leftover_scan_for_every_program_read_only()
    {
        var programs = ProgramInventory.ReadAll();
        var scanner = LeftoverScanner.ForCurrentSystem();
        foreach (var p in programs)
        {
            var others = programs.Where(o => !ReferenceEquals(o, p)).ToList();
            var found = scanner.Scan(p, others);
            if (found.Count == 0) continue;
            output.WriteLine($"## {p.Name}");
            foreach (var f in found)
                output.WriteLine($"   [{f.Confidence,-6}] {f.Kind,-11} {f.Path}  ({Format.Bytes(f.Size)}, {f.FileCount} files){(f.NeedsAdmin ? " admin" : "")} — {f.Reason}");
        }
    }
}
