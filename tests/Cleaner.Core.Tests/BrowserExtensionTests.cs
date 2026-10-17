using Cleaner.Core.Browsers;
using Cleaner.Core.Programs;
using Microsoft.Win32;

namespace Cleaner.Core.Tests;

/// <summary>Расширения — на поддельном профиле Chromium в песочнице. Реальные браузеры и реестр не трогаются.</summary>
public class BrowserExtensionTests
{
    const string Good = "aaaabbbbccccddddeeeeffffgggghhhh";      // 32 буквы a–p
    const string Hijacker = "kadaohckdkghfaclhjmkmplebcdcnfnp";
    const string Bundled = "ghbmnnjooekpmoecnnnilnnbdlolhkhi";
    const string Component = "mhjfbmdgcfjbbpaeojofohoefgiehjai";
    const string Gone = "pppppppppppppppppppppppppppppppp";

    static BrowserDef Setup(Sandbox box)
    {
        var ud = Path.Combine(box.Root, "User Data");
        var prof = Path.Combine(ud, "Profile 1");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(ud, "Local State"), """{"profile":{"info_cache":{"Profile 1":{"name":"Работа"}}}}""");

        void Ext(string id, string manifest, string? locale = null)
        {
            var dir = Path.Combine(prof, "Extensions", id, "1.0_0");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), manifest);
            File.WriteAllBytes(Path.Combine(dir, "icon48.png"), [1, 2, 3]);
            if (locale is not null)
            {
                Directory.CreateDirectory(Path.Combine(dir, "_locales", "ru"));
                File.WriteAllText(Path.Combine(dir, "_locales", "ru", "messages.json"), locale);
            }
        }
        Ext(Good, """{"name":"__MSG_appName__","version":"1.0","default_locale":"en","icons":{"16":"icon16.png","48":"icon48.png"},"permissions":["tabs"],"host_permissions":["<all_urls>"]}""",
            """{"appName":{"message":"Тёмная тема"}}""");
        Ext(Hijacker, """{"name":"Planet Search","version":"2.0.1","chrome_settings_overrides":{"search_provider":{"name":"Planet","search_url":"https://x/?q={searchTerms}"}}}""");
        Ext(Bundled, """{"name":"Docs Offline","version":"1.1"}""");
        Ext(Component, """{"name":"PDF","version":"1"}""");

        File.WriteAllText(Path.Combine(prof, "Preferences"), $$$"""
            {"profile":{"name":"Профиль"},"extensions":{"settings":{
              "{{{Good}}}":{"location":1,"from_webstore":true},
              "{{{Bundled}}}":{"location":6,"from_webstore":true},
              "{{{Component}}}":{"location":5},
              "{{{Gone}}}":{"location":1}
            }
            }
            }
            """);
        // Secure Preferences дополняет записи: отключено с причиной «внешнее, не подтверждено».
        File.WriteAllText(Path.Combine(prof, "Secure Preferences"), $$$"""
            {"extensions":{"settings":{
              "{{{Hijacker}}}":{"location":6,"from_webstore":true,"disable_reasons":8192},
              "{{{Good}}}":{"path":"{{{Good}}}\\1.0_0"}
            }
            }
            }
            """);
        return new BrowserDef("Chrome", ud, "chrome", "chrome.exe", @"Google\Chrome");
    }

    static IReadOnlyList<(string, RegistryView)> FakeKeys(BrowserDef b, string id) =>
        id == Hijacker ? [(@"HKLM\SOFTWARE\Google\Chrome\Extensions\" + id, RegistryView.Registry32)] : [];

    [Fact]
    public void Reads_profile_extensions_with_names_sources_and_warnings()
    {
        using var box = new Sandbox();
        var exts = BrowserExtensions.Read(Setup(box), FakeKeys);

        Assert.Equal(3, exts.Count);                                      // компонент и «настройки без файлов» не показываются
        Assert.DoesNotContain(exts, e => e.Id is Component or Gone);

        var good = exts.Single(e => e.Id == Good);
        Assert.Equal("Тёмная тема", good.Name);                          // __MSG_…__ из _locales\ru
        Assert.Equal("Работа", good.ProfileName);                           // имя из Local State
        Assert.Equal("Profile 1", good.ProfileDir);
        Assert.Equal(ExtensionSource.WebStore, good.Source);
        Assert.True(good.Enabled);
        Assert.False(good.IsSuspicious);
        Assert.Contains("видит и может менять все сайты", good.Notes);
        Assert.EndsWith("icon48.png", good.IconPath);

        var hijacker = exts.Single(e => e.Id == Hijacker);
        Assert.Equal(ExtensionSource.Program, hijacker.Source);
        Assert.False(hijacker.Enabled);
        Assert.Contains("подменяет поисковую систему", hijacker.Warnings);
        Assert.Single(hijacker.ExternalKeys);

        var bundled = exts.Single(e => e.Id == Bundled);
        Assert.Equal(ExtensionSource.Bundled, bundled.Source);           // внешнее, но без ключа — подложил сам браузер
        Assert.False(bundled.IsSuspicious);
    }

    [Fact]
    public void Only_program_installed_extension_gets_a_registry_removal()
    {
        using var box = new Sandbox();
        var exts = BrowserExtensions.Read(Setup(box), FakeKeys);

        var removal = Assert.Single(BrowserExtensions.RegistryRemoval(exts.Single(e => e.Id == Hijacker)));
        Assert.Equal(LeftoverKind.RegistryKey, removal.Kind);
        Assert.Equal(RegistryView.Registry32, removal.View);
        Assert.True(removal.NeedsAdmin);
        Assert.Empty(BrowserExtensions.RegistryRemoval(exts.Single(e => e.Id == Good)));
    }

    [Fact]
    public void Manage_url_points_to_the_extension_page()
    {
        using var box = new Sandbox();
        var e = BrowserExtensions.Read(Setup(box), FakeKeys).Single(x => x.Id == Good);
        Assert.Equal($"chrome://extensions/?id={Good}", e.ManageUrl);
    }

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Google\Chrome\Extensions\kadaohckdkghfaclhjmkmplebcdcnfnp", true)]
    [InlineData(@"HKCU\SOFTWARE\Microsoft\Edge\Extensions\ahkjpbeeocnddjkakilopmfdlnjdpcdm", true)]
    [InlineData(@"HKLM\SOFTWARE\Google\Chrome\Extensions", false)]                         // весь раздел — нельзя
    [InlineData(@"HKLM\SOFTWARE\Google\Chrome\Extensions\abc", false)]                     // не id
    [InlineData(@"HKLM\SOFTWARE\Google\Chrome\Extensions\kadaohckdkghfaclhjmkmplebcdcnfnz", false)]   // z — не из a–p
    [InlineData(@"HKLM\SOFTWARE\Google\Chrome\Policies\kadaohckdkghfaclhjmkmplebcdcnfnp", false)]
    [InlineData(@"HKLM\SOFTWARE\Google\Update\Extensions\kadaohckdkghfaclhjmkmplebcdcnfnp", false)]
    public void Only_single_extension_keys_of_known_browsers_are_removable(string path, bool ok) =>
        Assert.Equal(ok, LeftoverPlan.IsBrowserExtensionKey(path));

    [Fact]
    public void Missing_browser_folder_gives_empty_list()
    {
        var b = new BrowserDef("X", @"C:\NoSuch-Cleaner\User Data", "x", "x.exe", "X");
        Assert.Empty(BrowserExtensions.Read(b, FakeKeys));
    }
}
