<p align="center">
  <img src="FileLocker/frontend/public/assets/FileLocker_wordmark.png" alt="FileLocker logo" width="120" />
</p>

<h1 align="center">FileLocker</h1>

<p align="center">
  A local-first Windows app for locking files, checking integrity, previewing metadata exposure, and securely cleaning up sensitive files without sending anything to the cloud.
</p>

<p align="center">
  <a href="https://github.com/jeremymhayes/FileLocker/releases/latest">
    <img src="https://img.shields.io/github/v/release/jeremymhayes/FileLocker?label=latest%20release&style=for-the-badge&logo=github" alt="Latest FileLocker release" />
  </a>
  <img src="https://img.shields.io/badge/Windows-10%20%26%2011-0078D4?style=for-the-badge&logo=windows11&logoColor=white" alt="Windows 10 and 11" />
  <img src="https://img.shields.io/badge/Local%20First-No%20Cloud-198754?style=for-the-badge" alt="Local first" />
  <img src="https://img.shields.io/badge/Encryption-AES--256--GCM-111827?style=for-the-badge" alt="AES-256-GCM" />
</p>

<p align="center">
  <a href="https://github.com/jeremymhayes/FileLocker/releases/latest"><strong>Download the latest installer</strong></a>
  ·
  <a href="https://github.com/jeremymhayes/FileLocker/issues">Report an issue</a>
  ·
  <a href="https://github.com/jeremymhayes/FileLocker">Project page</a>
</p>

---

## What FileLocker Is

FileLocker is for people who want simple, local control over sensitive files on Windows.

You can encrypt documents and folders, decrypt them later, generate hashes to check integrity, preview the metadata a file may expose, and securely remove files you no longer want left behind. It is designed to feel like a desktop app first, not a command-line utility or a cloud service.

> [!IMPORTANT]
> FileLocker handles files locally. Passwords, keyfiles, recovery material, update downloads, and file contents stay on your device.

## Why People Use It

- Protect personal documents, archives, client files, or portable backups with strong local encryption.
- Keep encrypted copies in a separate folder instead of mixing `.locked` files back into the original source folder.
- Verify files with SHA-256 or SHA-512 when you want a clear fingerprint.
- Preview metadata before sharing a file with someone else.
- Securely remove files when a normal delete is not enough.

## At A Glance

| Category | What you get |
| --- | --- |
| Platform | Windows 10 and Windows 11 |
| Current repo version | `1.1.0.0` |
| Installer | Standard 64-bit Windows installer |
| Internet required | No, not after installation |
| Cloud account | None |
| Default encryption | AES-256-GCM |
| Updates | Optional checks against GitHub Releases |
| Interface | Drag-and-drop desktop app with quick actions and guided pages |

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

FileLocker uses **AES-256-GCM** for encryption. In practical terms, that means:

- your files are encrypted with a strong modern cipher,
- the app can detect tampering before it restores output,
- and a wrong password should fail safely instead of quietly giving you damaged results.

FileLocker also supports optional extra protection material such as a **keyfile** and **recovery key** for people who want that workflow, but the main experience still works as a normal password-based desktop app.

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
2. Download the newest `FileLocker-Setup-<version>.exe`.
3. Run the installer.
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
- Update checks against GitHub Releases with installer validation

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
- .NET 8 SDK
- Node.js 20 or newer
- Visual Studio 2022 with WinUI / Windows App SDK support if you want the full desktop development setup
- NSIS if you want to build the installer

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
dotnet test ..\FileLocker.Tests\FileLocker.Tests.csproj -nologo
```

Build the installer:

```powershell
cd ..
.\scripts\Build-Installer.ps1 -Configuration Release
```

The installer flow publishes the app into `artifacts\nsis\publish` and produces a `FileLocker-Setup-<version>.exe` installer in `artifacts\nsis`.

</details>

## Links

- [Latest release](https://github.com/jeremymhayes/FileLocker/releases/latest)
- [All releases](https://github.com/jeremymhayes/FileLocker/releases)
- [Issue tracker](https://github.com/jeremymhayes/FileLocker/issues)
- [Repository](https://github.com/jeremymhayes/FileLocker)
