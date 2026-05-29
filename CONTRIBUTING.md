# Contributing to FileLocker

Thank you for helping improve FileLocker. This project is a local-first Windows desktop app, so reliability, data safety, and clear user consent matter more than novelty.

## Project Principles

- Keep FileLocker local-first. Do not add cloud accounts, remote recovery, telemetry, or subscription behavior.
- Preserve the current WinUI 3 + WebView2 + React architecture unless there is explicit agreement to change it.
- Avoid redesigning the app. Keep navigation, workflows, and visual identity consistent.
- Treat encryption, secure delete, cleanup, startup, app management, and update behavior as safety-sensitive.
- Prefer small, reviewable changes with tests for risky behavior.

## Development Setup

Requirements:

- Windows 10 or Windows 11.
- .NET SDK matching `global.json`.
- Node.js 22 or newer for the frontend release gate.
- Visual Studio 2022 with Windows App SDK support for full desktop development.
- Inno Setup 6 for building the public Windows installer.

Install frontend dependencies:

```powershell
cd FileLocker\frontend
npm ci
```

Build from the repository root:

```powershell
dotnet restore .\FileLocker.slnx
dotnet build .\FileLocker\FileLocker.csproj -c Release -r win-x64 -nologo
```

Run tests:

```powershell
dotnet test --project .\FileLocker.Tests\FileLocker.Tests.csproj -c Release --no-restore -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:SkipFrontendBuild=true
```

Run frontend validation:

```powershell
cd FileLocker\frontend
npm run build
```

Run release gate checks from the repository root:

```powershell
.\scripts\Test-BridgeContracts.ps1
.\scripts\Test-ReleaseGate.ps1 -Configuration Release -RuntimeIdentifier win-x64
```

Build the public installer:

```powershell
.\scripts\Build-InnoInstaller.ps1 -Configuration Release -RuntimeIdentifier win-x64
```

## Pull Request Guidelines

Before opening a pull request:

- Rebase or merge against the current target branch.
- Keep the scope focused.
- Include tests for file handling, bridge contracts, parsing, cleanup rules, or release behavior when applicable.
- Update `contracts/bridge-actions.json` when adding, removing, or renaming bridge actions.
- Update TypeScript bridge types and C# DTOs together.
- Update release notes or docs when user-visible behavior changes.
- Avoid committing generated installers, local artifacts, user data, logs, or secrets.

Good pull requests include:

- What changed.
- Why it changed.
- User-visible impact.
- Validation commands and results.
- Screenshots only when UI behavior changed.

## Safety Rules

- Never weaken encryption defaults.
- Never log passwords, key material, full sensitive payloads, or unnecessary full paths.
- Never delete broad system folders recursively.
- Never silently uninstall apps.
- Never bypass confirmation for destructive workflows.
- Never make startup, registry, ProgramData, or common Startup changes without administrator checks when required.

## Release Checklist

Release preparation should include:

- Version metadata sync across project, app manifest, Inno installer metadata, README, release notes, and release gate report.
- `npm ci`
- `npm run build`
- `dotnet restore`
- `dotnet build -c Release`
- `dotnet test -c Release`
- `scripts\Test-BridgeContracts.ps1`
- `scripts\Test-ReleaseGate.ps1 -Configuration Release -RuntimeIdentifier win-x64 -RequireInstallerAssets`
- Inno setup executable and SHA-256 sidecar validation.
- Manual smoke testing of install, launch, About version, encrypt/decrypt, hashing, and System Care scan flows.

Do not create tags or publish releases from a dirty working tree.
