using System;
using System.IO;

namespace FileLocker;

internal static class SensitiveDataRedactor
{
    internal static string RedactPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string leaf = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(leaf) ? "[redacted]" : Path.Combine("[redacted]", leaf);
    }

    internal static string RedactMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        string redacted = message.Trim();
        redacted = RedactKnownRoot(redacted, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
        redacted = RedactKnownRoot(redacted, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%");
        redacted = RedactQuotedAbsolutePaths(redacted);
        return redacted;
    }

    private static string RedactKnownRoot(string message, string root, string token)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return message;
        }

        return message.Replace(root, token, StringComparison.OrdinalIgnoreCase);
    }

    private static string RedactQuotedAbsolutePaths(string message)
    {
        string[] quotePairs = ["'", "\""];
        foreach (string quote in quotePairs)
        {
            int start = 0;
            while (start < message.Length)
            {
                int open = message.IndexOf(quote, start, StringComparison.Ordinal);
                if (open < 0)
                {
                    break;
                }

                int close = message.IndexOf(quote, open + quote.Length, StringComparison.Ordinal);
                if (close < 0)
                {
                    break;
                }

                string candidate = message.Substring(open + quote.Length, close - open - quote.Length);
                if (Path.IsPathFullyQualified(candidate))
                {
                    string replacement = $"{quote}{Path.Combine("[redacted]", Path.GetFileName(candidate))}{quote}";
                    message = string.Concat(message.AsSpan(0, open), replacement, message.AsSpan(close + quote.Length));
                    start = open + replacement.Length;
                    continue;
                }

                start = close + quote.Length;
            }
        }

        return message;
    }
}
