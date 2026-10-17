using Cleaner.Core.Programs;

namespace Cleaner.Core.Tests;

public class ProgramsTests
{
    // ── Разбор UninstallString ────────────────────────────────────────────────
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\unins000.exe\" /SILENT", "C:\\Program Files\\App\\unins000.exe", "/SILENT")]
    [InlineData("\"C:\\Program Files\\App\\uninstall.exe\"", "C:\\Program Files\\App\\uninstall.exe", "")]
    [InlineData("C:\\Program Files\\My App\\uninstall.exe /S", "C:\\Program Files\\My App\\uninstall.exe", "/S")]
    [InlineData("C:\\Program Files (x86)\\Foo\\Uninstall.EXE", "C:\\Program Files (x86)\\Foo\\Uninstall.EXE", "")]
    [InlineData("rundll32.exe dfshim.dll,ShArpMaintain app.application", "rundll32.exe", "dfshim.dll,ShArpMaintain app.application")]
    public void Parses_uninstall_strings(string raw, string file, string args)
    {
        var c = UninstallCommand.Parse(raw)!;
        Assert.Equal(file, c.FileName);
        Assert.Equal(args, c.Arguments);
    }

    [Theory]
    [InlineData("MsiExec.exe /I{12345678-1234-1234-1234-123456789ABC}", "/X{12345678-1234-1234-1234-123456789ABC}")]
    [InlineData("MsiExec.exe /i {12345678-1234-1234-1234-123456789ABC}", "/X{12345678-1234-1234-1234-123456789ABC}")]
    [InlineData("MsiExec.exe /X{12345678-1234-1234-1234-123456789ABC}", "/X{12345678-1234-1234-1234-123456789ABC}")]
    public void Msiexec_install_switch_becomes_uninstall(string raw, string args)
    {
        var c = UninstallCommand.Parse(raw)!;
        Assert.Equal("MsiExec.exe", c.FileName);
        Assert.Equal(args, c.Arguments);
    }

    [Fact]
    public void Msi_product_uses_msiexec_x_regardless_of_uninstall_string()
    {
        var p = FromValues("{12345678-1234-1234-1234-123456789ABC}", new()
        {
            ["DisplayName"] = "Some MSI App",
            ["WindowsInstaller"] = 1,
            ["UninstallString"] = "MsiExec.exe /I{12345678-1234-1234-1234-123456789ABC}",
        })!;
        var c = UninstallCommand.For(p)!;
        Assert.Equal("msiexec.exe", c.FileName);
        Assert.Equal("/x {12345678-1234-1234-1234-123456789ABC}", c.Arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_uninstall_string_is_null(string raw) => Assert.Null(UninstallCommand.Parse(raw));

    // ── Фильтр записей реестра ───────────────────────────────────────────────
    static InstalledProgram? FromValues(string key, Dictionary<string, object> values) =>
        ProgramInventory.FromValues(key, ProgramScope.Machine64, v => values.GetValueOrDefault(v));

    [Fact]
    public void Regular_program_is_read_with_all_fields()
    {
        var p = FromValues("App", new()
        {
            ["DisplayName"] = "  My App ",
            ["Publisher"] = "Acme",
            ["DisplayVersion"] = "1.2.3",
            ["InstallDate"] = "20240315",
            ["EstimatedSize"] = 2048,
            ["InstallLocation"] = "\"C:\\Program Files\\My App\\\"",
            ["UninstallString"] = "\"C:\\Program Files\\My App\\unins000.exe\"",
        })!;
        Assert.Equal("My App", p.Name);
        Assert.Equal("Acme", p.Publisher);
        Assert.Equal(new DateTime(2024, 3, 15), p.InstallDate);
        Assert.Equal(2048L * 1024, p.SizeBytes);
        Assert.Equal("C:\\Program Files\\My App", p.InstallLocation);
        Assert.True(p.CanUninstall);
        Assert.Null(p.MsiProductCode);
    }

    [Fact] public void No_name_is_skipped() => Assert.Null(FromValues("X", new() { ["UninstallString"] = "u.exe" }));
    [Fact] public void System_component_is_skipped() => Assert.Null(FromValues("X", new() { ["DisplayName"] = "A", ["UninstallString"] = "u.exe", ["SystemComponent"] = 1 }));
    [Fact] public void Update_with_parent_is_skipped() => Assert.Null(FromValues("X", new() { ["DisplayName"] = "KB1", ["UninstallString"] = "u.exe", ["ParentKeyName"] = "Office" }));
    [Fact] public void Hotfix_is_skipped() => Assert.Null(FromValues("X", new() { ["DisplayName"] = "Fix", ["UninstallString"] = "u.exe", ["ReleaseType"] = "Hotfix" }));
    [Fact] public void Without_any_uninstaller_is_skipped() => Assert.Null(FromValues("X", new() { ["DisplayName"] = "A" }));

    [Fact]
    public void NoRemove_program_is_listed_but_cannot_be_uninstalled()
    {
        var p = FromValues("X", new() { ["DisplayName"] = "A", ["UninstallString"] = "u.exe", ["NoRemove"] = 1 })!;
        Assert.False(p.CanUninstall);
    }

    [Theory]
    [InlineData("20231011", 2023, 10, 11)]
    [InlineData("2023-10-11", 2023, 10, 11)]
    public void Parses_install_date(string s, int y, int m, int d) => Assert.Equal(new DateTime(y, m, d), ProgramInventory.ParseDate(s));

    [Theory]
    [InlineData("11.10.23")]
    [InlineData("99999999")]
    [InlineData("19000101")]
    public void Bad_install_date_is_null(string s) => Assert.Null(ProgramInventory.ParseDate(s));

    [Fact]
    public void Duplicates_from_32_and_64_bit_views_are_merged()
    {
        var a = new InstalledProgram { Name = "Tool", Version = "1.0", Publisher = "Acme", Scope = ProgramScope.Machine64, RegistryKeyName = "Tool" };
        var b = a with { Scope = ProgramScope.Machine32 };
        var c = a with { Version = "2.0" };
        Assert.Equal(2, ProgramInventory.Deduplicate([a, b, c]).Count());
    }

    // ── Безопасность: что считаем «своей папкой программы» ──────────────────────
    [Fact]
    public void Shared_folders_are_not_program_folders()
    {
        Assert.False(ProgramInventory.IsProgramOwnFolder("C:\\"));
        Assert.False(ProgramInventory.IsProgramOwnFolder(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Assert.False(ProgramInventory.IsProgramOwnFolder(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.False(ProgramInventory.IsProgramOwnFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32")));
        Assert.False(ProgramInventory.IsProgramOwnFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.False(ProgramInventory.IsProgramOwnFolder(null));
    }

    [Fact]
    public void Own_folder_is_a_program_folder()
    {
        var dir = Directory.CreateTempSubdirectory("cleaner-prog-").FullName;
        try { Assert.True(ProgramInventory.IsProgramOwnFolder(dir)); }
        finally { Directory.Delete(dir); }
    }

    [Theory]
    [InlineData("Un_A", true)]
    [InlineData("Au_", true)]
    [InlineData("unins000", true)]
    [InlineData("_iu14D2N.tmp", true)]
    [InlineData("chrome", false)]
    [InlineData("explorer", false)]
    public void Recognizes_temporary_uninstaller_processes(string name, bool expected) =>
        Assert.Equal(expected, Uninstaller.LooksLikeUninstaller(name));
}
