using Cleaner.Core;
using Cleaner.Core.Programs;
using Wpf.Ui.Controls;

namespace Cleaner.App.ViewModels;

public sealed class LeftoverItemViewModel(Leftover leftover) : Observable
{
    public Leftover Leftover { get; } = leftover;

    bool _isChecked = leftover.Confidence == LeftoverConfidence.High;
    public bool IsChecked { get => _isChecked; set { Set(ref _isChecked, value); CheckedChanged?.Invoke(); } }
    public event Action? CheckedChanged;

    public string Path => Leftover.Kind switch
    {
        LeftoverKind.Service => "Служба Windows: " + Leftover.Path,
        LeftoverKind.ScheduledTask => "Задача Планировщика: " + Leftover.Path,
        LeftoverKind.RegistryValue => $"Автозагрузка: «{Leftover.ValueName}» в {Leftover.Path}",
        _ => Leftover.Path,
    };

    public SymbolRegular Icon => Leftover.Kind switch
    {
        LeftoverKind.Folder => SymbolRegular.Folder24,
        LeftoverKind.Shortcut => SymbolRegular.Link24,
        LeftoverKind.Service => SymbolRegular.Settings24,
        LeftoverKind.ScheduledTask => SymbolRegular.CalendarClock24,
        LeftoverKind.File => SymbolRegular.Document24,
        LeftoverKind.RegistryValue => SymbolRegular.Rocket24,
        _ => SymbolRegular.Key24,
    };

    public string Detail
    {
        get
        {
            var parts = new List<string>
            {
                Leftover.Kind switch
                {
                    LeftoverKind.Folder when Leftover.FileCount < 0 => "папка",
                    LeftoverKind.Folder when Leftover.FileCount == 0 => "пустая папка",
                    LeftoverKind.Folder => $"папка · файлов: {Leftover.FileCount:N0} · {Format.Bytes(Leftover.Size)}",
                    LeftoverKind.Shortcut => "ярлык",
                    LeftoverKind.Service => "будет остановлена и удалена (вернётся после перезагрузки, если восстановить из карантина)",
                    LeftoverKind.ScheduledTask => "задача будет удалена",
                    LeftoverKind.File => $"файл · {Format.Bytes(Leftover.Size)}",
                    LeftoverKind.RegistryValue => "запуск при входе в Windows",
                    _ => "ключ реестра" + (Leftover.View == Microsoft.Win32.RegistryView.Registry32 ? " (32-битный)" : ""),
                },
                Leftover.Reason,
            };
            if (Leftover.NeedsAdmin && !App.SystemInfo.IsElevated) parts.Add("нужны права администратора");
            return string.Join(" · ", parts);
        }
    }

    public bool IsMedium => Leftover.Confidence == LeftoverConfidence.Medium;
}
