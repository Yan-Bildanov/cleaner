using System.Diagnostics;
using Cleaner.Core.Cleaning;
using Microsoft.Win32;

namespace Cleaner.Core.Programs;

/// <summary>
/// Службы Windows, чей исполняемый файл лежит в папке удаляемой программы.
/// Драйверы не трогаем никогда — только обычные службы (Win32 own/share process).
/// Удаление: остановить → сохранить ключ службы в .reg → sc delete. Возврат: импорт .reg (служба появится после перезагрузки).
/// </summary>
public static class SystemServices
{
    public const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
    public static Action<Exception>? OnError;

    public sealed record ServiceInfo(string Name, string DisplayName, string Executable);

    public static IReadOnlyList<ServiceInfo> FindInside(IReadOnlyCollection<string> folders)
    {
        var result = new List<ServiceInfo>();
        if (folders.Count == 0) return result;
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(ServicesKey);
            if (root is null) return result;
            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var k = root.OpenSubKey(name);
                    if (k?.GetValue("Type") is not int type || (type & 0x30) == 0) continue;     // 0x10/0x20 — службы, не драйверы
                    if (k.GetValue("ImagePath") is not string image) continue;
                    var exe = UninstallCommand.Parse(image)?.FileName;
                    if (exe is null || !Path.IsPathRooted(exe) || IsSystemLocation(exe)) continue;
                    if (k.GetValue("Start") is int start && start <= 1) continue;
                    if (!folders.Any(f => LeftoverScanner.IsInside(exe, f))) continue;
                    var display = k.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(display) || display.StartsWith('@')) display = name;
                    result.Add(new ServiceInfo(name, display, exe));
                }
                catch (Exception ex) { OnError?.Invoke(ex); }
            }
        }
        catch (Exception ex) { OnError?.Invoke(ex); }
        return result;
    }

    /// <summary>
    /// Последняя проверка перед удалением службы: это обычная служба (не драйвер) и её файл не в папке Windows.
    /// </summary>
    public static bool IsSafeToRemove(string serviceName)
    {
        try
        {
            if (serviceName.Contains('\\') || serviceName.Contains('/') || serviceName.Length == 0) return false;
            using var k = Registry.LocalMachine.OpenSubKey(ServicesKey + "\\" + serviceName);
            if (k?.GetValue("Type") is not int type || (type & 0x30) == 0) return false;
            if (k.GetValue("Start") is int start && start <= 1) return false;          // загрузочные и системные — никогда
            if (k.GetValue("ImagePath") is not string image) return false;
            var exe = UninstallCommand.Parse(image)?.FileName;
            if (exe is null || !Path.IsPathRooted(exe)) return false;
            return !IsSystemLocation(exe);
        }
        catch { return false; }
    }

    /// <summary>
    /// Файл лежит в папке Windows или в любой папке «Microsoft» / «Windows…» (Защитник живёт в
    /// ProgramData\Microsoft\Windows Defender, а не в C:\Windows) — такие службы не трогаем никогда.
    /// </summary>
    public static bool IsSystemLocation(string exe)
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        if (LeftoverScanner.IsInside(exe, win)) return true;
        foreach (var seg in exe.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (seg.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)
                || seg.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)
                || seg.Equals("Microsoft Shared", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public static string KeyPath(string serviceName) => @"HKLM\" + ServicesKey + @"\" + serviceName;

    /// <summary>Остановить и удалить службу; ключ предварительно сохраняется в <paramref name="backupFile"/>.</summary>
    public static bool StopAndDelete(string serviceName, string backupFile)
    {
        var key = KeyPath(serviceName);
        if (!Sc($"stop \"{serviceName}\"", out _)) { /* уже остановлена или не останавливается — пробуем дальше */ }
        WaitStopped(serviceName, TimeSpan.FromSeconds(20));
        if (!RegistryBackup.Export(key, RegistryView.Registry64, backupFile)) return false;
        return Sc($"delete \"{serviceName}\"", out _);
    }

    static void WaitStopped(string name, TimeSpan timeout)
    {
        var until = DateTime.Now + timeout;
        while (DateTime.Now < until)
        {
            if (Sc($"query \"{name}\"", out var output) && output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return;
            if (!output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) && !output.Contains("PENDING", StringComparison.OrdinalIgnoreCase)) return;
            Thread.Sleep(500);
        }
    }

    static bool Sc(string args, out string output)
    {
        output = "";
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30_000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch (Exception ex) { OnError?.Invoke(ex); return false; }
    }
}
