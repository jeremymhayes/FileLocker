# FileLocker 1.2.1.0

FileLocker 1.2.1.0 makes the System Care tools easier to understand and safer to use. The release focuses on clearer cleanup decisions, better startup and registry review screens, and smoother file browsing when running with administrator permissions.

## Highlights

- Custom Clean now looks and works like a real cleanup dashboard, with space recoverable, items found, selected cleanup, last cleaned status, and safety/risk status visible at a glance.
- Startup Manager now gives a much clearer view of what starts with Windows, including impact, source, publisher, signature, risk, and restore information.
- Registry Fixer now checks more registry issue types instead of only looking for simple missing file references.
- App Manager now has a cleaner installed-app review screen with better summaries, filtering, sorting, and an app details panel.
- Browse Files and Browse Folder now work more reliably when FileLocker is running as administrator.

## Custom Clean

- Added grouped cleanup areas for Windows, browsers, applications, gaming tools, developer tools, privacy items, and advanced cleanup.
- Added search, category filters, safety filters, and sorting by size, name, category, or safety.
- Added Select All, Select Filtered, and page-level selection controls.
- Added item details showing scanned locations, files found, total size, what will be removed, what will not be removed, and a recommendation.
- Expanded cleanup coverage for browser caches, app caches, launcher caches, logs, shader caches, recent-file traces, developer caches, and other common cleanup targets.
- Unsupported cleanup items now show as unavailable instead of behaving like errors.
- Missing paths now show as not found instead of warning the user about a normal empty location.

## Startup Manager

- Added a startup impact summary with high, medium, and low impact counts.
- Added clearer tabs and filters for enabled, disabled, broken, advanced, and ignored startup entries.
- Added details for source type, location, command, resolved file path, publisher, signature, impact, and risk.
- Added support for reviewing more startup sources, including Run and RunOnce entries, startup folders, scheduled tasks, services and drivers, shell and Explorer hooks, WMI event consumers, and packaged startup apps.
- Added source-opening actions so users can jump to the registry, file location, Task Scheduler, WMI management, or Startup Apps settings when supported.
- Added export and copy actions for startup item details.
- Added ignore/return-to-review behavior for items the user does not want to keep seeing.
- Broken startup entries can be removed only after confirmation and remain backed by FileLocker restore metadata where supported.

## Registry Fixer

- Added a more readable Registry Fixer screen with issue counts, selected items, last scan, registry health, category tabs, severity filtering, and issue details.
- Added scanning for stale Application Paths entries.
- Added scanning for invalid ActiveX and COM server references.
- Added scanning for broken file type associations.
- Added scanning for invalid shared DLL and help-file references.
- Kept startup and uninstall registry issue review in the same safer, details-first workflow.

## App Manager

- Added summary cards for installed apps, visible apps, known app size, and large apps.
- Added search, app type filters, size filters, and sorting by size, name, publisher, or install date.
- Added tabs for all apps, large apps, recently installed apps, Microsoft apps, Store apps, and desktop apps.
- Added an app details panel with publisher, version, size, install date, install location, install source, uninstall command, registry entry, and description where available.
- Added quick actions to open install locations, copy app details, export the app list, and launch supported uninstallers.

## Reliability And Safety

- File and folder browsing now falls back to a native Windows shell dialog when FileLocker is elevated, avoiding the generic picker failure seen in administrator mode.
- Privacy, developer, advanced, and sign-out-sensitive cleanup items are not selected by default.
- Browser cookies, browser history, saved passwords, autofill data, Local Storage, IndexedDB, saved games, mods, worlds, screenshots, configs, and user-created content remain protected by default.
- Advanced cleanup actions are clearly marked for review and may require administrator access.
- Cleanup scans use real file results and report unknown or unavailable locations honestly instead of showing fake cleanup sizes.

## Download

Download `FileLocker-Setup-1.2.1.0.exe` from the FileLocker GitHub release when it is published.
