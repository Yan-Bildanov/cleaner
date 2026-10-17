using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace Cleaner.Core.Cleaning;

public enum RegistryMoveOutcome { Moved, Missing, Denied, ExportFailed }
public enum RegistryRestoreOutcome { Restored, AlreadyExists, Failed }

/// <summary>
/// Ключи реестра в карантине: сначала экспорт в .reg штатным reg.exe, только потом удаление.
/// Возврат — импорт того же .reg. Путь ключа — «HKLM\SOFTWARE\Имя» или «HKCU\Software\Имя».
/// </summary>
public static class RegistryBackup
{
    public static Action<Exception>? OnError;

    static string RegExe => Path.Combine(Environment.SystemDirectory, "reg.exe");

    public static string KindFor(string keyPath, RegistryView view) =>
        IsUser(keyPath) ? QuarantineEntry.KindRegUser
        : view == RegistryView.Registry32 ? QuarantineEntry.KindReg32 : QuarantineEntry.KindReg64;

    static bool IsUser(string keyPath) => keyPath.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase);

    static RegistryView ViewOf(string kind) => kind == QuarantineEntry.KindReg32 ? RegistryView.Registry32 : RegistryView.Registry64;

    static (RegistryHive Hive, string Sub) Split(string keyPath)
    {
        var i = keyPath.IndexOf('\\');
        var hive = keyPath[..i].ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            _ => throw new ArgumentException("Поддерживаются только HKCU и HKLM: " + keyPath),
        };
        return (hive, keyPath[(i + 1)..].TrimEnd('\\'));
    }

    public static bool Exists(string keyPath, RegistryView view)
    {
        var (hive, sub) = Split(keyPath);
        using var b = RegistryKey.OpenBaseKey(hive, hive == RegistryHive.CurrentUser ? RegistryView.Default : view);
        using var k = b.OpenSubKey(sub);
        return k is not null;
    }

    public static RegistryMoveOutcome ExportAndDelete(string keyPath, RegistryView view, string toFile)
    {
        try
        {
            var (hive, sub) = Split(keyPath);
            var v = hive == RegistryHive.CurrentUser ? RegistryView.Default : view;
            using var baseKey = RegistryKey.OpenBaseKey(hive, v);

            // Права на изменение проверяем до экспорта.
            try
            {
                using var probe = baseKey.OpenSubKey(sub, writable: true);
                if (probe is null) return RegistryMoveOutcome.Missing;
            }
            catch (Exception e) when (e is SecurityException or UnauthorizedAccessException) { return RegistryMoveOutcome.Denied; }

            var viewArg = hive == RegistryHive.CurrentUser || view != RegistryView.Registry32 ? "/reg:64" : "/reg:32";
            if (!RunReg($"export \"{keyPath}\" \"{toFile}\" /y {viewArg}") || !File.Exists(toFile) || new FileInfo(toFile).Length == 0)
                return RegistryMoveOutcome.ExportFailed;

            var parentPath = Path.GetDirectoryName(sub)!;    // «SOFTWARE\Имя» → «SOFTWARE»
            var name = Path.GetFileName(sub);
            try
            {
                using var parent = baseKey.OpenSubKey(parentPath, writable: true);
                if (parent is null) return RegistryMoveOutcome.Missing;
                parent.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                return RegistryMoveOutcome.Moved;
            }
            catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
            {
                // Удалилось частично — возвращаем всё из только что сделанной копии.
                RunReg($"import \"{toFile}\" {viewArg}");
                OnError?.Invoke(e);
                return RegistryMoveOutcome.Denied;
            }
        }
        catch (Exception e)
        {
            OnError?.Invoke(e);
            return RegistryMoveOutcome.ExportFailed;
        }
    }

    /// <summary>Только экспорт ключа в .reg (для служб: сам ключ потом удаляет sc delete).</summary>
    public static bool Export(string keyPath, RegistryView view, string toFile)
    {
        var viewArg = IsUser(keyPath) || view != RegistryView.Registry32 ? "/reg:64" : "/reg:32";
        return RunReg($"export \"{keyPath}\" \"{toFile}\" /y {viewArg}") && File.Exists(toFile) && new FileInfo(toFile).Length > 0;
    }

    public static RegistryRestoreOutcome Restore(string keyPath, string kind, string file)
    {
        try
        {
            var view = ViewOf(kind);
            if (Exists(keyPath, view)) return RegistryRestoreOutcome.AlreadyExists;
            if (!File.Exists(file)) return RegistryRestoreOutcome.Failed;
            var viewArg = kind == QuarantineEntry.KindReg32 ? "/reg:32" : "/reg:64";
            return RunReg($"import \"{file}\" {viewArg}") && Exists(keyPath, view)
                ? RegistryRestoreOutcome.Restored : RegistryRestoreOutcome.Failed;
        }
        catch (Exception e)
        {
            OnError?.Invoke(e);
            return RegistryRestoreOutcome.Failed;
        }
    }

    static bool RunReg(string args)
    {
        using var p = Process.Start(new ProcessStartInfo(RegExe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        if (!p.WaitForExit(30_000)) { try { p.Kill(); } catch { } return false; }
        return p.ExitCode == 0;
    }
}
