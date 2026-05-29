using Konscious.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
// Both System.Security.Cryptography and Org.BouncyCastle.Crypto.Modes expose a
// ChaCha20Poly1305 type. This service uses the .NET BCL implementation (Span-based
// Encrypt/Decrypt); alias it explicitly so the BouncyCastle reference can coexist.
using ChaCha20Poly1305 = System.Security.Cryptography.ChaCha20Poly1305;

namespace FileLocker;

internal enum PayloadKeySlotKind : byte
{
    Password = 1,
    Recovery = 2
}

internal sealed record PayloadUnlockInputs(
    string? Password,
    byte[]? KeyfileBytes,
    string? RecoveryKey);

internal sealed record PayloadWriteInputs(
    string Password,
    byte[]? KeyfileBytes,
    string? RecoveryKey,
    int ChunkSize,
    byte Flags,
    byte AlgorithmId = EncryptionAlgorithmCatalog.PayloadAlgorithmAes256Gcm);

internal sealed record PayloadRotateInputs(
    PayloadUnlockInputs CurrentInputs,
    string NewPassword,
    byte[]? NewKeyfileBytes,
    string? NewRecoveryKey);

internal sealed record PayloadHeader(
    byte Version,
    byte AlgorithmId,
    byte KdfId,
    byte Flags,
    int ArgonIterations,
    int ArgonMemoryKb,
    int ArgonParallelism,
    int ChunkSize,
    byte[] NoncePrefix,
    IReadOnlyList<WrappedKeySlot> Slots,
    long CiphertextOffset);

internal sealed record PayloadKdfParameters(
    int ArgonIterations,
    int ArgonMemoryKb,
    int ArgonParallelism);

internal sealed record WrappedKeySlot(
    PayloadKeySlotKind Kind,
    byte[] Salt,
    byte[] Nonce,
    byte[] Tag,
    byte[] WrappedDek);

internal sealed class OpenPayloadResult : IDisposable
{
    private readonly byte[] _dek;

    internal OpenPayloadResult(PayloadHeader header, byte[] metadataBytes, Stream plaintextStream, byte[] dek)
    {
        Header = header;
        MetadataBytes = metadataBytes;
        PlaintextStream = plaintextStream;
        _dek = dek;
    }

    internal PayloadHeader Header { get; }

    internal byte[] MetadataBytes { get; }

    internal Stream PlaintextStream { get; }

    public void Dispose()
    {
        PlaintextStream.Dispose();
        CryptographicOperations.ZeroMemory(MetadataBytes);
        CryptographicOperations.ZeroMemory(_dek);
    }
}

internal static class PayloadChunkedService
{
    private const string Magic = "FLKR";
    internal const byte LegacyVersion = 3;
    internal const byte CurrentVersion = 4;
    private const byte HeaderAuthenticatedVersion = CurrentVersion;
    private const byte KdfIdArgon2Id = 1;
    private const int SaltSize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int DekSize = 32;
    private const int NoncePrefixSize = 8;
    private const int MaxMetadataSize = 64 * 1024 * 1024;
    private const int MaxChunkSize = 16 * 1024 * 1024;
    private const int MaxArgonIterations = 10;
    private const int MinArgonMemoryKb = 1024;
    private const int MaxArgonMemoryKb = 1024 * 1024;
    private const int MaxArgonParallelism = 32;
    private const int MaxKeySlots = 2;

    internal static void WritePayload(
        Stream output,
        byte[] metadataBytes,
        Action<Stream, CancellationToken> writeContent,
        PayloadWriteInputs inputs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(metadataBytes);
        ArgumentNullException.ThrowIfNull(writeContent);
        ValidateWriteInputs(inputs);
        ValidateMetadataLength(metadataBytes.Length);
        cancellationToken.ThrowIfCancellationRequested();

        byte[] dek = GenerateRandomBytes(DekSize);
        byte[] noncePrefix = GenerateRandomBytes(GetNoncePrefixSize(inputs.AlgorithmId));
        PayloadKdfParameters kdfParameters = CreateCurrentKdfParameters();
        List<WrappedKeySlot> slots = [];

        try
        {
            slots = CreateWrappedSlots(dek, inputs, kdfParameters, noncePrefix);
            WriteHeader(output, inputs, noncePrefix, slots, kdfParameters);

            using var chunkStream = new ChunkEncryptingStream(output, dek, noncePrefix, inputs.ChunkSize, inputs.AlgorithmId, cancellationToken);
            Span<byte> metadataLength = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(metadataLength, metadataBytes.Length);
            chunkStream.Write(metadataLength);
            chunkStream.Write(metadataBytes, 0, metadataBytes.Length);
            writeContent(chunkStream, cancellationToken);
            chunkStream.Complete();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            ClearWrappedSlots(slots);
        }
    }

    private static void ValidateWriteInputs(PayloadWriteInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (string.IsNullOrWhiteSpace(inputs.Password))
        {
            throw new ArgumentException("Payload password is required.", nameof(inputs));
        }

        ValidateSecretTextLength(inputs.Password, "Payload password");
        ValidateSecretTextLength(inputs.RecoveryKey, "Payload recovery key");

        if (inputs.ChunkSize <= 0 || inputs.ChunkSize > MaxChunkSize)
        {
            throw new ArgumentOutOfRangeException(nameof(inputs), "Payload chunk size must be greater than zero and no larger than the supported maximum.");
        }

        if (!EncryptionAlgorithmCatalog.CanEncryptNewPayload(inputs.AlgorithmId))
        {
            throw new NotSupportedException("Unsupported payload algorithm for new encryption.");
        }

        EnsurePayloadAlgorithmRuntimeSupported(inputs.AlgorithmId);
    }

    internal static OpenPayloadResult OpenPayload(Stream input, PayloadUnlockInputs unlockInputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(unlockInputs);
        ValidateUnlockInputs(unlockInputs);
        cancellationToken.ThrowIfCancellationRequested();

        PayloadHeader header = ReadHeader(input);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] dek = UnwrapDek(header, unlockInputs);
        ChunkDecryptingStream? plaintextStream = null;
        try
        {
            plaintextStream = new ChunkDecryptingStream(input, dek, header.NoncePrefix, header.ChunkSize, header.AlgorithmId, cancellationToken);
            byte[] metadataLengthBuffer = ReadExactly(plaintextStream, sizeof(int));
            int metadataLength = BinaryPrimitives.ReadInt32LittleEndian(metadataLengthBuffer);
            ValidateMetadataLength(metadataLength);
            byte[] metadataBytes = ReadExactly(plaintextStream, metadataLength);
            ChunkDecryptingStream resultStream = plaintextStream;
            plaintextStream = null;
            return new OpenPayloadResult(header, metadataBytes, resultStream, dek);
        }
        catch
        {
            plaintextStream?.Dispose();
            CryptographicOperations.ZeroMemory(dek);
            throw;
        }
    }

    internal static void ValidateMetadataLength(int metadataLength)
    {
        if (metadataLength < 0 || metadataLength > MaxMetadataSize)
        {
            throw new InvalidDataException("Payload metadata length is invalid.");
        }
    }

    internal static bool IsPayloadAlgorithmRuntimeSupported(byte algorithmId)
    {
        try
        {
            EnsurePayloadAlgorithmRuntimeSupported(algorithmId);
            return true;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    internal static bool CanEncryptNewPayloadOnThisRuntime(EncryptionAlgorithmDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.CanEncryptNewPayloads &&
            IsPayloadAlgorithmRuntimeSupported(definition.PayloadAlgorithmId);
    }

    internal static PayloadHeader InspectHeader(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanSeek)
        {
            throw new ArgumentException("Payload header inspection requires a seekable stream.", nameof(input));
        }

        long originalPosition = input.Position;
        try
        {
            input.Position = 0;
            return ReadHeader(input);
        }
        finally
        {
            input.Position = originalPosition;
        }
    }

    internal static void RotateKeys(Stream input, Stream output, PayloadRotateInputs inputs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ValidateRotateInputs(inputs);
        if (!input.CanSeek)
        {
            throw new ArgumentException("Payload key rotation requires a seekable input stream.", nameof(input));
        }

        cancellationToken.ThrowIfCancellationRequested();

        PayloadHeader header = ReadHeader(input);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] dek = UnwrapDek(header, inputs.CurrentInputs);
        List<WrappedKeySlot> slots = [];

        try
        {
            var kdfParameters = new PayloadKdfParameters(
                header.ArgonIterations,
                header.ArgonMemoryKb,
                header.ArgonParallelism);

            AuthenticatePayloadCiphertext(input, header, dek, cancellationToken);

            var newWriteInputs = new PayloadWriteInputs(
                inputs.NewPassword,
                inputs.NewKeyfileBytes,
                inputs.NewRecoveryKey,
                header.ChunkSize,
                header.Flags,
                header.AlgorithmId);

            slots = CreateWrappedSlots(
                dek,
                newWriteInputs,
                kdfParameters,
                header.NoncePrefix);

            WriteHeader(
                output,
                newWriteInputs,
                header.NoncePrefix,
                slots,
                kdfParameters);

            input.Position = header.CiphertextOffset;
            CopyRemainingStream(input, output, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            ClearWrappedSlots(slots);
        }
    }

    private static void AuthenticatePayloadCiphertext(
        Stream input,
        PayloadHeader header,
        byte[] dek,
        CancellationToken cancellationToken)
    {
        input.Position = header.CiphertextOffset;
        using var plaintext = new ChunkDecryptingStream(input, dek, header.NoncePrefix, header.ChunkSize, header.AlgorithmId, cancellationToken);
        byte[] buffer = new byte[131072];
        try
        {
            while (plaintext.Read(buffer, 0, buffer.Length) > 0)
            {
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static void CopyRemainingStream(Stream input, Stream output, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[131072];
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = input.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static void ValidateRotateInputs(PayloadRotateInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(inputs.CurrentInputs);
        if (string.IsNullOrWhiteSpace(inputs.NewPassword))
        {
            throw new ArgumentException("New payload password is required.", nameof(inputs));
        }

        ValidateUnlockInputs(inputs.CurrentInputs);
        ValidateSecretTextLength(inputs.NewPassword, "New payload password");
        ValidateSecretTextLength(inputs.NewRecoveryKey, "New payload recovery key");
    }

    private static void ValidateUnlockInputs(PayloadUnlockInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ValidateSecretTextLength(inputs.Password, "Payload password");
        ValidateSecretTextLength(inputs.RecoveryKey, "Payload recovery key");
        if (string.IsNullOrWhiteSpace(inputs.Password) &&
            string.IsNullOrWhiteSpace(inputs.RecoveryKey))
        {
            throw new ArgumentException("Payload password or recovery key is required.", nameof(inputs));
        }
    }

    private static void ValidateSecretTextLength(string? secretText, string description)
    {
        if (string.IsNullOrEmpty(secretText))
        {
            return;
        }

        KdfSecretValidator.Validate(secretText, description);
    }

    internal static bool LooksLikePayloadV3(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead || !input.CanSeek)
        {
            return false;
        }

        long originalPosition = input.Position;
        try
        {
            Span<byte> buffer = stackalloc byte[4];
            try
            {
                input.ReadExactly(buffer);
            }
            catch (EndOfStreamException)
            {
                return false;
            }

            return Encoding.ASCII.GetString(buffer) == Magic;
        }
        finally
        {
            input.Position = originalPosition;
        }
    }

    private static void WriteHeader(
        Stream output,
        PayloadWriteInputs inputs,
        byte[] noncePrefix,
        IReadOnlyList<WrappedKeySlot> slots,
        PayloadKdfParameters kdfParameters)
    {
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes(Magic));
        writer.Write(CurrentVersion);
        writer.Write(inputs.AlgorithmId);
        writer.Write(KdfIdArgon2Id);
        writer.Write(inputs.Flags);
        WriteInt32LittleEndian(writer, kdfParameters.ArgonIterations);
        WriteInt32LittleEndian(writer, kdfParameters.ArgonMemoryKb);
        WriteInt32LittleEndian(writer, kdfParameters.ArgonParallelism);
        WriteInt32LittleEndian(writer, inputs.ChunkSize);
        writer.Write((byte)noncePrefix.Length);
        writer.Write(noncePrefix);
        writer.Write((byte)slots.Count);
        foreach (WrappedKeySlot slot in slots)
        {
            writer.Write((byte)slot.Kind);
            writer.Write(slot.Salt);
            writer.Write(slot.Nonce);
            writer.Write(slot.Tag);
            writer.Write(slot.WrappedDek);
        }
    }

    private static PayloadHeader ReadHeader(Stream input)
    {
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        try
        {
            string magic = Encoding.ASCII.GetString(ReadRequiredBytes(reader, 4, "magic"));
            if (!string.Equals(magic, Magic, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Unsupported payload format.");
            }

            byte version = reader.ReadByte();
            if (version is not LegacyVersion and not CurrentVersion)
            {
                throw new InvalidDataException($"Unsupported payload version: {version}.");
            }

            byte algorithmId = reader.ReadByte();
            byte kdfId = reader.ReadByte();
            byte flags = reader.ReadByte();
            int argonIterations = ReadInt32LittleEndian(reader, "Argon2 iterations");
            int argonMemoryKb = ReadInt32LittleEndian(reader, "Argon2 memory");
            int argonParallelism = ReadInt32LittleEndian(reader, "Argon2 parallelism");
            int chunkSize = ReadInt32LittleEndian(reader, "chunk size");
            if (!EncryptionAlgorithmCatalog.IsSupportedPayloadAlgorithm(algorithmId))
            {
                throw new InvalidDataException("Unsupported payload algorithm.");
            }

            if (version == LegacyVersion && algorithmId != EncryptionAlgorithmCatalog.PayloadAlgorithmAes256Gcm)
            {
                throw new InvalidDataException("Legacy payload headers support only AES-256-GCM.");
            }

            if (kdfId != KdfIdArgon2Id)
            {
                throw new InvalidDataException("Unsupported payload key derivation.");
            }

            if (argonIterations <= 0 ||
                argonIterations > MaxArgonIterations ||
                argonMemoryKb < MinArgonMemoryKb ||
                argonMemoryKb > MaxArgonMemoryKb ||
                argonParallelism <= 0 ||
                argonParallelism > MaxArgonParallelism)
            {
                throw new InvalidDataException("Invalid payload key-derivation parameters.");
            }

            if (chunkSize <= 0 || chunkSize > MaxChunkSize)
            {
                throw new InvalidDataException("Invalid payload chunk size.");
            }

            int noncePrefixLength = reader.ReadByte();
            if (noncePrefixLength != GetNoncePrefixSize(algorithmId))
            {
                throw new InvalidDataException("Invalid payload nonce prefix length.");
            }

            byte[] noncePrefix = ReadRequiredBytes(reader, noncePrefixLength, "nonce prefix");
            int slotCount = reader.ReadByte();
            if (slotCount <= 0)
            {
                throw new InvalidDataException("Payload header does not contain any key slots.");
            }

            if (slotCount > MaxKeySlots)
            {
                throw new InvalidDataException("Payload header contains too many key slots.");
            }

            var slots = new List<WrappedKeySlot>(slotCount);
            var slotKinds = new HashSet<PayloadKeySlotKind>();

            for (int i = 0; i < slotCount; i++)
            {
                var slotKind = (PayloadKeySlotKind)reader.ReadByte();
                if (slotKind is not PayloadKeySlotKind.Password and not PayloadKeySlotKind.Recovery)
                {
                    throw new InvalidDataException("Unsupported payload key-slot kind.");
                }

                if (!slotKinds.Add(slotKind))
                {
                    throw new InvalidDataException("Payload header contains duplicate key-slot kinds.");
                }

                slots.Add(new WrappedKeySlot(
                    slotKind,
                    ReadRequiredBytes(reader, SaltSize, "key-slot salt"),
                    ReadRequiredBytes(reader, NonceSize, "key-slot nonce"),
                    ReadRequiredBytes(reader, TagSize, "key-slot tag"),
                    ReadRequiredBytes(reader, DekSize, "wrapped data-encryption key")));
            }

            return new PayloadHeader(
                version,
                algorithmId,
                kdfId,
                flags,
                argonIterations,
                argonMemoryKb,
                argonParallelism,
                chunkSize,
                noncePrefix,
                slots,
                input.Position);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("Payload header is truncated.", ex);
        }
    }

    private static byte[] ReadRequiredBytes(BinaryReader reader, int count, string description)
    {
        byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
        {
            throw new InvalidDataException($"Payload header is truncated while reading {description}.");
        }

        return bytes;
    }

    private static void WriteInt32LittleEndian(BinaryWriter writer, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        writer.Write(buffer);
    }

    private static int ReadInt32LittleEndian(BinaryReader reader, string description)
    {
        byte[] bytes = ReadRequiredBytes(reader, sizeof(int), description);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static List<WrappedKeySlot> CreateWrappedSlots(
        byte[] dek,
        PayloadWriteInputs inputs,
        PayloadKdfParameters kdfParameters,
        byte[] noncePrefix)
    {
        var slots = new List<WrappedKeySlot>();
        try
        {
            slots.Add(CreateWrappedSlot(PayloadKeySlotKind.Password, inputs.Password, inputs.KeyfileBytes, dek, inputs, kdfParameters, noncePrefix));

            if (!string.IsNullOrWhiteSpace(inputs.RecoveryKey))
            {
                slots.Add(CreateWrappedSlot(PayloadKeySlotKind.Recovery, inputs.RecoveryKey, null, dek, inputs, kdfParameters, noncePrefix));
            }

            return slots;
        }
        catch
        {
            ClearWrappedSlots(slots);
            throw;
        }
    }

    private static void ClearWrappedSlots(IEnumerable<WrappedKeySlot> slots)
    {
        foreach (WrappedKeySlot slot in slots)
        {
            CryptographicOperations.ZeroMemory(slot.Salt);
            CryptographicOperations.ZeroMemory(slot.Nonce);
            CryptographicOperations.ZeroMemory(slot.Tag);
            CryptographicOperations.ZeroMemory(slot.WrappedDek);
        }
    }

    private static WrappedKeySlot CreateWrappedSlot(
        PayloadKeySlotKind kind,
        string secretText,
        byte[]? keyfileBytes,
        byte[] dek,
        PayloadWriteInputs inputs,
        PayloadKdfParameters kdfParameters,
        byte[] noncePrefix)
    {
        byte[] salt = GenerateRandomBytes(SaltSize);
        byte[] nonce = GenerateRandomBytes(NonceSize);
        byte[] tag = new byte[TagSize];
        byte[] wrappedDek = new byte[dek.Length];
        byte[] kek = DeriveArgon2Key(secretText, salt, keyfileBytes, kdfParameters);
        bool slotAssigned = false;

        try
        {
            byte[] aad = BuildKeySlotAad(
                CurrentVersion,
                inputs.AlgorithmId,
                KdfIdArgon2Id,
                inputs.Flags,
                kdfParameters,
                inputs.ChunkSize,
                noncePrefix,
                kind);
            using var aes = new AesGcm(kek, TagSize);
            aes.Encrypt(nonce, dek, wrappedDek, tag, aad);
            WrappedKeySlot slot = new(kind, salt, nonce, tag, wrappedDek);
            slotAssigned = true;
            return slot;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
            if (!slotAssigned)
            {
                CryptographicOperations.ZeroMemory(salt);
                CryptographicOperations.ZeroMemory(nonce);
                CryptographicOperations.ZeroMemory(tag);
                CryptographicOperations.ZeroMemory(wrappedDek);
            }
        }
    }

    private static byte[] BuildKeySlotAad(PayloadHeader header, WrappedKeySlot slot)
    {
        if (header.Version < HeaderAuthenticatedVersion)
        {
            return Array.Empty<byte>();
        }

        var kdfParameters = new PayloadKdfParameters(
            header.ArgonIterations,
            header.ArgonMemoryKb,
            header.ArgonParallelism);

        return BuildKeySlotAad(
            header.Version,
            header.AlgorithmId,
            header.KdfId,
            header.Flags,
            kdfParameters,
            header.ChunkSize,
            header.NoncePrefix,
            slot.Kind);
    }

    private static byte[] BuildKeySlotAad(
        byte version,
        byte algorithmId,
        byte kdfId,
        byte flags,
        PayloadKdfParameters kdfParameters,
        int chunkSize,
        byte[] noncePrefix,
        PayloadKeySlotKind slotKind)
    {
        ArgumentNullException.ThrowIfNull(noncePrefix);

        byte[] aad = new byte[Magic.Length + 1 + 1 + 1 + 1 + (sizeof(int) * 4) + 1 + noncePrefix.Length + 1];
        int offset = 0;
        offset += Encoding.ASCII.GetBytes(Magic, 0, Magic.Length, aad, offset);
        aad[offset++] = version;
        aad[offset++] = algorithmId;
        aad[offset++] = kdfId;
        aad[offset++] = flags;
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(offset, sizeof(int)), kdfParameters.ArgonIterations);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(offset, sizeof(int)), kdfParameters.ArgonMemoryKb);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(offset, sizeof(int)), kdfParameters.ArgonParallelism);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(offset, sizeof(int)), chunkSize);
        offset += sizeof(int);
        aad[offset++] = (byte)noncePrefix.Length;
        Buffer.BlockCopy(noncePrefix, 0, aad, offset, noncePrefix.Length);
        offset += noncePrefix.Length;
        aad[offset] = (byte)slotKind;
        return aad;
    }

    private static byte[] UnwrapDek(PayloadHeader header, PayloadUnlockInputs unlockInputs)
    {
        foreach (WrappedKeySlot slot in header.Slots)
        {
            switch (slot.Kind)
            {
                case PayloadKeySlotKind.Password when !string.IsNullOrWhiteSpace(unlockInputs.Password):
                    if (TryUnwrapDek(header, slot, unlockInputs.Password!, unlockInputs.KeyfileBytes, out byte[] dekFromPassword))
                    {
                        return dekFromPassword;
                    }
                    break;

                case PayloadKeySlotKind.Recovery when !string.IsNullOrWhiteSpace(unlockInputs.RecoveryKey):
                    if (TryUnwrapDek(header, slot, unlockInputs.RecoveryKey!, null, out byte[] dekFromRecovery))
                    {
                        return dekFromRecovery;
                    }
                    break;
            }
        }

        throw new UnauthorizedAccessException("The supplied password, keyfile, or recovery key could not unlock this payload.");
    }

    private static bool TryUnwrapDek(
        PayloadHeader header,
        WrappedKeySlot slot,
        string secretText,
        byte[]? keyfileBytes,
        out byte[] dek)
    {
        byte[] kek = DeriveArgon2Key(
            secretText,
            slot.Salt,
            keyfileBytes,
            new PayloadKdfParameters(header.ArgonIterations, header.ArgonMemoryKb, header.ArgonParallelism));
        dek = new byte[DekSize];

        try
        {
            byte[] aad = BuildKeySlotAad(header, slot);
            using var aes = new AesGcm(kek, TagSize);
            aes.Decrypt(slot.Nonce, slot.WrappedDek, slot.Tag, dek, aad);
            return true;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(dek);
            dek = Array.Empty<byte>();
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private static byte[] DeriveArgon2Key(
        string secretText,
        byte[] salt,
        byte[]? keyfileBytes,
        PayloadKdfParameters kdfParameters)
    {
        byte[] passwordBytes = Encoding.UTF8.GetBytes(secretText);
        byte[]? combinedSecret = null;
        byte[] secret = KdfSecretMaterial.Build(passwordBytes, keyfileBytes, out combinedSecret);

        try
        {
            var argon2 = new Argon2id(secret)
            {
                Salt = salt,
                DegreeOfParallelism = kdfParameters.ArgonParallelism,
                Iterations = kdfParameters.ArgonIterations,
                MemorySize = kdfParameters.ArgonMemoryKb
            };

            return argon2.GetBytes(DekSize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            if (combinedSecret != null)
            {
                CryptographicOperations.ZeroMemory(combinedSecret);
            }

            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static byte[] GenerateRandomBytes(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        byte[] bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static PayloadKdfParameters CreateCurrentKdfParameters() =>
        new(KdfSettings.Argon2IdIterations, KdfSettings.Argon2IdMemoryKb, KdfSettings.Argon2IdParallelism);

    private static int GetNoncePrefixSize(byte algorithmId) => NoncePrefixSize;

    private static byte[] ReadExactly(Stream stream, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        byte[] buffer = new byte[count];
        try
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = stream.Read(buffer, totalRead, count - totalRead);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                totalRead += read;
            }

            return buffer;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(buffer);
            throw;
        }
    }

    private static void ValidateBufferRange(byte[] buffer, int offset, int count, string bufferDescription)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
        {
            throw new ArgumentException($"The offset and count exceed the {bufferDescription} buffer length.", nameof(count));
        }
    }

    private static void EncryptChunk(
        byte algorithmId,
        byte[] key,
        byte[] noncePrefix,
        int chunkIndex,
        ReadOnlySpan<byte> plaintext,
        byte[] ciphertext,
        byte[] tag,
        byte[] aad)
    {
        switch (algorithmId)
        {
            case EncryptionAlgorithmCatalog.PayloadAlgorithmAes256Gcm:
            {
                Span<byte> nonce = stackalloc byte[NonceSize];
                BuildChunkNonce(nonce, noncePrefix, chunkIndex);
                using var aes = new AesGcm(key, TagSize);
                aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
                return;
            }
            case EncryptionAlgorithmCatalog.PayloadAlgorithmChaCha20Poly1305:
            {
                EnsureChaCha20Poly1305Supported();
                Span<byte> nonce = stackalloc byte[NonceSize];
                BuildChunkNonce(nonce, noncePrefix, chunkIndex);
                using var chacha = new ChaCha20Poly1305(key);
                chacha.Encrypt(nonce, plaintext, ciphertext, tag, aad);
                return;
            }
            case EncryptionAlgorithmCatalog.PayloadAlgorithmAes256GcmSiv:
            {
                byte[] nonce = BuildChunkNonceBytes(NonceSize, noncePrefix, chunkIndex);
                try
                {
                    EncryptBouncyAead(new GcmSivBlockCipher(), key, nonce, plaintext, ciphertext, tag, aad);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(nonce);
                }

                return;
            }
            default:
                throw new InvalidDataException("Unsupported payload algorithm.");
        }
    }

    private static void DecryptChunk(
        byte algorithmId,
        byte[] key,
        byte[] noncePrefix,
        int chunkIndex,
        byte[] ciphertext,
        byte[] tag,
        byte[] plaintext,
        byte[] aad)
    {
        switch (algorithmId)
        {
            case EncryptionAlgorithmCatalog.PayloadAlgorithmAes256Gcm:
            {
                Span<byte> nonce = stackalloc byte[NonceSize];
                BuildChunkNonce(nonce, noncePrefix, chunkIndex);
                using var aes = new AesGcm(key, TagSize);
                aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
                return;
            }
            case EncryptionAlgorithmCatalog.PayloadAlgorithmChaCha20Poly1305:
            {
                EnsureChaCha20Poly1305Supported();
                Span<byte> nonce = stackalloc byte[NonceSize];
                BuildChunkNonce(nonce, noncePrefix, chunkIndex);
                using var chacha = new ChaCha20Poly1305(key);
                chacha.Decrypt(nonce, ciphertext, tag, plaintext, aad);
                return;
            }
            case EncryptionAlgorithmCatalog.PayloadAlgorithmAes256GcmSiv:
            {
                byte[] nonce = BuildChunkNonceBytes(NonceSize, noncePrefix, chunkIndex);
                try
                {
                    DecryptBouncyAead(new GcmSivBlockCipher(), key, nonce, ciphertext, tag, plaintext, aad);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(nonce);
                }

                return;
            }
            default:
                throw new InvalidDataException("Unsupported payload algorithm.");
        }
    }

    private static void EnsureChaCha20Poly1305Supported()
    {
        if (!ChaCha20Poly1305.IsSupported)
        {
            throw new PlatformNotSupportedException("ChaCha20-Poly1305 is not supported on this Windows runtime.");
        }
    }

    private static void EnsurePayloadAlgorithmRuntimeSupported(byte algorithmId)
    {
        // Every payload algorithm uses AES-GCM for authenticated key-slot wrapping.
        EnsureAesGcmSupported();

        switch (algorithmId)
        {
            case EncryptionAlgorithmCatalog.PayloadAlgorithmAes256Gcm:
                return;
            case EncryptionAlgorithmCatalog.PayloadAlgorithmChaCha20Poly1305:
                EnsureChaCha20Poly1305Supported();
                return;
            case EncryptionAlgorithmCatalog.PayloadAlgorithmAes256GcmSiv:
                return;
            default:
                throw new InvalidDataException("Unsupported payload algorithm.");
        }
    }

    private static void EnsureAesGcmSupported()
    {
        if (!AesGcm.IsSupported)
        {
            throw new PlatformNotSupportedException("AES-GCM is not supported on this Windows runtime.");
        }
    }

    private static byte[] BuildChunkNonceBytes(int nonceSize, byte[] noncePrefix, int chunkIndex)
    {
        byte[] nonce = new byte[nonceSize];
        BuildChunkNonce(nonce, noncePrefix, chunkIndex);
        return nonce;
    }

    private static void BuildChunkNonce(Span<byte> nonce, byte[] noncePrefix, int chunkIndex)
    {
        ValidateChunkIndex(chunkIndex);

        if (nonce.Length != noncePrefix.Length + sizeof(int))
        {
            throw new InvalidDataException("Invalid payload nonce prefix length.");
        }

        noncePrefix.CopyTo(nonce[..noncePrefix.Length]);
        BinaryPrimitives.WriteInt32LittleEndian(nonce[noncePrefix.Length..], chunkIndex);
    }

    private static byte[] BuildChunkAad(int chunkIndex)
    {
        ValidateChunkIndex(chunkIndex);

        byte[] aad = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(aad, chunkIndex);
        return aad;
    }

    private static void ValidateChunkIndex(int chunkIndex)
    {
        if (chunkIndex < 0 || chunkIndex == int.MaxValue)
        {
            throw new InvalidDataException("Payload chunk counter limit exceeded.");
        }
    }

    private static void EncryptBouncyAead(
        IAeadCipher cipher,
        byte[] key,
        byte[] nonce,
        ReadOnlySpan<byte> plaintext,
        byte[] ciphertext,
        byte[] tag,
        byte[] aad)
    {
        byte[] plaintextBytes = plaintext.ToArray();
        byte[]? output = null;
        try
        {
            cipher.Init(true, new AeadParameters(new KeyParameter(key), TagSize * 8, nonce, aad));
            output = new byte[cipher.GetOutputSize(plaintextBytes.Length)];
            int length = cipher.ProcessBytes(plaintextBytes, 0, plaintextBytes.Length, output, 0);
            length += cipher.DoFinal(output, length);
            if (length != plaintextBytes.Length + TagSize)
            {
                throw new CryptographicException("Authenticated encryption produced an unexpected output length.");
            }

            Buffer.BlockCopy(output, 0, ciphertext, 0, ciphertext.Length);
            Buffer.BlockCopy(output, ciphertext.Length, tag, 0, tag.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
            ClearBuffer(output);
        }
    }

    private static void DecryptBouncyAead(
        IAeadCipher cipher,
        byte[] key,
        byte[] nonce,
        byte[] ciphertext,
        byte[] tag,
        byte[] plaintext,
        byte[] aad)
    {
        byte[] combined = new byte[ciphertext.Length + tag.Length];
        byte[]? output = null;
        try
        {
            Buffer.BlockCopy(ciphertext, 0, combined, 0, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, combined, ciphertext.Length, tag.Length);

            cipher.Init(false, new AeadParameters(new KeyParameter(key), TagSize * 8, nonce, aad));
            output = new byte[cipher.GetOutputSize(combined.Length)];
            int length = cipher.ProcessBytes(combined, 0, combined.Length, output, 0);
            length += cipher.DoFinal(output, length);
            if (length != plaintext.Length)
            {
                throw new CryptographicException("Authenticated decryption produced an unexpected output length.");
            }

            Buffer.BlockCopy(output, 0, plaintext, 0, plaintext.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(combined);
            ClearBuffer(output);
        }
    }

    private static void ClearBuffer(byte[]? buffer)
    {
        if (buffer is { Length: > 0 })
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private sealed class ChunkEncryptingStream : Stream
    {
        private readonly Stream _output;
        private readonly byte[] _dek;
        private readonly byte[] _noncePrefix;
        private readonly byte[] _buffer;
        private readonly CancellationToken _cancellationToken;
        private readonly byte _algorithmId;
        private int _bufferLength;
        private int _chunkIndex;
        private bool _completed;
        private bool _disposed;

        internal ChunkEncryptingStream(Stream output, byte[] dek, byte[] noncePrefix, int chunkSize, byte algorithmId, CancellationToken cancellationToken)
        {
            _output = output;
            _dek = dek;
            _noncePrefix = noncePrefix;
            _buffer = new byte[chunkSize];
            _algorithmId = algorithmId;
            _cancellationToken = cancellationToken;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed && !_completed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => _output.Flush();

        public void Complete()
        {
            if (_completed)
            {
                return;
            }

            EnsureWritable();
            _cancellationToken.ThrowIfCancellationRequested();

            if (_bufferLength > 0)
            {
                Span<byte> pendingPlaintext = _buffer.AsSpan(0, _bufferLength);
                try
                {
                    WriteChunk(pendingPlaintext);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pendingPlaintext);
                    _bufferLength = 0;
                }
            }

            _cancellationToken.ThrowIfCancellationRequested();
            Span<byte> sentinel = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(sentinel, 0);
            _output.Write(sentinel);
            _completed = true;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateBufferRange(buffer, offset, count, "source");
            Write(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureWritable();

            while (!buffer.IsEmpty)
            {
                _cancellationToken.ThrowIfCancellationRequested();

                int writable = Math.Min(_buffer.Length - _bufferLength, buffer.Length);
                buffer[..writable].CopyTo(_buffer.AsSpan(_bufferLength));
                _bufferLength += writable;
                buffer = buffer[writable..];

                if (_bufferLength == _buffer.Length)
                {
                    try
                    {
                        WriteChunk(_buffer);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(_buffer);
                        _bufferLength = 0;
                    }
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CryptographicOperations.ZeroMemory(_buffer);
                _disposed = true;
            }

            base.Dispose(disposing);
        }

        private void EnsureWritable()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ChunkEncryptingStream));
            }

            if (_completed)
            {
                throw new InvalidOperationException("Payload write stream has already completed.");
            }
        }

        private void WriteChunk(ReadOnlySpan<byte> plaintext)
        {
            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[TagSize];
            byte[] aad = BuildChunkAad(_chunkIndex);
            EncryptChunk(_algorithmId, _dek, _noncePrefix, _chunkIndex, plaintext, ciphertext, tag, aad);

            Span<byte> lengthBuffer = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(lengthBuffer, plaintext.Length);
            _output.Write(lengthBuffer);
            _output.Write(tag, 0, tag.Length);
            _output.Write(ciphertext, 0, ciphertext.Length);
            _chunkIndex++;
        }
    }

    private sealed class ChunkDecryptingStream : Stream
    {
        private readonly Stream _input;
        private readonly byte[] _dek;
        private readonly byte[] _noncePrefix;
        private readonly int _chunkSize;
        private readonly CancellationToken _cancellationToken;
        private readonly byte _algorithmId;
        private byte[] _buffer = Array.Empty<byte>();
        private int _bufferOffset;
        private int _chunkIndex;
        private bool _completed;
        private bool _disposed;

        internal ChunkDecryptingStream(Stream input, byte[] dek, byte[] noncePrefix, int chunkSize, byte algorithmId, CancellationToken cancellationToken)
        {
            _input = input;
            _dek = dek;
            _noncePrefix = noncePrefix;
            _chunkSize = chunkSize;
            _algorithmId = algorithmId;
            _cancellationToken = cancellationToken;
        }

        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferRange(buffer, offset, count, "destination");
            EnsureReadable();

            if (count == 0)
            {
                return 0;
            }

            int totalWritten = 0;
            while (count > 0)
            {
                if (_bufferOffset >= _buffer.Length)
                {
                    if (_completed)
                    {
                        break;
                    }

                    FillBuffer();
                    if (_buffer.Length == 0 && _completed)
                    {
                        break;
                    }
                }

                int readable = Math.Min(count, _buffer.Length - _bufferOffset);
                int sourceOffset = _bufferOffset;
                Buffer.BlockCopy(_buffer, sourceOffset, buffer, offset, readable);
                CryptographicOperations.ZeroMemory(_buffer.AsSpan(sourceOffset, readable));
                _bufferOffset += readable;
                offset += readable;
                count -= readable;
                totalWritten += readable;
            }

            return totalWritten;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _buffer.Length > 0)
            {
                CryptographicOperations.ZeroMemory(_buffer);
            }

            _disposed = true;
            base.Dispose(disposing);
        }

        private void EnsureReadable()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ChunkDecryptingStream));
            }
        }

        private void FillBuffer()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            byte[] lengthBytes = PayloadChunkedService.ReadExactly(_input, sizeof(int));
            int chunkLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (chunkLength < 0 || chunkLength > _chunkSize)
            {
                throw new InvalidDataException("Invalid payload chunk length.");
            }

            if (chunkLength == 0)
            {
                _completed = true;
                if (_buffer.Length > 0)
                {
                    CryptographicOperations.ZeroMemory(_buffer);
                }

                _buffer = Array.Empty<byte>();
                _bufferOffset = 0;
                return;
            }

            byte[] tag = PayloadChunkedService.ReadExactly(_input, TagSize);
            byte[] ciphertext = PayloadChunkedService.ReadExactly(_input, chunkLength);
            byte[] plaintext = new byte[chunkLength];
            bool plaintextAssigned = false;

            try
            {
                byte[] aad = BuildChunkAad(_chunkIndex);
                DecryptChunk(_algorithmId, _dek, _noncePrefix, _chunkIndex, ciphertext, tag, plaintext, aad);

                if (_buffer.Length > 0)
                {
                    CryptographicOperations.ZeroMemory(_buffer);
                }

                _buffer = plaintext;
                plaintextAssigned = true;
                _bufferOffset = 0;
                _chunkIndex++;
            }
            finally
            {
                if (!plaintextAssigned)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }
    }
}
