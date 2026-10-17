using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Cleaner.Core;
using Cleaner.Core.Rules;
using Cleaner.Core.Scanning;
using Wpf.Ui.Controls;

namespace Cleaner.App.ViewModels;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RuleItemViewModel(CleanRule rule) : Observable
{
    public CleanRule Rule { get; } = rule;
    public string Name => Rule.Name;

    bool _isChecked = rule.DefaultChecked;
    public bool IsChecked { get => _isChecked; set { Set(ref _isChecked, value); CheckedChanged?.Invoke(); } }
    public event Action? CheckedChanged;

    string _sizeText = "";
    public string SizeText { get => _sizeText; private set => Set(ref _sizeText, value); }

    string? _detail = rule.Note;
    public string? Detail { get => _detail; private set => Set(ref _detail, value); }

    bool _isWarning;
    public bool IsWarning { get => _isWarning; private set => Set(ref _isWarning, value); }

    public RuleScanResult? Result { get; private set; }

    public void MarkScanning() { SizeText = "…"; }

    public void Apply(RuleScanResult r)
    {
        Result = r;
        IsWarning = false;
        if (r.NeedsAdmin)
        {
            SizeText = "—";
            Detail = "Нужны права администратора — кнопка «Права администратора» справа";
            IsWarning = true;
            return;
        }

        SizeText = Format.Bytes(r.TotalBytes);
        var parts = new List<string>();
        if (r.Rule.Note is { } note) parts.Add(note);
        parts.Add(r.Rule.Kind == RuleKind.RecycleBin ? $"объектов: {r.ItemCount:N0}" : $"файлов: {r.ItemCount:N0}");
        if (r.SkippedRecent > 0) parts.Add($"свежих пропущено: {r.SkippedRecent:N0}");
        if (r.RunningProcesses.Count > 0)
        {
            parts.Add("приложение открыто — занятые файлы будут пропущены, лучше закрыть");
            IsWarning = true;
        }
        Detail = string.Join(" · ", parts);
    }
}

public sealed class RuleGroupViewModel(string name, SymbolRegular icon, IEnumerable<RuleItemViewModel> items)
{
    public string Name { get; } = name;
    public SymbolRegular Icon { get; } = icon;
    public ObservableCollection<RuleItemViewModel> Items { get; } = new(items);
}
