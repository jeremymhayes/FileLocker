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
$publishDir = Join-Path $repoRoot "artifacts\release-gate\publish"
$innoScriptPath = Join-Path $repoRoot "installer\inno\FileLocker.iss"
$innoArtifactDir = Join-Path $repoRoot "artifacts\inno"

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

    Assert-Gate (Test-Path -LiteralPath $Path -PathType Leaf) "$Label is missing: $Path"
}

function Resolve-RepoChildPath {
    param([Parameter(Mandatory)][string]$Path)

    $repoFullPath = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($repoFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside the repository: $fullPath"
    }

    return $fullPath
}

Write-Host "Checking version metadata..."
$projectContent = Get-Content -Raw -LiteralPath $projectPath
$packageVersion = Get-RegexValue $projectContent '<Version>([^<]+)</Version>' 'Project Version'
$versionPrefix = Get-RegexValue $projectContent '<VersionPrefix>([^<]+)</VersionPrefix>' 'Project VersionPrefix'
$assemblyVersion = Get-RegexValue $projectContent '<AssemblyVersion>([^<]+)</AssemblyVersion>' 'Project AssemblyVersion'
$fileVersion = Get-RegexValue $projectContent '<FileVersion>([^<]+)</FileVersion>' 'Project FileVersion'
$informationalVersion = Get-RegexValue $projectContent '<InformationalVersion>([^<]+)</InformationalVersion>' 'Project InformationalVersion'

Assert-Gate ($packageVersion -match '^\d+\.\d+\.\d+\.\d+$') "Project Version must be a four-part Inno/Windows release version."
Assert-Gate ($versionPrefix -eq $packageVersion) "VersionPrefix ($versionPrefix) does not match Version ($packageVersion)."
Assert-Gate ($assemblyVersion -eq $packageVersion) "AssemblyVersion ($assemblyVersion) does not match Version ($packageVersion)."
Assert-Gate ($fileVersion -eq $packageVersion) "FileVersion ($fileVersion) does not match Version ($packageVersion)."
Assert-Gate ($informationalVersion -eq $packageVersion) "InformationalVersion ($informationalVersion) does not match Version ($packageVersion)."
Assert-Gate (-not $projectContent.Contains("Velopack")) "FileLocker.csproj still references Velopack."

$packageContent = Get-Content -Raw -LiteralPath (Join-Path $projectDir "Package.appxmanifest")
$appManifestContent = Get-Content -Raw -LiteralPath (Join-Path $projectDir "app.manifest")
$readmePath = Join-Path $repoRoot "README.md"
$releaseNotesPath = Join-Path $repoRoot "RELEASE_NOTES_$packageVersion.md"
$updateServicePath = Join-Path $projectDir "Services\UpdateService.cs"

Assert-Gate ((Get-RegexValue $packageContent 'Identity[^>]+Version="([^"]+)"' 'Package.appxmanifest Version') -eq $packageVersion) "Package.appxmanifest version is not synchronized."
Assert-Gate ((Get-RegexValue $appManifestContent 'assemblyIdentity[^>]+version="([^"]+)"' 'app.manifest version') -eq $packageVersion) "app.manifest version is not synchronized."
Assert-FileExists $readmePath "README"
Assert-FileExists $innoScriptPath "Inno Setup script"

$readmeContent = Get-Content -Raw -LiteralPath $readmePath
$innoScriptContent = Get-Content -Raw -LiteralPath $innoScriptPath
$updateServiceContent = Get-Content -Raw -LiteralPath $updateServicePath

Assert-Gate ($readmeContent.Contains($packageVersion)) "README does not mention current version $packageVersion."
Assert-Gate ($readmeContent.Contains("Inno Setup")) "README does not describe the Inno Setup installer flow."
if (Test-Path -LiteralPath $releaseNotesPath -PathType Leaf) {
    $releaseNotesContent = Get-Content -Raw -LiteralPath $releaseNotesPath
    Assert-Gate ($releaseNotesContent.Contains($packageVersion)) "Release notes do not mention current version $packageVersion."
    Assert-Gate ($releaseNotesContent.Contains("Inno Setup")) "Release notes do not describe the Inno Setup installer flow."
}
else {
    Write-Host "Release notes are not present in this checkout; skipping local release-notes validation."
}
Assert-Gate ($innoScriptContent.Contains("DefaultDirName={autopf}\FileLocker")) "Inno script must default to Program Files."
Assert-Gate ($innoScriptContent.Contains("OutputBaseFilename=FileLocker-Setup-{#AppVersion}")) "Inno script output filename is not versioned."
Assert-Gate ($innoScriptContent.Contains("ArchitecturesInstallIn64BitMode=x64compatible")) "Inno script is not configured for 64-bit install mode."
Assert-Gate ($updateServiceContent.Contains("FileLocker-Setup")) "Updater does not look for FileLocker setup installer assets."
Assert-Gate (-not $updateServiceContent.Contains("Velopack")) "Updater still references Velopack."

Write-Host "Validating bridge contracts..."
& (Join-Path $repoRoot "scripts\Test-BridgeContracts.ps1")

if (-not $SkipBuild) {
    Write-Host "Restoring solution..."
    dotnet restore (Join-Path $repoRoot "FileLocker.slnx")

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
    dotnet build $projectPath `
        -c $Configuration `
        -r $RuntimeIdentifier `
        -p:Platform=x64 `
        -p:SkipFrontendBuild=true `
        -nologo
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
    $safePublishDir = Resolve-RepoChildPath $publishDir
    if (Test-Path -LiteralPath $safePublishDir) {
        Remove-Item -LiteralPath $safePublishDir -Recurse -Force
    }

    dotnet publish $projectPath `
        -c $Configuration `
        -r $RuntimeIdentifier `
        --self-contained true `
        /p:PublishSingleFile=false `
        /p:PublishTrimmed=false `
        -p:Platform=x64 `
        -p:SkipFrontendBuild=true `
        -o $safePublishDir
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
    Write-Host "Building Inno Setup installer..."
    & (Join-Path $repoRoot "scripts\Build-InnoInstaller.ps1") -Configuration $Configuration -RuntimeIdentifier $RuntimeIdentifier
}

if ($RequireInstallerAssets) {
    $installerPath = Join-Path $innoArtifactDir "FileLocker-Setup-$packageVersion.exe"
    $digestPath = "$installerPath.sha256"
    Assert-FileExists $installerPath "Inno setup installer"
    Assert-FileExists $digestPath "Inno setup SHA-256 sidecar"
    $digestContent = Get-Content -Raw -LiteralPath $digestPath
    Assert-Gate ([regex]::IsMatch($digestContent, '^[0-9a-fA-F]{64}\s+FileLocker-Setup-.+\.exe\s*$')) "Installer SHA-256 sidecar is malformed."
}

Write-Host "Release gate passed for FileLocker $packageVersion."
