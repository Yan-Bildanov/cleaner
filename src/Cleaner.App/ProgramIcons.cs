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

    /// <summary>
    /// Порядок: значок из реестра → значок главного .exe в папке программы. Но некоторые программы (Total Commander и др.)
    /// указывают в реестре значок своего деинсталлятора — тогда сначала главный .exe, а значок деинсталлятора — в последнюю очередь.
    /// </summary>
    static IEnumerable<(string File, int Index)> Candidates(InstalledProgram p)
    {
        (string File, int Index)? display = null;
        if (p.DisplayIcon is { } di)
        {
            var s = Environment.ExpandEnvironmentVariables(di.Trim());
            var index = 0;
            var comma = s.LastIndexOf(',');
            if (comma > 1 && int.TryParse(s[(comma + 1)..].Trim(), out var i)) { index = i; s = s[..comma]; }
            display = (s.Trim().Trim('"'), index);
        }

        var uninstallExe = UninstallCommand.For(p)?.FileName;
        var displayIsUninstaller = display is { } d
            && (IsUninstallerFile(d.File) || (uninstallExe is not null && SamePath(d.File, uninstallExe)));

        if (display is { } first && !displayIsUninstaller) yield return first;

        // Папка программы: из реестра, а если её нет — папка, где лежит значок-деинсталлятор.
        var dir = p.InstallLocation;
        if ((dir is null || !Directory.Exists(dir)) && displayIsUninstaller) dir = Path.GetDirectoryName(display!.Value.File);
        if (dir is not null && ProgramInventory.IsProgramOwnFolder(dir) && MainExe(dir, p.Name) is { } main) yield return (main, 0);

        if (display is { } last && displayIsUninstaller) yield return last;      // лучше значок деинсталлятора, чем никакого
    }

    static bool IsUninstallerFile(string path)
    {
        var n = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return n.Contains("unin") || n.Contains("remove") || n.StartsWith("un_") || n.StartsWith("au_") || n is "uninst" or "uninstall";
    }

    static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>
    /// Главный .exe программы в её папке: сначала тот, чьё имя похоже на название программы
    /// («TOTALCMD64.EXE» ↔ «Total Commander»), иначе самый большой — служебные помощники обычно маленькие.
    /// </summary>
    static string? MainExe(string dir, string programName)
    {
        try
        {
            string[] helpers = ["setup", "update", "updater", "crash", "report", "helper", "install", "launcher", "elevate", "notify"];
            var exes = new DirectoryInfo(dir).EnumerateFiles("*.exe")
                .Where(f => !IsUninstallerFile(f.Name))
                .Where(f => !helpers.Any(h => f.Name.Contains(h, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (exes.Count == 0) return null;

            var words = LeftoverScanner.StripVersion(programName).Split(' ', '-', '_', '(', ')')
                .Select(LeftoverScanner.Norm).Where(w => w.Length >= 3).ToList();
            var byName = exes.Where(f =>
            {
                var n = LeftoverScanner.Norm(Path.GetFileNameWithoutExtension(f.Name));
                return words.Any(w => n.Contains(w) || w.Contains(n.Length >= 4 ? n : "\0"))
                       || (words.Count >= 2 && n.StartsWith(words[0][..Math.Min(5, words[0].Length)]) && n.Contains(words[1][..Math.Min(3, words[1].Length)]));
            }).OrderByDescending(f => f.Length).FirstOrDefault();
            return (byName ?? exes.OrderByDescending(f => f.Length).First()).FullName;
        }
        catch { return null; }
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
