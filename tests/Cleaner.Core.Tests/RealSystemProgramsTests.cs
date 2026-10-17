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

    [Fact, Trait("Category", "RealSystem")]
    public void List_browser_extensions_read_only()
    {
        foreach (var e in Cleaner.Core.Browsers.BrowserExtensions.ReadAll())
            output.WriteLine($"EXT {e.Browser.Name,-7} | {e.ProfileName,-14} | {e.Name,-35} | {e.Version,-12} | {(e.Enabled ? "on " : "off")} | {e.Source,-9} | {Format.Bytes(e.Size),8} | " +
                             $"{(e.IconPath is null ? "no-icon" : "icon")} | key={e.ExternalKey} | WARN: {string.Join("; ", e.Warnings)} | NOTE: {string.Join("; ", e.Notes)}");
        foreach (var b in Cleaner.Core.Browsers.BrowserDef.Known())
            output.WriteLine($"EXE {b.Name}: {Cleaner.Core.Browsers.BrowserExtensions.BrowserExe(b)}");
    }

    /// <summary>Два снимка подряд без установки: всё, что найдётся, — шум. ТОЛЬКО ЧТЕНИЕ.</summary>
    [Fact, Trait("Category", "RealSystem")]
    public void Monitor_snapshot_noise_without_install_read_only()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var scope = SnapshotScope.ForCurrentSystem();
        var a = InstallMonitor.Capture(scope);
        output.WriteLine($"SNAPSHOT: {sw.Elapsed.TotalSeconds:F1} s · paths {a.Paths.Count} · keys {a.Keys.Count} · services {a.Services.Count} · tasks {a.Tasks.Count} · run {a.RunValues.Count} · programs {a.Uninstall.Count}");
        Thread.Sleep(20_000);
        var b = InstallMonitor.Capture(scope);
        var d = InstallMonitor.Diff(a, b);
        output.WriteLine($"NOISE: {d.Items.Count}");
        foreach (var i in d.Items) output.WriteLine($"NOISE {i.Kind} {i.Path} {i.ValueName}");
        var json = System.Text.Json.JsonSerializer.Serialize(a);
        output.WriteLine($"PENDING SIZE: {json.Length / 1024} KB");
    }

    [Fact, Trait("Category", "RealSystem")]
    public void List_store_apps_and_broken_programs_read_only()
    {
        foreach (var a in StoreApps.ReadAll())
            output.WriteLine($"STORE {a.Name,-45} | {a.Publisher,-25} | {a.Version,-14} | {a.InstallDate:yyyy-MM-dd} | {a.PackageFamilyName}");
        foreach (var p in ProgramInventory.ReadAll())
        {
            if (UninstallCommand.IsBroken(p)) output.WriteLine($"BROKEN {p.Name} | {p.UninstallString}");
            var q = UninstallCommand.Quiet(p);
            output.WriteLine($"QUIET  {p.Name,-50} | {(q is null ? "—" : q.FileName + " " + q.Arguments)}");
        }
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
