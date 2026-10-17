using System.Text.Json;
using Cleaner.Core.Cleaning;

namespace Cleaner.Core.Programs;

/// <summary>
/// Передача хвостов, которым нужны права администратора, во второй экземпляр Cleaner.exe, запущенный через UAC:
/// окно остаётся обычным, а повышенный процесс делает только эту работу и закрывается.
/// «Cleaner.exe --remove-leftovers plan.json» → результат в plan.json.result.
/// </summary>
public sealed record LeftoverPlan(string Label, List<Leftover> Items, bool KillProcesses = false)
{
    public const string Argument = "--remove-leftovers";

    public static string Save(LeftoverPlan plan)
    {
        var dir = Path.Combine(AppPaths.DataDir, "tmp");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"leftovers-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(plan));
        return file;
    }

    public static string ResultPath(string planFile) => planFile + ".result";

    /// <summary>Выполняется в повышенном процессе.</summary>
    public static void Execute(string planFile, QuarantineStore store)
    {
        var plan = JsonSerializer.Deserialize<LeftoverPlan>(File.ReadAllText(planFile))!;
        // Файл плана мог подменить кто угодно с правами пользователя — повторно проверяем каждый пункт
        // теми же правилами, что и при поиске: только «свои» папки программ, только ключи Software\<имя>.
        var safe = plan.Items.Where(IsStillSafe).ToList();
        var report = LeftoverRemover.Remove(store, safe, isElevated: true, plan.Label, plan.KillProcesses);
        File.WriteAllText(ResultPath(planFile), JsonSerializer.Serialize(report));
    }

    public static LeftoverRemoveReport? ReadResult(string planFile)
    {
        try
        {
            var r = ResultPath(planFile);
            return File.Exists(r) ? JsonSerializer.Deserialize<LeftoverRemoveReport>(File.ReadAllText(r)) : null;
        }
        finally
        {
            try { File.Delete(planFile); } catch { }
            try { File.Delete(ResultPath(planFile)); } catch { }
        }
    }

    static bool IsStillSafe(Leftover l) => l.Kind switch
    {
        LeftoverKind.Folder => ProgramInventory.IsProgramOwnFolder(l.Path) && !LeftoverScanner.IsInside(AppPaths.DataDir, l.Path),
        LeftoverKind.Shortcut => l.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(l.Path),
        LeftoverKind.RegistryKey => IsSoftwareSubkey(l.Path),
        LeftoverKind.Service => SystemServices.IsSafeToRemove(l.Path),
        LeftoverKind.ScheduledTask => l.Path.StartsWith('\\') && !l.Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>
    /// Только «HKxx\SOFTWARE\Имя» или «HKxx\SOFTWARE\Издатель\Имя», и не системные ветки.
    /// Единственное исключение — сама запись программы в списке установленных
    /// (…\Microsoft\Windows\CurrentVersion\Uninstall\Ключ): её убирает принудительное удаление.
    /// </summary>
    public static bool IsSoftwareSubkey(string path)
    {
        if (IsUninstallEntry(path)) return true;
        var parts = path.Split('\\');
        if (parts.Length is < 3 or > 4) return false;
        if (parts[0] is not ("HKCU" or "HKLM")) return false;
        if (!parts[1].Equals("Software", StringComparison.OrdinalIgnoreCase)) return false;
        string[] forbidden = ["Microsoft", "Classes", "Policies", "Wow6432Node", "Clients", "RegisteredApplications", "Windows"];
        return !forbidden.Contains(parts[2], StringComparer.OrdinalIgnoreCase) && parts.Skip(2).All(p => p.Length > 0);
    }

    public static bool IsUninstallEntry(string path)
    {
        foreach (var hive in new[] { "HKLM\\", "HKCU\\" })
        {
            var prefix = hive + ProgramInventory.UninstallKey + "\\";
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = path[prefix.Length..];
                return rest.Length > 0 && !rest.Contains('\\');
            }
        }
        return false;
    }
}
