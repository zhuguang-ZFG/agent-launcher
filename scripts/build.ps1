param([string]$Output = (Join-Path $PSScriptRoot '../artifacts/preview'))
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Output = [IO.Path]::GetFullPath($Output)
$staging = Join-Path ([IO.Path]::GetDirectoryName($Output)) ('.preview-build-'+[guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $Output) {
    if (-not (Test-Path -LiteralPath (Join-Path $Output 'AgentLauncher.App.exe')) -or -not (Test-Path -LiteralPath (Join-Path $Output 'AgentLauncher.Worker.exe'))) { throw 'Output already exists and is not a preview package; choose an empty destination' }
    foreach ($file in Get-ChildItem -LiteralPath $Output -File | Where-Object { $_.Extension -in @('.exe','.dll') }) {
        $probe = [IO.File]::Open($file.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
        $probe.Dispose()
    }
}
foreach ($project in @('AgentLauncher.Worker','AgentLauncher.App')) {
    & dotnet publish (Join-Path $projectRoot "src/$project/$project.csproj") -c Release -p:DefineConstants=TRACE -o $staging --nologo -m:1
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
}
foreach ($name in @('LICENSE','NOTICE')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination (Join-Path $staging $name)
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'launch-preview.ps1') -Destination (Join-Path $staging 'Launch.ps1')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start-AgentLauncher.cmd') -Destination $staging
$readme = @'
Multi-Agent Launcher - Windows x64 technical preview

Extract this entire package, then double-click Start-AgentLauncher.cmd.
Launch.ps1 -CheckOnly checks companion file hashes and .NET 8 Desktop Runtime x64.
The check does not install dependencies or change saved configuration.
Windows Terminal and the selected CLI are user-installed. Git is required for worktrees.
Keep App, Worker, DLLs, JSON files, LICENSE and NOTICE from the same package together.
distribution.json records the source commit, source state and file hashes.
To remove this portable package, close its App and wait for all managed tasks to finish,
then remove the extracted directory. User state and worktrees are retained separately.
Current acceptance limitations: docs/IMPLEMENTATION.md in the source repository.
'@
[IO.File]::WriteAllText((Join-Path $staging 'README.txt'),$readme,[Text.UTF8Encoding]::new($false))
foreach ($name in @('AgentLauncher.Core.dll','AgentLauncher.Windows.dll','AgentLauncher.Worker.dll','AgentLauncher.App.dll')) {
    $bytes = [IO.File]::ReadAllBytes((Join-Path $staging $name))
    $utf8 = [Text.Encoding]::UTF8.GetString($bytes); $utf16 = [Text.Encoding]::Unicode.GetString($bytes); $utf16Odd = [Text.Encoding]::Unicode.GetString($bytes,1,$bytes.Length-1)
    foreach ($marker in @('AGENT_LAUNCHER_TEST_ROOT','AGENT_LAUNCHER_FAKE_SAMPLE','AGENT_LAUNCHER_FAIL_AT','--ui-check','test-root.txt')) {
        if ($utf8.Contains($marker) -or $utf16.Contains($marker) -or $utf16Odd.Contains($marker)) { throw ('Test hooks present in production assembly: '+$name) }
    }
}
$sourceCommit = (& git -C $projectRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot record source commit' }
$sourceChanges = @(& git -C $projectRoot status --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'Cannot check source state' }
$files = @(Get-ChildItem -LiteralPath $staging -File | Sort-Object Name | ForEach-Object { [ordered]@{Name=$_.Name;Length=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()} })
$manifest = [ordered]@{SchemaVersion=1;Architecture='x64';TargetFramework='net8.0-windows';SourceCommit=$sourceCommit;SourceDirty=($sourceChanges.Count -gt 0);BuiltAtUtc=[DateTime]::UtcNow.ToString('o');Files=$files}
[IO.File]::WriteAllText((Join-Path $staging 'distribution.json'),($manifest | ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $staging 'Launch.ps1') -CheckOnly
if ($LASTEXITCODE -ne 0) { throw 'Package startup checks failed; staging retained for diagnosis' }
$backup = $null
if (Test-Path -LiteralPath $Output) {
    foreach ($file in Get-ChildItem -LiteralPath $Output -File | Where-Object { $_.Extension -in @('.exe','.dll') }) {
        $probe = [IO.File]::Open($file.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
        $probe.Dispose()
    }
    $backup = $Output+'.before-'+[guid]::NewGuid().ToString('N'); [IO.Directory]::Move($Output,$backup)
}
try { [IO.Directory]::Move($staging,$Output) }
catch { if ($backup -and -not (Test-Path -LiteralPath $Output)) { [IO.Directory]::Move($backup,$Output) }; throw }
if ($backup) { Write-Output "Previous package retained: $backup" }
Write-Output "Preview: $Output/AgentLauncher.App.exe"
