using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FileLocker;

internal static class BoundedFileReader
{
    private const int DefaultBufferSize = 81920;
    private static readonly Encoding StrictUtf8Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static async Task<byte[]> ReadAllBytesAsync(
        string path,
        long maxBytes,
        string? tooLargeMessage = null,
        CancellationToken cancellationToken = default)
    {
        ValidateMaxBytes(maxBytes);
        ValidatePath(path);

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            DefaultBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (stream.Length > maxBytes)
        {
            throw CreateTooLargeException(tooLargeMessage);
        }

        using var output = new MemoryStream((int)Math.Min(stream.Length, maxBytes));
        byte[] buffer = new byte[(int)Math.Min(DefaultBufferSize, maxBytes + 1)];
        long totalBytesRead = 0;

        while (true)
        {
            int readLimit = GetNextReadLimit(buffer.Length, totalBytesRead, maxBytes, tooLargeMessage);
            int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, readLimit), cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                return output.ToArray();
            }

            totalBytesRead += bytesRead;
            if (totalBytesRead > maxBytes)
            {
                throw CreateTooLargeException(tooLargeMessage);
            }

            output.Write(buffer, 0, bytesRead);
        }
    }

    internal static byte[] ReadAllBytes(string path, long maxBytes, string? tooLargeMessage = null)
    {
        ValidateMaxBytes(maxBytes);
        ValidatePath(path);

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, DefaultBufferSize, FileOptions.SequentialScan);
        if (stream.Length > maxBytes)
        {
            throw CreateTooLargeException(tooLargeMessage);
        }

        using var output = new MemoryStream((int)Math.Min(stream.Length, maxBytes));
        byte[] buffer = new byte[(int)Math.Min(DefaultBufferSize, maxBytes + 1)];
        long totalBytesRead = 0;

        while (true)
        {
            int readLimit = GetNextReadLimit(buffer.Length, totalBytesRead, maxBytes, tooLargeMessage);
            int bytesRead = stream.Read(buffer, 0, readLimit);
            if (bytesRead == 0)
            {
                return output.ToArray();
            }

            totalBytesRead += bytesRead;
            if (totalBytesRead > maxBytes)
            {
                throw CreateTooLargeException(tooLargeMessage);
            }

            output.Write(buffer, 0, bytesRead);
        }
    }

    internal static async Task<string> ReadAllUtf8TextAsync(
        string path,
        long maxBytes,
        string? tooLargeMessage = null,
        CancellationToken cancellationToken = default)
    {
        byte[] bytes = await ReadAllBytesAsync(path, maxBytes, tooLargeMessage, cancellationToken).ConfigureAwait(false);
        try
        {
            return DecodeUtf8(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static string ReadAllUtf8Text(string path, long maxBytes, string? tooLargeMessage = null)
    {
        byte[] bytes = ReadAllBytes(path, maxBytes, tooLargeMessage);
        try
        {
            return DecodeUtf8(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        try
        {
            string text = StrictUtf8Encoding.GetString(bytes);
            return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("File is not valid UTF-8 text.", ex);
        }
    }

    private static int GetNextReadLimit(int bufferLength, long totalBytesRead, long maxBytes, string? tooLargeMessage)
    {
        long remainingAllowedBytes = maxBytes - totalBytesRead;
        if (remainingAllowedBytes < 0)
        {
            throw CreateTooLargeException(tooLargeMessage);
        }

        return (int)Math.Min(bufferLength, remainingAllowedBytes + 1);
    }

    private static void ValidateMaxBytes(long maxBytes)
    {
        if (maxBytes is < 0 or > int.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "The maximum read size must fit in a managed byte array.");
        }
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Any(character => char.IsControl(character) || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format))
        {
            throw new ArgumentException("File path contains invalid characters.", nameof(path));
        }

        try
        {
            string trimmedPath = path.Trim();
            if (!Path.IsPathFullyQualified(trimmedPath))
            {
                throw new ArgumentException("File path must reference a normal file.", nameof(path));
            }

            string fullPath = Path.GetFullPath(trimmedPath);
            string fileName = Path.GetFileName(fullPath);
            string root = Path.GetPathRoot(fullPath) ?? string.Empty;
            string pathWithoutRoot = fullPath.Length > root.Length ? fullPath[root.Length..] : string.Empty;

            if (string.IsNullOrWhiteSpace(fileName) ||
                pathWithoutRoot.Contains(':', StringComparison.Ordinal))
            {
                throw new ArgumentException("File path must reference a normal file.", nameof(path));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("File path must reference a normal file.", nameof(path), ex);
        }
    }

    private static InvalidDataException CreateTooLargeException(string? message)
    {
        return new InvalidDataException(string.IsNullOrWhiteSpace(message) ? "File is too large to read." : message);
    }
}
