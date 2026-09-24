[CmdletBinding()]
param(
    [string]$OutputDirectory = 'artifacts\win-x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') {
    throw 'Windows is required for WPF and the Windows/STA tests.'
}

Push-Location $PSScriptRoot
try {
    $outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
        [IO.Path]::GetFullPath($OutputDirectory)
    } else {
        [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory))
    }
    if ((Test-Path -LiteralPath $outputPath) -and
        (@(Get-ChildItem -LiteralPath $outputPath -Force).Count -gt 0)) {
        throw 'Output directory must be empty. Choose a new directory; existing builds are never overwritten.'
    }

    & dotnet build CheckboxBatchPrinter.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

    # This repository uses an executable test runner, not dotnet test.
    # Includes compiled WPF/STA tests; no real API credentials or printer needed.
    & dotnet run --project tests/CheckboxBatchPrinter.Tests/CheckboxBatchPrinter.Tests.csproj -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed; publish cancelled.' }

    & dotnet publish src/CheckboxBatchPrinter/CheckboxBatchPrinter.csproj -c Release -r win-x64 --self-contained true -o $outputPath
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
    if (-not (Test-Path -LiteralPath (Join-Path $outputPath 'CheckboxBatchPrinter.exe'))) {
        throw 'Published executable is missing.'
    }

    Write-Host "Ready: $outputPath\CheckboxBatchPrinter.exe"
    Write-Host 'Copy the entire output directory, not only the EXE.'
}
finally {
    Pop-Location
}
