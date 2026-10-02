#requires -Version 5.1
<#
.SYNOPSIS
Updates the installed CRMapp from a verified production package and backs up its profile.
.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Update-Production.ps1
.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Update-Production.ps1 -Build
.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Update-Production.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ReleaseDirectory,
    [string]$TargetExecutable = 'D:\Portable Soft\CheckboxBatchPrinter.exe',
    [string]$ProfileDirectory = (Join-Path $env:LOCALAPPDATA 'CheckboxBatchPrinter-Production'),
    [switch]$Build
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-AppClosed {
    if (@(Get-Process -Name CheckboxBatchPrinter -ErrorAction SilentlyContinue).Count) {
        throw 'Закрийте CRMapp через значок у треї -> Вийти, потім повторіть команду.'
    }
}

function Assert-NoReparse([string]$Path) {
    for ($entry = Get-Item -LiteralPath $Path -Force; $null -ne $entry;) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Оновлення через символічне посилання або junction не підтримується: $Path"
        }
        if ($entry -is [IO.DirectoryInfo]) { $entry = $entry.Parent } else { $entry = $entry.Directory }
    }
}

function Get-Hash([string]$Path) {
    # Direct .NET hashing also works when Windows PowerShell 5.1 inherits -WhatIf.
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

function Get-Snapshot([string]$Directory) {
    $snapshot = @{}
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File -Force) {
        $snapshot[$file.FullName.Substring($Directory.Length + 1)] = Get-Hash $file.FullName
    }
    return $snapshot
}

function Assert-Snapshot($Expected, $Actual) {
    if ($Expected.Count -ne $Actual.Count) { throw 'Кількість файлів профілю змінилася. Оновлення зупинено; резервну копію збережено.' }
    foreach ($key in $Expected.Keys) {
        if (!$Actual.ContainsKey($key) -or $Expected[$key] -ne $Actual[$key]) {
            throw 'Вміст профілю змінився. Оновлення зупинено; резервну копію збережено.'
        }
    }
}

function New-PrivateBackup([string]$Path) {
    New-Item -ItemType Directory -Path $Path | Out-Null
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl.SetOwner($sid)
    foreach ($identity in @($sid, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Test-ReleaseFile([string]$Directory, [string]$RelativeName) {
    $pattern = '^([0-9A-Fa-f]{64})  ' + [regex]::Escape($RelativeName) + '$'
    $entries = @(Get-Content -LiteralPath (Join-Path $Directory 'SHA256SUMS.txt') | Where-Object { $_ -match $pattern })
    if ($entries.Count -ne 1) { throw "У SHA256SUMS.txt немає однозначної контрольної суми: $RelativeName" }
    $expected = [regex]::Match($entries[0], $pattern).Groups[1].Value
    $file = Join-Path $Directory $RelativeName
    Assert-NoReparse $file
    $actual = Get-Hash $file
    if ($actual -ne $expected) { throw "Контрольна сума не збігається: $RelativeName. EXE не замінено." }
    return $actual
}

$repo = Split-Path $PSScriptRoot -Parent
$repositoryDefault = $false
if ($Build) {
    if ($ReleaseDirectory) { throw 'Використовуйте або -Build, або -ReleaseDirectory.' }
    $builder = Join-Path $PSScriptRoot 'Build-Production.ps1'
    if (!(Test-Path -LiteralPath $builder -PathType Leaf)) { throw 'Для -Build запускайте скрипт із папки scripts у репозиторії.' }
    $commit = (& git -C $repo -c "safe.directory=$repo" rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Не вдалося визначити Git HEAD.' }
    $changes = & git -C $repo -c "safe.directory=$repo" status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0 -or $changes) { throw 'Перед -Build закомітьте перевірені зміни. Production збирається лише з чистого Git-стану.' }
    [xml]$project = Get-Content -LiteralPath (Join-Path $repo 'src\CheckboxBatchPrinter\CheckboxBatchPrinter.csproj')
    $ReleaseDirectory = Join-Path $repo ('artifacts\production\' + $project.Project.PropertyGroup.Version + '-' + $commit.Substring(0, 12))
    if (!(Test-Path -LiteralPath (Join-Path $ReleaseDirectory 'SHA256SUMS.txt') -PathType Leaf)) {
        Assert-AppClosed
        if (!$PSCmdlet.ShouldProcess($repo, 'Зібрати й перевірити production-пакет із поточного Git HEAD')) { return }
        & powershell -NoProfile -ExecutionPolicy Bypass -File $builder -ExpectedCommit $commit
        if ($LASTEXITCODE -ne 0) { throw 'Production-збірка не пройшла перевірки. Встановлений EXE не змінено.' }
    }
}
if (!$ReleaseDirectory) {
    # The same script also works inside an unpacked release, without the source repository.
    if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'build-info.json') -PathType Leaf) {
        $ReleaseDirectory = $PSScriptRoot
    } else {
        $repositoryDefault = $true
        $releaseRoot = Join-Path $repo 'artifacts\production'
        $available = @(if (Test-Path -LiteralPath $releaseRoot -PathType Container) {
            foreach ($directory in Get-ChildItem -LiteralPath $releaseRoot -Directory) {
                $metadata = Join-Path $directory.FullName 'build-info.json'
                if (!(Test-Path -LiteralPath $metadata) -or !(Test-Path -LiteralPath (Join-Path $directory.FullName 'SHA256SUMS.txt'))) { continue }
                $info = Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json
                if ($info.channel -eq 'Production') {
                    [pscustomobject]@{ Path = $directory.FullName; Version = [version]$info.version; Built = [DateTimeOffset]$info.builtUtc }
                }
            }
        })
        $latest = $available | Sort-Object Version, Built -Descending | Select-Object -First 1
        if ($null -eq $latest) { throw 'Готових production-пакетів немає. Використайте -Build або -ReleaseDirectory <папка розпакованого релізу>.' }
        $ReleaseDirectory = $latest.Path
    }
}
$release = (Resolve-Path -LiteralPath $ReleaseDirectory).Path.TrimEnd('\')
$null = Test-ReleaseFile $release 'build-info.json'
$info = Get-Content -LiteralPath (Join-Path $release 'build-info.json') -Raw | ConvertFrom-Json
if ($info.channel -ne 'Production' -or $info.version -notmatch '^\d+\.\d+\.\d+$' -or $info.commit -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'Потрібен production-пакет із правильними version/commit/channel у build-info.json.'
}
if ($Build -and $info.commit -ne $commit) { throw 'Готовий пакет не відповідає поточному Git HEAD.' }
if ($repositoryDefault) {
    $projectPath = Join-Path $repo 'src\CheckboxBatchPrinter\CheckboxBatchPrinter.csproj'
    if (Test-Path -LiteralPath $projectPath -PathType Leaf) {
        [xml]$project = Get-Content -LiteralPath $projectPath
        $sourceVersion = [string]$project.Project.PropertyGroup.Version
        if ([version]$sourceVersion -gt [version]$info.version) {
            throw "Код має версію $sourceVersion, але останній готовий production-пакет — $($info.version). Спочатку запустіть цю команду з -Build. Старий пакет не встановлено."
        }
    }
}
$newHash = Test-ReleaseFile $release 'app/CheckboxBatchPrinter.exe'
$source = Join-Path $release 'app\CheckboxBatchPrinter.exe'
$targetPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($TargetExecutable)
if ([IO.Path]::GetExtension($targetPath) -ne '.exe') { throw 'TargetExecutable має вказувати на EXE.' }
$installDirectory = (Resolve-Path -LiteralPath (Split-Path $targetPath -Parent)).Path.TrimEnd('\')
Assert-NoReparse $installDirectory
$target = Join-Path $installDirectory ([IO.Path]::GetFileName($targetPath))
$hadExecutable = Test-Path -LiteralPath $target -PathType Leaf
if ((Test-Path -LiteralPath $target) -and !$hadExecutable) { throw 'Замість EXE за вказаним шляхом існує папка.' }
$oldHash = $null
if ($hadExecutable) {
    Assert-NoReparse $target
    $oldHash = Get-Hash $target
    if ($oldHash -eq $newHash) { Write-Host "Production $($info.version) уже встановлено. Змін немає."; return }
    $installedVersion = ((Get-Item -LiteralPath $target).VersionInfo.ProductVersion -split '\+')[0]
    if ($installedVersion -match '^(\d+\.\d+\.\d+)(?:\.\d+)?$' -and [version]$Matches[1] -gt [version]$info.version) {
        throw 'Встановлена версія новіша за вибраний пакет. Автоматичне пониження версії заборонено.'
    }
}
$profile = (Resolve-Path -LiteralPath $ProfileDirectory).Path.TrimEnd('\')
if (!(Test-Path -LiteralPath $profile -PathType Container)) { throw 'Папку production-профілю не знайдено.' }
Assert-NoReparse $profile
if (@(Get-ChildItem -LiteralPath $profile -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
    throw 'Профіль містить посилання/junction. Оновлення зупинено.'
}
$backupRoot = Join-Path $installDirectory 'CheckboxBatchPrinter-backups'
if ($backupRoot -eq $profile -or $backupRoot.StartsWith($profile + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $profile.StartsWith($backupRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Профіль і резервні копії мають бути в окремих папках.' }
if (Test-Path -LiteralPath $backupRoot) { Assert-NoReparse $backupRoot }
Write-Host "Пакет: $($info.version), commit $($info.commit)"
Write-Host "Програма: $target"
if (!$hadExecutable) { Write-Host 'EXE відсутній: буде відновлено з готового production-пакета.' }
if (!$PSCmdlet.ShouldProcess($target, 'Зберегти резервну копію профілю та наявного EXE, встановити production-пакет')) { return }
Assert-AppClosed
$before = Get-Snapshot $profile
$backup = Join-Path $backupRoot ('before-' + $info.version + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$stage = Join-Path $installDirectory ('.CheckboxBatchPrinter.update-' + [guid]::NewGuid().ToString('N') + '.exe')
New-PrivateBackup $backup
try {
    $profileBackup = Join-Path $backup 'profile'
    New-Item -ItemType Directory -Path $profileBackup | Out-Null
    foreach ($entry in Get-ChildItem -LiteralPath $profile -Force) { Copy-Item -LiteralPath $entry.FullName -Destination $profileBackup -Recurse -Force }
    Assert-Snapshot $before (Get-Snapshot $profileBackup)
    Assert-Snapshot $before (Get-Snapshot $profile)
    [IO.File]::Copy($source, $stage, $false)
    if ((Get-Hash $stage) -ne $newHash) { throw 'Контрольна сума підготовленого EXE не збігається.' }
    Assert-AppClosed
    Assert-Snapshot $before (Get-Snapshot $profile)
    if ($hadExecutable) {
        if ((Get-Hash $target) -ne $oldHash) { throw 'Встановлений EXE змінився під час оновлення.' }
        $backupExecutable = Join-Path $backup 'CheckboxBatchPrinter-previous.exe'
        # Staging and backup are on the target volume: replacement is atomic.
        [IO.File]::Replace($stage, $target, $backupExecutable)
        if ((Get-Hash $backupExecutable) -ne $oldHash) { throw 'Перевірка резервного EXE не пройшла.' }
    } else {
        if (Test-Path -LiteralPath $target) { throw "EXE з’явився під час відновлення. Повторіть команду." }
        # Rename into place without overwrite: a concurrent installation must not be replaced.
        [IO.File]::Move($stage, $target)
    }
    if ((Get-Hash $target) -ne $newHash) { throw 'Перевірка встановленого EXE не пройшла.' }
    Assert-Snapshot $before (Get-Snapshot $profile)
    Write-Host "Оновлено до $($info.version). Історія та налаштування збережені ($($before.Count) файлів)."
    Write-Host "Резервна копія: $backup"
    Write-Host 'Можна запускати CRMapp.'
} catch {
    Write-Warning "Резервну копію збережено: $backup"
    throw
}
