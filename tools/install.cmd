@echo off
rem Установка/обновление Cleaner. Двойной клик или: explorer.exe tools\install.cmd  (журнал: tools\install.log)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
