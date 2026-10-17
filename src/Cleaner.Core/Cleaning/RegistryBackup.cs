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

    // ── Отдельные значения (автозагрузка Run/RunOnce) ────────────────────────────────────────────
    public sealed record SavedValue(string Key, RegistryView View, string Name, RegistryValueKind Kind,
                                    string? Text = null, string[]? Lines = null, long Number = 0, string? Base64 = null);

    static RegistryKey OpenBase(string keyPath, RegistryView view, out string sub)
    {
        var (hive, s) = Split(keyPath);
        sub = s;
        return RegistryKey.OpenBaseKey(hive, hive == RegistryHive.CurrentUser ? RegistryView.Default : view);
    }

    public static bool ValueExists(string keyPath, RegistryView view, string name)
    {
        try
        {
            using var b = OpenBase(keyPath, view, out var sub);
            using var k = b.OpenSubKey(sub);
            return k?.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) == true;
        }
        catch { return false; }
    }

    /// <summary>Сохранить значение (имя, тип, данные) в JSON, затем удалить его из ключа.</summary>
    public static RegistryMoveOutcome SaveValueAndDelete(string keyPath, RegistryView view, string name, string toFile)
    {
        try
        {
            using var b = OpenBase(keyPath, view, out var sub);
            using var k = b.OpenSubKey(sub, writable: true);
            if (k is null || !k.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return RegistryMoveOutcome.Missing;
            var kind = k.GetValueKind(name);
            var data = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var saved = data switch
            {
                string s => new SavedValue(keyPath, view, name, kind, Text: s),
                string[] lines => new SavedValue(keyPath, view, name, kind, Lines: lines),
                int i => new SavedValue(keyPath, view, name, kind, Number: i),
                long l => new SavedValue(keyPath, view, name, kind, Number: l),
                byte[] bytes => new SavedValue(keyPath, view, name, kind, Base64: Convert.ToBase64String(bytes)),
                _ => new SavedValue(keyPath, view, name, kind),
            };
            File.WriteAllText(toFile, System.Text.Json.JsonSerializer.Serialize(saved));
            k.DeleteValue(name, throwOnMissingValue: false);
            return RegistryMoveOutcome.Moved;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException) { return RegistryMoveOutcome.Denied; }
        catch (Exception e) { OnError?.Invoke(e); return RegistryMoveOutcome.ExportFailed; }
    }

    public static RegistryRestoreOutcome RestoreValue(string file)
    {
        try
        {
            var v = System.Text.Json.JsonSerializer.Deserialize<SavedValue>(File.ReadAllText(file))!;
            using var b = OpenBase(v.Key, v.View, out var sub);
            using var k = b.CreateSubKey(sub, writable: true);
            if (k.GetValueNames().Contains(v.Name, StringComparer.OrdinalIgnoreCase)) return RegistryRestoreOutcome.AlreadyExists;
            object data = v.Kind switch
            {
                RegistryValueKind.MultiString => v.Lines ?? [],
                RegistryValueKind.DWord => (int)v.Number,
                RegistryValueKind.QWord => v.Number,
                RegistryValueKind.Binary => Convert.FromBase64String(v.Base64 ?? ""),
                _ => v.Text ?? "",
            };
            k.SetValue(v.Name, data, v.Kind);
            return RegistryRestoreOutcome.Restored;
        }
        catch (Exception e) { OnError?.Invoke(e); return RegistryRestoreOutcome.Failed; }
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
