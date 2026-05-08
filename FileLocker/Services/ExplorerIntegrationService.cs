using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FileLocker;

internal static class ExplorerIntegrationService
{
    private const string EncryptVerbKey = "FileLockerEncrypt";
    private const string EncryptVerbLabel = "Encrypt with FileLocker";
    private const string LegacyDecryptVerbKey = "FileLockerDecrypt";
    private const string LegacyVerifyVerbKey = "FileLockerVerify";
    private const string LegacyRotateVerbKey = "FileLockerRotate";

    private static readonly ExplorerVerbTarget[] EncryptTargets =
    [
        new(@"Software\Classes\*\shell", @"--encrypt ""%1"""),
        new(@"Software\Classes\Directory\shell", @"--encrypt ""%1""")
    ];

    private static readonly string[] LegacyVerbKeys =
    [
        LegacyDecryptVerbKey,
        LegacyVerifyVerbKey,
        LegacyRotateVerbKey
    ];

    internal static ExplorerIntegrationState GetState(string executablePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ExplorerIntegrationState(false, false, "Explorer integration is only available on Windows.");
        }

        try
        {
            bool isRegistered = EncryptTargets.All(target => IsTargetRegistered(target, executablePath));
            bool legacyEntriesPresent = EncryptTargets.Any(target => LegacyVerbKeys.Any(legacyKey => SubKeyExists(target.RootPath, legacyKey)));
            string status = isRegistered
                ? legacyEntriesPresent
                    ? "Explorer integration is enabled, but legacy FileLocker verbs need cleanup."
                    : "Explorer integration is enabled."
                : "Explorer integration is not installed.";

            return new ExplorerIntegrationState(isRegistered, true, status);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new ExplorerIntegrationState(false, false, $"Explorer integration status could not be read: {ex.Message}");
        }
    }

    internal static ExplorerIntegrationState SetEnabled(string executablePath, bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ExplorerIntegrationState(false, false, "Explorer integration is only available on Windows.");
        }

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            throw new FileNotFoundException("FileLocker executable was not found.", executablePath);
        }

        CleanupLegacyEntries();

        if (enabled)
        {
            foreach (ExplorerVerbTarget target in EncryptTargets)
            {
                RegisterTarget(target, executablePath);
            }
        }
        else
        {
            foreach (ExplorerVerbTarget target in EncryptTargets)
            {
                DeleteSubKeyTreeIfExists(target.RootPath, EncryptVerbKey);
            }
        }

        return GetState(executablePath);
    }

    internal static IReadOnlyList<string> GetManagedVerbKeys() =>
    [
        EncryptVerbKey,
        ..LegacyVerbKeys
    ];

    private static bool IsTargetRegistered(ExplorerVerbTarget target, string executablePath)
    {
        using RegistryKey? commandKey = Registry.CurrentUser.OpenSubKey($@"{target.RootPath}\{EncryptVerbKey}\command");
        string? command = commandKey?.GetValue(null) as string;
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        string expectedCommand = BuildCommand(executablePath, target.Arguments);
        return string.Equals(command, expectedCommand, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SubKeyExists(string rootPath, string subKeyName)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey($@"{rootPath}\{subKeyName}");
        return key != null;
    }

    private static void RegisterTarget(ExplorerVerbTarget target, string executablePath)
    {
        using RegistryKey verbKey = Registry.CurrentUser.CreateSubKey($@"{target.RootPath}\{EncryptVerbKey}", writable: true)
            ?? throw new InvalidOperationException("Could not create Explorer integration registry key.");
        verbKey.SetValue(null, EncryptVerbLabel, RegistryValueKind.String);
        verbKey.SetValue("Icon", executablePath, RegistryValueKind.String);

        using RegistryKey commandKey = verbKey.CreateSubKey("command", writable: true)
            ?? throw new InvalidOperationException("Could not create Explorer integration command key.");
        commandKey.SetValue(null, BuildCommand(executablePath, target.Arguments), RegistryValueKind.String);
    }

    private static void CleanupLegacyEntries()
    {
        foreach (ExplorerVerbTarget target in EncryptTargets)
        {
            foreach (string legacyKey in LegacyVerbKeys)
            {
                DeleteSubKeyTreeIfExists(target.RootPath, legacyKey);
            }
        }
    }

    private static void DeleteSubKeyTreeIfExists(string rootPath, string subKeyName)
    {
        using RegistryKey? rootKey = Registry.CurrentUser.OpenSubKey(rootPath, writable: true);
        rootKey?.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
    }

    private static string BuildCommand(string executablePath, string arguments) =>
        $@"""{executablePath}"" {arguments}";

    private sealed record ExplorerVerbTarget(string RootPath, string Arguments);
}

internal sealed record ExplorerIntegrationState(
    bool IsRegistered,
    bool CanManage,
    string StatusMessage);
