using Cleaner.Core.Cleaning;
using Cleaner.Core.Rules;
using Cleaner.Core.Scanning;

namespace Cleaner.Core.Tests;

public class CleanerTests
{
    static CleanRule Rule(string path) => new() { Id = "t.cache", Group = "g", Name = "n", Paths = [path] };

    static (Scanner, FileCleaner, QuarantineStore, FakeSystem) Setup(Sandbox box)
    {
        var sys = new FakeSystem();
        var store = new QuarantineStore(Path.Combine(box.Root, "Quarantine"), sys);
        return (new Scanner(sys), new FileCleaner(store, sys), store, sys);
    }

    [Fact]
    public void Clean_moves_files_into_quarantine_and_restore_puts_them_back()
    {
        using var box = new Sandbox();
        var (scanner, cleaner, store, _) = Setup(box);
        var a = box.File(@"cache\a.bin", 100);
        var b = box.File(@"cache\deep\a.bin", 50);                 // то же имя в другой папке
        File.WriteAllText(a, "hello");

        var report = cleaner.Clean([scanner.Scan(Rule(Path.Combine(box.Root, "cache")))], emptyRecycleBin: false);

        Assert.Equal(2, report.Moved);
        Assert.False(File.Exists(a) || File.Exists(b));
        var batch = Assert.Single(store.List());
        Assert.Equal(2, batch.Entries.Count);

        var restore = store.Restore(batch);

        Assert.Equal(2, restore.Restored);
        Assert.Equal("hello", File.ReadAllText(a));
        Assert.True(File.Exists(b));
        Assert.Empty(store.List());                                  // партия после полного возврата убрана
    }

    [Fact]
    public void Locked_file_is_skipped_and_stays_in_place()
    {
        using var box = new Sandbox();
        var (scanner, cleaner, _, _) = Setup(box);
        var locked = box.File(@"cache\locked.bin", 10);
        box.File(@"cache\free.bin", 20);
        var scan = scanner.Scan(Rule(Path.Combine(box.Root, "cache")));

        CleanReport report;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            report = cleaner.Clean([scan], emptyRecycleBin: false);

        Assert.Equal(1, report.Moved);
        Assert.Equal(1, report.SkippedLocked);
        Assert.True(File.Exists(locked));
    }

    [Fact]
    public void File_open_with_share_delete_like_chrome_cache_is_still_skipped()
    {
        using var box = new Sandbox();
        var (scanner, cleaner, _, _) = Setup(box);
        var open = box.File(@"cache\data_1", 10);
        var scan = scanner.Scan(Rule(Path.Combine(box.Root, "cache")));

        CleanReport report;
        // Так Chrome держит свои файлы кэша: Windows разрешила бы их переместить, но мы не должны.
        using (new FileStream(open, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            report = cleaner.Clean([scan], emptyRecycleBin: false);

        Assert.Equal(0, report.Moved);
        Assert.Equal(1, report.SkippedLocked);
        Assert.True(File.Exists(open));
    }

    [Fact]
    public void Restore_never_overwrites_a_new_file_at_the_original_path()
    {
        using var box = new Sandbox();
        var (scanner, cleaner, store, _) = Setup(box);
        var a = box.File(@"cache\a.bin", 10);
        cleaner.Clean([scanner.Scan(Rule(Path.Combine(box.Root, "cache")))], emptyRecycleBin: false);
        File.WriteAllText(a, "new");                                  // приложение создало файл заново

        var r = store.Restore(store.List()[0]);

        Assert.Equal(1, r.SkippedExisting);
        Assert.Equal("new", File.ReadAllText(a));
        Assert.Single(store.List());                                  // старая копия осталась в карантине
    }

    [Fact]
    public void Purge_removes_only_batches_older_than_7_days()
    {
        using var box = new Sandbox();
        var (scanner, cleaner, store, sys) = Setup(box);
        var start = DateTime.UtcNow;

        sys.UtcNow = start.AddDays(-8);
        box.File(@"c1\old.bin", 10);
        cleaner.Clean([scanner.Scan(Rule(Path.Combine(box.Root, "c1")))], false);

        sys.UtcNow = start.AddDays(-2);
        box.File(@"c2\recent.bin", 10);
        cleaner.Clean([scanner.Scan(Rule(Path.Combine(box.Root, "c2")))], false);

        sys.UtcNow = start;
        Assert.Equal(1, store.PurgeExpired());
        Assert.Single(store.List());
    }

    [Fact]
    public void Recycle_bin_is_emptied_only_with_explicit_consent()
    {
        using var box = new Sandbox();
        var (scanner, cleaner, _, sys) = Setup(box);
        sys.Bin = (1000, 2);
        var binScan = scanner.Scan(new CleanRule { Id = "bin", Group = "g", Name = "n", Kind = RuleKind.RecycleBin });

        var noConsent = cleaner.Clean([binScan], emptyRecycleBin: false);
        Assert.Equal(0, sys.EmptyCalls);
        Assert.False(noConsent.RecycleBinEmptied);

        var withConsent = cleaner.Clean([binScan], emptyRecycleBin: true);
        Assert.Equal(1, sys.EmptyCalls);
        Assert.True(withConsent.RecycleBinEmptied);
    }

    [Fact]
    public void Nothing_moved_means_no_empty_batch_left_behind()
    {
        using var box = new Sandbox();
        var (scanner, cleaner, store, _) = Setup(box);
        var report = cleaner.Clean([scanner.Scan(Rule(Path.Combine(box.Root, "empty")))], false);
        Assert.Null(report.BatchId);
        Assert.Empty(store.List());
    }
}
