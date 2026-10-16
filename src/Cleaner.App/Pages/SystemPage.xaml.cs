using System.Windows;
using System.Windows.Controls;
using Cleaner.App.ViewModels;
using Cleaner.Core.Health;

namespace Cleaner.App.Pages;

public partial class SystemPage : Page
{
    // Результат последней проверки живёт между переключениями вкладок — чтобы не ждать каждый раз.
    static (IReadOnlyList<HealthCheck> Checks, HardwareInfo Hardware, DateTime At)? _last;

    public SystemPage()
    {
        InitializeComponent();
        HealthCollector.OnError = App.Log;
        AdminButton.Visibility = App.SystemInfo.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        Loaded += async (_, _) =>
        {
            if (_last is { } last) Show(last.Checks, last.Hardware, last.At);
            else await RunAsync();
        };
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync();

    void Admin_Click(object sender, RoutedEventArgs e)
    {
        if (!App.RelaunchElevated()) SummaryText.Text = "Запуск от администратора отменён.";
    }

    async Task RunAsync()
    {
        RefreshButton.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        ChecksList.Visibility = Visibility.Collapsed;
        SummaryText.Text = "Проверяю… (несколько секунд)";

        var (checks, hardware) = await Task.Run(() =>
            (new HealthCollector(App.SystemInfo).RunAll(), HardwareCollector.Read()));
        _last = (checks, hardware, DateTime.Now);
        Show(checks, hardware, DateTime.Now);

        Busy.Visibility = Visibility.Collapsed;
        ChecksList.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = true;
    }

    void Show(IReadOnlyList<HealthCheck> checks, HardwareInfo hw, DateTime at)
    {
        Busy.Visibility = Visibility.Collapsed;
        ChecksList.ItemsSource = checks.Select(c => new HealthItemViewModel(c)).ToList();

        int problems = checks.Count(c => c.Status == HealthStatus.Problem),
            warnings = checks.Count(c => c.Status == HealthStatus.Warning);
        var verdict = (problems, warnings) switch
        {
            (0, 0) => "Всё в порядке",
            (0, _) => $"Есть на что обратить внимание: {warnings}",
            _ => $"Проблем: {problems}, на что обратить внимание: {warnings}",
        };
        SummaryText.Text = $"{verdict} · проверено в {at:HH:mm}. Нажми на строку, чтобы увидеть подробности.";

        var rows = new List<HardwareRow>
        {
            new("Модель", hw.Model),
            new("Система", hw.Os),
            new("Процессор", hw.Cpu),
            new("Память", hw.Ram),
        };
        rows.AddRange(hw.RamModules.Select((m, i) => new HardwareRow(i == 0 ? "Планки памяти" : "", m)));
        rows.AddRange(hw.Gpus.Select((g, i) => new HardwareRow(i == 0 ? "Видеокарты" : "", g)));
        rows.AddRange(hw.Disks.Select((d, i) => new HardwareRow(i == 0 ? "Накопители" : "", d)));
        rows.Add(new("BIOS", hw.Bios));
        HardwareList.ItemsSource = rows;
        HardwareTitle.Visibility = HardwareCard.Visibility = Visibility.Visible;
    }
}
