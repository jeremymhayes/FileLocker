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
    [switch]$KeepPublishOutput
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectDir = Join-Path $repoRoot "FileLocker"
$projectPath = Join-Path $projectDir "FileLocker.csproj"
$frontendDir = Join-Path $projectDir "frontend"
$innoScriptPath = Join-Path $repoRoot "installer\inno\FileLocker.iss"
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\inno"
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

function Remove-RepoDirectoryIfExists {
    param([Parameter(Mandatory)][string]$Path)

    $safePath = Resolve-RepoChildPath $Path
    if (Test-Path -LiteralPath $safePath) {
        Remove-Item -LiteralPath $safePath -Recurse -Force
    }

    return $safePath
}

function Find-InnoSetupCompiler {
    if (-not [string]::IsNullOrWhiteSpace($InnoSetupPath)) {
        if (Test-Path -LiteralPath $InnoSetupPath -PathType Leaf) {
            return (Resolve-Path -LiteralPath $InnoSetupPath).Path
        }

        throw "ISCC.exe was not found at $InnoSetupPath"
    }

    $command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    throw "Inno Setup compiler ISCC.exe was not found. Install Inno Setup 6 or pass -InnoSetupPath."
}

function Get-ProjectProperty {
    param(
        [xml]$ProjectXml,
        [Parameter(Mandatory)][string]$Name
    )

    foreach ($propertyGroup in $ProjectXml.Project.PropertyGroup) {
        $value = $propertyGroup.$Name
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return [string]$value
        }
    }

    throw "Project property <$Name> was not found."
}

function Invoke-SignTool {
    param([Parameter(Mandatory)][string]$InstallerPath)

    if ([string]::IsNullOrWhiteSpace($SignToolPath)) {
        $command = Get-Command "signtool.exe" -ErrorAction SilentlyContinue
        if ($command) {
            $SignToolPath = $command.Source
        }
    }

    if ([string]::IsNullOrWhiteSpace($SignToolPath) -or -not (Test-Path -LiteralPath $SignToolPath -PathType Leaf)) {
        throw "Signing was requested but signtool.exe was not found. Pass -SignToolPath."
    }

    if ([string]::IsNullOrWhiteSpace($CertificatePath) -or -not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) {
        throw "Signing was requested but a valid -CertificatePath was not provided."
    }

    $signArguments = @(
        "sign",
        "/fd", "SHA256",
        "/tr", $TimestampServer,
        "/td", "SHA256",
        "/f", $CertificatePath
    )

    if (-not [string]::IsNullOrEmpty($CertificatePassword)) {
        $signArguments += @("/p", $CertificatePassword)
    }

    $signArguments += $InstallerPath
    & $SignToolPath @signArguments
    if ($LASTEXITCODE -ne 0) {
        throw "signtool.exe failed with exit code $LASTEXITCODE."
    }
}

Write-Host "Checking FileLocker version metadata..."
[xml]$projectXml = Get-Content -Raw -LiteralPath $projectPath
$appVersion = Get-ProjectProperty $projectXml "Version"
$versionPrefix = Get-ProjectProperty $projectXml "VersionPrefix"
$assemblyVersion = Get-ProjectProperty $projectXml "AssemblyVersion"
$fileVersion = Get-ProjectProperty $projectXml "FileVersion"
$informationalVersion = Get-ProjectProperty $projectXml "InformationalVersion"

if ($appVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "FileLocker installer version must be a four-part Windows version. Found: $appVersion"
}

foreach ($versionValue in @($versionPrefix, $assemblyVersion, $fileVersion, $informationalVersion)) {
    if ($versionValue -ne $appVersion) {
        throw "Version metadata is not synchronized. Expected $appVersion but found $versionValue."
    }
}

$outputRootPath = Remove-RepoDirectoryIfExists $OutputRoot
$publishDir = Join-Path $outputRootPath "publish"
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

Write-Host "Restoring FileLocker..."
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

Write-Host "Publishing FileLocker $appVersion for $RuntimeIdentifier..."
dotnet publish $projectPath `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    /p:PublishSingleFile=false `
    /p:PublishTrimmed=false `
    -p:Platform=x64 `
    -p:SkipFrontendBuild=true `
    -o $publishDir

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
    $path = Join-Path $publishDir $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Published payload is missing required file: $relativePath"
    }
}

Get-ChildItem -LiteralPath $publishDir -Recurse -File -Include "*.pdb" | Remove-Item -Force

$isccPath = Find-InnoSetupCompiler
Write-Host "Building Inno Setup installer with $isccPath..."
& $isccPath `
    "/DAppVersion=$appVersion" `
    "/DPublishDir=$publishDir" `
    "/DOutputDir=$outputRootPath" `
    "/DSourceRoot=$repoRoot" `
    $innoScriptPath
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compiler failed with exit code $LASTEXITCODE."
}

$installerPath = Join-Path $outputRootPath "FileLocker-Setup-$appVersion.exe"
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Expected installer was not generated: $installerPath"
}

if ($RequireSigning -or -not [string]::IsNullOrWhiteSpace($CertificatePath)) {
    Write-Host "Signing installer..."
    Invoke-SignTool -InstallerPath $installerPath
}

$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $installerPath).Hash.ToLowerInvariant()
$hashPath = "$installerPath.sha256"
"$hash  $(Split-Path -Leaf $installerPath)" | Set-Content -LiteralPath $hashPath -Encoding ascii

if (-not $KeepPublishOutput) {
    Remove-RepoDirectoryIfExists $publishDir | Out-Null
}

$installerItem = Get-Item -LiteralPath $installerPath
$hashItem = Get-Item -LiteralPath $hashPath
Write-Host ""
Write-Host "Inno installer artifacts:"
Write-Host ("  Installer: {0} ({1:N2} MB)" -f $installerItem.FullName, ($installerItem.Length / 1MB))
Write-Host ("  SHA-256:   {0} ({1:N0} bytes)" -f $hashItem.FullName, $hashItem.Length)

[pscustomobject]@{
    Version = $appVersion
    InstallerPath = $installerItem.FullName
    Sha256Path = $hashItem.FullName
}
