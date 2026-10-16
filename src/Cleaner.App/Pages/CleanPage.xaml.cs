using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Cleaner.App.ViewModels;
using Cleaner.Core;
using Cleaner.Core.Rules;
using Wpf.Ui.Controls;
using UiMessageBox = Wpf.Ui.Controls.MessageBox;
using UiMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace Cleaner.App.Pages;

public partial class CleanPage : Page
{
    readonly List<RuleItemViewModel> _items;
    bool _analyzed;

    static SymbolRegular IconFor(string group) => group switch
    {
        BuiltInRules.GroupWindows => SymbolRegular.Window24,
        BuiltInRules.GroupBrowsers => SymbolRegular.Globe24,
        BuiltInRules.GroupApps => SymbolRegular.Apps24,
        _ => SymbolRegular.Code24,
    };

    public CleanPage()
    {
        InitializeComponent();
        _items = BuiltInRules.All.Select(r => new RuleItemViewModel(r)).ToList();
        foreach (var item in _items) item.CheckedChanged += UpdateTotal;
        GroupsList.ItemsSource = _items
            .GroupBy(i => i.Rule.Group)
            .Select(g => new RuleGroupViewModel(g.Key, IconFor(g.Key), g))
            .ToList();
        Loaded += (_, _) => UpdateQuarantineLink();
        AdminButton.Visibility = App.SystemInfo.IsElevated ? Visibility.Collapsed : Visibility.Visible;
    }

    void Admin_Click(object sender, RoutedEventArgs e)
    {
        if (!App.RelaunchElevated()) StatusText.Text = "Запуск от администратора отменён.";
    }

    /// <summary>Анализ: только чтение. Сканируем все правила (чтобы видеть размеры и у неотмеченных).</summary>
    async void Analyze_Click(object sender, RoutedEventArgs e) => await AnalyzeAsync();

    async Task AnalyzeAsync()
    {
        SetBusy(true);
        Progress.IsIndeterminate = false;
        Progress.Maximum = _items.Count;
        Progress.Value = 0;
        var sw = Stopwatch.StartNew();

        foreach (var item in _items) item.MarkScanning();
        foreach (var item in _items)
        {
            StatusText.Text = $"Проверяю: {item.Name}…";
            var result = await Task.Run(() => App.Scanner.Scan(item.Rule));
            item.Apply(result);
            Progress.Value++;
        }

        _analyzed = true;
        AnalyzeButton.Content = "Анализ ещё раз";
        SetBusy(false);
        UpdateTotal();
        StatusText.Text += $"\nПроверка заняла {sw.Elapsed.TotalSeconds:0.0} с.";
    }

    List<RuleItemViewModel> Selected() =>
        _items.Where(i => i.IsChecked && i.Result is { NeedsAdmin: false, ItemCount: > 0 }).ToList();

    async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var selected = Selected();
        var files = selected.Where(i => i.Rule.Kind == RuleKind.Files).ToList();
        var bin = selected.FirstOrDefault(i => i.Rule.Kind == RuleKind.RecycleBin);
        if (files.Count == 0 && bin is null) { StatusText.Text = "В отмеченных пунктах нечего очищать."; return; }

        // 1) Подтверждение переноса в карантин.
        if (files.Count > 0)
        {
            var count = files.Sum(i => i.Result!.ItemCount);
            var bytes = files.Sum(i => i.Result!.TotalBytes);
            var running = files.SelectMany(i => i.Result!.RunningProcesses).Distinct().ToList();
            var text = $"Переместить в карантин {count:N0} файлов ({Format.Bytes(bytes)})?\n\n" +
                       "Их можно вернуть в течение 7 дней через «Карантин». " +
                       "Место на диске освободится, когда карантин будет удалён (через 7 дней или вручную).";
            if (running.Count > 0)
                text += $"\n\nСейчас открыты: {string.Join(", ", running)} — их занятые файлы будут пропущены.";
            if (!await Confirm("Очистка", text, "Очистить")) return;
        }

        // 2) Корзина — отдельное подтверждение, потому что безвозвратно.
        var emptyBin = false;
        if (bin is not null)
        {
            emptyBin = await Confirm("Корзина",
                $"Очистить корзину: {bin.Result!.ItemCount:N0} объектов, {Format.Bytes(bin.Result.TotalBytes)}?\n\n" +
                "Это удаление БЕЗВОЗВРАТНОЕ — вернуть через карантин будет нельзя.",
                "Очистить корзину", "Не трогать корзину");
            if (files.Count == 0 && !emptyBin) return;
        }

        // 3) Точка восстановления (по галочке).
        string? restoreNote = null;
        if (RestorePointCheck.IsChecked == true)
        {
            if (!App.SystemInfo.IsElevated)
            {
                if (await Confirm("Точка восстановления",
                        "Для точки восстановления нужны права администратора.\n\n" +
                        "Перезапустить программу от администратора? Анализ нужно будет запустить заново.",
                        "Перезапустить", "Очистить без точки"))
                {
                    if (!App.RelaunchElevated()) StatusText.Text = "Запуск от администратора отменён.";
                    return;
                }
                restoreNote = "Точка восстановления не создавалась.";
            }
            else
            {
                SetBusy(true);
                Progress.IsIndeterminate = true;
                StatusText.Text = "Создаю точку восстановления… Это может занять до минуты.";
                var rp = await Task.Run(() => App.RestorePoints.Create("Cleaner: перед очисткой"));
                SetBusy(false);
                restoreNote = rp.Status switch
                {
                    Core.Platform.RestorePointStatus.Created => "Точка восстановления создана.",
                    Core.Platform.RestorePointStatus.ProtectionOff => "Точка не создана: «Защита системы» выключена для диска C:.",
                    Core.Platform.RestorePointStatus.Skipped => "Точка не создана: Windows пропустила запрос (недавно уже создавалась).",
                    _ => $"Точка не создана: {rp.Error}",
                };
                if (rp.Status != Core.Platform.RestorePointStatus.Created &&
                    !await Confirm("Точка восстановления", restoreNote + "\n\nПродолжить очистку без неё?", "Продолжить"))
                {
                    StatusText.Text = restoreNote;
                    return;
                }
            }
        }

        SetBusy(true);
        Progress.IsIndeterminate = true;
        StatusText.Text = "Очищаю…";
        var results = files.Select(i => i.Result!).ToList();
        if (emptyBin) results.Add(bin!.Result!);
        var report = await Task.Run(() => App.FileCleaner.Clean(results, emptyBin));

        var summary = $"В карантин: {report.Moved:N0} файлов.";
        if (report.SkippedLocked > 0) summary += $"\nПропущено занятых: {report.SkippedLocked:N0}.";
        if (report.RecycleBinEmptied) summary += $"\nКорзина очищена: {Format.Bytes(report.RecycleBinBytes)}.";
        if (restoreNote is not null) summary += "\n" + restoreNote;

        await AnalyzeAsync();                       // обновляем размеры после очистки
        var remaining = TotalText.Text;
        ResultTitle.Text = "Очищено";
        TotalText.Text = Format.Bytes(report.MovedBytes + (report.RecycleBinEmptied ? report.RecycleBinBytes : 0));
        StatusText.Text = summary + $"\nОсталось в отмеченных: {remaining}.";
        UpdateQuarantineLink();
    }

    void Quarantine_Click(object sender, RoutedEventArgs e)
    {
        new QuarantineWindow { Owner = Window.GetWindow(this) }.ShowDialog();
        UpdateQuarantineLink();
    }

    void UpdateQuarantineLink()
    {
        var batches = App.Quarantine.List();
        var bytes = batches.Sum(b => b.TotalBytes);
        QuarantineLink.Content = batches.Count == 0 ? "Карантин (пусто)" : $"Карантин: {Format.Bytes(bytes)}";
    }

    internal static async Task<bool> Confirm(string title, string text, string yes, string no = "Отмена")
    {
        var box = new UiMessageBox
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            PrimaryButtonText = yes,
            CloseButtonText = no,
        };
        return await box.ShowDialogAsync() == UiMessageBoxResult.Primary;
    }

    void SetBusy(bool busy)
    {
        AnalyzeButton.IsEnabled = !busy;
        CleanButton.IsEnabled = !busy && _analyzed;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateTotal()
    {
        if (!_analyzed) return;
        var selected = _items.Where(i => i.IsChecked && i.Result is { NeedsAdmin: false }).ToList();
        var bytes = selected.Sum(i => i.Result!.TotalBytes);
        ResultTitle.Text = "Результат анализа";
        TotalText.Text = Format.Bytes(bytes);
        TotalText.Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorPrimaryBrush");
        StatusText.Text = $"Можно освободить в отмеченных пунктах ({selected.Count}).";
        CleanButton.IsEnabled = AnalyzeButton.IsEnabled && bytes > 0;
    }
}
