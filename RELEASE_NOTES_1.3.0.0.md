FileLocker V1.3.0.0 Latest

# FileLocker 1.3.0.0

FileLocker 1.3.0.0 keeps the public Windows installer on Inno Setup, refreshes release metadata for the 1.3 line, and tightens the WebView2 desktop chrome so top-row app controls no longer sit under the native Windows caption buttons when the custom titlebar spacing is set to zero.

## Highlights

- Reserved native Windows caption-button space for top-page controls while keeping the app content pinned to the top of the window.
- Kept Windows assembly, file, package manifest, app manifest, installer, updater mock data, updater tests, README, support docs, and release-gate metadata aligned at `1.3.0.0`.
- Preserved the Inno Setup installer flow for normal Program Files installs, upgrades, Start Menu shortcuts, desktop shortcuts, and Apps & Features uninstall.
- Kept the in-app updater on `FileLocker-Setup-{version}.exe` GitHub release assets with optional SHA-256 sidecar verification.
- Carried forward the local-first encryption, hashing, metadata, secure-delete, startup, registry, cleanup, drive, and app-management surfaces.

## Packaging

- Installer system: Inno Setup 6
- Installer filename: `FileLocker-Setup-1.3.0.0.exe`
- Installer checksum: `FileLocker-Setup-1.3.0.0.exe.sha256`
- Default install location: `{autopf}\FileLocker`
- Windows file version: `1.3.0.0`
- Main executable: `FileLocker.exe`
- Build command: `.\scripts\Build-InnoInstaller.ps1 -Configuration Release -RuntimeIdentifier win-x64`

The generated installer, checksum sidecar, publish staging folder, logs, and other files under `artifacts\` are release artifacts and should not be committed.

## GitHub Release Assets

Upload these files from `artifacts\inno` to the GitHub release:

- `FileLocker-Setup-1.3.0.0.exe`
- `FileLocker-Setup-1.3.0.0.exe.sha256`

The in-app updater expects the installer asset to use the `FileLocker-Setup-{version}.exe` naming pattern and verifies the matching `.sha256` sidecar when available.

## Upgrade Notes

- Fresh installs should use the Inno Setup installer from the GitHub release.
- Upgrade installs should preserve user data because FileLocker stores settings, history, cleanup metadata, startup restore data, update logs, downloads, and WebView2 profile data under local app data.
- Updating directly from an older NSIS or Velopack install should be manually smoke tested before publishing broadly.

## Checksums

The package script writes a SHA-256 sidecar beside the installer. Publish the sidecar with the installer so update checks can verify the downloaded setup executable before launch.
