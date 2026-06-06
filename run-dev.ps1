$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $repoRoot "src\TestOverlay.App\TestOverlay.App.csproj"

Set-Location $repoRoot
dotnet run --project $projectPath
