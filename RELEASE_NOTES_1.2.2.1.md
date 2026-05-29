FileLocker V1.2.2.1 Latest

# FileLocker 1.2.2.1

FileLocker 1.2.2.1 uses Inno Setup as the public Windows installer. The release produces a normal setup wizard, installs application binaries under Program Files, supports upgrades and uninstall through Windows Apps & Features, and keeps FileLocker user data under local app data.

## Highlights

- Added an Inno Setup installer script at `installer\inno\FileLocker.iss`.
- Added `scripts\Build-InnoInstaller.ps1` to restore, build, publish, package, and generate `FileLocker-Setup-1.2.2.1.exe.sha256`.
- Updated `scripts\Build-Installer.ps1` and the release gate to validate Inno installer artifacts instead of Velopack packages.
- Removed Velopack from the runtime startup path and package references.
- Updated the in-app updater to find `FileLocker-Setup-{version}.exe` assets on GitHub Releases, verify SHA-256, launch the installer helper, and let the installer replace installed files.
- Kept local app data, settings, history, cleanup metadata, startup restore data, update downloads, logs, and WebView2 profile data outside Program Files.

## Packaging Changes

- Installer system: Inno Setup 6
- Installer filename: `FileLocker-Setup-1.2.2.1.exe`
- Installer checksum: `FileLocker-Setup-1.2.2.1.exe.sha256`
- Default install location: `{autopf}\FileLocker`
- Windows file version: `1.2.2.1`
- Main executable: `FileLocker.exe`
- Build command: `.\scripts\Build-InnoInstaller.ps1 -Configuration Release -RuntimeIdentifier win-x64`

The generated installer, checksum sidecar, publish staging folder, logs, and other files under `artifacts\` are release artifacts and should not be committed.

## GitHub Release Notes

Upload these files from `artifacts\inno` to the GitHub release:

- `FileLocker-Setup-1.2.2.1.exe`
- `FileLocker-Setup-1.2.2.1.exe.sha256`

The in-app updater expects the installer asset to use the `FileLocker-Setup-{version}.exe` naming pattern and verifies the matching `.sha256` sidecar when available.

## Upgrade Notes

- Fresh installs should use the Inno Setup installer from the GitHub release.
- Upgrade installs should preserve user data because FileLocker stores settings, history, cleanup metadata, startup restore data, update logs, downloads, and WebView2 profile data under local app data.
- Updating directly from an older NSIS or Velopack install should be manually smoke tested before publishing broadly.

## Checksums

The package script writes a SHA-256 sidecar beside the installer. Publish the sidecar with the installer so update checks can verify the downloaded setup executable before launch.
