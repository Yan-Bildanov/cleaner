using Cleaner.Core.Cleaning;

namespace Cleaner.Core.Tests;

/// <summary>«Кто держит файл» — через Restart Manager. Держателем выступает сам тестовый процесс.</summary>
public class FileLocksTests
{
    [Fact]
    public void Free_file_has_no_holders()
    {
        using var box = new Sandbox();
        var f = box.File(@"free.bin", 10);
        Assert.Empty(FileLocks.WhoUses(f));
    }

    [Fact]
    public void Open_file_reports_the_process_that_holds_it()
    {
        using var box = new Sandbox();
        var f = box.File(@"locked.bin", 10);
        using var hold = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.NotEmpty(FileLocks.WhoUses(f));
    }

    [Fact]
    public void Quarantine_delete_names_who_holds_a_file_instead_of_blaming_permissions()
    {
        using var box = new Sandbox();
        var store = new QuarantineStore(Path.Combine(box.Root, "Q"), new FakeSystem());
        var src = box.File(@"data\x.bin", 10);
        using (var batch = store.Begin()) batch.Move(src, 10, "t");
        var b = Assert.Single(store.List());
        var stored = Path.Combine(b.Dir, "files", b.Entries[0].Stored);

        DeleteReport r;
        using (new FileStream(stored, FileMode.Open, FileAccess.Read, FileShare.None))
            r = store.Delete(b);

        Assert.Equal(1, r.InUse);
        Assert.Equal(0, r.Denied);
        Assert.NotEmpty(r.Holders!);

        Assert.Equal(0, store.Delete(Assert.Single(store.List())).InUse);     // отпустили — удаляется
        Assert.Empty(store.List());
    }
}
