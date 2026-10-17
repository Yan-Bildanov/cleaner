using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Cleaner.Core.Programs;

public enum LeftoverKind { Folder, Shortcut, RegistryKey }

/// <summary>
/// High — имя папки/ключа точно совпадает с названием программы или это её папка установки (отмечено по умолчанию).
/// Medium — совпало только с издателем (не отмечено по умолчанию: там могут быть данные других его программ).
/// </summary>
public enum LeftoverConfidence { High, Medium }

public sealed record Leftover(
    LeftoverKind Kind,
    string Path,
    LeftoverConfidence Confidence,
    string Reason,
    long Size = 0,
    int FileCount = 0,
    RegistryView View = RegistryView.Default,
    bool NeedsAdmin = false)
{
    [JsonIgnore] public bool IsEmptyFolder => Kind == LeftoverKind.Folder && FileCount == 0;
}

/// <summary>
/// Поиск того, что осталось после удаления программы. Только чтение.
/// Правила:
///   • совпадение имени папки/ключа — ТОЛЬКО точное (без учёта регистра, пробелов и знаков), никаких «содержит»;
///   • имена, общие с другой установленной программой, и слишком общие слова (Microsoft, Common Files, Update…) не используются;
///   • папка не годится, если это корень, Program Files, Windows, профиль и т. п. (<see cref="ProgramInventory.IsProgramOwnFolder"/>),
///     или в ней/над ней лежит папка другой установленной программы.
/// </summary>
public sealed partial class LeftoverScanner(
    IReadOnlyList<string> folderRoots,
    IReadOnlyList<LeftoverScanner.RegistryRoot> registryRoots,
    IReadOnlyList<string> shortcutDirs,
    IReadOnlyList<string>? protectedFolders = null)
{
    public sealed record RegistryRoot(RegistryHive Hive, RegistryView View, string Path);

    public static Action<Exception>? OnError;

    public static LeftoverScanner ForCurrentSystem()
    {
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var local = F(Environment.SpecialFolder.LocalApplicationData);
        var startUser = Path.Combine(F(Environment.SpecialFolder.StartMenu), "Programs");
        var startCommon = Path.Combine(F(Environment.SpecialFolder.CommonStartMenu), "Programs");

        var folders = new List<string>
        {
            F(Environment.SpecialFolder.ProgramFiles),
            F(Environment.SpecialFolder.ProgramFilesX86),
            F(Environment.SpecialFolder.CommonApplicationData),
            F(Environment.SpecialFolder.ApplicationData),
            local,
            Path.Combine(local, "Programs"),
            startUser,
            startCommon,
        };
        var reg = new List<RegistryRoot>
        {
            new(RegistryHive.CurrentUser, RegistryView.Default, "Software"),
            new(RegistryHive.LocalMachine, RegistryView.Registry64, "SOFTWARE"),
        };
        if (Environment.Is64BitOperatingSystem) reg.Add(new(RegistryHive.LocalMachine, RegistryView.Registry32, "SOFTWARE"));

        var shortcuts = new List<string>
        {
            F(Environment.SpecialFolder.DesktopDirectory),
            F(Environment.SpecialFolder.CommonDesktopDirectory),
            startUser,
            startCommon,
        };
        return new LeftoverScanner(
            folders.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            reg, shortcuts.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            [AppPaths.DataDir]);
    }

    // ── Имена ─────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Слишком общие имена — по ним искать нельзя.</summary>
    static readonly HashSet<string> Generic =
    [
        "microsoft", "microsoftcorporation", "windows", "google", "googlellc", "intel", "nvidia", "nvidiacorporation",
        "amd", "apple", "appleinc", "adobe", "oracle", "common", "commonfiles", "programs", "program", "programfiles",
        "packages", "package", "packagecache", "temp", "tmp", "cache", "logs", "log", "data", "app", "apps", "application",
        "software", "system", "system32", "update", "updater", "updates", "installer", "installers", "setup", "uninstall",
        "user", "users", "default", "public", "desktop", "documents", "local", "roaming", "locallow", "programdata",
        "classes", "policies", "clients", "wow6432node", "registeredapplications", "drivers", "tools", "utilities",
        "settings", "config", "plugins", "extensions", "shared", "runtime", "service", "services", "client", "launcher",
        "helper", "support", "license", "licenses", "crashreports", "crashpad", "cleaner", "startup", "accessories",
        "administrativetools", "maintenance", "systemtools", "windowspowershell", "accessibility",
    ];

    /// <summary>Нормализованное имя: только буквы и цифры в нижнем регистре.</summary>
    public static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    [GeneratedRegex(@"[\s\-_]*[\(\[]?\s*(v?\d+([.,]\d+)*[a-z]?|x64|x86|amd64|arm64|64[\s\-]?bit|32[\s\-]?bit|64[\s\-]?разрядная|32[\s\-]?разрядная|user|machine|version|версия|\d+)\s*[\)\]]?\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TrailingNoise();

    [GeneratedRegex(@"\b(inc|llc|ltd|limited|corp|corporation|gmbh|co|company|s\.?a|ag|oy|ooo|ооо|software|technologies|labs)\b\.?", RegexOptions.IgnoreCase)]
    private static partial Regex CompanySuffix();

    /// <summary>«VLC media player 3.0.23 (64-bit)» → «VLC media player».</summary>
    public static string StripVersion(string name)
    {
        var s = name.Trim();
        for (var i = 0; i < 6; i++)
        {
            var t = TrailingNoise().Replace(s, "").Trim();
            if (t == s || t.Length == 0) break;
            s = t;
        }
        return s;
    }

    static bool Usable(string token) => token.Length >= 3 && !token.All(char.IsDigit) && !Generic.Contains(token);

    /// <summary>Имена, под которыми программа могла оставить папки и ключи.</summary>
    public static (HashSet<string> Names, HashSet<string> Publishers) Tokens(InstalledProgram p)
    {
        var names = new HashSet<string>();
        void AddName(string? s) { if (s is null) return; var n = Norm(s); if (Usable(n)) names.Add(n); }

        AddName(p.Name);
        var stripped = StripVersion(p.Name);
        AddName(stripped);
        // «Happ - Proxy Utility» → ещё и «Happ»
        var dash = stripped.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) AddName(stripped[..dash]);

        // Имя папки установки (после удаления её уже может не быть — тогда просто берём имя из пути).
        if (p.InstallLocation is { } loc && !IsSharedRoot(loc))
            AddName(SafeLeaf(loc));
        if (UninstallCommand.For(p)?.FileName is { } exe && Path.IsPathRooted(exe))
        {
            var dir = Path.GetDirectoryName(exe);
            if (dir is not null && !IsSharedRoot(dir) && !dir.Contains("Package Cache", StringComparison.OrdinalIgnoreCase)
                && !dir.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
                AddName(SafeLeaf(dir));
        }

        var publishers = new HashSet<string>();
        if (p.Publisher is { } pub)
        {
            var n = Norm(pub);
            if (Usable(n)) publishers.Add(n);
            var bare = Norm(CompanySuffix().Replace(pub, ""));
            if (Usable(bare)) publishers.Add(bare);
        }
        // Папка над папкой установки часто названа по издателю: Program Files\VideoLAN\VLC.
        if (p.InstallLocation is { } l && !IsSharedRoot(l) && Path.GetDirectoryName(l.TrimEnd('\\')) is { } parent && !IsSharedRoot(parent))
        {
            var leaf = Norm(SafeLeaf(parent) ?? "");
            if (Usable(leaf)) publishers.Add(leaf);
        }
        publishers.ExceptWith(names);
        return (names, publishers);
    }

    static string? SafeLeaf(string? path)
    {
        try { return path is null ? null : Path.GetFileName(path.TrimEnd('\\')); } catch { return null; }
    }

    static bool IsSharedRoot(string path)
    {
        var root = Path.GetPathRoot(path)?.TrimEnd('\\');
        var p = path.TrimEnd('\\');
        if (root is null || p.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var f in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                                  Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
                                  Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile })
            if (p.Equals(Environment.GetFolderPath(f).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
        return p.EndsWith(@"\Programs", StringComparison.OrdinalIgnoreCase);
    }

    // ── Поиск ─────────────────────────────────────────────────────────────────────────────────────
    public IReadOnlyList<Leftover> Scan(InstalledProgram removed, IReadOnlyList<InstalledProgram> stillInstalled)
    {
        var (names, allPublishers) = Tokens(removed);

        // Имена, которые есть и у оставшихся программ, использовать нельзя.
        // Издатель, у которого остались другие программы, — «общий»: его папку не предлагаем, но внутри ищем папку программы.
        var publishers = new HashSet<string>(allPublishers);
        var otherNames = new HashSet<string>();
        foreach (var other in stillInstalled)
        {
            var (on, op) = Tokens(other);
            otherNames.UnionWith(on);
            names.ExceptWith(on);
            names.ExceptWith(op);
            publishers.ExceptWith(on);
            publishers.ExceptWith(op);
            allPublishers.ExceptWith(on);
        }
        // «python» — начало имени «Python Launcher», который остаётся: такие совпадения только с пометкой «проверь».
        var ambiguous = names.Where(n => otherNames.Any(o => o.Length > n.Length && o.StartsWith(n, StringComparison.Ordinal))).ToHashSet();
        LeftoverConfidence ByName(string n) => ambiguous.Contains(n) ? LeftoverConfidence.Medium : LeftoverConfidence.High;
        string NameReason(string n, string what) => ambiguous.Contains(n)
            ? what + " — но так же начинается название другой установленной программы, проверь"
            : what;

        var otherLocations = stillInstalled
            .Select(o => o.InstallLocation).Where(l => !string.IsNullOrEmpty(l))
            .Select(l => Full(l!)).Where(l => l is not null).Cast<string>()
            .ToList();

        var found = new List<Leftover>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddFolder(string path, LeftoverConfidence conf, string reason)
        {
            var full = Full(path);
            if (full is null || !seen.Add(full)) return;
            if (!FolderAllowed(full, otherLocations)) return;
            var (size, count) = Measure(full);
            found.Add(new Leftover(LeftoverKind.Folder, full, conf, reason, size, count, NeedsAdmin: !IsUserWritableLocation(full)));
        }

        // 1. Папка установки, если она пережила деинсталлятор, и пустая папка над ней.
        if (removed.InstallLocation is { } loc && Directory.Exists(loc))
            AddFolder(loc, LeftoverConfidence.High, "папка установки программы");
        if (removed.InstallLocation is { } loc3 && Path.GetDirectoryName(loc3.TrimEnd('\\')) is { } parentDir
            && Directory.Exists(parentDir) && !IsSharedRoot(parentDir) && IsEmptyTree(parentDir))
            AddFolder(parentDir, LeftoverConfidence.High, "пустая папка, в которой была программа");

        // 2. Папки с точным именем программы или издателя.
        foreach (var root in folderRoots)
        {
            foreach (var dir in SafeDirs(root))
            {
                var n = Norm(Path.GetFileName(dir));
                if (names.Contains(n)) AddFolder(dir, ByName(n), NameReason(n, "имя папки совпадает с названием программы"));
                else if (allPublishers.Contains(n))
                {
                    // Внутри папки издателя — ищем папку самой программы.
                    foreach (var sub in SafeDirs(dir))
                        if (names.Contains(Norm(Path.GetFileName(sub))))
                            AddFolder(sub, LeftoverConfidence.High, "папка программы внутри папки издателя");

                    // Саму папку издателя — только если других его программ не осталось.
                    if (!publishers.Contains(n)) continue;
                    if (IsEmptyTree(dir))
                        AddFolder(dir, LeftoverConfidence.High, "пустая папка издателя");
                    else if (OnlyProgramFolders(dir, names))
                        AddFolder(dir, LeftoverConfidence.High, "папка издателя, в которой только эта программа");
                    else
                        AddFolder(dir, LeftoverConfidence.Medium, "папка издателя — проверь, нет ли там нужного");
                }
            }
        }

        // 3. Ярлыки с названием программы.
        foreach (var dir in shortcutDirs)
        {
            foreach (var lnk in SafeFiles(dir, "*.lnk"))
            {
                var n = Norm(Path.GetFileNameWithoutExtension(lnk));
                if (!names.Contains(n) && n != Norm(StripVersion(removed.Name))) continue;
                if (!seen.Add(lnk) || found.Any(f => f.Kind == LeftoverKind.Folder && IsInside(lnk, f.Path))) continue;
                long size = 0;
                try { size = new FileInfo(lnk).Length; } catch { }
                found.Add(new Leftover(LeftoverKind.Shortcut, lnk, LeftoverConfidence.High, "ярлык программы", size, 1,
                                       NeedsAdmin: !IsUserWritableLocation(lnk)));
            }
        }

        // 4. Ключи реестра Software\<программа> и Software\<издатель>\<программа>.
        foreach (var root in registryRoots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(root.Hive, root.View);
                using var soft = baseKey.OpenSubKey(root.Path);
                if (soft is null) continue;
                var prefix = (root.Hive == RegistryHive.CurrentUser ? "HKCU\\" : "HKLM\\") + root.Path + "\\";
                var admin = root.Hive != RegistryHive.CurrentUser;
                foreach (var name in soft.GetSubKeyNames())
                {
                    var n = Norm(name);
                    if (names.Contains(n))
                        AddKey(prefix + name, root.View, ByName(n), NameReason(n, "ключ реестра с названием программы"), admin);
                    else if (allPublishers.Contains(n))
                    {
                        using var pubKey = soft.OpenSubKey(name);
                        var subs = pubKey?.GetSubKeyNames() ?? [];
                        var progSubs = subs.Where(s => names.Contains(Norm(s))).ToList();
                        var hasValues = (pubKey?.ValueCount ?? 0) > 0;
                        if (!publishers.Contains(n) || (progSubs.Count > 0 && (progSubs.Count < subs.Length || hasValues)))
                            foreach (var s in progSubs)
                                AddKey(prefix + name + "\\" + s, root.View, LeftoverConfidence.High, "ключ программы внутри ключа издателя", admin);
                        else
                            AddKey(prefix + name, root.View, progSubs.Count > 0 ? LeftoverConfidence.High : LeftoverConfidence.Medium,
                                   progSubs.Count > 0 ? "ключ издателя только с этой программой" : "ключ издателя — проверь, не нужен ли он", admin);
                    }
                }
            }
            catch (Exception ex) { OnError?.Invoke(ex); }
        }

        void AddKey(string path, RegistryView view, LeftoverConfidence conf, string reason, bool admin)
        {
            // 32- и 64-битное представление HKCU и общих ключей HKLM может совпадать — не дублируем.
            if (!seen.Add(path + "|" + (path.StartsWith("HKCU") ? "" : view.ToString()))) return;
            found.Add(new Leftover(LeftoverKind.RegistryKey, path, conf, reason, View: view, NeedsAdmin: admin));
        }

        // Вложенное в уже найденную папку — не дублируем.
        var folders = found.Where(f => f.Kind == LeftoverKind.Folder).Select(f => f.Path).ToList();
        found.RemoveAll(f => f.Kind == LeftoverKind.Folder && folders.Any(o => !o.Equals(f.Path, StringComparison.OrdinalIgnoreCase) && IsInside(f.Path, o)));
        return found;
    }

    bool FolderAllowed(string full, IReadOnlyList<string> otherLocations)
    {
        if (!ProgramInventory.IsProgramOwnFolder(full)) return false;
        if (IsSharedRoot(full)) return false;
        foreach (var r in folderRoots) if (Full(r) is { } fr && fr.Equals(full, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var p in protectedFolders ?? []) if (Full(p) is { } pf && (pf.Equals(full, StringComparison.OrdinalIgnoreCase) || IsInside(full, pf) || IsInside(pf, full))) return false;
        try { if (new DirectoryInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint)) return false; } catch { return false; }
        foreach (var o in otherLocations)
            if (o.Equals(full, StringComparison.OrdinalIgnoreCase) || IsInside(o, full) || IsInside(full, o)) return false;
        return true;
    }

    static bool IsUserWritableLocation(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        return IsInside(path, profile);
    }

    public static bool IsInside(string path, string folder) =>
        path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    static string? Full(string p) { try { return Path.GetFullPath(p).TrimEnd('\\'); } catch { return null; } }

    static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : []; }
        catch { return []; }
    }

    static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : []; }
        catch { return []; }
    }

    static readonly EnumerationOptions Deep = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    static (long Size, int Count) Measure(string dir)
    {
        long size = 0; var count = 0;
        try { foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", Deep)) { count++; try { size += f.Length; } catch { } } }
        catch (Exception ex) { OnError?.Invoke(ex); }
        return (size, count);
    }

    /// <summary>В папке нет файлов, только подпапки с названием программы.</summary>
    static bool OnlyProgramFolders(string dir, HashSet<string> names)
    {
        try
        {
            var entries = Directory.EnumerateFileSystemEntries(dir).ToList();
            return entries.Count > 0 && entries.All(e => Directory.Exists(e) && names.Contains(Norm(Path.GetFileName(e))));
        }
        catch { return false; }
    }

    static bool IsEmptyTree(string dir)
    {
        try { return !Directory.EnumerateFiles(dir, "*", Deep).Any(); }
        catch { return false; }
    }
}
