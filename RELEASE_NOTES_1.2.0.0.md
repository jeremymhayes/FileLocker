# FileLocker 1.2.0.0

## Highlights

- Added System Care tools for startup entry review, installed app review, and app leftover cleanup.
- Improved reliability during file operations.
- Improved validation before packaging release builds.
- Improved installer and version consistency.
- Improved error handling and recovery across maintenance workflows.

## Improvements

- Added Startup Manager for reviewing and managing startup entries with reversible disable behavior.
- Added App Manager for reviewing installed applications, opening published install locations, and launching visible vendor uninstallers after confirmation.
- Added app leftover cleanup for approved AppData and ProgramData cache, log, temp, and stale app folders.
- Improved active-operation progress handling so current file operations are not confused with previous runs.
- Improved selected-path handling for file-operation lists.
- Improved release validation for bridge contracts, frontend output, staged publish files, installer output, and SHA-256 sidecars.

## Fixes

- Fixed cases where maintenance operation failures could disappear after a notification timed out.
- Fixed package-health issues reported by dependency scanning in frontend tooling dependencies.
- Fixed release workflow token scope so validation jobs use read-only repository access.
- Fixed release-gate behavior for the current unpackaged installer flow.

## Security And Reliability

- Startup item changes preserve restore information before disabling entries.
- App uninstall actions require confirmation and do not run silent uninstall commands.
- App leftover cleanup is limited to approved AppData and ProgramData locations.
- Program Files and Windows folders are excluded from recursive app-leftover cleanup.
- Sensitive operation data receives stronger redaction and safer failure presentation.

## Known Limitations

- FileLocker does not provide password recovery if a password, recovery key, or keyfile is lost.
- Secure delete is best-effort and may be less complete on SSDs because of wear leveling and device-level remapping.
- Some System Care operations require administrator mode.
- Vendor uninstallers are controlled by the app publisher after FileLocker launches them.
- The installer is unsigned unless a signing certificate is supplied during packaging.

## Install And Update Notes

- Download `FileLocker-Setup-1.2.0.0.exe` from the FileLocker 1.2.0.0 GitHub release when it is published.
- Close FileLocker before installing the update.
- The installer may request administrator approval.
- Existing local settings, history, update preferences, and WebView2 profile data remain under the user-local FileLocker app data folder.

## Checksums

| Artifact | SHA-256 |
| --- | --- |
| `FileLocker-Setup-1.2.0.0.exe` | `b81289a03b2b4c0ee8b80f5f72519a7f325a3feda17fac79c30ed755a467cbda` |
