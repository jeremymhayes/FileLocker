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
            UnauthorizedAccessException => "Access denied or wrong unlock secret",
            CryptographicException => "Authentication failed or corrupted payload",
            InvalidDataException => "Unsupported or corrupted payload",
            FileNotFoundException => "Missing file",
            DirectoryNotFoundException => "Missing folder",
            IOException ioException when IsDiskFull(ioException) => "Disk full",
            IOException => "File in use or I/O error",
            OperationCanceledException => "Cancelled",
            _ => "Unexpected error"
        };
    }

    private static bool IsDiskFull(IOException exception)
    {
        const int HResultDiskFull = unchecked((int)0x80070070);
        const int HResultHandleDiskFull = unchecked((int)0x80070027);
        return exception.HResult == HResultDiskFull || exception.HResult == HResultHandleDiskFull;
    }
}
