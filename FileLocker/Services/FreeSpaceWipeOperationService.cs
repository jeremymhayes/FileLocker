using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
    private const string CipherTemporaryDirectoryPrefix = "EFSTMP";
    private static readonly TimeSpan CleanupTimestampTolerance = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ProcessExitAfterKillTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProcessReadDrainTimeout = TimeSpan.FromSeconds(1);

    internal static FreeSpaceWipeProgress CreateInitialStatus(string operationId, string driveRoot) =>
        new(operationId, driveRoot, "Running", "Unknown", 0, "Starting Windows cipher", string.Empty, DateTime.UtcNow, null, "notNeeded", "Free-space wipe started.");

    internal static FreeSpaceWipeProgress ParseCipherProgress(string line, string operationId, string driveRoot) =>
        ParseCipherProgress(line, operationId, driveRoot, current: null);

    internal static FreeSpaceWipeProgress ParseCipherProgress(string line, string operationId, string driveRoot, FreeSpaceWipeProgress? current)
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

        if (current is not null)
        {
            return current with
            {
                state = "Running",
                status = "Running Windows cipher",
                output = normalized,
                completedAtUtc = null,
                message = "Running Windows cipher"
            };
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

    internal static FreeSpaceWipeProgress CreateCompletedStatus(FreeSpaceWipeProgress current) =>
        current with
        {
            state = "Completed",
            percent = 100,
            status = "Completed",
            output = SystemMaintenanceService.NormalizeMaintenanceToolOutput(current.output),
            completedAtUtc = DateTime.UtcNow,
            cleanupStatus = "notNeeded",
            message = $"Deleted-file traces in free space on {current.driveRoot} were overwritten."
        };

    internal static FreeSpaceWipeProgress CreateCancelledStatus(FreeSpaceWipeProgress current, string cleanupStatus, string cleanupMessage)
    {
        string message = $"Free-space wipe for {current.driveRoot} was cancelled before completion. The wipe is incomplete.";
        if (!string.IsNullOrWhiteSpace(cleanupMessage))
        {
            message = $"{message} {cleanupMessage.Trim()}";
        }

        return current with
        {
            state = "Cancelled",
            percent = Math.Min(current.percent, 99),
            status = "Cancelled",
            output = SystemMaintenanceService.NormalizeMaintenanceToolOutput(current.output),
            completedAtUtc = DateTime.UtcNow,
            cleanupStatus = NormalizeCleanupStatus(cleanupStatus),
            message = message
        };
    }

    internal static FreeSpaceWipeProgress CreateFailedStatus(FreeSpaceWipeProgress current, string? message = null) =>
        current with
        {
            state = "Failed",
            status = "Failed",
            output = SystemMaintenanceService.NormalizeMaintenanceToolOutput(current.output),
            completedAtUtc = DateTime.UtcNow,
            cleanupStatus = "unknown",
            message = string.IsNullOrWhiteSpace(message) ? current.message : message.Trim()
        };

    internal static async Task<FreeSpaceWipeProgress> RunCipherAsync(
        string operationId,
        string driveRoot,
        Action<FreeSpaceWipeProgress> onProgress,
        CancellationToken cancellationToken)
    {
        FreeSpaceWipeProgress current = CreateInitialStatus(operationId, driveRoot);
        onProgress(current);

        RootEntrySnapshot rootSnapshot = SnapshotRootEntries(driveRoot);
        DateTime processStartedAtUtc = DateTime.UtcNow;
        var outputBuilder = new StringBuilder();
        object outputLock = new();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cipher.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }
        };
        process.StartInfo.ArgumentList.Add($"/w:{driveRoot}");

        try
        {
            if (!process.Start())
            {
                return CreateFailedStatus(current, $"Unable to start cipher.exe for {driveRoot}.");
            }
        }
        catch (Exception ex)
        {
            return CreateFailedStatus(current, $"Unable to start cipher.exe for {driveRoot}. {ex.Message}");
        }

        processStartedAtUtc = DateTime.UtcNow;
        Task stdoutTask = ReadStandardOutputAsync();
        Task stderrTask = ReadStandardErrorAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            bool exitedAfterKill = await WaitForProcessExitAfterKillAsync(process, ProcessExitAfterKillTimeout);
            await WaitForReadTasksAfterCancellationAsync(ProcessReadDrainTimeout, stdoutTask, stderrTask);

            CleanupResult cleanup = exitedAfterKill
                ? CleanupAfterCancellation(driveRoot, rootSnapshot, processStartedAtUtc)
                : new CleanupResult("unknown", "cipher.exe did not exit within 5 seconds after cancellation, so temporary cleanup was not attempted.");
            FreeSpaceWipeProgress cancelledBase = current with
            {
                output = GetNormalizedOutput()
            };
            return CreateCancelledStatus(cancelledBase, cleanup.Status, cleanup.Message);
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch (Exception ex)
        {
            FreeSpaceWipeProgress failedBase = current with
            {
                output = GetNormalizedOutput()
            };
            return CreateFailedStatus(failedBase, $"Free-space wipe failed while reading cipher output for {driveRoot}. {ex.Message}");
        }

        FreeSpaceWipeProgress finalBase = current with
        {
            output = GetNormalizedOutput()
        };

        return process.ExitCode == 0
            ? CreateCompletedStatus(finalBase)
            : CreateFailedStatus(finalBase, $"Free-space wipe exited with code {process.ExitCode} for {driveRoot}.");

        async Task ReadStandardOutputAsync()
        {
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    break;
                }

                string normalizedOutput;
                lock (outputLock)
                {
                    outputBuilder.AppendLine(line);
                    normalizedOutput = GetNormalizedOutputUnsafe();
                    FreeSpaceWipeProgress parsed = ParseCipherProgress(line, operationId, driveRoot, current);
                    current = parsed with
                    {
                        output = normalizedOutput,
                        startedAtUtc = current.startedAtUtc
                    };
                }

                onProgress(current);
            }
        }

        async Task ReadStandardErrorAsync()
        {
            while (true)
            {
                string? line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    break;
                }

                lock (outputLock)
                {
                    outputBuilder.AppendLine(line);
                }
            }
        }

        string GetNormalizedOutput()
        {
            lock (outputLock)
            {
                return GetNormalizedOutputUnsafe();
            }
        }

        string GetNormalizedOutputUnsafe() =>
            SystemMaintenanceService.NormalizeMaintenanceToolOutput(outputBuilder.ToString());
    }

    internal static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    internal static bool IsKnownCipherTemporaryDirectoryName(string directoryName)
    {
        if (string.IsNullOrWhiteSpace(directoryName) ||
            !directoryName.StartsWith(CipherTemporaryDirectoryPrefix, StringComparison.OrdinalIgnoreCase) ||
            directoryName.Length == CipherTemporaryDirectoryPrefix.Length)
        {
            return false;
        }

        for (int index = CipherTemporaryDirectoryPrefix.Length; index < directoryName.Length; index++)
        {
            char character = directoryName[index];
            if (!IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.')
            {
                return false;
            }
        }

        return true;
    }

    private static FreeSpaceWipeProgress CreateParsedProgress(string operationId, string driveRoot, string pass, double percent, string status, string output) =>
        new(operationId, driveRoot, "Running", pass, percent, status, output, DateTime.UtcNow, null, "notNeeded", status);

    private static string EstimateHddDuration(long freeSpaceBytes)
    {
        double writeGiB = Math.Max(0, freeSpaceBytes) * 3d / 1024d / 1024d / 1024d;
        int lowHours = Math.Max(1, (int)Math.Floor(writeGiB / 600d));
        int highHours = Math.Max(lowHours + 1, (int)Math.Ceiling(writeGiB / 250d));
        return $"~{lowHours}-{highHours}+ hours";
    }

    private static string NormalizeCleanupStatus(string cleanupStatus)
    {
        return cleanupStatus switch
        {
            "cleanupSucceeded" or "cleanupFailed" or "notNeeded" or "unknown" => cleanupStatus,
            _ => "unknown"
        };
    }

    private static RootEntrySnapshot SnapshotRootEntries(string driveRoot)
    {
        try
        {
            var entries = new HashSet<string>(
                Directory.EnumerateFileSystemEntries(driveRoot).Select(NormalizeRootEntryPath),
                StringComparer.OrdinalIgnoreCase);
            return new RootEntrySnapshot(entries, true, string.Empty);
        }
        catch (Exception ex)
        {
            return new RootEntrySnapshot([], false, ex.Message);
        }
    }

    private static CleanupResult CleanupAfterCancellation(string driveRoot, RootEntrySnapshot snapshot, DateTime processStartedAtUtc)
    {
        if (!snapshot.Succeeded)
        {
            return new CleanupResult("unknown", $"Could not verify pre-wipe root contents, so cipher temporary cleanup was not attempted. {snapshot.ErrorMessage}");
        }

        List<DirectoryInfo> candidates;
        try
        {
            DateTime minimumTimestampUtc = processStartedAtUtc.Subtract(CleanupTimestampTolerance);
            candidates = Directory.EnumerateDirectories(driveRoot)
                .Select(path => new DirectoryInfo(path))
                .Where(directory => !snapshot.Entries.Contains(NormalizeRootEntryPath(directory.FullName)))
                .Where(IsLikelyCipherDirectory)
                .Where(directory => IsCreatedOrModifiedAfter(directory, minimumTimestampUtc))
                .ToList();
        }
        catch (Exception ex)
        {
            return new CleanupResult("unknown", $"Could not inspect the drive root for cipher temporary folders. {ex.Message}");
        }

        if (candidates.Count == 0)
        {
            return new CleanupResult("notNeeded", "No new cipher temporary folders were found after cancellation.");
        }

        var failures = new List<string>();
        foreach (DirectoryInfo candidate in candidates)
        {
            try
            {
                if (candidate.Exists)
                {
                    candidate.Delete(recursive: true);
                }
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (Exception ex)
            {
                failures.Add($"{candidate.Name}: {ex.Message}");
            }
        }

        if (failures.Count == 0)
        {
            string unit = candidates.Count == 1 ? "folder" : "folders";
            return new CleanupResult("cleanupSucceeded", $"Removed {candidates.Count} cipher temporary {unit} left by the cancelled wipe.");
        }

        return new CleanupResult(
            "cleanupFailed",
            $"Could not remove all cipher temporary folders. {string.Join(" ", failures)}");
    }

    private static bool IsLikelyCipherDirectory(DirectoryInfo directory) =>
        IsKnownCipherTemporaryDirectoryName(directory.Name);

    private static bool IsCreatedOrModifiedAfter(DirectoryInfo directory, DateTime minimumTimestampUtc)
    {
        try
        {
            directory.Refresh();
            return directory.CreationTimeUtc >= minimumTimestampUtc ||
                directory.LastWriteTimeUtc >= minimumTimestampUtc;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeRootEntryPath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static async Task IgnoreCancellationReadTaskAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task<bool> WaitForProcessExitAfterKillAsync(Process process, TimeSpan timeout)
    {
        try
        {
            if (process.HasExited)
            {
                return true;
            }

            using var timeoutCancellation = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(timeoutCancellation.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            try
            {
                return process.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }

    private static async Task WaitForReadTasksAfterCancellationAsync(TimeSpan timeout, params Task[] readTasks)
    {
        Task allReads = Task.WhenAll(readTasks);
        Task completedTask = await Task.WhenAny(allReads, Task.Delay(timeout));
        if (ReferenceEquals(completedTask, allReads))
        {
            await IgnoreCancellationReadTaskAsync(allReads);
            return;
        }

        _ = allReads.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static bool IsAsciiLetterOrDigit(char character) =>
        (character >= 'A' && character <= 'Z') ||
        (character >= 'a' && character <= 'z') ||
        (character >= '0' && character <= '9');

    private sealed record RootEntrySnapshot(HashSet<string> Entries, bool Succeeded, string ErrorMessage);

    private sealed record CleanupResult(string Status, string Message);
}
