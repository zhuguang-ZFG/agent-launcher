# 用法：powershell -NoProfile -Command "& scripts/test-terminal-layouts.ps1 -N 3,4,8"
# 注意：PowerShell 5.1 的 -File ... -N 8,16 会把数组错误绑定成整数 816；必须用 -Command 形式。
param([int[]]$N=@(3,4))
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
$aggregate=0
foreach($n in $N){
  $evidence=Join-Path $workspace ('artifacts/terminal-layout-N'+$n+'-'+$date)
  if(Test-Path -LiteralPath $evidence){$evidence=$evidence+'-'+(Get-Date -Format 'HHmmss')}
  $env:AGENT_LAUNCHER_TEST_ROOT=Join-Path $env:TEMP ('AgentLauncher-tests-layout-N'+$n+'-'+[guid]::NewGuid().ToString('N'))
  $env:AGENT_LAUNCHER_FAKE_SAMPLE='1'
  Write-Output ('terminal='+$terminal)
  Write-Output ('n='+$n+' evidence='+$evidence)
  try{
    & (Join-Path $runtime 'AgentLauncher.LayoutChecks.exe') --run --n $n --terminal $terminal --evidence $evidence
    $code=$LASTEXITCODE
  }
  finally{
    Remove-Item Env:AGENT_LAUNCHER_TEST_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:AGENT_LAUNCHER_FAKE_SAMPLE -ErrorAction SilentlyContinue
  }
  Write-Output ('TERMINAL_LAYOUT_N'+$n+'_EXIT='+$code)
  if($code -ne 0 -and $aggregate -eq 0){$aggregate=$code}
}
Write-Output ('TERMINAL_LAYOUT_EXIT='+$aggregate)
exit $aggregate
