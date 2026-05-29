FileLocker V1.2.2.0 Latest

FileLocker 1.2.2.0

FileLocker 1.2.2.0 is a reliability and security-hardening update focused on making the in-app updater install path work correctly, standardizing modern encryption options, and tightening file-operation validation across the app.

Highlights

Fixed the updater install button so FileLocker closes before the downloaded installer starts.
Kept installer cleanup automatic after the update installer exits.
Standardized new `.locked` encryption choices around supported authenticated encryption algorithms.
Added stronger authenticated payload metadata checks for new encrypted files.
Kept old AES-256-GCM FileLocker payloads decryptable.
Improved path validation and error handling across file, folder, hash, metadata, keyfile, and export workflows.
Removed generated browser-test snapshots and root screenshot files from the release branch.

What's New

Updater

Fixed the install flow used by the update popup and Settings page.
The updater now launches a helper process that waits for the running FileLocker process to exit before starting the installer.
The helper launches the installer from the verified local download path and removes that installer file after the installer exits.
The helper no longer relies on a fragile `cmd start` command, which improves handling for quoted paths and installer launch failures.
The updater still validates GitHub Releases installer assets before launch.

Encryption

Standardized supported encryption algorithms through shared app metadata instead of scattered strings.
Kept AES-256-GCM as the default encryption choice.
Added runtime-supported AEAD choices for new `.locked` files, including ChaCha20-Poly1305 and AES-256-GCM-SIV when the local runtime can support them safely.
Added authenticated v4 `.locked` payload headers with explicit algorithm, key-size, KDF, nonce, chunk, and key-slot metadata.
Kept existing AES-256-GCM v3 payloads compatible for decryption.
Improved tests for encryption round trips, wrong passwords, tampering, unsupported algorithm handling, and old AES-256-GCM compatibility.

File Workflows

Improved validation for selected files and folders before bridge actions run.
Improved keyfile handling so invalid keyfile paths fail before encryption starts.
Improved folder package restore checks so package paths stay inside the intended restore root.
Improved metadata and hash workflows so selected paths are validated consistently.
Improved CSV export safety by normalizing unsafe control and format characters before spreadsheet formula escaping.
Improved bounded text reading so temporary character buffers are cleared after use.
Improved user-safe redaction for non-path error text.

System Care

Kept the 1.2.1.0 System Care improvements in place.
Custom Clean still uses real scan results, safety labels, filters, and review-first cleanup controls.
Startup Manager still supports broader startup-source review with reversible disable metadata where FileLocker can safely provide it.
Registry Fixer still reviews bounded stale entries with details-first cleanup.
App Manager still supports installed-app inventory, visible uninstall launch, exports, and approved app-leftover cleanup.

Fixes

Fixed updater install launch failures that could stop the installer from opening after the user pressed Install.
Fixed release-branch cleanup by removing committed root screenshot PNGs and browser automation snapshots.
Fixed stale version metadata so the app, manifests, installer config, README, and release notes all report 1.2.2.0.
Fixed several unsafe-input edge cases where malformed paths, metadata, or export values could travel farther into a workflow than needed.

Security and Reliability

FileLocker remains local-first. Settings, history, update state, cleanup metadata, startup restore data, update downloads, and WebView profile data stay under local FileLocker app data locations.
New encryption options are still authenticated encryption only. FileLocker does not add legacy or unauthenticated algorithms just to increase the option count.
The selected encryption algorithm is stored in authenticated payload metadata for new `.locked` files.
Wrong passwords, tampered ciphertext, tampered tags, and tampered authenticated metadata are expected to fail safely.
Updater downloads still come from GitHub Releases and are validated before launch.
Generated screenshots, browser snapshots, installer binaries, and local automation artifacts should not be committed with source changes.

Known Limitations

FileLocker cannot recover encrypted files if the password, recovery key, or keyfile is lost.
Secure delete is best-effort. On SSDs, wear leveling and device-level remapping can make complete physical removal harder to guarantee.
Some System Care actions require administrator permission, including local-machine startup entries, common Startup folder entries, protected cleanup locations, drive optimization, and free-space wiping.
Vendor uninstallers are controlled by the app publisher after FileLocker launches them.
Custom Clean is intentionally conservative. Some advanced Windows cleanup areas are shown as unavailable or review-only when FileLocker cannot safely clean them through file-based cleanup.

Install and Update Notes

Download FileLocker-Setup-1.2.2.0.exe from the FileLocker 1.2.2.0 GitHub release when it is published.
The in-app updater can be used from FileLocker 1.2.1.0 once the 1.2.2.0 release is published with the installer asset.
The installer may request administrator approval.
Existing local settings, history, update preferences, cleanup metadata, startup restore data, and WebView2 profile data remain under local FileLocker app data locations.
After updating, open FileLocker and confirm the app reports version 1.2.2.0.

Checksums

Publish the generated `FileLocker-Setup-1.2.2.0.exe.sha256` file beside the installer asset in the GitHub release.
