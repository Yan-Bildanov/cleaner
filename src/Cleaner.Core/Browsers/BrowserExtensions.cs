using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Cleaner.Core.Browsers;

/// <summary>Браузер на движке Chromium: где лежат профили, как открыть его страницу расширений, где программы прописывают свои расширения.</summary>
public sealed record BrowserDef(string Name, string UserDataDir, string Scheme, string ExeName, string VendorKey)
{
    public static IReadOnlyList<BrowserDef> Known()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return
        [
            new("Chrome", Path.Combine(local, @"Google\Chrome\User Data"), "chrome", "chrome.exe", @"Google\Chrome"),
            new("Edge", Path.Combine(local, @"Microsoft\Edge\User Data"), "edge", "msedge.exe", @"Microsoft\Edge"),
            new("Brave", Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"), "brave", "brave.exe", @"BraveSoftware\Brave-Browser"),
            new("Vivaldi", Path.Combine(local, @"Vivaldi\User Data"), "vivaldi", "vivaldi.exe", @"Vivaldi"),
            new("Яндекс Браузер", Path.Combine(local, @"Yandex\YandexBrowser\User Data"), "browser", "browser.exe", @"Yandex\YandexBrowser"),
        ];
    }
}

public enum ExtensionSource
{
    /// <summary>Из интернет-магазина, поставил пользователь.</summary>
    WebStore,
    /// <summary>Поставила программа через реестр или файл external_extensions — без участия пользователя.</summary>
    Program,
    /// <summary>Распакованное, в режиме разработчика.</summary>
    Developer,
    /// <summary>Принудительно, политикой (организация или вредоносная программа).</summary>
    Policy,
    /// <summary>Другое (вручную из файла .crx, из командной строки).</summary>
    Other,
    /// <summary>Подложил сам браузер при установке (например, «Google Документы офлайн»): внешнее, но без ключа в реестре.</summary>
    Bundled,
}

public sealed record BrowserExtension
{
    public required BrowserDef Browser { get; init; }
    public required string ProfileDir { get; init; }
    public required string ProfileName { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; }
    public bool IsTheme { get; init; }
    public ExtensionSource Source { get; init; }
    /// <summary>Ключи реестра, через которые программа поставила расширение: «HKLM\SOFTWARE\Google\Chrome\Extensions\id» (бывает и в 64-, и в 32-битной ветке).</summary>
    public IReadOnlyList<(string Path, RegistryView View)> ExternalKeys { get; init; } = [];
    public string? ExternalKey => ExternalKeys.Count > 0 ? ExternalKeys[0].Path : null;
    public long Size { get; init; }
    public string? IconPath { get; init; }
    /// <summary>Тревожные признаки: подмена поиска, домашней страницы, новой вкладки, установка без участия пользователя.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
    /// <summary>Просто к сведению: широкие права, которые есть и у полезных расширений.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool IsSuspicious => Warnings.Count > 0;
    public string ManageUrl => $"{Browser.Scheme}://extensions/?id={Id}";
}

/// <summary>
/// Расширения браузеров на Chromium. Только чтение файлов профиля (Preferences, Secure Preferences, manifest.json).
/// Сами файлы браузера Cleaner не меняет: Chrome подписывает настройки и при вмешательстве сбрасывает их.
/// </summary>
public static partial class BrowserExtensions
{
    public static Action<Exception>? OnError;

    /// <summary>Встроенные служебные расширения, которые не показываются и в самом браузере.</summary>
    static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "nmmhkkegccagdldgiimedpiccmgmieda",   // Chrome Web Store Payments
        "pkedcjkdefgpdelpbcmbmeomcjbeemfm",   // Chrome Media Router
        "mhjfbmdgcfjbbpaeojofohoefgiehjai",   // Chrome PDF Viewer
        "neajdppkdcdipfabeoofebfddakdcjhd",   // Google Network Speech
        "nkeimhogjdpnpccoofpliimaahmaaome",   // Google Hangouts (служебное)
    };

    public static IReadOnlyList<BrowserExtension> ReadAll() =>
        BrowserDef.Known().Where(b => Directory.Exists(b.UserDataDir)).SelectMany(b => Read(b, FindExternalKeys)).ToList();

    /// <param name="externalKey">Где программа прописала расширение (для тестов — подстановка вместо реального реестра).</param>
    public static IReadOnlyList<BrowserExtension> Read(BrowserDef browser, Func<BrowserDef, string, IReadOnlyList<(string Path, RegistryView View)>> externalKey)
    {
        var result = new List<BrowserExtension>();
        if (!Directory.Exists(browser.UserDataDir)) return result;
        var names = ProfileNames(browser.UserDataDir);

        foreach (var profileDir in Directory.EnumerateDirectories(browser.UserDataDir))
        {
            var dirName = Path.GetFileName(profileDir);
            if (dirName is "System Profile" or "Guest Profile") continue;
            if (!File.Exists(Path.Combine(profileDir, "Preferences"))) continue;
            try
            {
                var settings = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in new[] { "Preferences", "Secure Preferences" })
                    Collect(Path.Combine(profileDir, file), settings);

                var profileName = names.GetValueOrDefault(dirName) ?? dirName;
                foreach (var (id, s) in settings)
                {
                    if (Hidden.Contains(id)) continue;
                    var ext = FromSettings(browser, profileDir, profileName, id, s, externalKey);
                    if (ext is not null) result.Add(ext);
                }
            }
            catch (Exception ex) { OnError?.Invoke(ex); }
        }
        return result.OrderBy(e => e.Browser.Name).ThenBy(e => e.ProfileName).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static void Collect(string file, Dictionary<string, JsonElement> into)
    {
        if (!File.Exists(file)) return;
        try
        {
            // Читаем, даже если браузер открыт: файл открыт им с разрешением на чтение.
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            if (!doc.RootElement.TryGetProperty("extensions", out var e) || !e.TryGetProperty("settings", out var settings)
                || settings.ValueKind != JsonValueKind.Object) return;
            foreach (var p in settings.EnumerateObject())
            {
                // Записи из двух файлов дополняют друг друга — сливаем поля.
                if (into.TryGetValue(p.Name, out var existing)) into[p.Name] = Merge(existing, p.Value);
                else into[p.Name] = p.Value.Clone();
            }
        }
        catch (Exception ex) { OnError?.Invoke(ex); }
    }

    static JsonElement Merge(JsonElement a, JsonElement b)
    {
        var dict = new Dictionary<string, JsonElement>();
        foreach (var p in a.EnumerateObject()) dict[p.Name] = p.Value;
        foreach (var p in b.EnumerateObject()) dict[p.Name] = p.Value;
        return JsonSerializer.SerializeToElement(dict);
    }

    static Dictionary<string, string> ProfileNames(string userData)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var ls = Path.Combine(userData, "Local State");
            if (!File.Exists(ls)) return map;
            using var fs = new FileStream(ls, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            if (doc.RootElement.TryGetProperty("profile", out var p) && p.TryGetProperty("info_cache", out var cache))
                foreach (var e in cache.EnumerateObject())
                    if (e.Value.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name) map[e.Name] = name;
        }
        catch (Exception ex) { OnError?.Invoke(ex); }
        return map;
    }

    static BrowserExtension? FromSettings(BrowserDef browser, string profileDir, string profileName, string id, JsonElement s,
                                          Func<BrowserDef, string, IReadOnlyList<(string Path, RegistryView View)>> externalKey)
    {
        var location = Int(s, "location") ?? 1;
        if (location is 5 or 10) return null;                                   // встроенные компоненты браузера

        // Папка расширения: для распакованных — абсолютный путь, иначе Extensions\id\версия.
        string? folder = null;
        var relPath = Str(s, "path");
        if (location == 4 && relPath is not null && Directory.Exists(relPath)) folder = relPath;
        else
        {
            var root = Path.Combine(profileDir, "Extensions", id);
            if (relPath is not null && Directory.Exists(Path.Combine(profileDir, "Extensions", relPath))) folder = Path.Combine(profileDir, "Extensions", relPath);
            else if (Directory.Exists(root)) folder = Directory.EnumerateDirectories(root).OrderBy(d => d).LastOrDefault();
        }
        if (folder is null) return null;                                         // остались только настройки, самого расширения нет

        JsonElement manifest;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
            manifest = doc.RootElement.Clone();
        }
        catch { return null; }

        var name = Localize(Str(manifest, "name") ?? id, folder, manifest);
        var description = Str(manifest, "description") is { } d ? Localize(d, folder, manifest) : null;

        var fromStore = Bool(s, "from_webstore") ?? false;
        var source = location switch
        {
            2 or 3 or 6 => ExtensionSource.Program,
            4 => ExtensionSource.Developer,
            7 or 9 => ExtensionSource.Policy,
            1 => fromStore ? ExtensionSource.WebStore : ExtensionSource.Other,
            _ => ExtensionSource.Other,
        };
        var keys = source == ExtensionSource.Program ? externalKey(browser, id) : [];
        // Внешнее, но без ключа в реестре — его подложил сам браузер (default apps, external_extensions.json рядом с браузером).
        if (source == ExtensionSource.Program && keys.Count == 0) source = ExtensionSource.Bundled;

        var warnings = new List<string>();
        var notes = new List<string>();
        if (manifest.TryGetProperty("chrome_settings_overrides", out var overrides))
        {
            if (overrides.TryGetProperty("search_provider", out _)) warnings.Add("подменяет поисковую систему");
            if (overrides.TryGetProperty("homepage", out _)) warnings.Add("подменяет домашнюю страницу");
            if (overrides.TryGetProperty("startup_pages", out _)) warnings.Add("подменяет стартовые страницы");
        }
        if (manifest.TryGetProperty("chrome_url_overrides", out var urlOv) && urlOv.TryGetProperty("newtab", out _))
            warnings.Add("подменяет новую вкладку");
        if (source == ExtensionSource.Program) warnings.Add("поставлено программой, а не тобой из магазина");
        if (source == ExtensionSource.Policy) warnings.Add("установлено принудительно политикой — удалить в браузере нельзя");
        if (source == ExtensionSource.Other) warnings.Add("поставлено не из интернет-магазина");

        var perms = Strings(manifest, "permissions").Concat(Strings(manifest, "host_permissions")).Concat(Strings(manifest, "optional_host_permissions"))
            .Concat(ContentScriptMatches(manifest)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (perms.Overlaps(["<all_urls>", "*://*/*", "http://*/*", "https://*/*"])) notes.Add("видит и может менять все сайты");
        if (perms.Contains("proxy")) notes.Add("может менять прокси");
        if (perms.Contains("management")) notes.Add("управляет другими расширениями");
        if (perms.Contains("nativeMessaging")) notes.Add("связывается с программой на компьютере");
        if (perms.Contains("debugger")) notes.Add("доступ к отладке страниц");
        if (perms.Contains("history")) notes.Add("видит историю");
        if (perms.Contains("cookies")) notes.Add("доступ к cookie");

        return new BrowserExtension
        {
            Browser = browser,
            ProfileDir = Path.GetFileName(profileDir),
            ProfileName = profileName,
            Id = id,
            Name = name,
            Version = Str(manifest, "version"),
            Description = description,
            Enabled = IsEnabled(s),
            IsTheme = manifest.TryGetProperty("theme", out _),
            Source = source,
            ExternalKeys = keys,
            Size = FolderSize(Path.Combine(profileDir, "Extensions", id)) is var sz and > 0 ? sz : FolderSize(folder),
            IconPath = Icon(folder, manifest),
            Warnings = warnings,
            Notes = notes,
        };
    }

    /// <summary>Включено ли: в новых версиях причины отключения — список, в старых — число и поле state.</summary>
    static bool IsEnabled(JsonElement s)
    {
        if (s.TryGetProperty("disable_reasons", out var r))
        {
            if (r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var n) && n != 0) return false;
            if (r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0) return false;
        }
        return Int(s, "state") is not 0;
    }

    // ── Локализация «__MSG_appName__» ─────────────────────────────────────────────────────────────
    [GeneratedRegex(@"^__MSG_(.+?)__$")]
    private static partial Regex MsgKey();

    public static string Localize(string text, string folder, JsonElement manifest)
    {
        var m = MsgKey().Match(text.Trim());
        if (!m.Success) return text;
        var key = m.Groups[1].Value;
        var locales = new List<string> { "ru" };
        if (Str(manifest, "default_locale") is { } dl) locales.Add(dl);
        locales.AddRange(["en", "en_US"]);
        foreach (var loc in locales.Distinct())
        {
            var file = Path.Combine(folder, "_locales", loc, "messages.json");
            if (!File.Exists(file)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && p.Value.TryGetProperty("message", out var msg) && msg.GetString() is { Length: > 0 } s)
                        return s;
            }
            catch { }
        }
        return key;
    }

    static string? Icon(string folder, JsonElement manifest)
    {
        if (!manifest.TryGetProperty("icons", out var icons) || icons.ValueKind != JsonValueKind.Object) return null;
        var best = icons.EnumerateObject()
            .Select(p => (Size: int.TryParse(p.Name, out var n) ? n : 0, File: p.Value.GetString()))
            .Where(x => x.File is not null)
            .OrderBy(x => x.Size >= 32 ? x.Size : 1000 - x.Size)
            .FirstOrDefault();
        if (best.File is null) return null;
        var path = Path.Combine(folder, best.File.TrimStart('/', '\\').Replace('/', '\\'));
        return File.Exists(path) ? path : null;
    }

    static IEnumerable<string> ContentScriptMatches(JsonElement manifest)
    {
        if (!manifest.TryGetProperty("content_scripts", out var cs) || cs.ValueKind != JsonValueKind.Array) yield break;
        foreach (var s in cs.EnumerateArray())
            foreach (var m in Strings(s, "matches")) yield return m;
    }

    static IEnumerable<string> Strings(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) yield break;
        foreach (var x in arr.EnumerateArray())
            if (x.ValueKind == JsonValueKind.String && x.GetString() is { } s) yield return s;
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    static readonly EnumerationOptions Deep = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    static long FolderSize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        try { foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", Deep)) { try { total += f.Length; } catch { } } }
        catch { }
        return total;
    }

    // ── Где программа прописала расширение ───────────────────────────────────────────────────────
    /// <summary>HKLM (64 и 32) и HKCU: Software\&lt;производитель браузера&gt;\Extensions\&lt;id&gt; — все найденные.</summary>
    public static IReadOnlyList<(string Path, RegistryView View)> FindExternalKeys(BrowserDef browser, string id)
    {
        var found = new List<(string, RegistryView)>();
        var sub = @"SOFTWARE\" + browser.VendorKey + @"\Extensions\" + id;
        foreach (var (hive, view, prefix) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM"),
                     (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM"),
                     (RegistryHive.CurrentUser, RegistryView.Default, "HKCU"),
                 })
        {
            try
            {
                using var b = RegistryKey.OpenBaseKey(hive, view);
                using var k = b.OpenSubKey(sub);
                if (k is not null) found.Add((prefix + @"\" + sub, view));
            }
            catch { }
        }
        return found;
    }

    /// <summary>
    /// Расширение, поставленное программой, удаляется через её ключи в реестре: ключи уходят в карантин,
    /// браузер при следующем запуске сам удаляет расширение. Пусто — у расширения нет таких ключей.
    /// </summary>
    public static IReadOnlyList<Programs.Leftover> RegistryRemoval(BrowserExtension e) =>
        e.ExternalKeys.Where(k => Programs.LeftoverPlan.IsBrowserExtensionKey(k.Path))
            .Select(k => new Programs.Leftover(Programs.LeftoverKind.RegistryKey, k.Path, Programs.LeftoverConfidence.High,
                $"через этот ключ программа ставит «{e.Name}» в {e.Browser.Name}", View: k.View,
                NeedsAdmin: k.Path.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase)))
            .ToList();

    // ── Открыть страницу расширения в нужном профиле ─────────────────────────────────────────────
    public static string? BrowserExe(BrowserDef browser)
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + browser.ExeName);
                if (k?.GetValue(null) is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
            }
            catch { }
        }
        // Запасной путь: рядом с User Data лежит папка Application (так устроены Chrome, Edge, Brave).
        var app = Path.Combine(Path.GetDirectoryName(browser.UserDataDir)!, "Application", browser.ExeName);
        return File.Exists(app) ? app : null;
    }

    /// <summary>Открывает страницу расширения в его профиле. false — браузер не найден.</summary>
    public static bool OpenManagePage(BrowserExtension e)
    {
        var exe = BrowserExe(e.Browser);
        if (exe is null) return false;
        try
        {
            Process.Start(new ProcessStartInfo(exe, $"--profile-directory=\"{e.ProfileDir}\" \"{e.ManageUrl}\"") { UseShellExecute = false });
            return true;
        }
        catch (Exception ex) { OnError?.Invoke(ex); return false; }
    }
}
