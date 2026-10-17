using Microsoft.Win32;

namespace Cleaner.Core.Programs;

/// <summary>
/// Принудительное удаление — когда деинсталлятор программы пропал или не работает.
/// Деинсталлятор не запускается: программа удаляется как набор «хвостов» (папки, ярлыки, ключи, службы, задачи)
/// плюс её запись в списке установленных. Всё — через карантин, всё можно вернуть.
/// Перед удалением закрываются процессы, запущенные из её папок.
/// </summary>
public static class ForcedUninstall
{
    public static IReadOnlyList<Leftover> Plan(InstalledProgram program, IReadOnlyList<InstalledProgram> all)
    {
        if (program.IsStoreApp) return [];
        var others = all.Where(o => !(o.Scope == program.Scope && o.RegistryKeyName == program.RegistryKeyName)).ToList();
        var found = LeftoverScanner.ForCurrentSystem().Scan(program, others).ToList();
        found.Add(UninstallEntry(program));
        return found;
    }

    public static Leftover UninstallEntry(InstalledProgram p)
    {
        var hive = p.Scope == ProgramScope.User ? "HKCU\\" : "HKLM\\";
        var view = p.Scope switch
        {
            ProgramScope.Machine32 => RegistryView.Registry32,
            ProgramScope.Machine64 => RegistryView.Registry64,
            _ => RegistryView.Default,
        };
        return new Leftover(LeftoverKind.RegistryKey, hive + ProgramInventory.UninstallKey + "\\" + p.RegistryKeyName,
            LeftoverConfidence.High, "запись программы в списке установленных", View: view, NeedsAdmin: p.Scope != ProgramScope.User);
    }

    /// <summary>Какие процессы придётся закрыть — показываем пользователю до подтверждения.</summary>
    public static IReadOnlyList<ProgramProcesses.Running> ProcessesToClose(IEnumerable<Leftover> plan) =>
        ProgramProcesses.Inside(plan.Where(l => l.Kind == LeftoverKind.Folder).Select(l => l.Path).ToList());
}
