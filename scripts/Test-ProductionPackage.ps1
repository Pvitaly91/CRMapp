[CmdletBinding()]
param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$ExpectedCommit,
      [ValidateSet('Development','Production')][string]$ExpectedChannel='Production',
      [string]$DevelopmentExecutable)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('cbp-package-smoke-' + [guid]::NewGuid().ToString('N'))
$appDirectory = Join-Path $root 'copied-app'
[IO.Directory]::CreateDirectory($appDirectory) | Out-Null
[IO.File]::WriteAllText((Join-Path $root 'allow-offline-verification'), 'Synthetic offline verification only')
$copiedExe = Join-Path $appDirectory 'CheckboxBatchPrinter.exe'
Copy-Item -LiteralPath $Executable -Destination $copiedExe
function Invoke-OfflineCheck([string]$Path, [string]$Channel, [bool]$Restarted) {
    # Direct apphost launch, not dotnet run. Different CWD, and only one file copied.
    $process = Start-Process -FilePath $Path -ArgumentList @('--verify-package', ('"' + $root + '"')) -WorkingDirectory $env:WINDIR -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(45000)) { $process.Kill(); throw 'Own offline package verification timed out.' }
    $process.Refresh()
    if ($process.ExitCode -ne 0) { throw "Offline package verification failed: $($process.ExitCode); $root" }
    $result = Get-Content -LiteralPath (Join-Path $root 'verification.json') -Raw | ConvertFrom-Json
    if (-not $result.Success -or $result.Channel -ne $Channel -or $result.Commit -ne $ExpectedCommit -or $result.Architecture -ne 'X64' -or $result.Restarted -ne $Restarted) { throw "Wrong package identity or persistence: $root" }
    Write-Host "PASS copied EXE, channel=$($result.Channel), restart=$($result.Restarted), runtime=$($result.Framework)"
}
Invoke-OfflineCheck $copiedExe $ExpectedChannel $false
Invoke-OfflineCheck $copiedExe $ExpectedChannel $true
if ($DevelopmentExecutable) {
    $prodSettings = Join-Path $root 'CheckboxBatchPrinter-Production\settings.json'
    $prodHash = (Get-FileHash -LiteralPath $prodSettings).Hash
    Invoke-OfflineCheck $DevelopmentExecutable 'Development' $false
    Invoke-OfflineCheck $DevelopmentExecutable 'Development' $true
    if ((Get-FileHash -LiteralPath $prodSettings).Hash -ne $prodHash) { throw 'Development launch changed production data.' }
    $devSettings = Join-Path $root 'CheckboxBatchPrinter\settings.json'
    $devHash = (Get-FileHash -LiteralPath $devSettings).Hash
    Invoke-OfflineCheck $copiedExe $ExpectedChannel $true
    if ((Get-FileHash -LiteralPath $devSettings).Hash -ne $devHash) { throw 'Production launch changed development data.' }
    Write-Host 'PASS two real app processes keep synthetic channel profiles independent.'
}
Write-Host "Offline XAML renders and report: $root"
