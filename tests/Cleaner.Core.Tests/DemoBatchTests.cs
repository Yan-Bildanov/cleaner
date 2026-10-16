using Cleaner.Core.Cleaning;
using Cleaner.Core.Platform;
using Cleaner.Core.Rules;
using Cleaner.Core.Scanning;

namespace Cleaner.Core.Tests;

/// <summary>
/// Создаёт демо-партию в НАСТОЯЩЕМ карантине из тестовых файлов в папке проекта demo-sandbox,
/// чтобы проверить окно «Карантин» (Вернуть / Удалить навсегда) без риска для реальных данных.
/// Запуск вручную: dotnet test --filter Category=Demo
/// </summary>
public class DemoBatchTests
{
    [Fact, Trait("Category", "Demo")]
    public void Create_demo_batch_from_sandbox_files()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\.."));
        var sandbox = Path.Combine(projectRoot, "demo-sandbox");
        Directory.CreateDirectory(Path.Combine(sandbox, "sub"));
        for (var i = 1; i <= 5; i++)
            File.WriteAllText(Path.Combine(sandbox, i <= 3 ? "" : "sub", $"demo-{i}.txt"), $"Тестовый файл {i}\n" + new string('x', 200_000));

        var sys = new WindowsSystemInfo();
        var rule = new CleanRule { Id = "Демо-файлы", Group = "demo", Name = "Демо-файлы", Paths = [sandbox] };
        var report = new FileCleaner(new QuarantineStore(QuarantineStore.DefaultRoot, sys), sys)
            .Clean([new Scanner(sys).Scan(rule)], emptyRecycleBin: false);

        Assert.Equal(5, report.Moved);
    }
}
