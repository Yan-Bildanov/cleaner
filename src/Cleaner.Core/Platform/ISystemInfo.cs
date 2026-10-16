using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Cleaner.Core.Platform;

/// <summary>Всё, что сканер узнаёт о системе. Интерфейс — чтобы в тестах подменять.</summary>
public interface ISystemInfo
{
    DateTime UtcNow { get; }
    bool IsElevated { get; }
    bool IsProcessRunning(string processName);
    (long Bytes, long Items) QueryRecycleBin();
    /// <summary>Очистить корзину на всех дисках БЕЗВОЗВРАТНО. Вызывать только после подтверждения пользователя.</summary>
    bool EmptyRecycleBin();
}

public sealed class WindowsSystemInfo : ISystemInfo
{
    public DateTime UtcNow => DateTime.UtcNow;

    public bool IsElevated { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public bool IsProcessRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    public (long Bytes, long Items) QueryRecycleBin()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        var hr = SHQueryRecycleBin(null, ref info);   // null = все диски
        return hr == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    public bool EmptyRecycleBin()
    {
        const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;
        // Подтверждение уже показали в нашем окне, поэтому системное — без вопросов и без звука.
        return SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND) == 0;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);
}
