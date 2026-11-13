param([switch]$CheckOnly, [string]$PackageDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
function Get-PackageSha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}
try {
    if (-not [Environment]::Is64BitOperatingSystem -or -not [Environment]::Is64BitProcess) { throw '请在 Windows x64 中使用 64 位 PowerShell 启动此包。' }
    $package = [IO.Path]::GetFullPath($PackageDirectory)
    $manifestPath = Join-Path $package 'distribution.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or (Get-Item -LiteralPath $manifestPath).Length -gt 1MB) { throw '发布清单缺失或无效，请重新完整解压同一版本的发布包。' }
    $manifest = Get-Content -LiteralPath $manifestPath -Encoding UTF8 -Raw | ConvertFrom-Json
    if ($manifest.SchemaVersion -ne 1 -or $manifest.Architecture -ne 'x64' -or $manifest.TargetFramework -ne 'net8.0-windows') { throw '此发布包的版本或体系结构不支持，请重新下载 Windows x64 包。' }
    $required = @('AgentLauncher.App.exe','AgentLauncher.App.dll','AgentLauncher.App.deps.json','AgentLauncher.App.runtimeconfig.json','AgentLauncher.Worker.exe','AgentLauncher.Worker.dll','AgentLauncher.Worker.deps.json','AgentLauncher.Worker.runtimeconfig.json','AgentLauncher.Core.dll','AgentLauncher.Windows.dll','LICENSE','NOTICE','Launch.ps1','Start-AgentLauncher.cmd')
    $seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $files = @($manifest.Files)
    if ($files.Count -lt $required.Count -or $files.Count -gt 128) { throw '发布文件清单不完整，请重新解压。' }
    foreach ($file in $files) {
        if ($file.Name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or -not $seen.Add([string]$file.Name) -or $file.Sha256 -notmatch '^[0-9a-fA-F]{64}$' -or $file.Length -lt 0) { throw '发布清单包含无效或重复文件，已停止启动。' }
        $path = Join-Path $package $file.Name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('配套文件缺失：' + $file.Name + '。请完整解压 App、Worker 和 DLL。') }
        if ((Get-Item -LiteralPath $path).Length -ne $file.Length -or (Get-PackageSha256 $path) -ne $file.Sha256) { throw ('文件版本不一致或已损坏：' + $file.Name + '。请重新完整解压同一版本的发布包。') }
    }
    foreach ($name in $required) { if (-not $seen.Contains($name)) { throw ('发布清单缺少配套文件：' + $name) } }
    if ($env:AGENT_LAUNCHER_TEST_ROOT -or $env:AGENT_LAUNCHER_FAKE_SAMPLE) { throw '当前终端带有验收用环境变量。请从资源管理器重新打开发布包。' }
    $dotnetDirectory = $env:DOTNET_ROOT_X64
    if (-not $dotnetDirectory) { $dotnetDirectory = $env:DOTNET_ROOT }
    if (-not $dotnetDirectory) {
        $dotnetDirectory = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64' -Name InstallLocation -ErrorAction SilentlyContinue).InstallLocation
    }
    if (-not $dotnetDirectory) { $dotnetDirectory = Join-Path $env:ProgramW6432 'dotnet' }
    $dotnet = Join-Path $dotnetDirectory 'dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw '缺少 .NET 8 Desktop Runtime x64。请安装 Microsoft 的 Windows x64 桌面运行时后重试；本程序不会自动安装。' }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $dotnet; $start.Arguments = '--list-runtimes'; $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $probe = New-Object Diagnostics.Process
    $probe.StartInfo = $start
    try {
        if (-not $probe.Start()) { throw '.NET 运行时检查未能启动。' }
        $stdout = $probe.StandardOutput.ReadToEndAsync(); $stderr = $probe.StandardError.ReadToEndAsync()
        if (-not $probe.WaitForExit(5000)) { $probe.Kill(); if (-not $probe.WaitForExit(5000)) { throw '.NET 检查进程退出未确认。' }; throw '.NET 运行时检查超时，请核对本机运行时安装。' }
        if (-not $stdout.Wait(2000) -or -not $stderr.Wait(2000)) { throw '.NET 检查输出未完成。' }
        if ($probe.ExitCode -ne 0 -or $stdout.Result -notmatch '(?m)^Microsoft\.WindowsDesktop\.App 8\.0\.\d+ ' -or $stdout.Result -notmatch '(?m)^Microsoft\.NETCore\.App 8\.0\.\d+ ') { throw '需要 .NET 8 Desktop Runtime x64；只有普通 .NET Runtime、x86 或其他主版本不足以运行此包。' }
    } finally { $probe.Dispose() }
    Write-Output ('发布文件和 .NET 8 Desktop Runtime x64 检查通过。源码提交：' + $manifest.SourceCommit)
    if (-not $CheckOnly) {
        $app = New-Object Diagnostics.ProcessStartInfo
        $app.FileName = Join-Path $package 'AgentLauncher.App.exe'; $app.WorkingDirectory = $package; $app.UseShellExecute = $false
        $app.EnvironmentVariables['DOTNET_ROOT_X64'] = [IO.Path]::GetFullPath($dotnetDirectory)
        $process = [Diagnostics.Process]::Start($app)
        if (-not $process) { throw '配置窗口未能启动，请保存此错误并联系维护者。' }
        $process.Dispose()
    }
    exit 0
} catch {
    [Console]::Error.WriteLine('启动检查未通过：' + $_.Exception.Message)
    exit 1
}
