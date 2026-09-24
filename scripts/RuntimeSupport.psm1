Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-RuntimeInventory {
    param([object[]]$Inventory, [version]$Minimum = '8.0.0')
    $groups = @($Inventory | Group-Object { if ($_.PSObject.Properties['HostPath']) { $_.HostPath } else { 'synthetic-host' } })
    foreach ($group in $groups) {
        $compatible = $true
        foreach ($name in @('Microsoft.WindowsDesktop.App', 'Microsoft.NETCore.App')) {
            $matches = @($group.Group | Where-Object {
                $_.Architecture -eq 'x64' -and $_.Name -eq $name -and
                $_.Version -match '^8\.0\.\d+$' -and [version]$_.Version -ge $Minimum
            })
            if ($matches.Count -eq 0) { $compatible = $false }
        }
        if ($compatible) { return $true }
    }
    return $false
}

function Get-DesktopRuntimeInventory {
    # Query the known x64 host, never infer architecture from PATH or folder name alone.
    $programFiles64 = [Environment]::GetEnvironmentVariable('ProgramW6432')
    if (-not $programFiles64) { $programFiles64 = $env:ProgramFiles }
    $candidates = @((Join-Path $programFiles64 'dotnet\dotnet.exe'))
    $key = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $installed = $key.OpenSubKey('SOFTWARE\dotnet\Setup\InstalledVersions\x64')
        if ($installed) {
            try { $location = $installed.GetValue('InstallLocation'); if ($location) { $candidates += Join-Path $location 'dotnet.exe' } }
            finally { $installed.Dispose() }
        }
    } finally { $key.Dispose() }
    foreach ($hostPath in @($candidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf)) { continue }
        $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($hostPath))
        try {
            $reader.BaseStream.Position = 0x3c; $pe = $reader.ReadInt32()
            $reader.BaseStream.Position = $pe
            if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { continue }
        } finally { $reader.Dispose() }
        $lines = & $hostPath --list-runtimes
        if ($LASTEXITCODE -ne 0) { continue }
        foreach ($line in $lines) {
            if ($line -match '^(Microsoft\.(?:WindowsDesktop|NETCore)\.App) (\S+) \[(.+)\]$') {
                [pscustomobject]@{ Name=$Matches[1]; Version=$Matches[2]; Architecture='x64'; Path=$Matches[3]; HostPath=$hostPath }
            }
        }
    }
}
Export-ModuleMember -Function Test-RuntimeInventory,Get-DesktopRuntimeInventory
