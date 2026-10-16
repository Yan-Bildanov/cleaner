using System.IO;
using System.Windows;
using Cleaner.Core;
using Cleaner.Core.Auto;
using WinForms = System.Windows.Forms;

namespace Cleaner.App;

/// <summary>Тихий режим «--auto»: без окна, результат — уведомлением и в журнал auto.log.</summary>
public static class AutoMode
{
    public static AutoCleaner Create() => new(App.Scanner, App.FileCleaner, App.Quarantine, App.SystemInfo);

    public static async Task RunAsync(bool force, bool dryRun)
    {
        AutoRunResult result;
        var store = AutoCleanStore.Default;
        var settings = store.LoadSettings();

        // Задача в планировщике осталась, а автоочистку выключили — просто выходим.
        if (!force && !settings.Enabled) { Application.Current.Shutdown(); return; }

        try { result = await Task.Run(() => Create().Run(settings, force, dryRun)); }
        catch (Exception ex)
        {
            App.Log(ex);
            result = new AutoRunResult { At = DateTime.UtcNow, DryRun = dryRun, Error = ex.Message };
        }

        if (!dryRun) store.SaveLastRun(result);
        var text = AutoCleaner.Describe(result);
        WriteLog(text, force, dryRun);

        // Уведомляем, только когда есть что сказать: что-то очищено, ошибка, или запуск вручную.
        if (force || dryRun || result.CleanedFiles > 0 || result.Error is not null) await NotifyAsync(text);
        Application.Current.Shutdown();
    }

    static void WriteLog(string text, bool force, bool dryRun)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            var mode = dryRun ? "пробный" : force ? "вручную" : "по расписанию";
            File.AppendAllText(Path.Combine(AppPaths.LogDir, "auto.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ({mode}) {text}\n");
        }
        catch (Exception ex) { App.Log(ex); }
    }

    static async Task NotifyAsync(string text)
    {
        using var icon = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Information,
            Text = "Cleaner",
            Visible = true,
        };
        // Windows 10 показывает это как обычное уведомление в правом нижнем углу.
        icon.ShowBalloonTip(8000, "Cleaner — автоочистка", text.Length > 250 ? text[..247] + "…" : text, WinForms.ToolTipIcon.Info);
        await Task.Delay(9000);
        icon.Visible = false;
    }
}
