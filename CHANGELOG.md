# Changelog

## Unreleased - Changes Since FileLocker 1.1.0.0

This changelog compares the current working tree against the GitHub release reference for `FileLocker 1.1.0.0`.

Comparison source:

- Release reference: GitHub release `FileLocker 1.1.0.0`, tag `v1.1.0.0`
- Release tag commit: `46e49b9608968bc7881dcb9f3d613b182ef1cdb9`
- Current HEAD: `1fb7d80` (`main`, `origin/main`)
- Scope: tracked diff from `v1.1.0.0` to the current working tree, plus untracked current-only files
- Diff size before this changelog was added: 121 tracked files changed, 35,556 insertions, 5,208 deletions, plus 9 untracked files

Baseline note:

- The GitHub release is named `FileLocker 1.1.0.0` and publishes `FileLocker-Setup-1.1.0.0.exe`, but the source tag currently points at a tree whose project file reports `1.0.4.0` and README reports `1.0.5.2`. This changelog uses the tag as the release baseline because it is the available release reference.
- The current checkout has uncommitted and untracked work. Treat this as a current-repo changelog, not a committed-only changelog.

### Added

- Added a WebView2-hosted React/Vite frontend under `FileLocker/frontend`, including a routed app shell, sidebar navigation, dashboard, dedicated workflow pages, reusable UI primitives, toast notifications, and bridge-backed state loading.
- Added a typed JavaScript-to-C# bridge contract in `contracts/bridge-actions.json` with 38 declared bridge actions covering app state, files, crypto, hashing, metadata, secure delete, maintenance, settings, shell integration, updates, and history.
- Added dedicated frontend pages for Dashboard, Encrypt Files, Decrypt Files, Hash Files, Encode Text, Metadata Scrambler, Secure Delete, Settings, About, and Security Guide.
- Added system-maintenance surfaces for Custom Clean, Partition Cleaner, Drive Optimizer, and Registry Fixer, backed by bridge actions such as `maintenance.scanCleanup`, `maintenance.runCleanup`, `maintenance.optimizeDrive`, `maintenance.wipeFreeSpace`, `maintenance.scanRegistry`, and `maintenance.cleanRegistry`.
- Added `SystemMaintenanceService` for bounded local maintenance tasks: drive enumeration, temp/recycle cleanup scanning and deletion, `defrag.exe` analyze/optimize calls, `cipher.exe /w` free-space wiping, and targeted stale registry cleanup with backup-first behavior.
- Added administrator-aware restart support so maintenance pages can request a relaunch into elevated mode for operations that require it.
- Added drag-and-drop event routing from WebView2 into the React pages, with queued file paths directed to the current compatible workflow.
- Added persisted app preferences for theme, output directories, history privacy, full-path export behavior, and timestamp policy.
- Added operation history models and export support for local workflow history.
- Added payload metadata models, queued file models, and folder/package metadata for richer encryption and decryption workflows.
- Added file hashing services for SHA-based hash computation, verification, hash manifest creation, and hash manifest verification.
- Added compression and output-path advisor services to support cleaner encrypted output behavior.
- Added secure delete and file cleanup helpers for overwrite/remove workflows.
- Added explorer integration plumbing and `Register-ExplorerIntegration.ps1` for a Windows right-click entry.
- Added bridge and release validation scripts: `scripts/Test-BridgeContracts.ps1` and `scripts/Test-ReleaseGate.ps1`.
- Added a Windows GitHub Actions release gate that installs Node and NSIS, builds the frontend/app, runs tests, publishes, and checks installer assets.
- Added a dedicated test project with coverage for compression advice, output path suggestions, encryption probing, hashing, hash manifests, operation history export, chunked payloads, storage savings, UI state rules, and update service behavior.
- Added repo planning artifacts for system maintenance and future features.

### Changed

- Reworked the app shell from a large XAML-first interface into a small WinUI host that loads a packaged React frontend through WebView2.
- Split the previous monolithic `MainWindow.xaml.cs` code into focused partial classes for initialization, navigation, settings, processing, workflow sections, support actions, and WebView bridge handling.
- Reduced `MainWindow.xaml` to host-level structure and moved most user-facing layout into frontend TSX pages and CSS.
- Updated project build behavior so `dotnet build` can run the frontend build, copy `frontend/dist` into `wwwroot`, and include web assets in build and publish output.
- Updated release packaging to require frontend output, including `wwwroot/index.html`, in staged publish and installer validation.
- Updated the NSIS flow and release-gate checks around the current unpackaged WinUI + WebView2 distribution model.
- Updated the WebView2 startup path to use a writable user-local data directory under FileLocker app data before `EnsureCoreWebView2Async`.
- Updated current version metadata in the working tree to `1.1.1.0` across the project and installer-facing files.
- Updated dependency/runtime configuration, including `.NET SDK 10.0.203` in `global.json`, Microsoft Testing Platform configuration, Windows App SDK package changes, WebView2 package usage, and React/Vite frontend dependencies.
- Updated README content from the older developer/release-flow oriented document into a user-facing product README describing local-first file protection, app areas, install flow, safety notes, and developer commands.
- Updated launcher settings and app manifests for the current unpackaged desktop flow.
- Updated bridge-facing frontend types to cover new dashboard, settings, operation, maintenance, and update payloads.

### Security And Reliability

- Added integrity-focused decrypt and verify paths that can validate payload content without always writing restored output.
- Added sensitive error redaction before bridge errors are returned to the frontend.
- Added local app data storage for preferences, WebView2 profile data, update state, and history.
- Added output collision handling, safer output path suggestions, and workflow preflight status reporting.
- Added admin gating and explicit confirmations for destructive or long-running maintenance operations such as free-space wiping and registry cleanup.
- Added release-gate checks for synchronized version metadata, bridge action coverage, frontend build, .NET build/tests, publish payload completeness, installer creation, and installer SHA-256 sidecar format.

### Removed

- Removed the older `DefaultXsltOutput.htm` artifact.
- Removed the older `FEATURE_IMPROVEMENTS.md` planning artifact from the tracked tree.
- Removed the old root-level `FileLocker/UpdateService.cs` placement in favor of `FileLocker/Services/UpdateService.cs`.
- Removed the old `Filelocker2.ico` reference and added `logo.ico` as the active app/installer icon.
- Removed old wordmark assets from `assets/` while moving frontend image usage under `FileLocker/frontend/public/assets`.
- Removed several early settings component files from the frontend in favor of the current page/component structure.

### Current Untracked Files Included In This Comparison

These files exist in the current checkout but are not yet tracked by Git:

- `FileLocker/SYSTEM_MAINTENANCE_PLAN.md`
- `FileLocker/Services/SystemMaintenanceService.cs`
- `FileLocker/frontend/src/components/dashboard/SummaryMetric.tsx`
- `FileLocker/frontend/src/components/layout/Workspace.tsx`
- `FileLocker/frontend/src/components/ui/badge.tsx`
- `FileLocker/frontend/src/components/ui/checkbox.tsx`
- `FileLocker/frontend/src/pages/SystemMaintenancePages.tsx`
- `FileLocker/future-features.md`

### Compatibility And Release Notes

- This current repo state is significantly larger than the 1.1.0.0 release source because it includes a frontend stack, a bridge contract, a test project, and new maintenance workflows.
- A future release should either commit or intentionally drop the current untracked files before packaging; otherwise the maintenance bridge actions will not be reproducible from source control.
- Version metadata now reports `1.1.1.0` for the current project and installer-facing files.
- The release gate expects `RELEASE_NOTES_1.1.1.0.md` for the current metadata version.
- The frontend build is now part of the app build/publish path unless `SkipFrontendBuild=true` is supplied.
