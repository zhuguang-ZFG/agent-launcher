$ErrorActionPreference='Stop'
# User authorization is for disposable CI only; never install a runtime on the workstation.
if($env:GITHUB_ACTIONS -ne 'true' -or -not $env:RUNNER_TEMP){throw 'Run this experiment in an independent GitHub Actions environment'}
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$evidence=Join-Path $workspace 'artifacts/omp-host-isolation'
if(Test-Path -LiteralPath $evidence){throw 'Audit evidence already exists'}
New-Item -ItemType Directory -Path $evidence|Out-Null
$temporary=Join-Path $env:RUNNER_TEMP ('launcher-omp-audit-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary|Out-Null
$archive=Join-Path $temporary 'bun.zip'
Invoke-WebRequest -UseBasicParsing 'https://github.com/oven-sh/bun/releases/download/bun-v1.4.4/bun-windows-x64.zip' -OutFile $archive
$bunHash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if($bunHash -ne 'cd2c0fccc78f3ede1f50ca827a15f063eda13d4d150a3d0674f4f6e323d864a5'){throw 'Pinned Bun release SHA256 mismatch'}
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $temporary 'bun')
$bun=Join-Path $temporary 'bun/bun-windows-x64/bun.exe'
if((& $bun --version).Trim() -ne '1.4.4'){throw 'Unexpected Bun version'}
$prefix=Join-Path $temporary 'sdk'
New-Item -ItemType Directory -Path $prefix|Out-Null
'{"private":true,"dependencies":{"@oh-my-pi/pi-coding-agent":"18.19.0"}}'|Set-Content -LiteralPath (Join-Path $prefix 'package.json') -Encoding ASCII
$previousErrorPreference=$ErrorActionPreference
try{
    # Windows PowerShell treats native stderr warnings as ErrorRecords. Preserve
    # their text, then use npm's actual exit code to decide installation success.
    $ErrorActionPreference='Continue'
    & npm.cmd install --prefix $prefix --ignore-scripts --no-audit --no-fund *> (Join-Path $evidence 'install.log')
    $installExit=$LASTEXITCODE
}finally{$ErrorActionPreference=$previousErrorPreference}
if($installExit -ne 0){throw 'Isolated SDK dependency installation failed'}
$lock=Get-Content -LiteralPath (Join-Path $prefix 'package-lock.json') -Raw|ConvertFrom-Json
$sdk=$lock.packages.'node_modules/@oh-my-pi/pi-coding-agent'
if($sdk.version -ne '18.19.0' -or $sdk.integrity -ne 'sha512-NYElWsH+J5Lo9pUitIOQNcWFipu+VzZgZ32z8dz+kA3Z/43483FEhhU6iEm6xjueYLX8p1CU8j7+2pwj5r9dgA=='){throw 'Pinned SDK integrity mismatch'}
Copy-Item -LiteralPath (Join-Path $prefix 'package-lock.json') -Destination $evidence
$probe=Join-Path $prefix 'isolation.mjs'
Copy-Item -LiteralPath (Join-Path $workspace 'tests/omp-host/isolation.mjs') -Destination $probe
$root=Join-Path $temporary 'test-home'
New-Item -ItemType Directory -Path $root|Out-Null
$result=Join-Path $evidence 'result.json'
$start=New-Object Diagnostics.ProcessStartInfo
$start.FileName=$bun
foreach($path in @($probe,$result,$root)){if($path.Contains('"')){throw 'Unsupported quote in temporary path'}}
$start.Arguments='"'+$probe+'" "'+$result+'" "'+$root+'"'
$start.WorkingDirectory=$prefix
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.RedirectStandardOutput=$true
$start.RedirectStandardError=$true
$start.EnvironmentVariables.Clear()
foreach($key in @('SystemRoot','WINDIR','COMSPEC','PATH','TEMP','TMP')){if([Environment]::GetEnvironmentVariable($key)){$start.EnvironmentVariables[$key]=[Environment]::GetEnvironmentVariable($key)}}
foreach($key in @('HOME','USERPROFILE','APPDATA','LOCALAPPDATA','XDG_CONFIG_HOME','XDG_CACHE_HOME','PI_CODING_AGENT_DIR')){$start.EnvironmentVariables[$key]=Join-Path $root $key}
$start.EnvironmentVariables['CI']='true'
$process=[Diagnostics.Process]::Start($start)
$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
try{
    if(-not $process.WaitForExit(120000)){$process.Kill();throw 'Isolated SDK probe exceeded 120 seconds'}
    $stdout.GetAwaiter().GetResult()|Set-Content -LiteralPath (Join-Path $evidence 'stdout.log') -Encoding UTF8
    $stderr.GetAwaiter().GetResult()|Set-Content -LiteralPath (Join-Path $evidence 'stderr.log') -Encoding UTF8
    if($process.ExitCode -ne 0){throw ('SDK audit fixture failed with exit '+$process.ExitCode)}
}finally{$process.Dispose()}
$report=Get-Content -LiteralPath $result -Raw|ConvertFrom-Json
if(-not $report.AuditCompleted -or $report.Runtime -ne '1.4.4' -or $report.SdkVersion -ne '18.19.0'){throw 'Incomplete pinned SDK audit'}
@{SourceCommit=(& git -C $workspace rev-parse HEAD).Trim();BunArchiveSha256=$bunHash;SdkIntegrity=$sdk.integrity;IsolationPassed=$report.IsolationPassed;EligibleForHost=$report.EligibleForHost}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $evidence 'environment.json') -Encoding UTF8
Write-Output ('Audit completed; isolation passed: '+$report.IsolationPassed+'; production host eligible: '+$report.EligibleForHost)
# An audit can complete with an unsupported result. The report, not workflow colour,
# decides feature eligibility; no SDK or Bun dependency is added to production.
