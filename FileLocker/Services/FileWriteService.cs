using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FileLocker;

internal static class FileWriteService
{
    internal static async Task WriteAllTextAtomicallyAsync(
        string path,
        string contents,
        Encoding encoding,
        CancellationToken cancellationToken = default)
    {
        ValidateWriteArguments(path, contents, encoding);
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? bytes = null;
        try
        {
            bytes = encoding.GetBytes(contents);
            await WriteAllBytesAtomicallyAsync(path, bytes, cancellationToken);
        }
        finally
        {
            ClearInternalBuffer(bytes);
        }
    }

    internal static void WriteAllTextAtomically(string path, string contents, Encoding encoding)
    {
        ValidateWriteArguments(path, contents, encoding);
        byte[]? bytes = null;
        try
        {
            bytes = encoding.GetBytes(contents);
            WriteAllBytesAtomically(path, bytes);
        }
        finally
        {
            ClearInternalBuffer(bytes);
        }
    }

    internal static async Task WriteAllBytesAtomicallyAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken = default)
    {
        ValidateWriteArguments(path, bytes);
        cancellationToken.ThrowIfCancellationRequested();

        string tempPath = CreateTemporarySiblingPath(path);

        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ReplaceFileWithTemporaryFile(tempPath, path);
        }
        finally
        {
            FileCleanupService.TryDeleteTemporaryFile(tempPath, out _);
        }
    }

    internal static void WriteAllBytesAtomically(string path, byte[] bytes)
    {
        ValidateWriteArguments(path, bytes);
        string tempPath = CreateTemporarySiblingPath(path);

        try
        {
            File.WriteAllBytes(tempPath, bytes);
            ReplaceFileWithTemporaryFile(tempPath, path);
        }
        finally
        {
            FileCleanupService.TryDeleteTemporaryFile(tempPath, out _);
        }
    }

    internal static string ResolveAvailablePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateFileTargetPath(path);

        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string fileName = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        string candidate = path;
        int counter = 1;

        while (File.Exists(candidate) || Directory.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{fileName}-{counter}{extension}");
            counter++;
        }

        return candidate;
    }

    private static string CreateTemporarySiblingPath(string path)
    {
        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return Path.Combine(
            string.IsNullOrWhiteSpace(directory) ? Environment.CurrentDirectory : directory,
            $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
    }

    private static void ValidateWriteArguments(string path, string contents, Encoding encoding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateFileTargetPath(path);
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(encoding);
    }

    private static void ValidateWriteArguments(string path, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateFileTargetPath(path);
        ArgumentNullException.ThrowIfNull(bytes);
    }

    private static void ValidateFileTargetPath(string path)
    {
        if (Path.EndsInDirectorySeparator(path) || string.IsNullOrWhiteSpace(Path.GetFileName(path)))
        {
            throw new ArgumentException("A file path must include a file name.", nameof(path));
        }
    }

    private static void ClearInternalBuffer(byte[]? buffer)
    {
        if (buffer is { Length: > 0 })
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    internal static void ReplaceFileWithTemporaryFile(string tempPath, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateFileTargetPath(tempPath);
        ValidateFileTargetPath(path);

        if (File.Exists(tempPath) &&
            (File.GetAttributes(tempPath) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
        {
            throw new IOException("Atomic file writes do not promote temp file reparse points.");
        }

        FileAttributes? originalAttributes = null;
        if (File.Exists(path))
        {
            originalAttributes = File.GetAttributes(path);
            if ((originalAttributes.Value & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
            {
                throw new IOException("Atomic file writes do not replace file reparse points.");
            }

            FileCleanupService.ClearReadOnlyAttribute(path);
        }

        try
        {
            if (originalAttributes is null)
            {
                File.Move(tempPath, path, overwrite: false);
            }
            else
            {
                File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }

            RestoreReplacementMetadataAttributes(path, originalAttributes);
        }
        catch
        {
            RestoreOriginalAttributes(path, originalAttributes);
            throw;
        }
    }

    private static void RestoreReplacementMetadataAttributes(string path, FileAttributes? originalAttributes)
    {
        if (originalAttributes is null || !File.Exists(path))
        {
            return;
        }

        const FileAttributes metadataAttributes = FileAttributes.Hidden | FileAttributes.System;
        FileAttributes preservedAttributes = originalAttributes.Value & metadataAttributes;
        if (preservedAttributes == 0)
        {
            return;
        }

        try
        {
            FileAttributes currentAttributes = File.GetAttributes(path);
            File.SetAttributes(path, (currentAttributes & ~metadataAttributes) | preservedAttributes);
        }
        catch
        {
            // Best effort: the replacement succeeded and callers need the write result first.
        }
    }

    private static void RestoreOriginalAttributes(string path, FileAttributes? originalAttributes)
    {
        if (originalAttributes is null || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.SetAttributes(path, originalAttributes.Value);
        }
        catch
        {
            // Best effort: the original write failure is more useful to callers.
        }
    }
}
