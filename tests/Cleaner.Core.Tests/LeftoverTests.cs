using Cleaner.Core.Cleaning;
using Cleaner.Core.Programs;
using Microsoft.Win32;

namespace Cleaner.Core.Tests;

/// <summary>Поиск и удаление хвостов — только в песочнице (временная папка и временный ключ HKCU\Software\CleanerTest-…).</summary>
public class LeftoverTests
{
    static InstalledProgram Prog(string name, string? publisher = null, string? location = null) => new()
    {
        Name = name, Publisher = publisher, InstallLocation = location,
        UninstallString = "u.exe", Scope = ProgramScope.Machine64, RegistryKeyName = name,
    };

    [Theory]
    [InlineData("VLC media player 3.0.23 (64-bit)", "VLC media player")]
    [InlineData("WinRAR 5.91 (64-разрядная)", "WinRAR")]
    [InlineData("Lightshot-5.5.0.7", "Lightshot")]
    [InlineData("Happ - Proxy Utility 2.17.1(566)", "Happ - Proxy Utility")]
    [InlineData("Telegram Desktop", "Telegram Desktop")]
    public void Strips_versions_from_names(string name, string expected) => Assert.Equal(expected, LeftoverScanner.StripVersion(name));

    [Fact]
    public void Generic_words_are_never_used_as_names()
    {
        var (names, pubs) = LeftoverScanner.Tokens(Prog("Update", "Microsoft Corporation"));
        Assert.Empty(names);
        Assert.Empty(pubs);
    }

    [Fact]
    public void Finds_install_folder_empty_publisher_folder_and_appdata_by_exact_name()
    {
        using var box = new Sandbox();
        var pf = Path.Combine(box.Root, "ProgramFiles");
        var appdata = Path.Combine(box.Root, "AppData");
        box.File(@"ProgramFiles\VideoLAN\VLC\vlc.exe", 10);
        Directory.CreateDirectory(Path.Combine(appdata, "vlc"));
        box.File(@"AppData\vlc\vlcrc", 5);
        box.File(@"AppData\VLCsomething\x.txt", 5);             // похожее, но не точное имя — не трогаем
        box.File(@"AppData\Other\x.txt", 5);

        var scanner = new LeftoverScanner([pf, appdata], [], []);
        var vlc = Prog("VLC media player 3.0.23", "VideoLAN", Path.Combine(pf, "VideoLAN", "VLC"));
        var found = scanner.Scan(vlc, []);

        Assert.Contains(found, f => f.Path == Path.Combine(pf, "VideoLAN") && f.Confidence == LeftoverConfidence.High);  // вложенная VLC схлопнута
        Assert.Contains(found, f => f.Path == Path.Combine(appdata, "vlc"));
        Assert.DoesNotContain(found, f => f.Path.Contains("VLCsomething") || f.Path.Contains("Other"));
        Assert.DoesNotContain(found, f => f.Path == pf || f.Path == appdata);
    }

    [Fact]
    public void Shared_publisher_folder_is_kept_only_program_subfolder_is_offered()
    {
        using var box = new Sandbox();
        var root = Path.Combine(box.Root, "Data");
        box.File(@"Data\Acme\Tool\settings.ini", 5);
        box.File(@"Data\Acme\OtherTool\settings.ini", 5);

        var scanner = new LeftoverScanner([root], [], []);
        var removed = Prog("Tool", "Acme");
        var other = Prog("OtherTool", "Acme");
        var found = scanner.Scan(removed, [other]);

        var f = Assert.Single(found);
        Assert.Equal(Path.Combine(root, "Acme", "Tool"), f.Path);
    }

    [Fact]
    public void Folder_holding_another_installed_program_is_never_offered()
    {
        using var box = new Sandbox();
        var root = Path.Combine(box.Root, "PF");
        box.File(@"PF\Suite\Editor\editor.exe", 5);
        box.File(@"PF\Suite\Viewer\viewer.exe", 5);

        var scanner = new LeftoverScanner([root], [], []);
        var removed = Prog("Suite", "Suite Inc", Path.Combine(root, "Suite"));
        var viewer = Prog("Viewer", "Someone", Path.Combine(root, "Suite", "Viewer"));
        var found = scanner.Scan(removed, [viewer]);

        Assert.DoesNotContain(found, f => f.Path == Path.Combine(root, "Suite"));
    }

    [Fact]
    public void Name_shared_with_installed_program_is_not_used()
    {
        using var box = new Sandbox();
        var root = Path.Combine(box.Root, "Local");
        box.File(@"Local\Python\Python312\python.exe", 5);

        var scanner = new LeftoverScanner([root], [], []);
        var found = scanner.Scan(Prog("Python 3.13.2 (64-bit)"), [Prog("Python 3.12.1 (64-bit)")]);

        Assert.Empty(found);
    }

    [Fact]
    public void Name_that_starts_another_installed_program_name_is_only_medium()
    {
        using var box = new Sandbox();
        var root = Path.Combine(box.Root, "Local");
        box.File(@"Local\Python\Python313\python.exe", 5);

        var found = new LeftoverScanner([root], [], []).Scan(Prog("Python 3.13.2 (64-bit)"), [Prog("Python Launcher")]);

        var f = Assert.Single(found);
        Assert.Equal(LeftoverConfidence.Medium, f.Confidence);
    }

    [Fact]
    public void Shortcut_with_program_name_is_found()
    {
        using var box = new Sandbox();
        var desk = Path.Combine(box.Root, "Desktop");
        var lnk = box.File(@"Desktop\VLC media player.lnk", 3);
        box.File(@"Desktop\Chrome.lnk", 3);

        var found = new LeftoverScanner([], [], [desk]).Scan(Prog("VLC media player 3.0.23"), []);

        var f = Assert.Single(found);
        Assert.Equal(lnk, f.Path);
        Assert.Equal(LeftoverKind.Shortcut, f.Kind);
    }

    [Theory]
    [InlineData(@"HKCU\Software\VideoLAN", true)]
    [InlineData(@"HKLM\SOFTWARE\VideoLAN\VLC", true)]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows", false)]
    [InlineData(@"HKCU\Software\Classes\VLC.mp4", false)]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet", false)]
    [InlineData(@"HKCU\Software", false)]
    [InlineData(@"HKCU\Software\A\B\C", false)]
    public void Only_software_subkeys_can_be_removed(string path, bool ok) => Assert.Equal(ok, LeftoverPlan.IsSoftwareSubkey(path));

    [Fact]
    public void Folder_and_registry_key_go_to_quarantine_and_come_back()
    {
        using var box = new Sandbox();
        var root = Path.Combine(box.Root, "AppData");
        var settings = box.File(@"AppData\Tool\conf\settings.ini", 5);
        File.WriteAllText(settings, "theme=dark");
        Directory.CreateDirectory(Path.Combine(root, "Tool", "emptysub"));

        var testRoot = "CleanerTest-" + Guid.NewGuid().ToString("N")[..8];
        using (var k = Registry.CurrentUser.CreateSubKey($@"Software\{testRoot}\Tool"))
        {
            k.SetValue("Volume", 42);
            using var sub = k.CreateSubKey("Recent");
            sub.SetValue("Last", "C:\\a.mp4");
        }

        try
        {
            var scanner = new LeftoverScanner([root], [new(RegistryHive.CurrentUser, RegistryView.Default, $@"Software\{testRoot}")], []);
            var found = scanner.Scan(Prog("Tool"), []);
            Assert.Contains(found, f => f.Kind == LeftoverKind.Folder && f.Path == Path.Combine(root, "Tool"));
            Assert.Contains(found, f => f.Kind == LeftoverKind.RegistryKey && f.Path == $@"HKCU\Software\{testRoot}\Tool");

            var store = new QuarantineStore(Path.Combine(box.Root, "Quarantine"), new FakeSystem());
            var report = LeftoverRemover.Remove(store, found, isElevated: false, "Tool");

            Assert.Equal(2, report.Removed);
            Assert.False(Directory.Exists(Path.Combine(root, "Tool")));
            Assert.Null(Registry.CurrentUser.OpenSubKey($@"Software\{testRoot}\Tool"));

            var batch = Assert.Single(store.List());
            var restore = store.Restore(batch);

            Assert.Equal(0, restore.Failed);
            Assert.Equal("theme=dark", File.ReadAllText(settings));
            Assert.True(Directory.Exists(Path.Combine(root, "Tool", "emptysub")));
            using var back = Registry.CurrentUser.OpenSubKey($@"Software\{testRoot}\Tool");
            Assert.NotNull(back);
            Assert.Equal(42, back!.GetValue("Volume"));
            using var backSub = back.OpenSubKey("Recent");
            Assert.Equal("C:\\a.mp4", backSub!.GetValue("Last"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\{testRoot}", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Admin_items_are_not_touched_without_elevation()
    {
        using var box = new Sandbox();
        var dir = Path.Combine(box.Root, "X");
        box.File(@"X\a.txt", 5);
        var store = new QuarantineStore(Path.Combine(box.Root, "Quarantine"), new FakeSystem());

        var r = LeftoverRemover.Remove(store, [new Leftover(LeftoverKind.Folder, dir, LeftoverConfidence.High, "t", NeedsAdmin: true)], isElevated: false, "X");

        Assert.Equal(0, r.Removed);
        Assert.Single(r.NeedAdmin);
        Assert.True(Directory.Exists(dir));
    }
}
