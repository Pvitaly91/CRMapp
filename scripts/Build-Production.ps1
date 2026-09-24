[CmdletBinding()]
param([string]$ExpectedCommit)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    if ($env:OS -ne 'Windows_NT') { throw 'Windows x64 and .NET SDK from global.json are required.' }
    $sha = (& git -c "safe.directory=$repo" rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve source revision.' }
    if ($ExpectedCommit -and $sha -ne $ExpectedCommit) { throw 'HEAD differs from ExpectedCommit.' }
    $changes = & git -c "safe.directory=$repo" status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0 -or $changes) { throw 'Commit/review source changes before production packaging; dirty sources are not released.' }
    $project = 'src/CheckboxBatchPrinter/CheckboxBatchPrinter.csproj'
    [xml]$projectXml = Get-Content -LiteralPath $project
    $version = [string]$projectXml.Project.PropertyGroup.Version
    $identity = $version + '-' + $sha.Substring(0,12)
    $release = Join-Path $repo ('artifacts\production\' + $identity)
    $zip = $release + '.zip'
    if ((Test-Path -LiteralPath $release) -or (Test-Path -LiteralPath $zip)) { throw 'Release already exists. Existing releases are immutable; use a new reviewed commit.' }
    $intermediate = Join-Path $repo ('artifacts\production-build\' + $identity + '-' + [guid]::NewGuid().ToString('N'))
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-Packaging.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Packaging script tests failed.' }
    foreach ($channel in @('Development','Production')) {
        $buildRoot = Join-Path $intermediate $channel
        & dotnet build CheckboxBatchPrinter.sln -c Release --artifacts-path $buildRoot "-p:AppChannel=$channel" "-p:SourceRevisionId=$sha"
        if ($LASTEXITCODE -ne 0) { throw "$channel build failed." }
        $testRunner = Join-Path $buildRoot 'bin\CheckboxBatchPrinter.Tests\release\CheckboxBatchPrinter.Tests.exe'
        & $testRunner
        if ($LASTEXITCODE -ne 0) { throw "$channel tests failed." }
    }
    $app = Join-Path $release 'app'
    & dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishProfile=Production-FDD -p:AppChannel=Production "-p:SourceRevisionId=$sha" --artifacts-path (Join-Path $intermediate 'publish') -o $app
    if ($LASTEXITCODE -ne 0) { throw 'Production publish failed.' }
    $files = @(Get-ChildItem -LiteralPath $app -Recurse -File)
    if ($files.Count -ne 1 -or $files[0].Name -ne 'CheckboxBatchPrinter.exe') { throw 'Unexpected publish payload. Inspect dependencies; do not remove files to fake single-file output.' }
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-ProductionPackage.ps1') -Executable $files[0].FullName -ExpectedCommit $sha
    if ($LASTEXITCODE -ne 0) { throw 'Copied EXE verification failed; no ZIP created.' }
    $publishedHash = (Get-FileHash -LiteralPath $files[0].FullName -Algorithm SHA256).Hash
    # A normal Release build (no production profile or AppChannel) must not replace the release.
    & dotnet build CheckboxBatchPrinter.sln -c Release --artifacts-path (Join-Path $intermediate 'development-after-publish')
    if ($LASTEXITCODE -ne 0) { throw 'Post-publish development build failed.' }
    if ((Get-FileHash -LiteralPath $files[0].FullName -Algorithm SHA256).Hash -ne $publishedHash) { throw 'Development build changed production EXE.' }
    Write-Host 'PASS ordinary development Release build did not change production EXE SHA-256.'
    foreach ($file in @('Ensure-DesktopRuntime.ps1','RuntimeSupport.psm1','Copy-DevelopmentProfile.ps1','ProfileMigration.psm1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $release
    }
    Copy-Item -LiteralPath (Join-Path $repo 'docs\PRODUCTION_README.md') -Destination (Join-Path $release 'README.md')
    $info = [ordered]@{ version=$version; commit=$sha; channel='Production'; targetFramework='net8.0-windows'; runtime='Microsoft.WindowsDesktop.App 8.0.x x64 + Microsoft.NETCore.App 8.0.x x64'; minimumRuntime='8.0.0'; selfContained=$false; singleFile=$true; sdk=(& dotnet --version).Trim(); builtUtc=[DateTime]::UtcNow.ToString('o'); dataDirectory='%LOCALAPPDATA%\CheckboxBatchPrinter-Production' }
    $info | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $release 'build-info.json') -Encoding UTF8
    $checksums = Get-ChildItem -LiteralPath $release -Recurse -File | Sort-Object FullName | ForEach-Object {
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash + '  ' + $_.FullName.Substring($release.Length + 1).Replace('\','/')
    }
    $checksums | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding ASCII
    Compress-Archive -LiteralPath @(Get-ChildItem -LiteralPath $release | Select-Object -ExpandProperty FullName) -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "Production: $app\CheckboxBatchPrinter.exe"
    Write-Host "ZIP: $zip"
    Get-FileHash -LiteralPath $files[0].FullName -Algorithm SHA256
} finally { Pop-Location }
