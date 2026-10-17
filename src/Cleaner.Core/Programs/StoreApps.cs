using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Cleaner.Core.Programs;

/// <summary>
/// Приложения Microsoft Store (пакеты MSIX/AppX) текущего пользователя — через системный PackageManager.
/// Не показываются: компоненты-библиотеки (frameworks), языковые пакеты и части самой Windows (подпись System).
/// Удаление — штатное (как «Параметры → Приложения»): пакет и его данные в %LOCALAPPDATA%\Packages убирает сама Windows.
/// </summary>
public static class StoreApps
{
    public static Action<Exception>? OnError;

    public static IReadOnlyList<InstalledProgram> ReadAll()
    {
        var list = new List<InstalledProgram>();
        try
        {
            var pm = new PackageManager();
            foreach (var pkg in pm.FindPackagesForUser(""))
            {
                try
                {
                    if (FromPackage(pkg) is { } p) list.Add(p);
                }
                catch (Exception ex) { OnError?.Invoke(ex); }
            }
        }
        catch (Exception ex) { OnError?.Invoke(ex); }
        return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Пакеты, без которых ломается что-то важное: магазин, winget, покупки — и само приложение Claude,
    /// через которое идёт работа. Показываем, но удалить не даём.
    /// </summary>
    static readonly (string FamilyPrefix, string Reason)[] Protected =
    [
        ("Microsoft.WindowsStore_", "Это Microsoft Store — без него не установить и не обновить приложения из магазина"),
        ("Microsoft.DesktopAppInstaller_", "Это «Установщик приложения» — на нём работает winget и установка пакетов MSIX"),
        ("Microsoft.StorePurchaseApp_", "Нужен Microsoft Store для покупок и лицензий"),
        ("Microsoft.XboxIdentityProvider_", "Нужен для входа в учётную запись в играх и Game Bar"),
        ("Claude_", "Это приложение Claude — через него сейчас идёт работа с Cleaner"),
    ];

    static InstalledProgram? FromPackage(Package pkg)
    {
        if (pkg.IsFramework || pkg.IsResourcePackage) return null;
        if (pkg.SignatureKind == PackageSignatureKind.System) return null;     // компоненты Windows
        if (pkg.IsStub) return null;

        // Служебные пакеты без своего окна (среды выполнения, источники winget, языковые пакеты, расширения кодеков)
        // в «Пуске» не видны и пользователю не нужны.
        try { if (pkg.GetAppListEntries().Count == 0) return null; }
        catch { return null; }

        var id = pkg.Id;
        string name;
        try { name = pkg.DisplayName; } catch { name = ""; }
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) name = id.Name;

        string? publisher = null;
        try { publisher = pkg.PublisherDisplayName; } catch { }
        if (string.IsNullOrWhiteSpace(publisher) || publisher.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) publisher = null;

        string? location = null;
        try { location = pkg.InstalledPath; } catch { }

        string? logo = null;
        try { logo = pkg.Logo?.LocalPath; } catch { }

        DateTime? installed = null;
        try { installed = pkg.InstalledDate.LocalDateTime.Date; } catch { }

        var v = id.Version;
        var protectedReason = Protected.FirstOrDefault(x => id.FamilyName.StartsWith(x.FamilyPrefix, StringComparison.OrdinalIgnoreCase)).Reason;
        return new InstalledProgram
        {
            NoRemove = protectedReason is not null,
            NoRemoveReason = protectedReason,
            Name = name.Trim(),
            Publisher = publisher?.Trim(),
            Version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}",
            InstallDate = installed,
            InstallLocation = location,
            LogoPath = logo,
            PackageFullName = id.FullName,
            PackageFamilyName = id.FamilyName,
            Scope = ProgramScope.Store,
            RegistryKeyName = id.FullName,
        };
    }

    public static bool Exists(InstalledProgram p)
    {
        try { return p.PackageFullName is { } full && new PackageManager().FindPackageForUser("", full) is not null; }
        catch (Exception ex) { OnError?.Invoke(ex); return true; }
    }

    public static async Task<UninstallResult> RemoveAsync(InstalledProgram p, IProgress<string>? progress)
    {
        if (p.PackageFullName is not { } full) return new(UninstallOutcome.Failed, "У приложения нет имени пакета.");
        progress?.Report("Удаляю приложение Microsoft Store…");
        try
        {
            var result = await new PackageManager().RemovePackageAsync(full).AsTask();
            if (!Exists(p)) return new(UninstallOutcome.Removed, $"«{p.Name}» удалено.");
            var text = string.IsNullOrWhiteSpace(result.ErrorText) ? "Windows не удалила пакет." : result.ErrorText.Trim();
            return new(UninstallOutcome.StillInstalled, "Не удалось удалить: " + text);
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex);
            if (!Exists(p)) return new(UninstallOutcome.Removed, $"«{p.Name}» удалено.");
            return new(UninstallOutcome.Failed, "Не удалось удалить: " + ex.Message);
        }
    }
}
