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

        return GetGenericPathReplacement(path);
    }

    internal static string RedactMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        string redacted = message.Trim();
        redacted = RedactKnownRoot(redacted, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%");
        redacted = RedactKnownRoot(redacted, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%");
        redacted = RedactKnownRoot(redacted, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
        redacted = RedactKnownRootToken(redacted, "%LOCALAPPDATA%");
        redacted = RedactKnownRootToken(redacted, "%APPDATA%");
        redacted = RedactKnownRootToken(redacted, "%USERPROFILE%");
        redacted = RedactQuotedAbsolutePaths(redacted);
        redacted = RedactUnquotedAbsolutePaths(redacted);
        return redacted;
    }

    private static string RedactKnownRootToken(string message, string token)
    {
        int index = 0;
        while (index < message.Length)
        {
            int match = message.IndexOf(token, index, StringComparison.OrdinalIgnoreCase);
            if (match < 0)
            {
                break;
            }

            if (!IsKnownRootMatchBoundary(message, match, token.Length))
            {
                index = match + token.Length;
                continue;
            }

            int end = FindUnquotedPathEnd(message, match);
            string candidateWithPunctuation = message[match..end];
            string candidate = candidateWithPunctuation.TrimEnd('.', ',', ';', ':', ')', ']', '}');
            string trailing = candidateWithPunctuation[candidate.Length..];
            string relativePath = candidate.Length > token.Length
                ? candidate[token.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : string.Empty;
            string leaf = string.IsNullOrWhiteSpace(relativePath)
                ? string.Empty
                : Path.GetFileName(relativePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string replacement = string.IsNullOrWhiteSpace(leaf)
                ? token + trailing
                : Path.Combine(token, leaf) + trailing;

            message = string.Concat(message.AsSpan(0, match), replacement, message.AsSpan(end));
            index = match + replacement.Length;
        }

        return message;
    }

    private static string RedactKnownRoot(string message, string root, string token)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return message;
        }

        int index = 0;
        while (index < message.Length)
        {
            int match = message.IndexOf(root, index, StringComparison.OrdinalIgnoreCase);
            if (match < 0)
            {
                break;
            }

            if (!IsKnownRootMatchBoundary(message, match, root.Length))
            {
                index = match + root.Length;
                continue;
            }

            int end = FindUnquotedPathEnd(message, match);
            string candidateWithPunctuation = message[match..end];
            string candidate = candidateWithPunctuation.TrimEnd('.', ',', ';', ':', ')', ']', '}');
            string leaf = Path.GetFileName(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string trailing = candidateWithPunctuation[candidate.Length..];
            string replacement = string.IsNullOrWhiteSpace(leaf)
                ? token + trailing
                : Path.Combine(token, leaf) + trailing;

            message = string.Concat(message.AsSpan(0, match), replacement, message.AsSpan(end));
            index = match + replacement.Length;
        }

        return message;
    }

    private static bool IsKnownRootMatchBoundary(string message, int matchIndex, int rootLength)
    {
        bool startsOnBoundary = matchIndex == 0 || message[matchIndex - 1] is '\'' or '"' or '(' or '[' or '{' || char.IsWhiteSpace(message[matchIndex - 1]);
        int afterRoot = matchIndex + rootLength;
        bool endsOnBoundary = afterRoot >= message.Length ||
            message[afterRoot] is '\\' or '/' or '\'' or '"' or '.' or ',' or ';' or ':' or ')' or ']' or '}' ||
            char.IsWhiteSpace(message[afterRoot]);

        return startsOnBoundary && endsOnBoundary;
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
                    string replacement = $"{quote}{GetGenericPathReplacement(candidate)}{quote}";
                    message = string.Concat(message.AsSpan(0, open), replacement, message.AsSpan(close + quote.Length));
                    start = open + replacement.Length;
                    continue;
                }

                start = close + quote.Length;
            }
        }

        return message;
    }

    private static string RedactUnquotedAbsolutePaths(string message)
    {
        int index = 0;
        while (index < message.Length)
        {
            if (!LooksLikeAbsolutePathStart(message, index))
            {
                index++;
                continue;
            }

            int end = FindUnquotedPathEnd(message, index);

            string candidateWithPunctuation = message[index..end];
            string candidate = candidateWithPunctuation.TrimEnd('.', ',', ';', ':', ')', ']', '}');
            if (candidate.Length == 0 || !Path.IsPathFullyQualified(candidate))
            {
                index = end;
                continue;
            }

            string trailing = candidateWithPunctuation[candidate.Length..];
            string replacement = GetGenericPathReplacement(candidate) + trailing;
            message = string.Concat(message.AsSpan(0, index), replacement, message.AsSpan(end));
            index += replacement.Length;
        }

        return message;
    }

    private static string GetGenericPathReplacement(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(trimmed) || IsRootPath(path))
        {
            return "[redacted]";
        }

        string leaf = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(leaf) ? "[redacted]" : Path.Combine("[redacted]", leaf);
    }

    private static bool IsRootPath(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            string normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static int FindUnquotedPathEnd(string message, int startIndex)
    {
        int end = startIndex + 1;
        while (end < message.Length)
        {
            char current = message[end];
            if (current is '\'' or '"' or '\r' or '\n')
            {
                break;
            }

            if (!char.IsWhiteSpace(current))
            {
                end++;
                continue;
            }

            int continuationStart = end;
            while (continuationStart < message.Length && char.IsWhiteSpace(message[continuationStart]))
            {
                continuationStart++;
            }

            if (continuationStart >= message.Length || message[continuationStart] is '\'' or '"' or '\r' or '\n')
            {
                break;
            }

            int continuationEnd = continuationStart;
            while (continuationEnd < message.Length &&
                !char.IsWhiteSpace(message[continuationEnd]) &&
                message[continuationEnd] is not '\'' and not '"' and not '\r' and not '\n')
            {
                continuationEnd++;
            }

            string continuation = message[continuationStart..continuationEnd].TrimEnd('.', ',', ';', ':', ')', ']', '}');
            if (!LooksLikePathContinuation(continuation))
            {
                break;
            }

            end = continuationEnd;
        }

        return end;
    }

    private static bool LooksLikePathContinuation(string token)
    {
        return token.Contains('\\', StringComparison.Ordinal) ||
            token.Contains('/', StringComparison.Ordinal) ||
            token.Contains('.', StringComparison.Ordinal);
    }

    private static bool LooksLikeAbsolutePathStart(string message, int index)
    {
        if (index + 2 < message.Length &&
            char.IsLetter(message[index]) &&
            message[index + 1] == ':' &&
            message[index + 2] is '\\' or '/')
        {
            return true;
        }

        return index + 1 < message.Length &&
            message[index] is '\\' or '/' &&
            message[index + 1] is '\\' or '/';
    }
}
