using Cleaner.Core.Health;

namespace Cleaner.Core.Tests;

public class HealthRulesTests
{
    const long GB = 1L << 30;

    [Theory]
    [InlineData(50000, 37000, HealthStatus.Ok)]          // 74 % — обычный износ для ноутбука нескольких лет
    [InlineData(50000, 45000, HealthStatus.Ok)]
    [InlineData(50000, 26000, HealthStatus.Warning)]     // 52 %
    [InlineData(50000, 17500, HealthStatus.Problem)]     // 35 %
    public void Battery_thresholds(long design, long full, HealthStatus expected) =>
        Assert.Equal(expected, HealthRules.Battery(new BatterySample(design, full, null)).Status);

    [Fact]
    public void Battery_missing_is_unknown() =>
        Assert.Equal(HealthStatus.Unknown, HealthRules.Battery(null).Status);

    [Theory]
    [InlineData(0x061100u, true, true)]     // типичный Защитник Windows: включён, базы актуальны
    [InlineData(0x040000u, false, true)]    // установленный, но выключенный сторонний антивирус
    [InlineData(0x061010u, true, false)]    // включён, базы устарели
    public void Decodes_antivirus_state(uint state, bool enabled, bool upToDate) =>
        Assert.Equal((enabled, upToDate), HealthRules.DecodeAvState(state));

    [Fact]
    public void Protection_ok_when_defender_on_and_mentions_disabled_duplicate_antivirus()
    {
        var check = HealthRules.Protection(
            [new("Other Antivirus", 0x040000), new("Other Antivirus", 0x040000), new("Windows Defender", 0x061100)],
            new Dictionary<string, bool?> { ["частная сеть"] = true, ["общественная сеть"] = true });

        Assert.Equal(HealthStatus.Ok, check.Status);
        Assert.Equal("Windows Defender", check.Value);
        Assert.Contains("Other Antivirus установлен, но выключен", check.Hint);
        Assert.Contains("2 раза", check.Hint);
    }

    [Fact]
    public void Protection_problem_without_any_active_antivirus() =>
        Assert.Equal(HealthStatus.Problem,
            HealthRules.Protection([new("Other Antivirus", 0x040000)], new Dictionary<string, bool?>()).Status);

    [Fact]
    public void Protection_warns_when_firewall_profile_off() =>
        Assert.Equal(HealthStatus.Warning,
            HealthRules.Protection([new("Windows Defender", 0x061100)], new Dictionary<string, bool?> { ["общественная сеть"] = false }).Status);

    [Fact]
    public void Updates_warn_on_long_uptime_and_old_patch()
    {
        var now = new DateTime(2026, 3, 1);
        var fresh = HealthRules.Updates(now.AddDays(-10), TimeSpan.FromDays(2), false, now);
        var longUptime = HealthRules.Updates(now.AddDays(-10), TimeSpan.FromDays(18), false, now);
        var old = HealthRules.Updates(now.AddDays(-90), TimeSpan.FromDays(1), false, now);

        Assert.Equal(HealthStatus.Ok, fresh.Status);
        Assert.Equal(HealthStatus.Warning, longUptime.Status);
        Assert.Contains("не перезагружался 18", longUptime.Hint);
        Assert.Equal(HealthStatus.Problem, old.Status);
    }

    [Fact]
    public void Startup_warns_about_remote_access_tool_but_ignores_disabled_entries()
    {
        var withRemote = HealthRules.Startup([new("TeamViewer", "все пользователи", true), new("OneDrive", "пользователь", false)]);
        var remoteOff = HealthRules.Startup([new("TeamViewer", "все пользователи", false)]);

        Assert.Equal(HealthStatus.Warning, withRemote.Status);
        Assert.Contains("TeamViewer", withRemote.Hint);
        Assert.StartsWith("1 программ включено, 1 выключено", withRemote.Value);
        Assert.Equal(HealthStatus.Ok, remoteOff.Status);

        // Частый случай: ярлык выключен, но служба стартует автоматически.
        var service = HealthRules.Startup([new("TeamViewer", "все пользователи", false), new("TeamViewer Service", "служба Windows", true)]);
        Assert.Equal(HealthStatus.Warning, service.Status);
        Assert.Contains("TeamViewer Service (служба)", service.Hint);
    }

    [Fact]
    public void Disk_low_space_and_wear()
    {
        var sys = new DiskSample("C:", 500 * GB, 30 * GB, "", null, null);            // 6 % свободно
        var ok = new DiskSample("C:", 500 * GB, 300 * GB, "", null, null);
        var ssd = new DiskSample("NVMe SSD", 512 * GB, 0, "Healthy", null, null);
        var worn = ssd with { WearPercent = 85 };

        Assert.Equal(HealthStatus.Problem, HealthRules.Disk(sys, [ssd]).Status);
        Assert.Equal(HealthStatus.Ok, HealthRules.Disk(ok, [ssd]).Status);
        Assert.Contains("правами администратора", HealthRules.Disk(ok, [ssd]).Hint);
        Assert.Equal(HealthStatus.Problem, HealthRules.Disk(ok, [worn]).Status);
    }

    [Fact]
    public void Temperature_unknown_without_data_and_hot_gpu_is_warning()
    {
        Assert.Equal(HealthStatus.Unknown, HealthRules.Temperature(null, null, false).Status);
        Assert.Equal(HealthStatus.Ok, HealthRules.Temperature(null, 45, false).Status);
        Assert.Equal(HealthStatus.Warning, HealthRules.Temperature(55, 82, true).Status);
    }
}
