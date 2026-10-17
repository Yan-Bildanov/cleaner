namespace Cleaner.Core.Programs;

/// <summary>Где Windows хранит запись о программе (ветка ...\CurrentVersion\Uninstall).</summary>
public enum ProgramScope
{
    /// <summary>HKLM, 64-битное представление — программы для всех пользователей.</summary>
    Machine64,
    /// <summary>HKLM\...\WOW6432Node — 32-битные программы для всех пользователей.</summary>
    Machine32,
    /// <summary>HKCU — программы, установленные только для текущего пользователя.</summary>
    User,
    /// <summary>Приложение Microsoft Store (пакет MSIX/AppX) текущего пользователя.</summary>
    Store,
}

/// <summary>Установленная программа — как её видит «Установка и удаление программ».</summary>
public sealed record InstalledProgram
{
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public DateTime? InstallDate { get; init; }

    /// <summary>Размер из реестра (EstimatedSize). null — не указан, его досчитывают по папке.</summary>
    public long? SizeBytes { get; init; }

    public string? InstallLocation { get; init; }
    /// <summary>Значок: «путь» или «путь,индекс».</summary>
    public string? DisplayIcon { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }

    /// <summary>Код продукта Windows Installer ({GUID}) — для удаления через msiexec /x.</summary>
    public string? MsiProductCode { get; init; }

    /// <summary>Установщик запретил удаление (NoRemove = 1) или Cleaner защищает программу сам.</summary>
    public bool NoRemove { get; init; }
    /// <summary>Почему удалить нельзя — для подсказки. null — «установщик запретил».</summary>
    public string? NoRemoveReason { get; init; }

    public required ProgramScope Scope { get; init; }
    /// <summary>Имя подключа в ...\Uninstall — по нему проверяем, удалилась ли программа.</summary>
    public required string RegistryKeyName { get; init; }

    /// <summary>Только для приложений Store: полное имя пакета (для удаления) и семейство (папка данных в Packages).</summary>
    public string? PackageFullName { get; init; }
    public string? PackageFamilyName { get; init; }
    /// <summary>Только для Store: путь к картинке-логотипу.</summary>
    public string? LogoPath { get; init; }

    public bool IsStoreApp => Scope == ProgramScope.Store;

    public bool CanUninstall => !NoRemove && (IsStoreApp
        ? PackageFullName is not null
        : MsiProductCode is not null || !string.IsNullOrWhiteSpace(UninstallString));

    public string RegistryPath => Scope switch
    {
        ProgramScope.Store => "",
        ProgramScope.Machine64 => @"HKLM\" + ProgramInventory.UninstallKey + @"\" + RegistryKeyName,
        ProgramScope.Machine32 => @"HKLM\" + ProgramInventory.UninstallKey32 + @"\" + RegistryKeyName,
        _ => @"HKCU\" + ProgramInventory.UninstallKey + @"\" + RegistryKeyName,
    };
}
