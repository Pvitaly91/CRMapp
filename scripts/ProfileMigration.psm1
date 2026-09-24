Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function New-PrivateDirectory {
    param([string]$Path)
    if (Test-Path -LiteralPath $Path) { throw 'Private output directory already exists.' }
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl.SetOwner($sid)
    foreach ($identity in @($sid, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    # Windows PowerShell/.NET Framework creates the directory with the ACL atomically.
    [IO.Directory]::CreateDirectory($Path, $acl) | Out-Null
}

function Copy-ProfileOnce {
    [CmdletBinding(SupportsShouldProcess=$true, ConfirmImpact='High')]
    param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Destination,
          [Parameter(Mandatory)][string]$BackupParent)
    $sourcePath = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\')
    $destinationPath = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
    if (Test-Path -LiteralPath $destinationPath) { throw 'Production data already exists. Nothing will be overwritten; use a separate reviewed migration.' }
    if ($destinationPath.StartsWith($sourcePath + '\', [StringComparison]::OrdinalIgnoreCase) -or $sourcePath -eq $destinationPath) { throw 'Profiles must be separate.' }
    foreach ($path in @($sourcePath, (Split-Path $destinationPath), $BackupParent)) {
        for ($directory = [IO.DirectoryInfo]::new($path); $null -ne $directory; $directory = $directory.Parent) {
            if ($directory.Exists -and ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse points are not allowed for migration.' }
        }
    }
    if (Get-Process CheckboxBatchPrinter -ErrorAction SilentlyContinue) { throw 'Close all Checkbox Batch Printer instances first.' }
    $entries = @(Get-ChildItem -LiteralPath $sourcePath -Recurse -Force)
    if (@($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Source contains a reparse point.' }
    if (-not $PSCmdlet.ShouldProcess($destinationPath, 'Copy development profile ONCE after a protected backup; source stays unchanged')) { return }
    Add-Type -AssemblyName System.Security
    $locks = [Collections.Generic.List[IO.FileStream]]::new()
    $backupPath = Join-Path $BackupParent ('CheckboxBatchPrinter-profile-backup-' + [guid]::NewGuid().ToString('N'))
    try {
        foreach ($file in @($entries | Where-Object { -not $_.PSIsContainer })) {
            $stream = [IO.File]::Open($file.FullName, 'Open', 'Read', 'Read')
            $locks.Add($stream)
            if ($file.Name -eq 'credential.bin' -or $file.Extension -eq '.dpapi') {
                $encrypted = [IO.File]::ReadAllBytes($file.FullName)
                $plain = $null
                try { $plain = [Security.Cryptography.ProtectedData]::Unprotect($encrypted, $null, 'CurrentUser') }
                catch { throw 'DPAPI validation failed for the current Windows account. No production data was written. Re-enter credentials locally on another account/PC.' }
                finally { if ($null -ne $plain) { [Array]::Clear($plain,0,$plain.Length) } }
            }
        }
        New-PrivateDirectory $backupPath
        # Copy encrypted files as-is. Backup never goes into the release folder or ZIP.
        foreach ($entry in $entries) {
            $relative = $entry.FullName.Substring($sourcePath.Length + 1)
            $target = Join-Path $backupPath $relative
            if ($entry.PSIsContainer) { [IO.Directory]::CreateDirectory($target) | Out-Null }
            else { [IO.Directory]::CreateDirectory((Split-Path $target)) | Out-Null; [IO.File]::Copy($entry.FullName,$target,$false) }
        }
        if (Get-Process CheckboxBatchPrinter -ErrorAction SilentlyContinue) { throw 'An app instance started during migration; backup retained, production not written.' }
        $after = @(Get-ChildItem -LiteralPath $sourcePath -Recurse -Force)
        if (@(Compare-Object @($entries.FullName) @($after.FullName)).Count) { throw 'Source changed during migration; backup retained, production not written.' }
        if (Test-Path -LiteralPath $destinationPath) { throw 'Production profile appeared during migration; it will not be overwritten.' }
        # Stage beside destination, then rename into place without overwrite.
        $stage = $destinationPath + '.migration-' + [guid]::NewGuid().ToString('N')
        New-PrivateDirectory $stage
        foreach ($entry in @(Get-ChildItem -LiteralPath $backupPath -Force)) { Copy-Item -LiteralPath $entry.FullName -Destination $stage -Recurse -ErrorAction Stop }
        [IO.Directory]::Move($stage,$destinationPath)
        Write-Host "Profile copied once. Protected backup: $backupPath"
    } finally { foreach ($stream in $locks) { $stream.Dispose() } }
}
Export-ModuleMember -Function Copy-ProfileOnce
