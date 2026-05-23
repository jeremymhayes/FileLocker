using System;
using System.IO;
using System.Security.Cryptography;

namespace FileLocker;

internal static class OperationFailureClassifier
{
    internal static string Classify(Exception exception)
    {
        Exception current = exception.GetBaseException();
        return current switch
        {
            UnauthorizedAccessException unauthorized when IsIntegrityValidationFailure(unauthorized) => "Integrity verification failed",
            UnauthorizedAccessException => "Access denied or wrong unlock secret",
            CryptographicException => "Authentication failed or corrupted payload",
            InvalidDataException => "Unsupported or corrupted payload",
            FileNotFoundException => "Missing file",
            DirectoryNotFoundException => "Missing folder",
            PathTooLongException => "Path too long",
            IOException ioException when IsDiskFull(ioException) => "Disk full",
            IOException ioException when IsFileInUse(ioException) => "File in use",
            IOException => "File in use or I/O error",
            OperationCanceledException => "Cancelled",
            ArgumentException => "Invalid input",
            NotSupportedException => "Unsupported operation",
            _ => "Unexpected error"
        };
    }

    private static bool IsDiskFull(IOException exception)
    {
        const int HResultDiskFull = unchecked((int)0x80070070);
        const int HResultHandleDiskFull = unchecked((int)0x80070027);
        return exception.HResult == HResultDiskFull || exception.HResult == HResultHandleDiskFull;
    }

    private static bool IsFileInUse(IOException exception)
    {
        const int HResultSharingViolation = unchecked((int)0x80070020);
        const int HResultLockViolation = unchecked((int)0x80070021);
        return exception.HResult == HResultSharingViolation || exception.HResult == HResultLockViolation;
    }

    private static bool IsIntegrityValidationFailure(UnauthorizedAccessException exception)
    {
        return exception.Message.Contains("integrity validation", StringComparison.OrdinalIgnoreCase);
    }
}
