using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Cleaner.App.ViewModels;
using Cleaner.Core;
using Cleaner.Core.Programs;
using Wpf.Ui.Controls;

namespace Cleaner.App.Pages;

public partial class ProgramsPage : Page
{
    // Список живёт между переключениями вкладок, чтобы не перечитывать реестр и значки каждый раз.
    static List<ProgramItemViewModel>? _all;
    static DateTime _loadedAt;

    CancellationTokenSource? _stopWaiting;
    bool _uninstalling;

    public ProgramsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (_all is not null) { Busy.Visibility = Visibility.Collapsed; ApplyView(); }
            else await LoadAsync();
        };
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    async Task LoadAsync()
    {
        RefreshButton.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        ProgramsList.Visibility = HeaderRow.Visibility = Visibility.Collapsed;
        SummaryText.Text = "Читаю список установленных программ…";

        var programs = await Task.Run(ProgramInventory.ReadAll);
        _all = programs.Select(p => new ProgramItemViewModel(p)).ToList();
        _loadedAt = DateTime.Now;

        Busy.Visibility = Visibility.Collapsed;
        ProgramsList.Visibility = HeaderRow.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = true;
        ApplyView();

        _ = FillDetailsAsync(_all);
    }

    /// <summary>Значки и недостающие размеры — в фоне, список уже виден.</summary>
    async Task FillDetailsAsync(List<ProgramItemViewModel> items)
    {
        await Task.Run(() =>
        {
            Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = 4 }, item =>
            {
                var icon = ProgramIcons.Load(item.Program);
                if (icon is not null) Dispatcher.BeginInvoke(() => item.Icon = icon);
            });
        });

        // Размер по папке — только для своих папок программ и не больше 2 одновременно, чтобы не грузить диск.
        var needSize = items.Where(i => i.SizeBytes is null && ProgramInventory.IsProgramOwnFolder(i.Program.InstallLocation)).ToList();
        await Task.Run(() =>
        {
            Parallel.ForEach(needSize, new ParallelOptions { MaxDegreeOfParallelism = 2 }, item =>
            {
                var bytes = ProgramInventory.MeasureFolder(item.Program.InstallLocation!);
                if (bytes > 0) Dispatcher.BeginInvoke(() => item.SizeBytes = bytes);
            });
        });
        if (ReferenceEquals(items, _all)) Dispatcher.Invoke(UpdateSummary);
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e) => ApplyView();
    void Sort_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) ApplyView(); }

    void ApplyView()
    {
        if (_all is null) return;
        var q = SearchBox.Text?.Trim() ?? "";
        IEnumerable<ProgramItemViewModel> view = _all.Where(i => i.Matches(q));
        view = SortBox.SelectedIndex switch
        {
            1 => view.OrderByDescending(i => i.SizeBytes ?? -1),
            2 => view.OrderByDescending(i => i.Program.InstallDate ?? DateTime.MinValue),
            _ => view.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        var list = view.ToList();
        ProgramsList.ItemsSource = list;
        EmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSummary();
    }

    void UpdateSummary()
    {
        if (_all is null) return;
        var total = _all.Sum(i => i.SizeBytes ?? 0);
        SummaryText.Text = $"Установлено программ: {_all.Count:N0} · занимают около {Format.Bytes(total)} · список от {_loadedAt:HH:mm}";
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProgramItemViewModel item || item.Program.InstallLocation is not { } dir) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); }
        catch (Exception ex) { App.Log(ex); }
    }

    async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProgramItemViewModel item) return;
        if (_uninstalling)
        {
            ShowStatus("Уже идёт удаление", "Дождись, пока закончится текущее удаление.", done: true, ok: false);
            return;
        }

        var p = item.Program;
        var text = $"Запустится собственный деинсталлятор «{p.Name}». Пройди его шаги в открывшемся окне.\n\n" +
                   "После этого Cleaner поищет оставшиеся папки, ярлыки и записи реестра и покажет их — без твоего подтверждения ничего не удалит.";
        if (!await CleanPage.Confirm("Удалить программу?", text, "Удалить")) return;

        _uninstalling = true;
        item.IsBusy = true;
        _stopWaiting = new CancellationTokenSource();
        ShowStatus($"Удаляю «{p.Name}»", "Запускаю деинсталлятор…", done: false);

        var progress = new Progress<string>(msg => StatusText.Text = msg);
        UninstallResult result;
        try { result = await Uninstaller.RunAsync(p, progress, _stopWaiting.Token); }
        catch (Exception ex) { App.Log(ex); result = new(UninstallOutcome.Failed, ex.Message); }

        item.IsBusy = false;
        _uninstalling = false;

        switch (result.Outcome)
        {
            case UninstallOutcome.Removed:
                _all?.Remove(item);
                ApplyView();
                ShowStatus(result.Message, "Ищу оставшиеся папки, ярлыки и записи реестра…", done: false);
                StopWaitingButton.Visibility = Visibility.Collapsed;
                await ScanLeftoversAsync(p);
                break;
            case UninstallOutcome.Cancelled:
                ShowStatus("Удаление отменено", result.Message, done: true, ok: false);
                break;
            default:
                ShowStatus("Программа не удалена", result.Message, done: true, ok: false);
                break;
        }
    }

    // ── Хвосты ─────────────────────────────────────────────────────────────────────────────────
    List<LeftoverItemViewModel> _leftovers = [];
    string _leftoversFor = "";

    async Task ScanLeftoversAsync(InstalledProgram removed)
    {
        IReadOnlyList<Leftover> found;
        try
        {
            found = await Task.Run(() =>
            {
                var still = ProgramInventory.ReadAll();     // свежий список: что-то могло удалиться вместе с программой
                return LeftoverScanner.ForCurrentSystem().Scan(removed, still);
            });
        }
        catch (Exception ex)
        {
            App.Log(ex);
            ShowStatus($"«{removed.Name}» удалена", "Не удалось поискать оставшиеся файлы — подробности в журнале ошибок.", done: true, ok: false);
            return;
        }

        if (found.Count == 0)
        {
            ShowStatus($"«{removed.Name}» удалена", "Хвостов не найдено — программа удалилась чисто.", done: true, ok: true);
            return;
        }

        StatusCard.Visibility = Visibility.Collapsed;
        _leftoversFor = removed.Name;
        _leftovers = found.Select(l => new LeftoverItemViewModel(l)).ToList();
        foreach (var l in _leftovers) l.CheckedChanged += UpdateLeftoversButton;
        LeftoversList.ItemsSource = _leftovers;
        var bytes = found.Sum(l => l.Size);
        LeftoversTitle.Text = $"«{removed.Name}» удалена. Осталось: {found.Count} " + Plural(found.Count, "объект", "объекта", "объектов")
                              + (bytes > 0 ? $" · {Format.Bytes(bytes)}" : "");
        LeftoversCard.Visibility = Visibility.Visible;
        UpdateLeftoversButton();
    }

    void UpdateLeftoversButton() => RemoveLeftoversButton.IsEnabled = _leftovers.Any(l => l.IsChecked);

    void KeepLeftovers_Click(object sender, RoutedEventArgs e)
    {
        LeftoversCard.Visibility = Visibility.Collapsed;
        _leftovers = [];
    }

    async void RemoveLeftovers_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _leftovers.Where(l => l.IsChecked).Select(l => l.Leftover).ToList();
        if (chosen.Count == 0) return;

        if (chosen.Any(l => l.Confidence == LeftoverConfidence.Medium)
            && !await CleanPage.Confirm("Удалить папки издателя?",
                   "Среди отмеченного есть папки или ключи, совпавшие только по имени издателя. Если у этого издателя есть другие программы, " +
                   "в них могут быть их настройки. Всё уйдёт в карантин на 7 дней, и это можно будет вернуть.", "Удалить"))
            return;

        RemoveLeftoversButton.IsEnabled = false;
        var label = _leftoversFor;
        var elevated = App.SystemInfo.IsElevated;
        var report = await Task.Run(() => LeftoverRemover.Remove(App.Quarantine, chosen, elevated, label));

        // Часть пунктов (Program Files, HKLM) требует прав администратора — делаем их отдельным процессом через UAC.
        int removed = report.Removed, locked = report.Locked, denied = report.Denied;
        long bytes = report.RemovedBytes;
        var adminCancelled = false;
        if (report.NeedAdmin.Count > 0)
        {
            var admin = await RunElevatedAsync(new LeftoverPlan(label, report.NeedAdmin.ToList()));
            if (admin is null) adminCancelled = true;
            else { removed += admin.Removed; locked += admin.Locked; denied += admin.Denied; bytes += admin.RemovedBytes; }
        }

        LeftoversCard.Visibility = Visibility.Collapsed;
        _leftovers = [];

        var lines = new List<string> { $"В карантин перенесено: {removed} " + Plural(removed, "объект", "объекта", "объектов") + (bytes > 0 ? $" ({Format.Bytes(bytes)})" : "") + "." };
        if (locked > 0) lines.Add($"Занято другой программой и пропущено: {locked} — закрой её и удали вручную или перезагрузись.");
        if (denied > 0) lines.Add($"Не хватило прав: {denied}.");
        if (adminCancelled) lines.Add($"Пункты, которым нужны права администратора ({report.NeedAdmin.Count}), пропущены: запрос UAC отменён.");
        lines.Add("Вернуть можно на вкладке «Очистка» → «Карантин» в течение 7 дней.");
        ShowStatus($"Хвосты «{label}» убраны", string.Join("\n", lines), done: true, ok: locked == 0 && denied == 0 && !adminCancelled);
    }

    /// <summary>Запускает Cleaner.exe --remove-leftovers через UAC и ждёт. null — пользователь отказался.</summary>
    static async Task<LeftoverRemoveReport?> RunElevatedAsync(LeftoverPlan plan)
    {
        var file = LeftoverPlan.Save(plan);
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"{LeftoverPlan.Argument} \"{file}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            if (proc is null) return null;
            await proc.WaitForExitAsync();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            try { File.Delete(file); } catch { }
            return null;
        }
        return LeftoverPlan.ReadResult(file);
    }

    static string Plural(int n, string one, string few, string many)
    {
        var m10 = n % 10; var m100 = n % 100;
        return m10 == 1 && m100 != 11 ? one : m10 is >= 2 and <= 4 && m100 is < 12 or > 14 ? few : many;
    }

    void StopWaiting_Click(object sender, RoutedEventArgs e) => _stopWaiting?.Cancel();
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
        StopWaitingButton.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        CloseStatusButton.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
    }
}
