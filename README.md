<p align="center">
  <img src="FileLocker/frontend/public/assets/FileLocker_Wordmark.png" alt="FileLocker Wordmark" width="900" />
</p>

<h1 align="center">FileLocker</h1>

<p align="center">
  A local-first Windows app for encrypting files, checking integrity, reviewing metadata exposure, managing startup apps, and cleaning up sensitive files without sending anything to the cloud.
</p>

<p align="center">
  <a href="https://github.com/jeremymhayes/FileLocker/releases/latest">
    <img src="https://img.shields.io/github/v/release/jeremymhayes/FileLocker?label=latest%20release&style=for-the-badge&logo=github" alt="Latest FileLocker release" />
  </a>
  <img src="https://img.shields.io/badge/Windows-10%20%26%2011-0078D4?style=for-the-badge&logo=windows11&logoColor=white" alt="Windows 10 and 11" />
  <img src="https://img.shields.io/badge/Local%20First-No%20Cloud-198754?style=for-the-badge" alt="Local first" />
  <img src="https://img.shields.io/badge/Encryption-Modern%20AEAD-111827?style=for-the-badge" alt="Modern AEAD encryption" />
</p>

<p align="center">
  <a href="https://github.com/jeremymhayes/FileLocker/releases/latest"><strong>Download the latest release</strong></a>
  ·
  <a href="https://github.com/jeremymhayes/FileLocker/issues">Report an issue</a>
  ·
  <a href="https://github.com/jeremymhayes/FileLocker">Project page</a>
</p>

---

## What FileLocker Is

FileLocker is for people who want simple, local control over sensitive files on Windows.

You can encrypt documents and folders, decrypt them later, generate hashes to check integrity, preview the metadata a file may expose, review startup apps, inspect installed apps, and securely remove files you no longer want left behind. It is designed to feel like a desktop app first, not a command-line utility or a cloud service.

> [!IMPORTANT]
> FileLocker handles files locally. Passwords, keyfiles, recovery material, update downloads, and file contents stay on your device.

## Why People Use It

- Protect personal documents, archives, client files, or portable backups with strong local encryption.
- Keep encrypted copies in a separate folder instead of mixing `.locked` files back into the original source folder.
- Verify files with SHA-256 or SHA-512 when you want a clear fingerprint.
- Preview metadata before sharing a file with someone else.
- Securely remove files when a normal delete is not enough.
- Review startup items and installed apps without sending system details to a remote service.
- Clean approved cache, log, temp, and leftover app folders with visible guardrails.

## At A Glance

| Category | What you get |
| --- | --- |
| Platform | Windows 10 and Windows 11 |
| Current version | `1.2.2.1` |
| Installer | Inno Setup installer from the latest GitHub release |
| Internet required | No, not after installation |
| Cloud account | None |
| Default encryption | AES-256-GCM |
| New `.locked` algorithms | Runtime-supported AEAD options: AES-256-GCM, ChaCha20-Poly1305, AES-256-GCM-SIV |
| Updates | Optional GitHub Releases checks for signed or checksum-verified setup installers |
| Interface | Drag-and-drop desktop app with quick actions, guided pages, and System Care tools |

## New In 1.2.2.1

- Migrated public installation and in-app updates to an Inno Setup installer named `FileLocker-Setup-1.2.2.1.exe`.
- Restored normal Windows installer behavior: Program Files install location, setup wizard, upgrade support, shortcuts, launch-after-install, and Apps & Features uninstall.
- Updated the in-app updater to download the setup installer from GitHub Releases, verify SHA-256 when a sidecar is published, launch the installer helper, and let the installer replace Program Files files.
- Kept Windows assembly, file, manifest, installer, README, release notes, and release-gate metadata aligned at `1.2.2.1`.
- Standardized new `.locked` encryption choices around runtime-supported AEAD algorithms with shared metadata, labels, validation, and payload headers.
- Added stronger v4 `.locked` payload metadata checks while keeping existing AES-256-GCM payloads decryptable.
- Hardened file, folder, keyfile, metadata, hash, CSV export, and bridge path handling so invalid or unsafe inputs fail earlier with clearer messages.
- Kept the System Care improvements from 1.2.1.0, including clearer Custom Clean, Startup Manager, Registry Fixer, and App Manager workflows.

## What You Can Do In The App

| Area | What it does |
| --- | --- |
| **Dashboard** | Quick encrypt, recent activity, security status, and drag-and-drop shortcuts |
| **Encrypt Files** | Turn files or folders into FileLocker `.locked` files |
| **Decrypt Files** | Restore `.locked` files with the right password or recovery material |
| **Hash Files** | Generate SHA-256 or SHA-512 hashes and create hash manifests |
| **Encode Text** | Convert text using Base64, URL, Hex, HTML entities, and UTF-8 tools |
| **Metadata Scrambler** | Preview metadata fields and review what may be exposed before sharing |
| **Secure Delete** | Overwrite selected files where possible before removing them |
| **Custom Clean** | Review and clean approved temporary, cache, recycle bin, and log locations |
| **Partition Cleaner** | Wipe free space with Windows tools so deleted-file traces are harder to recover |
| **Drive Optimizer** | Run Windows drive analysis and optimization from a guided page |
| **Registry Fixer** | Review bounded stale startup and uninstall entries with backup-first cleanup |
| **Startup Manager** | Review startup entries and disable or restore supported items |
| **App Manager** | Review installed apps, launch visible uninstallers, and clean approved leftovers |
| **Settings** | Choose output folders, history privacy, appearance, Explorer integration, and update behavior |
| **About + Security Guide** | Read plain-language guidance about what FileLocker does and how to use it safely |

## Folder Output, Without The Mess

One of the biggest quality-of-life details in FileLocker is how folder encryption can be routed.

- You can encrypt files beside the originals if you want to.
- You can also send encrypted output to a separate sibling folder in the same parent directory.
- When a folder is selected, FileLocker can suggest a cleaner output folder automatically.
- If you choose a custom destination, FileLocker can preserve the original folder layout inside that destination.

> [!TIP]
> If you are encrypting a large folder and want to avoid filling the source tree with duplicate `.locked` files, use a separate output folder such as `Folder Name (Encrypted)`.

## Security, In Plain English

FileLocker defaults to **AES-256-GCM** for file encryption and can expose **ChaCha20-Poly1305** and **AES-256-GCM-SIV** for new `.locked` payloads when the local runtime supports the implementation safely. In practical terms, that means:

- your files are encrypted with a strong modern cipher,
- the selected algorithm is saved in the payload header for automatic decryption,
- the app can detect tampering before it restores output,
- and a wrong password should fail safely instead of quietly giving you damaged results.

New `.locked` files use FileLocker's header-authenticated v4 payload format, with explicit algorithm and key-size metadata checked against the authenticated header. Existing AES-256-GCM v3 payloads remain supported for decryption.

The v4 header stores the format version, stable payload algorithm id, KDF id, Argon2id settings, chunk size, nonce prefix, and encrypted key slots. Each key slot has its own salt, nonce, and authentication tag, and encrypted payload chunks carry their own AEAD tags so tampering fails before plaintext is restored.

FileLocker also supports optional extra protection material such as a **keyfile** and **recovery key** for people who want that workflow, but the main experience still works as a normal password-based desktop app.

PNG carrier output uses the older AES-GCM carrier path, is only available with AES-256-GCM, and is capped at 64 MB per source file to avoid the memory pressure of wrapping the payload inside an image. Standard `.locked` files should be used for larger files or when choosing ChaCha20-Poly1305 or AES-256-GCM-SIV.

FileLocker does **not** upload your files or give you a cloud password reset path. If you lose both the password and any recovery material you chose to use, the protected file should be treated as inaccessible.

## What FileLocker Feels Like To Use

- Drag files or folders into the app.
- Choose what you want to do.
- Pick an output location if needed.
- Run the job locally.
- Review the results, history, and any warnings before you move on.

The app keeps a strong boundary between the interface and the file-handling logic. That means the visible app stays easy to use, while the file access, encryption, validation, update checks, and delete workflows remain inside the Windows host app.

## Download And Install

1. Open the [latest release page](https://github.com/jeremymhayes/FileLocker/releases/latest).
2. Download `FileLocker-Setup-1.2.2.1.exe` or the newest `FileLocker-Setup-{version}.exe` asset.
3. Run the setup executable and follow the installer wizard.
4. Launch FileLocker from the Start Menu or desktop shortcut.

FileLocker is distributed as a 64-bit Windows desktop app. It works offline after installation.

<details>
<summary><strong>Install notes</strong></summary>

- The app expects a normal Windows environment with the Microsoft Edge WebView2 Runtime available.
- Most Windows 10 and Windows 11 systems already have WebView2 installed.
- If the app launches but the interface does not appear correctly, install or update WebView2 and reopen FileLocker.

</details>

## Safety Notes

> [!WARNING]
> FileLocker is not a backup service. If something matters, keep a backup before deleting originals.

- Test decryption on a copy before removing important source files.
- Secure delete is best-effort and is generally more reliable on spinning hard drives than on SSDs.
- Startup Manager saves restore information before disabling supported entries.
- App Manager launches vendor uninstallers only after confirmation and does not run silent uninstall commands.
- App leftover cleanup is limited to approved AppData and ProgramData cleanup areas. Program Files and Windows folders are excluded from recursive cleanup.
- Some System Care actions need administrator mode because Windows protects the target locations.
- Use full-disk encryption such as BitLocker alongside FileLocker if you want stronger device-level protection.
- Metadata preview is helpful, but no general-purpose tool can guarantee that every possible metadata field is removed from every file type.

## Advanced Features

<details>
<summary><strong>See the deeper toolset</strong></summary>

- Optional custom encrypt and decrypt output folders
- Suggested sibling output folders for folder-based encryption
- Compression before encryption
- Output-name scrambling
- Optional PNG carrier output
- Folder packaging mode
- Recovery key support
- Keyfile support
- Hash manifest generation
- Explorer right-click integration
- Local history with privacy modes and redacted exports
- Startup entry review with reversible disable support
- Installed app inventory and visible uninstaller launch
- Approved app leftover cleanup for AppData and ProgramData
- GitHub Releases update checks with setup-installer checksum verification

</details>

## What FileLocker Is Not

FileLocker is not:

- a cloud storage service,
- a password manager,
- a VPN,
- a backup platform,
- or a replacement for full-disk encryption.

It is a focused desktop utility for local file protection and cleanup workflows.

## For Developers

<details>
<summary><strong>Build from source</strong></summary>

Requirements:

- Windows 10 or Windows 11
- .NET 10 SDK
- Node.js 22 or newer
- Inno Setup 6 if you need to build the public installer
- Visual Studio 2022 with WinUI / Windows App SDK support if you want the full desktop development setup

Build the frontend and app:

```powershell
cd FileLocker\frontend
npm install
npm run build

cd ..
dotnet build .\FileLocker.csproj -c Release
```

Run tests:

```powershell
dotnet test --project ..\FileLocker.Tests\FileLocker.Tests.csproj -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:SkipFrontendBuild=true
```

Build the Inno Setup installer:

```powershell
cd ..
.\scripts\Build-InnoInstaller.ps1 -Configuration Release -RuntimeIdentifier win-x64
```

The installer flow publishes the app into a clean staging folder, compiles `installer\inno\FileLocker.iss`, and writes `FileLocker-Setup-1.2.2.1.exe` plus `FileLocker-Setup-1.2.2.1.exe.sha256` to `artifacts\inno`.

</details>

## Links

- [Latest release](https://github.com/jeremymhayes/FileLocker/releases/latest)
- [All releases](https://github.com/jeremymhayes/FileLocker/releases)
- [Issue tracker](https://github.com/jeremymhayes/FileLocker/issues)
- [Repository](https://github.com/jeremymhayes/FileLocker)
- [FileLocker 1.2.2.1 release notes](RELEASE_NOTES_1.2.2.1.md)
- [FileLocker 1.2.1.0 release notes](RELEASE_NOTES_1.2.1.0.md)

## Project Documents

- [License](LICENSE)
- [Security policy](SECURITY.md)
- [Contributing guide](CONTRIBUTING.md)
- [Support guide](SUPPORT.md)
- [Code of conduct](CODE_OF_CONDUCT.md)
