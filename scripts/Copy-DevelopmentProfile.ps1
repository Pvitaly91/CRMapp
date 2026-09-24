[CmdletBinding(SupportsShouldProcess=$true, ConfirmImpact='High')]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ProfileMigration.psm1') -Force
$localData = [Environment]::GetFolderPath('LocalApplicationData')
$destination = Join-Path $localData 'CheckboxBatchPrinter-Production'
if ($PSCmdlet.ShouldProcess($destination, 'Create an independent production copy of your development data (including encrypted secrets)')) {
    Copy-ProfileOnce -Source (Join-Path $localData 'CheckboxBatchPrinter') -Destination $destination -BackupParent $localData -Confirm:$false
}
