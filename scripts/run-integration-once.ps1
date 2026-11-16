param([string]$Suite = '--integration')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runtime = Join-Path $root 'artifacts/test-runtime'
foreach ($project in @('tests/AgentLauncher.Tests', 'src/AgentLauncher.Worker')) {
    $name = Split-Path $project -Leaf
    & dotnet publish (Join-Path $root "$project/$name.csproj") -c Release -p:DefineConstants=INTEGRATION_TESTS -o $runtime --nologo -m:1
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $name" }
}
$env:AGENT_LAUNCHER_TEST_ROOT = Join-Path $env:TEMP ('AgentLauncher-tests-integration-' + [guid]::NewGuid().ToString('N'))
$env:AGENT_LAUNCHER_FAKE_SAMPLE = '1'
$env:PI_CODING_AGENT_DIR = Join-Path $env:AGENT_LAUNCHER_TEST_ROOT 'pi-config'
try {
    & (Join-Path $runtime 'AgentLauncher.Tests.exe') $Suite
    $suiteExit = $LASTEXITCODE
    Write-Host "INTEGRATION_EXIT=$suiteExit"
} finally {
    Remove-Item Env:AGENT_LAUNCHER_FAKE_SAMPLE -ErrorAction SilentlyContinue
    Remove-Item Env:AGENT_LAUNCHER_TEST_ROOT -ErrorAction SilentlyContinue
}
exit $suiteExit
