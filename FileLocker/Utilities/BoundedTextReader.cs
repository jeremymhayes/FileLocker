using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FileLocker;

internal readonly record struct BoundedTextReadResult(string Text, bool Truncated);

internal static class BoundedTextReader
{
    private const int DefaultBufferChars = 4096;

    internal static async Task<BoundedTextReadResult> ReadToEndAsync(
        TextReader reader,
        int maxChars,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (maxChars < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxChars), "The maximum read size cannot be negative.");
        }

        char[] buffer = new char[DefaultBufferChars];
        var builder = new StringBuilder(Math.Min(maxChars, DefaultBufferChars));
        bool truncated = false;

        try
        {
            while (true)
            {
                int charsRead = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (charsRead == 0)
                {
                    return new BoundedTextReadResult(builder.ToString(), truncated);
                }

                int remainingCapacity = maxChars - builder.Length;
                if (remainingCapacity > 0)
                {
                    int charsToAppend = Math.Min(charsRead, remainingCapacity);
                    builder.Append(buffer, 0, charsToAppend);
                    truncated |= charsToAppend < charsRead;
                }
                else
                {
                    truncated = true;
                }
            }
        }
        finally
        {
            Array.Clear(buffer);
        }
    }
}
