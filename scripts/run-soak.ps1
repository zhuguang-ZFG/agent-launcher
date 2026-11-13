param(
  [Parameter(Mandatory=$true)][string]$OmpPath,
  [Parameter(Mandatory=$true)][string]$OmpVersion,
  [string]$NodePath = (Get-Command node.exe -ErrorAction Stop).Source,
  [string]$GitPath = (Get-Command git.exe -ErrorAction Stop).Source,
  [ValidateRange(1,120)][int]$WaitMinutes = 120,
  [string]$EvidenceDirectory
)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts=Join-Path $workspace 'artifacts'
if(-not $EvidenceDirectory){$EvidenceDirectory=Join-Path $artifacts ('soak-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N'))}
$base=[IO.Path]::GetFullPath($EvidenceDirectory)
if(-not $base.StartsWith($artifacts+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'EvidenceDirectory must be a fresh directory under artifacts'}
if(Test-Path -LiteralPath $base){throw 'EvidenceDirectory already exists; existing evidence will not be overwritten'}
for($cursor=New-Object IO.DirectoryInfo($base);$cursor;$cursor=$cursor.Parent){if($cursor.Exists -and ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Evidence path must not traverse a reparse point'}}
foreach($tool in @($OmpPath,$NodePath,$GitPath)){if(-not [IO.Path]::IsPathRooted($tool) -or -not (Test-Path -LiteralPath $tool -PathType Leaf) -or [IO.Path]::GetExtension($tool) -ne '.exe'){throw 'Tools must be existing absolute exe paths'}}
if($OmpVersion -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.\-]+)?$'){throw 'OmpVersion must be an exact tested version'}
if($env:AGENT_LAUNCHER_FAKE_SAMPLE -or $env:AGENT_LAUNCHER_TEST_ROOT){throw 'Remove inherited fake sampling and test root before real acceptance'}
$sourceSha=(& git -C $workspace rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0 -or (& git -C $workspace status --porcelain)){throw 'Commit source before recording long stability evidence'}
New-Item -ItemType Directory -Path $base | Out-Null
$runtime=Join-Path $base 'runtime'
$summaryPath=Join-Path $base 'supervisor.json'
$events=Join-Path $base 'memory-samples.jsonl'
$summary=[ordered]@{SourceCommit=$sourceSha;Status='building';StartedAtUtc=[DateTime]::UtcNow.ToString('o');RequiredBytes=[uint64](2GB+2*768MB);StableSamples=0;AvailablePhysicalBytes=$null;CalibrationRoot=$null;StabilityRoot=$null;Failure=$null;CompletedAtUtc=$null}
function Save-Supervisor {
  $temp=$summaryPath+'.tmp-'+[guid]::NewGuid().ToString('N')
  try {
    $bytes=[Text.Encoding]::UTF8.GetBytes(($summary|ConvertTo-Json -Depth 8))
    $stream=[IO.File]::Open($temp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
    if(Test-Path -LiteralPath $summaryPath){[IO.File]::Replace($temp,$summaryPath,$summaryPath+'.bak')}else{[IO.File]::Move($temp,$summaryPath)}
  }finally{if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp}}
}
function Assert-Source {
  if((& git -C $workspace rev-parse HEAD).Trim() -ne $sourceSha -or (& git -C $workspace status --porcelain)){throw 'Source changed during acceptance; stopped before next load'}
}
function Invoke-Observation([string]$name,[bool]$calibration) {
  Assert-Source
  $testRoot=Join-Path $base ('AgentLauncher-tests-'+$name+'-'+[guid]::NewGuid().ToString('N'))
  if($calibration){$summary.CalibrationRoot=$testRoot}else{$summary.StabilityRoot=$testRoot}
  $summary.Status=$name;Save-Supervisor
  $arguments=@('observe','--root',$testRoot,'--runtime',$runtime,'--omp',$OmpPath,'--omp-version',$OmpVersion,'--node',$NodePath,'--git',$GitPath)
  if($calibration){$arguments+=@('--calibration-seconds','20')}
  & (Join-Path $runtime 'SoakRunner.exe') @arguments *> (Join-Path $base ($name+'.log'))
  $runnerExit=$LASTEXITCODE
  $reports=@(Get-ChildItem -LiteralPath $testRoot -Filter 'soak-result.json' -Recurse -File)
  if($reports.Count -ne 1){throw ($name+': expected one final result; inspect runner log')}
  $report=Get-Content -LiteralPath $reports[0].FullName -Encoding UTF8 -Raw|ConvertFrom-Json
  if($runnerExit -ne 0){throw ($name+': exit '+$runnerExit+'; '+$report.failure)}
  if(-not $report.cleanup.verified -or @($report.cleanup.errors).Count -or $report.cleanup.monitorLeftRunning){throw ($name+': cleanup was not verified')}
  if($calibration){
    if($report.status -ne 'calibration-completed' -or $report.rounds.Count -ne 1 -or -not $report.rounds[0].passed -or $report.stability.eligible){throw 'Invalid calibration evidence'}
  }elseif($report.status -ne 'succeeded' -or -not $report.stability.eligible -or $report.stability.completedRounds -ne 3){throw 'Long stability requirements not met'}
  Assert-Source
}
try {
  Save-Supervisor
  $previousBuildAppData=$env:APPDATA
  try {
    $env:APPDATA=Join-Path $base 'build-appdata'
    $nuget=Join-Path $env:APPDATA 'NuGet'
    New-Item -ItemType Directory -Path $nuget -Force|Out-Null
    [IO.File]::WriteAllText((Join-Path $nuget 'NuGet.Config'),'<configuration><packageSources><clear /></packageSources></configuration>')
    foreach($project in @('tests/AgentLauncher.Soak/AgentLauncher.Soak.csproj','src/AgentLauncher.Worker/AgentLauncher.Worker.csproj')){
      & dotnet publish (Join-Path $workspace $project) -c Release -p:DefineConstants=INTEGRATION_TESTS -o $runtime --nologo -m:1 -p:UseSharedCompilation=false -nodeReuse:false >> (Join-Path $base 'build.log')
      if($LASTEXITCODE -ne 0){throw ('Acceptance publish failed: '+$project)}
    }
  }finally{
    $env:APPDATA=$previousBuildAppData
  }
  Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class LauncherSoakMemory {
  [StructLayout(LayoutKind.Sequential)] public struct Status {
    public uint Length,Load;
    public ulong TotalPhysical,AvailablePhysical,TotalPage,AvailablePage,TotalVirtual,AvailableVirtual,Extended;
  }
  [DllImport("kernel32.dll",SetLastError=true)] public static extern bool GlobalMemoryStatusEx(ref Status status);
}
'@
  $summary.Status='waiting-for-memory';Save-Supervisor
  $watch=[Diagnostics.Stopwatch]::StartNew()
  while($watch.Elapsed.TotalMinutes -lt $WaitMinutes){
    Assert-Source
    $sample=New-Object LauncherSoakMemory+Status
    $sample.Length=[Runtime.InteropServices.Marshal]::SizeOf($sample)
    if(-not [LauncherSoakMemory]::GlobalMemoryStatusEx([ref]$sample)){throw 'Physical memory query failed'}
    $summary.AvailablePhysicalBytes=$sample.AvailablePhysical
    if($sample.AvailablePhysical -ge $summary.RequiredBytes){$summary.StableSamples++}else{$summary.StableSamples=0}
    [ordered]@{AtUtc=[DateTime]::UtcNow.ToString('o');ElapsedSeconds=$watch.Elapsed.TotalSeconds;Available=$sample.AvailablePhysical;CommitAvailable=$sample.AvailablePage;StableSamples=$summary.StableSamples}|ConvertTo-Json -Compress|Add-Content -LiteralPath $events -Encoding UTF8
    Save-Supervisor
    if($summary.StableSamples -ge 3){break}
    Start-Sleep -Seconds 15
  }
  if($summary.StableSamples -lt 3){throw 'Timed out waiting for three samples at the original 3.5 GiB floor; no workload started'}
  Invoke-Observation 'calibration' $true
  Invoke-Observation 'stability' $false
  $summary.Status='succeeded'
}catch{
  $summary.Status='stopped';$summary.Failure=$_.ToString();throw
}finally{
  if($summary.Status -notin @('succeeded','stopped')){$summary.Status='interrupted';$summary.Failure='Execution interrupted; inspect any launched runner and Job evidence before reuse'}
  $summary.CompletedAtUtc=[DateTime]::UtcNow.ToString('o');Save-Supervisor
}
