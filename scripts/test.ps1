param([switch]$SkipUi, [switch]$SkipBrowser)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runtime=Join-Path $root 'artifacts/test-runtime'
& dotnet run --project (Join-Path $root 'tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj') -c Release
if($LASTEXITCODE -ne 0){throw 'Native/model tests failed'}
foreach($project in @('tests/AgentLauncher.Tests','src/AgentLauncher.Worker','src/AgentLauncher.App')){
  $name=Split-Path $project -Leaf
  & dotnet publish (Join-Path $root "$project/$name.csproj") -c Release -p:DefineConstants=INTEGRATION_TESTS -o $runtime --nologo -m:1
  if($LASTEXITCODE -ne 0){throw "Test publish failed: $name"}
}
$env:AGENT_LAUNCHER_TEST_ROOT=Join-Path $env:TEMP ('AgentLauncher-tests-integration-'+[guid]::NewGuid().ToString('N'))
$env:AGENT_LAUNCHER_FAKE_SAMPLE='1'
try {
  $env:AGENT_LAUNCHER_TEST_ROOT=Join-Path $env:TEMP ('AgentLauncher-tests-repairs-'+[guid]::NewGuid().ToString('N'))
  & (Join-Path $runtime 'AgentLauncher.Tests.exe') --repair-checks
  if($LASTEXITCODE -ne 0){throw 'Repair checks failed'}
  Copy-Item (Join-Path $env:AGENT_LAUNCHER_TEST_ROOT 'repair-results.json') (Join-Path $root 'artifacts/repair-results.json')
  foreach($suite in @('parallel-batch','terminal-batch','integration')){
    $env:AGENT_LAUNCHER_TEST_ROOT=Join-Path $env:TEMP ('AgentLauncher-tests-'+$suite+'-'+[guid]::NewGuid().ToString('N'))
    $testArgument=if($suite -eq 'integration'){'--integration'}else{'--'+$suite+'-checks'}
    try { & (Join-Path $runtime 'AgentLauncher.Tests.exe') $testArgument }
    finally {
    $diagnostics=Join-Path $root ('artifacts/ci-diagnostics/'+$suite)
    New-Item -ItemType Directory -Path $diagnostics -Force | Out-Null
    foreach($name in @('control.json','control.json.bak','monitor-pid.json','monitor-error.json','terminal-16-diagnostics.json','integration-results.json','attempts')){
      $source=Join-Path $env:AGENT_LAUNCHER_TEST_ROOT $name
      if(Test-Path -LiteralPath $source){Copy-Item -LiteralPath $source -Destination $diagnostics -Recurse -Force}
    }
    }
    if($LASTEXITCODE -ne 0){throw "$suite checks failed"}
  }
  Copy-Item (Join-Path $env:AGENT_LAUNCHER_TEST_ROOT 'integration-results.json') (Join-Path $root 'artifacts/integration-results.json')
  Remove-Item Env:AGENT_LAUNCHER_FAKE_SAMPLE
  if(-not $SkipUi){
    $env:AGENT_LAUNCHER_TEST_ROOT=Join-Path $env:TEMP ('AgentLauncher-tests-ui-'+[guid]::NewGuid().ToString('N'))
    $report=Join-Path $root 'artifacts/ui'
    $progressPath=Join-Path $report 'ui-progress.json'
    $startedAt=[DateTime]::UtcNow;$lastProgressAt=$startedAt;$lastProgress='no UI progress recorded';$hardDeadline=$startedAt.AddSeconds(90)
    $p=Start-Process (Join-Path $runtime 'AgentLauncher.App.exe') -ArgumentList '--ui-check',('"'+$report+'"') -PassThru
    while(-not $p.WaitForExit(500)){
      try{$progressInfo=Get-Item $progressPath -ErrorAction Stop;if($progressInfo.LastWriteTimeUtc -ge $startedAt -and $progressInfo.LastWriteTimeUtc -gt $lastProgressAt){$lastProgressAt=$progressInfo.LastWriteTimeUtc;$lastProgress=Get-Content $progressPath -Raw -ErrorAction Stop}}catch{}
      $now=[DateTime]::UtcNow
      if($now -ge $hardDeadline -or ($now-$lastProgressAt).TotalSeconds -ge 30){try{$p.Kill()}catch{};if(-not $p.WaitForExit(5000)){throw "UI check could not stop after timeout; last progress: $lastProgress"};throw "UI check stalled or exceeded 90-second hard timeout; last progress: $lastProgress"}
    }
    if($p.ExitCode -ne 0 -or -not(Test-Path (Join-Path $report 'ui-checks.json'))){throw 'UI checks failed'}
  }
  & node --test (Join-Path $root 'tests/prototype.test.cjs')
  if($LASTEXITCODE -ne 0){throw 'Prototype model tests failed'}
  if(-not $SkipBrowser){& node (Join-Path $root 'scripts/test-browser.mjs');if($LASTEXITCODE -ne 0){throw 'Browser tests failed'}}
} finally {
  Remove-Item Env:AGENT_LAUNCHER_FAKE_SAMPLE -ErrorAction SilentlyContinue
  Remove-Item Env:AGENT_LAUNCHER_TEST_ROOT -ErrorAction SilentlyContinue
}
