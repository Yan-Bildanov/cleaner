using System.Xml.Linq;
using Cleaner.Core.Auto;
using Cleaner.Core.Cleaning;
using Cleaner.Core.Platform;
using Cleaner.Core.Scanning;

namespace Cleaner.Core.Tests;

public class AutoTests
{
    [Fact]
    public void Eligible_rules_never_include_recycle_bin_or_admin_rules()
    {
        Assert.DoesNotContain(AutoCleaner.Eligible, r => r.Id == "win.recycle");
        Assert.DoesNotContain(AutoCleaner.Eligible, r => r.RequiresAdmin);
        Assert.Contains(AutoCleaner.Eligible, r => r.Id == "br.chrome");
    }

    [Fact]
    public void Default_settings_select_only_eligible_rules()
    {
        var ids = AutoCleaner.Eligible.Select(r => r.Id).ToHashSet();
        Assert.All(new AutoCleanSettings().RuleIds, id => Assert.Contains(id, ids));
    }

    [Fact]
    public void Open_app_rules_are_skipped_entirely_and_dry_run_touches_nothing()
    {
        var sys = new FakeSystem();
        sys.Running.Add("chrome");
        using var box = new Sandbox();
        var auto = new AutoCleaner(new Scanner(sys), new FileCleaner(new QuarantineStore(Path.Combine(box.Root, "q"), sys), sys),
                                   new QuarantineStore(Path.Combine(box.Root, "q"), sys), sys);

        var r = auto.Run(new AutoCleanSettings { RuleIds = ["br.chrome"] }, dryRun: true);

        Assert.Contains("Google Chrome", r.SkippedOpenApps);
        Assert.Equal(0, r.FoundBytes);
        Assert.Equal(0, r.CleanedFiles);
    }

    [Theory]
    // пятница 16.10.2026 14:00 → ближайшее воскресенье 18.10 12:00
    [InlineData("2026-10-16T14:00", AutoFrequency.Weekly, "2026-10-18T12:00")]
    // воскресенье до полудня → сегодня
    [InlineData("2026-10-18T09:00", AutoFrequency.Weekly, "2026-10-18T12:00")]
    // воскресенье после полудня → через неделю
    [InlineData("2026-10-18T13:00", AutoFrequency.Weekly, "2026-10-25T12:00")]
    [InlineData("2026-10-16T14:00", AutoFrequency.Daily, "2026-10-17T12:00")]
    [InlineData("2026-10-16T08:00", AutoFrequency.Threshold, "2026-10-16T12:00")]
    public void Next_run_is_computed_correctly(string now, AutoFrequency f, string expected) =>
        Assert.Equal(DateTime.Parse(expected), AutoCleanTask.NextRun(f, DateTime.Parse(now)));

    [Fact]
    public void Task_xml_is_valid_and_runs_without_admin_and_catches_up_missed_runs()
    {
        var xml = AutoCleanTask.BuildXml(@"C:\Apps\Cleaner & Co\Cleaner.exe", AutoFrequency.Weekly, new DateTime(2026, 10, 16, 14, 0, 0));
        var doc = XDocument.Parse(xml);                        // валидный XML даже с «&» в пути
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal("LeastPrivilege", doc.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("true", doc.Descendants(ns + "StartWhenAvailable").Single().Value);
        Assert.Equal("--auto", doc.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal(@"C:\Apps\Cleaner & Co\Cleaner.exe", doc.Descendants(ns + "Command").Single().Value);
        Assert.Single(doc.Descendants(ns + "Sunday"));
    }

    [Fact]
    public void Settings_round_trip_and_survive_corrupt_file()
    {
        using var box = new Sandbox();
        var store = new AutoCleanStore(box.Root);
        store.SaveSettings(new AutoCleanSettings { Enabled = true, Frequency = AutoFrequency.Threshold, RuleIds = ["win.temp"] });
        var loaded = store.LoadSettings();
        Assert.True(loaded.Enabled);
        Assert.Equal(AutoFrequency.Threshold, loaded.Frequency);
        Assert.Equal(["win.temp"], loaded.RuleIds);

        File.WriteAllText(Path.Combine(box.Root, "settings.json"), "{ испорчено");
        Assert.False(store.LoadSettings().Enabled);            // по умолчанию — выключено
    }
}
