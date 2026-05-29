using System;
using System.Globalization;

namespace FileLocker;

internal static class CsvCellFormatter
{
    internal static string Format(string? value, bool alwaysQuote = false)
    {
        string sanitized = EscapeFormula(value ?? string.Empty);
        bool shouldQuote = alwaysQuote ||
            sanitized.Contains(',') ||
            sanitized.Contains('"') ||
            sanitized.Contains('\n') ||
            sanitized.Contains('\r');

        return shouldQuote
            ? $"\"{sanitized.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : sanitized;
    }

    private static string EscapeFormula(string value)
    {
        int firstMeaningfulIndex = 0;
        while (firstMeaningfulIndex < value.Length && IsFormulaPrefixIgnored(value[firstMeaningfulIndex]))
        {
            firstMeaningfulIndex++;
        }

        if (firstMeaningfulIndex >= value.Length || value[firstMeaningfulIndex] is not ('=' or '+' or '-' or '@'))
        {
            return value;
        }

        return $"'{value}";
    }

    private static bool IsFormulaPrefixIgnored(char value)
    {
        return value == ' ' || char.IsControl(value) || char.GetUnicodeCategory(value) == UnicodeCategory.Format;
    }
}
