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
        if (p.IsStoreApp) return LoadStoreLogo(p.LogoPath);
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

    /// <summary>
    /// Логотип приложения Store. В манифесте указан «Assets\StoreLogo.png», а на диске лежат варианты
    /// «StoreLogo.scale-100.png», «…scale-200.png» и т. п. — берём подходящий по размеру.
    /// </summary>
    static ImageSource? LoadStoreLogo(string? logo)
    {
        if (string.IsNullOrEmpty(logo)) return null;
        try
        {
            var file = logo;
            if (!File.Exists(file))
            {
                var dir = Path.GetDirectoryName(logo);
                var stem = Path.GetFileNameWithoutExtension(logo);
                if (dir is null || !Directory.Exists(dir)) return null;
                file = Directory.EnumerateFiles(dir, stem + ".*" + Path.GetExtension(logo))
                    .OrderBy(f => f.Contains("scale-100") ? 0 : f.Contains("scale-200") ? 1 : f.Contains("targetsize-48") ? 2 : 3)
                    .FirstOrDefault(f => !f.Contains("contrast-", StringComparison.OrdinalIgnoreCase));
                if (file is null) return null;
            }
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 64;
            img.UriSource = new Uri(file);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
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
