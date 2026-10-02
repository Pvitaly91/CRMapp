#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$updater = Join-Path $PSScriptRoot 'Update-Production.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('cbp-update-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$updateTestState = @{ AppRunning = $false }
# Only this synthetic test runspace overrides process discovery. The updater has no bypass switch.
function Get-Process { param($Name, $ErrorAction) if ($updateTestState.AppRunning) { [pscustomobject]@{ Name = 'CheckboxBatchPrinter' } } }
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Fixture([string]$Name) {
    $root = Join-Path $testRoot $Name
    $release = Join-Path $root 'release'; $install = Join-Path $root 'install'; $profile = Join-Path $root 'profile'
    foreach ($folder in @((Join-Path $release 'app'), $install, $profile)) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }
    $target = Join-Path $install 'CheckboxBatchPrinter.exe'
    [IO.File]::WriteAllText($target, 'synthetic-old-executable-never-launched')
    [IO.File]::WriteAllText((Join-Path $release 'app\CheckboxBatchPrinter.exe'), 'synthetic-new-executable-never-launched')
    [IO.File]::WriteAllText((Join-Path $profile 'printed-receipts.json'), '[{"syntheticReceipt":"printed"}]')
    [IO.File]::WriteAllBytes((Join-Path $profile 'synthetic-secret.dpapi'), [byte[]](10, 20, 30))
    $hidden = Join-Path $profile 'hidden-cache'
    New-Item -ItemType Directory -Path $hidden | Out-Null
    [IO.File]::SetAttributes($hidden, [IO.FileAttributes]::Directory -bor [IO.FileAttributes]::Hidden)
    [IO.File]::WriteAllText((Join-Path $hidden 'synthetic-cache.json'), '{"synthetic":"cache"}')
    [IO.File]::WriteAllText((Join-Path $release 'build-info.json'),
        (@{ version = '1.0.20'; commit = ('a' * 40); channel = 'Production'; builtUtc = '2026-10-01T00:00:00Z' } | ConvertTo-Json))
    Set-Checksums $release
    return @{ ReleaseDirectory = $release; TargetExecutable = $target; ProfileDirectory = $profile }
}
function Set-Checksums([string]$Release) {
    $lines = foreach ($relative in @('app/CheckboxBatchPrinter.exe', 'build-info.json')) {
        (Get-FileHash -LiteralPath (Join-Path $Release $relative) -Algorithm SHA256).Hash + '  ' + $relative
    }
    [IO.File]::WriteAllLines((Join-Path $Release 'SHA256SUMS.txt'), [string[]]$lines)
}
function Refused($Parameters, [string]$Message) {
    $hadExecutable = Test-Path -LiteralPath $Parameters.TargetExecutable
    $old = if ($hadExecutable) { (Get-FileHash -LiteralPath $Parameters.TargetExecutable).Hash } else { $null }
    $failed = $false
    try { & $updater @Parameters } catch { $failed = $true }
    Assert $failed $Message
    if ($hadExecutable) { Assert ((Get-FileHash -LiteralPath $Parameters.TargetExecutable).Hash -eq $old) 'Refusal changed installed EXE.' }
    else { Assert (!(Test-Path -LiteralPath $Parameters.TargetExecutable)) 'Refusal installed missing EXE.' }
    Assert (!(Test-Path -LiteralPath (Join-Path (Split-Path $Parameters.TargetExecutable) 'CheckboxBatchPrinter-backups'))) 'Refusal created backup files.'
}
function Remove-SyntheticExecutable($Fixture) {
    $path = (Resolve-Path -LiteralPath $Fixture.TargetExecutable).Path
    if (!$path.StartsWith($testRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected synthetic fixture path.' }
    [IO.File]::Delete($path)
}

$f = Fixture 'whatif'
& $updater @f -WhatIf
Assert ((Get-Content -LiteralPath $f.TargetExecutable -Raw) -eq 'synthetic-old-executable-never-launched') 'WhatIf replaced EXE.'
Assert (!(Test-Path -LiteralPath (Join-Path (Split-Path $f.TargetExecutable) 'CheckboxBatchPrinter-backups'))) 'WhatIf wrote backups.'
Write-Host 'PASS update: WhatIf leaves EXE/profile untouched.'

$f = Fixture 'whatif-running'; $updateTestState.AppRunning = $true
try { & $updater @f -WhatIf } finally { $updateTestState.AppRunning = $false }
Assert ((Get-Content -LiteralPath $f.TargetExecutable -Raw) -eq 'synthetic-old-executable-never-launched') 'Running-app WhatIf replaced EXE.'
Assert (!(Test-Path -LiteralPath (Join-Path (Split-Path $f.TargetExecutable) 'CheckboxBatchPrinter-backups'))) 'Running-app WhatIf wrote backups.'
Write-Host 'PASS update: preview is available while the app is running, without writes.'

$f = Fixture 'missing-exe'; Remove-SyntheticExecutable $f
$profileBefore = @{}
foreach ($file in Get-ChildItem -LiteralPath $f.ProfileDirectory -Recurse -Force -File) { $profileBefore[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName).Hash }
& $updater @f -WhatIf
Assert (!(Test-Path -LiteralPath $f.TargetExecutable)) 'WhatIf restored deleted EXE.'
Assert (!(Test-Path -LiteralPath (Join-Path (Split-Path $f.TargetExecutable) 'CheckboxBatchPrinter-backups'))) 'Missing EXE WhatIf wrote backups.'
& $updater @f
Assert ((Get-Content -LiteralPath $f.TargetExecutable -Raw) -eq 'synthetic-new-executable-never-launched') 'Deleted EXE not restored.'
foreach ($file in $profileBefore.Keys) { Assert ((Get-FileHash -LiteralPath $file).Hash -eq $profileBefore[$file]) 'Restoration changed profile bytes.' }
$restoredBackup = @(Get-ChildItem -LiteralPath (Join-Path (Split-Path $f.TargetExecutable) 'CheckboxBatchPrinter-backups') -Directory)
Assert ($restoredBackup.Count -eq 1) 'Restoration did not back up profile.'
Assert (!(Test-Path -LiteralPath (Join-Path $restoredBackup[0].FullName 'CheckboxBatchPrinter-previous.exe'))) 'Restoration fabricated previous EXE backup.'
Assert (@(Get-ChildItem -LiteralPath (Join-Path $restoredBackup[0].FullName 'profile') -Recurse -Force -File).Count -eq $profileBefore.Count) 'Restoration profile backup incomplete.'
Write-Host 'PASS update: deleted EXE restores from verified package with unchanged profile; WhatIf performs no writes.'

$f = Fixture 'missing-running'; Remove-SyntheticExecutable $f; $updateTestState.AppRunning = $true
try { Refused $f 'Running app with deleted EXE accepted.' } finally { $updateTestState.AppRunning = $false }
$f = Fixture 'missing-tampered'; Remove-SyntheticExecutable $f
[IO.File]::AppendAllText((Join-Path $f.ReleaseDirectory 'app\CheckboxBatchPrinter.exe'), 'tampered')
Refused $f 'Tampered package restored deleted EXE.'
Write-Host 'PASS update: deleted EXE restoration still blocks a running app and invalid package.'

$f = Fixture 'running'; $updateTestState.AppRunning = $true
try { Refused $f 'Running app accepted.' } finally { $updateTestState.AppRunning = $false }
Write-Host 'PASS update: running app blocks replacement.'

$f = Fixture 'tampered-exe'
[IO.File]::AppendAllText((Join-Path $f.ReleaseDirectory 'app\CheckboxBatchPrinter.exe'), 'tampered')
Refused $f 'Tampered EXE accepted.'
$f = Fixture 'tampered-metadata'
[IO.File]::AppendAllText((Join-Path $f.ReleaseDirectory 'build-info.json'), ' ')
Refused $f 'Tampered metadata accepted.'
Write-Host 'PASS update: EXE and metadata checksum mismatches block replacement.'

$f = Fixture 'development'
$metadata = Get-Content -LiteralPath (Join-Path $f.ReleaseDirectory 'build-info.json') -Raw | ConvertFrom-Json
$metadata.channel = 'Development'
[IO.File]::WriteAllText((Join-Path $f.ReleaseDirectory 'build-info.json'), ($metadata | ConvertTo-Json))
Set-Checksums $f.ReleaseDirectory
Refused $f 'Development package accepted.'
Write-Host 'PASS update: Development channel rejected.'

$f = Fixture 'replace'
$old = (Get-FileHash -LiteralPath $f.TargetExecutable).Hash
$before = @{}
foreach ($file in Get-ChildItem -LiteralPath $f.ProfileDirectory -Recurse -Force -File) { $before[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName).Hash }
& $updater @f
Assert ((Get-Content -LiteralPath $f.TargetExecutable -Raw) -eq 'synthetic-new-executable-never-launched') 'New EXE not installed.'
foreach ($file in $before.Keys) { Assert ((Get-FileHash -LiteralPath $file).Hash -eq $before[$file]) 'Profile bytes changed.' }
$backups = @(Get-ChildItem -LiteralPath (Join-Path (Split-Path $f.TargetExecutable) 'CheckboxBatchPrinter-backups') -Directory)
Assert ($backups.Count -eq 1) 'Expected exactly one backup.'
$backup = $backups[0].FullName
Assert ((Get-Acl -LiteralPath $backup).AreAccessRulesProtected) 'Backup ACL not protected.'
Assert ((Get-FileHash -LiteralPath (Join-Path $backup 'CheckboxBatchPrinter-previous.exe')).Hash -eq $old) 'Previous EXE backup changed.'
Assert (@(Get-ChildItem -LiteralPath (Join-Path $backup 'profile') -Recurse -Force -File).Count -eq $before.Count) 'Hidden profile files missing from backup.'
Write-Host 'PASS update: atomic replacement, protected EXE/profile backup and unchanged profile (including hidden files).'

$updateTestState.AppRunning = $true
try { & $updater @f } finally { $updateTestState.AppRunning = $false }
Assert (@(Get-ChildItem -LiteralPath (Split-Path $backup) -Directory).Count -eq 1) 'Same release created duplicate backups.'
Write-Host 'PASS update: identical installed release is a no-op, even when app is running.'

$f = Fixture 'downgrade'
# Use a local versioned PE only as input bytes; never launch it.
Copy-Item -LiteralPath (Join-Path $PSHOME 'powershell.exe') -Destination $f.TargetExecutable
Refused $f 'Downgrade accepted.'
Write-Host 'PASS update: downgrading an installed newer version is blocked.'

$autoRepo = Join-Path $testRoot 'auto-repo'
$autoScripts = Join-Path $autoRepo 'scripts'; $autoReleases = Join-Path $autoRepo 'artifacts\production'
New-Item -ItemType Directory -Path $autoScripts -Force | Out-Null
New-Item -ItemType Directory -Path $autoReleases -Force | Out-Null
Copy-Item -LiteralPath $updater -Destination $autoScripts
foreach ($name in @('older', 'newest', 'dev')) {
    $package = Fixture ('auto-' + $name)
    $info = Get-Content -LiteralPath (Join-Path $package.ReleaseDirectory 'build-info.json') -Raw | ConvertFrom-Json
    if ($name -eq 'newest') { $info.version = '1.0.21'; $info.builtUtc = '2026-10-02T00:00:00Z' }
    if ($name -eq 'dev') { $info.version = '99.0.0'; $info.channel = 'Development' }
    [IO.File]::WriteAllText((Join-Path $package.ReleaseDirectory 'build-info.json'), ($info | ConvertTo-Json))
    [IO.File]::WriteAllText((Join-Path $package.ReleaseDirectory 'app\CheckboxBatchPrinter.exe'), ('synthetic-' + $name))
    Set-Checksums $package.ReleaseDirectory
    Copy-Item -LiteralPath $package.ReleaseDirectory -Destination (Join-Path $autoReleases $name) -Recurse
}
$f = Fixture 'auto-target'
& (Join-Path $autoScripts 'Update-Production.ps1') -TargetExecutable $f.TargetExecutable -ProfileDirectory $f.ProfileDirectory
Assert ((Get-Content -LiteralPath $f.TargetExecutable -Raw) -eq 'synthetic-newest') 'Latest Production version not chosen, or Development selected.'
Write-Host 'PASS update: default invocation selects the newest Production package, excluding Development.'

# Reproduce the stale no-op: the latest packaged EXE is already installed, but the source is newer.
$autoProject = Join-Path $autoRepo 'src\CheckboxBatchPrinter\CheckboxBatchPrinter.csproj'
New-Item -ItemType Directory -Path (Split-Path $autoProject) -Force | Out-Null
[IO.File]::WriteAllText($autoProject, '<Project><PropertyGroup><Version>1.0.22</Version></PropertyGroup></Project>')
$oldUpdater = $updater
$updater = Join-Path $autoScripts 'Update-Production.ps1'
$failure = $null
try { & $updater -TargetExecutable $f.TargetExecutable -ProfileDirectory $f.ProfileDirectory }
catch { $failure = $_.Exception.Message }
Assert ($null -ne $failure -and $failure -match '1\.0\.22' -and $failure -match '\-Build') 'Newer source was silently reported as already installed.'
Assert ((Get-Content -LiteralPath $f.TargetExecutable -Raw) -eq 'synthetic-newest') 'Stale package check changed installed EXE.'
Assert (@(Get-ChildItem -LiteralPath (Join-Path (Split-Path $f.TargetExecutable) 'CheckboxBatchPrinter-backups') -Directory).Count -eq 1) 'Stale package check created another backup.'
Write-Host 'PASS update: newer source blocks a misleading no-op before any installation writes.'

[IO.File]::WriteAllText($autoProject, '<Project><PropertyGroup><Version>1.0.21</Version></PropertyGroup></Project>')
& $updater -TargetExecutable $f.TargetExecutable -ProfileDirectory $f.ProfileDirectory
Write-Host 'PASS update: matching source and package versions still allow the installed-release no-op.'

# An explicit release remains independent of the checked-out source version.
[IO.File]::WriteAllText($autoProject, '<Project><PropertyGroup><Version>1.0.22</Version></PropertyGroup></Project>')
$explicit = Fixture 'explicit-release'
& $updater @explicit
Assert ((Get-Content -LiteralPath $explicit.TargetExecutable -Raw) -eq 'synthetic-new-executable-never-launched') 'Explicit release was incorrectly blocked by newer source.'
$updater = $oldUpdater
Write-Host 'PASS update: an explicitly selected release remains independent of source version.'

$f = Fixture 'packaged-script'
Copy-Item -LiteralPath $updater -Destination $f.ReleaseDirectory
& (Join-Path $f.ReleaseDirectory 'Update-Production.ps1') -TargetExecutable $f.TargetExecutable -ProfileDirectory $f.ProfileDirectory
Assert ((Get-Content -LiteralPath $f.TargetExecutable -Raw) -eq 'synthetic-new-executable-never-launched') 'Unpacked release invocation failed.'
Write-Host 'PASS update: script works directly from an unpacked release without a repository.'
Write-Host "Synthetic fixtures retained: $testRoot"
