using System.IO;
using System.Windows.Media;
using Cleaner.Core;
using Cleaner.Core.Programs;

namespace Cleaner.App.ViewModels;

public sealed class ProgramItemViewModel(InstalledProgram program) : Observable
{
    public InstalledProgram Program { get; } = program;

    public string Name => Program.Name;
    public string Subtitle => string.Join(" · ", new[] { Program.IsStoreApp ? "Microsoft Store" : null, Program.Publisher, Program.Version,
                                                         IsTraced ? "установка записана" : null }
                                                     .Where(s => !string.IsNullOrEmpty(s)));

    bool _isTraced;
    /// <summary>Установка этой программы записана монитором — хвосты найдутся точно по записи.</summary>
    public bool IsTraced { get => _isTraced; set { Set(ref _isTraced, value); OnPropertyChanged(nameof(Subtitle)); } }
    public string DateText => Program.InstallDate?.ToString("dd.MM.yyyy") ?? "—";
    public bool CanUninstall => Program.CanUninstall;
    public bool HasFolder => !Program.IsStoreApp && Program.InstallLocation is { } f && Directory.Exists(f);

    /// <summary>Деинсталлятор указан, но файла нет — удалить можно только принудительно.</summary>
    public bool IsBroken { get; } = !program.IsStoreApp && UninstallCommand.IsBroken(program);

    public string UninstallText => IsBroken ? "Принудительно" : "Удалить";
    public Wpf.Ui.Controls.ControlAppearance UninstallAppearance =>
        IsBroken ? Wpf.Ui.Controls.ControlAppearance.Caution : Wpf.Ui.Controls.ControlAppearance.Secondary;
    public string UninstallTooltip => !Program.CanUninstall
        ? Program.NoRemoveReason ?? "Установщик запретил удаление этой программы"
        : IsBroken
            ? "Деинсталлятор программы пропал с диска — Cleaner уберёт её файлы, ярлыки, службы и записи сам (через карантин)"
            : Program.IsStoreApp ? "Удалить приложение Microsoft Store" : "Запустить деинсталлятор программы";

    long? _sizeBytes = program.SizeBytes;
    public long? SizeBytes { get => _sizeBytes; set { Set(ref _sizeBytes, value); OnPropertyChanged(nameof(SizeText)); } }
    public string SizeText => SizeBytes is long b && b > 0 ? Format.Bytes(b) : "—";

    ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set { Set(ref _icon, value); OnPropertyChanged(nameof(HasIcon)); } }
    public bool HasIcon => _icon is not null;

    bool _isBusy;
    /// <summary>Сейчас идёт удаление этой программы.</summary>
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    bool _isChecked;
    /// <summary>Отмечена для пакетного удаления.</summary>
    public bool IsChecked { get => _isChecked; set { Set(ref _isChecked, value); CheckedChanged?.Invoke(); } }
    public event Action? CheckedChanged;

    public bool Matches(string query) =>
        query.Length == 0
        || Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || (Program.Publisher?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false);
}
