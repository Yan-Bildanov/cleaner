using System.Windows;
using Cleaner.App.Pages;
using Cleaner.Core;
using Cleaner.Core.Cleaning;
using Cleaner.Core.Rules;
using Wpf.Ui.Controls;

namespace Cleaner.App;

public sealed record BatchRow(QuarantineBatch Batch, string Title, string Details);

public partial class QuarantineWindow : FluentWindow
{
    public QuarantineWindow()
    {
        InitializeComponent();
        Refresh();
    }

    void Refresh()
    {
        var names = BuiltInRules.All.ToDictionary(r => r.Id, r => r.Name);
        var rows = App.Quarantine.List().Select(b =>
        {
            var what = b.Entries.Select(e => names.GetValueOrDefault(e.RuleId, e.RuleId)).Distinct().Take(4).ToList();
            var whatText = string.Join(", ", what) + (b.Entries.Select(e => e.RuleId).Distinct().Count() > 4 ? "…" : "");
            var expires = b.ExpiresUtc.ToLocalTime();
            return new BatchRow(b,
                $"Очистка {b.CreatedUtc.ToLocalTime():d MMMM, HH:mm}",
                $"{b.Entries.Count:N0} файлов · {Format.Bytes(b.TotalBytes)} · удалится навсегда {expires:d MMMM}\n{whatText}");
        }).ToList();

        BatchList.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static QuarantineBatch BatchOf(object sender) => ((BatchRow)((FrameworkElement)sender).DataContext).Batch;

    async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var batch = BatchOf(sender);
        var r = await Task.Run(() => App.Quarantine.Restore(batch));
        StatusText.Text = $"Возвращено на место: {r.Restored:N0} файлов." +
            (r.SkippedExisting > 0 ? $" Не возвращено {r.SkippedExisting:N0}: приложение уже создало новые файлы на их месте (старые копии остались здесь)." : "") +
            (r.Failed > 0 ? $" Не удалось вернуть {r.Failed:N0} — обычно это файлы из системных папок, для них нужны права администратора." : "");
        Refresh();
    }

    async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var batch = BatchOf(sender);
        if (!await CleanPage.Confirm("Удалить навсегда",
                $"Удалить {batch.Entries.Count:N0} файлов ({Format.Bytes(batch.TotalBytes)}) навсегда?\nВернуть их будет нельзя.",
                "Удалить навсегда")) return;
        var r = await Task.Run(() => App.Quarantine.Delete(batch));
        StatusText.Text = $"Удалено навсегда, освобождено {Format.Bytes(r.FreedBytes)}.";
        if (r.InUse > 0)
            StatusText.Text += $" Осталось {r.InUse:N0} файлов: их всё ещё держит открытая программа (обычно Chrome или Проводник). " +
                               "Закрой её или перезагрузи ноутбук и нажми «Удалить навсегда» ещё раз.";
        if (r.Denied > 0)
            StatusText.Text += $" Осталось {r.Denied:N0} файлов из системных папок — их можно удалить только с правами администратора " +
                               "(кнопка «Права администратора» на вкладке «Очистка»).";
        Refresh();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
