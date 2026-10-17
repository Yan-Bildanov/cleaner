using System.Diagnostics;
using System.IO;
using Cleaner.Core.Programs;

namespace Cleaner.App;

/// <summary>Удаление хвостов с правами администратора: второй экземпляр Cleaner.exe через UAC делает только эту работу.</summary>
public static class Elevation
{
    /// <summary>Запускает Cleaner.exe --remove-leftovers через UAC и ждёт. null — пользователь отказался.</summary>
    public static async Task<LeftoverRemoveReport?> RunAsync(LeftoverPlan plan)
    {
        var file = LeftoverPlan.Save(plan);
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"{LeftoverPlan.Argument} \"{file}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            if (proc is null) return null;
            await proc.WaitForExitAsync();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            try { File.Delete(file); } catch { }
            return null;
        }
        return LeftoverPlan.ReadResult(file);
    }

    /// <summary>Всё, что можно, — в этом процессе; остальное — через UAC. AdminCancelled: пользователь отказался в окне UAC.</summary>
    public static async Task<(LeftoverRemoveReport Report, bool AdminCancelled)> RemoveAsync(IReadOnlyList<Leftover> items, string label, bool killProcesses = false)
    {
        var elevated = App.SystemInfo.IsElevated;
        var report = await Task.Run(() => LeftoverRemover.Remove(App.Quarantine, items, elevated, label, killProcesses));
        if (report.NeedAdmin.Count == 0) return (report, false);

        var admin = await RunAsync(new LeftoverPlan(label, report.NeedAdmin.ToList(), killProcesses));
        if (admin is null) return (report, true);
        return (new LeftoverRemoveReport(report.Removed + admin.Removed, report.RemovedBytes + admin.RemovedBytes,
                                         report.Locked + admin.Locked, report.Denied + admin.Denied,
                                         report.BatchId ?? admin.BatchId, admin.NeedAdmin), false);
    }
}
