using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Cleaner.Core.Programs;

/// <summary>
/// Список установленных программ — те же три ветки реестра, что читает «Установка и удаление программ».
/// Только чтение. Обновления, системные компоненты и записи без деинсталлятора отбрасываются.
/// </summary>
public static partial class ProgramInventory
{
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string UninstallKey32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    public static Action<Exception>? OnError;

    static readonly HashSet<string> UpdateReleaseTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Security Update", "Update Rollup", "Hotfix", "Update", "ServicePack" };

    public static IReadOnlyList<InstalledProgram> ReadAll()
    {
        var list = new List<InstalledProgram>();
        Read(ProgramScope.Machine64, list);
        if (Environment.Is64BitOperatingSystem) Read(ProgramScope.Machine32, list);
        Read(ProgramScope.User, list);
        return Deduplicate(list).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Есть ли ещё запись о программе в реестре — так проверяем, что удаление прошло.</summary>
    public static bool Exists(InstalledProgram p)
    {
        try
        {
            using var root = OpenRoot(p.Scope);
            using var key = root?.OpenSubKey(p.RegistryKeyName);
            return key is not null;
        }
        catch (Exception ex) { OnError?.Invoke(ex); return true; }   // не знаем — считаем, что ещё стоит
    }

    static RegistryKey? OpenRoot(ProgramScope scope) => scope switch
    {
        ProgramScope.Machine64 => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(UninstallKey),
        ProgramScope.Machine32 => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(UninstallKey),
        _ => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default).OpenSubKey(UninstallKey),
    };

    static void Read(ProgramScope scope, List<InstalledProgram> into)
    {
        try
        {
            using var root = OpenRoot(scope);
            if (root is null) return;
            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var key = root.OpenSubKey(name);
                    if (key is null) continue;
                    var p = FromValues(name, scope, v => key.GetValue(v), KeyLastWriteTime(key));
                    if (p is not null) into.Add(p);
                }
                catch (Exception ex) { OnError?.Invoke(ex); }
            }
        }
        catch (Exception ex) { OnError?.Invoke(ex); }
    }

    /// <summary>
    /// Запись реестра → программа. null — запись не показываем (обновление, системный компонент, без имени
    /// или без способа удаления). Отдельно от реестра, чтобы покрыть тестами.
    /// </summary>
    public static InstalledProgram? FromValues(string keyName, ProgramScope scope, Func<string, object?> get, DateTime? keyWritten = null)
    {
        var name = Str(get("DisplayName"));
        if (name is null) return null;
        if (Int(get("SystemComponent")) == 1) return null;
        if (Str(get("ParentKeyName")) is not null || Str(get("ParentDisplayName")) is not null) return null;
        if (Str(get("ReleaseType")) is { } rt && UpdateReleaseTypes.Contains(rt)) return null;

        string? msi = Int(get("WindowsInstaller")) == 1 && GuidKey().IsMatch(keyName) ? keyName.ToUpperInvariant() : null;
        var uninstall = Str(get("UninstallString"));
        if (msi is null && uninstall is null) return null;

        var size = Int(get("EstimatedSize")) is int kb and > 0 ? (long)kb * 1024 : (long?)null;

        return new InstalledProgram
        {
            Name = name,
            Publisher = Str(get("Publisher")),
            Version = Str(get("DisplayVersion")),
            InstallDate = ParseDate(Str(get("InstallDate"))) ?? keyWritten,
            SizeBytes = size,
            InstallLocation = CleanPath(Str(get("InstallLocation"))),
            DisplayIcon = Str(get("DisplayIcon")),
            UninstallString = uninstall,
            QuietUninstallString = Str(get("QuietUninstallString")),
            MsiProductCode = msi,
            NoRemove = Int(get("NoRemove")) == 1,
            Scope = scope,
            RegistryKeyName = keyName,
        };
    }

    /// <summary>Одна и та же программа часто записана и в 64-, и в 32-битную ветку — оставляем одну.</summary>
    public static IEnumerable<InstalledProgram> Deduplicate(IEnumerable<InstalledProgram> programs) =>
        programs.GroupBy(p => (p.Name.Trim().ToLowerInvariant(), (p.Version ?? "").ToLowerInvariant(), (p.Publisher ?? "").ToLowerInvariant()))
                .Select(g => g.First());

    /// <summary>InstallDate в реестре — строка «yyyyMMdd» (иногда с мусором вокруг).</summary>
    public static DateTime? ParseDate(string? s)
    {
        if (s is null) return null;
        var digits = new string(s.Where(char.IsDigit).ToArray());
        return digits.Length == 8 && DateTime.TryParseExact(digits, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
               && d.Year >= 1995 && d <= DateTime.Now.AddDays(1)
            ? d : null;
    }

    static string? CleanPath(string? s) => s?.Trim().Trim('"').TrimEnd('\\') is { Length: > 0 } t ? t : null;

    static string? Str(object? v) => v switch
    {
        string s when !string.IsNullOrWhiteSpace(s) => s.Trim(),
        _ => null,
    };

    static int? Int(object? v) => v switch
    {
        int i => i,
        long l => (int)l,
        string s when int.TryParse(s, out var i) => i,
        _ => null,
    };

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")]
    private static partial Regex GuidKey();

    // ── Дата изменения ключа: когда установщик не записал InstallDate ───────────────────────────
    [DllImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW")]
    private static extern int RegQueryInfoKey(SafeRegistryHandle hKey, IntPtr lpClass, IntPtr lpcchClass, IntPtr lpReserved,
        IntPtr lpcSubKeys, IntPtr lpcbMaxSubKeyLen, IntPtr lpcbMaxClassLen, IntPtr lpcValues, IntPtr lpcbMaxValueNameLen,
        IntPtr lpcbMaxValueLen, IntPtr lpcbSecurityDescriptor, out long lpftLastWriteTime);

    static DateTime? KeyLastWriteTime(RegistryKey key)
    {
        try
        {
            return RegQueryInfoKey(key.Handle, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, out var ft) == 0 && ft > 0
                ? DateTime.FromFileTime(ft).Date : null;
        }
        catch { return null; }
    }

    // ── Размер по папке: когда установщик не записал EstimatedSize ───────────────────────────────
    /// <summary>
    /// Можно ли считать размер папки как размер программы: это должна быть собственная папка программы,
    /// а не корень диска, Program Files, Windows или профиль пользователя.
    /// </summary>
    public static bool IsProgramOwnFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\'); } catch { return false; }

        var root = Path.GetPathRoot(full)?.TrimEnd('\\');
        if (root is null || full.Equals(root, StringComparison.OrdinalIgnoreCase)) return false;

        string[] shared =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        ];
        foreach (var s in shared)
        {
            if (string.IsNullOrEmpty(s)) continue;
            var sh = s.TrimEnd('\\');
            if (full.Equals(sh, StringComparison.OrdinalIgnoreCase)) return false;
            // Папка Windows и всё внутри — не «папка программы».
            if (sh.Equals(Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                && full.StartsWith(sh + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return Directory.Exists(full);
    }

    /// <summary>Размер папки (без обхода ссылок/junction). Только чтение.</summary>
    public static long MeasureFolder(string path, CancellationToken ct = default)
    {
        long total = 0;
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        try
        {
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", opts))
            {
                ct.ThrowIfCancellationRequested();
                try { total += f.Length; } catch { }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { OnError?.Invoke(ex); }
        return total;
    }
}
