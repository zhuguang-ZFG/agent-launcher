param([string]$Output = (Join-Path $PSScriptRoot '../artifacts/preview'))
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Output = [IO.Path]::GetFullPath($Output)
foreach ($project in @('AgentLauncher.Worker','AgentLauncher.App')) {
    & dotnet publish (Join-Path $projectRoot "src/$project/$project.csproj") -c Release -o $Output --nologo -m:1
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
}
Write-Output "Preview: $Output/AgentLauncher.App.exe"
