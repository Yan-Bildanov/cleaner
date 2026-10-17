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
            var leftovers = b.Entries.Any(e => e.RuleId.StartsWith("Хвосты:", StringComparison.Ordinal));
            return new BatchRow(b,
                $"{(leftovers ? "Удаление" : "Очистка")} {b.CreatedUtc.ToLocalTime():d MMMM, HH:mm}",
                $"{Count(b.Entries.Count)} · {Format.Bytes(b.TotalBytes)} · удалится навсегда {expires:d MMMM}\n{whatText}");
        }).ToList();

        BatchList.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>«1 объект», «3 объекта», «5 объектов» — в карантине не только файлы, но и папки, ключи, службы, задачи.</summary>
    static string Count(int n)
    {
        var m10 = n % 10; var m100 = n % 100;
        var word = m10 == 1 && m100 != 11 ? "объект" : m10 is >= 2 and <= 4 && m100 is < 12 or > 14 ? "объекта" : "объектов";
        return $"{n:N0} {word}";
    }

    static QuarantineBatch BatchOf(object sender) => ((BatchRow)((FrameworkElement)sender).DataContext).Batch;

    async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var batch = BatchOf(sender);
        var r = await Task.Run(() => App.Quarantine.Restore(batch));
        StatusText.Text = $"Возвращено на место: {Count(r.Restored)}." +
            (r.SkippedExisting > 0 ? $" Не возвращено {r.SkippedExisting:N0}: приложение уже создало новые файлы на их месте (старые копии остались здесь)." : "") +
            (r.Failed > 0 ? $" Не удалось вернуть {r.Failed:N0} — обычно это файлы из системных папок, для них нужны права администратора." : "");
        Refresh();
    }

    async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var batch = BatchOf(sender);
        if (!await CleanPage.Confirm("Удалить навсегда",
                $"Удалить {Count(batch.Entries.Count)} ({Format.Bytes(batch.TotalBytes)}) навсегда?\nВернуть {(batch.Entries.Count == 1 ? "его" : "их")} будет нельзя.",
                "Удалить навсегда")) return;
        var r = await Task.Run(() => App.Quarantine.Delete(batch));
        StatusText.Text = $"Удалено навсегда, освобождено {Format.Bytes(r.FreedBytes)}.";
        if (r.InUse > 0)
        {
            var who = r.Holders is { Count: > 0 } h ? string.Join(", ", h) : null;
            StatusText.Text += who is null
                ? $" Осталось {Count(r.InUse)}: их всё ещё держит открытая программа (обычно Chrome или Проводник). " +
                  "Закрой её или перезагрузи ноутбук и нажми «Удалить навсегда» ещё раз."
                : $" Осталось {Count(r.InUse)}: их держит {who}. " +
                  (r.Holders!.Contains("Проводник")
                      ? "Перезапусти Проводник (Диспетчер задач → «Проводник» → «Перезапустить») или перезагрузи ноутбук"
                      : "Закрой эту программу или перезагрузи ноутбук") +
                  " и нажми «Удалить навсегда» ещё раз.";
        }
        if (r.Denied > 0)
            StatusText.Text += $" Осталось {Count(r.Denied)} из системных папок — их можно удалить только с правами администратора " +
                               "(кнопка «Права администратора» на вкладке «Очистка»).";
        Refresh();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
