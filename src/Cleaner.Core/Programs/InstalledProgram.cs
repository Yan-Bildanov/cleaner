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

    /// <summary>Установщик запретил удаление (NoRemove = 1).</summary>
    public bool NoRemove { get; init; }

    public required ProgramScope Scope { get; init; }
    /// <summary>Имя подключа в ...\Uninstall — по нему проверяем, удалилась ли программа.</summary>
    public required string RegistryKeyName { get; init; }

    public bool CanUninstall => !NoRemove && (MsiProductCode is not null || !string.IsNullOrWhiteSpace(UninstallString));

    public string RegistryPath => Scope switch
    {
        ProgramScope.Machine64 => @"HKLM\" + ProgramInventory.UninstallKey + @"\" + RegistryKeyName,
        ProgramScope.Machine32 => @"HKLM\" + ProgramInventory.UninstallKey32 + @"\" + RegistryKeyName,
        _ => @"HKCU\" + ProgramInventory.UninstallKey + @"\" + RegistryKeyName,
    };
}
