param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$NsisPath,
    [string]$SignToolPath,
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [string]$TimestampServer = "http://timestamp.digicert.com",
    [switch]$RequireSigning
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectDir = Join-Path $repoRoot "FileLocker"
$projectPath = Join-Path $projectDir "FileLocker.csproj"
$installerScript = Join-Path $repoRoot "installer\\FileLocker.nsi"
$publishDir = Join-Path $repoRoot "artifacts\\nsis\\publish"
$outputDir = Join-Path $repoRoot "artifacts\\nsis"

[xml]$projectXml = Get-Content -Raw $projectPath
$targetFramework = $projectXml.Project.PropertyGroup.TargetFramework | Select-Object -First 1
$version = @(
    $projectXml.Project.PropertyGroup.Version | Select-Object -First 1
    $projectXml.Project.PropertyGroup.FileVersion | Select-Object -First 1
    $projectXml.Project.PropertyGroup.VersionPrefix | Select-Object -First 1
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1

if (-not $targetFramework) {
    throw "TargetFramework was not found in $projectPath."
}

if (-not $version) {
    throw "Version metadata was not found in $projectPath."
}

if (-not $NsisPath) {
    $candidatePaths = @(
        (Get-Command makensis.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
        "C:\\Program Files (x86)\\NSIS\\makensis.exe",
        "C:\\Program Files\\NSIS\\makensis.exe"
    ) | Where-Object { $_ -and (Test-Path $_) }

    $NsisPath = $candidatePaths | Select-Object -First 1
}

if (-not $NsisPath) {
    throw "makensis.exe was not found. Install NSIS or pass -NsisPath."
}

if (-not (Test-Path $NsisPath)) {
    throw "makensis.exe was not found at '$NsisPath'."
}

if (-not $SignToolPath) {
    $signToolCandidates = @(
        (Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
        "C:\\Program Files (x86)\\Windows Kits\\10\\bin\\10.0.26100.0\\x64\\signtool.exe",
        "C:\\Program Files (x86)\\Windows Kits\\10\\bin\\10.0.22621.0\\x64\\signtool.exe",
        "C:\\Program Files (x86)\\Windows Kits\\10\\bin\\x64\\signtool.exe"
    ) | Where-Object { $_ -and (Test-Path $_) }

    $SignToolPath = $signToolCandidates | Select-Object -First 1
}

if ($RequireSigning -and [string]::IsNullOrWhiteSpace($CertificatePath)) {
    throw "Installer signing is required, but -CertificatePath was not provided."
}

if ($CertificatePath -and -not (Test-Path -LiteralPath $CertificatePath)) {
    throw "Signing certificate was not found: $CertificatePath"
}

if ($CertificatePath -and (-not $SignToolPath -or -not (Test-Path -LiteralPath $SignToolPath))) {
    throw "signtool.exe was not found. Install the Windows SDK or pass -SignToolPath."
}

if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

Write-Host "Publishing unpackaged app..."
dotnet publish $projectPath `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    /p:PublishSingleFile=false `
    /p:PublishTrimmed=false `
    -o $publishDir

if (-not (Test-Path (Join-Path $publishDir "FileLocker.exe"))) {
    throw "Expected publish output was not found at $publishDir."
}

$requiredPublishFiles = @(
    "App.xbf",
    "MainWindow.xbf",
    "FileLocker.pri",
    "Themes\\Styles.xbf",
    "Assets\\StoreLogo.png",
    "wwwroot\\index.html"
)

$missingPublishFiles = $requiredPublishFiles | Where-Object {
    -not (Test-Path (Join-Path $publishDir $_))
}

if ($missingPublishFiles.Count -gt 0) {
    throw "Publish output is incomplete. Missing required files: $($missingPublishFiles -join ', ')"
}

Write-Host "Building NSIS installer..."
& $NsisPath `
    "/DAPP_VERSION=$version" `
    "/DAPP_FILE_VERSION=$version" `
    "/DPUBLISH_DIR=$publishDir" `
    "/DOUTPUT_DIR=$outputDir" `
    $installerScript

if ($LASTEXITCODE -ne 0) {
    throw "makensis.exe failed with exit code $LASTEXITCODE."
}

$installerPath = Join-Path $outputDir "FileLocker-Setup-$version.exe"

if (-not (Test-Path -LiteralPath $installerPath)) {
    throw "Installer output was not found: $installerPath"
}

if ($CertificatePath) {
    Write-Host "Signing installer..."
    $signArgs = @(
        "sign",
        "/fd", "SHA256",
        "/td", "SHA256",
        "/tr", $TimestampServer,
        "/f", $CertificatePath
    )

    if (-not [string]::IsNullOrWhiteSpace($CertificatePassword)) {
        $signArgs += @("/p", $CertificatePassword)
    }

    $signArgs += $installerPath
    & $SignToolPath @signArgs

    if ($LASTEXITCODE -ne 0) {
        throw "signtool.exe failed with exit code $LASTEXITCODE."
    }
} elseif ($RequireSigning) {
    throw "Installer signing is required, but no certificate was supplied."
} else {
    Write-Host "Installer signing skipped because no certificate was supplied."
}

$digestPath = "$installerPath.sha256"
$digest = (Get-FileHash -Algorithm SHA256 -LiteralPath $installerPath).Hash.ToLowerInvariant()
"$digest  $(Split-Path -Leaf $installerPath)" | Set-Content -LiteralPath $digestPath -Encoding ascii

Write-Host ""
Write-Host "Installer ready:"
Write-Host $installerPath
Write-Host "SHA-256 digest:"
Write-Host $digestPath
