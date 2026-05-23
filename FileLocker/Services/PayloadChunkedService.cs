using Konscious.Security.Cryptography;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

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
    byte Flags);

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
    private const byte Version = 3;
    private const byte AlgorithmIdAes256Gcm = 1;
    private const byte KdfIdArgon2Id = 1;
    private const int SaltSize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int DekSize = 32;
    private const int NoncePrefixSize = 8;
    private const int MaxMetadataSize = 64 * 1024 * 1024;

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
        byte[] noncePrefix = GenerateRandomBytes(NoncePrefixSize);
        List<WrappedKeySlot> slots = [];

        try
        {
            slots = CreateWrappedSlots(dek, inputs);
            WriteHeader(output, inputs, noncePrefix, slots);

            using var chunkStream = new ChunkEncryptingStream(output, dek, noncePrefix, inputs.ChunkSize, cancellationToken);
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

        if (inputs.ChunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputs), "Payload chunk size must be greater than zero.");
        }
    }

    internal static OpenPayloadResult OpenPayload(Stream input, PayloadUnlockInputs unlockInputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(unlockInputs);
        cancellationToken.ThrowIfCancellationRequested();

        PayloadHeader header = ReadHeader(input);
        byte[] dek = UnwrapDek(header, unlockInputs);
        try
        {
            var plaintextStream = new ChunkDecryptingStream(input, dek, header.NoncePrefix, header.ChunkSize, cancellationToken);
            byte[] metadataLengthBuffer = ReadExactly(plaintextStream, sizeof(int));
            int metadataLength = BinaryPrimitives.ReadInt32LittleEndian(metadataLengthBuffer);
            ValidateMetadataLength(metadataLength);
            byte[] metadataBytes = ReadExactly(plaintextStream, metadataLength);
            return new OpenPayloadResult(header, metadataBytes, plaintextStream, dek);
        }
        catch
        {
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

    internal static PayloadHeader InspectHeader(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
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
        cancellationToken.ThrowIfCancellationRequested();

        PayloadHeader header = ReadHeader(input);
        byte[] dek = UnwrapDek(header, inputs.CurrentInputs);
        List<WrappedKeySlot> slots = [];

        try
        {
            slots = CreateWrappedSlots(
                dek,
                new PayloadWriteInputs(
                    inputs.NewPassword,
                    inputs.NewKeyfileBytes,
                    inputs.NewRecoveryKey,
                    header.ChunkSize,
                    header.Flags));

            WriteHeader(
                output,
                new PayloadWriteInputs(
                    inputs.NewPassword,
                    inputs.NewKeyfileBytes,
                    inputs.NewRecoveryKey,
                    header.ChunkSize,
                    header.Flags),
                header.NoncePrefix,
                slots);

            input.Position = header.CiphertextOffset;
            CopyRemainingStream(input, output, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            ClearWrappedSlots(slots);
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
            if (input.Read(buffer) != buffer.Length)
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

    private static void WriteHeader(Stream output, PayloadWriteInputs inputs, byte[] noncePrefix, IReadOnlyList<WrappedKeySlot> slots)
    {
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes(Magic));
        writer.Write(Version);
        writer.Write(AlgorithmIdAes256Gcm);
        writer.Write(KdfIdArgon2Id);
        writer.Write(inputs.Flags);
        writer.Write(3); // Argon2id iterations
        writer.Write(65536); // Argon2id memory (KB)
        writer.Write(Math.Clamp(Environment.ProcessorCount, 1, 8));
        writer.Write(inputs.ChunkSize);
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
        string magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (!string.Equals(magic, Magic, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Unsupported payload format.");
        }

        byte version = reader.ReadByte();
        if (version != Version)
        {
            throw new InvalidDataException($"Unsupported payload version: {version}.");
        }

        byte algorithmId = reader.ReadByte();
        byte kdfId = reader.ReadByte();
        byte flags = reader.ReadByte();
        int argonIterations = reader.ReadInt32();
        int argonMemoryKb = reader.ReadInt32();
        int argonParallelism = reader.ReadInt32();
        int chunkSize = reader.ReadInt32();
        if (algorithmId != AlgorithmIdAes256Gcm)
        {
            throw new InvalidDataException("Unsupported payload algorithm.");
        }

        if (kdfId != KdfIdArgon2Id)
        {
            throw new InvalidDataException("Unsupported payload key derivation.");
        }

        if (argonIterations <= 0 || argonMemoryKb <= 0 || argonParallelism <= 0)
        {
            throw new InvalidDataException("Invalid payload key-derivation parameters.");
        }

        if (chunkSize <= 0)
        {
            throw new InvalidDataException("Invalid payload chunk size.");
        }

        int noncePrefixLength = reader.ReadByte();
        if (noncePrefixLength != NoncePrefixSize)
        {
            throw new InvalidDataException("Invalid payload nonce prefix length.");
        }

        byte[] noncePrefix = ReadRequiredBytes(reader, noncePrefixLength, "nonce prefix");
        int slotCount = reader.ReadByte();
        if (slotCount <= 0)
        {
            throw new InvalidDataException("Payload header does not contain any key slots.");
        }

        var slots = new List<WrappedKeySlot>(slotCount);

        for (int i = 0; i < slotCount; i++)
        {
            slots.Add(new WrappedKeySlot(
                (PayloadKeySlotKind)reader.ReadByte(),
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

    private static byte[] ReadRequiredBytes(BinaryReader reader, int count, string description)
    {
        byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
        {
            throw new InvalidDataException($"Payload header is truncated while reading {description}.");
        }

        return bytes;
    }

    private static List<WrappedKeySlot> CreateWrappedSlots(byte[] dek, PayloadWriteInputs inputs)
    {
        var slots = new List<WrappedKeySlot>();
        try
        {
            slots.Add(CreateWrappedSlot(PayloadKeySlotKind.Password, inputs.Password, inputs.KeyfileBytes, dek));

            if (!string.IsNullOrWhiteSpace(inputs.RecoveryKey))
            {
                slots.Add(CreateWrappedSlot(PayloadKeySlotKind.Recovery, inputs.RecoveryKey, null, dek));
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

    private static WrappedKeySlot CreateWrappedSlot(PayloadKeySlotKind kind, string secretText, byte[]? keyfileBytes, byte[] dek)
    {
        byte[] salt = GenerateRandomBytes(SaltSize);
        byte[] nonce = GenerateRandomBytes(NonceSize);
        byte[] tag = new byte[TagSize];
        byte[] wrappedDek = new byte[dek.Length];
        byte[] kek = DeriveArgon2Key(secretText, salt, keyfileBytes);
        bool slotAssigned = false;

        try
        {
            using var aes = new AesGcm(kek, TagSize);
            aes.Encrypt(nonce, dek, wrappedDek, tag);
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

    private static byte[] UnwrapDek(PayloadHeader header, PayloadUnlockInputs unlockInputs)
    {
        foreach (WrappedKeySlot slot in header.Slots)
        {
            switch (slot.Kind)
            {
                case PayloadKeySlotKind.Password when !string.IsNullOrWhiteSpace(unlockInputs.Password):
                    if (TryUnwrapDek(slot, unlockInputs.Password!, unlockInputs.KeyfileBytes, out byte[] dekFromPassword))
                    {
                        return dekFromPassword;
                    }
                    break;

                case PayloadKeySlotKind.Recovery when !string.IsNullOrWhiteSpace(unlockInputs.RecoveryKey):
                    if (TryUnwrapDek(slot, unlockInputs.RecoveryKey!, null, out byte[] dekFromRecovery))
                    {
                        return dekFromRecovery;
                    }
                    break;
            }
        }

        throw new UnauthorizedAccessException("The supplied password, keyfile, or recovery key could not unlock this payload.");
    }

    private static bool TryUnwrapDek(WrappedKeySlot slot, string secretText, byte[]? keyfileBytes, out byte[] dek)
    {
        byte[] kek = DeriveArgon2Key(secretText, slot.Salt, keyfileBytes);
        dek = new byte[DekSize];

        try
        {
            using var aes = new AesGcm(kek, TagSize);
            aes.Decrypt(slot.Nonce, slot.WrappedDek, slot.Tag, dek);
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

    private static byte[] DeriveArgon2Key(string secretText, byte[] salt, byte[]? keyfileBytes)
    {
        byte[] passwordBytes = Encoding.UTF8.GetBytes(secretText);
        byte[]? combinedSecret = null;
        byte[] secret = BuildKdfSecret(passwordBytes, keyfileBytes, out combinedSecret);

        try
        {
            var argon2 = new Argon2id(secret)
            {
                Salt = salt,
                DegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8),
                Iterations = 3,
                MemorySize = 65536
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

    private static byte[] BuildKdfSecret(byte[] passwordBytes, byte[]? keyfileBytes, out byte[]? combinedSecret)
    {
        combinedSecret = null;
        if (keyfileBytes is not { Length: > 0 })
        {
            return passwordBytes;
        }

        combinedSecret = new byte[passwordBytes.Length + keyfileBytes.Length];
        Buffer.BlockCopy(passwordBytes, 0, combinedSecret, 0, passwordBytes.Length);
        Buffer.BlockCopy(keyfileBytes, 0, combinedSecret, passwordBytes.Length, keyfileBytes.Length);
        return SHA256.HashData(combinedSecret);
    }

    private static byte[] GenerateRandomBytes(int length)
    {
        byte[] bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static byte[] ReadExactly(Stream stream, int count)
    {
        byte[] buffer = new byte[count];
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

    private sealed class ChunkEncryptingStream : Stream
    {
        private readonly Stream _output;
        private readonly byte[] _dek;
        private readonly byte[] _noncePrefix;
        private readonly byte[] _buffer;
        private readonly CancellationToken _cancellationToken;
        private int _bufferLength;
        private int _chunkIndex;
        private bool _completed;

        internal ChunkEncryptingStream(Stream output, byte[] dek, byte[] noncePrefix, int chunkSize, CancellationToken cancellationToken)
        {
            _output = output;
            _dek = dek;
            _noncePrefix = noncePrefix;
            _buffer = new byte[chunkSize];
            _cancellationToken = cancellationToken;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => _output.Flush();

        public void Complete()
        {
            if (_completed)
            {
                return;
            }

            if (_bufferLength > 0)
            {
                WriteChunk(_buffer.AsSpan(0, _bufferLength));
                _bufferLength = 0;
            }

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
            while (count > 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();

                int writable = Math.Min(_buffer.Length - _bufferLength, count);
                Buffer.BlockCopy(buffer, offset, _buffer, _bufferLength, writable);
                _bufferLength += writable;
                offset += writable;
                count -= writable;

                if (_bufferLength == _buffer.Length)
                {
                    WriteChunk(_buffer);
                    _bufferLength = 0;
                }
            }
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            byte[] temp = buffer.ToArray();
            try
            {
                Write(temp, 0, temp.Length);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(temp);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CryptographicOperations.ZeroMemory(_buffer);
            }

            base.Dispose(disposing);
        }

        private void WriteChunk(ReadOnlySpan<byte> plaintext)
        {
            Span<byte> nonce = stackalloc byte[NonceSize];
            _noncePrefix.CopyTo(nonce[..NoncePrefixSize]);
            BinaryPrimitives.WriteInt32LittleEndian(nonce[NoncePrefixSize..], _chunkIndex);

            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[TagSize];
            byte[] aad = BitConverter.GetBytes(_chunkIndex);
            using (var aes = new AesGcm(_dek, TagSize))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
            }

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
        private byte[] _buffer = Array.Empty<byte>();
        private int _bufferOffset;
        private int _chunkIndex;
        private bool _completed;

        internal ChunkDecryptingStream(Stream input, byte[] dek, byte[] noncePrefix, int chunkSize, CancellationToken cancellationToken)
        {
            _input = input;
            _dek = dek;
            _noncePrefix = noncePrefix;
            _chunkSize = chunkSize;
            _cancellationToken = cancellationToken;
        }

        public override bool CanRead => true;
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
                Buffer.BlockCopy(_buffer, _bufferOffset, buffer, offset, readable);
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

            base.Dispose(disposing);
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
                Span<byte> nonce = stackalloc byte[NonceSize];
                _noncePrefix.CopyTo(nonce[..NoncePrefixSize]);
                BinaryPrimitives.WriteInt32LittleEndian(nonce[NoncePrefixSize..], _chunkIndex);
                byte[] aad = BitConverter.GetBytes(_chunkIndex);

                using (var aes = new AesGcm(_dek, TagSize))
                {
                    aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
                }

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
