using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Cleaner.App;

public partial class App : Application
{
    /// <summary>Журнал ошибок: %USERPROFILE%\.cleaner\logs\errors.log — смотреть первым при «программа упала».</summary>
    public static string LogDir => Core.AppPaths.LogDir;

    // Общие сервисы приложения.
    public static Core.Platform.WindowsSystemInfo SystemInfo { get; } = new();
    public static Core.Cleaning.QuarantineStore Quarantine { get; } = new(Core.Cleaning.QuarantineStore.DefaultRoot, SystemInfo);
    public static Core.Scanning.Scanner Scanner { get; } = new(SystemInfo);
    public static Core.Cleaning.FileCleaner FileCleaner { get; } = new(Quarantine, SystemInfo);
    public static Core.Platform.RestorePoints RestorePoints { get; } = new(SystemInfo);

    /// <summary>Перезапуск с правами администратора (покажет запрос UAC). false — пользователь отказался.</summary>
    public static bool RelaunchElevated()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            Current.Shutdown();
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)   // «Нет» в окне UAC
        {
            return false;
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Данные — в %USERPROFILE%\.cleaner; карантин со старого места (AppData) переносим туда.
        try { Core.AppPaths.EnsureDataDir(); Core.AppPaths.MigrateLegacyQuarantine(); }
        catch (Exception ex) { Log(ex); }

        // Партии карантина старше 7 дней стираем навсегда — в фоне, при каждом запуске.
        Task.Run(() => { try { Quarantine.PurgeExpired(); } catch (Exception ex) { Log(ex); } });

        // Русские разделители чисел везде: «7 276 файлов», «4,0 с».
        var ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = ru;
        System.Globalization.CultureInfo.CurrentCulture = ru;

        Core.Cleaning.QuarantineStore.OnError = Log;
        Core.Rules.LocalRules.OnError = Log;
        WatchCloseSignal();
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log(a.Exception); a.SetObserved(); };
        base.OnStartup(e);

        // «Cleaner.exe --auto» — тихий режим для планировщика (без окна, уведомление в трее).
        //   --now      не смотреть на порог и выключенный переключатель (ярлык «Быстрая очистка»)
        //   --dry-run  только посчитать, ничего не трогать (для проверки)
        var args = e.Args.Select(a => a.ToLowerInvariant()).ToHashSet();
        if (args.Contains("--auto")) { _ = AutoMode.RunAsync(force: args.Contains("--now"), dryRun: args.Contains("--dry-run")); return; }

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    /// <summary>
    /// Служебное: файл %USERPROFILE%\.cleaner\close.signal просит программу закрыться.
    /// Нужно для пересборки, когда окно запущено от администратора и снаружи его закрыть нельзя.
    /// </summary>
    FileSystemWatcher? _closeWatcher;

    void WatchCloseSignal()
    {
        try
        {
            var signal = Core.AppPaths.CloseSignal;
            if (File.Exists(signal)) File.Delete(signal);              // старый сигнал не должен закрыть новый запуск
            _closeWatcher = new FileSystemWatcher(Core.AppPaths.DataDir, "close.signal") { EnableRaisingEvents = true };
            _closeWatcher.Created += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                try { File.Delete(signal); } catch { }
                Shutdown();
            });
        }
        catch (Exception ex) { Log(ex); }
    }

    void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        MessageBox.Show($"Произошла ошибка, она записана в журнал:\n{LogDir}\n\n{e.Exception.Message}",
                        "Cleaner", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    public static void Log(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(Path.Combine(LogDir, "errors.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { /* журнал не должен ронять программу */ }
    }
}
