# FileLocker 1.2.0.0 Release Gate

## Target

- Version target: `1.2.0.0`
- Git tag target: `v1.2.0.0`
- Release title: `FileLocker 1.2.0.0`
- Branch tested: `release/v1.2.0.0`
- Commit tested: Pending validation

## Required Checks

- `dotnet restore .\FileLocker.slnx`
- `npm ci`
- `npm run build`
- `dotnet build .\FileLocker\FileLocker.csproj -c Release -r win-x64 -nologo`
- `dotnet test --project .\FileLocker.Tests\FileLocker.Tests.csproj -c Release --no-restore -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:SkipFrontendBuild=true`
- `.\scripts\Test-BridgeContracts.ps1`
- `.\scripts\Test-ReleaseGate.ps1 -Configuration Release -RuntimeIdentifier win-x64 -RequireInstallerAssets`

## Artifact Targets

| Artifact | SHA-256 |
| --- | --- |
| `artifacts\nsis\FileLocker-Setup-1.2.0.0.exe` | `b81289a03b2b4c0ee8b80f5f72519a7f325a3feda17fac79c30ed755a467cbda` |
| `artifacts\nsis\FileLocker-Setup-1.2.0.0.exe.sha256` | Contains matching SHA-256 sidecar |

## Manual Testing Checklist

- [ ] Install `FileLocker-Setup-1.2.0.0.exe` on Windows 10 or Windows 11.
- [ ] Confirm About/Settings reports version `1.2.0.0`.
- [ ] Encrypt a small file and decrypt it successfully.
- [ ] Generate a SHA-256 hash for a known file.
- [ ] Confirm Dashboard quick encrypt progress stays scoped to the current run.
- [ ] Open System Care and confirm Custom Clean scan completes.
- [ ] Open Startup Manager and confirm non-admin scan works.
- [ ] Open App Manager and confirm installed apps load.
- [ ] Confirm app uninstaller launch prompts before opening a vendor uninstaller.
- [ ] Confirm update check does not report the current version as newer than itself after publication.

## Known Issues

- Installer signing still depends on providing a signing certificate during packaging.
- Manual installed-app smoke testing is still required before publishing.

## Release Blockers

- None from automated release-gate validation.

## Final Recommendation

READY WITH WARNINGS
