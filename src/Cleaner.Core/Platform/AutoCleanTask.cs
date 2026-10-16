using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using Cleaner.Core.Auto;

namespace Cleaner.Core.Platform;

/// <summary>
/// Задача в Планировщике заданий Windows, которая запускает «Cleaner.exe --auto».
/// Создаётся от имени текущего пользователя, без прав администратора (RunLevel = LeastPrivilege).
/// Если ноутбук был выключен в назначенное время — запустится при следующем включении (StartWhenAvailable).
/// </summary>
public static class AutoCleanTask
{
    public const string TaskName = @"Cleaner\Автоочистка";
    const int Hour = 12;

    public static bool Exists() => Schtasks($"/Query /TN \"{TaskName}\"").ExitCode == 0;

    public static (bool Ok, string Output) Register(string exePath, AutoFrequency frequency, DateTime now)
    {
        var xml = Path.Combine(Path.GetTempPath(), $"cleaner-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xml, BuildXml(exePath, frequency, now), Encoding.Unicode);   // schtasks ждёт UTF-16
            var r = Schtasks($"/Create /TN \"{TaskName}\" /XML \"{xml}\" /F");
            return (r.ExitCode == 0, r.Output);
        }
        finally { try { File.Delete(xml); } catch { } }
    }

    public static (bool Ok, string Output) Unregister()
    {
        if (!Exists()) return (true, "");
        var r = Schtasks($"/Delete /TN \"{TaskName}\" /F");
        return (r.ExitCode == 0, r.Output);
    }

    /// <summary>Когда будет следующий запуск (считаем сами — вывод schtasks зависит от языка Windows).</summary>
    public static DateTime NextRun(AutoFrequency frequency, DateTime now)
    {
        var today = now.Date.AddHours(Hour);
        if (frequency != AutoFrequency.Weekly) return now < today ? today : today.AddDays(1);
        var daysToSunday = ((int)DayOfWeek.Sunday - (int)now.DayOfWeek + 7) % 7;
        var next = today.AddDays(daysToSunday);
        return now < next ? next : next.AddDays(7);
    }

    public static string BuildXml(string exePath, AutoFrequency frequency, DateTime now)
    {
        var start = NextRun(frequency, now).ToString("yyyy-MM-dd'T'HH:mm:ss");
        var schedule = frequency == AutoFrequency.Weekly
            ? "<ScheduleByWeek><DaysOfWeek><Sunday /></DaysOfWeek><WeeksInterval>1</WeeksInterval></ScheduleByWeek>"
            : "<ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>";
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var exe = SecurityElement.Escape(exePath);
        var dir = SecurityElement.Escape(Path.GetDirectoryName(exePath)!);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Автоочистка Cleaner: кэши и временные файлы в карантин на 7 дней.</Description></RegistrationInfo>
              <Triggers>
                <CalendarTrigger><StartBoundary>{start}</StartBoundary><Enabled>true</Enabled>{schedule}</CalendarTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT30M</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{exe}</Command><Arguments>--auto</Arguments><WorkingDirectory>{dir}</WorkingDirectory></Exec>
              </Actions>
            </Task>
            """;
    }

    static (int ExitCode, string Output) Schtasks(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        })!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        return (p.ExitCode, output.Trim());
    }
}
