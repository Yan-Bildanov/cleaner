using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Cleaner.Core.Cleaning;

/// <summary>
/// Какие программы держат файл открытым или загруженным (например, DLL расширения Проводника) —
/// через Restart Manager Windows, тот же механизм, которым установщики выясняют, что закрыть перед обновлением.
/// Удалить загруженную DLL нельзя даже администратору: Windows отвечает «Отказано в доступе», а не «файл занят».
/// </summary>
public static class FileLocks
{
    public static Action<Exception>? OnError;

    /// <summary>Понятные имена программ, которые держат файл. Пусто — никто (или узнать не удалось).</summary>
    public static IReadOnlyList<string> WhoUses(string path)
    {
        var result = new List<string>();
        var key = new StringBuilder(CchRmSessionKey + 1);
        if (RmStartSession(out var session, 0, key) != 0) return result;
        try
        {
            if (RmRegisterResources(session, 1, [path], 0, null, 0, null) != 0) return result;
            uint needed = 0, count = 0, reasons = 0;
            var rc = RmGetList(session, out needed, ref count, null, ref reasons);
            if (rc == ErrorMoreData && needed > 0)
            {
                var infos = new RmProcessInfo[needed];
                count = needed;
                rc = RmGetList(session, out needed, ref count, infos, ref reasons);
                if (rc == 0)
                    for (var i = 0; i < count; i++)
                        result.Add(FriendlyName(infos[i]));
            }
        }
        catch (Exception ex) { OnError?.Invoke(ex); }
        finally { RmEndSession(session); }
        return result.Distinct().ToList();
    }

    static string FriendlyName(RmProcessInfo info)
    {
        try
        {
            using var p = Process.GetProcessById(info.Process.dwProcessId);
            if (p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return "Проводник";
        }
        catch { }
        return string.IsNullOrWhiteSpace(info.strAppName) ? $"процесс {info.Process.dwProcessId}" : info.strAppName;
    }

    const int CchRmSessionKey = 32;   // длина GUID в символах
    const int CchRmMaxAppName = 255;
    const int CchRmMaxSvcName = 63;
    const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    struct RmUniqueProcess
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll")]
    static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, RmUniqueProcess[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RmProcessInfo[]? rgAffectedApps, ref uint lpdwRebootReasons);
}
