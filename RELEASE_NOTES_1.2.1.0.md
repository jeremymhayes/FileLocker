FileLocker V1.2.1.0 Latest

# FileLocker 1.2.1.0

FileLocker 1.2.1.0 is a System Care update focused on clearer cleanup decisions, safer startup and registry review, better installed-app management, and more reliable file browsing when FileLocker is running as administrator.

# Highlights

Rebuilt Custom Clean into a modern cleanup dashboard with space recoverable, items found, selected cleanup, last cleaned status, and safety/risk status visible at a glance.
Expanded Custom Clean with more Windows, browser, application, gaming, developer, privacy, and advanced cleanup categories.
Improved Startup Manager so startup entries are easier to review by impact, source, publisher, signature, risk, and restore state.
Improved Registry Fixer so it can find more issue types, including Application Paths, ActiveX/COM, file association, shared DLL, help-file, startup, and uninstall problems.
Improved App Manager with clearer installed-app summaries, filtering, sorting, details, export, and uninstall actions.
Fixed Browse Files and Browse Folder failures that could happen when FileLocker was running as administrator.

# What's New

Custom Clean

Added grouped cleanup tabs for All, Windows, Browsers, Applications, Gaming, Developer Tools, Privacy, and Advanced.
Added search, category filters, safety filters, and sorting by size, name, category, or safety.
Added Select All, Select Filtered, Clear Selection, and page-level selection controls.
Added a right-side item details panel showing scanned locations, files found, total size, what will be removed, what will not be removed, and a recommendation.
Added safety labels for cleanup items, including Safe, Review, Advanced, Risky, and Privacy.
Added graceful unavailable and not-found states so unsupported or missing cleanup locations do not look like app failures.
Added broader cleanup coverage for browser caches, app caches, launcher caches, logs, shader caches, recent-file traces, developer caches, and other common cleanup targets.

Startup Manager

Added a startup impact summary with high, medium, and low impact counts.
Added clearer tabs and filters for enabled, disabled, broken, advanced, and ignored startup entries.
Added details for startup type, location, command, resolved file path, publisher, signature, impact, risk, and restore records.
Added support for reviewing more startup sources, including Run and RunOnce entries, startup folders, scheduled tasks, services and drivers, shell and Explorer hooks, WMI event consumers, and packaged startup apps.
Added source-opening actions so users can jump to the registry, file location, Task Scheduler, WMI management, or Startup Apps settings when supported.
Added export and copy actions for startup item details.
Added ignore and return-to-review actions for startup entries the user does not want to keep seeing.
Added confirmation-protected removal for broken startup entries that FileLocker can safely manage.

Registry Fixer

Added a clearer Registry Fixer screen with issue counts, selected items, last scan time, registry health, category tabs, severity filtering, and issue details.
Added scanning for stale Application Paths entries.
Added scanning for invalid ActiveX and COM server references.
Added scanning for broken file type associations.
Added scanning for invalid shared DLL references.
Added scanning for invalid Windows help-file references.
Kept startup and uninstall registry issue review in a safer details-first workflow.

App Manager

Added summary cards for installed apps, visible apps, total known size, and large apps.
Added search, app type filters, size filters, and sorting by size, name, publisher, or install date.
Added tabs for all apps, large apps, recently installed apps, Microsoft apps, Store apps, and desktop apps.
Added an app details panel with publisher, version, size, install date, install location, install source, uninstall command, registry entry, and description where available.
Added quick actions to open install locations, copy app details, export the app list, and launch supported uninstallers.

File Workflows

Improved Browse Files and Browse Folder behavior when FileLocker is elevated.
Added a native Windows shell dialog fallback for elevated file and folder selection.
Kept the normal Windows picker behavior for non-elevated runs.
Improved reliability for workflows that depend on selecting files or folders before encrypting, decrypting, hashing, deleting, or reviewing metadata.

App Experience

Added clearer page subtitles for Custom Clean, Registry Fixer, Startup Manager, and App Manager.
Improved System Care pages with calmer tables, summary cards, details panels, and action areas.
Improved selected-row highlights, status badges, filters, and empty states across System Care screens.
Improved administrator-mode feedback for startup management.

# Fixes

Fixed elevated Browse Files and Browse Folder requests that could fail with "The request could not be completed."
Fixed Custom Clean selection friction by adding visible select-all controls.
Fixed unsupported cleanup areas so they display as unavailable instead of returning misleading cleanup results.
Fixed missing cleanup paths so they display as not found instead of looking like errors.
Fixed Registry Fixer coverage so registry scans are not limited to simple missing-file pointers.
Fixed startup source actions so supported registry, file, task, WMI, and settings locations can be opened directly from the details panel.

# Security and Reliability

FileLocker remains local-first. Settings, history, update state, cleanup metadata, startup restore data, and WebView profile data stay under local FileLocker app data locations.
Custom Clean uses real scan results instead of hardcoded cleanup sizes.
Privacy, developer, advanced, and sign-out-sensitive cleanup items are not selected by default.
Browser cookies, browser history, saved passwords, autofill data, Local Storage, IndexedDB, saved games, mods, worlds, screenshots, configs, and user-created content remain protected by default.
Advanced cleanup actions are clearly marked for review and may require administrator access.
Startup item disabling remains designed around reversible metadata where FileLocker can safely support it.
Broken startup entry removal requires confirmation.
App uninstall actions require confirmation and do not run silent uninstall commands.
System cleanup and registry repair actions stay bounded to safer, explicit targets instead of guessing at broad system locations.

# Known Limitations

FileLocker cannot recover encrypted files if the password, recovery key, or keyfile is lost.
Secure delete is best-effort. On SSDs, wear leveling and device-level remapping can make complete physical removal harder to guarantee.
Some System Care actions require administrator permission, including local-machine startup entries, common Startup folder entries, protected cleanup locations, drive optimization, and free-space wiping.
Vendor uninstallers are controlled by the app publisher after FileLocker launches them.
Custom Clean is intentionally conservative. Some advanced Windows cleanup areas are shown as unavailable or review-only when FileLocker cannot safely clean them through file-based cleanup.
Some cleanup results can show unknown size when Windows does not expose a safe or complete file count before cleanup.

# Install and Update Notes

Download FileLocker-Setup-1.2.1.0.exe from the FileLocker 1.2.1.0 GitHub release when it is published.
Close FileLocker before installing the update.
The installer may request administrator approval.
Existing local settings, history, update preferences, cleanup metadata, startup restore data, and WebView2 profile data remain under local FileLocker app data locations.
After updating, open FileLocker and confirm the app reports version 1.2.1.0.

# Checksums

Use the checksum below to verify the installer you downloaded matches the published release file.

| Artifact | SHA-256 |
| --- | --- |
| FileLocker-Setup-1.2.1.0.exe | a1698b9b0fadaf0fa8431e6ed85a6d5a52ad98c0430c634a9306a5378e8e4f19 |
