using Cleaner.Core.Health;
using Wpf.Ui.Controls;

namespace Cleaner.App.ViewModels;

public sealed class HealthItemViewModel(HealthCheck check)
{
    public HealthCheck Check { get; } = check;
    public string Title => Check.Title;
    public string Value => Check.Value;
    public string? Hint => Check.Hint;
    public HealthStatus Status => Check.Status;
    public string StatusText => Status switch
    {
        HealthStatus.Ok => "Норма",
        HealthStatus.Warning => "Внимание",
        HealthStatus.Problem => "Проблема",
        _ => "Нет данных",
    };
    public string? DetailsText => Check.Details is { Count: > 0 } d ? string.Join("\n", d) : null;
    public bool HasDetails => DetailsText is not null;

    public SymbolRegular Icon => Check.Id switch
    {
        "disk" => SymbolRegular.Storage24,
        "battery" => SymbolRegular.Battery624,
        "temp" => SymbolRegular.Temperature24,
        "updates" => SymbolRegular.ArrowSync24,
        "protection" => SymbolRegular.Shield24,
        "startup" => SymbolRegular.Rocket24,
        _ => SymbolRegular.Info24,
    };
}

public sealed record HardwareRow(string Label, string Value);
