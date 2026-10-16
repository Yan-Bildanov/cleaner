using Cleaner.Core.Rules;

namespace Cleaner.Core.Tests;

public class LocalRulesTests
{
    [Fact]
    public void Loads_electron_app_and_plain_paths()
    {
        using var box = new Sandbox();
        var file = Path.Combine(box.Root, "rules.local.json");
        File.WriteAllText(file, """
            [
              // комментарии разрешены
              { "id": "local.app", "name": "MyApp — кэш", "electronApp": "%APPDATA%\\MyApp", "processNames": ["MyApp"] },
              { "id": "local.logs", "group": "Разработка", "name": "Логи", "paths": ["%LOCALAPPDATA%\\MyTool\\logs"], "minAgeDays": 7, "defaultChecked": false },
            ]
            """);

        var rules = LocalRules.Load(file);

        Assert.Equal(2, rules.Count);
        Assert.Equal(3, rules[0].Paths.Count);                       // Cache, Code Cache, GPUCache
        Assert.Equal(BuiltInRules.GroupApps, rules[0].Group);
        Assert.True(rules[0].DefaultChecked);
        Assert.Equal(TimeSpan.FromDays(7), rules[1].MinAge);
        Assert.False(rules[1].DefaultChecked);
    }

    [Theory]
    [InlineData(@"%USERPROFILE%")]
    [InlineData(@"C:\")]
    [InlineData(@"%WINDIR%")]
    [InlineData(@"%PROGRAMFILES%")]
    [InlineData(@"relative\path")]
    [InlineData(@"%NO_SUCH_VAR%\x\y\z")]
    public void Rejects_dangerous_paths(string path) => Assert.False(LocalRules.IsSafe(path));

    [Fact]
    public void Broken_file_gives_no_rules_instead_of_crash()
    {
        using var box = new Sandbox();
        var file = Path.Combine(box.Root, "rules.local.json");
        File.WriteAllText(file, "{ не json");
        Assert.Empty(LocalRules.Load(file));
    }

    [Fact]
    public void Missing_file_gives_no_rules() => Assert.Empty(LocalRules.Load(@"C:\definitely\not\here.json"));
}
