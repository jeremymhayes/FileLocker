# FileLocker 1.1.1.0

## Highlights

- Refined the React/WebView interface into a denser desktop utility layout with flatter workspace surfaces and tighter page hierarchy.
- Added local maintenance tools for Custom Clean, Partition Cleaner, Drive Optimizer, and Registry Fixer.
- Added bridge-contract validation, release-gate checks, staged publish validation, and NSIS installer output checks.
- Expanded automated tests for hashing, manifests, compression advice, payload handling, history export, and update behavior.

## Fixes

- Fixed the Custom Clean administrator notice so it uses a compact full-width status row instead of collapsing inside the narrow options pane.
- Improved WebView2 startup packaging by preserving required frontend assets in build and publish output.
- Updated installer metadata, app manifests, and project version fields to `1.1.1.0`.

## Validation

- Use `scripts\Test-ReleaseGate.ps1 -RequireInstallerAssets` before publishing this release.
- Expected installer asset: `artifacts\nsis\FileLocker-Setup-1.1.1.0.exe`
- Expected digest sidecar: `artifacts\nsis\FileLocker-Setup-1.1.1.0.exe.sha256`
