@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\use-validated-preview.ps1"
if errorlevel 1 (
    pause
) else (
    call "%~dp0Start-AgentLauncher.cmd"
)
