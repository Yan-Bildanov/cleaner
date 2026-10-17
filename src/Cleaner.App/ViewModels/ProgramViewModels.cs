using System.IO;
using System.Windows.Media;
using Cleaner.Core;
using Cleaner.Core.Programs;

namespace Cleaner.App.ViewModels;

public sealed class ProgramItemViewModel(InstalledProgram program) : Observable
{
    public InstalledProgram Program { get; } = program;

    public string Name => Program.Name;
    public string Subtitle => string.Join(" · ", new[] { Program.Publisher, Program.Version }.Where(s => !string.IsNullOrEmpty(s)));
    public string DateText => Program.InstallDate?.ToString("dd.MM.yyyy") ?? "—";
    public bool CanUninstall => Program.CanUninstall;
    public bool HasFolder => Program.InstallLocation is { } f && Directory.Exists(f);
    public string UninstallTooltip => Program.CanUninstall
        ? "Запустить деинсталлятор программы"
        : "Установщик запретил удаление этой программы";

    long? _sizeBytes = program.SizeBytes;
    public long? SizeBytes { get => _sizeBytes; set { Set(ref _sizeBytes, value); OnPropertyChanged(nameof(SizeText)); } }
    public string SizeText => SizeBytes is long b && b > 0 ? Format.Bytes(b) : "—";

    ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set { Set(ref _icon, value); OnPropertyChanged(nameof(HasIcon)); } }
    public bool HasIcon => _icon is not null;

    bool _isBusy;
    /// <summary>Сейчас идёт удаление этой программы.</summary>
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    public bool Matches(string query) =>
        query.Length == 0
        || Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || (Program.Publisher?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false);
}
