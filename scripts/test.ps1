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
  & (Join-Path $runtime 'AgentLauncher.Tests.exe') --integration
  if($LASTEXITCODE -ne 0){throw 'Integration failed'}
  Copy-Item (Join-Path $env:AGENT_LAUNCHER_TEST_ROOT 'integration-results.json') (Join-Path $root 'artifacts/integration-results.json')
  Remove-Item Env:AGENT_LAUNCHER_FAKE_SAMPLE
  if(-not $SkipUi){
    $env:AGENT_LAUNCHER_TEST_ROOT=Join-Path $env:TEMP ('AgentLauncher-tests-ui-'+[guid]::NewGuid().ToString('N'))
    $report=Join-Path $root 'artifacts/ui'
    $p=Start-Process (Join-Path $runtime 'AgentLauncher.App.exe') -ArgumentList '--ui-check',('"'+$report+'"') -PassThru
    if(-not $p.WaitForExit(30000)){$p.Kill();throw 'UI check timeout'}
    if($p.ExitCode -ne 0 -or -not(Test-Path (Join-Path $report 'ui-checks.json'))){throw 'UI checks failed'}
  }
  & node --test (Join-Path $root 'tests/prototype.test.cjs')
  if($LASTEXITCODE -ne 0){throw 'Prototype model tests failed'}
  if(-not $SkipBrowser){& node (Join-Path $root 'scripts/test-browser.mjs');if($LASTEXITCODE -ne 0){throw 'Browser tests failed'}}
} finally {
  Remove-Item Env:AGENT_LAUNCHER_FAKE_SAMPLE -ErrorAction SilentlyContinue
  Remove-Item Env:AGENT_LAUNCHER_TEST_ROOT -ErrorAction SilentlyContinue
}
