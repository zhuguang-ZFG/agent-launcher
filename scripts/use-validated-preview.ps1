param([string]$PackageDirectory = (Join-Path $PSScriptRoot '../artifacts/preview'), [string]$DesktopDirectory = [Environment]::GetFolderPath('Desktop'), [switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath($PackageDirectory)
$launch = Join-Path $package 'Launch.ps1'
$powershell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
& $powershell -NoProfile -ExecutionPolicy Bypass -File $launch -CheckOnly
if ($LASTEXITCODE -ne 0) { throw 'Package checks failed; the desktop entry was not changed.' }
$desktop = [IO.Path]::GetFullPath($DesktopDirectory)
$shortcutPath = Join-Path $desktop (([string][char]0x591A)+' Agent '+([string][char]0x542F)+([string][char]0x52A8)+([string][char]0x5668)+'.lnk')
if ($CheckOnly) { Write-Output ('Validated desktop target: '+$package); exit 0 }
$backupRoot = Join-Path $PSScriptRoot '../artifacts/launcher-entry-backups'
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
$shell = New-Object -ComObject WScript.Shell
if (Test-Path -LiteralPath $shortcutPath) {
    $existing = $shell.CreateShortcut($shortcutPath)
    if ($existing.TargetPath -notmatch 'AgentLauncher|powershell' -and $existing.Arguments -notmatch 'AgentLauncher|Launch.ps1') { throw 'The desktop shortcut has an unexpected target; it was not replaced.' }
    Copy-Item -LiteralPath $shortcutPath -Destination (Join-Path $backupRoot ('before-'+[guid]::NewGuid().ToString('N')+'.lnk'))
}
$temporary = Join-Path $desktop ('AgentLauncher-'+[guid]::NewGuid().ToString('N')+'.lnk')
try {
    $shortcut = $shell.CreateShortcut($temporary)
    $shortcut.TargetPath = Join-Path $package 'Start-AgentLauncher.cmd'
    $shortcut.Arguments = ''
    $shortcut.WindowStyle = 7
    $shortcut.WorkingDirectory = $package
    $shortcut.IconLocation = (Join-Path $package 'AgentLauncher.App.exe')+',0'
    $shortcut.Description = 'Multi-Agent Launcher - validated package'
    $shortcut.Save()
    if (Test-Path -LiteralPath $shortcutPath) { [IO.File]::Replace($temporary,$shortcutPath,$null) }
    else { [IO.File]::Move($temporary,$shortcutPath) }
    $saved = $shell.CreateShortcut($shortcutPath)
    if ($saved.TargetPath -ne $shortcut.TargetPath -or $saved.Arguments -ne $shortcut.Arguments) { throw 'Desktop entry verification failed.' }
    Write-Output ('Desktop entry updated: '+$shortcutPath)
    Write-Output 'Existing processes and their resource limits were preserved.'
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
