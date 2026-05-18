$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$contractPath = Join-Path $repoRoot "contracts\bridge-actions.json"
$bridgePathCandidates = @(
    (Join-Path $repoRoot "FileLocker\MainWindow\Web\MainWindow.WebView.cs"),
    (Join-Path $repoRoot "FileLocker\MainWindow.WebView.cs")
)
$bridgePath = $bridgePathCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$frontendSrc = Join-Path $repoRoot "FileLocker\frontend\src"

if (-not (Test-Path -LiteralPath $contractPath)) {
    throw "Bridge action contract is missing: $contractPath"
}

if (-not $bridgePath) {
    throw "Bridge dispatch file is missing. Checked: $($bridgePathCandidates -join ', ')"
}

$contract = Get-Content -Raw -LiteralPath $contractPath | ConvertFrom-Json
$contractActions = @($contract.actions) | Sort-Object -Unique
if ($contractActions.Count -eq 0) {
    throw "Bridge action contract does not list any actions."
}

$bridgeContent = Get-Content -Raw -LiteralPath $bridgePath
$dispatchActions = [regex]::Matches($bridgeContent, '"([A-Za-z]+(?:\.[A-Za-z]+)+)"\s*=>') |
    ForEach-Object { $_.Groups[1].Value } |
    Sort-Object -Unique

$frontendActions = Get-ChildItem -LiteralPath $frontendSrc -Recurse -Include *.ts,*.tsx |
    ForEach-Object {
        [regex]::Matches((Get-Content -Raw -LiteralPath $_.FullName), 'invoke(?:<[^>]+>)?\("([^"]+)"') |
            ForEach-Object { $_.Groups[1].Value }
    } |
    Sort-Object -Unique

$missingFromContract = @($dispatchActions + $frontendActions | Sort-Object -Unique | Where-Object { $_ -notin $contractActions })
if ($missingFromContract.Count -gt 0) {
    throw "Bridge actions missing from contracts\bridge-actions.json: $($missingFromContract -join ', ')"
}

$missingFromDispatch = @($contractActions | Where-Object { $_ -notin $dispatchActions })
if ($missingFromDispatch.Count -gt 0) {
    throw "Bridge contract actions missing from C# dispatch: $($missingFromDispatch -join ', ')"
}

Write-Host "Bridge contract validation passed for $($contractActions.Count) action(s)."
