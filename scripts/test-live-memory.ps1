param(
    [Parameter(Mandatory=$true)][ValidateSet('qoder-hooks','qoder-session','qoder-subagent','qoder','omp')][string]$Scenario,
    [Parameter(Mandatory=$true)][string]$AgentPath,
    [Parameter(Mandatory=$true)][string]$PackageDirectory,
    [string]$NodePath = (Get-Command node.exe -ErrorAction Stop).Source
)
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root=Join-Path $repository ('artifacts/AgentLauncher-tests-live-'+$Scenario+'-'+[guid]::NewGuid().ToString('N'))
$build=Join-Path $repository ('artifacts/live-build-'+[guid]::NewGuid().ToString('N'))
$worker=Join-Path ([IO.Path]::GetFullPath($PackageDirectory)) 'AgentLauncher.Worker.exe'
foreach($path in @($AgentPath,$worker,$NodePath)){
    if(-not [IO.Path]::IsPathRooted($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)){throw 'Tools must be existing absolute executable paths'}
}
# External acceptance consumes the existing Qoder login/model quota when selected.
# No credentials are read/copied; no user settings or existing sessions are changed.
New-Item -ItemType Directory -Path $build | Out-Null
$restore=Join-Path $build 'NuGet.Config'
[IO.File]::WriteAllText($restore,'<configuration><packageSources><clear /></packageSources></configuration>')
& dotnet publish (Join-Path $repository 'tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj') -c Release -o $build --nologo -m:1 "-p:RestoreConfigFile=$restore"
if($LASTEXITCODE -ne 0){throw 'Acceptance runner build failed'}
$files=@($AgentPath,$worker,(Join-Path $build 'AgentLauncher.Tests.dll'),(Join-Path $build 'AgentLauncher.Windows.dll'),(Join-Path $repository 'tests/fixtures/memory-workload.mjs'),(Join-Path $repository 'tests/fixtures/omp-memory-soak.mjs'))
$manifest=[ordered]@{Scenario=$Scenario;StartedUtc=[DateTime]::UtcNow.ToString('o');SourceCommit=(& git -C $repository rev-parse HEAD).Trim();SourceDirty=[bool](& git -C $repository status --porcelain);Files=@($files|ForEach-Object {Get-FileHash -LiteralPath $_ -Algorithm SHA256|Select-Object Path,Hash});Evidence=$root}
$manifest|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $build 'manifest.json') -Encoding UTF8
if($Scenario -in @('qoder-hooks','qoder-session','qoder-subagent')){
    $entry=@{'qoder-hooks'='--qoder-live';'qoder-session'='--qoder-live-session';'qoder-subagent'='--qoder-live-child'}[$Scenario]
    & (Join-Path $build 'AgentLauncher.Tests.exe') $entry $AgentPath $worker $root
}else{
    & (Join-Path $build 'AgentLauncher.Tests.exe') --live-memory-soak $Scenario $AgentPath $worker $NodePath $repository $root
}
$result=$LASTEXITCODE
if(Test-Path -LiteralPath $root){Copy-Item -LiteralPath (Join-Path $build 'manifest.json') -Destination (Join-Path $root 'manifest.json')}
if($result -ne 0){throw ('Live acceptance failed; evidence: '+$root)}
Write-Output ('Live acceptance passed: '+$root)
