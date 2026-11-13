param([Parameter(Mandatory=$true)][string]$PackageDirectory,[string]$TerminalPath='')
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$package=[IO.Path]::GetFullPath($PackageDirectory)
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $package 'Launch.ps1') -CheckOnly
if($LASTEXITCODE -ne 0){throw 'Production package entry check failed before console probe'}
$evidence=Join-Path $workspace ('artifacts/production-pane-'+[guid]::NewGuid().ToString('N'))
$runtime=Join-Path $evidence 'runtime'
& dotnet publish (Join-Path $workspace 'tests/AgentLauncher.PaneChecks/AgentLauncher.PaneChecks.csproj') -c Release -p:DefineConstants=TRACE -o $runtime --nologo -m:1
if($LASTEXITCODE -ne 0){throw 'Production pane fixture publish failed'}
$result=Join-Path $evidence 'result.json'
if(-not $TerminalPath){$TerminalPath='-'}
& (Join-Path $runtime 'AgentLauncher.PaneChecks.exe') --launch $package $result $TerminalPath
if($LASTEXITCODE -ne 0){throw ('Production console dismissal checks failed: '+$result)}
Write-Output ('Production F12/Enter automation passed; independent human acceptance remains separate: '+$result)
