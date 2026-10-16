namespace Cleaner.Core.Rules;

/// <summary>
/// Белый список того, что программа вообще имеет право трогать.
/// Добавлять сюда только настоящие кэши, которые приложение пересоздаёт само.
/// НЕ добавлять: скачанные программы и среды выполнения (часто лежат в %USERPROFILE%\.cache),
/// .nuget\packages (нужны для сборки), локальные ИИ-модели, cookies и историю браузеров.
/// </summary>
public static class BuiltInRules
{
    public const string GroupWindows = "Windows";
    public const string GroupBrowsers = "Браузеры (только кэш, без cookies и истории)";
    public const string GroupApps = "Приложения";
    public const string GroupDev = "Разработка";

    static string[] Chromium(string userData) =>
    [
        $@"{userData}\*\Cache",
        $@"{userData}\*\Code Cache",
        $@"{userData}\*\GPUCache",
    ];

    internal static string[] Electron(string root) =>
    [
        $@"{root}\Cache",
        $@"{root}\Code Cache",
        $@"{root}\GPUCache",
    ];

    /// <summary>Встроенные правила + личные из %USERPROFILE%\.cleaner\rules.local.json (см. LocalRules).</summary>
    public static IReadOnlyList<CleanRule> All { get; } = [.. Builtin, .. LocalRules.Load()];

    static IReadOnlyList<CleanRule> Builtin =>
    [
        // ---------- Windows ----------
        new() { Id = "win.temp", Group = GroupWindows, Name = "Временные файлы",
                Note = "Только файлы старше суток", Paths = [@"%TEMP%"], MinAge = TimeSpan.FromDays(1) },
        new() { Id = "win.systemp", Group = GroupWindows, Name = "Системные временные файлы",
                Note = "Только файлы старше суток", Paths = [@"%WINDIR%\Temp"],
                MinAge = TimeSpan.FromDays(1), RequiresAdmin = true },
        new() { Id = "win.recycle", Group = GroupWindows, Name = "Корзина",
                Note = "Удаляется безвозвратно — спросим отдельно", Kind = RuleKind.RecycleBin },
        new() { Id = "win.thumbs", Group = GroupWindows, Name = "Кэш эскизов",
                Note = "Проводник пересоздаст при просмотре папок",
                Paths = [@"%LOCALAPPDATA%\Microsoft\Windows\Explorer"], FilePattern = "thumbcache_*.db" },
        new() { Id = "win.crash", Group = GroupWindows, Name = "Отчёты об ошибках и дампы",
                Paths = [@"%LOCALAPPDATA%\CrashDumps", @"%LOCALAPPDATA%\Microsoft\Windows\WER",
                         @"%PROGRAMDATA%\Microsoft\Windows\WER\ReportArchive",
                         @"%PROGRAMDATA%\Microsoft\Windows\WER\ReportQueue"] },
        new() { Id = "win.wudl", Group = GroupWindows, Name = "Загрузки Центра обновления",
                Note = "Уже установленные обновления, файлы старше недели",
                Paths = [@"%WINDIR%\SoftwareDistribution\Download"], RequiresAdmin = true,
                DefaultChecked = false, MinAge = TimeSpan.FromDays(7) },

        // ---------- Браузеры ----------
        new() { Id = "br.chrome", Group = GroupBrowsers, Name = "Google Chrome",
                Note = "Все профили", Paths = Chromium(@"%LOCALAPPDATA%\Google\Chrome\User Data"),
                ProcessNames = ["chrome"] },
        new() { Id = "br.edge", Group = GroupBrowsers, Name = "Microsoft Edge",
                Note = "Все профили", Paths = Chromium(@"%LOCALAPPDATA%\Microsoft\Edge\User Data"),
                ProcessNames = ["msedge"] },

        // ---------- Приложения ----------
        new() { Id = "app.telegram", Group = GroupApps, Name = "Telegram — кэш медиа",
                Note = "Фото и видео заново подгрузятся из облака Telegram",
                Paths = [@"%APPDATA%\Telegram Desktop\tdata\user_data"],
                DefaultChecked = false, ProcessNames = ["Telegram"] },
        new() { Id = "app.claude", Group = GroupApps, Name = "Claude — кэш",
                Paths = Electron(@"%APPDATA%\Claude"), DefaultChecked = false, ProcessNames = ["claude"] },
        new() { Id = "app.vscode", Group = GroupApps, Name = "VS Code — кэш",
                Paths = Electron(@"%APPDATA%\Code"), ProcessNames = ["Code"] },

        // ---------- Разработка ----------
        new() { Id = "dev.pip", Group = GroupDev, Name = "Кэш pip",
                Paths = [@"%LOCALAPPDATA%\pip\cache"], DefaultChecked = false },
        new() { Id = "dev.npm", Group = GroupDev, Name = "Кэш npm",
                Paths = [@"%LOCALAPPDATA%\npm-cache"], DefaultChecked = false },   // node крутится внутри Claude/VS Code — кэш не держит
        new() { Id = "dev.nuget", Group = GroupDev, Name = "Кэш загрузок NuGet",
                Note = "Только HTTP-кэш; сами пакеты не трогаем",
                Paths = [@"%LOCALAPPDATA%\NuGet\v3-cache"], DefaultChecked = false },
    ];
}
