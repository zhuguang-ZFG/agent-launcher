$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$env:AGENT_LAUNCHER_TEST_ROOT=Join-Path $env:TEMP ('AgentLauncher-tests-ui-'+[guid]::NewGuid().ToString('N'))
$env:AGENT_LAUNCHER_FAKE_SAMPLE='1'
$report=Join-Path $root 'artifacts/ui-rerun'
New-Item -ItemType Directory -Force -Path $report | Out-Null
Remove-Item (Join-Path $report '*.json') -Force -ErrorAction SilentlyContinue
$progressPath=Join-Path $report 'ui-progress.json'
$startedAt=[DateTime]::UtcNow;$lastProgressAt=$startedAt;$lastProgress='none';$hardDeadline=$startedAt.AddSeconds(150)
$p=Start-Process (Join-Path $root 'artifacts/test-runtime/AgentLauncher.App.exe') -ArgumentList '--ui-check',('"'+$report+'"') -PassThru
while(-not $p.WaitForExit(500)){
  try{$pi=Get-Item $progressPath -ErrorAction Stop;if($pi.LastWriteTimeUtc -ge $startedAt -and $pi.LastWriteTimeUtc -gt $lastProgressAt){$lastProgressAt=$pi.LastWriteTimeUtc;$lastProgress=Get-Content $progressPath -Raw}}catch{}
  $now=[DateTime]::UtcNow
  if($now -ge $hardDeadline -or ($now-$lastProgressAt).TotalSeconds -ge 30){try{$p.Kill()}catch{};throw "UI stalled; last: $lastProgress"}
}
Write-Output ('EXIT='+$p.ExitCode)
Write-Output ('LAST='+$lastProgress)
Remove-Item Env:AGENT_LAUNCHER_FAKE_SAMPLE -ErrorAction SilentlyContinue
