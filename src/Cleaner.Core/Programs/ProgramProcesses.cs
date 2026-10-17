using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Cleaner.Core.Programs;

/// <summary>Запущенные процессы, чей .exe лежит в папке программы. Нужны для принудительного удаления.</summary>
public static class ProgramProcesses
{
    public static Action<Exception>? OnError;

    public sealed record Running(int Id, string Name, string Path);

    public static IReadOnlyList<Running> Inside(IReadOnlyCollection<string> folders)
    {
        var result = new List<Running>();
        if (folders.Count == 0) return result;
        var self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == self || p.Id <= 4) continue;
                var path = ImagePath(p.Id);
                if (path is null || SystemServices.IsSystemLocation(path)) continue;     // системные процессы — никогда
                if (folders.Any(f => LeftoverScanner.IsInside(path, f))) result.Add(new Running(p.Id, p.ProcessName, path));
            }
        }
        return result;
    }

    /// <summary>Закрыть процессы программы. Возвращает число закрытых.</summary>
    public static int KillInside(IReadOnlyCollection<string> folders)
    {
        var killed = 0;
        foreach (var r in Inside(folders))
        {
            try
            {
                using var p = Process.GetProcessById(r.Id);
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
                killed++;
            }
            catch (Exception ex) { OnError?.Invoke(ex); }
        }
        if (killed > 0) Thread.Sleep(500);       // дать Windows отпустить файлы
        return killed;
    }

    /// <summary>Путь к .exe процесса — работает и для процессов с правами администратора (QueryFullProcessImageName).</summary>
    static string? ImagePath(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);
}
