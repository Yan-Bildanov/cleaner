using Cleaner.Core.Cleaning;
using Cleaner.Core.Programs;
using Microsoft.Win32;

namespace Cleaner.Core.Tests;

/// <summary>Монитор установок — только в песочнице: временные папки и временный ключ HKCU\Software\CleanerTest-….</summary>
public class InstallMonitorTests
{
    static SnapshotScope Scope(string folder, string regRoot) => new()
    {
        FolderRoots = [folder],
        RegistryRoots = [new(RegistryHive.CurrentUser, RegistryView.Default, regRoot)],
    };

    [Fact]
    public void Diff_finds_new_folders_files_shortcuts_and_keys_and_collapses_nested()
    {
        using var box = new Sandbox();
        var root = Path.Combine(box.Root, "PF");
        box.File(@"PF\Existing\old.dll", 5);
        var reg = @"Software\CleanerTest-" + Guid.NewGuid().ToString("N")[..8];
        Registry.CurrentUser.CreateSubKey(reg + @"\ExistingVendor\Old")!.Dispose();
        try
        {
            var scope = Scope(root, reg);
            var before = InstallMonitor.Capture(scope);

            box.File(@"PF\NewApp\bin\app.exe", 5);                         // новая папка (вложенное схлопнется)
            box.File(@"PF\Existing\plugin\p.dll", 5);                      // новая подпапка в старой папке
            box.File(@"PF\NewApp.lnk", 1);                                 // ярлык
            box.File(@"PF\readme.txt", 1);                                 // файл
            Registry.CurrentUser.CreateSubKey(reg + @"\NewVendor\NewApp\Settings")!.Dispose();
            Registry.CurrentUser.CreateSubKey(reg + @"\ExistingVendor\NewApp")!.Dispose();

            var after = InstallMonitor.Capture(scope);
            var trace = InstallMonitor.Diff(before, after, []);

            Assert.Contains(trace.Items, i => i.Kind == LeftoverKind.Folder && i.Path == Path.Combine(root, "NewApp"));
            Assert.DoesNotContain(trace.Items, i => i.Path.Contains(@"NewApp\bin"));
            Assert.Contains(trace.Items, i => i.Kind == LeftoverKind.Folder && i.Path == Path.Combine(root, "Existing", "plugin"));
            Assert.Contains(trace.Items, i => i.Kind == LeftoverKind.Shortcut && i.Path.EndsWith("NewApp.lnk"));
            Assert.Contains(trace.Items, i => i.Kind == LeftoverKind.File && i.Path.EndsWith("readme.txt"));
            Assert.Contains(trace.Items, i => i.Kind == LeftoverKind.RegistryKey && i.Path == @"HKCU\" + reg + @"\NewVendor");
            Assert.DoesNotContain(trace.Items, i => i.Path.EndsWith(@"NewVendor\NewApp"));
            Assert.Contains(trace.Items, i => i.Kind == LeftoverKind.RegistryKey && i.Path == @"HKCU\" + reg + @"\ExistingVendor\NewApp");
            Assert.DoesNotContain(trace.Items, i => i.Path.Contains("Existing\\old") || i.Path.EndsWith(@"ExistingVendor\Old"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(reg, throwOnMissingSubKey: false); }
    }

    [Fact]
    public void Noise_folders_are_not_recorded()
    {
        using var box = new Sandbox();
        var root = Path.Combine(box.Root, "Local");
        Directory.CreateDirectory(root);
        var before = InstallMonitor.Capture(new SnapshotScope { FolderRoots = [root], RegistryRoots = [] });
        box.File(@"Local\Temp\setup.tmp", 1);
        box.File(@"Local\Microsoft\x.dat", 1);
        var after = InstallMonitor.Capture(new SnapshotScope { FolderRoots = [root], RegistryRoots = [] });
        Assert.Empty(InstallMonitor.Diff(before, after, []).Items);
    }

    [Fact]
    public void Traced_items_become_confident_leftovers_but_never_another_programs_folder()
    {
        using var box = new Sandbox();
        var mine = box.File(@"PF\Mine\app.exe", 5);
        box.File(@"PF\Shared\other\other.exe", 5);
        var trace = new InstallTrace
        {
            Id = "t",
            Items =
            [
                new TraceItem(LeftoverKind.Folder, Path.Combine(box.Root, "PF", "Mine")),
                new TraceItem(LeftoverKind.Folder, Path.Combine(box.Root, "PF", "Shared")),     // внутри — чужая программа
                new TraceItem(LeftoverKind.Folder, Path.Combine(box.Root, "PF", "Gone")),       // уже нет
            ],
        };
        var other = new InstalledProgram { Name = "Other", UninstallString = "u.exe", Scope = ProgramScope.Machine64,
                                           RegistryKeyName = "Other", InstallLocation = Path.Combine(box.Root, "PF", "Shared", "other") };

        var found = InstallMonitor.LeftoversFrom(trace, [other]);

        var f = Assert.Single(found);
        Assert.Equal(Path.GetDirectoryName(mine), f.Path);
        Assert.True(f.FromTrace);
        Assert.Equal(LeftoverConfidence.High, f.Confidence);
    }

    [Fact]
    public void Trace_store_saves_finds_by_program_and_keeps_pending_snapshot()
    {
        using var box = new Sandbox();
        var store = new TraceStore(Path.Combine(box.Root, "traces"));
        var trace = new InstallTrace
        {
            Id = "20261017-120000",
            Programs = [new TracedProgram(ProgramScope.User, "AppKey", "App")],
            Items = [new TraceItem(LeftoverKind.Folder, @"C:\X\App")],
        };
        store.Save(trace);
        var p = new InstalledProgram { Name = "App", UninstallString = "u", Scope = ProgramScope.User, RegistryKeyName = "AppKey" };

        var back = store.FindFor(p);
        Assert.NotNull(back);
        Assert.Single(back!.Items);
        Assert.Null(store.FindFor(p with { Scope = ProgramScope.Machine64 }));

        store.SavePending(new InstallSnapshot { TakenUtc = DateTime.UtcNow, Paths = ["D|C:\\A"] });
        Assert.Equal(["D|C:\\A"], store.LoadPending()!.Paths);
        Assert.Single(store.All());                 // незаконченный снимок — не запись
        store.ClearPending();
        Assert.Null(store.LoadPending());

        store.Delete(back);
        Assert.Empty(store.All());
    }

    [Fact]
    public void Combine_prefers_first_and_collapses_nested()
    {
        var a = new Leftover(LeftoverKind.Folder, @"C:\PF\App", LeftoverConfidence.High, "trace", FromTrace: true);
        var b = new Leftover(LeftoverKind.Folder, @"C:\PF\App", LeftoverConfidence.Medium, "scan");
        var c = new Leftover(LeftoverKind.Folder, @"C:\PF\App\sub", LeftoverConfidence.High, "scan");
        var all = InstallMonitor.Combine([a], [b, c]);
        var only = Assert.Single(all);
        Assert.True(only.FromTrace);
    }

    // ── Правила для новых видов ───────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(@"HKCU\Software\Classes\.vlcplaylist", true)]
    [InlineData(@"HKLM\SOFTWARE\Classes\VLC.mp4", true)]
    [InlineData(@"HKCU\Software\Classes\CLSID", false)]
    [InlineData(@"HKCU\Software\Classes\.exe", false)]
    [InlineData(@"HKCU\Software\Classes\Directory", false)]
    [InlineData(@"HKCU\Software\Classes\VLC.mp4\shell", false)]
    [InlineData(@"HKCU\Software\Other\.x", false)]
    public void Only_top_level_non_system_classes_keys_from_trace(string path, bool ok) =>
        Assert.Equal(ok, LeftoverPlan.IsTracedClassesKey(path));

    [Fact]
    public void Classes_key_is_refused_unless_it_comes_from_a_trace()
    {
        using var box = new Sandbox();
        var store = new QuarantineStore(Path.Combine(box.Root, "Q"), new FakeSystem());
        var r = LeftoverRemover.Remove(store, [new Leftover(LeftoverKind.RegistryKey, @"HKCU\Software\Classes\.cleanertest-none",
                                                            LeftoverConfidence.High, "scan")], isElevated: false, "t");
        Assert.Equal(1, r.Denied);
    }

    [Theory]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", true)]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", true)]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer", false)]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services", false)]
    public void Autostart_values_only_in_run_keys(string path, bool ok) => Assert.Equal(ok, LeftoverPlan.IsAutostartKey(path));

    [Fact]
    public void Autostart_value_goes_to_quarantine_and_comes_back_with_its_data()
    {
        using var box = new Sandbox();
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        var name = "CleanerTest-" + Guid.NewGuid().ToString("N")[..8];
        using (var k = Registry.CurrentUser.CreateSubKey(run)) k.SetValue(name, "\"C:\\NoSuch\\app.exe\" --tray", RegistryValueKind.String);
        try
        {
            var store = new QuarantineStore(Path.Combine(box.Root, "Q"), new FakeSystem());
            var item = new Leftover(LeftoverKind.RegistryValue, @"HKCU\" + run, LeftoverConfidence.High, "t", ValueName: name, FromTrace: true);

            var r = LeftoverRemover.Remove(store, [item], isElevated: false, "t");
            Assert.Equal(1, r.Removed);
            Assert.False(RegistryBackup.ValueExists(@"HKCU\" + run, RegistryView.Default, name));

            Assert.Equal(1, store.Restore(Assert.Single(store.List())).Restored);
            using var back = Registry.CurrentUser.OpenSubKey(run);
            Assert.Equal("\"C:\\NoSuch\\app.exe\" --tray", back!.GetValue(name));
        }
        finally
        {
            using var k = Registry.CurrentUser.OpenSubKey(run, writable: true);
            k?.DeleteValue(name, throwOnMissingValue: false);
        }
    }
}
