param([Parameter(Mandatory=$true)][string]$EvidenceDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($EvidenceDirectory)
$result=Get-Content -LiteralPath (Join-Path $root 'result.json') -Encoding UTF8 -Raw|ConvertFrom-Json
if(-not $result.Passed -or -not $result.Drained -or -not $result.HandshakeConfirmed -or $result.DriverExit -ne 0){throw 'The underlying acceptance run did not pass'}
$work=@(Get-Content -LiteralPath (Join-Path $root 'workload.jsonl') -Encoding UTF8|ForEach-Object {$_|ConvertFrom-Json})
if($work.Count -ne 30 -or $result.Steps -ne 30){throw 'Expected all 30 completed workloads'}
for($i=0;$i -lt $work.Count;$i++){
    if($work[$i].step -ne ($i+1) -or $work[$i].seconds -lt 60 -or $work[$i].passes -lt 1){throw 'Invalid, duplicate, skipped, or shortened workload'}
}
$seconds=($work|Measure-Object seconds -Sum).Sum
if($seconds -lt 1800 -or $result.Seconds -lt $seconds -or $result.Memory.PeakBytes -le 2GB -or $result.Memory.LimitBytes -ne 4GB){throw 'Duration or native Job memory requirements not met'}
$samples=@(Get-Content -LiteralPath (Join-Path $root 'memory.jsonl') -Encoding UTF8|ForEach-Object {$_|ConvertFrom-Json})
if($samples.Count -lt 800 -or @($samples|Where-Object {$null -eq $_.Commit}).Count){throw 'Native memory sampling is incomplete'}
$summary=[ordered]@{
    Passed=$true;Agent=$result.Agent;WallSeconds=$result.Seconds;ProductiveSeconds=$seconds;CompletedSteps=$work.Count
    HashValidations=($work|Measure-Object passes -Sum).Sum;PeakJobMiB=[math]::Round($result.Memory.PeakBytes/1MB,2)
    MinimumAvailablePhysicalMiB=[math]::Round(($samples|Measure-Object Available -Minimum).Minimum/1MB,2)
    MinimumAvailableCommitMiB=[math]::Round(($samples|Measure-Object CommitAvailable -Minimum).Minimum/1MB,2)
    GuardPhysicalFloorMiB=$result.PhysicalFloor/1MB;GuardCommitFloorMiB=$result.CommitFloor/1MB
    SyntheticCommitMiB=$result.SyntheticCommitMiB;AuthenticatedModel=$result.AuthenticatedModel
    EvidenceHashes=@('result.json','workload.jsonl','memory.jsonl','stdout.jsonl','stderr.txt'|ForEach-Object {Get-FileHash -LiteralPath (Join-Path $root $_) -Algorithm SHA256|Select-Object Path,Hash})
}
$summary|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $root 'summary.json') -Encoding UTF8
[pscustomobject]$summary|Select-Object Passed,Agent,WallSeconds,ProductiveSeconds,CompletedSteps,HashValidations,PeakJobMiB|ConvertTo-Json
