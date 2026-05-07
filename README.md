<p align="center">
  <img src="assets/FileLocker_Wordmark2.png" alt="FileLocker wordmark" width="1672"/>
</p>

# FileLocker

**Current version:** 1.1.0.0

FileLocker is a local Windows desktop app for protecting files, checking integrity, cleaning metadata, and safely removing sensitive data. It runs on your machine, keeps passwords and file contents local, and uses the Windows desktop app only as the secure host for the FileLocker interface.

## What You Can Do

- Encrypt files and folders into FileLocker `.locked` files.
- Decrypt `.locked` files with the original password.
- Hash files with SHA-256 or SHA-512 to verify integrity.
- Encode and decode text using common formats.
- Review file metadata and remove or scramble supported fields.
- Securely delete files with overwrite passes before removal.
- Save file handling, security, and appearance preferences.
- Review recent activity from the dashboard.
- Read the built-in security guide before handling sensitive files.

## Security Model

FileLocker keeps encryption, decryption, hashing, file access, secure delete, settings, and validation inside the Windows app. The interface sends structured requests to the app and receives structured results back. Passwords, keys, and file contents are never moved into a browser tab or sent to a remote service.

Encryption uses AES-256-GCM for authenticated encryption. That means encrypted files are protected for confidentiality and checked for tampering during decryption. A wrong password or modified encrypted file will fail safely instead of producing silent bad output.

## App Sections

- **Dashboard** - Drag files or folders to encrypt, open quick actions, and review recent activity.
- **Encrypt Files** - Choose files or folders, set a password, and create `.locked` files.
- **Decrypt Files** - Choose `.locked` files, enter the password, and restore the originals.
- **Hash Files** - Generate SHA-256 or SHA-512 fingerprints.
- **Encode Text** - Encode or decode text locally.
- **Metadata Scrambler** - Inspect supported metadata and remove or scramble selected fields.
- **Secure Delete** - Confirm and overwrite files before deleting them.
- **Settings** - Configure file handling, security preferences, and appearance.
- **About** - View app and release details.
- **Security Guide** - Learn practical guidance for using FileLocker safely.

## Install

1. Open the [FileLocker releases page](https://github.com/jeremymhayes/FileLocker/releases).
2. Download `FileLocker-Setup-1.1.0.0.exe` from the `v1.1.0.0` release.
3. Run the installer and follow the prompts.
4. Launch FileLocker from the Start Menu or desktop shortcut.

FileLocker is distributed as an unpackaged 64-bit Windows desktop app with an NSIS installer. The installer places the app in `Program Files`, creates shortcuts, and registers a normal Windows uninstall entry.

The app works offline after installation. Windows 10 and Windows 11 normally include the Microsoft Edge WebView2 Runtime. If the app opens but the interface does not load, install the current WebView2 Runtime from Microsoft and reopen FileLocker.

## Updates

FileLocker checks GitHub Releases for new versions and expects installer assets named like:

```text
FileLocker-Setup-1.1.0.0.exe
```

Update downloads are validated before install. If a release asset cannot be verified, FileLocker blocks the update instead of running it.

## Build From Source

Requirements:

- Windows 10 or Windows 11
- Visual Studio 2022 with Windows App SDK workload support
- .NET 8 SDK
- Node.js 20 or newer
- NSIS, if you want to build the installer

Build the app:

```powershell
cd FileLocker\frontend
npm install
npm run build

cd ..
dotnet build .\FileLocker.csproj -c Release
```

Build the installer:

```powershell
cd ..
.\scripts\Build-Installer.ps1 -Configuration Release
```

The installer script publishes the app into `artifacts\nsis\publish`, verifies required desktop and interface files, and writes `FileLocker-Setup-1.1.0.0.exe` into `artifacts\nsis`.

## Important Notes

- FileLocker cannot recover forgotten passwords. There is no recovery key or backdoor.
- Test decryption on a copy before deleting important originals.
- Secure delete is less reliable on SSDs than on traditional hard drives because of wear leveling. Use full-disk encryption such as BitLocker for stronger device-level protection.
- FileLocker is not a password manager, VPN, or replacement for full-disk encryption.
