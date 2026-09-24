[CmdletBinding()]
param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$ExpectedCommit,
      [ValidateSet('Development','Production')][string]$ExpectedChannel='Production')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('cbp-package-smoke-' + [guid]::NewGuid().ToString('N'))
$appDirectory = Join-Path $root 'copied-app'
[IO.Directory]::CreateDirectory($appDirectory) | Out-Null
[IO.File]::WriteAllText((Join-Path $root 'allow-offline-verification'), 'Synthetic offline verification only')
$copiedExe = Join-Path $appDirectory 'CheckboxBatchPrinter.exe'
Copy-Item -LiteralPath $Executable -Destination $copiedExe
for ($pass = 0; $pass -lt 2; $pass++) {
    # Direct apphost launch, not dotnet run. Different CWD, and only one file copied.
    $process = Start-Process -FilePath $copiedExe -ArgumentList @('--verify-package', ('"' + $root + '"')) -WorkingDirectory $env:WINDIR -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(45000)) { $process.Kill(); throw 'Own offline package verification timed out.' }
    $process.Refresh()
    if ($process.ExitCode -ne 0) { throw "Offline package verification failed: $($process.ExitCode); $root" }
    $result = Get-Content -LiteralPath (Join-Path $root 'verification.json') -Raw | ConvertFrom-Json
    if (-not $result.Success -or $result.Channel -ne $ExpectedChannel -or $result.Commit -ne $ExpectedCommit -or $result.Architecture -ne 'X64' -or $result.Restarted -ne ($pass -eq 1)) { throw "Wrong package identity or persistence: $root" }
    Write-Host "PASS copied EXE, channel=$($result.Channel), restart=$($result.Restarted), runtime=$($result.Framework)"
}
Write-Host "Offline XAML renders and report: $root"
