# Установка / обновление Cleaner:
#   1) финальная сборка в один .exe (Release, self-contained);
#   2) закрыть запущенный Cleaner (через close.signal) и положить .exe в %USERPROFILE%\.cleaner\app;
#   3) ярлыки: рабочий стол + меню «Пуск» (+ перенаправить «Быстрая очистка», если есть);
#   4) перенаправить задачу планировщика «Cleaner\Автоочистка» на установленный .exe.
#
# ВАЖНО: запускать вне Claude (через install.cmd двойным кликом или «explorer.exe tools\install.cmd»),
# иначе ярлык в «Пуске» (это папка AppData) попадёт в виртуализованную папку Claude и будет не виден.
# Журнал: tools\install.log
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$log = Join-Path $PSScriptRoot 'install.log'
Start-Transcript -Path $log -Force | Out-Null
try {
    $dotnet = 'C:\Program Files\dotnet\dotnet.exe'     # не «dotnet»: в PATH раньше стоит старый x86 без SDK
    $data = Join-Path $env:USERPROFILE '.cleaner'
    $appDir = Join-Path $data 'app'
    $exe = Join-Path $appDir 'Cleaner.exe'
    $publish = Join-Path $root 'publish'

    Write-Host '== 1. Сборка'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
    & $dotnet publish (Join-Path $root 'src\Cleaner.App\Cleaner.App.csproj') -c Release -o $publish -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE" }
    $built = Join-Path $publish 'Cleaner.exe'
    Write-Host ("   {0:N1} МБ" -f ((Get-Item $built).Length / 1MB))

    Write-Host '== 2. Закрываю запущенный Cleaner и копирую'
    if (Get-Process Cleaner -ErrorAction SilentlyContinue) {
        New-Item -ItemType Directory $data -Force | Out-Null
        New-Item -ItemType File (Join-Path $data 'close.signal') -Force | Out-Null
        for ($i = 0; $i -lt 20 -and (Get-Process Cleaner -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 300 }
        Get-Process Cleaner -ErrorAction SilentlyContinue | Stop-Process -ErrorAction SilentlyContinue
        Start-Sleep 1
    }
    New-Item -ItemType Directory $appDir -Force | Out-Null
    (Get-Item $data -Force).Attributes = (Get-Item $data -Force).Attributes -bor [IO.FileAttributes]::Hidden
    Copy-Item $built $exe -Force
    Write-Host "   $exe"

    Write-Host '== 3. Ярлыки'
    $shell = New-Object -ComObject WScript.Shell
    function Make-Link([string]$path, [string]$arguments, [string]$desc) {
        $l = $shell.CreateShortcut($path)
        $l.TargetPath = $exe; $l.Arguments = $arguments; $l.WorkingDirectory = $appDir
        $l.IconLocation = "$exe,0"; $l.Description = $desc; $l.Save()
        Write-Host "   $path"
    }
    $desktop = [Environment]::GetFolderPath('DesktopDirectory')
    $startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Cleaner.lnk'
    Make-Link (Join-Path $desktop 'Cleaner.lnk') '' 'Cleaner — очистка и проверка ноутбука'
    Make-Link $startMenu '' 'Cleaner — очистка и проверка ноутбука'
    $quick = Join-Path $desktop 'Быстрая очистка.lnk'
    if (Test-Path $quick) { Make-Link $quick '--auto --now' 'Cleaner: быстрая очистка по настройкам автоочистки' }

    Write-Host '== 4. Задача планировщика'
    $task = 'Cleaner\Автоочистка'
    $ErrorActionPreference = 'Continue'      # schtasks пишет в stderr, если задачи нет — это не ошибка
    schtasks /Query /TN $task *> $null
    if ($LASTEXITCODE -eq 0) {
        $xmlFile = Join-Path $env:TEMP 'cleaner-task.xml'
        $xml = (schtasks /Query /TN $task /XML) -join "`r`n"
        $xml = [regex]::Replace($xml, '<Command>.*?</Command>', "<Command>$([Security.SecurityElement]::Escape($exe))</Command>")
        $xml = [regex]::Replace($xml, '<WorkingDirectory>.*?</WorkingDirectory>', "<WorkingDirectory>$([Security.SecurityElement]::Escape($appDir))</WorkingDirectory>")
        [IO.File]::WriteAllText($xmlFile, $xml, [Text.Encoding]::Unicode)
        schtasks /Create /TN $task /XML $xmlFile /F | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "не удалось обновить задачу ($LASTEXITCODE)" }
        Remove-Item $xmlFile -Force
        Write-Host "   задача теперь запускает $exe"
    } else { Write-Host '   автоочистка не включена — задачи нет, пропускаю' }

    Write-Host '== Готово'
}
catch { Write-Host "ОШИБКА: $_" }
finally { Stop-Transcript | Out-Null }
