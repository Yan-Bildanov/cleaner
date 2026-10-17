using System.Windows;
using System.Windows.Controls;
using Cleaner.App.ViewModels;
using Cleaner.Core.Browsers;
using Cleaner.Core.Programs;
using Wpf.Ui.Controls;

namespace Cleaner.App.Pages;

public partial class ExtensionsPage : Page
{
    List<ExtensionItemViewModel> _all = [];
    bool _busy;

    public ExtensionsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    async Task LoadAsync()
    {
        RefreshButton.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        ExtList.Visibility = Visibility.Collapsed;
        IReadOnlyList<BrowserExtension> exts;
        try { exts = await Task.Run(BrowserExtensions.ReadAll); }
        catch (Exception ex) { App.Log(ex); exts = []; }
        _all = exts.Select(x => new ExtensionItemViewModel(x)).ToList();

        var selected = (BrowserBox.SelectedItem as ComboBoxItem)?.Tag as string;
        BrowserBox.SelectionChanged -= Filter_Changed;
        BrowserBox.Items.Clear();
        BrowserBox.Items.Add(new ComboBoxItem { Content = "Все браузеры", Tag = "" });
        foreach (var b in _all.Select(x => x.Ext.Browser.Name).Distinct())
            BrowserBox.Items.Add(new ComboBoxItem { Content = b, Tag = b });
        BrowserBox.SelectedItem = BrowserBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selected) ?? BrowserBox.Items[0];
        BrowserBox.SelectionChanged += Filter_Changed;

        var browsers = _all.GroupBy(x => x.Ext.Browser.Name)
            .Select(g => $"{g.Key}: {g.Count()} в {g.Select(x => x.Ext.ProfileDir).Distinct().Count()} " +
                         Plural(g.Select(x => x.Ext.ProfileDir).Distinct().Count(), "профиле", "профилях", "профилях"));
        var suspicious = _all.Count(x => x.Ext.IsSuspicious);
        SummaryText.Text = _all.Count == 0
            ? "Браузеров на Chromium (Chrome, Edge, Brave, Vivaldi, Яндекс) с расширениями не найдено."
            : string.Join(" · ", browsers) + (suspicious > 0 ? $" · с предупреждениями: {suspicious}" : "");

        Busy.Visibility = Visibility.Collapsed;
        ExtList.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = true;
        ApplyView();
    }

    void Filter_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) ApplyView(); }

    void ApplyView()
    {
        var browser = (BrowserBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var view = _all.Where(x => browser.Length == 0 || x.Ext.Browser.Name == browser)
                       .Where(x => SuspiciousOnly.IsChecked != true || x.Ext.IsSuspicious)
                       .OrderByDescending(x => x.Ext.IsSuspicious)
                       .ThenBy(x => x.Ext.Browser.Name).ThenBy(x => x.Ext.ProfileName)
                       .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                       .ToList();
        ExtList.ItemsSource = view;
        EmptyText.Text = SuspiciousOnly.IsChecked == true ? "Расширений с предупреждениями нет." : "Расширений нет.";
        EmptyText.Visibility = view.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void Action_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ExtensionItemViewModel item || _busy) return;
        var x = item.Ext;

        if (!item.RemovableByCleaner)
        {
            var opened = BrowserExtensions.OpenManagePage(x);
            try { Clipboard.SetText(x.ManageUrl); } catch { }
            ShowStatus(opened ? $"Открыл «{x.Name}» в {x.Browser.Name}" : $"Не нашёл {x.Browser.Name}",
                (opened ? $"Страница расширения открыта в профиле «{x.ProfileName}» — там его можно отключить или нажать «Удалить». " : "") +
                $"Адрес страницы скопирован: если браузер открылся на другой вкладке, вставь его в адресную строку ({x.ManageUrl}).",
                done: true, ok: opened);
            return;
        }

        // Поставлено программой: убираем её ключ(и) в реестре — браузер сам удалит расширение при следующем запуске.
        var sameKey = _all.Where(o => o.Ext.ExternalKeys.Any(k => x.ExternalKeys.Contains(k))).Select(o => $"«{o.Ext.ProfileName}»").Distinct().ToList();
        var text = $"Ключ реестра, через который программа ставит «{x.Name}» в {x.Browser.Name}, уйдёт в карантин (его можно вернуть 7 дней). " +
                   $"При следующем запуске {x.Browser.Name} сам удалит расширение" +
                   (sameKey.Count > 1 ? $" — во всех профилях, где оно есть: {string.Join(", ", sameKey)}." : ".") +
                   (x.Name.Contains("Kaspersky", StringComparison.OrdinalIgnoreCase)
                       ? "\n\nЭто расширение антивируса: пока Kaspersky установлен, он может вернуть его при обновлении." : "") +
                   "\n\nЕсли браузер сейчас открыт, закрой его — удаление сработает при следующем запуске.";
        if (!await CleanPage.Confirm("Удалить расширение?", text, "Удалить")) return;

        _busy = true;
        ShowStatus($"Удаляю «{x.Name}»", "Переношу ключ реестра в карантин…", done: false);
        var (report, adminCancelled) = await Elevation.RemoveAsync(BrowserExtensions.RegistryRemoval(x), $"расширение {x.Name}");
        _busy = false;

        if (adminCancelled)
            ShowStatus("Не удалено", "Для этого ключа нужны права администратора, а запрос Windows был отменён.", done: true, ok: false);
        else if (report.Removed > 0)
            ShowStatus($"«{x.Name}» будет удалено", $"Ключ в карантине. При следующем запуске {x.Browser.Name} уберёт расширение сам. " +
                       "Вернуть можно на вкладке «Очистка» → «Карантин» в течение 7 дней.", done: true, ok: true);
        else
            ShowStatus("Не удалось", "Ключ реестра не удалось перенести — подробности в журнале ошибок.", done: true, ok: false);
        await LoadAsync();
    }

    void CloseStatus_Click(object sender, RoutedEventArgs e) => StatusCard.Visibility = Visibility.Collapsed;

    void ShowStatus(string title, string text, bool done, bool ok = true)
    {
        StatusCard.Visibility = Visibility.Visible;
        StatusTitle.Text = title;
        StatusText.Text = text;
        StatusRing.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        StatusIcon.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        StatusIcon.Symbol = ok ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.Warning24;
        StatusIcon.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
        CloseStatusButton.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
    }

    static string Plural(int n, string one, string few, string many)
    {
        var m10 = n % 10; var m100 = n % 100;
        return m10 == 1 && m100 != 11 ? one : m10 is >= 2 and <= 4 && m100 is < 12 or > 14 ? few : many;
    }
}
