using System.ComponentModel;
using System.Diagnostics;

namespace Cleaner.Core.Programs;

public enum UninstallOutcome
{
    /// <summary>Записи о программе больше нет — удалена.</summary>
    Removed,
    /// <summary>Деинсталлятор закрылся, а программа осталась (отменили в его окне или он завершился с ошибкой).</summary>
    StillInstalled,
    /// <summary>Отказались в окне UAC или в самом деинсталляторе.</summary>
    Cancelled,
    /// <summary>Деинсталлятор не запустился.</summary>
    Failed,
}

public sealed record UninstallResult(UninstallOutcome Outcome, string Message, int? ExitCode = null);

/// <summary>
/// Штатное удаление: запускает собственный деинсталлятор программы и ждёт, пока он закончит.
/// Сам ничего не удаляет — удаляет только деинсталлятор программы.
/// </summary>
public static class Uninstaller
{
    public static Action<Exception>? OnError;

    /// <summary>Как долго ждём, если не видно ни деинсталлятора, ни изменений: после этого считаем, что окно закрыли.</summary>
    static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(4);

    /// <param name="quiet">Удалить без вопросов, если для этого деинсталлятора известен тихий режим (<see cref="UninstallCommand.Quiet"/>).</param>
    public static async Task<UninstallResult> RunAsync(InstalledProgram program, IProgress<string>? progress = null,
                                                       CancellationToken stopWaiting = default, bool quiet = false)
    {
        if (program.Scope == ProgramScope.Store) return await StoreApps.RemoveAsync(program, progress);

        var quietCmd = quiet ? UninstallCommand.Quiet(program) : null;
        var cmd = quietCmd ?? UninstallCommand.For(program);
        if (cmd is null || !program.CanUninstall)
            return new(UninstallOutcome.Failed, "У программы нет деинсталлятора (или установщик запретил удаление).");
        if (UninstallCommand.IsBroken(program))
            return new(UninstallOutcome.Failed, "Деинсталлятор программы не найден на диске — её можно удалить только принудительно.");

        var before = Snapshot();
        var started = DateTime.Now;
        Process? proc;
        try
        {
            proc = Process.Start(new ProcessStartInfo(cmd.FileName, cmd.Arguments)
            {
                UseShellExecute = true,   // деинсталлятор сам попросит права администратора, если нужны
                WorkingDirectory = SafeDir(cmd.FileName),
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new(UninstallOutcome.Cancelled, "Удаление отменено в окне контроля учётных записей.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3)
        {
            return new(UninstallOutcome.Failed, $"Деинсталлятор не найден: {cmd.FileName}. Похоже, программа уже частично удалена.");
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex);
            return new(UninstallOutcome.Failed, "Не удалось запустить деинсталлятор: " + ex.Message);
        }

        progress?.Report(quietCmd is not null
            ? "Удаляю в тихом режиме — вопросов не будет, может мелькнуть окно с прогрессом."
            : "Открылся деинсталлятор программы — пройди его шаги в его окне.");

        int? exitCode = null;
        if (proc is not null)
        {
            try
            {
                await proc.WaitForExitAsync(stopWaiting);
                try { exitCode = proc.ExitCode; } catch { }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { OnError?.Invoke(ex); }
        }

        // Деинсталляторы NSIS и Inno Setup копируют себя во временную папку, запускают копию и сразу выходят.
        // Поэтому после выхода основного процесса ждём, пока исчезнет запись о программе
        // или пока не останется ни одного запущенного нами процесса-деинсталлятора.
        var quietSince = DateTime.Now;
        while (!stopWaiting.IsCancellationRequested && ProgramInventory.Exists(program))
        {
            if (UninstallerStillRunning(before, started)) quietSince = DateTime.Now;
            else if (DateTime.Now - quietSince > QuietPeriod) break;

            progress?.Report("Жду, пока деинсталлятор закончит работу…");
            try { await Task.Delay(700, stopWaiting); } catch (OperationCanceledException) { break; }
        }

        if (!ProgramInventory.Exists(program))
            return new(UninstallOutcome.Removed, $"«{program.Name}» удалена.", exitCode);

        // 1602 / 1223 — «отменено пользователем» у Windows Installer и у Windows.
        if (exitCode is 1602 or 1223)
            return new(UninstallOutcome.Cancelled, "Удаление отменено в окне деинсталлятора.", exitCode);

        return new(UninstallOutcome.StillInstalled,
            exitCode is int code and not 0
                ? $"Деинсталлятор завершился с кодом {code}, программа осталась в списке установленных."
                : "Программа осталась в списке установленных — похоже, удаление отменили или его нужно завершить после перезагрузки.",
            exitCode);
    }

    static string SafeDir(string file)
    {
        try { return Path.GetDirectoryName(file) is { Length: > 0 } d && Directory.Exists(d) ? d : Environment.SystemDirectory; }
        catch { return Environment.SystemDirectory; }
    }

    static HashSet<int> Snapshot()
    {
        try { return Process.GetProcesses().Select(p => p.Id).ToHashSet(); }
        catch { return []; }
    }

    /// <summary>Типичные имена временных копий деинсталляторов (NSIS, Inno Setup, InstallShield, Windows Installer).</summary>
    public static bool LooksLikeUninstaller(string processName)
    {
        var n = processName.ToLowerInvariant();
        return n.StartsWith("un_") || n.StartsWith("au_") || n.StartsWith("_iu") || n.StartsWith("unins")
            || n.StartsWith("uninst") || n.Contains("uninstall") || n is "un_a" or "un_b" or "msiexec" or "setup" or "_isdel"
            || n.StartsWith("is-") || n.EndsWith(".tmp");
    }

    static bool UninstallerStillRunning(HashSet<int> before, DateTime started)
    {
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (before.Contains(p.Id) || !LooksLikeUninstaller(p.ProcessName)) continue;
                    // msiexec живёт постоянно как служба — считаем только запущенные после нас.
                    try { if (p.StartTime < started.AddSeconds(-1)) continue; } catch { /* чужой процесс с правами админа: время не прочитать */ }
                    return true;
                }
            }
        }
        catch (Exception ex) { OnError?.Invoke(ex); }
        return false;
    }
}
