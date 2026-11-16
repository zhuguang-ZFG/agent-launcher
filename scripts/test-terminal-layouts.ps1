param()
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$portable=Join-Path $workspace 'artifacts/terminal-portable-1.25.3131.0/terminal-1.25.3131.0/wt.exe'
if(Test-Path -LiteralPath $portable){$terminal=$portable}
else{throw ('Portable Windows Terminal not found: '+$portable)}
$runtime=Join-Path $workspace 'artifacts/terminal-layout-runtime'
if(Test-Path -LiteralPath $runtime){Remove-Item -LiteralPath $runtime -Recurse -Force}
foreach($project in @('src/AgentLauncher.Worker','tests/AgentLauncher.LayoutChecks')){
  $name=Split-Path $project -Leaf
  & dotnet publish (Join-Path $workspace ($project+'/'+$name+'.csproj')) -c Release -p:DefineConstants=INTEGRATION_TESTS -o $runtime --nologo -m:1
  if($LASTEXITCODE -ne 0){throw ('Publish failed: '+$name)}
}
if(-not(Test-Path -LiteralPath (Join-Path $runtime 'AgentLauncher.Worker.exe'))){throw 'Runtime directory is missing AgentLauncher.Worker.exe'}
$date=Get-Date -Format 'yyyy-MM-dd'
$evidence=Join-Path $workspace ('artifacts/terminal-layout-N3-'+$date)
if(Test-Path -LiteralPath $evidence){$evidence=$evidence+'-'+(Get-Date -Format 'HHmmss')}
$env:AGENT_LAUNCHER_TEST_ROOT=Join-Path $env:TEMP ('AgentLauncher-tests-layout-N3-'+[guid]::NewGuid().ToString('N'))
$env:AGENT_LAUNCHER_FAKE_SAMPLE='1'
Write-Output ('terminal='+$terminal)
Write-Output ('evidence='+$evidence)
try{
  & (Join-Path $runtime 'AgentLauncher.LayoutChecks.exe') --run --n 3 --terminal $terminal --evidence $evidence
  $code=$LASTEXITCODE
}
finally{
  Remove-Item Env:AGENT_LAUNCHER_TEST_ROOT -ErrorAction SilentlyContinue
  Remove-Item Env:AGENT_LAUNCHER_FAKE_SAMPLE -ErrorAction SilentlyContinue
}
Write-Output ('TERMINAL_LAYOUT_EXIT='+$code)
exit $code
