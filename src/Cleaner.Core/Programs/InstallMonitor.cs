using System.Text.Json;
using Microsoft.Win32;

namespace Cleaner.Core.Programs;

/// <summary>Что и где снимать. Для тестов — песочница, для работы — <see cref="ForCurrentSystem"/>.</summary>
public sealed class SnapshotScope
{
    /// <summary>Папки, в которых запоминаются вложенные папки и файлы на два уровня вглубь.</summary>
    public required IReadOnlyList<string> FolderRoots { get; init; }
    /// <summary>Ветки реестра (обычно Software), в которых запоминаются ключи на два уровня вглубь.</summary>
    public required IReadOnlyList<LeftoverScanner.RegistryRoot> RegistryRoots { get; init; }
    /// <summary>Ключи автозагрузки (Run, RunOnce): запоминаются имена значений.</summary>
    public IReadOnlyList<LeftoverScanner.RegistryRoot> RunKeys { get; init; } = [];
    public bool IncludeServices { get; init; }
    public bool IncludeTasks { get; init; }
    public bool IncludeUninstall { get; init; }

    public static SnapshotScope ForCurrentSystem()
    {
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var local = F(Environment.SpecialFolder.LocalApplicationData);
        string[] folders =
        [
            F(Environment.SpecialFolder.ProgramFiles),
            F(Environment.SpecialFolder.ProgramFilesX86),
            F(Environment.SpecialFolder.CommonApplicationData),
            F(Environment.SpecialFolder.ApplicationData),
            local,
            Path.Combine(local, "Programs"),
            Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow"),
            Path.Combine(F(Environment.SpecialFolder.StartMenu), "Programs"),
            Path.Combine(F(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            F(Environment.SpecialFolder.DesktopDirectory),
            F(Environment.SpecialFolder.CommonDesktopDirectory),
        ];
        const string run = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string runOnce = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
        var reg = new List<LeftoverScanner.RegistryRoot>
        {
            new(RegistryHive.CurrentUser, RegistryView.Default, "Software"),
            new(RegistryHive.LocalMachine, RegistryView.Registry64, "SOFTWARE"),
        };
        var runKeys = new List<LeftoverScanner.RegistryRoot>
        {
            new(RegistryHive.CurrentUser, RegistryView.Default, run),
            new(RegistryHive.CurrentUser, RegistryView.Default, runOnce),
            new(RegistryHive.LocalMachine, RegistryView.Registry64, run),
            new(RegistryHive.LocalMachine, RegistryView.Registry64, runOnce),
        };
        if (Environment.Is64BitOperatingSystem)
        {
            reg.Add(new(RegistryHive.LocalMachine, RegistryView.Registry32, "SOFTWARE"));
            runKeys.Add(new(RegistryHive.LocalMachine, RegistryView.Registry32, run));
            runKeys.Add(new(RegistryHive.LocalMachine, RegistryView.Registry32, runOnce));
        }
        return new SnapshotScope
        {
            FolderRoots = folders.Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RegistryRoots = reg,
            RunKeys = runKeys,
            IncludeServices = true,
            IncludeTasks = true,
            IncludeUninstall = true,
        };
    }
}

/// <summary>Снимок: какие папки, файлы, ключи, службы, задачи, значения автозагрузки и программы были до установки.</summary>
public sealed class InstallSnapshot
{
    public DateTime TakenUtc { get; init; }
    /// <summary>«D|путь» — папка, «F|путь» — файл.</summary>
    public List<string> Paths { get; init; } = [];
    /// <summary>«64|HKLM\SOFTWARE\X», «32|…», «U|HKCU\Software\X».</summary>
    public List<string> Keys { get; init; } = [];
    public List<string> Services { get; init; } = [];
    public List<string> Tasks { get; init; } = [];
    /// <summary>«вид|ключ|имя значения».</summary>
    public List<string> RunValues { get; init; } = [];
    /// <summary>«Scope|имя ключа в Uninstall».</summary>
    public List<string> Uninstall { get; init; } = [];
}

public sealed record TraceItem(LeftoverKind Kind, string Path, RegistryView View = RegistryView.Default, string? ValueName = null);
public sealed record TracedProgram(ProgramScope Scope, string RegistryKeyName, string Name);

/// <summary>Запись установки: что появилось в системе между двумя снимками.</summary>
public sealed class InstallTrace
{
    public required string Id { get; init; }
    public DateTime StartedUtc { get; init; }
    public DateTime FinishedUtc { get; init; }
    public List<TracedProgram> Programs { get; init; } = [];
    public List<TraceItem> Items { get; set; } = [];

    public string Title => Programs.Count > 0 ? string.Join(", ", Programs.Select(p => p.Name)) : "Установка " + FinishedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
}

/// <summary>
/// Монитор установок: снимок «до» → пользователь ставит программу → снимок «после» → разница = что поставила программа.
/// Только чтение реестра и диска. Никаких драйверов и перехватов: шумные места (Temp, папки Microsoft, кэши) не снимаются,
/// а остальное пользователь проверяет галочками перед сохранением.
/// </summary>
public static class InstallMonitor
{
    public static Action<Exception>? OnError;

    /// <summary>Папки, которые постоянно меняются сами по себе, — не снимаем и не заходим внутрь.</summary>
    static readonly HashSet<string> NoiseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Temp", "Tmp", "Microsoft", "Packages", "CrashDumps", "D3DSCache", "Comms", "ConnectedDevicesPlatform",
        "PeerDistRepub", "WinGet", "NVIDIA", "NVIDIA Corporation", "History", "Cache", "INetCache", "INetCookies",
        "desktop.ini", "WindowsApps", "Windows Defender", "Windows NT", "Windows Mail", "Windows Media Player",
        "Windows Photo Viewer", "Windows Portable Devices", "Windows Sidebar", "WindowsPowerShell", "SquirrelTemp",
    };

    /// <summary>Ветки Software, куда не заходим: системные и общие.</summary>
    static readonly HashSet<string> RegistryNoise = new(StringComparer.OrdinalIgnoreCase)
        { "Microsoft", "WOW6432Node", "Policies", "Wow6432Node", "RegisteredApplications", "Clients" };

    public static InstallSnapshot Capture(SnapshotScope scope)
    {
        var paths = new List<string>();
        foreach (var root in scope.FolderRoots) AddPaths(root, paths);

        var keys = new List<string>();
        foreach (var r in scope.RegistryRoots) AddKeys(r, keys);

        var runValues = new List<string>();
        foreach (var r in scope.RunKeys) AddRunValues(r, runValues);

        return new InstallSnapshot
        {
            TakenUtc = DateTime.UtcNow,
            Paths = paths,
            Keys = keys,
            RunValues = runValues,
            Services = scope.IncludeServices ? ServiceNames() : [],
            Tasks = scope.IncludeTasks ? ScheduledTasks.AllPaths().ToList() : [],
            Uninstall = scope.IncludeUninstall ? ProgramInventory.ReadAll().Select(UninstallId).ToList() : [],
        };
    }

    static string UninstallId(InstalledProgram p) => p.Scope + "|" + p.RegistryKeyName;

    static readonly EnumerationOptions Shallow = new() { IgnoreInaccessible = true, AttributesToSkip = 0 };

    static void AddPaths(string root, List<string> into)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var e1 in new DirectoryInfo(root).EnumerateFileSystemInfos("*", Shallow))
            {
                if (NoiseNames.Contains(e1.Name)) continue;
                var isDir = e1.Attributes.HasFlag(FileAttributes.Directory);
                into.Add((isDir ? "D|" : "F|") + e1.FullName);
                if (!isDir || e1.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                try
                {
                    foreach (var e2 in ((DirectoryInfo)e1).EnumerateFileSystemInfos("*", Shallow))
                    {
                        if (NoiseNames.Contains(e2.Name)) continue;
                        into.Add((e2.Attributes.HasFlag(FileAttributes.Directory) ? "D|" : "F|") + e2.FullName);
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
            }
        }
        catch (Exception ex) { OnError?.Invoke(ex); }
    }

    static string Prefix(LeftoverScanner.RegistryRoot r) =>
        r.Hive == RegistryHive.CurrentUser ? "U" : r.View == RegistryView.Registry32 ? "32" : "64";

    static string HiveName(LeftoverScanner.RegistryRoot r) => r.Hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";

    static void AddKeys(LeftoverScanner.RegistryRoot r, List<string> into)
    {
        try
        {
            using var b = RegistryKey.OpenBaseKey(r.Hive, r.View);
            using var soft = b.OpenSubKey(r.Path);
            if (soft is null) return;
            var prefix = Prefix(r) + "|" + HiveName(r) + "\\" + r.Path + "\\";
            foreach (var n1 in soft.GetSubKeyNames())
            {
                if (RegistryNoise.Contains(n1)) continue;
                // HKLM\SOFTWARE\Classes в 64-битной Windows общий для 32- и 64-битного вида: снимаем его один раз (в 64-битном).
                if (r.View == RegistryView.Registry32 && n1.Equals("Classes", StringComparison.OrdinalIgnoreCase)) continue;
                into.Add(prefix + n1);
                try
                {
                    using var k1 = soft.OpenSubKey(n1);
                    if (k1 is null) continue;
                    foreach (var n2 in k1.GetSubKeyNames()) into.Add(prefix + n1 + "\\" + n2);
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
        }
        catch (Exception ex) { OnError?.Invoke(ex); }
    }

    static void AddRunValues(LeftoverScanner.RegistryRoot r, List<string> into)
    {
        try
        {
            using var b = RegistryKey.OpenBaseKey(r.Hive, r.View);
            using var k = b.OpenSubKey(r.Path);
            if (k is null) return;
            foreach (var v in k.GetValueNames())
                if (v.Length > 0) into.Add(Prefix(r) + "|" + HiveName(r) + "\\" + r.Path + "|" + v);
        }
        catch (Exception ex) { OnError?.Invoke(ex); }
    }

    static List<string> ServiceNames()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(SystemServices.ServicesKey);
            return root?.GetSubKeyNames().ToList() ?? [];
        }
        catch (Exception ex) { OnError?.Invoke(ex); return []; }
    }

    // ── Сравнение ─────────────────────────────────────────────────────────────────────────────────
    public static InstallTrace Diff(InstallSnapshot before, InstallSnapshot after, IReadOnlyList<InstalledProgram>? programsAfter = null)
    {
        var items = new List<TraceItem>();
        var cmp = StringComparer.OrdinalIgnoreCase;

        // Папки и файлы: только новые; вложенное в новую папку не дублируем.
        var oldPaths = before.Paths.Select(p => p[2..]).ToHashSet(cmp);
        var newEntries = after.Paths.Where(p => !oldPaths.Contains(p[2..])).ToList();
        var newDirs = newEntries.Where(p => p[0] == 'D').Select(p => p[2..]).ToList();
        foreach (var e in newEntries)
        {
            var path = e[2..];
            if (newDirs.Any(d => !d.Equals(path, StringComparison.OrdinalIgnoreCase) && LeftoverScanner.IsInside(path, d))) continue;
            var kind = e[0] == 'D' ? LeftoverKind.Folder
                     : path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? LeftoverKind.Shortcut : LeftoverKind.File;
            items.Add(new TraceItem(kind, path));
        }

        // Ключи реестра: только новые; вложенное в новый ключ не дублируем.
        var oldKeys = before.Keys.ToHashSet(cmp);
        var newKeys = after.Keys.Where(k => !oldKeys.Contains(k)).ToList();
        foreach (var k in newKeys)
        {
            if (newKeys.Any(o => !o.Equals(k, StringComparison.OrdinalIgnoreCase) && k.StartsWith(o + "\\", StringComparison.OrdinalIgnoreCase))) continue;
            var bar = k.IndexOf('|');
            var view = k[..bar] switch { "64" => RegistryView.Registry64, "32" => RegistryView.Registry32, _ => RegistryView.Default };
            items.Add(new TraceItem(LeftoverKind.RegistryKey, k[(bar + 1)..], view));
        }

        foreach (var s in after.Services.Except(before.Services, cmp))
            items.Add(new TraceItem(LeftoverKind.Service, s));
        foreach (var t in after.Tasks.Except(before.Tasks, cmp))
            if (!t.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase))
                items.Add(new TraceItem(LeftoverKind.ScheduledTask, t));
        foreach (var v in after.RunValues.Except(before.RunValues, cmp))
        {
            var parts = v.Split('|', 3);
            var view = parts[0] switch { "64" => RegistryView.Registry64, "32" => RegistryView.Registry32, _ => RegistryView.Default };
            items.Add(new TraceItem(LeftoverKind.RegistryValue, parts[1], view, parts[2]));
        }

        var programs = new List<TracedProgram>();
        var newUninstall = after.Uninstall.Except(before.Uninstall, cmp).ToHashSet(cmp);
        if (newUninstall.Count > 0)
            foreach (var p in programsAfter ?? ProgramInventory.ReadAll())
                if (newUninstall.Contains(UninstallId(p)))
                    programs.Add(new TracedProgram(p.Scope, p.RegistryKeyName, p.Name));

        return new InstallTrace
        {
            Id = after.TakenUtc.ToString("yyyyMMdd-HHmmss"),
            StartedUtc = before.TakenUtc,
            FinishedUtc = after.TakenUtc,
            Programs = programs,
            Items = items,
        };
    }

    // ── Записи → хвосты при удалении ──────────────────────────────────────────────────────────────
    /// <summary>
    /// Пункты записи, которые всё ещё есть в системе, — как уверенные хвосты. Те же защитные правила, что и при поиске:
    /// чужие папки (где стоит другая установленная программа), системные службы и ветки не предлагаются.
    /// </summary>
    public static IReadOnlyList<Leftover> LeftoversFrom(InstallTrace trace, IReadOnlyList<InstalledProgram> stillInstalled)
    {
        var result = new List<Leftover>();
        var others = stillInstalled.Select(o => o.InstallLocation).Where(l => !string.IsNullOrEmpty(l))
                                   .Select(l => l!.TrimEnd('\\')).ToList();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        const string reason = "записано монитором при установке";

        foreach (var i in trace.Items)
        {
            try
            {
                switch (i.Kind)
                {
                    case LeftoverKind.Folder:
                        if (!Directory.Exists(i.Path) || !ProgramInventory.IsProgramOwnFolder(i.Path)) break;
                        if (others.Any(o => o.Equals(i.Path, StringComparison.OrdinalIgnoreCase)
                                            || LeftoverScanner.IsInside(o, i.Path) || LeftoverScanner.IsInside(i.Path, o))) break;
                        var (size, count) = Measure(i.Path);
                        result.Add(new Leftover(LeftoverKind.Folder, i.Path, LeftoverConfidence.High, reason, size, count,
                                                NeedsAdmin: !LeftoverScanner.IsInside(i.Path, profile), FromTrace: true));
                        break;
                    case LeftoverKind.File:
                    case LeftoverKind.Shortcut:
                        if (!File.Exists(i.Path)) break;
                        if (others.Any(o => LeftoverScanner.IsInside(i.Path, o))) break;
                        result.Add(new Leftover(i.Kind, i.Path, LeftoverConfidence.High, reason, new FileInfo(i.Path).Length, 1,
                                                NeedsAdmin: !LeftoverScanner.IsInside(i.Path, profile), FromTrace: true));
                        break;
                    case LeftoverKind.RegistryKey:
                        if (!Cleaning.RegistryBackup.Exists(i.Path, i.View)) break;
                        if (!LeftoverPlan.IsSoftwareSubkey(i.Path) && !LeftoverPlan.IsTracedClassesKey(i.Path)) break;
                        result.Add(new Leftover(LeftoverKind.RegistryKey, i.Path, LeftoverConfidence.High, reason, View: i.View,
                                                NeedsAdmin: i.Path.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase), FromTrace: true));
                        break;
                    case LeftoverKind.Service:
                        if (!SystemServices.IsSafeToRemove(i.Path)) break;
                        result.Add(new Leftover(LeftoverKind.Service, i.Path, LeftoverConfidence.High, reason, NeedsAdmin: true, FromTrace: true));
                        break;
                    case LeftoverKind.ScheduledTask:
                        if (!ScheduledTasks.Exists(i.Path)) break;
                        result.Add(new Leftover(LeftoverKind.ScheduledTask, i.Path, LeftoverConfidence.High, reason, NeedsAdmin: true, FromTrace: true));
                        break;
                    case LeftoverKind.RegistryValue:
                        if (i.ValueName is null || !LeftoverPlan.IsAutostartKey(i.Path)) break;
                        if (!Cleaning.RegistryBackup.ValueExists(i.Path, i.View, i.ValueName)) break;
                        result.Add(new Leftover(LeftoverKind.RegistryValue, i.Path, LeftoverConfidence.High, reason, View: i.View,
                                                NeedsAdmin: i.Path.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase),
                                                ValueName: i.ValueName, FromTrace: true));
                        break;
                }
            }
            catch (Exception ex) { OnError?.Invoke(ex); }
        }
        return result;
    }

    /// <summary>
    /// Хвосты для удалённой программы: сначала записанное монитором (если запись есть), потом найденное по именам.
    /// Дубли убираются, вложенные папки схлопываются в внешнюю.
    /// </summary>
    public static IReadOnlyList<Leftover> LeftoversFor(InstalledProgram removed, IReadOnlyList<InstalledProgram> stillInstalled,
                                                       LeftoverScanner scanner, TraceStore? traces)
    {
        var traced = traces?.FindFor(removed) is { } t ? LeftoversFrom(t, stillInstalled) : [];
        return Combine(traced, scanner.Scan(removed, stillInstalled));
    }

    public static IReadOnlyList<Leftover> Combine(IEnumerable<Leftover> first, IEnumerable<Leftover> second)
    {
        var all = new List<Leftover>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in first.Concat(second))
            if (seen.Add($"{l.Kind}|{l.Path}|{l.View}|{l.ValueName}")) all.Add(l);
        var folders = all.Where(f => f.Kind == LeftoverKind.Folder).Select(f => f.Path).ToList();
        all.RemoveAll(f => (f.Kind is LeftoverKind.Folder or LeftoverKind.File or LeftoverKind.Shortcut)
                           && folders.Any(o => !o.Equals(f.Path, StringComparison.OrdinalIgnoreCase) && LeftoverScanner.IsInside(f.Path, o)));
        return all;
    }

    static readonly EnumerationOptions Deep = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    static (long, int) Measure(string dir)
    {
        long size = 0; var count = 0;
        try { foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", Deep)) { count++; try { size += f.Length; } catch { } } }
        catch { }
        return (size, count);
    }
}

/// <summary>Записи установок на диске: %USERPROFILE%\.cleaner\traces. Плюс «незаконченный» снимок, если окно закрыли посреди установки.</summary>
public sealed class TraceStore(string dir)
{
    public static TraceStore Default => new(Path.Combine(AppPaths.DataDir, "traces"));
    const string PendingFile = "pending.json";

    public string Dir { get; } = dir;

    public void Save(InstallTrace trace)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, trace.Id + ".json"), JsonSerializer.Serialize(trace));
    }

    public IReadOnlyList<InstallTrace> All()
    {
        if (!Directory.Exists(Dir)) return [];
        var list = new List<InstallTrace>();
        foreach (var f in Directory.EnumerateFiles(Dir, "*.json"))
        {
            if (Path.GetFileName(f).Equals(PendingFile, StringComparison.OrdinalIgnoreCase)) continue;
            try { if (JsonSerializer.Deserialize<InstallTrace>(File.ReadAllText(f)) is { } t) list.Add(t); }
            catch (Exception ex) { InstallMonitor.OnError?.Invoke(ex); }
        }
        return list.OrderByDescending(t => t.FinishedUtc).ToList();
    }

    /// <summary>Запись установки этой программы (по её ключу в списке установленных).</summary>
    public InstallTrace? FindFor(InstalledProgram p) =>
        p.IsStoreApp ? null : All().FirstOrDefault(t => t.Programs.Any(x => x.Scope == p.Scope
            && x.RegistryKeyName.Equals(p.RegistryKeyName, StringComparison.OrdinalIgnoreCase)));

    public void Delete(InstallTrace trace)
    {
        try { File.Delete(Path.Combine(Dir, trace.Id + ".json")); } catch (Exception ex) { InstallMonitor.OnError?.Invoke(ex); }
    }

    public void SavePending(InstallSnapshot before)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, PendingFile), JsonSerializer.Serialize(before));
    }

    public InstallSnapshot? LoadPending()
    {
        var f = Path.Combine(Dir, PendingFile);
        try { return File.Exists(f) ? JsonSerializer.Deserialize<InstallSnapshot>(File.ReadAllText(f)) : null; }
        catch (Exception ex) { InstallMonitor.OnError?.Invoke(ex); return null; }
    }

    public void ClearPending()
    {
        try { File.Delete(Path.Combine(Dir, PendingFile)); } catch { }
    }
}
