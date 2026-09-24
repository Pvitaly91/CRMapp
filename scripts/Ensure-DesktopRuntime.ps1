[CmdletBinding(SupportsShouldProcess=$true, ConfirmImpact='High')]
param([switch]$Install)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'RuntimeSupport.psm1') -Force
$inventory = @(Get-DesktopRuntimeInventory)
if (Test-RuntimeInventory $inventory) {
    $inventory | Format-Table Name,Version,Architecture
    Write-Host 'Compatible .NET Desktop Runtime 8 x64 is installed. No changes made.'
    exit 0
}
if (-not $Install) { throw 'Install Microsoft .NET Desktop Runtime 8 x64, or run this script with -Install. Other architectures/major versions are not sufficient.' }
if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    throw 'WinGet unavailable. Use the signed Microsoft x64 Desktop Runtime 8 installer from https://dotnet.microsoft.com/download/dotnet/8.0 . Do not install SDK or Hosting Bundle.'
}
& winget show --id Microsoft.DotNet.DesktopRuntime.8 --exact --source winget
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify the official WinGet package.' }
if (-not $PSCmdlet.ShouldProcess('Microsoft.DotNet.DesktopRuntime.8 x64', 'Install stable Desktop Runtime (Windows may request UAC approval)')) { return }
& winget install --id Microsoft.DotNet.DesktopRuntime.8 --exact --source winget --architecture x64 --disable-interactivity
if ($LASTEXITCODE -ne 0) { throw 'Runtime installation failed or requires user action. No automatic restart is performed.' }
if (-not (Test-RuntimeInventory @(Get-DesktopRuntimeInventory))) { throw 'A compatible x64 Desktop Runtime is still not detected.' }
Write-Host 'Compatible Desktop Runtime verified.'
