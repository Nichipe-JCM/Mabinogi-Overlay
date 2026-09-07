$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputPath = Join-Path $projectRoot 'artifacts/test-lab'
dotnet build (Join-Path $projectRoot 'tools/TestOverlay.TestLab/TestOverlay.TestLab.csproj') -c Release -o $outputPath --nologo
if ($LASTEXITCODE -ne 0) { throw 'Test lab build failed.' }
Start-Process -FilePath (Join-Path $outputPath 'TestOverlay.TestLab.exe') -WindowStyle Normal
