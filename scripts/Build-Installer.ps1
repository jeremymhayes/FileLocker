param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$InnoSetupPath,
    [string]$OutputRoot,
    [string]$SignToolPath,
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [string]$TimestampServer = "http://timestamp.digicert.com",
    [switch]$RequireSigning,
    [switch]$KeepPublishOutput,
    [string]$NsisPath
)

$ErrorActionPreference = "Stop"

if (-not [string]::IsNullOrWhiteSpace($NsisPath)) {
    Write-Warning "-NsisPath is ignored because FileLocker now packages with Inno Setup."
}

$arguments = @(
    "-Configuration", $Configuration,
    "-RuntimeIdentifier", $RuntimeIdentifier,
    "-TimestampServer", $TimestampServer
)

if (-not [string]::IsNullOrWhiteSpace($InnoSetupPath)) {
    $arguments += @("-InnoSetupPath", $InnoSetupPath)
}

if (-not [string]::IsNullOrWhiteSpace($OutputRoot)) {
    $arguments += @("-OutputRoot", $OutputRoot)
}

if (-not [string]::IsNullOrWhiteSpace($SignToolPath)) {
    $arguments += @("-SignToolPath", $SignToolPath)
}

if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
    $arguments += @("-CertificatePath", $CertificatePath)
}

if (-not [string]::IsNullOrEmpty($CertificatePassword)) {
    $arguments += @("-CertificatePassword", $CertificatePassword)
}

if ($RequireSigning) {
    $arguments += "-RequireSigning"
}

if ($KeepPublishOutput) {
    $arguments += "-KeepPublishOutput"
}

& (Join-Path $PSScriptRoot "Build-InnoInstaller.ps1") @arguments
