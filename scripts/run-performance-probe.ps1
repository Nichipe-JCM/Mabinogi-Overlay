$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputPath = Join-Path $projectRoot 'artifacts/performance-probe'
dotnet build (Join-Path $projectRoot 'tools/TestOverlay.PerformanceProbe/TestOverlay.PerformanceProbe.csproj') -c Release -o $outputPath --nologo
if ($LASTEXITCODE -ne 0) { throw 'Performance probe build failed.' }
$previousTiering = $env:DOTNET_TieredCompilation
try {
    $env:DOTNET_TieredCompilation = '0'
    $reportPath = Join-Path $projectRoot ('artifacts/performance-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
    dotnet (Join-Path $outputPath 'TestOverlay.PerformanceProbe.dll') $reportPath
    if ($LASTEXITCODE -ne 0) { throw 'Performance probe failed.' }
    Write-Host $reportPath
}
finally { $env:DOTNET_TieredCompilation = $previousTiering }
