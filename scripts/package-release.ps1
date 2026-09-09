[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [switch]$WriteUpdateMetadata,
    [switch]$ValidationOnly
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src/TestOverlay.App/TestOverlay.App.csproj')
$releaseVersion = [string]$project.Project.PropertyGroup.Version
$notesDirectory = Join-Path $projectRoot ('docs/updates/' + $releaseVersion)
if (-not $ValidationOnly) {
    foreach ($language in @('ko-KR', 'en-US')) {
        if (-not (Test-Path -LiteralPath (Join-Path $notesDirectory ($language + '.md')))) { throw "Missing in-app notes: $language. Copy and review the unreleased notes for $releaseVersion first." }
    }
    if ($WriteUpdateMetadata) {
        git -C $projectRoot rev-parse --quiet --verify ('refs/tags/' + $releaseVersion) *> $null
        if ($LASTEXITCODE -eq 0) { throw 'Do not change metadata for an existing release tag.' }
    }
}
$packageRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $packageRoot) { throw 'Choose a new, empty output directory; existing packages are not overwritten.' }
New-Item -ItemType Directory -Path $packageRoot | Out-Null
$buildRoot = Join-Path $projectRoot ('artifacts/package-build-' + [Guid]::NewGuid().ToString('N'))
dotnet publish (Join-Path $projectRoot 'src/TestOverlay.App/TestOverlay.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:PublishTrimmed=false "-p:BaseOutputPath=$buildRoot/app/" -o $packageRoot --nologo
if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }
dotnet publish (Join-Path $projectRoot 'src/TestOverlay.Updater/TestOverlay.Updater.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false "-p:BaseOutputPath=$buildRoot/helper/" -o (Join-Path $packageRoot 'Updater') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Updater publish failed.' }
# Only known debug-symbol files inside this newly-created package directory are removed.
Get-ChildItem -LiteralPath $packageRoot -File -Recurse -Filter '*.pdb' | ForEach-Object {
    if (-not $_.FullName.StartsWith($packageRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Debug file escaped package directory.' }
    Remove-Item -LiteralPath $_.FullName
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'),(Join-Path $projectRoot 'LICENSE') -Destination $packageRoot
$docsRoot = Join-Path $projectRoot 'docs'
foreach ($file in Get-ChildItem -LiteralPath $docsRoot -Recurse -File) {
    $relative = [IO.Path]::GetRelativePath($docsRoot, $file.FullName)
    if ($relative.StartsWith('updates' + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { continue }
    $destination = Join-Path (Join-Path $packageRoot 'docs') $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}
if (-not $ValidationOnly) {
    & (Join-Path $PSScriptRoot 'sign-release.ps1') -FilePath (Join-Path $packageRoot 'Mabinogi Overlay.exe') | Out-Host
    & (Join-Path $PSScriptRoot 'sign-release.ps1') -FilePath (Join-Path $packageRoot 'Updater/MabinogiOverlay.Updater.exe') | Out-Host
}
$files = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($packageRoot, $_.FullName).Replace('\', '/') } | Sort-Object)
$fileList = ConvertTo-Json -InputObject $files
if ($ValidationOnly) {
    [IO.File]::WriteAllText($packageRoot + '.files.json', $fileList, [Text.UTF8Encoding]::new($false))
} elseif ($WriteUpdateMetadata) {
    [IO.File]::WriteAllText((Join-Path $notesDirectory 'files.json'), $fileList, [Text.UTF8Encoding]::new($false))
    Write-Host 'Commit the generated file list and localized notes with the release before tagging. The file list is not included in the ZIP.'
} else {
    $expected = @(Get-Content -LiteralPath (Join-Path $notesDirectory 'files.json') -Raw | ConvertFrom-Json)
    if (Compare-Object $expected $files) { throw 'Published files differ from the committed product file list.' }
}
$zip = Join-Path $packageRoot ("MabinogiOverlay-$releaseVersion-win-x64-portable.zip")
$inputs = @(Get-ChildItem -LiteralPath $packageRoot | Select-Object -ExpandProperty FullName)
Compress-Archive -LiteralPath $inputs -DestinationPath $zip -CompressionLevel Optimal
Write-Host $zip
