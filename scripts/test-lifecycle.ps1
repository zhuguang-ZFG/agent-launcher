param([string]$RuntimeDirectory = (Join-Path $PSScriptRoot '../artifacts/test-runtime'), [switch]$SkipBuild)
# design.md §8 lifecycle gates: INT, DRAIN, CLOSE, WPF-CRASH, REOPEN, WORKER-EXIT.
# Evidence: artifacts/embedded-terminal/<gate>.txt. ASCII-only script body on purpose: run under Windows PowerShell 5.1 without a BOM; Chinese strings flow from the JSON files.
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runtime=[IO.Path]::GetFullPath($RuntimeDirectory)
$evidence=Join-Path $root 'artifacts/embedded-terminal'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$exe=Join-Path $runtime 'AgentLauncher.App.exe'
if(-not $SkipBuild){
  foreach($project in @('tests/AgentLauncher.Soak','tests/AgentLauncher.Tests','src/AgentLauncher.Worker','src/AgentLauncher.App')){
    $name=Split-Path $project -Leaf
    & dotnet publish (Join-Path $root "$project/$name.csproj") -c Release -p:DefineConstants=INTEGRATION_TESTS -o $runtime --nologo -m:1
    if($LASTEXITCODE -ne 0){throw "Test publish failed: $name"}
  }
}

function Read-Json([string]$path){
  if(-not (Test-Path -LiteralPath $path)){ return $null }
  return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json)
}
function Wait-File([string]$path,[int]$seconds){
  $deadline=[DateTime]::UtcNow.AddSeconds($seconds)
  while([DateTime]::UtcNow -lt $deadline){
    if(Test-Path -LiteralPath $path){ return $true }
    Start-Sleep -Milliseconds 200
  }
  return (Test-Path -LiteralPath $path)
}
function Test-PidAlive([int]$processId){
  if($processId -le 0){ return $false }
  try{ $proc=Get-Process -Id $processId -ErrorAction Stop; return (-not $proc.HasExited) }catch{ return $false }
}
function Stop-Pid([int]$processId){
  if($processId -le 0){ return }
  if(Test-PidAlive $processId){ & taskkill @('/PID',"$processId",'/F') | Out-Null }
}
function New-TestRoot([string]$suite){
  $env:AGENT_LAUNCHER_TEST_ROOT=Join-Path $env:TEMP ('AgentLauncher-tests-lifecycle-'+$suite+'-'+[guid]::NewGuid().ToString('N'))
  $env:AGENT_LAUNCHER_FAKE_SAMPLE='1'
  $env:PI_CODING_AGENT_DIR=Join-Path $env:AGENT_LAUNCHER_TEST_ROOT 'pi-config'
}
function Clear-TestRoot(){
  $monitor=Read-Json (Join-Path $env:AGENT_LAUNCHER_TEST_ROOT 'monitor-pid.json')
  if($monitor){ Stop-Pid ([int]$monitor) }
  foreach($marker in @(Get-ChildItem -LiteralPath $env:AGENT_LAUNCHER_TEST_ROOT -Filter 'embedded.json' -Recurse -ErrorAction SilentlyContinue)){
    try{ $hostMark=Read-Json $marker.FullName; if($hostMark.Pid){ Stop-Pid ([int]$hostMark.Pid) } }catch{}
  }
  $custody=Read-Json (Join-Path $env:AGENT_LAUNCHER_TEST_ROOT 'custody.json')
  if($custody){ Stop-Pid ([int]$custody.Pid) }
  try{
    if(Test-Path -LiteralPath $env:AGENT_LAUNCHER_TEST_ROOT){
      if(@(Get-ChildItem -LiteralPath $env:AGENT_LAUNCHER_TEST_ROOT -Filter '*-state.error.json' -ErrorAction SilentlyContinue).Count -gt 0){
        try{ Copy-Item -LiteralPath $env:AGENT_LAUNCHER_TEST_ROOT -Destination (Join-Path $evidence ((Split-Path -Leaf $env:AGENT_LAUNCHER_TEST_ROOT)+'-diagnostics')) -Recurse -Force }catch{}
      }
      Remove-Item -LiteralPath $env:AGENT_LAUNCHER_TEST_ROOT -Recurse -Force
    }
  }catch{}
  Remove-Item Env:AGENT_LAUNCHER_TEST_ROOT -ErrorAction SilentlyContinue
  Remove-Item Env:AGENT_LAUNCHER_FAKE_SAMPLE -ErrorAction SilentlyContinue
  Remove-Item Env:PI_CODING_AGENT_DIR -ErrorAction SilentlyContinue
}
function Start-Stage([string]$stage,[string]$statePath){
  return Start-Process $exe -WindowStyle Hidden -PassThru -ArgumentList '--embedded-stage',$stage,('"'+$statePath+'"')
}
function Get-AttemptDir([string]$attemptId){
  return (Join-Path (Join-Path $env:AGENT_LAUNCHER_TEST_ROOT 'attempts') ($attemptId -replace '-',''))
}
function Get-Entry([object]$ledger,[string]$attemptId){
  if($null -eq $ledger){ return $null }
  $found=@($ledger.Entries | Where-Object { "$($_.Plan.Id)" -eq $attemptId })
  if($found.Count -gt 0){ return $found[0] }
  return $null
}
function Write-Evidence([string]$name,[string[]]$lines){
  $text=[Environment]::NewLine+($lines -join [Environment]::NewLine)+[Environment]::NewLine
  [IO.File]::WriteAllText((Join-Path $evidence $name),$text,[Text.UTF8Encoding]::new($false))
}
$summary=New-Object System.Collections.Generic.List[string]
function Record([string]$gate,[string]$status){
  $summary.Add(($gate+' = '+$status))
  Write-Host ($gate+' '+$status)
}
function Get-Result([bool]$Passed){ if($Passed){ return 'PASSED' } return 'FAILED' }
$summary=New-Object System.Collections.Generic.List[string]

# ---------------------------------------------------------------- INT
New-TestRoot 'interrupt'
try{
  $state=Join-Path $env:AGENT_LAUNCHER_TEST_ROOT 'interrupt-state.json'
  $app=Start-Stage 'interrupt' $state
  $exited=$app.WaitForExit(150000)
  $info=Read-Json $state
  $receipt=$null
  if($info){ $receipt=Read-Json (Join-Path (Get-AttemptDir $info.Attempt) 'receipt.json') }
  if(-not $exited){ $app.Kill() }
  $passed=$exited -and $app.ExitCode -eq 0 -and $info -and $info.Passed -and $info.InterruptByteReceived -and $receipt -and $receipt.Phase -eq 'Finished' -and $receipt.ExitCode -eq 0
  Write-Evidence 'interrupt.txt' @(
    'Gate INT: Ctrl+C byte 0x03 reaches the child; no TerminateJobObject (natural exit 0; a Job kill would leave exit code 1).',
    'Command: AgentLauncher.App.exe --embedded-stage interrupt',
    ('Attempt: '+$info.Attempt+'  AgentPid: '+$info.AgentPid),
    ('Interrupt record bytes: '+$info.RecordBytes+'  contains 0x03: '+$info.InterruptByteReceived),
    ('Receipt: Phase='+$receipt.Phase+' ExitCode='+$receipt.ExitCode),
    ('App exit code: '+$app.ExitCode),
    ('Result: '+(Get-Result -Passed:$passed))
  )
  Record 'INT' $(if($passed){'PASSED'}else{'FAILED'})
  if(-not $passed){ throw 'INT gate failed' }
}finally{ Clear-TestRoot }

# ---------------------------------------------------------------- DRAIN
New-TestRoot 'drain'
try{
  $state=Join-Path $env:AGENT_LAUNCHER_TEST_ROOT 'drain-state.json'
  $app=Start-Stage 'drain' $state
  $exited=$app.WaitForExit(240000)
  $info=Read-Json $state
  $receipt=$null
  if($info){ $receipt=Read-Json (Join-Path (Get-AttemptDir $info.Attempt) 'receipt.json') }
  if(-not $exited){ $app.Kill() }
  $passed=$exited -and $app.ExitCode -eq 0 -and $info -and $info.Passed -and $info.WriteMs -le 30000 -and $info.GenerationAfter -gt $info.GenerationBefore -and $info.FramesAppliedAfterReconnect -le 10 -and $receipt -and $receipt.Phase -eq 'Finished' -and $receipt.ExitCode -eq 0
  Write-Evidence 'drain.txt' @(
    'Gate DRAIN: child writes 8 MiB while the UI is detached; after reattach only one fresh full generation is applied.',
    'Command: AgentLauncher.App.exe --embedded-stage drain',
    ('Attempt: '+$info.Attempt+'  AgentPid: '+$info.AgentPid),
    ('WriteMs (detach -> pty accepted 8 MiB + DRAINDONE): '+$info.WriteMs+'  budget: 30000'),
    ('Generation before detach: '+$info.GenerationBefore+'  after reattach: '+$info.GenerationAfter),
    ('Frames applied after reattach: '+$info.FramesAppliedAfterReconnect),
    ('Receipt: Phase='+$receipt.Phase+' ExitCode='+$receipt.ExitCode),
    ('App exit code: '+$app.ExitCode),
    ('Result: '+(Get-Result -Passed:$passed))
  )
  Record 'DRAIN' $(if($passed){'PASSED'}else{'FAILED'})
  if(-not $passed){ throw 'DRAIN gate failed' }
}finally{ Clear-TestRoot }

# ---------------------------------------------------------------- CLOSE
New-TestRoot 'close'
try{
  $state=Join-Path $env:AGENT_LAUNCHER_TEST_ROOT 'close-state.json'
  $app=Start-Stage 'close-window' $state
  if(-not (Wait-File $state 90)){ $app.Kill(); throw 'CLOSE: prepare did not finish' }
  $info=Read-Json $state
  $attempt=Get-AttemptDir $info.Attempt
  if(-not $app.WaitForExit(60000)){ $app.Kill(); throw 'CLOSE: App did not exit after window close' }
  $receipt=Read-Json (Join-Path $attempt 'receipt.json')
  $hostPid=[int]((Read-Json (Join-Path $attempt 'embedded.json')).Pid)
  $monitor=Read-Json (Join-Path $env:AGENT_LAUNCHER_TEST_ROOT 'monitor-pid.json')
  $ledger=Read-Json (Join-Path $env:AGENT_LAUNCHER_TEST_ROOT 'control.json')
  $entry=Get-Entry $ledger $info.Attempt
  $agentAlive=Test-PidAlive ([int]$info.AgentPid)
  $hostAlive=Test-PidAlive $hostPid
  $monitorAlive=$false
  if($monitor){ $monitorAlive=Test-PidAlive ([int]$monitor) }
  $receiptNonTerminal=($receipt -and $receipt.Phase -eq 'Started' -and $null -eq $receipt.ExitCode)
  $entryHeld=($entry -and $entry.State -eq 'Started')
  $passed=$app.ExitCode -eq 0 -and $receiptNonTerminal -and $agentAlive -and $hostAlive -and $monitorAlive -and $entryHeld
  Write-Evidence 'close-window.txt' @(
    'Gate CLOSE: WPF window closes without Stop; receipt stays non-terminal and the agent survives headless.',
    'Command: AgentLauncher.App.exe --embedded-stage close-window',
    ('Attempt: '+$info.Attempt+'  AgentPid: '+$info.AgentPid+'  HostPid: '+$hostPid),
    ('Receipt: Phase='+$receipt.Phase+' ExitCode='+$receipt.ExitCode+' (expect Started / null)'),
    ('Agent alive: '+$agentAlive+'  Terminal host alive: '+$hostAlive+'  Monitor alive: '+$monitorAlive),
    ('Ledger entry: State='+$entry.State+' Detail='+$entry.Detail),
    ('App exit code: '+$app.ExitCode),
    ('Result: '+(Get-Result -Passed:$passed))
  )
  Record 'CLOSE' $(if($passed){'PASSED'}else{'FAILED'})
  if(-not $passed){ throw 'CLOSE gate failed' }
  Stop-Pid ([int]$info.AgentPid)
  Stop-Pid $hostPid
}finally{ Clear-TestRoot }

# ---------------------------------------------------------------- WPF-CRASH then REOPEN (same session)
New-TestRoot 'crash'
try{
  $state=Join-Path $env:AGENT_LAUNCHER_TEST_ROOT 'crash-state.json'
  $app=Start-Stage 'crash-wait' $state
  if(-not (Wait-File $state 90)){ $app.Kill(); throw 'WPF-CRASH: prepare did not finish' }
  $info=Read-Json $state
  $attempt=Get-AttemptDir $info.Attempt
  # Kill only the WPF process itself (no /T): terminal host, pane worker, agent and monitor must survive.
  & taskkill @('/PID',"$($app.Id)",'/F') | Out-Null
  $app.WaitForExit(10000) | Out-Null
  $receipt=Read-Json (Join-Path $attempt 'receipt.json')
  $hostPid=[int]((Read-Json (Join-Path $attempt 'embedded.json')).Pid)
  $monitor=Read-Json (Join-Path $env:AGENT_LAUNCHER_TEST_ROOT 'monitor-pid.json')
  $ledger=Read-Json (Join-Path $env:AGENT_LAUNCHER_TEST_ROOT 'control.json')
  $entry=Get-Entry $ledger $info.Attempt
  $agentAlive=Test-PidAlive ([int]$info.AgentPid)
  $hostAlive=Test-PidAlive $hostPid
  $monitorAlive=$false
  if($monitor){ $monitorAlive=Test-PidAlive ([int]$monitor) }
  $receiptNonTerminal=($receipt -and $receipt.Phase -eq 'Started' -and $null -eq $receipt.ExitCode)
  $entryHeld=($entry -and $entry.State -eq 'Started')
  $crashPassed=$agentAlive -and $hostAlive -and $monitorAlive -and $receiptNonTerminal -and $entryHeld
  Write-Evidence 'wpf-crash.txt' @(
    'Gate WPF-CRASH: kill the WPF process only; agent, terminal host and monitor survive.',
    'Command: AgentLauncher.App.exe --embedded-stage crash-wait, then taskkill /PID <App> /F',
    ('Attempt: '+$info.Attempt+'  AgentPid: '+$info.AgentPid+'  HostPid: '+$hostPid),
    ('Receipt: Phase='+$receipt.Phase+' ExitCode='+$receipt.ExitCode+' (expect Started / null)'),
    ('Agent alive: '+$agentAlive+'  Terminal host alive: '+$hostAlive+'  Monitor alive: '+$monitorAlive),
    ('Ledger entry: State='+$entry.State+' Detail='+$entry.Detail),
    ('Result: '+(Get-Result -Passed:$crashPassed))
  )
  Record 'WPF-CRASH' $(if($crashPassed){'PASSED'}else{'FAILED'})
  if(-not $crashPassed){ throw 'WPF-CRASH gate failed' }

  $reopen=Start-Stage 'reopen' $state
  $exited=$reopen.WaitForExit(180000)
  $info2=Read-Json $state
  $receipt2=$null
  if($info2){ $receipt2=Read-Json (Join-Path (Get-AttemptDir $info2.Attempt) 'receipt.json') }
  if(-not $exited){ $reopen.Kill() }
  $reopenPassed=$exited -and $reopen.ExitCode -eq 0 -and $info2 -and $info2.Passed -and $info2.RestoredScreen -and $info2.SameAgent -and $receipt2 -and $receipt2.Phase -eq 'Finished' -and $receipt2.ExitCode -eq 0
  Write-Evidence 'reopen.txt' @(
    'Gate REOPEN: a fresh App instance boots against the existing ledger and reattaches the surviving terminal host.',
    'Deviation vs design.md 8.6: the tree intentionally offers real screen restoration (stronger than the design''s ledger-only expectation); recorded here instead of silently passing.',
    'Command: AgentLauncher.App.exe --embedded-stage reopen',
    ('Attempt: '+$info2.Attempt+'  AgentPid: '+$info2.AgentPid+' (same pid as before the crash: '+$info2.SameAgent+')'),
    ('LedgerShown: '+$info2.LedgerShown+'  RestoredScreen: '+$info2.RestoredScreen),
    ('Receipt: Phase='+$receipt2.Phase+' ExitCode='+$receipt2.ExitCode),
    ('App exit code: '+$reopen.ExitCode),
    ('Result: '+(Get-Result -Passed:$reopenPassed))
  )
  Record 'REOPEN' $(if($reopenPassed){'PASSED'}else{'FAILED'})
  if(-not $reopenPassed){ throw 'REOPEN gate failed' }
  Stop-Pid ([int]$info.AgentPid)
  Stop-Pid $hostPid
}finally{ Clear-TestRoot }

# ---------------------------------------------------------------- WORKER-EXIT
New-TestRoot 'worker-exit'
$app=$null
try{
  $state=Join-Path $env:AGENT_LAUNCHER_TEST_ROOT 'worker-state.json'
  $app=Start-Stage 'worker-exit' $state
  if(-not (Wait-File $state 90)){ $app.Kill(); throw 'WORKER-EXIT: prepare did not finish' }
  $info=Read-Json $state
  $attempt=Get-AttemptDir $info.Attempt
  $runner=Read-Json (Join-Path $attempt 'runner-entered.json')
  $sshBefore=@(Get-Process -Name ssh -ErrorAction SilentlyContinue).Count
  # Kill only the pane worker (no /T): its child agent must survive.
  & taskkill @('/PID',"$($runner.Pid)",'/F') | Out-Null
  $hostExitPath=Join-Path $attempt 'host-exit.json'
  $noticed=Wait-File $hostExitPath 25
  $hostExit=Read-Json $hostExitPath
  $receipt=Read-Json (Join-Path $attempt 'receipt.json')
  $ledger=Read-Json (Join-Path $env:AGENT_LAUNCHER_TEST_ROOT 'control.json')
  $entry=Get-Entry $ledger $info.Attempt
  $sshAfter=@(Get-Process -Name ssh -ErrorAction SilentlyContinue).Count
  $agentAlive=Test-PidAlive ([int]$info.AgentPid)
  $knownNull=($hostExit -and $null -eq $hostExit.KnownExitCode)
  $noFakeExit=($receipt -and $receipt.Phase -eq 'Started' -and $null -eq $receipt.ExitCode)
  $entryUnknown=$false
  if($entry -and $hostExit){ $entryUnknown=($entry.State -eq 'Started' -and "$($entry.Detail)" -like ('*'+$hostExit.Detail+'*')) }
  $passed=$noticed -and $knownNull -and $noFakeExit -and $entryUnknown -and ($sshBefore -eq $sshAfter)
  Write-Evidence 'worker-exit.txt' @(
    'Gate WORKER-EXIT: kill the pane worker; the monitor marks the attempt as host-exit-unknown without fabricating an exit code.',
    'Command: AgentLauncher.App.exe --embedded-stage worker-exit, then taskkill /PID <pane worker> /F',
    ('Attempt: '+$info.Attempt+'  AgentPid: '+$info.AgentPid+'  PaneWorkerPid: '+$runner.Pid),
    ('host-exit.json: KnownExitCode='+$(if($hostExit){$hostExit.KnownExitCode}else{'<missing>'})+' Detail='+$(if($hostExit){$hostExit.Detail}else{'<missing>'})),
    ('Receipt unchanged: Phase='+$receipt.Phase+' ExitCode='+$receipt.ExitCode+' (expect Started / null; no fabricated 255)'),
    ('Ledger entry: State='+$entry.State+' Detail='+$entry.Detail),
    ('Agent alive after pane worker death (truthful observation, not a gate condition): '+$agentAlive),
    ('ssh.exe count before: '+$sshBefore+'  after: '+$sshAfter),
    ('Monitor wrote host-exit.json: '+$noticed),
    ('Result: '+(Get-Result -Passed:$passed))
  )
  Record 'WORKER-EXIT' $(if($passed){'PASSED'}else{'FAILED'})
  if(-not $passed){ throw 'WORKER-EXIT gate failed' }
  Stop-Pid ([int]$info.AgentPid)
}finally{ if($app){ Stop-Pid $app.Id }; Clear-TestRoot }

[IO.File]::WriteAllText((Join-Path $evidence 'summary.txt'),(($summary -join [Environment]::NewLine)+[Environment]::NewLine),[Text.UTF8Encoding]::new($false))
Write-Host 'Lifecycle gates complete. Evidence:' $evidence
