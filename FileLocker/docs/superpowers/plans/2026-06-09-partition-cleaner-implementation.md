# Partition Cleaner Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Revamp Partition Cleaner into a compact Free-Space Sanitizer with truthful media-aware guidance, host-owned running state, cancel support, and a polished guided inspector UI.

**Architecture:** Add conservative drive media detection and a host-owned free-space wipe operation layer before changing the UI. The host starts `cipher.exe`, streams wipe status events to the React layer, owns cancellation and reattach state, and the frontend renders the approved guided flow from real bridge state.

**Tech Stack:** C#/.NET 8 WinUI 3 WebView2 host, `DriveInfo`, Windows Storage WMI through PowerShell/CIM, React 19, TypeScript, Vite, Tailwind 4, existing bridge event channel.

---

## Scope Check

This is one feature with three tightly coupled parts: drive metadata, wipe lifecycle, and the Partition Cleaner UI. Do not split into independent implementation branches because the approved UI must not ship with fake media or fake running-state controls.

## File Structure

- Modify: `Services/SystemMaintenanceService.cs`
  - Add drive media payload fields.
  - Keep drive enumeration and normalization in the existing maintenance boundary.
  - Keep pure helpers here only if they are short and maintenance-specific.
- Create: `Services/DriveMediaTypeDetector.cs`
  - Resolve volume media type with a timeout and safe `Unknown`/`Mixed` fallbacks.
  - Expose pure classification helpers for tests.
- Create: `Services/FreeSpaceWipeOperationService.cs`
  - Own `cipher.exe` process execution, stdout/stderr streaming, pass parsing, cancellation, and result creation.
  - Stay UI-agnostic by reporting state through callbacks.
- Modify: `MainWindow/Web/MainWindow.WebView.cs`
  - Add bridge actions for start/status/cancel.
  - Store the single active wipe operation in the host and post status events.
- Modify: `frontend/src/types/bridge.ts`
  - Add drive media fields, wipe status/result types, and the `maintenanceWipeStatus` bridge event.
- Modify: `frontend/src/services/bridge.ts`
  - Route `maintenanceWipeStatus` events to subscribers.
- Modify: `frontend/src/App.tsx`
  - Store latest wipe status from events and pass it to `PartitionCleanerPage`.
- Modify: `frontend/src/pages/SystemMaintenancePages.tsx`
  - Replace the current Partition Cleaner layout with the approved guided inspector flow.
  - Add local helpers/components only for this page.
- Modify: `frontend/src/services/devBridgeMock.ts`
  - Add media fixtures and mock start/status/cancel behavior.
- Modify: `../FileLocker.Tests/SystemMaintenanceServiceTests.cs`
  - Add media classification, pass parsing, estimate, and cancellation-status tests.

---

### Task 1: Add Media Model and Pure Classifier

**Files:**
- Create: `Services/DriveMediaTypeDetector.cs`
- Modify: `Services/SystemMaintenanceService.cs:79-90`
- Test: `../FileLocker.Tests/SystemMaintenanceServiceTests.cs`

- [ ] **Step 1: Write failing media classification tests**

Append these tests to `../FileLocker.Tests/SystemMaintenanceServiceTests.cs`:

```csharp
[Fact]
public void ClassifyMediaTypes_ReturnsHddForSingleHddPhysicalDisk()
{
    DriveMediaInfo media = DriveMediaTypeDetector.ClassifyPhysicalMediaTypes([3], physicalDiskCount: 1, timedOut: false, unsupported: false);

    Assert.Equal("HDD", media.mediaType);
    Assert.Equal("Detected", media.mediaDetectionStatus);
    Assert.Contains("traditional hard drive", media.mediaDescription, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public void ClassifyMediaTypes_ReturnsSsdForSingleSsdPhysicalDisk()
{
    DriveMediaInfo media = DriveMediaTypeDetector.ClassifyPhysicalMediaTypes([4], physicalDiskCount: 1, timedOut: false, unsupported: false);

    Assert.Equal("SSD", media.mediaType);
    Assert.Equal("Detected", media.mediaDetectionStatus);
    Assert.Contains("TRIM", media.mediaDescription, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public void ClassifyMediaTypes_ReturnsMixedForConflictingPhysicalDisks()
{
    DriveMediaInfo media = DriveMediaTypeDetector.ClassifyPhysicalMediaTypes([3, 4], physicalDiskCount: 2, timedOut: false, unsupported: false);

    Assert.Equal("Mixed", media.mediaType);
    Assert.Equal("Mixed", media.mediaDetectionStatus);
}

[Fact]
public void ClassifyMediaTypes_ReturnsUnknownForMultiDiskSameMediaVolume()
{
    DriveMediaInfo media = DriveMediaTypeDetector.ClassifyPhysicalMediaTypes([3, 3], physicalDiskCount: 2, timedOut: false, unsupported: false);

    Assert.Equal("Unknown", media.mediaType);
    Assert.Equal("Unknown", media.mediaDetectionStatus);
}

[Fact]
public void ClassifyMediaTypes_ReturnsUnknownForTimeout()
{
    DriveMediaInfo media = DriveMediaTypeDetector.ClassifyPhysicalMediaTypes([], physicalDiskCount: 0, timedOut: true, unsupported: false);

    Assert.Equal("Unknown", media.mediaType);
    Assert.Equal("TimedOut", media.mediaDetectionStatus);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```powershell
dotnet test --project ..\FileLocker.Tests\FileLocker.Tests.csproj -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:SkipFrontendBuild=true -- --filter-class FileLocker.Tests.SystemMaintenanceServiceTests
```

Expected: build fails because `DriveMediaInfo` and `DriveMediaTypeDetector` do not exist.

- [ ] **Step 3: Add the media detector model and classifier**

Create `Services/DriveMediaTypeDetector.cs`:

```csharp
using System.Diagnostics;
using System.Text.Json;

namespace FileLocker;

internal sealed record DriveMediaInfo(
    string mediaType,
    string mediaDetectionStatus,
    string mediaDescription);

internal static class DriveMediaTypeDetector
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    internal static DriveMediaInfo DetectForDriveRoot(string driveRoot)
    {
        if (string.IsNullOrWhiteSpace(driveRoot) || driveRoot.Length < 2 || driveRoot[1] != ':')
        {
            return Unknown("Unsupported", "FileLocker could not map this volume to physical media.");
        }

        char driveLetter = char.ToUpperInvariant(driveRoot[0]);
        if (!char.IsAsciiLetter(driveLetter))
        {
            return Unknown("Unsupported", "FileLocker could not map this volume to physical media.");
        }

        try
        {
            return DetectWithPowerShell(driveLetter, DefaultTimeout);
        }
        catch
        {
            return Unknown("Unknown", "FileLocker could not confidently identify the underlying storage media.");
        }
    }

    internal static DriveMediaInfo Removable() =>
        new("Removable", "Detected", "Limited benefit on removable flash media because wear leveling can prevent direct overwrite and writes add wear.");

    internal static DriveMediaInfo ClassifyPhysicalMediaTypes(IEnumerable<int> mediaTypes, int physicalDiskCount, bool timedOut, bool unsupported)
    {
        if (timedOut)
        {
            return Unknown("TimedOut", "Media detection timed out. FileLocker will not guess the storage type.");
        }

        if (unsupported)
        {
            return Unknown("Unsupported", "This volume does not expose a supported physical media mapping.");
        }

        int[] normalized = mediaTypes
            .Where(value => value is 3 or 4)
            .Order()
            .ToArray();

        if (normalized.Length == 0)
        {
            return Unknown("Unknown", "FileLocker could not confidently identify the underlying storage media.");
        }

        int[] distinct = normalized.Distinct().ToArray();
        if (distinct.Length > 1)
        {
            return new DriveMediaInfo("Mixed", "Mixed", "This volume maps to multiple physical media types.");
        }

        if (physicalDiskCount != 1)
        {
            return Unknown("Unknown", "This volume maps to multiple physical disks, so FileLocker will not guess the storage type.");
        }

        return distinct[0] == 3
            ? new DriveMediaInfo("HDD", "Detected", "Good fit for traditional hard drives; free-space overwrite works as intended on spinning disks.")
            : new DriveMediaInfo("SSD", "Detected", "Limited benefit on SSDs because TRIM and wear leveling can prevent direct overwrite of deleted data.");
    }

    private static DriveMediaInfo DetectWithPowerShell(char driveLetter, TimeSpan timeout)
    {
        string script = string.Join(" ", [
            "$ErrorActionPreference='Stop';",
            $"$partition=Get-Partition -DriveLetter '{driveLetter}';",
            "$disks=@($partition | Get-Disk);",
            "$physical=@();",
            "foreach($disk in $disks){ try { $physical += @(Get-PhysicalDisk -DeviceNumber $disk.Number -ErrorAction Stop) } catch {} }",
            "$mediaTypes=[int[]]@($physical | ForEach-Object { switch -Regex ([string]$_.MediaType) { '^3$|^HDD$' { 3; break } '^4$|^SSD$' { 4; break } default { 0 } } });",
            "[pscustomobject]@{ PhysicalDiskCount=$physical.Count; MediaTypes=$mediaTypes } | ConvertTo-Json -Compress"
        ]);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(script);

        if (!process.Start())
        {
            return Unknown("Unknown", "FileLocker could not start media detection.");
        }

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            TryKill(process);
            return Unknown("TimedOut", "Media detection timed out. FileLocker will not guess the storage type.");
        }

        string output = process.StandardOutput.ReadToEnd();
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            return Unknown("Unknown", "FileLocker could not confidently identify the underlying storage media.");
        }

        PowerShellMediaPayload payload = ParsePowerShellMediaPayload(output);
        return ClassifyPhysicalMediaTypes(payload.MediaTypes, payload.PhysicalDiskCount, timedOut: false, unsupported: false);
    }

    private static PowerShellMediaPayload ParsePowerShellMediaPayload(string output)
    {
        try
        {
            return JsonSerializer.Deserialize<PowerShellMediaPayload>(output.Trim()) ?? new PowerShellMediaPayload();
        }
        catch
        {
            return new PowerShellMediaPayload();
        }
    }

    private sealed record PowerShellMediaPayload
    {
        public int PhysicalDiskCount { get; init; }
        public int[] MediaTypes { get; init; } = [];
    }

    private static DriveMediaInfo Unknown(string status, string description) =>
        new("Unknown", status, description);

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { }
    }
}
```

- [ ] **Step 4: Extend `MaintenanceDrive`**

In `Services/SystemMaintenanceService.cs`, update `ToMaintenanceDrive` and the `MaintenanceDrive` record.

Replace the return block in `ToMaintenanceDrive` with:

```csharp
DriveMediaInfo media = !isReady
    ? new DriveMediaInfo("Unknown", "Unsupported", "The drive is not ready.")
    : drive.DriveType == DriveType.Removable
        ? DriveMediaTypeDetector.Removable()
        : DriveMediaTypeDetector.DetectForDriveRoot(drive.Name);

return new MaintenanceDrive(
    drive.Name,
    string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})",
    drive.Name,
    drive.DriveType.ToString(),
    isReady ? drive.DriveFormat : "Unavailable",
    totalSizeBytes,
    FormatFileSize(totalSizeBytes),
    freeSpaceBytes,
    FormatFileSize(freeSpaceBytes),
    isReady,
    media.mediaType,
    media.mediaDetectionStatus,
    media.mediaDescription);
```

Update the `MaintenanceDrive` record near the bottom of the file to include:

```csharp
internal sealed record MaintenanceDrive(
    string id,
    string name,
    string rootPath,
    string driveType,
    string driveFormat,
    long totalSizeBytes,
    string totalSizeDisplay,
    long freeSpaceBytes,
    string freeSpaceDisplay,
    bool isReady,
    string mediaType,
    string mediaDetectionStatus,
    string mediaDescription);
```

- [ ] **Step 5: Run tests**

Run:

```powershell
dotnet test --project ..\FileLocker.Tests\FileLocker.Tests.csproj -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:SkipFrontendBuild=true -- --filter-class FileLocker.Tests.SystemMaintenanceServiceTests
```

Expected: PASS for the new classification tests and existing `SystemMaintenanceServiceTests`.

- [ ] **Step 6: Commit**

```powershell
git add Services\DriveMediaTypeDetector.cs Services\SystemMaintenanceService.cs ..\FileLocker.Tests\SystemMaintenanceServiceTests.cs
git commit -m "feat: add conservative drive media detection"
```

---

### Task 2: Add Free-Space Wipe State Model and Pass Parser

**Files:**
- Create: `Services/FreeSpaceWipeOperationService.cs`
- Test: `../FileLocker.Tests/SystemMaintenanceServiceTests.cs`

- [ ] **Step 1: Write failing parser and estimate tests**

Append:

```csharp
[Theory]
[InlineData("Writing 0x00", "Zeros", 20)]
[InlineData("Writing 0xFF", "Ones", 55)]
[InlineData("Writing Random Numbers", "Random", 85)]
public void ParseCipherPass_RecognizesEnglishCipherPasses(string line, string expectedPass, double expectedPercent)
{
    FreeSpaceWipeProgress progress = FreeSpaceWipeOperationService.ParseCipherProgress(line, "operation-1", "D:\\");

    Assert.Equal(expectedPass, progress.pass);
    Assert.Equal(expectedPercent, progress.percent);
}

[Fact]
public void ParseCipherPass_FallsBackForLocalizedOrUnexpectedOutput()
{
    FreeSpaceWipeProgress progress = FreeSpaceWipeOperationService.ParseCipherProgress("Schreibe freie Speicherbereiche", "operation-1", "D:\\");

    Assert.Equal("Unknown", progress.pass);
    Assert.Equal(0, progress.percent);
    Assert.Contains("Running", progress.status, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public void EstimateWipeDuration_UsesWideHddRange()
{
    FreeSpaceWipeEstimate estimate = FreeSpaceWipeOperationService.EstimateWipe(412L * 1024 * 1024 * 1024, "HDD");

    Assert.Equal(412L * 1024 * 1024 * 1024 * 3, estimate.estimatedWriteBytes);
    Assert.Contains("2", estimate.durationDisplay, StringComparison.Ordinal);
    Assert.Contains("5", estimate.durationDisplay, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run the same filtered `SystemMaintenanceServiceTests` command from Task 1.

Expected: build fails because `FreeSpaceWipeOperationService`, `FreeSpaceWipeProgress`, and `FreeSpaceWipeEstimate` do not exist.

- [ ] **Step 3: Add wipe status records and pure helpers**

Create `Services/FreeSpaceWipeOperationService.cs` with the model and helpers first:

```csharp
using System.Diagnostics;
using System.Text;

namespace FileLocker;

internal sealed record FreeSpaceWipeEstimate(long estimatedWriteBytes, string writeDisplay, string durationDisplay);

internal sealed record FreeSpaceWipeProgress(
    string operationId,
    string driveRoot,
    string state,
    string pass,
    double percent,
    string status,
    string output,
    DateTime startedAtUtc,
    DateTime? completedAtUtc,
    string cleanupStatus,
    string message);

internal sealed record FreeSpaceWipeStartResult(string operationId, FreeSpaceWipeProgress status);

internal static class FreeSpaceWipeOperationService
{
    internal static FreeSpaceWipeProgress CreateInitialStatus(string operationId, string driveRoot) =>
        new(operationId, driveRoot, "Running", "Unknown", 0, "Starting Windows cipher", "", DateTime.UtcNow, null, "notNeeded", "Free-space wipe started.");

    internal static FreeSpaceWipeProgress ParseCipherProgress(string line, string operationId, string driveRoot)
    {
        string normalized = line.Trim();
        if (normalized.Contains("0x00", StringComparison.OrdinalIgnoreCase))
        {
            return CreateParsedProgress(operationId, driveRoot, "Zeros", 20, "Pass 1 of 3: writing zeros", normalized);
        }

        if (normalized.Contains("0xFF", StringComparison.OrdinalIgnoreCase))
        {
            return CreateParsedProgress(operationId, driveRoot, "Ones", 55, "Pass 2 of 3: writing 0xFF", normalized);
        }

        if (normalized.Contains("Random", StringComparison.OrdinalIgnoreCase))
        {
            return CreateParsedProgress(operationId, driveRoot, "Random", 85, "Pass 3 of 3: writing random data", normalized);
        }

        return CreateParsedProgress(operationId, driveRoot, "Unknown", 0, "Running Windows cipher", normalized);
    }

    internal static FreeSpaceWipeEstimate EstimateWipe(long freeSpaceBytes, string mediaType)
    {
        long writeBytes = Math.Max(0, freeSpaceBytes) * 3;
        string duration = string.Equals(mediaType, "HDD", StringComparison.OrdinalIgnoreCase)
            ? EstimateHddDuration(freeSpaceBytes)
            : "Rough estimate unavailable";

        return new FreeSpaceWipeEstimate(writeBytes, SystemMaintenanceService.FormatFileSizeForDisplay(writeBytes), duration);
    }

    private static FreeSpaceWipeProgress CreateParsedProgress(string operationId, string driveRoot, string pass, double percent, string status, string output) =>
        new(operationId, driveRoot, "Running", pass, percent, status, output, DateTime.UtcNow, null, "notNeeded", status);

    private static string EstimateHddDuration(long freeSpaceBytes)
    {
        double gib = freeSpaceBytes / 1024d / 1024d / 1024d;
        int lowHours = Math.Max(1, (int)Math.Floor((gib * 3) / 220d));
        int highHours = Math.Max(lowHours + 1, (int)Math.Ceiling((gib * 3) / 80d));
        return $"~{lowHours}-{highHours}+ hours";
    }
}
```

Add this display helper to `SystemMaintenanceService.cs` next to existing size formatting:

```csharp
internal static string FormatFileSizeForDisplay(long bytes) => FormatFileSize(bytes);
```

- [ ] **Step 4: Run tests**

Run:

```powershell
dotnet test --project ..\FileLocker.Tests\FileLocker.Tests.csproj -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:SkipFrontendBuild=true -- --filter-class FileLocker.Tests.SystemMaintenanceServiceTests
```

Expected: PASS for parser and estimate tests.

- [ ] **Step 5: Commit**

```powershell
git add Services\FreeSpaceWipeOperationService.cs Services\SystemMaintenanceService.cs ..\FileLocker.Tests\SystemMaintenanceServiceTests.cs
git commit -m "feat: model free-space wipe progress"
```

---

### Task 3: Implement Host-Owned Start, Status, and Cancel

**Files:**
- Modify: `Services/FreeSpaceWipeOperationService.cs`
- Modify: `MainWindow/Web/MainWindow.WebView.cs`
- Test: `../FileLocker.Tests/SystemMaintenanceServiceTests.cs`

- [ ] **Step 1: Write failing cancellation-status test**

Append:

```csharp
[Fact]
public void CreateCancelledStatus_ReportsIncompleteWipeAndCleanupStatus()
{
    FreeSpaceWipeProgress running = FreeSpaceWipeOperationService.CreateInitialStatus("operation-1", "D:\\");

    FreeSpaceWipeProgress cancelled = FreeSpaceWipeOperationService.CreateCancelledStatus(running, "cleanupFailed", "Residual cipher temporary files may remain.");

    Assert.Equal("Cancelled", cancelled.state);
    Assert.Equal("cleanupFailed", cancelled.cleanupStatus);
    Assert.Contains("incomplete", cancelled.message, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("Residual", cancelled.message, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run the filtered `SystemMaintenanceServiceTests` command.

Expected: build fails because `CreateCancelledStatus` does not exist.

- [ ] **Step 3: Add status helpers and streaming runner**

In `Services/FreeSpaceWipeOperationService.cs`, add:

```csharp
internal static FreeSpaceWipeProgress CreateCompletedStatus(FreeSpaceWipeProgress current, int exitCode, string output)
{
    string state = exitCode == 0 ? "Completed" : "Failed";
    string message = exitCode == 0
        ? $"Deleted-file traces in free space on {current.driveRoot} were overwritten."
        : $"Free-space wipe exited with code {exitCode} for {current.driveRoot}.";

    return current with
    {
        state = state,
        percent = exitCode == 0 ? 100 : current.percent,
        status = state,
        output = SystemMaintenanceService.NormalizeMaintenanceToolOutput(output),
        completedAtUtc = DateTime.UtcNow,
        message = message
    };
}

internal static FreeSpaceWipeProgress CreateCancelledStatus(FreeSpaceWipeProgress current, string cleanupStatus, string cleanupMessage) =>
    current with
    {
        state = "Cancelled",
        status = "Wipe incomplete",
        completedAtUtc = DateTime.UtcNow,
        cleanupStatus = cleanupStatus,
        message = string.IsNullOrWhiteSpace(cleanupMessage)
            ? $"Wipe incomplete for {current.driveRoot}."
            : $"Wipe incomplete for {current.driveRoot}. {cleanupMessage}"
    };

internal static FreeSpaceWipeProgress CreateFailedStatus(FreeSpaceWipeProgress current, string message) =>
    current with
    {
        state = "Failed",
        status = "Failed",
        completedAtUtc = DateTime.UtcNow,
        message = string.IsNullOrWhiteSpace(message) ? $"Free-space wipe failed for {current.driveRoot}." : message
    };

internal static async Task<FreeSpaceWipeProgress> RunCipherAsync(
    string operationId,
    string driveRoot,
    Action<FreeSpaceWipeProgress> onProgress,
    CancellationToken cancellationToken)
{
    FreeSpaceWipeProgress current = CreateInitialStatus(operationId, driveRoot);
    onProgress(current);
    IReadOnlySet<string> rootSnapshot = SnapshotRootEntries(driveRoot);

    var output = new StringBuilder();
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "cipher.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        },
        EnableRaisingEvents = true
    };
    process.StartInfo.ArgumentList.Add($"/w:{driveRoot}");

    try
    {
        if (!process.Start())
        {
            return CreateFailedStatus(current, "Unable to start cipher.exe.");
        }
    }
    catch (Exception ex)
    {
        return CreateFailedStatus(current, $"Unable to start cipher.exe. {ex.Message}");
    }

    Task readOutput = ReadLinesAsync(process.StandardOutput, line =>
    {
        output.AppendLine(line);
        current = ParseCipherProgress(line, operationId, driveRoot) with
        {
            startedAtUtc = current.startedAtUtc,
            output = SystemMaintenanceService.NormalizeMaintenanceToolOutput(output.ToString())
        };
        onProgress(current);
    }, cancellationToken);

    Task readError = ReadLinesAsync(process.StandardError, line => output.AppendLine(line), cancellationToken);

    try
    {
        await process.WaitForExitAsync(cancellationToken);
    }
    catch (OperationCanceledException)
    {
        TryKill(process);
        await IgnoreCancellation(readOutput);
        await IgnoreCancellation(readError);
        string cleanupStatus = TryCleanupCipherArtifacts(driveRoot, rootSnapshot, current.startedAtUtc, out string cleanupMessage);
        return CreateCancelledStatus(current, cleanupStatus, cleanupMessage);
    }

    await Task.WhenAll(readOutput, readError);
    return CreateCompletedStatus(current, process.ExitCode, output.ToString());
}

private static async Task ReadLinesAsync(StreamReader reader, Action<string> onLine, CancellationToken cancellationToken)
{
    while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
    {
        string? line = await reader.ReadLineAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(line))
        {
            onLine(line);
        }
    }
}

private static Task IgnoreCancellation(Task task)
{
    return task.ContinueWith(
        _ => { },
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
}

private static IReadOnlySet<string> SnapshotRootEntries(string driveRoot)
{
    try
    {
        return Directory.EnumerateFileSystemEntries(driveRoot)
            .Select(NormalizeRootEntry)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
    catch
    {
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
}

private static string TryCleanupCipherArtifacts(string driveRoot, IReadOnlySet<string> rootSnapshot, DateTime startedAtUtc, out string message)
{
    var deleted = 0;
    var failed = 0;
    var failures = new List<string>();
    List<string> candidates;

    try
    {
        candidates = Directory.EnumerateDirectories(driveRoot)
            .Where(path => !rootSnapshot.Contains(NormalizeRootEntry(path)))
            .Where(path => Directory.GetCreationTimeUtc(path) >= startedAtUtc.AddMinutes(-2) || Directory.GetLastWriteTimeUtc(path) >= startedAtUtc.AddMinutes(-2))
            .Where(LooksLikeCipherWorkingDirectory)
            .ToList();
    }
    catch (Exception ex)
    {
        message = $"FileLocker could not inspect the drive root for residual cipher files. {ex.Message}";
        return "unknown";
    }

    if (candidates.Count == 0)
    {
        message = "No recognizable cipher temporary folder was found. Windows may have cleaned it already, or FileLocker could not identify the folder safely.";
        return "unknown";
    }

    foreach (string candidate in candidates)
    {
        try
        {
            Directory.Delete(candidate, recursive: true);
            deleted++;
        }
        catch (Exception ex)
        {
            failed++;
            failures.Add($"{Path.GetFileName(candidate)}: {ex.Message}");
        }
    }

    if (failed > 0)
    {
        message = $"Deleted {deleted} residual cipher folder(s), but {failed} cleanup attempt(s) failed. {string.Join(" ", failures.Take(2))}";
        return "cleanupFailed";
    }

    message = $"Deleted {deleted} residual cipher temporary folder(s).";
    return "cleanupSucceeded";
}

private static bool LooksLikeCipherWorkingDirectory(string path)
{
    string name = Path.GetFileName(path);
    return name.StartsWith("EFSTMP", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("cipher", StringComparison.OrdinalIgnoreCase);
}

private static string NormalizeRootEntry(string path)
{
    return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

private static void TryKill(Process process)
{
    try { process.Kill(entireProcessTree: true); }
    catch { }
}
```

- [ ] **Step 4: Add bridge actions and host-owned state**

In `MainWindow/Web/MainWindow.WebView.cs`, add private fields near other fields:

```csharp
private readonly object _freeSpaceWipeLock = new();
private FreeSpaceWipeProgress? _freeSpaceWipeStatus;
private CancellationTokenSource? _freeSpaceWipeCancellation;
private Task? _freeSpaceWipeTask;
```

Add dispatch cases:

```csharp
"maintenance.startWipeFreeSpace" => StartWipeFreeSpaceFromBridge(ReadPayload<MaintenanceDriveActionRequest>(request.Payload)),
"maintenance.getWipeFreeSpaceStatus" => Task.FromResult<object?>(_freeSpaceWipeStatus),
"maintenance.cancelWipeFreeSpace" => Task.FromResult<object?>(CancelWipeFreeSpaceFromBridge()),
```

Add methods:

```csharp
private Task<object?> StartWipeFreeSpaceFromBridge(MaintenanceDriveActionRequest request)
{
    string root = SystemMaintenanceService.NormalizeDriveRoot(request.DriveRoot);
    SystemMaintenanceService.RequireAdministratorForBridge("Free-space wiping");
    if (!string.Equals(request.Confirmation, "WIPE FREE SPACE", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Confirm the free-space wipe before starting.");
    }

    lock (_freeSpaceWipeLock)
    {
        if (_freeSpaceWipeTask is { IsCompleted: false })
        {
            throw new InvalidOperationException("A free-space wipe is already running.");
        }

        string operationId = Guid.NewGuid().ToString("N");
        _freeSpaceWipeCancellation = new CancellationTokenSource();
        _freeSpaceWipeStatus = FreeSpaceWipeOperationService.CreateInitialStatus(operationId, root);
        PostFreeSpaceWipeStatus(_freeSpaceWipeStatus);
        _freeSpaceWipeTask = Task.Run(async () =>
        {
            try
            {
                FreeSpaceWipeProgress final = await FreeSpaceWipeOperationService.RunCipherAsync(
                    operationId,
                    root,
                    status =>
                    {
                        _freeSpaceWipeStatus = status;
                        PostFreeSpaceWipeStatus(status);
                    },
                    _freeSpaceWipeCancellation.Token);
                _freeSpaceWipeStatus = final;
                PostFreeSpaceWipeStatus(final);
            }
            catch (Exception ex)
            {
                FreeSpaceWipeProgress failed = FreeSpaceWipeOperationService.CreateFailedStatus(
                    _freeSpaceWipeStatus ?? FreeSpaceWipeOperationService.CreateInitialStatus(operationId, root),
                    $"Free-space wipe failed for {root}. {ex.Message}");
                _freeSpaceWipeStatus = failed;
                PostFreeSpaceWipeStatus(failed);
            }
        });

        return Task.FromResult<object?>(_freeSpaceWipeStatus);
    }
}

private object? CancelWipeFreeSpaceFromBridge()
{
    lock (_freeSpaceWipeLock)
    {
        _freeSpaceWipeCancellation?.Cancel();
        return _freeSpaceWipeStatus;
    }
}

private void PostFreeSpaceWipeStatus(FreeSpaceWipeProgress status)
{
    PostBridgeEvent(new { type = "maintenanceWipeStatus", status });
}
```

Add this internal wrapper in `SystemMaintenanceService.cs` because `RequireAdministrator` is private today:

```csharp
internal static void RequireAdministratorForBridge(string operationName) => RequireAdministrator(operationName);
```

- [ ] **Step 5: Run tests**

Run:

```powershell
dotnet test --project ..\FileLocker.Tests\FileLocker.Tests.csproj -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:SkipFrontendBuild=true -- --filter-class FileLocker.Tests.SystemMaintenanceServiceTests
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add Services\FreeSpaceWipeOperationService.cs Services\SystemMaintenanceService.cs MainWindow\Web\MainWindow.WebView.cs ..\FileLocker.Tests\SystemMaintenanceServiceTests.cs
git commit -m "feat: stream free-space wipe status"
```

---

### Task 4: Add Frontend Bridge Types and App State

**Files:**
- Modify: `frontend/src/types/bridge.ts`
- Modify: `frontend/src/services/bridge.ts`
- Modify: `frontend/src/App.tsx`

- [ ] **Step 1: Add TypeScript bridge types**

In `frontend/src/types/bridge.ts`, add:

```ts
export type DriveMediaType = "HDD" | "SSD" | "Removable" | "Mixed" | "Unknown"

export type MaintenanceDrive = {
  id: string
  name: string
  rootPath: string
  driveType: string
  driveFormat: string
  totalSizeBytes: number
  totalSizeDisplay: string
  freeSpaceBytes: number
  freeSpaceDisplay: string
  isReady: boolean
  mediaType: DriveMediaType
  mediaDetectionStatus: string
  mediaDescription: string
}

export type FreeSpaceWipeStatus = {
  operationId: string
  driveRoot: string
  state: "Running" | "Completed" | "Failed" | "Cancelled" | "TimedOut"
  pass: "Unknown" | "Zeros" | "Ones" | "Random"
  percent: number
  status: string
  output: string
  startedAtUtc: string
  completedAtUtc?: string | null
  cleanupStatus: "cleanupSucceeded" | "cleanupFailed" | "notNeeded" | "unknown"
  message: string
}

export type MaintenanceWipeStatusEvent = {
  type: "maintenanceWipeStatus"
  status: FreeSpaceWipeStatus
}
```

Update `BridgeEvent`:

```ts
export type BridgeEvent = ProgressEvent | DroppedPathsEvent | DropErrorEvent | UpdateAvailableEvent | MaintenanceWipeStatusEvent
```

- [ ] **Step 2: Route the new event type**

In `frontend/src/services/bridge.ts`, include the event:

```ts
if (
  message.type === "progress" ||
  message.type === "droppedPaths" ||
  message.type === "dropError" ||
  message.type === "updateAvailable" ||
  message.type === "maintenanceWipeStatus"
) {
  notifyBridgeListeners(message as BridgeEvent)
  return
}
```

- [ ] **Step 3: Store wipe status in App**

In `frontend/src/App.tsx`, import the type:

```ts
import type { DashboardState, EncryptionAlgorithmOption, FreeSpaceWipeStatus, InitialState, PageKey, ProgressEvent, SettingsState, UpdateCheckResult } from "@/types/bridge"
```

Add state:

```ts
const [freeSpaceWipeStatus, setFreeSpaceWipeStatus] = useState<FreeSpaceWipeStatus | null>(null)
```

In the bridge event listener:

```ts
if (event.type === "maintenanceWipeStatus") {
  setFreeSpaceWipeStatus(event.status)
}
```

Pass it to Partition Cleaner:

```tsx
{activePage === "partition-cleaner" ? <PartitionCleanerPage invoke={invokeBridge} isAdministrator={initialState.app.isAdministrator} onRestartAsAdministrator={() => void restartAsAdministrator("partition-cleaner")} wipeStatus={freeSpaceWipeStatus} onWipeStatusChange={setFreeSpaceWipeStatus} /> : null}
```

- [ ] **Step 4: Run frontend build to expose type errors**

Run:

```powershell
npm run build
```

from `frontend`.

Expected: FAIL because `PartitionCleanerPage` props have not been updated yet.

- [ ] **Step 5: Commit after Task 5, not now**

Do not commit this task alone if TypeScript fails. Continue directly to Task 5 and commit the frontend bridge and page changes together.

---

### Task 5: Replace Partition Cleaner UI With Guided Inspector

**Files:**
- Modify: `frontend/src/pages/SystemMaintenancePages.tsx`
- Modify: `frontend/src/App.tsx`
- Modify: `frontend/src/types/bridge.ts`
- Modify: `frontend/src/services/bridge.ts`

- [ ] **Step 1: Update page props and drive type usage**

Import bridge types in `SystemMaintenancePages.tsx`:

```ts
import type {
  AppLeftoverCleanResult,
  AppLeftoverScanResult,
  FreeSpaceWipeStatus,
  InstalledApp,
  InstalledAppsScanResult,
  MaintenanceDrive as BridgeMaintenanceDrive,
  StartupItem,
  StartupExportResult,
  StartupIgnoreResult,
  StartupOpenLocationResult,
  StartupScanResult,
  StartupRestoreRecord,
  StartupToggleResult,
  UninstallerLaunchResult,
} from "@/types/bridge"
```

Replace the local `MaintenanceDrive` type with:

```ts
type MaintenanceDrive = BridgeMaintenanceDrive
```

Extend props:

```ts
type PartitionCleanerPageProps = MaintenancePageProps & {
  wipeStatus: FreeSpaceWipeStatus | null
  onWipeStatusChange: (status: FreeSpaceWipeStatus | null) => void
}
```

Change the function signature:

```ts
export function PartitionCleanerPage({ invoke, isAdministrator, onRestartAsAdministrator, wipeStatus, onWipeStatusChange }: PartitionCleanerPageProps) {
```

Add cancel confirmation state next to the existing wipe confirmation state:

```ts
const [showCancelConfirmation, setShowCancelConfirmation] = useState(false)
```

- [ ] **Step 2: Switch to start/status/cancel bridge actions**

Replace `runWipe` with:

```ts
async function runWipe() {
  if (isRunning) return
  if (!isAdministrator) { toast.error("Restart FileLocker as administrator before starting a free-space wipe."); return }
  if (!selectedDrive) { toast.error("Select a drive first."); return }
  if (!selectedDrive.isReady) { toast.error("Select a ready drive before starting a free-space wipe."); return }
  setResult(null)
  setWipeError("")
  try {
    const response = await invoke<FreeSpaceWipeStatus>("maintenance.startWipeFreeSpace", {
      driveRoot: selectedDrive.rootPath,
      confirmation: "WIPE FREE SPACE",
    })
    onWipeStatusChange(response)
    toast.message("Free-space wipe started.")
  } catch (error) {
    setWipeError(showMaintenanceError(error, "Free-space wipe failed to start."))
  }
}
```

Add:

```ts
function requestCancelWipe() {
  if (!isRunning) return
  setShowCancelConfirmation(true)
}

async function cancelWipe() {
  setShowCancelConfirmation(false)
  try {
    const response = await invoke<FreeSpaceWipeStatus | null>("maintenance.cancelWipeFreeSpace", {})
    onWipeStatusChange(response)
    toast.warning("Free-space wipe cancellation requested.")
  } catch (error) {
    toast.error(error instanceof Error ? error.message : "Unable to cancel free-space wipe.")
  }
}
```

On mount, reattach:

```ts
useEffect(() => {
  void invoke<FreeSpaceWipeStatus | null>("maintenance.getWipeFreeSpaceStatus", {})
    .then(onWipeStatusChange)
    .catch(() => undefined)
}, [invoke, onWipeStatusChange])
```

Set:

```ts
const activeWipe = wipeStatus?.state === "Running" ? wipeStatus : null
const isRunning = Boolean(activeWipe)
```

- [ ] **Step 3: Add local display helpers**

Add below `PartitionCleanerPage`:

```ts
function getPartitionStep(selectedDrive: MaintenanceDrive | null, wipeStatus: FreeSpaceWipeStatus | null): MaintenanceStep {
  if (wipeStatus?.state === "Running") return "apply"
  if (wipeStatus && wipeStatus.state !== "Running") return "apply"
  return selectedDrive ? "review" : "scan"
}

function getDriveMediaGuidance(drive: MaintenanceDrive | null) {
  if (!drive) return { tone: "neutral" as const, title: "No drive selected", body: "Select a ready drive to review free-space wipe behavior." }
  if (drive.mediaType === "HDD") return { tone: "good" as const, title: "Good fit for this drive", body: "Free-space overwrite works as intended on traditional hard drives." }
  if (drive.mediaType === "SSD") return { tone: "warning" as const, title: "Limited benefit on SSD", body: "TRIM and wear leveling can prevent direct overwrite of deleted data and add write wear." }
  if (drive.mediaType === "Removable") return { tone: "warning" as const, title: "Limited benefit on removable media", body: "USB flash storage may use wear leveling, so results can vary and writes add wear." }
  if (drive.mediaType === "Mixed") return { tone: "warning" as const, title: "Mixed storage backing", body: "This volume maps to multiple physical media types, so FileLocker cannot make a precise recommendation." }
  return { tone: "neutral" as const, title: "Media type unknown", body: drive.mediaDescription || "FileLocker could not confidently identify the underlying storage media." }
}

function estimateWipeWriteDisplay(drive: MaintenanceDrive | null) {
  if (!drive) return "Unknown"
  return formatBytes(drive.freeSpaceBytes * 3)
}

function estimateWipeDurationDisplay(drive: MaintenanceDrive | null) {
  if (!drive) return "Unknown"
  if (drive.mediaType !== "HDD") return "Rough estimate unavailable"
  const gib = drive.freeSpaceBytes / 1024 / 1024 / 1024
  const low = Math.max(1, Math.floor((gib * 3) / 220))
  const high = Math.max(low + 1, Math.ceil((gib * 3) / 80))
  return `~${low}-${high}+ hours`
}

function getPartitionDriveStatus(drive: MaintenanceDrive, selected: boolean, wipeStatus: FreeSpaceWipeStatus | null) {
  if (!drive.isReady) return "Unsupported"
  if (wipeStatus?.state === "Running" && wipeStatus.driveRoot === drive.rootPath) return "Running"
  if (drive.mediaType === "SSD" || drive.mediaType === "Removable") return "Limited"
  if (drive.mediaType === "Mixed" || drive.mediaType === "Unknown") return "Unknown media"
  return selected ? "Selected" : "Ready"
}

function getPartitionRiskLabel(drive: MaintenanceDrive | null, wipeStatus: FreeSpaceWipeStatus | null) {
  if (wipeStatus?.state === "Running") return "In progress"
  if (!drive) return "No target"
  if (!drive.isReady) return "Unavailable"
  if (drive.mediaType === "HDD") return "Low"
  if (drive.mediaType === "SSD" || drive.mediaType === "Removable") return "Limited benefit"
  return "Unknown"
}

function getMediaGuidanceClass(tone: "neutral" | "good" | "warning") {
  if (tone === "good") return "border-accent-green/35 bg-accent-green/8 text-accent-green"
  if (tone === "warning") return "border-amber-400/35 bg-amber-400/10 text-amber-100"
  return "border-border bg-bg-subtle text-secondary"
}

function getWipeElapsedDisplay(status: FreeSpaceWipeStatus | null) {
  if (!status) return "Not run"
  const started = Date.parse(status.startedAtUtc)
  const ended = status.completedAtUtc ? Date.parse(status.completedAtUtc) : Date.now()
  if (!Number.isFinite(started) || !Number.isFinite(ended) || ended < started) return "Unknown"
  const seconds = Math.max(0, Math.round((ended - started) / 1000))
  const minutes = Math.floor(seconds / 60)
  const remainingSeconds = seconds % 60
  return minutes > 0 ? `${minutes}m ${remainingSeconds}s` : `${remainingSeconds}s`
}

function getWipePassSummary(status: FreeSpaceWipeStatus | null) {
  if (!status) return "No passes completed"
  if (status.state === "Completed") return "3 of 3 passes completed"
  if (status.pass === "Random") return "Pass 3 of 3"
  if (status.pass === "Ones") return "Pass 2 of 3"
  if (status.pass === "Zeros") return "Pass 1 of 3"
  return "Pass tracking unavailable"
}
```

- [ ] **Step 4: Replace the JSX**

Replace the current return body inside `PartitionCleanerPage` with a guided layout using these local components:

```tsx
return (
  <MaintenanceFrame>
    <section className="section-surface">
      <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2.5">
            <HardDrive className="size-4 text-muted" aria-hidden />
            <h2 className="font-display text-base font-semibold tracking-tight text-primary">Free-Space Sanitizer</h2>
            {isAdministrator ? <Badge variant="outline" className="border-accent-green/35 bg-accent-green/10 text-accent-green">Running as administrator</Badge> : <Badge variant="outline" className="border-amber-400/35 bg-amber-400/10 text-amber-200">Administrator required</Badge>}
          </div>
          <p className="mt-2 max-w-3xl text-sm text-secondary">Overwrite unused space so already-deleted files on the selected drive are harder to recover.</p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {!isAdministrator ? <Button variant="outline" onClick={onRestartAsAdministrator}><ShieldAlert data-icon="inline-start" />Restart as Administrator</Button> : null}
          <Button variant="outline" onClick={refreshDrives} disabled={isLoading || isRunning}><RefreshCcw data-icon="inline-start" />Refresh Drives</Button>
        </div>
      </div>
      <ScanStepIndicator current={getPartitionStep(selectedDrive, wipeStatus)} applyLabel={wipeStatus?.state === "Running" ? "Wipe free space" : "Report"} className="mt-4" />
    </section>

    <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_370px]">
      <PartitionDriveTable drives={drives} selectedDriveRoot={selectedDriveRoot} wipeStatus={wipeStatus} isBusy={isLoading || isRunning} loadError={driveLoadError} onSelect={selectDrive} onRefresh={refreshDrives} />
      <PartitionReviewPanel drive={selectedDrive} driveCount={drives.length} wipeStatus={wipeStatus} isAdministrator={isAdministrator} canStart={canStart} isRunning={isRunning} onStart={requestWipe} onCancel={requestCancelWipe} onRestartAsAdministrator={onRestartAsAdministrator} />
    </div>

    <PartitionRunStatus status={wipeStatus} errorMessage={wipeError} />

    <MaintenanceConfirmDialog
      open={showWipeConfirmation}
      onOpenChange={setShowWipeConfirmation}
      title="Wipe free space?"
      description={`This will run Windows cipher against ${selectedDrive?.rootPath ?? "the selected drive"}. Existing files are kept, but the operation can run for a long time and writes temporary data into free space.`}
      confirmLabel="Wipe Free Space"
      onConfirm={() => void runWipe()}
      onDontShowAgain={() => { setSkipWipeConfirmation(true); void runWipe() }}
      isBusy={isRunning}
    />

    <AlertDialog open={showCancelConfirmation} onOpenChange={setShowCancelConfirmation}>
      <AlertDialogContent className="sm:max-w-md">
        <AlertDialogHeader>
          <AlertDialogTitle>Cancel free-space wipe?</AlertDialogTitle>
          <AlertDialogDescription className="leading-[1.6]">
            Windows cipher will be interrupted and the selected drive will be only partially processed. FileLocker will attempt to clean up residual temporary fill files and report the cleanup result.
          </AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>Keep Running</AlertDialogCancel>
          <AlertDialogAction variant="secondary" onClick={() => void cancelWipe()}>Cancel Wipe</AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  </MaintenanceFrame>
)
```

Add these local components and badge helpers below `PartitionCleanerPage`:

```tsx
type PartitionDriveTableProps = {
  drives: MaintenanceDrive[]
  selectedDriveRoot: string
  wipeStatus: FreeSpaceWipeStatus | null
  isBusy: boolean
  loadError: string
  onSelect: (root: string) => void
  onRefresh: () => void
}

function PartitionDriveTable({ drives, selectedDriveRoot, wipeStatus, isBusy, loadError, onSelect, onRefresh }: PartitionDriveTableProps) {
  return (
    <section className="section-surface overflow-hidden">
      <div className="flex items-center justify-between gap-3">
        <div className="flex items-center gap-2.5">
          <HardDrive className="size-4 shrink-0 text-muted" aria-hidden />
          <span className="font-display text-sm font-semibold tracking-tight text-primary">Partitions</span>
        </div>
        <Button variant="outline" size="sm" onClick={onRefresh} disabled={isBusy}>
          <RefreshCcw data-icon="inline-start" />
          Refresh
        </Button>
      </div>

      {loadError ? (
        <div className="app-inline-notice app-inline-notice-warning mt-3">
          <AlertTriangle className="mt-0.5 size-4 shrink-0 text-amber-400" aria-hidden />
          <span>{loadError}</span>
        </div>
      ) : null}

      <div className="mt-3 overflow-x-auto border-y border-border">
        <table className="w-full min-w-[700px] border-collapse text-sm">
          <thead className="bg-bg-subtle/70 text-xs text-muted">
            <tr className="[&>th]:px-3 [&>th]:py-2 [&>th]:text-left [&>th]:font-medium">
              <th>Drive</th>
              <th>Media</th>
              <th>Free</th>
              <th>Format</th>
              <th>Status</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-border">
            {drives.map((drive) => {
              const selected = drive.rootPath.toLowerCase() === selectedDriveRoot.toLowerCase()
              const status = getPartitionDriveStatus(drive, selected, wipeStatus)
              return (
                <tr
                  key={drive.id}
                  className={cn(
                    "transition-colors [&>td]:px-3 [&>td]:py-2.5",
                    selected && "bg-accent/8",
                    !drive.isReady && "text-secondary"
                  )}
                >
                  <td>
                    <button
                      type="button"
                      className="flex w-full min-w-0 flex-col text-left disabled:cursor-not-allowed"
                      onClick={() => onSelect(drive.rootPath)}
                      disabled={isBusy || !drive.isReady}
                    >
                      <span className="truncate font-medium text-primary">{drive.name}</span>
                      <span className="text-xs text-muted">{drive.rootPath}</span>
                    </button>
                  </td>
                  <td><PartitionPill label={drive.mediaType} tone={drive.mediaType === "HDD" ? "good" : drive.mediaType === "SSD" || drive.mediaType === "Removable" ? "warning" : "neutral"} /></td>
                  <td>
                    <div className="font-medium text-primary">{drive.freeSpaceDisplay}</div>
                    <div className="mt-1 h-1.5 w-28 overflow-hidden rounded-sm bg-border/70">
                      <div className="h-full bg-accent/70" style={{ width: `${getDriveUsedPercent(drive)}%` }} />
                    </div>
                  </td>
                  <td className="text-secondary">{drive.driveFormat}</td>
                  <td><PartitionPill label={status} tone={status === "Ready" || status === "Selected" ? "good" : status === "Limited" || status === "Running" ? "warning" : "neutral"} /></td>
                </tr>
              )
            })}
          </tbody>
        </table>
        {drives.length === 0 ? (
          <div className="px-3 py-8 text-center text-sm text-secondary">
            {isBusy ? "Loading partitions..." : "No ready fixed or removable partitions were found."}
          </div>
        ) : null}
      </div>
    </section>
  )
}

type PartitionReviewPanelProps = {
  drive: MaintenanceDrive | null
  driveCount: number
  wipeStatus: FreeSpaceWipeStatus | null
  isAdministrator: boolean
  canStart: boolean
  isRunning: boolean
  onStart: () => void
  onCancel: () => void
  onRestartAsAdministrator: () => void
}

function PartitionReviewPanel({ drive, driveCount, wipeStatus, isAdministrator, canStart, isRunning, onStart, onCancel, onRestartAsAdministrator }: PartitionReviewPanelProps) {
  const guidance = getDriveMediaGuidance(drive)
  const disabledReason = !isAdministrator
    ? "Restart as administrator before starting."
    : !drive
      ? "Select a drive first."
      : !drive.isReady
        ? "This drive is not ready for Windows cipher."
        : isRunning
          ? "A free-space wipe is already running."
          : ""

  return (
    <aside className="section-surface">
      <div className="flex items-center justify-between gap-3">
        <div>
          <h3 className="font-display text-sm font-semibold tracking-tight text-primary">Review</h3>
          <p className="mt-1 text-xs text-muted">{drive ? `${drive.rootPath} selected` : "Select a partition to continue"}</p>
        </div>
        <PartitionPill label={getPartitionRiskLabel(drive, wipeStatus)} tone={drive?.mediaType === "HDD" ? "good" : drive ? "warning" : "neutral"} />
      </div>

      <div className="mt-3 grid grid-cols-2 divide-x divide-y divide-border border border-border text-sm">
        <MetricTile label="Scanned partitions" value={String(driveCount)} />
        <MetricTile label="Reclaimable space" value="0 B" />
        <MetricTile label="Selected item" value={drive ? "Free-space wipe" : "None"} />
        <MetricTile label="Estimated writes" value={estimateWipeWriteDisplay(drive)} />
      </div>

      <div className={cn("mt-3 rounded-md border px-3 py-2.5 text-sm", getMediaGuidanceClass(guidance.tone))}>
        <div className="font-medium">{guidance.title}</div>
        <p className="mt-1 leading-[1.55]">{guidance.body}</p>
      </div>

      <div className="mt-3 border-y border-border py-3 text-sm">
        <dl className="grid grid-cols-[auto,1fr] gap-x-4 gap-y-2">
          <dt className="text-muted">Existing files</dt>
          <dd className="text-right font-medium text-primary">Untouched</dd>
          <dt className="text-muted">Time estimate</dt>
          <dd className="text-right font-medium text-primary">{estimateWipeDurationDisplay(drive)}</dd>
          <dt className="text-muted">Media</dt>
          <dd className="text-right font-medium text-primary">{drive?.mediaType ?? "Unknown"}</dd>
        </dl>
      </div>

      {!isAdministrator ? (
        <div className="app-inline-notice app-inline-notice-warning mt-3">
          <ShieldAlert className="mt-0.5 size-4 shrink-0 text-amber-400" aria-hidden />
          <span>Relaunching as administrator closes the current in-page state.</span>
        </div>
      ) : null}

      {isRunning ? (
        <div className="mt-3 flex flex-col gap-2">
          <Button onClick={onCancel}>
            <AlertTriangle data-icon="inline-start" />
            Cancel Wipe
          </Button>
          <Button variant="outline" disabled>
            <Play data-icon="inline-start" />
            Wipe Free Space
          </Button>
          <p className="text-xs leading-[1.5] text-muted">Cancel interrupts Windows cipher. The wipe will be incomplete and FileLocker will report whether residual temporary files were cleaned up.</p>
        </div>
      ) : (
        <div className="mt-3 flex flex-col gap-2">
          <ActionWithReason disabled={!canStart} reason={disabledReason}>
            <Button onClick={onStart} disabled={!canStart}>
              <Play data-icon="inline-start" />
              Wipe Free Space
            </Button>
          </ActionWithReason>
          {!isAdministrator ? (
            <Button variant="outline" onClick={onRestartAsAdministrator}>
              <ShieldAlert data-icon="inline-start" />
              Restart as Administrator
            </Button>
          ) : null}
        </div>
      )}
    </aside>
  )
}

function PartitionRunStatus({ status, errorMessage }: { status: FreeSpaceWipeStatus | null; errorMessage: string }) {
  if (!status && !errorMessage) return null
  const percent = Math.max(0, Math.min(100, status?.percent ?? 0))
  const isRunning = status?.state === "Running"

  return (
    <section className="section-surface">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-2.5">
          <Download className="size-4 shrink-0 text-muted" aria-hidden />
          <span className="font-display text-sm font-semibold tracking-tight text-primary">{isRunning ? "Wipe Progress" : "Report"}</span>
        </div>
        {status ? <PartitionPill label={status.state} tone={status.state === "Completed" ? "good" : status.state === "Running" ? "warning" : "neutral"} /> : null}
      </div>

      {errorMessage ? (
        <div className="app-inline-notice app-inline-notice-warning mt-3">
          <AlertTriangle className="mt-0.5 size-4 shrink-0 text-amber-400" aria-hidden />
          <span>{errorMessage}</span>
        </div>
      ) : null}

      {status ? (
        <div className="mt-3 flex flex-col gap-3">
          <div>
            <div className="mb-1 flex items-center justify-between gap-3 text-xs">
              <span className="font-medium text-secondary">{status.status}</span>
              <span className="text-muted">{isRunning && percent === 0 ? "Tracking output" : `${Math.round(percent)}%`}</span>
            </div>
            <div className="h-2 overflow-hidden rounded-sm bg-border">
              <div className={cn("h-full bg-accent transition-all", percent === 0 && isRunning && "w-1/3 animate-pulse")} style={percent > 0 ? { width: `${percent}%` } : undefined} />
            </div>
          </div>
          <div className="grid grid-cols-2 divide-x divide-y divide-border border border-border text-sm md:grid-cols-4">
            <MetricTile label="Passes" value={getWipePassSummary(status)} />
            <MetricTile label="Elapsed" value={getWipeElapsedDisplay(status)} />
            <MetricTile label="Cleanup" value={status.cleanupStatus} />
            <MetricTile label="Drive" value={status.driveRoot} />
          </div>
          <p className="text-sm text-secondary">{status.message}</p>
          {status.output ? <pre className="terminal-output max-h-[300px]">{status.output}</pre> : null}
        </div>
      ) : null}
    </section>
  )
}

function PartitionPill({ label, tone }: { label: string; tone: "good" | "warning" | "neutral" }) {
  return (
    <Badge variant="outline" className={cn("h-6 w-fit whitespace-nowrap px-2 text-xs", getPartitionPillClass(tone))}>
      {label}
    </Badge>
  )
}

function getPartitionPillClass(tone: "good" | "warning" | "neutral") {
  if (tone === "good") return "border-accent-green/35 bg-accent-green/10 text-accent-green"
  if (tone === "warning") return "border-amber-400/35 bg-amber-400/10 text-amber-100"
  return "border-border bg-bg-subtle text-secondary"
}
```

- [ ] **Step 5: Run frontend build**

Run from `frontend`:

```powershell
npm run build
```

Expected: PASS with the snippets in Steps 1-4. Type errors in this task should be limited to mismatched imports or prop names introduced by the new Partition Cleaner components; correct those local mismatches before proceeding.

- [ ] **Step 6: Commit**

```powershell
git add frontend\src\App.tsx frontend\src\types\bridge.ts frontend\src\services\bridge.ts frontend\src\pages\SystemMaintenancePages.tsx
git commit -m "feat: rebuild partition cleaner UI"
```

---

### Task 6: Add Dev Bridge Fixtures for Media and Wipe States

**Files:**
- Modify: `frontend/src/services/devBridgeMock.ts`

- [ ] **Step 1: Import the wipe status type**

Add `FreeSpaceWipeStatus` to the existing type import from `@/types/bridge`:

```ts
import type {
  AppLeftoverScanResult,
  DashboardState,
  FreeSpaceWipeStatus,
  InitialState,
  InstalledAppsScanResult,
  SettingsState,
  StartupScanResult,
} from "@/types/bridge"
```

- [ ] **Step 2: Add mock drive media states**

Replace the `maintenance.getDrives` mock drives with:

```ts
drives: [
  { id: "C", name: "Windows", rootPath: "C:\\", driveType: "Fixed", driveFormat: "NTFS", totalSizeBytes: 511_000_000_000, totalSizeDisplay: "476 GB", freeSpaceBytes: 142_000_000_000, freeSpaceDisplay: "132 GB", isReady: true, mediaType: "SSD", mediaDetectionStatus: "Detected", mediaDescription: "Limited benefit on SSDs because TRIM and wear leveling can prevent direct overwrite of deleted data." },
  { id: "D", name: "Data", rootPath: "D:\\", driveType: "Fixed", driveFormat: "NTFS", totalSizeBytes: 2_000_000_000_000, totalSizeDisplay: "1.8 TB", freeSpaceBytes: 1_400_000_000_000, freeSpaceDisplay: "1.3 TB", isReady: true, mediaType: "HDD", mediaDetectionStatus: "Detected", mediaDescription: "Good fit for traditional hard drives." },
  { id: "E", name: "USB", rootPath: "E:\\", driveType: "Removable", driveFormat: "exFAT", totalSizeBytes: 64_000_000_000, totalSizeDisplay: "59 GB", freeSpaceBytes: 40_000_000_000, freeSpaceDisplay: "37 GB", isReady: true, mediaType: "Removable", mediaDetectionStatus: "Detected", mediaDescription: "Limited benefit on flash media." },
  { id: "R", name: "Recovery", rootPath: "R:\\", driveType: "Fixed", driveFormat: "RAW", totalSizeBytes: 0, totalSizeDisplay: "0 B", freeSpaceBytes: 0, freeSpaceDisplay: "0 B", isReady: false, mediaType: "Unknown", mediaDetectionStatus: "Unsupported", mediaDescription: "RAW or unformatted volumes are not supported by Windows cipher." },
]
```

- [ ] **Step 3: Add mock wipe lifecycle**

Near other mock state, add:

```ts
let mockWipeStatus: FreeSpaceWipeStatus | null = null
```

Add cases:

```ts
case "maintenance.startWipeFreeSpace":
  mockWipeStatus = {
    operationId: crypto.randomUUID(),
    driveRoot: typeof payload === "object" && payload && "driveRoot" in payload ? String((payload as { driveRoot?: unknown }).driveRoot) : "D:\\",
    state: "Running",
    pass: "Zeros",
    percent: 20,
    status: "Pass 1 of 3: writing zeros",
    output: "cipher /w:D:\\\nWriting 0x00 ...",
    startedAtUtc: new Date().toISOString(),
    completedAtUtc: null,
    cleanupStatus: "notNeeded",
    message: "Free-space wipe started.",
  }
  return mockWipeStatus
case "maintenance.getWipeFreeSpaceStatus":
  return mockWipeStatus
case "maintenance.cancelWipeFreeSpace":
  mockWipeStatus = mockWipeStatus ? { ...mockWipeStatus, state: "Cancelled", status: "Wipe incomplete", completedAtUtc: new Date().toISOString(), cleanupStatus: "unknown", message: "Wipe incomplete. FileLocker attempted to locate residual cipher temporary files." } : null
  return mockWipeStatus
```

- [ ] **Step 4: Run frontend build**

```powershell
npm run build
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add frontend\src\services\devBridgeMock.ts
git commit -m "test: add partition cleaner dev fixtures"
```

---

### Task 7: Full Validation

**Files:**
- No source edits expected.

- [ ] **Step 1: Run frontend build**

```powershell
Push-Location frontend
npm run build
Pop-Location
```

Expected: PASS.

- [ ] **Step 2: Run .NET build**

```powershell
dotnet build .\FileLocker.csproj -p:BaseOutputPath=bin\codex-verify\ -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SkipFrontendBuild=true -nologo
```

Expected: PASS.

- [ ] **Step 3: Run focused tests**

```powershell
dotnet test --project ..\FileLocker.Tests\FileLocker.Tests.csproj -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:SkipFrontendBuild=true -- --filter-class FileLocker.Tests.SystemMaintenanceServiceTests
```

Expected: PASS.

- [ ] **Step 4: Run broader relevant tests**

```powershell
dotnet test --project ..\FileLocker.Tests\FileLocker.Tests.csproj -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:SkipFrontendBuild=true -- --filter-class FileLocker.Tests.BridgePayloadTests
```

Expected: PASS.

- [ ] **Step 5: Manual UI check**

Start the app or Vite dev surface and verify:

- Partition Cleaner route still opens.
- Page-local title reads `Free-Space Sanitizer`.
- SSD, HDD, Removable, and RAW mock drives show distinct guidance.
- `Wipe Free Space` is disabled without admin or without a ready selected drive.
- Starting a wipe shows step 3, disables drive selection, disables `Wipe Free Space`, and shows `Cancel Wipe`.
- `Cancel Wipe` opens a confirmation that says the wipe will be incomplete.
- Cancelling shows `Wipe incomplete` and cleanup status.
- Completed or failed status shows command output only after a run.
- Other maintenance pages render without intentional changes.

- [ ] **Step 6: Final status check**

```powershell
git status --short
```

Expected: only intended changes are present, plus any pre-existing unrelated dirty files that were already in the worktree.
