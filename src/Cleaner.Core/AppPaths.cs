namespace Cleaner.Core;

/// <summary>
/// Где программа хранит свои данные (карантин, журнал, служебный сигнал).
///
/// ВАЖНО: не в %LOCALAPPDATA%. Если Cleaner запущен из приложения, упакованного в MSIX (например, из Claude
/// во время разработки), Windows тихо перенаправляет записи в AppData в папку этого приложения
/// (...\Packages\&lt;app&gt;\LocalCache). Тогда карантин, созданный в одном запуске, не виден в другом.
/// Папка профиля пользователя этой виртуализации не подвержена — храним всё там, в скрытой папке.
/// </summary>
public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cleaner");

    public static string QuarantineDir => Path.Combine(DataDir, "Quarantine");
    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string CloseSignal => Path.Combine(DataDir, "close.signal");

    /// <summary>Установленная версия (tools\install.ps1). Планировщик и ярлыки всегда указывают сюда.</summary>
    public static string InstallDir => Path.Combine(DataDir, "app");
    public static string InstalledExe => Path.Combine(InstallDir, "Cleaner.exe");

    /// <summary>Что запускать из планировщика и ярлыков: установленную версию, если она есть, иначе текущий .exe.</summary>
    public static string LaunchExe => File.Exists(InstalledExe) ? InstalledExe : Environment.ProcessPath!;

    /// <summary>Старое место (до 2026-10-16) — оттуда переносим карантин при запуске.</summary>
    public static string LegacyDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cleaner");

    public static void EnsureDataDir()
    {
        var dir = Directory.CreateDirectory(DataDir);
        if (!dir.Attributes.HasFlag(FileAttributes.Hidden)) dir.Attributes |= FileAttributes.Hidden;
    }

    /// <summary>Сколько файлов не удалось перенести со старого места (нужны права администратора).</summary>
    public static int LegacyLeft { get; private set; }

    /// <summary>Переносит партии карантина со старого места. Возвращает число перенесённых партий.</summary>
    public static int MigrateLegacyQuarantine()
    {
        var legacy = Path.Combine(LegacyDir, "Quarantine");
        if (!Directory.Exists(legacy)) return 0;
        Directory.CreateDirectory(QuarantineDir);
        var moved = 0;
        foreach (var batch in Directory.EnumerateDirectories(legacy).ToList())
        {
            var target = Path.Combine(QuarantineDir, Path.GetFileName(batch));
            // Переносим по файлам: целую папку из виртуализованного AppData Windows переносить не даёт.
            // Если прошлый перенос прервался — досылаем оставшееся, уже перенесённое не трогаем.
            foreach (var file in Directory.EnumerateFiles(batch, "*", SearchOption.AllDirectories).ToList())
            {
                var dest = Path.Combine(target, Path.GetRelativePath(batch, file));
                if (File.Exists(dest)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                // Файлы из системных папок (C:\Windows\Temp) без прав администратора не переносятся —
                // пропускаем, их доперенесёт запуск от администратора.
                try { File.Move(file, dest); }
                catch (UnauthorizedAccessException) { LegacyLeft++; }
            }
            if (LegacyLeft == 0)
                try { Directory.Delete(batch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            moved++;
        }
        return moved;
    }
}
