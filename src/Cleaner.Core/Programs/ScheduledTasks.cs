using System.Diagnostics;
using System.Text;

namespace Cleaner.Core.Programs;

/// <summary>
/// Задачи Планировщика заданий, которые запускают файл из папки удаляемой программы (обновлялки, «помощники»).
/// Удаление: сохранить XML задачи → удалить. Возврат: schtasks /create /xml.
/// Работает через COM «Schedule.Service» — тот же интерфейс, что у самого Планировщика.
/// </summary>
public static class ScheduledTasks
{
    public static Action<Exception>? OnError;

    public sealed record TaskInfo(string Path, string Executable);

    public static IReadOnlyList<TaskInfo> FindInside(IReadOnlyCollection<string> folders)
    {
        var result = new List<TaskInfo>();
        if (folders.Count == 0) return result;
        try
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            service.Connect();
            Walk(service.GetFolder("\\"), folders, result, 0);
        }
        catch (Exception ex) { OnError?.Invoke(ex); }
        return result;
    }

    static void Walk(dynamic folder, IReadOnlyCollection<string> folders, List<TaskInfo> into, int depth)
    {
        if (depth > 8) return;
        try
        {
            foreach (dynamic task in folder.GetTasks(1))          // 1 = вместе со скрытыми
            {
                try
                {
                    foreach (dynamic action in task.Definition.Actions)
                    {
                        if ((int)action.Type != 0) continue;       // 0 = запуск программы
                        var raw = Environment.ExpandEnvironmentVariables(((string?)action.Path ?? "").Trim().Trim('"'));
                        if (raw.Length == 0 || !System.IO.Path.IsPathRooted(raw) || SystemServices.IsSystemLocation(raw)) continue;
                        if (!folders.Any(f => LeftoverScanner.IsInside(raw, f))) continue;
                        into.Add(new TaskInfo((string)task.Path, raw));
                        break;
                    }
                }
                catch (Exception ex) { OnError?.Invoke(ex); }
            }
            foreach (dynamic sub in folder.GetFolders(0)) Walk(sub, folders, into, depth + 1);
        }
        catch (Exception ex) { OnError?.Invoke(ex); }
    }

    /// <summary>Сохранить XML задачи в файл и удалить задачу.</summary>
    public static bool ExportAndDelete(string taskPath, string backupFile)
    {
        try
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            service.Connect();
            var (folderPath, name) = Split(taskPath);
            dynamic folder = service.GetFolder(folderPath);
            dynamic task = folder.GetTask(name);
            string xml = task.Xml;
            File.WriteAllText(backupFile, xml, Encoding.Unicode);    // XML задачи объявлен как UTF-16
            folder.DeleteTask(name, 0);
            return true;
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex);
            try { File.Delete(backupFile); } catch { }
            return false;
        }
    }

    public static bool Exists(string taskPath)
    {
        try
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            service.Connect();
            var (folderPath, name) = Split(taskPath);
            service.GetFolder(folderPath).GetTask(name);
            return true;
        }
        catch { return false; }
    }

    public static bool Restore(string taskPath, string backupFile)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                $"/create /tn \"{taskPath}\" /xml \"{backupFile}\" /f")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            return p.WaitForExit(30_000) && p.ExitCode == 0;
        }
        catch (Exception ex) { OnError?.Invoke(ex); return false; }
    }

    static (string Folder, string Name) Split(string taskPath)
    {
        var i = taskPath.LastIndexOf('\\');
        return i <= 0 ? ("\\", taskPath.TrimStart('\\')) : (taskPath[..i], taskPath[(i + 1)..]);
    }
}
