using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FileLocker;

internal sealed record DriveMediaInfo(string mediaType, string mediaDetectionStatus, string mediaDescription);

internal static class DriveMediaTypeDetector
{
    private static readonly TimeSpan DetectionTimeout = TimeSpan.FromSeconds(3);

    internal static DriveMediaInfo DetectForDriveRoot(string driveRoot)
    {
        if (!TryGetDriveLetter(driveRoot, out char driveLetter))
        {
            return Unknown("Unsupported", "Drive media detection is not available for this drive.");
        }

        return DetectWithPowerShell(driveLetter, DetectionTimeout);
    }

    internal static DriveMediaInfo Removable()
    {
        return new DriveMediaInfo(
            "Removable",
            "Detected",
            "This is a removable drive. Optimization and free-space wiping may provide limited benefit, and flash media wear leveling can reduce wipe reliability while adding writes.");
    }

    internal static DriveMediaInfo ClassifyPhysicalMediaTypes(
        IEnumerable<int> mediaTypes,
        int physicalDiskCount,
        bool timedOut,
        bool unsupported) =>
        ClassifyPhysicalMediaTypes(mediaTypes, [], physicalDiskCount, timedOut, unsupported);

    internal static DriveMediaInfo ClassifyPhysicalMediaTypes(
        IEnumerable<int> mediaTypes,
        IEnumerable<string> busTypes,
        int physicalDiskCount,
        bool timedOut,
        bool unsupported)
    {
        if (timedOut)
        {
            return Unknown("TimedOut", "Drive media detection timed out. FileLocker could not determine whether this drive is an SSD or HDD.");
        }

        if (unsupported)
        {
            return Unknown("Unsupported", "Drive media detection is not available for this drive.");
        }

        int[] explicitMediaTypes = mediaTypes
            .Where(mediaType => mediaType is 3 or 4)
            .Distinct()
            .Order()
            .ToArray();
        int[] inferredMediaTypes = busTypes
            .Where(IsNvmeBusType)
            .Select(_ => 4)
            .Distinct()
            .Order()
            .ToArray();
        int[] knownMediaTypes = explicitMediaTypes.Length > 0 ? explicitMediaTypes : inferredMediaTypes;

        if (knownMediaTypes.Length == 0)
        {
            return Unknown("Unknown", "FileLocker could not determine whether this drive is an SSD or HDD.");
        }

        if (knownMediaTypes.Length > 1)
        {
            return new DriveMediaInfo(
                "Mixed",
                "Mixed",
                "This volume spans physical disks with conflicting media types, so FileLocker will not assume SSD or HDD behavior.");
        }

        if (physicalDiskCount != 1)
        {
            return Unknown("Unknown", "This volume maps to multiple physical disks, so FileLocker will not label it as only SSD or HDD.");
        }

        return knownMediaTypes[0] switch
        {
            3 => new DriveMediaInfo(
                "HDD",
                "Detected",
                "Detected a traditional hard drive. Free-space overwrite works as intended on spinning disks."),
            4 => new DriveMediaInfo(
                "SSD",
                "Detected",
                "Detected a solid-state drive. Windows optimization normally issues TRIM; avoid unnecessary free-space wiping because SSD wear leveling can limit its reliability."),
            _ => Unknown("Unknown", "FileLocker could not determine whether this drive is an SSD or HDD.")
        };
    }

    private static DriveMediaInfo DetectWithPowerShell(char driveLetter, TimeSpan timeout)
    {
        string script = "$ErrorActionPreference='Stop'; " +
            $"$partition=Get-Partition -DriveLetter '{driveLetter}'; " +
            "$disks=@($partition | Get-Disk); " +
            "$physical=@(); " +
            "foreach($disk in $disks){ try { $physical += @(Get-PhysicalDisk -DeviceNumber $disk.Number -ErrorAction Stop) } catch {} }; " +
            "$mediaTypes=[int[]]@($physical | ForEach-Object { switch -Regex ([string]$_.MediaType) { '^3$|^HDD$' { 3; break } '^4$|^SSD$' { 4; break } default { 0 } } }); " +
            "$busTypes=[string[]]@(@($disks | ForEach-Object { [string]$_.BusType }) + @($physical | ForEach-Object { [string]$_.BusType })); " +
            "[pscustomobject]@{ PhysicalDiskCount=$disks.Count; MediaTypes=$mediaTypes; BusTypes=$busTypes } | ConvertTo-Json -Compress";

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo("powershell.exe")
            {
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
            process.StartInfo.ArgumentList.Add("Bypass");
            process.StartInfo.ArgumentList.Add("-Command");
            process.StartInfo.ArgumentList.Add(script);

            if (!process.Start())
            {
                return Unknown("Unknown", "Drive media detection could not be started.");
            }

            bool exited = process.WaitForExit((int)Math.Ceiling(timeout.TotalMilliseconds));
            if (!exited)
            {
                KillProcessTree(process);
                return ClassifyPhysicalMediaTypes([], physicalDiskCount: 0, timedOut: true, unsupported: false);
            }

            string output = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return Unknown("Unknown", "Drive media detection did not return a usable result.");
            }

            DriveMediaPowerShellPayload? payload = ParsePayload(output);
            if (payload is null)
            {
                return Unknown("Unknown", "Drive media detection did not return a usable result.");
            }

            return ClassifyPhysicalMediaTypes(
                payload.MediaTypes,
                payload.BusTypes,
                payload.PhysicalDiskCount,
                timedOut: false,
                unsupported: false);
        }
        catch
        {
            return Unknown("Unknown", "Drive media detection failed.");
        }
    }

    private static DriveMediaPowerShellPayload? ParsePayload(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("PhysicalDiskCount", out JsonElement countElement) ||
                !countElement.TryGetInt32(out int physicalDiskCount))
            {
                return null;
            }

            var mediaTypes = new List<int>();
            if (root.TryGetProperty("MediaTypes", out JsonElement mediaTypesElement))
            {
                if (mediaTypesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in mediaTypesElement.EnumerateArray())
                    {
                        if (item.TryGetInt32(out int mediaType))
                        {
                            mediaTypes.Add(mediaType);
                        }
                    }
                }
                else if (mediaTypesElement.TryGetInt32(out int mediaType))
                {
                    mediaTypes.Add(mediaType);
                }
            }

            string[] busTypes = ParseStringArray(root, "BusTypes");

            return new DriveMediaPowerShellPayload(physicalDiskCount, mediaTypes.ToArray(), busTypes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string[] ParseStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element))
        {
            return [];
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToArray();
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            string? value = element.GetString();
            return string.IsNullOrWhiteSpace(value) ? [] : [value];
        }

        return [];
    }

    private static bool IsNvmeBusType(string? busType) =>
        !string.IsNullOrWhiteSpace(busType) &&
        busType.Contains("NVMe", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetDriveLetter(string driveRoot, out char driveLetter)
    {
        driveLetter = '\0';

        if (string.IsNullOrWhiteSpace(driveRoot))
        {
            return false;
        }

        string? root = Path.GetPathRoot(driveRoot.Trim());
        if (root is not { Length: >= 2 } || root[1] != ':' || !char.IsLetter(root[0]))
        {
            return false;
        }

        driveLetter = char.ToUpperInvariant(root[0]);
        return true;
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static DriveMediaInfo Unknown(string status, string description)
    {
        return new DriveMediaInfo("Unknown", status, description);
    }

    private sealed record DriveMediaPowerShellPayload(int PhysicalDiskCount, int[] MediaTypes, string[] BusTypes);
}
