param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath,

    [switch]$Unregister
)

$verbs = @(
    @{ Key = 'FileLockerEncrypt'; Label = 'Encrypt with FileLocker'; Args = '--encrypt "%1"'; Targets = @('HKCU:\Software\Classes\*\shell', 'HKCU:\Software\Classes\Directory\shell') },
    @{ Key = 'FileLockerRecycleBinShred'; Label = 'Shred with FileLocker'; Args = '--recycle-bin-shred'; Targets = @('HKCU:\Software\Classes\CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\shell') }
)

$legacyVerbKeys = @(
    'FileLockerDecrypt',
    'FileLockerVerify',
    'FileLockerRotate'
)

if (-not (Test-Path -LiteralPath $ExecutablePath)) {
    throw "Executable not found: $ExecutablePath"
}

foreach ($targetRoot in @('HKCU:\Software\Classes\*\shell', 'HKCU:\Software\Classes\Directory\shell')) {
    foreach ($legacyVerbKey in $legacyVerbKeys) {
        $legacyVerbPath = Join-Path $targetRoot $legacyVerbKey
        if (Test-Path -LiteralPath $legacyVerbPath) {
            Remove-Item -LiteralPath $legacyVerbPath -Recurse -Force
        }
    }
}

foreach ($verb in $verbs) {
    foreach ($targetRoot in $verb.Targets) {
        $verbPath = Join-Path $targetRoot $verb.Key
        $commandPath = Join-Path $verbPath 'command'

        if ($Unregister) {
            if (Test-Path -LiteralPath $verbPath) {
                Remove-Item -LiteralPath $verbPath -Recurse -Force
            }
            continue
        }

        New-Item -Path $verbPath -Force | Out-Null
        Set-ItemProperty -Path $verbPath -Name '(Default)' -Value $verb.Label
        Set-ItemProperty -Path $verbPath -Name 'Icon' -Value $ExecutablePath

        New-Item -Path $commandPath -Force | Out-Null
        $command = '"' + $ExecutablePath + '" ' + $verb.Args
        Set-ItemProperty -Path $commandPath -Name '(Default)' -Value $command
    }
}

if ($Unregister) {
    Write-Host 'FileLocker Explorer integration removed from the current user profile.'
} else {
    Write-Host 'FileLocker Explorer integration registered for the current user profile.'
}
