@echo off
setlocal
set "launcher_powershell=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "launcher_powershell=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
"%launcher_powershell%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Launch.ps1"
if errorlevel 1 pause
