using System;

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

    private static FreeSpaceWipeProgress CreateParsedProgress(string operationId, string driveRoot, string pass, double percent, string status, string output) =>
        new(operationId, driveRoot, "Running", pass, percent, status, output, DateTime.UtcNow, null, "notNeeded", status);

    private static string EstimateHddDuration(long freeSpaceBytes)
    {
        double writeGiB = Math.Max(0, freeSpaceBytes) * 3d / 1024d / 1024d / 1024d;
        int lowHours = Math.Max(1, (int)Math.Floor(writeGiB / 600d));
        int highHours = Math.Max(lowHours + 1, (int)Math.Ceiling(writeGiB / 250d));
        return $"~{lowHours}-{highHours}+ hours";
    }
}
