## Summary

- 

## User Impact

- 

## Validation

- [ ] `npm ci`
- [ ] `npm run build`
- [ ] `dotnet restore .\FileLocker.slnx`
- [ ] `dotnet build .\FileLocker\FileLocker.csproj -c Release -r win-x64 -nologo`
- [ ] `dotnet test --project .\FileLocker.Tests\FileLocker.Tests.csproj -c Release --no-restore -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:SkipFrontendBuild=true`
- [ ] `.\scripts\Test-BridgeContracts.ps1`
- [ ] `.\scripts\Test-ReleaseGate.ps1 -Configuration Release -RuntimeIdentifier win-x64`

## Safety Checklist

- [ ] No secrets, passwords, key material, or private file contents are logged or committed.
- [ ] Destructive workflows still require explicit user action.
- [ ] Bridge action changes are reflected in `contracts/bridge-actions.json`.
- [ ] Version, setup/update, or release changes include matching docs/release-note updates.
- [ ] Generated Inno installer artifacts and local build outputs are not committed.

## Screenshots

Only include screenshots for UI-visible changes.
