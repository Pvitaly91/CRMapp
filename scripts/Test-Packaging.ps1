Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'RuntimeSupport.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'ProfileMigration.psm1') -Force
function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
$correct = @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App') | ForEach-Object { [pscustomobject]@{ Name=$_; Version='8.0.31'; Architecture='x64' } }
Assert (Test-RuntimeInventory $correct) 'Compatible x64 runtimes rejected'
Assert (-not (Test-RuntimeInventory @())) 'Absent runtime accepted'
$x86 = $correct | ForEach-Object { [pscustomobject]@{ Name=$_.Name; Version=$_.Version; Architecture='x86' } }
Assert (-not (Test-RuntimeInventory $x86)) 'x86-only runtime accepted'
Assert (-not (Test-RuntimeInventory @($correct[0]))) 'Core without Desktop accepted'
$wrongMajor = $correct | ForEach-Object { [pscustomobject]@{ Name=$_.Name; Version='9.0.0'; Architecture='x64' } }
Assert (-not (Test-RuntimeInventory $wrongMajor)) 'Different major accepted'
$preview = $correct | ForEach-Object { [pscustomobject]@{ Name=$_.Name; Version='8.0.0-preview.1'; Architecture='x64' } }
Assert (-not (Test-RuntimeInventory $preview)) 'Preview accepted'
Write-Host 'PASS runtime: available / absent / x86-only / Core-only / other-major / preview'

# Synthetic DPAPI data only. No source or destination points into an actual app profile.
$root = Join-Path ([IO.Path]::GetTempPath()) ('cbp-migration-test-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$source = Join-Path $root 'development'
$destination = Join-Path $root 'production'
[IO.Directory]::CreateDirectory($source) | Out-Null
[IO.File]::WriteAllText((Join-Path $source 'settings.json'), '{"PrinterName":"synthetic-printer"}')
[IO.File]::WriteAllText((Join-Path $source 'printed-receipts.json'), '[]')
Add-Type -AssemblyName System.Security
$bytes = [Text.Encoding]::UTF8.GetBytes('synthetic-not-a-real-password')
$encrypted = [Security.Cryptography.ProtectedData]::Protect($bytes,$null,'CurrentUser')
[Array]::Clear($bytes,0,$bytes.Length)
[IO.File]::WriteAllBytes((Join-Path $source 'credential.bin'), $encrypted)
$before = (Get-FileHash -LiteralPath (Join-Path $source 'credential.bin')).Hash
Copy-ProfileOnce -Source $source -Destination $destination -BackupParent $root -WhatIf
Assert (-not (Test-Path -LiteralPath $destination)) 'WhatIf wrote production'
Copy-ProfileOnce -Source $source -Destination $destination -BackupParent $root -Confirm:$false
Assert ((Get-FileHash -LiteralPath (Join-Path $destination 'credential.bin')).Hash -eq $before) 'Encrypted credentials changed'
Assert ((Get-FileHash -LiteralPath (Join-Path $source 'credential.bin')).Hash -eq $before) 'Source changed'
Assert ((Get-Content -LiteralPath (Join-Path $destination 'settings.json') -Raw) -match 'synthetic-printer') 'Printer setting lost'
Assert (Test-Path -LiteralPath (Join-Path $destination 'printed-receipts.json')) 'History lost'
Assert ((Get-Acl -LiteralPath $destination).AreAccessRulesProtected) 'Destination ACL not protected'
$backup = @(Get-ChildItem -LiteralPath $root -Directory -Filter 'CheckboxBatchPrinter-profile-backup-*')
Assert ($backup.Count -eq 1 -and (Get-Acl -LiteralPath $backup[0].FullName).AreAccessRulesProtected) 'Protected backup missing'
$refused = $false
try { Copy-ProfileOnce -Source $source -Destination $destination -BackupParent $root -Confirm:$false } catch { $refused = $true }
Assert $refused 'Existing production overwritten'
[IO.File]::WriteAllBytes((Join-Path $source 'credential.bin'), [byte[]](1,2,3))
$refused = $false
try { Copy-ProfileOnce -Source $source -Destination ($destination + '-invalid') -BackupParent $root -Confirm:$false } catch { $refused = $true }
Assert ($refused -and -not (Test-Path -LiteralPath ($destination + '-invalid'))) 'Invalid DPAPI accepted'
Write-Host "PASS migration: confirmation/WhatIf, protected backup, unchanged source, encrypted copy, printer/history, existing destination and invalid DPAPI refusal. Synthetic fixtures: $root"
