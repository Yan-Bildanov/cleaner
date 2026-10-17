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
            RestoreMonitorState();
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

        var (programs, traced) = await Task.Run(() =>
        {
            var list = ProgramInventory.ReadAll().Concat(StoreApps.ReadAll()).ToList();
            var keys = TraceStore.Default.All().SelectMany(t => t.Programs).Select(x => x.Scope + "|" + x.RegistryKeyName)
                                 .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return (list, keys);
        });
        _all = programs.Select(p =>
        {
            var vm = new ProgramItemViewModel(p) { IsTraced = traced.Contains(p.Scope + "|" + p.RegistryKeyName) };
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
        _leftoversPrograms = [p];
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
                var traces = TraceStore.Default;
                IReadOnlyList<Leftover> all = [];
                foreach (var p in removed)
                    all = InstallMonitor.Combine(all, InstallMonitor.LeftoversFor(p, still, scanner, traces));
                return all;
            });
        }
        catch (Exception ex)
        {
            App.Log(ex);
            ShowStatus(title, "Не удалось поискать оставшиеся файлы — подробности в журнале ошибок.", done: true, ok: false);
            return;
        }

        _leftoversPrograms = removed.ToList();
        if (found.Count == 0)
        {
            ForgetTraces(removed);
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
        if (_leftoversForced is null) ForgetTraces(_leftoversPrograms);
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
        ForgetTraces(_leftoversPrograms);

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

    List<InstalledProgram> _leftoversPrograms = [];

    /// <summary>Записи монитора для программ, которых больше нет, больше не нужны.</summary>
    static void ForgetTraces(IEnumerable<InstalledProgram> programs)
    {
        try
        {
            var store = TraceStore.Default;
            foreach (var p in programs)
                if (!ProgramInventory.Exists(p) && store.FindFor(p) is { } t) store.Delete(t);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    // ── Монитор установок ──────────────────────────────────────────────────────────────────────
    // Снимок «до» живёт между переходами по вкладкам и на диске (pending.json) — на случай, если окно закроют посреди установки.
    static InstallSnapshot? _before;
    static InstallTrace? _trace;
    List<LeftoverItemViewModel> _traceItems = [];

    enum MonitorStage { Hidden, Working, Watching, Result }

    void SetMonitor(MonitorStage stage, string title = "", string text = "")
    {
        MonitorCard.Visibility = stage == MonitorStage.Hidden ? Visibility.Collapsed : Visibility.Visible;
        MonitorTitle.Text = title;
        MonitorText.Text = text;
        MonitorRing.Visibility = stage == MonitorStage.Working ? Visibility.Visible : Visibility.Collapsed;
        MonitorIcon.Visibility = stage == MonitorStage.Working ? Visibility.Collapsed : Visibility.Visible;
        MonitorLaunchButton.Visibility = stage == MonitorStage.Watching ? Visibility.Visible : Visibility.Collapsed;
        MonitorDoneButton.Visibility = stage == MonitorStage.Watching ? Visibility.Visible : Visibility.Collapsed;
        MonitorSaveButton.Visibility = stage == MonitorStage.Result ? Visibility.Visible : Visibility.Collapsed;
        MonitorCancelButton.Visibility = stage is MonitorStage.Watching or MonitorStage.Result ? Visibility.Visible : Visibility.Collapsed;
        MonitorCancelButton.Content = stage == MonitorStage.Result ? (_trace?.Programs.Count > 0 ? "Удалить запись" : "Закрыть") : "Отменить";
        MonitorSaveButton.Content = "Готово";
        MonitorList.Visibility = stage == MonitorStage.Result ? Visibility.Visible : Visibility.Collapsed;
        MonitorButton.IsEnabled = stage == MonitorStage.Hidden;
    }

    const string WatchingText =
        "Снимок «до» сделан. Теперь установи программу как обычно: кнопкой ниже или как угодно ещё (из браузера, из папки «Загрузки»). " +
        "Во время установки лучше не ставить и не удалять ничего другого. Когда установщик закончит и программа запустится, нажми «Установка завершена».";

    void RestoreMonitorState()
    {
        if (_trace is not null) { ShowTraceResult(_trace); return; }
        _before ??= TraceStore.Default.LoadPending();
        if (_before is not null && DateTime.UtcNow - _before.TakenUtc < TimeSpan.FromDays(3))
            SetMonitor(MonitorStage.Watching, "Идёт отслеживание установки", WatchingText +
                       $"\nСнимок сделан {_before.TakenUtc.ToLocalTime():dd.MM HH:mm}.");
        else
        {
            if (_before is not null) TraceStore.Default.ClearPending();
            _before = null;
            SetMonitor(MonitorStage.Hidden);
        }
    }

    async void StartMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureNotBusy()) return;
        SetMonitor(MonitorStage.Working, "Запоминаю систему перед установкой…",
                   "Записываю папки программ, ярлыки, реестр, службы, задачи и автозагрузку. Это займёт несколько секунд.");
        try
        {
            _before = await Task.Run(() => InstallMonitor.Capture(SnapshotScope.ForCurrentSystem()));
            TraceStore.Default.SavePending(_before);
            SetMonitor(MonitorStage.Watching, "Идёт отслеживание установки", WatchingText);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            _before = null;
            SetMonitor(MonitorStage.Hidden);
            ShowStatus("Не получилось", "Не удалось сделать снимок системы — подробности в журнале ошибок.", done: true, ok: false);
        }
    }

    void MonitorLaunch_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выбери установщик программы",
            Filter = "Установщики (*.exe; *.msi)|*.exe;*.msi|Все файлы|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
        if (dlg.ShowDialog() != true) return;
        try { Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
        catch (Exception ex) { App.Log(ex); ShowStatus("Не удалось запустить установщик", ex.Message, done: true, ok: false); }
    }

    async void MonitorDone_Click(object sender, RoutedEventArgs e)
    {
        if (_before is null) { SetMonitor(MonitorStage.Hidden); return; }
        SetMonitor(MonitorStage.Working, "Сравниваю систему до и после установки…", "Делаю второй снимок и ищу, что появилось.");
        try
        {
            var before = _before;
            var trace = await Task.Run(() =>
            {
                var after = InstallMonitor.Capture(SnapshotScope.ForCurrentSystem());
                return InstallMonitor.Diff(before, after);
            });
            _trace = trace;
            // Запись сохраняется сразу, чтобы её нельзя было потерять (обновить список, закрыть окно, начать новое отслеживание).
            // «Готово» сохраняет снятые галочки, «Удалить запись» — удаляет её.
            if (trace.Programs.Count > 0 && trace.Items.Count > 0)
            {
                try { TraceStore.Default.Save(trace); }
                catch (Exception ex) { App.Log(ex); }
            }
            TraceStore.Default.ClearPending();
            _before = null;
            ShowTraceResult(trace);
            if (trace.Programs.Count > 0) await LoadAsync();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            SetMonitor(MonitorStage.Watching, "Идёт отслеживание установки", "Не удалось сделать второй снимок — попробуй ещё раз. Подробности в журнале ошибок.");
        }
    }

    void ShowTraceResult(InstallTrace trace)
    {
        _traceItems = trace.Items.Select(i => new LeftoverItemViewModel(new Leftover(i.Kind, i.Path, LeftoverConfidence.High,
            "появилось во время установки", Size: i.Kind is LeftoverKind.File or LeftoverKind.Shortcut ? SafeSize(i.Path) : 0,
            FileCount: i.Kind == LeftoverKind.Folder ? -1 : 1, View: i.View, ValueName: i.ValueName))).ToList();
        MonitorList.ItemsSource = _traceItems;

        string title, text;
        if (trace.Programs.Count > 0)
        {
            title = "Установлено: " + string.Join(", ", trace.Programs.Select(p => p.Name));
            text = $"Во время установки появилось {_traceItems.Count} " + Plural(_traceItems.Count, "объект", "объекта", "объектов") +
                   ". Запись уже сохранена. Если что-то здесь точно не относится к этой программе (например, в это время обновлялась другая), " +
                   "сними галочку и нажми «Готово». Когда будешь удалять программу, всё отмеченное найдётся автоматически.";
        }
        else
        {
            title = "Новых программ в списке установленных не появилось";
            text = _traceItems.Count > 0
                ? "Похоже, установщик ещё не закончил или программа не регистрируется в списке установленных. Запись можно сохранить, но " +
                  "привязать её к программе не получится. Лучше нажать «Закрыть» и начать заново, если установка ещё идёт."
                : "Изменений не найдено. Возможно, установщик ещё не закончил работу.";
        }
        SetMonitor(MonitorStage.Result, title, text);
        MonitorSaveButton.IsEnabled = trace.Programs.Count > 0 && _traceItems.Count > 0;
    }

    static long SafeSize(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }

    async void MonitorSave_Click(object sender, RoutedEventArgs e)
    {
        if (_trace is null) return;
        var keep = _traceItems.Where(i => i.IsChecked).Select(i => i.Leftover).ToHashSet();
        _trace.Items = _trace.Items.Where(i => keep.Any(l => l.Kind == i.Kind && l.Path == i.Path && l.View == i.View && l.ValueName == i.ValueName)).ToList();
        try { TraceStore.Default.Save(_trace); }
        catch (Exception ex)
        {
            App.Log(ex);
            ShowStatus("Не удалось сохранить запись", ex.Message, done: true, ok: false);
            return;
        }
        var title = _trace.Title;
        var count = _trace.Items.Count;
        FinishMonitor();
        ShowStatus("Запись установки сохранена", $"«{title}»: записано {count} " + Plural(count, "объект", "объекта", "объектов") +
                   ". При удалении этой программы всё записанное найдётся автоматически.", done: true, ok: true);
        await LoadAsync();
    }

    async void MonitorCancel_Click(object sender, RoutedEventArgs e)
    {
        // В результате с найденной программой это «Удалить запись»: запись уже на диске — убираем её.
        if (_trace is { Programs.Count: > 0 } t)
        {
            if (!await CleanPage.Confirm("Удалить запись установки?",
                    $"Запись «{t.Title}» будет удалена. При удалении программы хвосты будут искаться только по именам.", "Удалить запись"))
                return;
            TraceStore.Default.Delete(t);
            FinishMonitor();
            await LoadAsync();
            return;
        }
        FinishMonitor();
    }

    void FinishMonitor()
    {
        TraceStore.Default.ClearPending();
        _before = null;
        _trace = null;
        _traceItems = [];
        SetMonitor(MonitorStage.Hidden);
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
