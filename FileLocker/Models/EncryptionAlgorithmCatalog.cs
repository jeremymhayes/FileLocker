using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;

namespace FileLocker;

internal sealed record EncryptionAlgorithmDefinition(
    string Id,
    string DisplayName,
    string FileFormatName,
    byte PayloadAlgorithmId,
    int KeySizeBits,
    bool CanEncryptNewPayloads,
    bool CanReadPayloads,
    bool CanUsePngCarrier,
    string Status,
    string Detail,
    string BestFor,
    string SupportNote);

internal static class EncryptionAlgorithmCatalog
{
    internal const int MaxAlgorithmNameChars = 128;

    internal const string Aes256Gcm = "AES-256-GCM";
    internal const string ChaCha20Poly1305 = "ChaCha20-Poly1305";
    internal const string XChaCha20Poly1305 = "XChaCha20-Poly1305";
    internal const string Aes256GcmSiv = "AES-256-GCM-SIV";

    internal const byte PayloadAlgorithmAes256Gcm = 1;
    internal const byte PayloadAlgorithmChaCha20Poly1305 = 2;
    internal const byte PayloadAlgorithmXChaCha20Poly1305 = 3;
    internal const byte PayloadAlgorithmAes256GcmSiv = 4;

    internal static readonly EncryptionAlgorithmDefinition[] Definitions =
    [
        new(
            Aes256Gcm,
            Aes256Gcm,
            Aes256Gcm,
            PayloadAlgorithmAes256Gcm,
            256,
            CanEncryptNewPayloads: true,
            CanReadPayloads: true,
            CanUsePngCarrier: true,
            "Default",
            "Hardware-accelerated on most Windows machines. This is the safest default for normal file locking.",
            "Everyday files, archives, and folder packages.",
            "Default authenticated file encryption."),
        new(
            ChaCha20Poly1305,
            ChaCha20Poly1305,
            ChaCha20Poly1305,
            PayloadAlgorithmChaCha20Poly1305,
            256,
            CanEncryptNewPayloads: true,
            CanReadPayloads: true,
            CanUsePngCarrier: false,
            "Fast software",
            "Modern authenticated encryption that stays quick when AES hardware acceleration is not the bottleneck.",
            "Large jobs on mixed hardware or lower-power devices.",
            "Authenticated stream cipher using the platform implementation when available."),
        new(
            XChaCha20Poly1305,
            XChaCha20Poly1305,
            XChaCha20Poly1305,
            PayloadAlgorithmXChaCha20Poly1305,
            256,
            CanEncryptNewPayloads: false,
            CanReadPayloads: false,
            CanUsePngCarrier: false,
            "Unavailable",
            "Reserved for compatibility detection only until a maintained XChaCha20-Poly1305 AEAD implementation is available.",
            "Not available for new encryption or decryption in this build.",
            "Reserved payload id; no local cryptographic implementation is used."),
        new(
            Aes256GcmSiv,
            Aes256GcmSiv,
            Aes256GcmSiv,
            PayloadAlgorithmAes256GcmSiv,
            256,
            CanEncryptNewPayloads: true,
            CanReadPayloads: true,
            CanUsePngCarrier: false,
            "Misuse resistant",
            "AES with better protection if nonce handling ever goes wrong. It trades a little familiarity for extra margin.",
            "High-value local files and cautious cleanup workflows.",
            "Misuse-resistant authenticated encryption via Bouncy Castle.")
    ];

    internal static string Normalize(string? algorithm)
    {
        return TryNormalize(algorithm, out string normalized)
            ? normalized
            : Aes256Gcm;
    }

    internal static string NormalizeForNewPayload(string? algorithm)
    {
        if (TryGetDefinition(algorithm, out EncryptionAlgorithmDefinition? definition))
        {
            if (definition.CanEncryptNewPayloads)
            {
                return definition.DisplayName;
            }

            if (definition.CanReadPayloads)
            {
                throw new NotSupportedException($"{definition.DisplayName} is supported for reading existing payloads but is not available for new encrypted files.");
            }

            throw new NotSupportedException($"{definition.DisplayName} is recognized but is not supported by this build.");
        }

        throw new NotSupportedException("Unsupported file encryption algorithm.");
    }

    internal static bool TryNormalize(string? algorithm, out string normalized)
    {
        if (TryGetDefinition(algorithm, out EncryptionAlgorithmDefinition? definition))
        {
            normalized = definition.DisplayName;
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    internal static bool TryGetDefinition(string? algorithm, [NotNullWhen(true)] out EncryptionAlgorithmDefinition? definition)
    {
        string value = (algorithm ?? string.Empty).Trim();
        if (value.Length > MaxAlgorithmNameChars ||
            value.Any(character => char.IsControl(character) || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format))
        {
            definition = null;
            return false;
        }

        string compact = value.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

        string id = compact switch
        {
            "AESGCM" or "AES256GCM" => Aes256Gcm,
            "CHACHA20POLY1305" or "CHACHA20POLY1305IETF" => ChaCha20Poly1305,
            "XCHACHA20POLY1305" => XChaCha20Poly1305,
            "AESGCMSIV" or "AES256GCMSIV" => Aes256GcmSiv,
            _ => string.Empty
        };

        definition = Definitions.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        return definition is not null;
    }

    internal static bool TryGetDefinition(byte algorithmId, [NotNullWhen(true)] out EncryptionAlgorithmDefinition? definition)
    {
        definition = Definitions.FirstOrDefault(candidate => candidate.PayloadAlgorithmId == algorithmId);
        return definition is not null;
    }

    internal static bool IsAesGcm(string? algorithm) =>
        TryNormalize(algorithm, out string normalized) &&
        string.Equals(normalized, Aes256Gcm, StringComparison.Ordinal);

    internal static int GetKeySizeBits(string? algorithm)
    {
        if (TryGetDefinition(algorithm, out EncryptionAlgorithmDefinition? definition))
        {
            return definition.KeySizeBits;
        }

        throw new NotSupportedException("Unsupported file encryption algorithm.");
    }

    internal static int GetKeySizeBits(byte algorithmId)
    {
        if (TryGetDefinition(algorithmId, out EncryptionAlgorithmDefinition? definition))
        {
            return definition.KeySizeBits;
        }

        throw new NotSupportedException("Unsupported payload algorithm.");
    }

    internal static byte GetPayloadAlgorithmId(string? algorithm)
    {
        if (TryGetDefinition(algorithm, out EncryptionAlgorithmDefinition? definition))
        {
            return definition.PayloadAlgorithmId;
        }

        throw new NotSupportedException("Unsupported file encryption algorithm.");
    }

    internal static string GetFileFormatName(string? algorithm)
    {
        if (TryGetDefinition(algorithm, out EncryptionAlgorithmDefinition? definition))
        {
            return definition.FileFormatName;
        }

        throw new NotSupportedException("Unsupported file encryption algorithm.");
    }

    internal static byte GetNewPayloadAlgorithmId(string? algorithm)
    {
        string normalized = NormalizeForNewPayload(algorithm);
        return GetPayloadAlgorithmId(normalized);
    }

    internal static string GetDisplayName(byte algorithmId)
    {
        TryGetDefinition(algorithmId, out EncryptionAlgorithmDefinition? definition);
        return definition?.DisplayName ?? "Unknown";
    }

    internal static bool IsSupportedPayloadAlgorithm(byte algorithmId) =>
        TryGetDefinition(algorithmId, out EncryptionAlgorithmDefinition? definition) && definition.CanReadPayloads;

    internal static bool CanEncryptNewPayload(byte algorithmId) =>
        TryGetDefinition(algorithmId, out EncryptionAlgorithmDefinition? definition) && definition.CanEncryptNewPayloads;

    internal static bool CanEncryptNewPayload(string? algorithm) =>
        TryGetDefinition(algorithm, out EncryptionAlgorithmDefinition? definition) && definition.CanEncryptNewPayloads;
}
