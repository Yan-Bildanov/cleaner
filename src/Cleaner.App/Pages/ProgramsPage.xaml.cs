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
    static ProgramsPage? _current;

    CancellationTokenSource? _stopWaiting;
    bool _busy;

    public ProgramsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            _current = this;
            if (_all is not null) { Busy.Visibility = Visibility.Collapsed; ProgramsList.Visibility = HeaderRow.Visibility = Visibility.Visible; ApplyView(); }
            else await LoadAsync();
        };
        Unloaded += (_, _) => { if (ReferenceEquals(_current, this)) _current = null; };
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    async Task LoadAsync()
    {
        RefreshButton.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        ProgramsList.Visibility = HeaderRow.Visibility = Visibility.Collapsed;
        SummaryText.Text = "Читаю список установленных программ…";

        var programs = await Task.Run(() => ProgramInventory.ReadAll().Concat(StoreApps.ReadAll()).ToList());
        _all = programs.Select(p =>
        {
            var vm = new ProgramItemViewModel(p);
            vm.CheckedChanged += () => _current?.UpdateBatchBar();
            return vm;
        }).ToList();
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

    List<ProgramItemViewModel> _shown = [];

    void ApplyView()
    {
        if (_all is null) return;
        var q = SearchBox.Text?.Trim() ?? "";
        IEnumerable<ProgramItemViewModel> view = _all.Where(i => i.Matches(q));
        view = KindBox.SelectedIndex switch
        {
            1 => view.Where(i => !i.Program.IsStoreApp),
            2 => view.Where(i => i.Program.IsStoreApp),
            _ => view,
        };
        view = SortBox.SelectedIndex switch
        {
            1 => view.OrderByDescending(i => i.SizeBytes ?? -1),
            2 => view.OrderByDescending(i => i.Program.InstallDate ?? DateTime.MinValue),
            _ => view.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        _shown = view.ToList();
        ProgramsList.ItemsSource = _shown;
        EmptyText.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSummary();
        UpdateBatchBar();
    }

    void UpdateSummary()
    {
        if (_all is null) return;
        var total = _all.Sum(i => i.SizeBytes ?? 0);
        var store = _all.Count(i => i.Program.IsStoreApp);
        SummaryText.Text = $"Программ: {_all.Count - store:N0}, приложений Microsoft Store: {store:N0} · занимают около {Format.Bytes(total)} · список от {_loadedAt:HH:mm}";
    }

    // ── Выбор для пакетного удаления ───────────────────────────────────────────────────────────
    void UpdateBatchBar()
    {
        if (_all is null) return;
        var n = _all.Count(i => i.IsChecked);
        BatchBar.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        BatchText.Text = $"Выбрано: {n}";
        RemoveSelectedButton.IsEnabled = n > 0 && !_busy;
        SelectAllCheck.IsChecked = _shown.Count > 0 && _shown.Where(i => i.CanUninstall).All(i => i.IsChecked) ? true
                                 : _shown.Any(i => i.IsChecked) ? null : false;
    }

    void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var check = _shown.Where(i => i.CanUninstall).Any(i => !i.IsChecked);
        foreach (var i in _shown.Where(i => i.CanUninstall)) i.IsChecked = check;
    }

    void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_all is null) return;
        foreach (var i in _all) i.IsChecked = false;
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProgramItemViewModel item || item.Program.InstallLocation is not { } dir) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); }
        catch (Exception ex) { App.Log(ex); }
    }

    bool EnsureNotBusy()
    {
        if (!_busy) return true;
        ShowStatus("Уже идёт удаление", "Дождись, пока закончится текущее удаление.", done: true, ok: false);
        return false;
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;
        UpdateBatchBar();
    }

    // ── Удаление одной программы ───────────────────────────────────────────────────────────────
    async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProgramItemViewModel item || !EnsureNotBusy()) return;
        if (item.IsBroken) { await ForceAsync(item.Program, brokenUninstaller: true); return; }

        var p = item.Program;
        var text = p.IsStoreApp
            ? $"Приложение «{p.Name}» будет удалено вместе с его данными — так же, как через «Параметры → Приложения»."
            : $"Запустится собственный деинсталлятор «{p.Name}». Пройди его шаги в открывшемся окне.\n\n" +
              "После этого Cleaner поищет оставшиеся папки, ярлыки, службы и записи реестра и покажет их — без твоего подтверждения ничего не удалит.";
        if (!await CleanPage.Confirm(p.IsStoreApp ? "Удалить приложение?" : "Удалить программу?", text, "Удалить")) return;

        SetBusy(true);
        var result = await RunUninstallerAsync(item, $"Удаляю «{p.Name}»", quiet: false);
        SetBusy(false);

        switch (result.Outcome)
        {
            case UninstallOutcome.Removed when p.IsStoreApp:
                ShowStatus(result.Message, "Приложение и его данные удалены Windows.", done: true, ok: true);
                break;
            case UninstallOutcome.Removed:
                ShowStatus(result.Message, "Ищу оставшиеся папки, ярлыки, службы и записи реестра…", done: false);
                StopWaitingButton.Visibility = Visibility.Collapsed;
                await ScanLeftoversAsync([p], $"«{p.Name}» удалена");
                break;
            case UninstallOutcome.Cancelled:
                ShowStatus("Удаление отменено", result.Message, done: true, ok: false);
                break;
            default:
                ShowStatus("Программа не удалена", result.Message + (p.IsStoreApp ? "" : "\nЕсли деинсталлятор не справляется, программу можно удалить принудительно."),
                           done: true, ok: false, forceFor: p.IsStoreApp ? null : p);
                break;
        }
    }

    async Task<UninstallResult> RunUninstallerAsync(ProgramItemViewModel item, string title, bool quiet)
    {
        item.IsBusy = true;
        _stopWaiting = new CancellationTokenSource();
        ShowStatus(title, "Запускаю деинсталлятор…", done: false);
        var progress = new Progress<string>(msg => StatusText.Text = msg);
        UninstallResult result;
        try { result = await Uninstaller.RunAsync(item.Program, progress, _stopWaiting.Token, quiet); }
        catch (Exception ex) { App.Log(ex); result = new(UninstallOutcome.Failed, ex.Message); }
        item.IsBusy = false;
        if (result.Outcome == UninstallOutcome.Removed)
        {
            item.IsChecked = false;
            _all?.Remove(item);
            ApplyView();
        }
        return result;
    }

    // ── Пакетное удаление ──────────────────────────────────────────────────────────────────────
    async void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_all is null || !EnsureNotBusy()) return;
        var chosen = _all.Where(i => i.IsChecked && i.CanUninstall).ToList();
        if (chosen.Count == 0) return;

        var broken = chosen.Where(i => i.IsBroken).ToList();
        var toRun = chosen.Except(broken).ToList();
        var quiet = QuietCheck.IsChecked == true;

        var names = string.Join("\n", toRun.Take(12).Select(i => "• " + i.Name)) + (toRun.Count > 12 ? $"\n… и ещё {toRun.Count - 12}" : "");
        var text = $"Будут удалены по очереди ({toRun.Count}):\n{names}\n\n" +
                   (quiet ? "Где деинсталлятор это умеет — тихо, без вопросов; остальные покажут своё окно."
                          : "Каждая программа покажет окно своего деинсталлятора.") +
                   (broken.Count > 0 ? $"\n\nПропущены — у них пропал деинсталлятор, их можно удалить только принудительно по одной: " +
                                       string.Join(", ", broken.Select(b => b.Name)) + "." : "") +
                   "\n\nПосле этого Cleaner поищет хвосты всех удалённых программ разом.";
        if (toRun.Count == 0)
        {
            ShowStatus("Нечего удалять штатно", "У всех выбранных программ пропал деинсталлятор — удали их кнопкой «Принудительно» по одной.", done: true, ok: false);
            return;
        }
        if (!await CleanPage.Confirm($"Удалить выбранные ({toRun.Count})?", text, "Удалить")) return;

        SetBusy(true);
        LeftoversCard.Visibility = Visibility.Collapsed;
        var removed = new List<InstalledProgram>();
        var notRemoved = new List<string>();
        for (var i = 0; i < toRun.Count; i++)
        {
            var item = toRun[i];
            var result = await RunUninstallerAsync(item, $"{i + 1} из {toRun.Count}: удаляю «{item.Name}»", quiet);
            if (result.Outcome == UninstallOutcome.Removed) removed.Add(item.Program);
            else notRemoved.Add($"«{item.Name}» — {result.Message}");
        }
        SetBusy(false);

        var summary = $"Удалено: {removed.Count} из {toRun.Count}." + (notRemoved.Count > 0 ? "\nНе удалены:\n" + string.Join("\n", notRemoved) : "");
        var win32 = removed.Where(p => !p.IsStoreApp).ToList();
        if (win32.Count == 0)
        {
            ShowStatus("Пакетное удаление завершено", summary, done: true, ok: notRemoved.Count == 0);
            return;
        }
        ShowStatus("Пакетное удаление завершено", summary + "\nИщу хвосты…", done: false);
        StopWaitingButton.Visibility = Visibility.Collapsed;
        await ScanLeftoversAsync(win32, $"Удалено программ: {removed.Count}", notRemoved.Count > 0 ? summary : null);
    }

    // ── Принудительное удаление ────────────────────────────────────────────────────────────────
    InstalledProgram? _forceCandidate;

    async void ForceFromStatus_Click(object sender, RoutedEventArgs e)
    {
        if (_forceCandidate is { } p && EnsureNotBusy()) await ForceAsync(p, brokenUninstaller: false);
    }

    async Task ForceAsync(InstalledProgram p, bool brokenUninstaller)
    {
        var intro = brokenUninstaller
            ? $"Деинсталлятор «{p.Name}» пропал с диска, штатно её не удалить."
            : $"Деинсталлятор «{p.Name}» не справился.";
        if (!await CleanPage.Confirm("Удалить принудительно?",
                intro + "\n\nCleaner сам найдёт её папки, ярлыки, службы, задачи Планировщика и записи реестра и покажет список. " +
                "Удалится только то, что ты оставишь отмеченным, и всё уйдёт в карантин на 7 дней.", "Найти"))
            return;

        SetBusy(true);
        ShowStatus($"Принудительное удаление «{p.Name}»", "Ищу файлы, службы и записи программы…", done: false);
        StopWaitingButton.Visibility = Visibility.Collapsed;
        IReadOnlyList<Leftover> plan;
        IReadOnlyList<ProgramProcesses.Running> procs;
        try
        {
            (plan, procs) = await Task.Run(() =>
            {
                var pl = ForcedUninstall.Plan(p, ProgramInventory.ReadAll());
                return (pl, ForcedUninstall.ProcessesToClose(pl));
            });
        }
        catch (Exception ex)
        {
            App.Log(ex);
            SetBusy(false);
            ShowStatus("Не получилось", "Не удалось собрать список — подробности в журнале ошибок.", done: true, ok: false);
            return;
        }
        SetBusy(false);

        var note = "Отмеченное уйдёт в карантин на 7 дней — вернуть можно на вкладке «Очистка» → «Карантин».";
        if (procs.Count > 0)
            note += "\nПеред удалением будут закрыты запущенные из её папки процессы: " +
                    string.Join(", ", procs.Select(r => r.Name).Distinct()) + ".";
        ShowLeftovers(plan, $"Принудительное удаление «{p.Name}»: найдено {plan.Count} " + Plural(plan.Count, "объект", "объекта", "объектов"),
                      p.Name, note, forced: p);
    }

    // ── Хвосты ─────────────────────────────────────────────────────────────────────────────────
    List<LeftoverItemViewModel> _leftovers = [];
    string _leftoversLabel = "";
    InstalledProgram? _leftoversForced;
    const string DefaultLeftoversNote = "Отмеченное уйдёт в карантин на 7 дней — вернуть можно на вкладке «Очистка» → «Карантин». " +
                                        "Пункты без галочки совпали только по имени издателя: проверь их, прежде чем отмечать.";

    async Task ScanLeftoversAsync(IReadOnlyList<InstalledProgram> removed, string title, string? extraNote = null)
    {
        IReadOnlyList<Leftover> found;
        try
        {
            found = await Task.Run(() =>
            {
                var still = ProgramInventory.ReadAll();     // свежий список: что-то могло удалиться вместе с программой
                var scanner = LeftoverScanner.ForCurrentSystem();
                var all = new List<Leftover>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in removed)
                    foreach (var l in scanner.Scan(p, still))
                        if (seen.Add($"{l.Kind}|{l.Path}|{l.View}")) all.Add(l);
                return (IReadOnlyList<Leftover>)all;
            });
        }
        catch (Exception ex)
        {
            App.Log(ex);
            ShowStatus(title, "Не удалось поискать оставшиеся файлы — подробности в журнале ошибок.", done: true, ok: false);
            return;
        }

        if (found.Count == 0)
        {
            ShowStatus(title, (extraNote is null ? "" : extraNote + "\n") + "Хвостов не найдено — всё удалилось чисто.", done: true, ok: extraNote is null);
            return;
        }

        var label = removed.Count == 1 ? removed[0].Name : $"{removed.Count} программ";
        ShowLeftovers(found, $"{title}. Осталось: {found.Count} " + Plural(found.Count, "объект", "объекта", "объектов"),
                      label, (extraNote is null ? "" : extraNote + "\n\n") + DefaultLeftoversNote, forced: null);
    }

    void ShowLeftovers(IReadOnlyList<Leftover> found, string title, string label, string note, InstalledProgram? forced)
    {
        StatusCard.Visibility = Visibility.Collapsed;
        _leftoversLabel = label;
        _leftoversForced = forced;
        _leftovers = found.Select(l => new LeftoverItemViewModel(l)).ToList();
        foreach (var l in _leftovers) l.CheckedChanged += UpdateLeftoversButton;
        LeftoversList.ItemsSource = _leftovers;
        var bytes = found.Sum(l => l.Size);
        LeftoversTitle.Text = title + (bytes > 0 ? $" · {Format.Bytes(bytes)}" : "");
        LeftoversNote.Text = note;
        RemoveLeftoversButton.Content = forced is null ? "Удалить отмеченное" : "Удалить принудительно";
        LeftoversCard.Visibility = Visibility.Visible;
        UpdateLeftoversButton();
    }

    void UpdateLeftoversButton() => RemoveLeftoversButton.IsEnabled = _leftovers.Any(l => l.IsChecked) && !_busy;

    void KeepLeftovers_Click(object sender, RoutedEventArgs e)
    {
        LeftoversCard.Visibility = Visibility.Collapsed;
        _leftovers = [];
        _leftoversForced = null;
    }

    async void RemoveLeftovers_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _leftovers.Where(l => l.IsChecked).Select(l => l.Leftover).ToList();
        if (chosen.Count == 0 || !EnsureNotBusy()) return;

        if (chosen.Any(l => l.Confidence == LeftoverConfidence.Medium)
            && !await CleanPage.Confirm("Удалить сомнительные пункты?",
                   "Среди отмеченного есть папки или ключи, совпавшие только по имени издателя или похожие на другую программу. " +
                   "В них могут быть чужие настройки. Всё уйдёт в карантин на 7 дней, и это можно будет вернуть.", "Удалить"))
            return;

        var forced = _leftoversForced;
        var label = _leftoversLabel;
        SetBusy(true);
        RemoveLeftoversButton.IsEnabled = false;
        var elevated = App.SystemInfo.IsElevated;
        var report = await Task.Run(() => LeftoverRemover.Remove(App.Quarantine, chosen, elevated, label, killProcesses: forced is not null));

        // Часть пунктов (Program Files, HKLM, службы) требует прав администратора — делаем их отдельным процессом через UAC.
        int removed = report.Removed, locked = report.Locked, denied = report.Denied;
        long bytes = report.RemovedBytes;
        var adminCancelled = false;
        if (report.NeedAdmin.Count > 0)
        {
            var admin = await RunElevatedAsync(new LeftoverPlan(label, report.NeedAdmin.ToList(), KillProcesses: forced is not null));
            if (admin is null) adminCancelled = true;
            else { removed += admin.Removed; locked += admin.Locked; denied += admin.Denied; bytes += admin.RemovedBytes; }
        }
        SetBusy(false);

        LeftoversCard.Visibility = Visibility.Collapsed;
        _leftovers = [];
        _leftoversForced = null;

        if (forced is not null && _all is not null && !ProgramInventory.Exists(forced))
        {
            _all.RemoveAll(i => i.Program.Scope == forced.Scope && i.Program.RegistryKeyName == forced.RegistryKeyName);
            ApplyView();
        }

        var lines = new List<string> { $"В карантин перенесено: {removed} " + Plural(removed, "объект", "объекта", "объектов") + (bytes > 0 ? $" ({Format.Bytes(bytes)})" : "") + "." };
        if (locked > 0) lines.Add($"Занято другой программой и пропущено: {locked} — закрой её и повтори или перезагрузись.");
        if (denied > 0) lines.Add($"Не удалось (нет прав или защищено системой): {denied}.");
        if (adminCancelled) lines.Add($"Пункты, которым нужны права администратора ({report.NeedAdmin.Count}), пропущены: запрос UAC отменён.");
        lines.Add("Вернуть можно на вкладке «Очистка» → «Карантин» в течение 7 дней.");
        var title = forced is not null
            ? (ProgramInventory.Exists(forced) ? $"«{label}» удалена не полностью" : $"«{label}» удалена принудительно")
            : $"Хвосты убраны";
        ShowStatus(title, string.Join("\n", lines), done: true, ok: locked == 0 && denied == 0 && !adminCancelled);
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

    void ShowStatus(string title, string text, bool done, bool ok = true, InstalledProgram? forceFor = null)
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
        _forceCandidate = forceFor;
        ForceButton.Visibility = forceFor is not null ? Visibility.Visible : Visibility.Collapsed;
    }
}
