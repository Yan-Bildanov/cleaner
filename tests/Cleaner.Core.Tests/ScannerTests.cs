using Cleaner.Core;
using Cleaner.Core.Platform;
using Cleaner.Core.Rules;
using Cleaner.Core.Scanning;

namespace Cleaner.Core.Tests;

sealed class FakeSystem : ISystemInfo
{
    public DateTime UtcNow { get; set; } = DateTime.UtcNow;
    public bool IsElevated { get; set; }
    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);
    public (long, long) Bin { get; set; }
    public bool IsProcessRunning(string name) => Running.Contains(name);
    public (long Bytes, long Items) QueryRecycleBin() => Bin;
    public int EmptyCalls { get; private set; }
    public bool EmptyRecycleBin() { EmptyCalls++; return true; }   // реальную корзину в тестах не трогаем
}

/// <summary>Временная песочница: всё создаётся и удаляется только внутри неё.</summary>
sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "cleaner-tests", Guid.NewGuid().ToString("N"));
    public Sandbox() => Directory.CreateDirectory(Root);

    public string File(string relative, int bytes, DateTime? time = null)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        if (time is { } t) { System.IO.File.SetCreationTimeUtc(path, t); System.IO.File.SetLastWriteTimeUtc(path, t); }
        return path;
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
}

public class ScannerTests
{
    static CleanRule Rule(params string[] paths) =>
        new() { Id = "t", Group = "g", Name = "n", Paths = paths };

    [Fact]
    public void Finds_files_recursively_and_sums_sizes_without_touching_them()
    {
        using var box = new Sandbox();
        var a = box.File(@"cache\a.bin", 100);
        var b = box.File(@"cache\sub\b.bin", 250);

        var r = new Scanner(new FakeSystem()).Scan(Rule(Path.Combine(box.Root, "cache")));

        Assert.Equal(350, r.TotalBytes);
        Assert.Equal(2, r.ItemCount);
        Assert.True(File.Exists(a) && File.Exists(b));   // анализ ничего не удаляет
    }

    [Fact]
    public void Skips_files_younger_than_MinAge()
    {
        using var box = new Sandbox();
        var now = DateTime.UtcNow;
        box.File(@"tmp\old.tmp", 10, now.AddDays(-3));
        box.File(@"tmp\fresh.tmp", 20, now.AddHours(-2));

        var rule = Rule(Path.Combine(box.Root, "tmp")) with { MinAge = TimeSpan.FromDays(1) };
        var r = new Scanner(new FakeSystem { UtcNow = now }).Scan(rule);

        Assert.Equal(10, r.TotalBytes);
        Assert.Equal(1, r.SkippedRecent);
    }

    [Fact]
    public void Wildcard_segment_covers_all_browser_profiles()
    {
        using var box = new Sandbox();
        box.File(@"User Data\Default\Cache\x", 5);
        box.File(@"User Data\Profile 1\Cache\y", 7);
        box.File(@"User Data\Profile 1\Cookies", 1000);           // не кэш — не должен попасть

        var r = new Scanner(new FakeSystem()).Scan(Rule(Path.Combine(box.Root, @"User Data\*\Cache")));

        Assert.Equal(12, r.TotalBytes);
        Assert.DoesNotContain(r.Files, f => f.Path.EndsWith("Cookies"));
    }

    [Fact]
    public void FilePattern_limits_matched_files()
    {
        using var box = new Sandbox();
        box.File(@"Explorer\thumbcache_256.db", 30);
        box.File(@"Explorer\iconcache_16.db", 40);

        var rule = Rule(Path.Combine(box.Root, "Explorer")) with { FilePattern = "thumbcache_*.db" };
        var r = new Scanner(new FakeSystem()).Scan(rule);

        Assert.Equal(30, r.TotalBytes);
    }

    [Fact]
    public void Admin_rule_without_elevation_reports_NeedsAdmin_and_finds_nothing()
    {
        using var box = new Sandbox();
        box.File(@"sys\a", 10);
        var rule = Rule(Path.Combine(box.Root, "sys")) with { RequiresAdmin = true };

        var r = new Scanner(new FakeSystem { IsElevated = false }).Scan(rule);

        Assert.True(r.NeedsAdmin);
        Assert.Equal(0, r.TotalBytes);
    }

    [Fact]
    public void Missing_paths_and_undefined_variables_are_ignored()
    {
        var r = new Scanner(new FakeSystem()).Scan(Rule(@"C:\definitely\not\here", @"%NO_SUCH_VAR_123%\x"));
        Assert.Equal(0, r.TotalBytes);
    }

    [Fact]
    public void Reports_running_app_and_recycle_bin()
    {
        var sys = new FakeSystem { Bin = (5000, 3) };
        sys.Running.Add("chrome");
        var scanner = new Scanner(sys);

        Assert.Equal(["chrome"], scanner.Scan(Rule() with { ProcessNames = ["chrome", "msedge"] }).RunningProcesses);
        var bin = scanner.Scan(Rule() with { Kind = RuleKind.RecycleBin });
        Assert.Equal((5000L, 3L), (bin.TotalBytes, bin.ItemCount));
    }

    [Theory]
    [InlineData(0, "0 Б")]
    [InlineData(1536, "1,5 КБ")]
    [InlineData(734003200, "700 МБ")]
    [InlineData(1288490188, "1,2 ГБ")]
    public void Formats_bytes_in_russian(long bytes, string expected) =>
        Assert.Equal(expected, Format.Bytes(bytes));
}
