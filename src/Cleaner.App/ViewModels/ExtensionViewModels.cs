using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cleaner.Core;
using Cleaner.Core.Browsers;
using Wpf.Ui.Controls;

namespace Cleaner.App.ViewModels;

public sealed class ExtensionItemViewModel(BrowserExtension ext) : Observable
{
    public BrowserExtension Ext { get; } = ext;

    public string Name => Ext.Name + (Ext.IsTheme ? " (тема)" : "");

    public string Subtitle => string.Join(" · ", new[]
    {
        $"{Ext.Browser.Name}, профиль «{Ext.ProfileName}»",
        Ext.Version is { } v ? "версия " + v : null,
        SourceText,
        Ext.Enabled ? "включено" : "отключено",
        Ext.Size > 0 ? Format.Bytes(Ext.Size) : null,
    }.Where(s => s is not null));

    string SourceText => Ext.Source switch
    {
        ExtensionSource.WebStore => "из интернет-магазина",
        ExtensionSource.Program => "поставлено программой",
        ExtensionSource.Bundled => "поставлено вместе с браузером",
        ExtensionSource.Developer => "режим разработчика",
        ExtensionSource.Policy => "установлено политикой",
        _ => "не из магазина",
    };

    public string WarningsText => string.Join(" · ", Ext.Warnings);
    public bool HasWarnings => Ext.Warnings.Count > 0;
    public string NotesText => Ext.Notes.Count > 0 ? "Права: " + string.Join(", ", Ext.Notes) : "";
    public bool HasNotes => Ext.Notes.Count > 0;
    public string? Description => Ext.Description;

    /// <summary>Поставлено программой через реестр — Cleaner может убрать сам (ключ в карантин).</summary>
    public bool RemovableByCleaner => Ext.Source == ExtensionSource.Program && Ext.ExternalKeys.Count > 0;
    public bool CanAct => Ext.Source != ExtensionSource.Policy;

    public string ActionText => RemovableByCleaner ? "Удалить" : Ext.Source == ExtensionSource.Policy ? "Удалить нельзя" : "Открыть в браузере";
    public ControlAppearance ActionAppearance => RemovableByCleaner ? ControlAppearance.Caution : ControlAppearance.Secondary;
    public SymbolRegular ActionIcon => RemovableByCleaner ? SymbolRegular.Delete24 : SymbolRegular.Open24;
    public string ActionTooltip => RemovableByCleaner
        ? "Убрать ключ реестра, через который программа ставит это расширение. Браузер сам удалит его при следующем запуске."
        : Ext.Source == ExtensionSource.Policy
            ? "Расширение установлено принудительно политикой — его убирает администратор или программа, которая прописала политику"
            : "Открыть страницу этого расширения в браузере, в его профиле: там его можно отключить или удалить";

    ImageSource? _icon;
    public ImageSource? Icon
    {
        get
        {
            if (_icon is null && Ext.IconPath is { } p)
            {
                try
                {
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.DecodePixelWidth = 48;
                    img.UriSource = new Uri(p);
                    img.EndInit();
                    img.Freeze();
                    _icon = img;
                }
                catch { }
            }
            return _icon;
        }
    }
    public bool HasIcon => Icon is not null;
}
