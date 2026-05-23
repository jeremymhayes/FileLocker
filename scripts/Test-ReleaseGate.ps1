param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [switch]$SkipBuild,
    [switch]$SkipTests,
    [switch]$SkipPublish,
    [switch]$SkipInstallerBuild,
    [switch]$RequireInstallerAssets
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectDir = Join-Path $repoRoot "FileLocker"
$projectPath = Join-Path $projectDir "FileLocker.csproj"
$testProjectPath = Join-Path $repoRoot "FileLocker.Tests\FileLocker.Tests.csproj"
$frontendDir = Join-Path $projectDir "frontend"
$installerScript = Join-Path $repoRoot "installer\FileLocker.nsi"
$publishDir = Join-Path $repoRoot "artifacts\release-gate\publish"

function Assert-Gate {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-RegexValue {
    param(
        [string]$Content,
        [string]$Pattern,
        [string]$Label
    )

    $match = [regex]::Match($Content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    Assert-Gate $match.Success "$Label was not found."
    return $match.Groups[1].Value
}

function Assert-FileExists {
    param(
        [string]$Path,
        [string]$Label
    )

    Assert-Gate (Test-Path -LiteralPath $Path) "$Label is missing: $Path"
}

Write-Host "Checking version metadata..."
$projectContent = Get-Content -Raw -LiteralPath $projectPath
$version = Get-RegexValue $projectContent '<Version>([^<]+)</Version>' 'Project Version'
$versionFields = [ordered]@{
    "VersionPrefix" = Get-RegexValue $projectContent '<VersionPrefix>([^<]+)</VersionPrefix>' 'Project VersionPrefix'
    "AssemblyVersion" = Get-RegexValue $projectContent '<AssemblyVersion>([^<]+)</AssemblyVersion>' 'Project AssemblyVersion'
    "FileVersion" = Get-RegexValue $projectContent '<FileVersion>([^<]+)</FileVersion>' 'Project FileVersion'
    "InformationalVersion" = Get-RegexValue $projectContent '<InformationalVersion>([^<]+)</InformationalVersion>' 'Project InformationalVersion'
}

foreach ($field in $versionFields.GetEnumerator()) {
    Assert-Gate ($field.Value -eq $version) "$($field.Key) ($($field.Value)) does not match Version ($version)."
}

$packageContent = Get-Content -Raw -LiteralPath (Join-Path $projectDir "Package.appxmanifest")
$appManifestContent = Get-Content -Raw -LiteralPath (Join-Path $projectDir "app.manifest")
$appInstallerPath = Join-Path $projectDir "FileLocker.appinstaller"
$nsiContent = Get-Content -Raw -LiteralPath $installerScript
$readmePath = Join-Path $repoRoot "README.md"
$releaseNotesPath = Join-Path $repoRoot "RELEASE_NOTES_$version.md"

Assert-Gate ((Get-RegexValue $packageContent 'Identity[^>]+Version="([^"]+)"' 'Package.appxmanifest Version') -eq $version) "Package.appxmanifest version is not synchronized."
Assert-Gate ((Get-RegexValue $appManifestContent 'assemblyIdentity[^>]+version="([^"]+)"' 'app.manifest version') -eq $version) "app.manifest version is not synchronized."
if (Test-Path -LiteralPath $appInstallerPath) {
    $appInstallerContent = Get-Content -Raw -LiteralPath $appInstallerPath
    Assert-Gate ((Get-RegexValue $appInstallerContent '<AppInstaller[^>]+Version="([^"]+)"' 'FileLocker.appinstaller Version') -eq $version) "FileLocker.appinstaller version is not synchronized."
}
Assert-Gate ((Get-RegexValue $nsiContent '!define\s+APP_VERSION\s+"([^"]+)"' 'NSIS APP_VERSION') -eq $version) "NSIS APP_VERSION is not synchronized."
Assert-FileExists $readmePath "README"
Assert-FileExists $releaseNotesPath "Release notes"

$readmeContent = Get-Content -Raw -LiteralPath $readmePath
$releaseNotesContent = Get-Content -Raw -LiteralPath $releaseNotesPath
Assert-Gate ($readmeContent.Contains($version)) "README does not mention current version $version."
Assert-Gate ($releaseNotesContent.Contains($version)) "Release notes do not mention current version $version."

Write-Host "Validating bridge contracts..."
& (Join-Path $repoRoot "scripts\Test-BridgeContracts.ps1")

if (-not $SkipBuild) {
    Write-Host "Building frontend..."
    Push-Location $frontendDir
    try {
        npm ci
        npm run build
    }
    finally {
        Pop-Location
    }

    Write-Host "Building app..."
    dotnet build $projectPath -c $Configuration -r $RuntimeIdentifier -nologo
}

if (-not $SkipTests) {
    Write-Host "Running tests..."
    dotnet test --project $testProjectPath `
        -c $Configuration `
        --no-restore `
        -p:Platform=x64 `
        -p:RuntimeIdentifier=$RuntimeIdentifier `
        -p:SelfContained=true `
        -p:SkipFrontendBuild=true
}

if (-not $SkipPublish) {
    Write-Host "Publishing release-gate output..."
    if (Test-Path -LiteralPath $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }

    dotnet publish $projectPath `
        -c $Configuration `
        -r $RuntimeIdentifier `
        --self-contained true `
        /p:PublishSingleFile=false `
        /p:PublishTrimmed=false `
        -o $publishDir
}

if (-not $SkipPublish -or (Test-Path -LiteralPath $publishDir)) {
    Write-Host "Checking staged publish files..."
    $requiredPublishFiles = @(
        "FileLocker.exe",
        "App.xbf",
        "MainWindow.xbf",
        "FileLocker.pri",
        "Themes\Styles.xbf",
        "Assets\StoreLogo.png",
        "logo.ico",
        "wwwroot\index.html"
    )

    foreach ($relativePath in $requiredPublishFiles) {
        Assert-FileExists (Join-Path $publishDir $relativePath) "Publish payload file"
    }
}

if (-not $SkipInstallerBuild) {
    Write-Host "Building installer..."
    & (Join-Path $repoRoot "scripts\Build-Installer.ps1") -Configuration $Configuration -RuntimeIdentifier $RuntimeIdentifier
}

if ($RequireInstallerAssets) {
    $installerPath = Join-Path $repoRoot "artifacts\nsis\FileLocker-Setup-$version.exe"
    $digestPath = "$installerPath.sha256"
    Assert-FileExists $installerPath "Installer asset"
    Assert-FileExists $digestPath "Installer SHA-256 sidecar"
    $digestContent = Get-Content -Raw -LiteralPath $digestPath
    Assert-Gate ([regex]::IsMatch($digestContent, '^[0-9a-fA-F]{64}\s+FileLocker-Setup-.+\.exe\s*$')) "Installer SHA-256 sidecar is malformed."
}

Write-Host "Release gate passed for FileLocker $version."
