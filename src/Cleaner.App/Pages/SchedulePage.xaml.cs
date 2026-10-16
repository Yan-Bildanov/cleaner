using System.IO;
using System.Windows;
using System.Windows.Controls;
using Cleaner.App.ViewModels;
using Cleaner.Core;
using Cleaner.Core.Auto;
using Cleaner.Core.Platform;

namespace Cleaner.App.Pages;

public sealed class AutoRuleItem(string id, string name, bool isChecked)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public bool IsChecked { get; set; } = isChecked;
}

public partial class SchedulePage : Page
{
    readonly AutoCleanStore _store = AutoCleanStore.Default;
    AutoCleanSettings _settings;
    readonly List<AutoRuleItem> _rules;
    bool _loading = true;

    static string ExePath => AppPaths.LaunchExe;
    static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Быстрая очистка.lnk");

    public SchedulePage()
    {
        InitializeComponent();
        _settings = _store.LoadSettings();
        _rules = AutoCleaner.Eligible.Select(r => new AutoRuleItem(r.Id, r.Name, _settings.RuleIds.Contains(r.Id))).ToList();
        RulesList.ItemsSource = _rules;
        EnabledToggle.IsChecked = _settings.Enabled;
        FrequencyBox.SelectedIndex = (int)_settings.Frequency;
        _loading = false;

        // Включено, но задачу удалили (или программа переехала) — тихо пересоздаём.
        if (_settings.Enabled) Loaded += (_, _) => EnsureTask();
        UpdateTexts();
    }

    void Enabled_Click(object sender, RoutedEventArgs e)
    {
        _settings = _settings with { Enabled = EnabledToggle.IsChecked == true };
        Save();
        if (_settings.Enabled) EnsureTask(force: true);
        else
        {
            var (ok, output) = AutoCleanTask.Unregister();
            StatusText.Text = ok ? "Автоочистка выключена, задача удалена из планировщика." : $"Не удалось удалить задачу: {output}";
        }
        UpdateTexts();
    }

    void Frequency_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings = _settings with { Frequency = (AutoFrequency)FrequencyBox.SelectedIndex };
        Save();
        if (_settings.Enabled) EnsureTask(force: true);
        UpdateTexts();
    }

    void Rule_Click(object sender, RoutedEventArgs e)
    {
        _settings = _settings with { RuleIds = _rules.Where(r => r.IsChecked).Select(r => r.Id).ToList() };
        Save();
    }

    void Save()
    {
        try { _store.SaveSettings(_settings); }
        catch (Exception ex) { App.Log(ex); StatusText.Text = "Не удалось сохранить настройки: " + ex.Message; }
    }

    void EnsureTask(bool force = false)
    {
        if (!force && AutoCleanTask.Exists()) return;
        var (ok, output) = AutoCleanTask.Register(ExePath, _settings.Frequency, DateTime.Now);
        StatusText.Text = ok ? "Задача в планировщике Windows создана." : $"Не удалось создать задачу в планировщике: {output}";
    }

    async void RunNow_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.RuleIds.Count == 0) { StatusText.Text = "Не выбрано ни одного пункта в «Что чистить автоматически»."; return; }
        RunNowButton.IsEnabled = false;
        StatusText.Text = "Очищаю…";
        try
        {
            var settings = _settings;
            var result = await Task.Run(() => AutoMode.Create().Run(settings, force: true));
            _store.SaveLastRun(result);
            StatusText.Text = AutoCleaner.Describe(result);
        }
        catch (Exception ex) { App.Log(ex); StatusText.Text = "Ошибка: " + ex.Message; }
        RunNowButton.IsEnabled = true;
        UpdateTexts();
    }

    void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // WScript.Shell — стандартный способ создать .lnk без сторонних библиотек.
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(ShortcutPath);
            link.TargetPath = ExePath;
            link.Arguments = "--auto --now";
            link.WorkingDirectory = Path.GetDirectoryName(ExePath);
            link.IconLocation = ExePath + ",0";
            link.Description = "Cleaner: быстрая очистка по настройкам автоочистки";
            link.Save();
            StatusText.Text = "Ярлык «Быстрая очистка» создан на рабочем столе.";
        }
        catch (Exception ex) { App.Log(ex); StatusText.Text = "Не удалось создать ярлык: " + ex.Message; }
        UpdateTexts();
    }

    void UpdateTexts()
    {
        NextRunText.Text = _settings.Enabled
            ? $"Включена. Следующий запуск: {AutoCleanTask.NextRun(_settings.Frequency, DateTime.Now):dddd, d MMMM, HH:mm}" +
              (_settings.Frequency == AutoFrequency.Threshold ? " (очистит, только если мусора больше 1 ГБ)" : "")
            : "Выключена";

        var last = _store.LoadLastRun();
        LastRunText.Text = last is null
            ? "Ещё не запускалась"
            : $"{last.At.ToLocalTime():d MMMM, HH:mm} — {AutoCleaner.Describe(last)}";

        ShortcutButton.Content = File.Exists(ShortcutPath) ? "Пересоздать" : "Создать ярлык";
    }
}
