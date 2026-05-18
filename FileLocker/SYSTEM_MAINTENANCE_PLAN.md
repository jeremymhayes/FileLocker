# FileLocker System Maintenance Plan

## Safety scope

FileLocker is expanding from file protection into local system maintenance. The first pass keeps every maintenance feature local, free, and explicit. It does not add cloud accounts, subscriptions, background telemetry, or raw disk/partition-table rewriting.

Partition cleaning is implemented as a free-space wipe workflow, not a live partition rewrite. That matches the recovery-prevention goal without destroying existing files or corrupting partition metadata.

Registry cleanup is intentionally bounded to stale startup and uninstall entries with backup-first handling. Broad "registry fixer" behavior is risky, hard to prove safe, and can break installed applications.

## Checklist

- [x] Define the maintenance feature scope and guardrails.
- [x] Add local drive inventory, cleanup scan, cleanup run, drive optimize, free-space wipe, and registry health actions.
- [x] Add typed frontend bridge models for maintenance results.
- [x] Add Partition Cleaner page for free-space wiping.
- [x] Add Drive Optimizer page for analysis and Windows optimize/trim.
- [x] Replace the flat Disk Cleanup page with Custom Clean / Health Check selection groups.
- [x] Add Registry Fixer page for bounded stale-entry scan and backup-first cleanup.
- [x] Add administrator-mode detection and one-click elevated restart for protected maintenance actions.
- [x] Add the new maintenance pages to navigation and page routing.
- [x] Update bridge action contract coverage.
- [x] Run frontend and .NET validation.

## Current expansion checklist

- [x] Add Custom Clean / Health Check grouped cleanup selection.
- [x] Add Windows, browser, and application cleanup buckets.
- [x] Replace the old Disk Cleanup route with Custom Clean.
- [x] Modernize and reorganize the sidebar with thicker, better-spaced buttons.
- [x] Sweep stale route/code references and unused feature files.
- [x] Run frontend, service, contract, and test validation after the sweep.

## Initial feature behavior

### Partition Cleaner

- Lists fixed/removable drives with size, free space, filesystem, and readiness state.
- Uses Windows `cipher /w` against the selected drive root for free-space wiping.
- Shows a final confirmation dialog before starting, with a "do not show again" option.
- Warns that SSD wear leveling and TRIM can limit overwrite guarantees.

### Drive Optimizer

- Lists available drives.
- Supports analyze and optimize actions through Windows `defrag.exe`.
- Uses `/A` for analysis and `/O` for the Windows-recommended optimization path, allowing Windows to choose defrag or trim depending on media.

### Custom Clean / Health Check

- Scans known safe cleanup buckets across Windows, browsers, and application caches.
- Lets the user choose exactly which cleanup areas should be scanned and cleaned.
- Deletes files only from approved locations.
- Requires administrator mode only when selected cleanup areas point at protected system locations.
- Reports skipped/locked items instead of failing the whole run.

### Registry Fixer

- Scans bounded keys for stale file references:
  - Current-user startup entries.
  - Local-machine startup entries when readable.
  - Current-user uninstall entries.
  - Local-machine uninstall entries when readable.
- Creates a `.reg` backup for every cleanup run before deleting values/keys.
- Does not attempt broad COM, driver, service, or shared DLL repairs in this pass.
