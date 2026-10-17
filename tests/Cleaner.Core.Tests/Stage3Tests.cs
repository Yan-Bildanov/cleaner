using System.Diagnostics;
using Cleaner.Core.Cleaning;
using Cleaner.Core.Programs;
using Microsoft.Win32;

namespace Cleaner.Core.Tests;

/// <summary>Этап 3: тихое и принудительное удаление, службы, задачи. Всё — в песочнице или на временных объектах.</summary>
public class Stage3Tests
{
    static InstalledProgram Prog(string name, string? uninstall = null, string? quiet = null, string? msi = null, string? location = null) => new()
    {
        Name = name, UninstallString = uninstall, QuietUninstallString = quiet, MsiProductCode = msi, InstallLocation = location,
        Scope = ProgramScope.Machine64, RegistryKeyName = msi ?? name,
    };

    // ── Тихое удаление ───────────────────────────────────────────────────────────────────────
    [Fact]
    public void Quiet_msi_uses_basic_ui_without_restart()
    {
        var c = UninstallCommand.Quiet(Prog("A", msi: "{12345678-1234-1234-1234-123456789ABC}"))!;
        Assert.Equal("msiexec.exe", c.FileName);
        Assert.Contains("/x {12345678-1234-1234-1234-123456789ABC}", c.Arguments);
        Assert.Contains("/qb", c.Arguments);
        Assert.Contains("/norestart", c.Arguments);
    }

    [Fact]
    public void Quiet_prefers_programs_own_quiet_string()
    {
        var c = UninstallCommand.Quiet(Prog("A", "\"C:\\X\\u.exe\"", "\"C:\\X\\u.exe\" --silent"))!;
        Assert.Equal("--silent", c.Arguments);
    }

    [Fact]
    public void Quiet_inno_setup_gets_verysilent()
    {
        var c = UninstallCommand.Quiet(Prog("A", "\"C:\\Program Files\\A\\unins000.exe\""))!;
        Assert.Contains("/VERYSILENT", c.Arguments);
        Assert.Contains("/SUPPRESSMSGBOXES", c.Arguments);
    }

    [Fact]
    public void Quiet_nsis_is_recognized_by_file_content_and_unknown_gets_null()
    {
        using var box = new Sandbox();
        var nsis = box.File(@"A\uninstall.exe", 0);
        File.WriteAllText(nsis, "MZ....Nullsoft.NSIS.exehead....");
        var other = box.File(@"B\uninstall.exe", 0);
        File.WriteAllText(other, "MZ....some other installer....");

        Assert.Equal("/S", UninstallCommand.Quiet(Prog("A", $"\"{nsis}\""))!.Arguments);
        Assert.Null(UninstallCommand.Quiet(Prog("B", $"\"{other}\"")));
    }

    // ── Сломанный деинсталлятор ──────────────────────────────────────────────────────────────
    [Fact]
    public void Missing_uninstaller_file_is_broken_but_msiexec_is_not()
    {
        Assert.True(UninstallCommand.IsBroken(Prog("A", "\"C:\\NoSuchFolder-Cleaner\\unins000.exe\"")));
        Assert.False(UninstallCommand.IsBroken(Prog("B", "MsiExec.exe /X{12345678-1234-1234-1234-123456789ABC}")));
        Assert.False(UninstallCommand.IsBroken(Prog("C", msi: "{12345678-1234-1234-1234-123456789ABC}")));
        Assert.False(UninstallCommand.IsBroken(Prog("D", $"\"{Path.Combine(Environment.SystemDirectory, "cmd.exe")}\"")));
    }

    // ── Правила безопасности ──────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VLC media player", true)]
    [InlineData(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{GUID}", true)]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false)]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\A\B", false)]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false)]
    public void Uninstall_entry_is_the_only_allowed_key_under_microsoft(string path, bool ok) =>
        Assert.Equal(ok, LeftoverPlan.IsSoftwareSubkey(path));

    [Theory]
    [InlineData("Dnscache")]          // svchost — в папке Windows
    [InlineData("WinDefend")]
    [InlineData("Tcpip")]             // драйвер
    [InlineData("Cleaner-NoSuchService-xyz")]
    [InlineData(@"..\Tcpip")]
    public void System_services_and_drivers_are_never_removable(string name) =>
        Assert.False(SystemServices.IsSafeToRemove(name));

    [Theory]
    [InlineData(@"C:\ProgramData\Microsoft\Windows Defender\Platform\4.18\MsMpEng.exe", true)]
    [InlineData(@"C:\Program Files\Windows Defender Advanced Threat Protection\MsSense.exe", true)]
    [InlineData(@"C:\Program Files\Common Files\microsoft shared\ClickToRun\OfficeClickToRun.exe", true)]
    [InlineData(@"C:\Windows\System32\svchost.exe", true)]
    [InlineData(@"C:\Program Files (x86)\AnyDesk\AnyDesk.exe", false)]
    [InlineData(@"C:\Users\demo\AppData\Local\Programs\Microsoft VS Code\Code.exe", false)]
    public void Microsoft_and_windows_locations_are_system(string exe, bool system) =>
        Assert.Equal(system, SystemServices.IsSystemLocation(exe));

    [Fact]
    public void Unsafe_service_and_microsoft_task_are_refused_even_when_passed_in()
    {
        using var box = new Sandbox();
        var store = new QuarantineStore(Path.Combine(box.Root, "Quarantine"), new FakeSystem());
        var r = LeftoverRemover.Remove(store,
        [
            new Leftover(LeftoverKind.Service, "Dnscache", LeftoverConfidence.High, "t", NeedsAdmin: true),
            new Leftover(LeftoverKind.ScheduledTask, @"\Microsoft\Windows\Defrag\ScheduledDefrag", LeftoverConfidence.High, "t", NeedsAdmin: true),
            new Leftover(LeftoverKind.RegistryKey, @"HKLM\SYSTEM\CurrentControlSet", LeftoverConfidence.High, "t", NeedsAdmin: true),
        ], isElevated: true, "test");
        Assert.Equal(0, r.Removed);
        Assert.Equal(3, r.Denied);
    }

    // ── Службы и задачи в поиске хвостов ─────────────────────────────────────────────────────
    [Fact]
    public void Scanner_asks_for_services_and_tasks_inside_program_folders()
    {
        using var box = new Sandbox();
        var root = Path.Combine(box.Root, "PF");
        box.File(@"PF\Tool\tool.exe", 5);
        IReadOnlyCollection<string>? asked = null;
        var scanner = new LeftoverScanner([root], [], [], null, folders =>
        {
            asked = folders;
            return [new Leftover(LeftoverKind.Service, "ToolSvc", LeftoverConfidence.High, "svc", NeedsAdmin: true)];
        });

        var found = scanner.Scan(Prog("Tool", location: Path.Combine(root, "Tool")), []);

        Assert.NotNull(asked);
        Assert.Contains(Path.Combine(root, "Tool"), asked!);
        Assert.Contains(found, f => f.Kind == LeftoverKind.Service && f.Path == "ToolSvc");
    }

    [Fact]
    public void Forced_plan_contains_the_uninstall_entry()
    {
        var p = Prog("Ghost") with { Scope = ProgramScope.Machine32, RegistryKeyName = "Ghost" };
        var e = ForcedUninstall.UninstallEntry(p);
        Assert.Equal(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Ghost", e.Path);
        Assert.Equal(RegistryView.Registry32, e.View);
        Assert.True(e.NeedsAdmin);
    }

    [Fact]
    public void User_uninstall_entry_goes_to_quarantine_and_comes_back()
    {
        using var box = new Sandbox();
        var name = "CleanerTest-" + Guid.NewGuid().ToString("N")[..8];
        var keyPath = ProgramInventory.UninstallKey + "\\" + name;
        using (var k = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            k.SetValue("DisplayName", "Cleaner test ghost");
            k.SetValue("UninstallString", "\"C:\\NoSuchFolder-Cleaner\\unins000.exe\"");
        }
        try
        {
            var p = new InstalledProgram { Name = "Cleaner test ghost", UninstallString = "x", Scope = ProgramScope.User, RegistryKeyName = name };
            Assert.True(ProgramInventory.Exists(p));
            var store = new QuarantineStore(Path.Combine(box.Root, "Quarantine"), new FakeSystem());

            var r = LeftoverRemover.Remove(store, [ForcedUninstall.UninstallEntry(p)], isElevated: false, "ghost");

            Assert.Equal(1, r.Removed);
            Assert.False(ProgramInventory.Exists(p));
            store.Restore(Assert.Single(store.List()));
            Assert.True(ProgramInventory.Exists(p));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false); }
    }

    [Fact]
    public void Scheduled_task_round_trip_through_quarantine()
    {
        using var box = new Sandbox();
        var exe = box.File(@"App\updater.exe", 5);
        var task = @"\CleanerTest-" + Guid.NewGuid().ToString("N")[..8];
        Schtasks($"/create /tn \"{task}\" /tr \"\\\"{exe}\\\"\" /sc once /st 23:59 /f");
        try
        {
            Assert.True(ScheduledTasks.Exists(task));
            var found = ScheduledTasks.FindInside([Path.Combine(box.Root, "App")]);
            Assert.Contains(found, t => t.Path.Equals(task, StringComparison.OrdinalIgnoreCase));

            var store = new QuarantineStore(Path.Combine(box.Root, "Quarantine"), new FakeSystem());
            var r = LeftoverRemover.Remove(store, [new Leftover(LeftoverKind.ScheduledTask, task, LeftoverConfidence.High, "t")], isElevated: false, "t");
            Assert.Equal(1, r.Removed);
            Assert.False(ScheduledTasks.Exists(task));

            var restore = store.Restore(Assert.Single(store.List()));
            Assert.Equal(1, restore.Restored);
            Assert.True(ScheduledTasks.Exists(task));
        }
        finally { Schtasks($"/delete /tn \"{task}\" /f"); }
    }

    static void Schtasks(string args)
    {
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
    }
}
