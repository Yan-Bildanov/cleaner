using System.Text.RegularExpressions;

namespace Cleaner.Core.Programs;

/// <summary>Команда деинсталлятора, разобранная на файл и аргументы.</summary>
public sealed partial record UninstallCommand(string FileName, string Arguments)
{
    /// <summary>
    /// Команда удаления программы. Для пакетов Windows Installer — всегда «msiexec /x {код}»:
    /// у многих из них UninstallString содержит «/I» (это «изменить», а не «удалить»).
    /// </summary>
    public static UninstallCommand? For(InstalledProgram p)
    {
        if (p.MsiProductCode is { } code) return new("msiexec.exe", "/x " + code);
        return p.UninstallString is { } raw ? Parse(raw) : null;
    }

    /// <summary>
    /// Строка из реестра → файл + аргументы. Встречается всё:
    /// <c>"C:\Program Files\X\unins000.exe" /SILENT</c>, <c>C:\Program Files\X\uninstall.exe /S</c> (без кавычек, с пробелами),
    /// <c>MsiExec.exe /I{GUID}</c>, <c>rundll32.exe dfshim.dll,ShArpMaintain ...</c>.
    /// </summary>
    public static UninstallCommand? Parse(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0) return null;

        string file, args;
        if (s[0] == '"')
        {
            var close = s.IndexOf('"', 1);
            if (close < 0) { file = s.Trim('"'); args = ""; }
            else { file = s[1..close]; args = s[(close + 1)..].Trim(); }
        }
        else
        {
            // Без кавычек: файл заканчивается на первом «.exe», за которым пробел или конец строки.
            var m = ExeEnd().Match(s);
            if (m.Success) { file = s[..(m.Index + 4)]; args = s[(m.Index + 4)..].Trim(); }
            else
            {
                var sp = s.IndexOf(' ');
                (file, args) = sp < 0 ? (s, "") : (s[..sp], s[(sp + 1)..].Trim());
            }
        }

        file = Environment.ExpandEnvironmentVariables(file.Trim());
        if (file.Length == 0) return null;

        // msiexec /I{GUID} — это «установить/изменить». Для удаления нужен /X.
        if (Path.GetFileNameWithoutExtension(file).Equals("msiexec", StringComparison.OrdinalIgnoreCase))
            args = MsiInstallSwitch().Replace(args, "/X$1");

        return new(file, args);
    }

    [GeneratedRegex(@"\.exe(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ExeEnd();

    [GeneratedRegex(@"/I\s*(\{[0-9A-Fa-f\-]{36}\})", RegexOptions.IgnoreCase)]
    private static partial Regex MsiInstallSwitch();
}
