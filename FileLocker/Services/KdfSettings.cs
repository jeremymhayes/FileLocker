using System;

namespace FileLocker;

internal static class KdfSettings
{
    internal const int Argon2IdIterations = 3;
    internal const int Argon2IdMemoryKb = 65_536;
    internal const int Pbkdf2FallbackIterations = 600_000;

    internal static int Argon2IdParallelism => Math.Clamp(Environment.ProcessorCount, 1, 8);
}
