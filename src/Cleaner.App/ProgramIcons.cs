using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cleaner.Core.Programs;

namespace Cleaner.App;

/// <summary>Значок программы из DisplayIcon («путь» или «путь,индекс») или из её папки. Можно вызывать не из UI-потока.</summary>
public static class ProgramIcons
{
    public static ImageSource? Load(InstalledProgram p)
    {
        foreach (var (file, index) in Candidates(p))
        {
            var img = Extract(file, index);
            if (img is not null) return img;
        }
        return null;
    }

    static IEnumerable<(string File, int Index)> Candidates(InstalledProgram p)
    {
        if (p.DisplayIcon is { } di)
        {
            var s = Environment.ExpandEnvironmentVariables(di.Trim());
            var index = 0;
            var comma = s.LastIndexOf(',');
            if (comma > 1 && int.TryParse(s[(comma + 1)..].Trim(), out var i)) { index = i; s = s[..comma]; }
            yield return (s.Trim().Trim('"'), index);
        }
        // Нет значка в реестре — берём первый .exe в папке программы.
        if (p.InstallLocation is { } dir && ProgramInventory.IsProgramOwnFolder(dir))
        {
            string? exe = null;
            try { exe = Directory.EnumerateFiles(dir, "*.exe").FirstOrDefault(f => !Path.GetFileName(f).StartsWith("unins", StringComparison.OrdinalIgnoreCase)); }
            catch { }
            if (exe is not null) yield return (exe, 0);
        }
    }

    static ImageSource? Extract(string file, int index)
    {
        if (!File.Exists(file)) return null;
        var large = new IntPtr[1];
        try
        {
            if (ExtractIconEx(file, index, large, null, 1) < 1 || large[0] == IntPtr.Zero) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { if (large[0] != IntPtr.Zero) DestroyIcon(large[0]); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);
}
