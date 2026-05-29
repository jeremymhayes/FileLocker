using FileLocker;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Konscious.Security.Cryptography;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;


namespace FileLocker
{
    public sealed partial class MainWindow : Window
    {
        // --- Encryption/Decryption Logic ---
        private ProcessingRunOptions CaptureProcessingRunOptions()
        {
            string keyfilePath = KeyfilePathBox.Text?.Trim() ?? string.Empty;
            return CaptureProcessingRunOptions(keyfilePath, ReadKeyfileBytesIfConfigured(keyfilePath));
        }

        private ProcessingRunOptions CaptureProcessingRunOptions(
            string keyfilePath,
            byte[]? keyfileBytes,
            bool encryptingNewPayload = true)
        {
            keyfilePath = keyfilePath.Trim();
            bool useDecryptPageOutputOptions = _currentSection == AppSection.DecryptFiles;

            ProcessingRunOptions rawOptions = new(
                IsCompressModeEnabled,
                IsScrambleNamesEnabled,
                IsSteganographyEnabled,
                GetComboContent(AlgorithmCombo) ?? EncryptionAlgorithmCatalog.Aes256Gcm,
                GetComboContent(OperationModeCombo) ?? "Encrypt / Decrypt",
                ParseKeySizeSelection(),
                RemoveOriginalsToggle.IsOn,
                SecureDeleteOriginalsToggle.IsOn,
                VerifyAfterWriteToggle.IsOn,
                OutputCustomLocationRadio.IsChecked == true,
                EncryptOutputFolderBox.Text?.Trim() ?? string.Empty,
                useDecryptPageOutputOptions && DecryptSaveNextToEncryptedToggle != null && !DecryptSaveNextToEncryptedToggle.IsOn,
                useDecryptPageOutputOptions ? DecryptOutputLocationBox?.Text?.Trim() ?? string.Empty : string.Empty,
                DecryptRestoreOriginalFilenamesToggle == null || DecryptRestoreOriginalFilenamesToggle.IsOn,
                useDecryptPageOutputOptions && DecryptPreserveFolderStructureToggle != null && DecryptPreserveFolderStructureToggle.IsOn,
                PackageFoldersToggle.IsOn,
                AppPreferencesStore.NormalizeOutputTimestampPolicy((OutputTimestampPolicyCombo.SelectedItem as ComboBoxItem)?.Content as string),
                BackupFolderBox.Text?.Trim() ?? string.Empty,
                string.IsNullOrWhiteSpace(keyfilePath) ? null : keyfilePath,
                keyfileBytes,
                string.IsNullOrWhiteSpace(RecoveryKeyBox.Text) ? null : RecoveryKeyBox.Text.Trim(),
                ProfileCombo.SelectedItem as string ?? "Recommended",
                new MetadataOverridesSnapshot(
                    MetadataNameBox.Text?.Trim() ?? string.Empty,
                    MetadataNotesBox.Text?.Trim() ?? string.Empty,
                    MetadataRandomizeToggle.IsOn,
                    MetadataCreatedBox.Text ?? string.Empty,
                    MetadataModifiedBox.Text ?? string.Empty));

            return NormalizeRunOptionsForCurrentMode(rawOptions, encryptingNewPayload);
        }

        private ProcessingRunOptions NormalizeRunOptionsForCurrentMode(
            ProcessingRunOptions options,
            bool encryptingNewPayload = true)
        {
            byte[]? originalKeyfileBytes = options.KeyfileBytes;
            string algorithm = encryptingNewPayload
                ? EncryptionAlgorithmCatalog.NormalizeForNewPayload(options.Algorithm)
                : EncryptionAlgorithmCatalog.Normalize(options.Algorithm);
            if (encryptingNewPayload && options.UseSteganography && !EncryptionAlgorithmCatalog.IsAesGcm(algorithm))
            {
                throw new InvalidOperationException($"PNG carrier output currently uses {EncryptionAlgorithmCatalog.Aes256Gcm}. Turn off PNG carrier output to use another cipher.");
            }

            ProcessingRunOptions normalized = options with
            {
                Algorithm = algorithm,
                KeySizeBits = EncryptionAlgorithmCatalog.GetKeySizeBits(algorithm),
                Metadata = NormalizeMetadataOverrides(options.Metadata)
            };

            if (encryptingNewPayload && _currentExperienceLevel == UserExperienceLevel.Beginner)
            {
                normalized = normalized with
                {
                    CompressFiles = true,
                    ScrambleNames = false,
                    UseSteganography = false,
                    RemoveOriginalsAfterSuccess = false,
                    SecureDeleteOriginals = false,
                    VerifyAfterWrite = true,
                    UseCustomEncryptOutputDirectory = normalized.UseCustomEncryptOutputDirectory,
                    EncryptOutputDirectory = normalized.EncryptOutputDirectory,
                    PackageFolders = false,
                    BackupFolderPath = string.Empty,
                    KeyfilePath = null,
                    KeyfileBytes = null,
                    RecoveryKey = null,
                    Metadata = new MetadataOverridesSnapshot(string.Empty, string.Empty, false, string.Empty, string.Empty)
                };
            }
            else if (encryptingNewPayload && _currentExperienceLevel == UserExperienceLevel.Intermediate)
            {
                normalized = normalized with
                {
                    ScrambleNames = false,
                    UseSteganography = false,
                    SecureDeleteOriginals = false,
                    UseCustomEncryptOutputDirectory = normalized.UseCustomEncryptOutputDirectory,
                    EncryptOutputDirectory = normalized.EncryptOutputDirectory,
                    PackageFolders = false,
                    KeyfilePath = null,
                    KeyfileBytes = null,
                    RecoveryKey = null,
                    Metadata = new MetadataOverridesSnapshot(string.Empty, string.Empty, false, string.Empty, string.Empty)
                };
            }

            if (normalized.RemoveOriginalsAfterSuccess && !normalized.VerifyAfterWrite)
            {
                normalized = normalized with { VerifyAfterWrite = true };
            }

            if (!normalized.RemoveOriginalsAfterSuccess && normalized.SecureDeleteOriginals)
            {
                normalized = normalized with { SecureDeleteOriginals = false };
            }

            if (originalKeyfileBytes is { Length: > 0 } &&
                !ReferenceEquals(normalized.KeyfileBytes, originalKeyfileBytes))
            {
                CryptographicOperations.ZeroMemory(originalKeyfileBytes);
            }

            return normalized;
        }

        private static MetadataOverridesSnapshot NormalizeMetadataOverrides(MetadataOverridesSnapshot metadata)
        {
            return new MetadataOverridesSnapshot(
                NormalizeMetadataText(metadata.Label, MaxMetadataLabelChars, allowLineBreaks: false),
                NormalizeMetadataText(metadata.Notes, MaxMetadataNotesChars, allowLineBreaks: true),
                metadata.Randomize,
                NormalizeMetadataText(metadata.CreatedText, MaxMetadataDateTextChars, allowLineBreaks: false),
                NormalizeMetadataText(metadata.ModifiedText, MaxMetadataDateTextChars, allowLineBreaks: false));
        }

        private static string NormalizeMetadataText(string? value, int maxChars, bool allowLineBreaks)
        {
            if (string.IsNullOrWhiteSpace(value) || maxChars <= 0)
            {
                return string.Empty;
            }

            string trimmed = value.Trim();
            var builder = new StringBuilder(Math.Min(trimmed.Length, maxChars));
            bool pendingSpace = false;

            foreach (char ch in trimmed)
            {
                if (ch is '\r' or '\n')
                {
                    if (allowLineBreaks && builder.Length > 0 && builder[^1] != '\n')
                    {
                        builder.Append('\n');
                        if (builder.Length >= maxChars)
                        {
                            break;
                        }
                    }

                    pendingSpace = false;
                    continue;
                }

                if (char.IsWhiteSpace(ch) ||
                    char.IsControl(ch) ||
                    CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format)
                {
                    pendingSpace = true;
                    continue;
                }

                if (pendingSpace && builder.Length > 0 && builder[^1] != '\n')
                {
                    if (builder.Length >= maxChars)
                    {
                        break;
                    }

                    builder.Append(' ');
                }

                if (builder.Length >= maxChars)
                {
                    break;
                }

                builder.Append(ch);
                pendingSpace = false;
                if (builder.Length >= maxChars)
                {
                    break;
                }
            }

            return builder.ToString().Trim();
        }

        private static bool CanUseChunkedPayload(string sourcePath, ProcessingRunOptions options)
        {
            if (options.UseSteganography)
            {
                return false;
            }

            return File.Exists(sourcePath);
        }

        internal static void ValidatePngCarrierSourceSize(long sourceBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sourceBytes);
            if (sourceBytes > MaxPngCarrierSourceBytes)
            {
                throw new InvalidOperationException(GetPngCarrierSizeLimitMessage());
            }
        }

        internal static void ValidatePngCarrierQueueSizes(IEnumerable<QueuedFileItem> queueItems, bool useSteganography)
        {
            ArgumentNullException.ThrowIfNull(queueItems);
            if (!useSteganography)
            {
                return;
            }

            QueuedFileItem? oversizedItem = queueItems.FirstOrDefault(item => item.SizeBytes > MaxPngCarrierSourceBytes);
            if (oversizedItem is not null)
            {
                throw new InvalidOperationException($"{Path.GetFileName(oversizedItem.SourcePath)} is too large for PNG carrier mode. {GetPngCarrierSizeLimitMessage()}");
            }
        }

        internal static void ValidatePngCarrierPayloadSize(long payloadBytes)
        {
            if (payloadBytes < 0 || payloadBytes > MaxPngCarrierPayloadBytes)
            {
                throw new InvalidDataException("PNG carrier payload is too large to load safely. Use standard .locked files for large payloads.");
            }
        }

        private static string GetPngCarrierSizeLimitMessage() =>
            $"PNG carrier mode supports files up to {FormatFileSize(MaxPngCarrierSourceBytes)}. Use standard .locked output for larger files.";

        internal static byte[] ComputeSha256ForFile(string filePath, CancellationToken cancellationToken = default)
        {
            string safePath = RequireExistingFile(filePath);
            using FileStream stream = File.OpenRead(safePath);
            return ComputeStreamHash(stream, cancellationToken);
        }

        private static long CalculateSizeHidingPadding(long originalSize)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(originalSize);

            long[] buckets =
            [
                64 * 1024L,
                256 * 1024L,
                1024 * 1024L,
                5 * 1024 * 1024L,
                10 * 1024 * 1024L,
                25 * 1024 * 1024L,
                50 * 1024 * 1024L,
                100 * 1024 * 1024L,
                250 * 1024 * 1024L,
                500 * 1024 * 1024L,
                1024 * 1024 * 1024L
            ];

            foreach (long bucket in buckets)
            {
                if (originalSize <= bucket)
                {
                    return Math.Max(0, bucket - originalSize);
                }
            }

            const long largeBucket = 256L * 1024L * 1024L;
            long remainder = originalSize % largeBucket;
            if (remainder == 0)
            {
                return 0;
            }

            long padding = largeBucket - remainder;
            if (long.MaxValue - originalSize < padding)
            {
                return 0;
            }

            long nextBucket = originalSize + padding;
            return Math.Max(0, nextBucket - originalSize);
        }

        internal static void WriteRandomPadding(Stream stream, long paddingLength, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(paddingLength);
            if (paddingLength == 0)
            {
                return;
            }

            byte[] buffer = new byte[131072];
            try
            {
                long remaining = paddingLength;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int toWrite = (int)Math.Min(buffer.Length, remaining);
                    RandomNumberGenerator.Fill(buffer.AsSpan(0, toWrite));
                    stream.Write(buffer, 0, toWrite);
                    remaining -= toWrite;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }

        internal static void SkipBytes(Stream stream, long byteCount, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
            if (byteCount == 0)
            {
                return;
            }

            byte[] buffer = new byte[131072];
            try
            {
                long remaining = byteCount;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int read = stream.Read(buffer, 0, toRead);
                    if (read == 0)
                    {
                        throw new EndOfStreamException();
                    }

                    remaining -= read;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }

        internal static void DrainBufferedPayloadPadding(Stream stream, long byteCount, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
            if (byteCount == 0)
            {
                return;
            }

            byte[] buffer = new byte[131072];
            try
            {
                long remaining = byteCount;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int read = stream.Read(buffer, 0, toRead);
                    if (read == 0)
                    {
                        return;
                    }

                    remaining -= read;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }

        private static string ComputeSha256Base64ForFile(string filePath, CancellationToken cancellationToken = default)
        {
            return Convert.ToBase64String(ComputeSha256ForFile(filePath, cancellationToken));
        }

        internal static long CopyStreamWithProgress(
            Stream input,
            Stream output,
            CancellationToken cancellationToken,
            long totalLength,
            Action<double, string>? progress,
            double startPercent,
            double endPercent,
            string status)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(totalLength);
            byte[] buffer = new byte[131072];
            try
            {
                long processed = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = input.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        return processed;
                    }

                    output.Write(buffer, 0, read);
                    processed += read;
                    if (totalLength > 0)
                    {
                        double percent = startPercent + ((double)processed / totalLength) * (endPercent - startPercent);
                        progress?.Invoke(percent, status);
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }

        private byte[] ReadAllBytesWithProgress(
            string filePath,
            CancellationToken cancellationToken,
            Action<double, string>? progress,
            double startPercent,
            double endPercent,
            string status)
        {
            string safePath = RequireExistingFile(filePath);
            using FileStream input = File.OpenRead(safePath);
            using var memory = new MemoryStream();
            try
            {
                CopyStreamWithProgress(input, memory, cancellationToken, input.Length, progress, startPercent, endPercent, status);
                return memory.ToArray();
            }
            finally
            {
                ClearMemoryStreamBuffer(memory);
            }
        }

        private static void ClearMemoryStreamBuffer(MemoryStream memory)
        {
            if (memory.TryGetBuffer(out ArraySegment<byte> buffer) && buffer.Array is not null)
            {
                CryptographicOperations.ZeroMemory(buffer.Array.AsSpan(0, buffer.Array.Length));
            }
        }

        private void WriteAllBytesWithProgress(
            string filePath,
            byte[] data,
            CancellationToken cancellationToken,
            Action<double, string>? progress,
            double startPercent,
            double endPercent,
            string status)
        {
            using FileStream output = new(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var input = new MemoryStream(data, writable: false);
            CopyStreamWithProgress(input, output, cancellationToken, data.LongLength, progress, startPercent, endPercent, status);
        }

        internal static bool IsPayloadV3File(string filePath)
        {
            string safePath = RequireExistingFile(filePath);
            using FileStream stream = new(safePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return PayloadChunkedService.LooksLikePayloadV3(stream);
        }

        private static string ResolveEncryptOutputDirectory(
            string sourcePath,
            string? customOutputDirectory,
            string? sourceRootPath = null,
            bool sourceRootIsFolder = false)
        {
            if (!string.IsNullOrWhiteSpace(customOutputDirectory))
            {
                string outputDirectory = customOutputDirectory.Trim();
                if (sourceRootIsFolder && !string.IsNullOrWhiteSpace(sourceRootPath))
                {
                    string normalizedRootPath = Path.GetFullPath(sourceRootPath.Trim());
                    string sourceDirectory = Path.GetDirectoryName(sourcePath) ?? normalizedRootPath;
                    string relativeDirectory = Path.GetRelativePath(normalizedRootPath, sourceDirectory);
                    string safeRelativeDirectory = SanitizeRelativeDirectory(relativeDirectory);
                    if (!string.IsNullOrWhiteSpace(safeRelativeDirectory))
                    {
                        outputDirectory = Path.Combine(outputDirectory, safeRelativeDirectory);
                    }
                }

                Directory.CreateDirectory(outputDirectory);
                return outputDirectory;
            }

            return Path.GetDirectoryName(sourcePath)
                ?? throw new InvalidOperationException("File directory is not available.");
        }

        private static string BuildFolderPackageOutputPath(string folderRootPath, bool scrambleNames, string? customOutputDirectory)
        {
            string parentDirectory = ResolveEncryptOutputDirectory(folderRootPath, customOutputDirectory);
            string baseName = scrambleNames
                ? GenerateRandomString(18)
                : $"{GetFolderDisplayName(folderRootPath)}_package";
            return Path.Combine(parentDirectory, baseName + ENCRYPTED_EXTENSION);
        }

        internal static string GetFolderDisplayName(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return "Folder";
            }

            string trimmed = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Path.GetFileName(trimmed);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            string? root = Path.GetPathRoot(folderPath);
            string rootName = root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? string.Empty;
            return GetRootFolderDisplayName(rootName);
        }

        private static string GetRootFolderDisplayName(string rootName)
        {
            if (string.IsNullOrWhiteSpace(rootName))
            {
                return "Folder";
            }

            string candidate = rootName.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Trim();
            if (candidate.Length == 2 && candidate[1] == ':' && char.IsLetter(candidate[0]))
            {
                return $"{char.ToUpperInvariant(candidate[0])}-drive";
            }

            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                candidate = candidate.Replace(invalid, '-');
            }

            candidate = candidate
                .Replace(Path.DirectorySeparatorChar, '-')
                .Replace(Path.AltDirectorySeparatorChar, '-')
                .Trim('-', ' ');

            return string.IsNullOrWhiteSpace(candidate) ? "Folder" : candidate;
        }

        private static string GetRelativePathSafe(string rootFolderPath, string filePath)
        {
            string relative = Path.GetRelativePath(rootFolderPath, filePath);
            return relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }

        internal static bool IsSafeFolderPackageRelativePath(string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) ||
                Path.IsPathFullyQualified(relativePath) ||
                Path.IsPathRooted(relativePath) ||
                Path.EndsInDirectorySeparator(relativePath.Trim()))
            {
                return false;
            }

            return relativePath
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
                .All(segment => segment is not "." and not ".." && IsSafeRestoredFileName(segment));
        }

        internal static string ResolveFolderPackageEntryPath(string rootPath, string relativePath)
        {
            if (!TryNormalizeBoundaryDirectoryPath(rootPath, out string fullRoot))
            {
                throw new UnauthorizedAccessException("Folder package restore root is invalid.");
            }

            if (!IsSafeFolderPackageRelativePath(relativePath))
            {
                throw new UnauthorizedAccessException("Folder package contains an unsafe restore path.");
            }

            string fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
            if (!IsSameDirectoryOrChild(fullPath, fullRoot))
            {
                throw new UnauthorizedAccessException("Folder package entry resolved outside the restore folder.");
            }

            return fullPath;
        }

        private static bool IsSameDirectoryOrChild(string candidatePath, string rootPath)
        {
            string normalizedRoot = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(candidatePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
            return candidatePath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
        }

        private static void WriteInt64(Stream stream, long value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
            stream.Write(buffer);
        }

        private static long ReadInt64(Stream stream)
        {
            Span<byte> buffer = stackalloc byte[sizeof(long)];
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = stream.Read(buffer[totalRead..]);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                totalRead += read;
            }

            return BinaryPrimitives.ReadInt64LittleEndian(buffer);
        }

        private FileOperationResult EncryptFileAdvanced(string filePath, string password, ProcessingRunOptions options, Action<double, string>? progress = null)
        {
            return EncryptFileAdvancedCore(filePath, password, options, null, false, progress);
        }

        private FileOperationResult EncryptFileAdvancedCore(
            string filePath,
            string password,
            ProcessingRunOptions options,
            string? sourceRootPath = null,
            bool sourceRootIsFolder = false,
            Action<double, string>? progress = null)
        {
            filePath = RequireExistingFile(filePath);
            ValidateSecureDeleteSourceFile(filePath, options.RemoveOriginalsAfterSuccess, options.SecureDeleteOriginals);

            if (CanUseChunkedPayload(filePath, options))
            {
                return EncryptFileAdvancedV3Core(filePath, password, options, sourceRootPath, sourceRootIsFolder, progress);
            }

            var elapsed = Stopwatch.StartNew();
            string? backupPath = null;
            string encryptedPath = string.Empty;
            string tempPath = string.Empty;
            byte[]? salt = null;
            byte[]? iv = null;
            byte[]? key = null;
            byte[]? fileData = null;
            byte[]? dataToEncrypt = null;
            byte[]? padding = null;
            byte[]? metadataBytes = null;
            byte[]? combined = null;
            byte[]? ciphertext = null;
            byte[]? tag = null;
            byte[]? payload = null;
            byte[]? outputBytes = null;

            try
            {
                progress?.Invoke(5, "Reading source");
                if (options.UseSteganography)
                {
                    ValidatePngCarrierSourceSize(new FileInfo(filePath).Length);
                }

                salt = GenerateRandomBytes(SALT_SIZE);
                iv = GenerateRandomBytes(IV_SIZE);
                key = DeriveArgon2idKey(password, salt, options.KeyfileBytes);
                fileData = ReadAllBytesWithProgress(filePath, _processingCancellation?.Token ?? CancellationToken.None, progress, 5, 30, "Reading source");
                string originalFileName = Path.GetFileName(filePath) ?? string.Empty;

                FileMetadata metadata = new()
                {
                    OriginalFileName = originalFileName,
                    OriginalSize = fileData.Length,
                    CreationTime = File.GetCreationTimeUtc(filePath),
                    LastWriteTime = File.GetLastWriteTimeUtc(filePath),
                    LastAccessTime = File.GetLastAccessTimeUtc(filePath),
                    OriginalAttributes = File.GetAttributes(filePath),
                    IsSteganographyContainer = options.UseSteganography
                };

                ApplyMetadataOverrides(metadata, filePath, options);

                dataToEncrypt = fileData;
                bool compressionApplied = false;
                long? compressedSizeBytes = null;
                string compressionReason = options.CompressFiles
                    ? CompressionAdvisor.HasKnownIncompressibleExtension(filePath)
                        ? "File type is already compressed."
                        : "Compression requested."
                    : "Compression disabled.";
                if (options.CompressFiles && !CompressionAdvisor.HasKnownIncompressibleExtension(filePath))
                {
                    progress?.Invoke(40, "Compressing");
                    dataToEncrypt = CompressData(fileData, out bool compressed);
                    metadata.IsCompressed = compressed;
                    compressionApplied = compressed;
                    compressedSizeBytes = dataToEncrypt.LongLength;
                    compressionReason = compressed
                        ? "Compression reduced the payload before encryption."
                        : "Compression did not save enough space.";
                    if (!compressed)
                    {
                        dataToEncrypt = fileData;
                    }
                }

                metadata.ContentHash = ComputeSha256(fileData);

                padding = GenerateRandomBytes((int)Math.Min(int.MaxValue, CalculateSizeHidingPadding(fileData.LongLength)));
                metadataBytes = SerializeMetadata(metadata);
                combined = new byte[4 + metadataBytes.Length + 4 + padding.Length + dataToEncrypt.Length];
                int offset = 0;
                BinaryPrimitives.WriteInt32LittleEndian(combined.AsSpan(offset, sizeof(int)), metadataBytes.Length);
                offset += 4;
                Buffer.BlockCopy(metadataBytes, 0, combined, offset, metadataBytes.Length);
                offset += metadataBytes.Length;
                BinaryPrimitives.WriteInt32LittleEndian(combined.AsSpan(offset, sizeof(int)), padding.Length);
                offset += 4;
                Buffer.BlockCopy(padding, 0, combined, offset, padding.Length);
                offset += padding.Length;
                Buffer.BlockCopy(dataToEncrypt, 0, combined, offset, dataToEncrypt.Length);

                ciphertext = new byte[combined.Length];
                tag = new byte[TAG_SIZE];
                progress?.Invoke(60, "Encrypting");
                using (var aes = new AesGcm(key, TAG_SIZE))
                {
                    aes.Encrypt(iv, combined, ciphertext, tag);
                }

                payload = BuildEncryptedPayload(salt, iv, tag, ciphertext);
                encryptedPath = ResolveAvailablePath(BuildOutputPath(
                    filePath,
                    options.ScrambleNames,
                    options.UseSteganography,
                    options.UseCustomEncryptOutputDirectory ? options.EncryptOutputDirectory : null,
                    sourceRootPath,
                    sourceRootIsFolder));
                tempPath = ResolveTemporaryOutputPath(encryptedPath);
                outputBytes = options.UseSteganography ? EmbedInPngContainer(payload) : payload;

                if (!string.IsNullOrWhiteSpace(options.BackupFolderPath))
                {
                    backupPath = CreateBackupCopy(filePath, options.BackupFolderPath);
                }

                progress?.Invoke(80, "Writing output");
                WriteAllBytesWithProgress(tempPath, outputBytes, _processingCancellation?.Token ?? CancellationToken.None, progress, 80, 95, "Writing output");
                if (options.VerifyAfterWrite)
                {
                    progress?.Invoke(97, "Verifying");
                    VerifyWrittenFile(tempPath, outputBytes);
                }

                PromoteTemporaryOutput(tempPath, encryptedPath);
                ApplyOutputTimestampPolicy(encryptedPath, filePath, options.OutputTimestampPolicy);
                long outputSizeBytes = new FileInfo(encryptedPath).Length;

                bool retained = true;
                if (options.RemoveOriginalsAfterSuccess)
                {
                    DeleteSourceFile(filePath, options.SecureDeleteOriginals);
                    retained = false;
                }

                progress?.Invoke(100, "Completed");

                return new FileOperationResult
                {
                    SourcePath = filePath,
                    OutputPath = encryptedPath,
                    BackupPath = backupPath,
                    Status = "Completed",
                    OriginalRetained = retained,
                    OutputVerified = options.VerifyAfterWrite,
                    Message = options.RemoveOriginalsAfterSuccess
                        ? options.SecureDeleteOriginals ? "Encrypted and securely removed original." : "Encrypted and removed original."
                        : "Encrypted and retained original.",
                    OriginalSizeBytes = fileData.LongLength,
                    OutputSizeBytes = outputSizeBytes,
                    CompressionRequested = options.CompressFiles,
                    CompressionApplied = compressionApplied,
                    CompressionReason = compressionReason,
                    EstimatedCompressedSizeBytes = compressedSizeBytes,
                    CompressedSizeBytes = compressionApplied ? compressedSizeBytes : null,
                    ElapsedMilliseconds = elapsed.ElapsedMilliseconds,
                    Algorithm = options.Algorithm,
                    KeySizeBits = options.KeySizeBits
                };
            }
            catch (OperationCanceledException)
            {
                CleanupTemporaryFile(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                CleanupTemporaryFile(tempPath);
                throw new InvalidOperationException($"Encryption failed: {GetFriendlyExceptionMessage(ex, "Unknown error while encrypting.")}", ex);
            }
            finally
            {
                ClearSensitiveBuffer(salt);
                ClearSensitiveBuffer(iv);
                ClearSensitiveBuffer(key);
                if (dataToEncrypt is not null && !ReferenceEquals(dataToEncrypt, fileData))
                {
                    ClearSensitiveBuffer(dataToEncrypt);
                }

                ClearSensitiveBuffer(fileData);
                ClearSensitiveBuffer(padding);
                ClearSensitiveBuffer(metadataBytes);
                ClearSensitiveBuffer(combined);
                ClearSensitiveBuffer(ciphertext);
                ClearSensitiveBuffer(tag);
                ClearSensitiveBuffer(payload);
                if (outputBytes is not null && !ReferenceEquals(outputBytes, payload))
                {
                    ClearSensitiveBuffer(outputBytes);
                }
            }
        }

        private FileOperationResult DecryptFileAdvanced(
            string filePath,
            string password,
            ProcessingRunOptions options,
            Action<double, string>? progress = null,
            string? relativeOutputDirectory = null)
        {
            filePath = RequireExistingFile(filePath);
            ValidateSecureDeleteSourceFile(filePath, options.RemoveOriginalsAfterSuccess, options.SecureDeleteOriginals);

            var elapsed = Stopwatch.StartNew();
            if (IsPayloadV3File(filePath))
            {
                return DecryptFileAdvancedV3(filePath, password, options, progress, relativeOutputDirectory);
            }

            string? backupPath = null;
            string finalPath = string.Empty;
            string tempPath = string.Empty;
            byte[]? fileData = null;
            try
            {
                long encryptedInputSize = new FileInfo(filePath).Length;
                progress?.Invoke(5, "Reading payload");
                (FileMetadata metadata, byte[] unlockedFileData) = UnlockFilePayload(filePath, password, options, progress);
                fileData = unlockedFileData;

                string directory = ResolveDecryptOutputDirectory(filePath, options, relativeOutputDirectory);
                string outputFileName = ResolveDecryptedFileName(filePath, metadata.OriginalFileName, options.RestoreOriginalFilenames);
                string originalPath = Path.Combine(directory, outputFileName);
                finalPath = ResolveAvailablePath(originalPath);
                tempPath = ResolveTemporaryOutputPath(finalPath);

                if (!string.IsNullOrWhiteSpace(options.BackupFolderPath))
                {
                    backupPath = CreateBackupCopy(filePath, options.BackupFolderPath);
                }

                progress?.Invoke(80, "Writing output");
                WriteAllBytesWithProgress(tempPath, fileData, _processingCancellation?.Token ?? CancellationToken.None, progress, 80, 95, "Writing output");
                if (options.VerifyAfterWrite)
                {
                    progress?.Invoke(97, "Verifying");
                    VerifyWrittenFile(tempPath, fileData);
                }

                PromoteTemporaryOutput(tempPath, finalPath);
                RestoreFileMetadata(finalPath, metadata);
                long outputSizeBytes = new FileInfo(finalPath).Length;

                bool retained = true;
                if (options.RemoveOriginalsAfterSuccess)
                {
                    DeleteSourceFile(filePath, options.SecureDeleteOriginals);
                    retained = false;
                }

                progress?.Invoke(100, "Completed");

                return new FileOperationResult
                {
                    SourcePath = filePath,
                    OutputPath = finalPath,
                    BackupPath = backupPath,
                    Status = "Completed",
                    OriginalRetained = retained,
                    OutputVerified = options.VerifyAfterWrite,
                    Message = options.RemoveOriginalsAfterSuccess
                        ? options.SecureDeleteOriginals ? "Decrypted and securely removed source payload." : "Decrypted and removed source payload."
                        : "Decrypted and retained source payload.",
                    OriginalSizeBytes = encryptedInputSize,
                    OutputSizeBytes = outputSizeBytes,
                    CompressionApplied = metadata.IsCompressed,
                    CompressionReason = metadata.IsCompressed
                        ? "Source payload was compressed before encryption."
                        : "Source payload was not compressed.",
                    ElapsedMilliseconds = elapsed.ElapsedMilliseconds,
                    Algorithm = metadata.Algorithm,
                    KeySizeBits = metadata.KeySizeBits
                };
            }
            catch (OperationCanceledException)
            {
                CleanupTemporaryFile(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                CleanupTemporaryFile(tempPath);
                throw new InvalidOperationException($"Decryption failed: {GetFriendlyExceptionMessage(ex, "Unknown error while decrypting.")}", ex);
            }
            finally
            {
                ClearSensitiveBuffer(fileData);
            }
        }

        private FileOperationResult VerifyLockedFile(string filePath, string password, ProcessingRunOptions options, Action<double, string>? progress = null)
        {
            filePath = RequireExistingFile(filePath);
            if (IsPayloadV3File(filePath))
            {
                return VerifyLockedFileV3(filePath, password, options, progress);
            }

            progress?.Invoke(10, "Reading payload");
            byte[]? fileData = null;
            try
            {
                (FileMetadata metadata, byte[] unlockedFileData) = UnlockFilePayload(filePath, password, options, progress);
                fileData = unlockedFileData;
                string displayFileName = ResolveDecryptedFileName(filePath, metadata.OriginalFileName, restoreOriginalFilename: true);
                progress?.Invoke(100, "Verified");
                return new FileOperationResult
                {
                    SourcePath = filePath,
                    OutputPath = null,
                    BackupPath = null,
                    Status = "Completed",
                    OriginalRetained = true,
                    OutputVerified = true,
                    Message = $"Verified {displayFileName} ({FormatFileSize(fileData.LongLength)}) without writing output.",
                    Algorithm = metadata.Algorithm,
                    KeySizeBits = metadata.KeySizeBits
                };
            }
            finally
            {
                ClearSensitiveBuffer(fileData);
            }
        }

        private FileOperationResult EncryptFileAdvancedV3(string filePath, string password, ProcessingRunOptions options, Action<double, string>? progress = null)
        {
            ValidateSecureDeleteSourceFile(filePath, options.RemoveOriginalsAfterSuccess, options.SecureDeleteOriginals);
            return EncryptFileAdvancedV3Core(filePath, password, options, null, false, progress);
        }

        private FileOperationResult EncryptFileAdvancedV3Core(
            string filePath,
            string password,
            ProcessingRunOptions options,
            string? sourceRootPath = null,
            bool sourceRootIsFolder = false,
            Action<double, string>? progress = null)
        {
            var elapsed = Stopwatch.StartNew();
            string? backupPath = null;
            string encryptedPath = string.Empty;
            string tempPath = string.Empty;
            byte[]? metadataBytes = null;

            try
            {
                string normalizedAlgorithm = EncryptionAlgorithmCatalog.NormalizeForNewPayload(options.Algorithm);
                string metadataAlgorithm = EncryptionAlgorithmCatalog.GetFileFormatName(normalizedAlgorithm);
                int normalizedKeySizeBits = EncryptionAlgorithmCatalog.GetKeySizeBits(normalizedAlgorithm);
                var fileInfo = new FileInfo(filePath);
                byte[] contentHash = ComputeSha256ForFile(filePath, _processingCancellation?.Token ?? CancellationToken.None);
                CompressionPlan compressionPlan = CompressionAdvisor.CreatePlan(filePath, fileInfo.Length, options.CompressFiles);
                long originalSizeBytes = fileInfo.Length;
                var metadata = new FilePayloadMetadata
                {
                    OriginalFileName = Path.GetFileName(filePath) ?? string.Empty,
                    OriginalSize = originalSizeBytes,
                    CreationTimeUtc = File.GetCreationTimeUtc(filePath),
                    LastWriteTimeUtc = File.GetLastWriteTimeUtc(filePath),
                    LastAccessTimeUtc = File.GetLastAccessTimeUtc(filePath),
                    OriginalAttributes = (int)File.GetAttributes(filePath),
                    IsCompressed = compressionPlan.ShouldCompress,
                    IsSteganographyContainer = false,
                    ContentHashBase64 = Convert.ToBase64String(contentHash),
                    Algorithm = metadataAlgorithm,
                    Mode = options.Mode,
                    KeySizeBits = normalizedKeySizeBits,
                    CustomNote = options.Metadata.Notes,
                    MetadataLabel = string.IsNullOrWhiteSpace(options.Metadata.Label)
                        ? Path.GetFileName(filePath) ?? string.Empty
                        : options.Metadata.Label,
                    ContentPaddingLength = CalculateSizeHidingPadding(originalSizeBytes)
                };

                if (options.Metadata.Randomize)
                {
                    (DateTime created, DateTime modified) = GenerateRandomizedDates();
                    metadata.CreationTimeUtc = created.ToUniversalTime();
                    metadata.LastWriteTimeUtc = modified.ToUniversalTime();
                }
                else
                {
                    metadata.CreationTimeUtc = ParseDateOrDefault(options.Metadata.CreatedText, File.GetCreationTimeUtc(filePath)).ToUniversalTime();
                    metadata.LastWriteTimeUtc = ParseDateOrDefault(options.Metadata.ModifiedText, File.GetLastWriteTimeUtc(filePath)).ToUniversalTime();
                }

                metadataBytes = JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions);
                encryptedPath = ResolveAvailablePath(BuildOutputPath(
                    filePath,
                    options.ScrambleNames,
                    false,
                    options.UseCustomEncryptOutputDirectory ? options.EncryptOutputDirectory : null,
                    sourceRootPath,
                    sourceRootIsFolder));
                tempPath = ResolveTemporaryOutputPath(encryptedPath);
                long? compressedSizeBytes = null;

                if (!string.IsNullOrWhiteSpace(options.BackupFolderPath))
                {
                    backupPath = CreateBackupCopy(filePath, options.BackupFolderPath);
                }

                using (FileStream output = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    progress?.Invoke(5, compressionPlan.ShouldCompress ? "Preparing compression" : "Preparing");
                    PayloadChunkedService.WritePayload(
                        output,
                        metadataBytes,
                        (payloadStream, cancellationToken) =>
                        {
                            using FileStream input = File.OpenRead(filePath);
                            if (compressionPlan.ShouldCompress)
                            {
                                using var countingStream = new CountingWriteStream(payloadStream);
                                using (var gzip = new GZipStream(countingStream, CompressionLevel.SmallestSize, leaveOpen: true))
                                {
                                    CopyStreamWithProgress(input, gzip, cancellationToken, input.Length, progress, 10, 80, "Compressing");
                                }

                                compressedSizeBytes = countingStream.BytesWritten;
                            }
                            else
                            {
                                CopyStreamWithProgress(input, payloadStream, cancellationToken, input.Length, progress, 10, 80, "Encrypting");
                            }

                            progress?.Invoke(85, "Applying padding");
                            WriteRandomPadding(payloadStream, metadata.ContentPaddingLength, cancellationToken);
                        },
                        new PayloadWriteInputs(
                            password,
                            options.KeyfileBytes,
                            options.RecoveryKey,
                            131072,
                            0b0000_0001,
                            EncryptionAlgorithmCatalog.GetNewPayloadAlgorithmId(normalizedAlgorithm)),
                        _processingCancellation?.Token ?? CancellationToken.None);
                }

                if (options.VerifyAfterWrite)
                {
                    progress?.Invoke(95, "Verifying");
                    VerifyLockedFileV3(tempPath, password, options, progress);
                }

                PromoteTemporaryOutput(tempPath, encryptedPath);
                ApplyOutputTimestampPolicy(encryptedPath, filePath, options.OutputTimestampPolicy);
                long outputSizeBytes = new FileInfo(encryptedPath).Length;

                bool retained = true;
                if (options.RemoveOriginalsAfterSuccess)
                {
                    DeleteSourceFile(filePath, options.SecureDeleteOriginals);
                    retained = false;
                }

                progress?.Invoke(100, "Completed");

                return new FileOperationResult
                {
                    SourcePath = filePath,
                    OutputPath = encryptedPath,
                    BackupPath = backupPath,
                    Status = "Completed",
                    OriginalRetained = retained,
                    OutputVerified = options.VerifyAfterWrite,
                    Message = options.RemoveOriginalsAfterSuccess
                        ? options.SecureDeleteOriginals ? "Encrypted with streaming payload and removed original using best-effort overwrite." : "Encrypted with streaming payload and removed original."
                        : "Encrypted with streaming payload and retained original.",
                    OriginalSizeBytes = originalSizeBytes,
                    OutputSizeBytes = outputSizeBytes,
                    CompressionRequested = options.CompressFiles,
                    CompressionApplied = compressionPlan.ShouldCompress,
                    CompressionReason = compressionPlan.Reason,
                    EstimatedCompressedSizeBytes = compressionPlan.EstimatedCompressedSize,
                    CompressedSizeBytes = compressedSizeBytes,
                    ElapsedMilliseconds = elapsed.ElapsedMilliseconds,
                    Algorithm = normalizedAlgorithm,
                    KeySizeBits = normalizedKeySizeBits
                };
            }
            catch (OperationCanceledException)
            {
                CleanupTemporaryFile(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                CleanupTemporaryFile(tempPath);
                throw new InvalidOperationException($"Encryption failed: {GetFriendlyExceptionMessage(ex, "Unknown error while encrypting.")}", ex);
            }
            finally
            {
                ClearSensitiveBuffer(metadataBytes);
            }
        }

        private FileOperationResult DecryptFileAdvancedV3(
            string filePath,
            string password,
            ProcessingRunOptions options,
            Action<double, string>? progress = null,
            string? relativeOutputDirectory = null)
        {
            filePath = RequireExistingFile(filePath);

            var elapsed = Stopwatch.StartNew();
            string? backupPath = null;
            string finalPath = string.Empty;
            string tempPath = string.Empty;

            try
            {
                long encryptedInputSize = new FileInfo(filePath).Length;
                FileOperationResult result;

                using (FileStream input = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (OpenPayloadResult payload = PayloadChunkedService.OpenPayload(
                    input,
                    new PayloadUnlockInputs(password, options.KeyfileBytes, options.RecoveryKey),
                    _processingCancellation?.Token ?? CancellationToken.None))
                {
                    progress?.Invoke(10, "Reading payload");
                    string payloadAlgorithm = EncryptionAlgorithmCatalog.GetDisplayName(payload.Header.AlgorithmId);

                    if (IsFolderPackagePayloadMetadata(payload.MetadataBytes))
                    {
                        FolderPackageMetadata packageMetadata = JsonSerializer.Deserialize<FolderPackageMetadata>(payload.MetadataBytes, JsonOptions)
                            ?? throw new InvalidDataException("Folder package metadata is invalid.");
                        ValidateFolderPackageMetadata(packageMetadata);
                        ValidatePayloadMetadataAlgorithm(
                            packageMetadata.Algorithm,
                            packageMetadata.KeySizeBits,
                            payload.Header.AlgorithmId,
                            payload.Header.Version,
                            "Folder package metadata");
                        result = DecryptFolderPackagePayloadV3(
                            filePath,
                            payload,
                            packageMetadata,
                            options with { RemoveOriginalsAfterSuccess = false },
                            progress,
                            relativeOutputDirectory);
                    }
                    else
                    {
                        FilePayloadMetadata metadata = JsonSerializer.Deserialize<FilePayloadMetadata>(payload.MetadataBytes, JsonOptions)
                            ?? throw new InvalidDataException("File payload metadata is invalid.");
                        ValidateFilePayloadMetadata(metadata);
                        ValidatePayloadMetadataAlgorithm(
                            metadata.Algorithm,
                            metadata.KeySizeBits,
                            payload.Header.AlgorithmId,
                            payload.Header.Version,
                            "File payload metadata");

                        string directory = ResolveDecryptOutputDirectory(filePath, options, relativeOutputDirectory);
                        string outputFileName = ResolveDecryptedFileName(filePath, metadata.OriginalFileName, options.RestoreOriginalFilenames);
                        finalPath = ResolveAvailablePath(Path.Combine(directory, outputFileName));
                        tempPath = ResolveTemporaryOutputPath(finalPath);

                        if (!string.IsNullOrWhiteSpace(options.BackupFolderPath))
                        {
                            backupPath = CreateBackupCopy(filePath, options.BackupFolderPath);
                        }

                        using (FileStream output = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            if (metadata.IsCompressed)
                            {
                                long restoredBytes;
                                using (var gzip = new GZipStream(payload.PlaintextStream, CompressionMode.Decompress, leaveOpen: true))
                                {
                                    restoredBytes = CopyStreamWithProgress(gzip, output, _processingCancellation?.Token ?? CancellationToken.None, metadata.OriginalSize, progress, 15, 90, "Decrypting");
                                }

                                if (restoredBytes != metadata.OriginalSize)
                                {
                                    throw new InvalidDataException("File payload decompressed length does not match metadata.");
                                }

                                DrainBufferedPayloadPadding(payload.PlaintextStream, metadata.ContentPaddingLength, _processingCancellation?.Token ?? CancellationToken.None);
                            }
                            else
                            {
                                CopyFixedLengthStream(payload.PlaintextStream, output, metadata.OriginalSize, _processingCancellation?.Token ?? CancellationToken.None, progress, 15, 90, "Decrypting");
                                SkipBytes(payload.PlaintextStream, metadata.ContentPaddingLength, _processingCancellation?.Token ?? CancellationToken.None);
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(metadata.ContentHashBase64))
                        {
                            byte[] expectedHash = Convert.FromBase64String(metadata.ContentHashBase64);
                            byte[] actualHash = ComputeSha256ForFile(tempPath, _processingCancellation?.Token ?? CancellationToken.None);
                            if (!expectedHash.AsSpan().SequenceEqual(actualHash))
                            {
                                throw new UnauthorizedAccessException("The restored file failed integrity validation.");
                            }
                        }

                        RestoreFileMetadata(
                            tempPath,
                            new FileMetadata
                            {
                                OriginalFileName = metadata.OriginalFileName,
                                OriginalSize = metadata.OriginalSize,
                                CreationTime = metadata.CreationTimeUtc,
                                LastWriteTime = metadata.LastWriteTimeUtc,
                                LastAccessTime = metadata.LastAccessTimeUtc,
                                OriginalAttributes = (System.IO.FileAttributes)metadata.OriginalAttributes
                            });
                        PromoteTemporaryOutput(tempPath, finalPath);
                        long outputSizeBytes = new FileInfo(finalPath).Length;

                        result = new FileOperationResult
                        {
                            SourcePath = filePath,
                            OutputPath = finalPath,
                            BackupPath = backupPath,
                            Status = "Completed",
                            OriginalRetained = true,
                            OutputVerified = options.VerifyAfterWrite,
                            Message = $"Decrypted {payloadAlgorithm} v{payload.Header.Version} payload. Source payload retained.",
                            OriginalSizeBytes = encryptedInputSize,
                            OutputSizeBytes = outputSizeBytes,
                            CompressionApplied = metadata.IsCompressed,
                            CompressionReason = metadata.IsCompressed
                                ? "Source payload was compressed before encryption."
                                : "Source payload was not compressed.",
                            ElapsedMilliseconds = elapsed.ElapsedMilliseconds,
                            Algorithm = payloadAlgorithm,
                            KeySizeBits = metadata.KeySizeBits > 0
                                ? metadata.KeySizeBits
                                : EncryptionAlgorithmCatalog.GetKeySizeBits(payloadAlgorithm)
                        };
                    }
                }

                if (options.RemoveOriginalsAfterSuccess)
                {
                    DeleteSourceFile(filePath, options.SecureDeleteOriginals);
                    result.OriginalRetained = false;
                    result.Message = MarkSourcePayloadRemoved(result.Message);
                }

                progress?.Invoke(100, "Completed");
                return result;
            }
            catch (OperationCanceledException)
            {
                CleanupTemporaryFile(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                CleanupTemporaryFile(tempPath);
                throw new InvalidOperationException($"Decryption failed: {GetFriendlyExceptionMessage(ex, "Unknown error while decrypting.")}", ex);
            }
        }

        private FileOperationResult VerifyLockedFileV3(string filePath, string password, ProcessingRunOptions options, Action<double, string>? progress = null)
        {
            filePath = RequireExistingFile(filePath);
            using FileStream input = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using OpenPayloadResult payload = PayloadChunkedService.OpenPayload(
                input,
                new PayloadUnlockInputs(password, options.KeyfileBytes, options.RecoveryKey),
                _processingCancellation?.Token ?? CancellationToken.None);
            progress?.Invoke(10, "Inspecting");
            string payloadAlgorithm = EncryptionAlgorithmCatalog.GetDisplayName(payload.Header.AlgorithmId);

            if (IsFolderPackagePayloadMetadata(payload.MetadataBytes))
            {
                FolderPackageMetadata packageMetadata = JsonSerializer.Deserialize<FolderPackageMetadata>(payload.MetadataBytes, JsonOptions)
                    ?? throw new InvalidDataException("Folder package metadata is invalid.");
                ValidateFolderPackageMetadata(packageMetadata);
                ValidatePayloadMetadataAlgorithm(
                    packageMetadata.Algorithm,
                    packageMetadata.KeySizeBits,
                    payload.Header.AlgorithmId,
                    payload.Header.Version,
                    "Folder package metadata");
                string displayRootName = ResolveFolderPackageRootName(filePath, packageMetadata.RootFolderName, restoreOriginalFilename: true);
                VerifyFolderPackagePayloadV3(payload, packageMetadata, _processingCancellation?.Token ?? CancellationToken.None, progress);
                progress?.Invoke(100, "Verified");
                return new FileOperationResult
                {
                    SourcePath = filePath,
                    Status = "Completed",
                    OriginalRetained = true,
                    OutputVerified = true,
                    Message = $"Verified {payloadAlgorithm} folder package {displayRootName} with {packageMetadata.Entries.Count} entries.",
                    Algorithm = payloadAlgorithm,
                    KeySizeBits = packageMetadata.KeySizeBits > 0
                        ? packageMetadata.KeySizeBits
                        : EncryptionAlgorithmCatalog.GetKeySizeBits(payloadAlgorithm)
                };
            }

            FilePayloadMetadata metadata = JsonSerializer.Deserialize<FilePayloadMetadata>(payload.MetadataBytes, JsonOptions)
                ?? throw new InvalidDataException("File payload metadata is invalid.");
            ValidateFilePayloadMetadata(metadata);
            ValidatePayloadMetadataAlgorithm(
                metadata.Algorithm,
                metadata.KeySizeBits,
                payload.Header.AlgorithmId,
                payload.Header.Version,
                "File payload metadata");

            byte[] verifiedHash;
            if (metadata.IsCompressed)
            {
                using (var gzip = new GZipStream(payload.PlaintextStream, CompressionMode.Decompress, leaveOpen: true))
                {
                    verifiedHash = ComputeStreamHash(gzip, _processingCancellation?.Token ?? CancellationToken.None, progress, 15, 95, "Verifying");
                }

                DrainBufferedPayloadPadding(payload.PlaintextStream, metadata.ContentPaddingLength, _processingCancellation?.Token ?? CancellationToken.None);
            }
            else
            {
                verifiedHash = ComputeFixedLengthStreamHash(payload.PlaintextStream, metadata.OriginalSize, _processingCancellation?.Token ?? CancellationToken.None, progress, 15, 95, "Verifying");
                SkipBytes(payload.PlaintextStream, metadata.ContentPaddingLength, _processingCancellation?.Token ?? CancellationToken.None);
            }

            if (!string.IsNullOrWhiteSpace(metadata.ContentHashBase64))
            {
                if (!verifiedHash.AsSpan().SequenceEqual(Convert.FromBase64String(metadata.ContentHashBase64)))
                {
                    throw new UnauthorizedAccessException("File failed integrity validation after decryption.");
                }
            }

            progress?.Invoke(100, "Verified");
            string displayFileName = ResolveDecryptedFileName(filePath, metadata.OriginalFileName, restoreOriginalFilename: true);
            return new FileOperationResult
            {
                SourcePath = filePath,
                Status = "Completed",
                OriginalRetained = true,
                OutputVerified = true,
                Message = $"Verified {payloadAlgorithm} payload for {displayFileName} ({FormatFileSize(metadata.OriginalSize)}) without writing output.",
                Algorithm = payloadAlgorithm,
                KeySizeBits = metadata.KeySizeBits > 0
                    ? metadata.KeySizeBits
                    : EncryptionAlgorithmCatalog.GetKeySizeBits(payloadAlgorithm)
            };
        }

        private static string MarkSourcePayloadRemoved(string? message)
        {
            const string retained = "Source payload retained.";
            const string removed = "Source payload removed.";
            if (string.IsNullOrWhiteSpace(message))
            {
                return removed;
            }

            return message.Contains(retained, StringComparison.Ordinal)
                ? message.Replace(retained, removed, StringComparison.Ordinal)
                : $"{message} {removed}";
        }

        internal static string ReadPayloadMetadataKind(byte[] metadataBytes)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(metadataBytes);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("Payload metadata is invalid.");
                }

                EnsureNoDuplicateJsonPropertiesIgnoreCase(document.RootElement);

                if (!TryGetJsonPropertyIgnoreCase(document.RootElement, nameof(FilePayloadMetadata.Kind), out JsonElement kindElement) ||
                    kindElement.ValueKind == JsonValueKind.Null)
                {
                    return PayloadKinds.File;
                }

                if (kindElement.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("Payload metadata kind is invalid.");
                }

                string? kind = kindElement.GetString();
                if (string.IsNullOrWhiteSpace(kind))
                {
                    return PayloadKinds.File;
                }

                string normalizedKind = kind.Trim();
                if (string.Equals(normalizedKind, PayloadKinds.File, StringComparison.Ordinal) ||
                    string.Equals(normalizedKind, PayloadKinds.FolderPackage, StringComparison.Ordinal))
                {
                    return normalizedKind;
                }

                throw new InvalidDataException("Unsupported payload metadata kind.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Payload metadata is invalid.", ex);
            }
        }

        private static void EnsureNoDuplicateJsonPropertiesIgnoreCase(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var propertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!propertyNames.Add(property.Name))
                    {
                        throw new InvalidDataException($"Payload metadata contains duplicate {property.Name} fields.");
                    }

                    EnsureNoDuplicateJsonPropertiesIgnoreCase(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    EnsureNoDuplicateJsonPropertiesIgnoreCase(item);
                }
            }
        }

        private static bool IsFolderPackagePayloadMetadata(byte[] metadataBytes) =>
            string.Equals(ReadPayloadMetadataKind(metadataBytes), PayloadKinds.FolderPackage, StringComparison.Ordinal);

        private static bool TryGetJsonPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
        {
            bool found = false;
            value = default;
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    if (found)
                    {
                        throw new InvalidDataException("Payload metadata contains duplicate kind fields.");
                    }

                    value = property.Value;
                    found = true;
                }
            }

            return found;
        }

        internal static void ValidateFilePayloadMetadata(FilePayloadMetadata metadata)
        {
            ArgumentNullException.ThrowIfNull(metadata);

            if (metadata.OriginalSize < 0)
            {
                throw new InvalidDataException("File payload metadata contains an invalid original size.");
            }

            if (metadata.ContentPaddingLength < 0)
            {
                throw new InvalidDataException("File payload metadata contains an invalid padding length.");
            }

            ValidateRequiredPayloadTimestamp(metadata.CreationTimeUtc, "File payload metadata", "creation");
            ValidateRequiredPayloadTimestamp(metadata.LastWriteTimeUtc, "File payload metadata", "last write");
            ValidateOptionalPayloadTimestamp(metadata.LastAccessTimeUtc, "File payload metadata", "last access");

            if (!string.IsNullOrWhiteSpace(metadata.ContentHashBase64))
            {
                byte[] hash;
                try
                {
                    hash = Convert.FromBase64String(metadata.ContentHashBase64);
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException("File payload metadata contains an invalid content hash.", ex);
                }

                if (hash.Length != SHA256.HashSizeInBytes)
                {
                    throw new InvalidDataException("File payload metadata contains an invalid content hash length.");
                }
            }
        }

        internal static void ValidateFolderPackageMetadata(FolderPackageMetadata metadata)
        {
            ArgumentNullException.ThrowIfNull(metadata);

            if (metadata.PackagePaddingLength < 0)
            {
                throw new InvalidDataException("Folder package metadata contains an invalid padding length.");
            }

            if (metadata.Entries is null)
            {
                throw new InvalidDataException("Folder package metadata is missing the entry list.");
            }

            var restorePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalEntrySize = 0;

            foreach (FolderPackageEntryMetadata entry in metadata.Entries)
            {
                if (entry is null)
                {
                    throw new InvalidDataException("Folder package metadata contains an invalid entry.");
                }

                if (!IsSafeFolderPackageRelativePath(entry.RelativePath))
                {
                    throw new InvalidDataException("Folder package metadata contains an unsafe restore path.");
                }

                string normalizedRelativePath = NormalizeFolderPackageRelativePathForComparison(entry.RelativePath);
                if (!restorePaths.Add(normalizedRelativePath))
                {
                    throw new InvalidDataException("Folder package metadata contains duplicate restore paths.");
                }

                if (entry.OriginalSize < 0)
                {
                    throw new InvalidDataException("Folder package metadata contains an invalid entry size.");
                }

                try
                {
                    checked
                    {
                        totalEntrySize += entry.OriginalSize;
                    }
                }
                catch (OverflowException ex)
                {
                    throw new InvalidDataException("Folder package metadata contains an invalid total entry size.", ex);
                }

                ValidateRequiredPayloadTimestamp(entry.CreationTimeUtc, "Folder package metadata", "entry creation");
                ValidateRequiredPayloadTimestamp(entry.LastWriteTimeUtc, "Folder package metadata", "entry last write");
                ValidateOptionalPayloadTimestamp(entry.LastAccessTimeUtc, "Folder package metadata", "entry last access");

                if (string.IsNullOrWhiteSpace(entry.ContentHashBase64))
                {
                    throw new InvalidDataException("Folder package metadata is missing an entry hash.");
                }

                byte[] hash;
                try
                {
                    hash = Convert.FromBase64String(entry.ContentHashBase64);
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException("Folder package metadata contains an invalid entry hash.", ex);
                }

                if (hash.Length != SHA256.HashSizeInBytes)
                {
                    throw new InvalidDataException("Folder package metadata contains an invalid entry hash length.");
                }
            }
        }

        internal static void ValidatePayloadMetadataAlgorithm(
            string? metadataAlgorithm,
            int keySizeBits,
            byte headerAlgorithmId,
            byte headerVersion,
            string metadataDescription)
        {
            if (!EncryptionAlgorithmCatalog.IsSupportedPayloadAlgorithm(headerAlgorithmId))
            {
                throw new InvalidDataException($"{metadataDescription} references an unsupported payload header algorithm.");
            }

            int expectedKeySizeBits = EncryptionAlgorithmCatalog.GetKeySizeBits(headerAlgorithmId);
            bool allowsLegacyAesMetadataDefaults =
                headerVersion == PayloadChunkedService.LegacyVersion &&
                headerAlgorithmId == EncryptionAlgorithmCatalog.PayloadAlgorithmAes256Gcm;

            if (string.IsNullOrWhiteSpace(metadataAlgorithm))
            {
                if (!allowsLegacyAesMetadataDefaults)
                {
                    throw new InvalidDataException($"{metadataDescription} is missing the algorithm name required for this payload header.");
                }
            }
            else
            {
                if (!EncryptionAlgorithmCatalog.TryNormalize(metadataAlgorithm, out string normalizedAlgorithm))
                {
                    throw new InvalidDataException($"{metadataDescription} contains an unsupported algorithm name.");
                }

                byte metadataAlgorithmId = EncryptionAlgorithmCatalog.GetPayloadAlgorithmId(normalizedAlgorithm);
                if (metadataAlgorithmId != headerAlgorithmId)
                {
                    throw new InvalidDataException($"{metadataDescription} algorithm does not match the payload header.");
                }
            }

            if (keySizeBits != 0 && keySizeBits != expectedKeySizeBits)
            {
                throw new InvalidDataException($"{metadataDescription} contains an invalid key size.");
            }

            if (keySizeBits == 0 && !allowsLegacyAesMetadataDefaults)
            {
                throw new InvalidDataException($"{metadataDescription} is missing the key size required for this payload header.");
            }
        }

        private static string NormalizeFolderPackageRelativePathForComparison(string relativePath)
        {
            string[] segments = relativePath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            return string.Join(Path.DirectorySeparatorChar, segments);
        }

        private static void ValidateRequiredPayloadTimestamp(DateTime timestamp, string metadataDescription, string timestampDescription)
        {
            if (timestamp == default)
            {
                throw new InvalidDataException($"{metadataDescription} contains an invalid {timestampDescription} timestamp.");
            }

            ValidatePayloadTimestamp(timestamp, metadataDescription, timestampDescription);
        }

        private static void ValidateOptionalPayloadTimestamp(DateTime timestamp, string metadataDescription, string timestampDescription)
        {
            if (timestamp == default)
            {
                return;
            }

            ValidatePayloadTimestamp(timestamp, metadataDescription, timestampDescription);
        }

        private static void ValidatePayloadTimestamp(DateTime timestamp, string metadataDescription, string timestampDescription)
        {
            try
            {
                _ = timestamp.ToFileTimeUtc();
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new InvalidDataException($"{metadataDescription} contains an invalid {timestampDescription} timestamp.", ex);
            }
        }

        private FileOperationResult EncryptFolderPackage(ProcessingWorkItem workItem, string password, ProcessingRunOptions options, Action<double, string>? progress = null)
        {
            var elapsed = Stopwatch.StartNew();
            if (!workItem.EncryptAsFolderPackage || string.IsNullOrWhiteSpace(workItem.FolderRootPath))
            {
                throw new InvalidOperationException("Folder package encryption requires a folder-root work item.");
            }

            if (options.UseSteganography)
            {
                throw new InvalidOperationException("PNG carrier mode is not supported for folder packages.");
            }

            string rootFolderPath = workItem.FolderRootPath;
            string? backupPath = null;
            string outputPath = string.Empty;
            string tempPath = string.Empty;
            byte[]? metadataBytes = null;
            ValidateFolderPackageSourceRoot(rootFolderPath);

            if (options.RemoveOriginalsAfterSuccess &&
                options.UseCustomEncryptOutputDirectory &&
                IsDirectoryInsideSource(rootFolderPath, options.EncryptOutputDirectory))
            {
                throw new InvalidOperationException("Choose an encrypt output folder outside the source folder before removing originals.");
            }

            try
            {
                string normalizedAlgorithm = EncryptionAlgorithmCatalog.NormalizeForNewPayload(options.Algorithm);
                string metadataAlgorithm = EncryptionAlgorithmCatalog.GetFileFormatName(normalizedAlgorithm);
                int normalizedKeySizeBits = EncryptionAlgorithmCatalog.GetKeySizeBits(normalizedAlgorithm);
                long packageOriginalSizeBytes = workItem.QueueItems.Sum(item => item.SizeBytes);
                var manifest = new FolderPackageMetadata
                {
                    RootFolderPath = rootFolderPath,
                    RootFolderName = GetFolderDisplayName(rootFolderPath),
                    PackageLabel = string.IsNullOrWhiteSpace(options.Metadata.Label)
                        ? GetFolderDisplayName(rootFolderPath)
                        : options.Metadata.Label,
                    PackageNote = options.Metadata.Notes,
                    Algorithm = metadataAlgorithm,
                    KeySizeBits = normalizedKeySizeBits,
                    PackagePaddingLength = CalculateSizeHidingPadding(packageOriginalSizeBytes)
                };

                foreach (QueuedFileItem queueItem in workItem.QueueItems.OrderBy(item => GetRelativePathSafe(rootFolderPath, item.SourcePath), StringComparer.OrdinalIgnoreCase))
                {
                    manifest.Entries.Add(new FolderPackageEntryMetadata
                    {
                        RelativePath = GetRelativePathSafe(rootFolderPath, queueItem.SourcePath),
                        OriginalSize = queueItem.SizeBytes,
                        CreationTimeUtc = File.GetCreationTimeUtc(queueItem.SourcePath),
                        LastWriteTimeUtc = File.GetLastWriteTimeUtc(queueItem.SourcePath),
                        LastAccessTimeUtc = File.GetLastAccessTimeUtc(queueItem.SourcePath),
                        OriginalAttributes = (int)File.GetAttributes(queueItem.SourcePath),
                        ContentHashBase64 = ComputeSha256Base64ForFile(queueItem.SourcePath, _processingCancellation?.Token ?? CancellationToken.None)
                    });
                }

                metadataBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
                outputPath = ResolveAvailablePath(BuildFolderPackageOutputPath(rootFolderPath, options.ScrambleNames, options.UseCustomEncryptOutputDirectory ? options.EncryptOutputDirectory : null));
                tempPath = ResolveTemporaryOutputPath(outputPath);

                if (!string.IsNullOrWhiteSpace(options.BackupFolderPath))
                {
                    backupPath = CreateBackupFolderCopy(rootFolderPath, options.BackupFolderPath);
                }

                using (FileStream output = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    progress?.Invoke(5, "Preparing package");
                    PayloadChunkedService.WritePayload(
                        output,
                        metadataBytes,
                        (payloadStream, cancellationToken) =>
                        {
                            long totalBytes = manifest.Entries.Sum(entry => entry.OriginalSize);
                            long processedBytes = 0;
                            foreach (FolderPackageEntryMetadata entry in manifest.Entries)
                            {
                                string entrySourcePath = ResolveFolderPackageEntryPath(rootFolderPath, entry.RelativePath);
                                WriteInt64(payloadStream, entry.OriginalSize);
                                using FileStream source = File.OpenRead(entrySourcePath);
                                CopyStreamWithProgress(
                                    source,
                                    payloadStream,
                                    cancellationToken,
                                    source.Length,
                                    (percent, status) =>
                                    {
                                        double adjustedPercent = CalculateFolderPackageProgress(processedBytes, entry.OriginalSize, percent, totalBytes, 10, 80);
                                        progress?.Invoke(adjustedPercent, "Encrypting package");
                                    },
                                    0,
                                    100,
                                    "Encrypting package");
                                processedBytes += entry.OriginalSize;
                            }

                            progress?.Invoke(85, "Applying padding");
                            WriteRandomPadding(payloadStream, manifest.PackagePaddingLength, cancellationToken);
                        },
                        new PayloadWriteInputs(
                            password,
                            options.KeyfileBytes,
                            options.RecoveryKey,
                            131072,
                            0b0000_0011,
                            EncryptionAlgorithmCatalog.GetNewPayloadAlgorithmId(normalizedAlgorithm)),
                        _processingCancellation?.Token ?? CancellationToken.None);
                }

                if (options.VerifyAfterWrite)
                {
                    progress?.Invoke(95, "Verifying");
                    VerifyLockedFileV3(tempPath, password, options, progress);
                }

                PromoteTemporaryOutput(tempPath, outputPath);
                ApplyOutputTimestampPolicy(outputPath, rootFolderPath, options.OutputTimestampPolicy);
                long outputSizeBytes = new FileInfo(outputPath).Length;

                bool retained = true;
                if (options.RemoveOriginalsAfterSuccess)
                {
                    DeleteSourceDirectory(rootFolderPath, options.SecureDeleteOriginals);
                    retained = false;
                }

                progress?.Invoke(100, "Completed");

                return new FileOperationResult
                {
                    SourcePath = rootFolderPath,
                    OutputPath = outputPath,
                    BackupPath = backupPath,
                    Status = "Completed",
                    OriginalRetained = retained,
                    OutputVerified = options.VerifyAfterWrite,
                    Message = retained
                        ? $"Encrypted {manifest.Entries.Count} files into one folder package."
                        : $"Encrypted {manifest.Entries.Count} files into one folder package and removed the source folder.",
                    OriginalSizeBytes = packageOriginalSizeBytes,
                    OutputSizeBytes = outputSizeBytes,
                    CompressionRequested = options.CompressFiles,
                    CompressionApplied = false,
                    CompressionReason = "Folder packages are streamed without per-file compression.",
                    ElapsedMilliseconds = elapsed.ElapsedMilliseconds,
                    Algorithm = normalizedAlgorithm,
                    KeySizeBits = normalizedKeySizeBits
                };
            }
            catch (OperationCanceledException)
            {
                CleanupTemporaryFile(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                CleanupTemporaryFile(tempPath);
                throw new InvalidOperationException($"Folder package encryption failed: {GetFriendlyExceptionMessage(ex, "Unknown error while encrypting the folder package.")}", ex);
            }
            finally
            {
                ClearSensitiveBuffer(metadataBytes);
            }
        }

        private FileOperationResult DecryptFolderPackagePayloadV3(
            string filePath,
            OpenPayloadResult payload,
            FolderPackageMetadata packageMetadata,
            ProcessingRunOptions options,
            Action<double, string>? progress = null,
            string? relativeOutputDirectory = null)
        {
            ValidateSecureDeleteSourceFile(filePath, options.RemoveOriginalsAfterSuccess, options.SecureDeleteOriginals);

            var elapsed = Stopwatch.StartNew();
            string directory = ResolveDecryptOutputDirectory(filePath, options, relativeOutputDirectory);
            string packageFolderName = ResolveFolderPackageRootName(
                filePath,
                packageMetadata.RootFolderName,
                options.RestoreOriginalFilenames);
            string restoreRoot = ResolveAvailablePath(Path.Combine(directory, packageFolderName));
            bool restoreEntriesCompleted = false;

            try
            {
                Directory.CreateDirectory(restoreRoot);

                long totalBytes = packageMetadata.Entries.Sum(entry => entry.OriginalSize);
                long processedBytes = 0;
                foreach (FolderPackageEntryMetadata entry in packageMetadata.Entries)
                {
                    string targetPath = ResolveFolderPackageEntryPath(restoreRoot, entry.RelativePath);
                    string? targetDirectory = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrWhiteSpace(targetDirectory))
                    {
                        Directory.CreateDirectory(targetDirectory);
                    }

                    long entryLength = ReadInt64(payload.PlaintextStream);
                    ValidateFolderPackageEntryLength(entry, entryLength);
                    string finalTargetPath = ResolveAvailablePath(targetPath);
                    using (FileStream output = CreateNewOutputFileStream(finalTargetPath))
                    {
                        CopyFixedLengthStream(
                            payload.PlaintextStream,
                            output,
                            entryLength,
                            _processingCancellation?.Token ?? CancellationToken.None,
                            (percent, status) =>
                            {
                                double adjustedPercent = CalculateFolderPackageProgress(processedBytes, entry.OriginalSize, percent, totalBytes, 10, 90);
                                progress?.Invoke(adjustedPercent, "Decrypting package");
                            },
                            0,
                            100,
                            "Decrypting package");
                    }

                    ValidateFolderPackageEntryFileHash(finalTargetPath, entry, _processingCancellation?.Token ?? CancellationToken.None);
                    processedBytes += entry.OriginalSize;
                    RestoreFileMetadata(
                        finalTargetPath,
                        new FileMetadata
                        {
                            OriginalFileName = Path.GetFileName(finalTargetPath),
                            OriginalSize = entry.OriginalSize,
                            CreationTime = entry.CreationTimeUtc,
                            LastWriteTime = entry.LastWriteTimeUtc,
                            LastAccessTime = entry.LastAccessTimeUtc,
                            OriginalAttributes = (System.IO.FileAttributes)entry.OriginalAttributes
                        });
                }

                SkipBytes(payload.PlaintextStream, packageMetadata.PackagePaddingLength, _processingCancellation?.Token ?? CancellationToken.None);

                restoreEntriesCompleted = true;
                bool retained = true;
                if (options.RemoveOriginalsAfterSuccess)
                {
                    DeleteSourceFile(filePath, options.SecureDeleteOriginals);
                    retained = false;
                }

                progress?.Invoke(100, "Completed");

                return new FileOperationResult
                {
                    SourcePath = filePath,
                    OutputPath = restoreRoot,
                    Status = "Completed",
                    OriginalRetained = retained,
                    OutputVerified = true,
                    Message = $"Restored {EncryptionAlgorithmCatalog.GetDisplayName(payload.Header.AlgorithmId)} folder package to {restoreRoot} with {packageMetadata.Entries.Count} files.",
                    OriginalSizeBytes = new FileInfo(filePath).Length,
                    OutputSizeBytes = totalBytes,
                    CompressionApplied = false,
                    CompressionReason = "Folder package payload was not compressed.",
                    ElapsedMilliseconds = elapsed.ElapsedMilliseconds,
                    Algorithm = EncryptionAlgorithmCatalog.GetDisplayName(payload.Header.AlgorithmId),
                    KeySizeBits = packageMetadata.KeySizeBits > 0
                        ? packageMetadata.KeySizeBits
                        : EncryptionAlgorithmCatalog.GetKeySizeBits(EncryptionAlgorithmCatalog.GetDisplayName(payload.Header.AlgorithmId))
                };
            }
            catch
            {
                if (!restoreEntriesCompleted)
                {
                    TryCleanupPartialFolderRestore(restoreRoot);
                }

                throw;
            }
        }

        internal static bool TryCleanupPartialFolderRestore(string restoreRoot)
        {
            try
            {
                DeleteSourceDirectory(restoreRoot, secureDelete: false);
                return !Directory.Exists(restoreRoot);
            }
            catch
            {
                return false;
            }
        }

        private static void VerifyFolderPackagePayloadV3(
            OpenPayloadResult payload,
            FolderPackageMetadata packageMetadata,
            CancellationToken cancellationToken,
            Action<double, string>? progress = null)
        {
            long totalBytes = packageMetadata.Entries.Sum(entry => entry.OriginalSize);
            long processedBytes = 0;
            foreach (FolderPackageEntryMetadata entry in packageMetadata.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long entryLength = ReadInt64(payload.PlaintextStream);
                ValidateFolderPackageEntryLength(entry, entryLength);
                byte[] hash = ComputeFixedLengthStreamHash(
                    payload.PlaintextStream,
                    entryLength,
                    cancellationToken,
                    (percent, status) =>
                    {
                        double adjustedPercent = CalculateFolderPackageProgress(processedBytes, entry.OriginalSize, percent, totalBytes, 10, 95);
                        progress?.Invoke(adjustedPercent, "Verifying package");
                    },
                    0,
                    100,
                    "Verifying package");
                processedBytes += entry.OriginalSize;
                if (!string.IsNullOrWhiteSpace(entry.ContentHashBase64) &&
                    !hash.AsSpan().SequenceEqual(Convert.FromBase64String(entry.ContentHashBase64)))
                {
                    throw new UnauthorizedAccessException("Folder package entry failed integrity validation.");
                }
            }

            SkipBytes(payload.PlaintextStream, packageMetadata.PackagePaddingLength, cancellationToken);
        }

        internal static void ValidateFolderPackageEntryFileHash(
            string filePath,
            FolderPackageEntryMetadata entry,
            CancellationToken cancellationToken = default)
        {
            byte[] expectedHash = Convert.FromBase64String(entry.ContentHashBase64);
            byte[] actualHash = ComputeSha256ForFile(filePath, cancellationToken);
            if (!expectedHash.AsSpan().SequenceEqual(actualHash))
            {
                throw new UnauthorizedAccessException("Restored folder package entry failed integrity validation.");
            }
        }

        internal static void ValidateFolderPackageEntryLength(FolderPackageEntryMetadata entry, long payloadLength)
        {
            if (entry.OriginalSize < 0)
            {
                throw new InvalidDataException("Folder package entry has invalid size metadata.");
            }

            if (payloadLength < 0)
            {
                throw new InvalidDataException("Folder package entry has invalid payload length.");
            }

            if (payloadLength != entry.OriginalSize)
            {
                throw new InvalidDataException("Folder package entry length does not match metadata.");
            }
        }

        internal static double CalculateFolderPackageProgress(
            long processedBytes,
            long entryBytes,
            double entryPercent,
            long totalBytes,
            double startPercent,
            double endPercent)
        {
            if (totalBytes <= 0)
            {
                return endPercent;
            }

            double safeEntryPercent = double.IsFinite(entryPercent) ? Math.Clamp(entryPercent, 0, 100) : 0;
            double currentEntryBytes = safeEntryPercent / 100.0 * Math.Max(0, entryBytes);
            double completedRatio = Math.Clamp((processedBytes + currentEntryBytes) / totalBytes, 0, 1);
            double progress = startPercent + completedRatio * (endPercent - startPercent);
            return double.IsFinite(progress) ? progress : endPercent;
        }

        internal static byte[] ComputeStreamHash(
            Stream stream,
            CancellationToken cancellationToken,
            Action<double, string>? progress = null,
            double startPercent = 0,
            double endPercent = 100,
            string status = "Processing")
        {
            using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[131072];
            try
            {
                long totalLength = stream.CanSeek ? Math.Max(1, stream.Length - stream.Position) : 0;
                long processed = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        break;
                    }

                    incrementalHash.AppendData(buffer, 0, read);
                    processed += read;
                    if (totalLength > 0)
                    {
                        double percent = startPercent + ((double)processed / totalLength) * (endPercent - startPercent);
                        progress?.Invoke(percent, status);
                    }
                }

                return incrementalHash.GetHashAndReset();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }

        private static byte[] ComputeFixedLengthStreamHash(
            Stream stream,
            long length,
            CancellationToken cancellationToken,
            Action<double, string>? progress = null,
            double startPercent = 0,
            double endPercent = 100,
            string status = "Processing")
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[131072];
            try
            {
                long remaining = length;
                long processed = 0;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int read = stream.Read(buffer, 0, toRead);
                    if (read == 0)
                    {
                        throw new EndOfStreamException();
                    }

                    incrementalHash.AppendData(buffer, 0, read);
                    remaining -= read;
                    processed += read;
                    if (length > 0)
                    {
                        double percent = startPercent + ((double)processed / length) * (endPercent - startPercent);
                        progress?.Invoke(percent, status);
                    }
                }

                return incrementalHash.GetHashAndReset();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }

        internal static void CopyFixedLengthStream(
            Stream input,
            Stream output,
            long length,
            CancellationToken cancellationToken,
            Action<double, string>? progress = null,
            double startPercent = 0,
            double endPercent = 100,
            string status = "Processing")
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            byte[] buffer = new byte[131072];
            try
            {
                long remaining = length;
                long processed = 0;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int read = input.Read(buffer, 0, toRead);
                    if (read == 0)
                    {
                        throw new EndOfStreamException();
                    }

                    output.Write(buffer, 0, read);
                    remaining -= read;
                    processed += read;
                    if (length > 0)
                    {
                        double percent = startPercent + ((double)processed / length) * (endPercent - startPercent);
                        progress?.Invoke(percent, status);
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }

        private static string CreateBackupFolderCopy(string sourceFolderPath, string backupFolderPath)
        {
            if (IsBackupFolderInsideSource(sourceFolderPath, backupFolderPath))
            {
                throw new InvalidOperationException("Choose a backup folder outside the source folder before removing originals.");
            }

            Directory.CreateDirectory(backupFolderPath);
            string destinationRoot = ResolveAvailableDirectoryPath(Path.Combine(
                backupFolderPath,
                $"{GetFolderDisplayName(sourceFolderPath)}_{DateTime.Now:yyyyMMdd_HHmmss}"));
            CopyDirectory(sourceFolderPath, destinationRoot);
            return destinationRoot;
        }

        internal static string ResolveAvailableDirectoryPath(string directoryPath)
        {
            ValidateNormalOutputPath(directoryPath, allowDirectoryPath: true);
            if (!Directory.Exists(directoryPath) && !File.Exists(directoryPath))
            {
                return directoryPath;
            }

            for (int counter = 1; counter <= MaxResolveAvailablePathAttempts; counter++)
            {
                string candidate = $"{directoryPath}_{counter}";
                if (!Directory.Exists(candidate) && !File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new IOException("Could not find an available directory name near the requested path.");
        }

        internal static bool IsBackupFolderInsideSource(string sourceFolderPath, string backupFolderPath)
        {
            return IsDirectoryInsideSource(sourceFolderPath, backupFolderPath);
        }

        internal static bool IsDirectoryInsideSource(string sourceFolderPath, string candidateDirectoryPath)
        {
            if (!TryNormalizeBoundaryDirectoryPath(sourceFolderPath, out string sourceFullPath) ||
                !TryNormalizeBoundaryDirectoryPath(candidateDirectoryPath, out string candidateFullPath))
            {
                return false;
            }

            if (IsSameDirectoryOrChild(candidateFullPath, sourceFullPath))
            {
                return true;
            }

            string? sourceResolvedPath = TryResolveDirectoryPathThroughExistingParent(sourceFullPath);
            string? candidateResolvedPath = TryResolveDirectoryPathThroughExistingParent(candidateFullPath);
            return sourceResolvedPath != null &&
                candidateResolvedPath != null &&
                IsSameDirectoryOrChild(candidateResolvedPath, sourceResolvedPath);
        }

        private static bool TryNormalizeBoundaryDirectoryPath(string? path, out string fullPath)
        {
            fullPath = string.Empty;
            if (string.IsNullOrWhiteSpace(path) ||
                path.Any(character => char.IsControl(character) || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format))
            {
                return false;
            }

            string trimmedPath = path.Trim();
            if (!Path.IsPathFullyQualified(trimmedPath))
            {
                return false;
            }

            try
            {
                string normalizedPath = Path.GetFullPath(trimmedPath);
                string root = Path.GetPathRoot(normalizedPath) ?? string.Empty;
                string pathWithoutRoot = normalizedPath.Length > root.Length ? normalizedPath[root.Length..] : string.Empty;
                if (pathWithoutRoot.Contains(':', StringComparison.Ordinal))
                {
                    return false;
                }

                fullPath = normalizedPath;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        private static string? TryResolveDirectoryPathThroughExistingParent(string directoryPath)
        {
            try
            {
                string fullPath = Path.GetFullPath(directoryPath.Trim());
                if (Directory.Exists(fullPath))
                {
                    return TryResolveExistingDirectoryTarget(fullPath);
                }

                var pendingSegments = new Stack<string>();
                string? currentPath = fullPath;
                while (!string.IsNullOrWhiteSpace(currentPath) && !Directory.Exists(currentPath))
                {
                    string trimmedPath = currentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string segment = Path.GetFileName(trimmedPath);
                    if (string.IsNullOrWhiteSpace(segment))
                    {
                        return null;
                    }

                    pendingSegments.Push(segment);
                    currentPath = Path.GetDirectoryName(trimmedPath);
                }

                if (string.IsNullOrWhiteSpace(currentPath))
                {
                    return null;
                }

                string? resolvedPath = TryResolveExistingDirectoryTarget(currentPath);
                if (resolvedPath == null)
                {
                    return null;
                }

                foreach (string segment in pendingSegments)
                {
                    resolvedPath = Path.Combine(resolvedPath, segment);
                }

                return Path.GetFullPath(resolvedPath);
            }
            catch
            {
                return null;
            }
        }

        private static string? TryResolveExistingDirectoryTarget(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                return null;
            }

            var directoryInfo = new DirectoryInfo(directoryPath);
            FileSystemInfo? resolvedTarget = directoryInfo.ResolveLinkTarget(returnFinalTarget: true);
            return Path.GetFullPath(resolvedTarget?.FullName ?? directoryInfo.FullName);
        }

        private static void CopyDirectory(string sourceFolderPath, string destinationFolderPath)
        {
            Directory.CreateDirectory(destinationFolderPath);
            EnumerationOptions options = CreateUserTreeEnumerationOptions();
            int copiedDirectories = 0;
            foreach (string directory in Directory.EnumerateDirectories(sourceFolderPath, "*", options))
            {
                if (copiedDirectories >= MaxQueueExpandedDirectories)
                {
                    throw new InvalidOperationException("Source folder is too large to back up safely.");
                }

                copiedDirectories++;
                string relative = Path.GetRelativePath(sourceFolderPath, directory);
                Directory.CreateDirectory(Path.Combine(destinationFolderPath, relative));
            }

            int copiedFiles = 0;
            foreach (string file in Directory.EnumerateFiles(sourceFolderPath, "*", options))
            {
                if (copiedFiles >= MaxQueueExpandedFiles)
                {
                    throw new InvalidOperationException("Source folder is too large to back up safely.");
                }

                copiedFiles++;
                string relative = Path.GetRelativePath(sourceFolderPath, file);
                string destination = Path.Combine(destinationFolderPath, relative);
                string? directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.Copy(file, destination, overwrite: false);
            }
        }

        internal static void DeleteSourceDirectory(string directoryPath, bool secureDelete, int secureDeletePasses = 3)
        {
            if (!Directory.Exists(directoryPath))
            {
                return;
            }

            if (IsReparsePointPath(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: false);
                return;
            }

            DeleteSourceDirectoryContents(directoryPath, secureDelete, secureDeletePasses);
            FileCleanupService.ClearReadOnlyAttribute(directoryPath);
            Directory.Delete(directoryPath, recursive: false);
        }

        internal static EnumerationOptions CreateUserTreeEnumerationOptions()
        {
            return new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = System.IO.FileAttributes.ReparsePoint
            };
        }

        internal static void ValidateFolderPackageSourceRoot(string rootFolderPath)
        {
            if (IsReparsePointPath(rootFolderPath))
            {
                throw new IOException("Folder package source root cannot be a symlink or junction.");
            }
        }

        private static void DeleteSourceDirectoryContents(string directoryPath, bool secureDelete, int secureDeletePasses)
        {
            foreach (string file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly))
            {
                if (IsReparsePointPath(file))
                {
                    File.Delete(file);
                    continue;
                }

                DeleteSourceFile(file, secureDelete, secureDeletePasses);
            }

            foreach (string childDirectory in Directory.EnumerateDirectories(directoryPath, "*", SearchOption.TopDirectoryOnly))
            {
                if (IsReparsePointPath(childDirectory))
                {
                    Directory.Delete(childDirectory, recursive: false);
                    continue;
                }

                DeleteSourceDirectoryContents(childDirectory, secureDelete, secureDeletePasses);
                FileCleanupService.ClearReadOnlyAttribute(childDirectory);
                Directory.Delete(childDirectory, recursive: false);
            }
        }

        private static bool IsReparsePointPath(string path)
        {
            try
            {
                return (File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint) == System.IO.FileAttributes.ReparsePoint;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> ConfirmFolderPackageRestoreAsync(
            string filePath,
            string password,
            ProcessingRunOptions options,
            string? relativeOutputDirectory = null)
        {
            FolderPackageMetadata? metadata = TryReadFolderPackageMetadata(filePath, password, options);
            if (metadata == null)
            {
                return true;
            }

            string previewDirectory = ResolveDecryptOutputDirectory(filePath, options, relativeOutputDirectory);
            string previewRootName = ResolveFolderPackageRootName(filePath, metadata.RootFolderName, options.RestoreOriginalFilenames);
            string preferredRoot = Path.Combine(previewDirectory, previewRootName);
            string previewRoot = ResolveAvailablePath(preferredRoot);
            bool rootWillBeRenamed = !string.Equals(preferredRoot, previewRoot, StringComparison.OrdinalIgnoreCase);

            FolderPackageEntryMetadata? unsafeEntry = metadata.Entries.FirstOrDefault(entry => !IsSafeFolderPackageRelativePath(entry.RelativePath));
            if (unsafeEntry != null)
            {
                throw new InvalidDataException("Folder package contains an unsafe restore path.");
            }

            List<FolderPackageEntryMetadata> conflictingEntries = metadata.Entries
                .Where(entry =>
                {
                    string targetPath = ResolveFolderPackageEntryPath(preferredRoot, entry.RelativePath);
                    return File.Exists(targetPath) || Directory.Exists(targetPath);
                })
                .ToList();

            string sampleEntries = string.Join(
                "\n",
                metadata.Entries
                    .Take(8)
                    .Select(entry => $"- {entry.RelativePath} ({FormatFileSize(entry.OriginalSize)})"));
            string conflictSamples = conflictingEntries.Count == 0
                ? "None"
                : string.Join("\n", conflictingEntries.Take(5).Select(entry => $"- {entry.RelativePath}"));

            string message =
                $"Folder package: {metadata.RootFolderName}\n" +
                $"Files: {metadata.Entries.Count}\n" +
                $"Requested restore root: {preferredRoot}\n" +
                $"Final restore root: {previewRoot}\n" +
                $"Root will be renamed: {(rootWillBeRenamed ? "Yes" : "No")}\n" +
                $"Existing entries inside requested root: {conflictingEntries.Count}\n" +
                $"Conflict samples:\n{conflictSamples}\n\n" +
                $"{sampleEntries}\n\n" +
                "Continue with restore preview and write the package contents?";

            return await ShowConfirmDialogAsync(message, "Restore Preview");
        }

        private FolderPackageMetadata? TryReadFolderPackageMetadata(string filePath, string password, ProcessingRunOptions options)
        {
            filePath = RequireExistingFile(filePath);
            using FileStream input = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using OpenPayloadResult payload = PayloadChunkedService.OpenPayload(
                input,
                new PayloadUnlockInputs(password, options.KeyfileBytes, options.RecoveryKey),
                CancellationToken.None);

            if (!IsFolderPackagePayloadMetadata(payload.MetadataBytes))
            {
                return null;
            }

            FolderPackageMetadata? metadata = JsonSerializer.Deserialize<FolderPackageMetadata>(payload.MetadataBytes, JsonOptions);
            if (metadata != null)
            {
                ValidateFolderPackageMetadata(metadata);
                ValidatePayloadMetadataAlgorithm(
                    metadata.Algorithm,
                    metadata.KeySizeBits,
                    payload.Header.AlgorithmId,
                    payload.Header.Version,
                    "Folder package metadata");
            }

            return metadata;
        }

        private void RotatePayloadKeys(
            string filePath,
            string currentPassword,
            string? currentRecoveryKey,
            byte[]? keyfileBytes,
            string newPassword,
            string? newRecoveryKey,
            CancellationToken cancellationToken = default)
        {
            filePath = RequireExistingFile(filePath);
            string tempPath = ResolveTemporaryOutputPath(filePath, ".rotate.tmp");
            try
            {
                using (FileStream input = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (FileStream output = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    PayloadChunkedService.RotateKeys(
                        input,
                        output,
                        new PayloadRotateInputs(
                            new PayloadUnlockInputs(currentPassword, keyfileBytes, currentRecoveryKey),
                            newPassword,
                            keyfileBytes,
                            newRecoveryKey),
                        cancellationToken);

                    output.Flush(flushToDisk: true);
                }

                FileWriteService.ReplaceFileWithTemporaryFile(tempPath, filePath);
            }
            catch
            {
                CleanupTemporaryFile(tempPath);
                throw;
            }
        }

        private (FileMetadata Metadata, byte[] FileData) UnlockFilePayload(
            string filePath,
            string password,
            ProcessingRunOptions options,
            Action<double, string>? progress = null)
        {
            byte[]? encryptedBytes = null;
            byte[]? salt = null;
            byte[]? iv = null;
            byte[]? tag = null;
            byte[]? ciphertext = null;
            byte[]? key = null;
            byte[]? plaintext = null;
            byte[]? metadataBytes = null;
            byte[]? compressedFileData = null;
            byte[]? fileData = null;
            bool returnsFileData = false;

            try
            {
                encryptedBytes = TryExtractStegoPayload(filePath) ?? ReadAllBytesWithProgress(
                    filePath,
                    _processingCancellation?.Token ?? CancellationToken.None,
                    progress,
                    5,
                    35,
                    "Reading payload");

                using var fs = new MemoryStream(encryptedBytes);
                byte version = (byte)fs.ReadByte();
                if (version != LegacyPayloadFormatVersion)
                {
                    throw new InvalidDataException("Unsupported file format version.");
                }

                salt = new byte[SALT_SIZE];
                iv = new byte[IV_SIZE];
                tag = new byte[TAG_SIZE];
                ReadExact(fs, salt, 0, SALT_SIZE);
                ReadExact(fs, iv, 0, IV_SIZE);
                ReadExact(fs, tag, 0, TAG_SIZE);

                int ciphertextLength = GetLegacyPayloadCiphertextLength(fs.Length);
                ciphertext = new byte[ciphertextLength];
                ReadExact(fs, ciphertext, 0, ciphertext.Length);
                key = DeriveArgon2idKey(password, salt, options.KeyfileBytes);
                plaintext = new byte[ciphertext.Length];

                progress?.Invoke(60, "Decrypting");
                using (var aes = new AesGcm(key, TAG_SIZE))
                {
                    try
                    {
                        aes.Decrypt(iv, ciphertext, tag, plaintext);
                    }
                    catch (CryptographicException)
                    {
                        throw new UnauthorizedAccessException("Invalid password or corrupted file.");
                    }
                }

                var layout = ReadLegacyPayloadLayout(plaintext);
                metadataBytes = plaintext.AsSpan(layout.MetadataOffset, layout.MetadataLength).ToArray();
                FileMetadata metadata = DeserializeMetadata(metadataBytes);
                fileData = plaintext.AsSpan(layout.FileDataOffset).ToArray();
                if (metadata.IsCompressed)
                {
                    compressedFileData = fileData;
                    fileData = DecompressData(compressedFileData, _processingCancellation?.Token ?? CancellationToken.None);
                }

                if (metadata.ContentHash.Length > 0)
                {
                    EnsureHashMatch(metadata.ContentHash, fileData);
                }

                returnsFileData = true;
                return (metadata, fileData);
            }
            finally
            {
                ClearSensitiveBuffer(encryptedBytes);
                ClearSensitiveBuffer(salt);
                ClearSensitiveBuffer(iv);
                ClearSensitiveBuffer(tag);
                ClearSensitiveBuffer(ciphertext);
                ClearSensitiveBuffer(key);
                ClearSensitiveBuffer(plaintext);
                ClearSensitiveBuffer(metadataBytes);

                if (compressedFileData is not null && !ReferenceEquals(compressedFileData, fileData))
                {
                    ClearSensitiveBuffer(compressedFileData);
                }

                if (!returnsFileData)
                {
                    ClearSensitiveBuffer(fileData);
                }
            }
        }

        internal static int GetLegacyPayloadCiphertextLength(long payloadLength)
        {
            const int legacyEnvelopeOverhead = 1 + SALT_SIZE + IV_SIZE + TAG_SIZE;
            long ciphertextLength = payloadLength - legacyEnvelopeOverhead;
            if (ciphertextLength < sizeof(int))
            {
                throw new InvalidDataException("Legacy payload is truncated.");
            }

            if (ciphertextLength > int.MaxValue)
            {
                throw new InvalidDataException("Legacy payload is too large to decrypt in memory.");
            }

            return (int)ciphertextLength;
        }

        internal static (int MetadataOffset, int MetadataLength, int PaddingLength, int FileDataOffset) ReadLegacyPayloadLayout(ReadOnlySpan<byte> plaintext)
        {
            if (plaintext.Length < sizeof(int))
            {
                throw new InvalidDataException("Legacy payload is missing metadata length.");
            }

            int metadataLength = BinaryPrimitives.ReadInt32LittleEndian(plaintext[..sizeof(int)]);
            if (metadataLength <= 0)
            {
                throw new InvalidDataException("Legacy payload contains an invalid metadata length.");
            }

            int metadataOffset = sizeof(int);
            if (metadataLength > plaintext.Length - metadataOffset)
            {
                throw new InvalidDataException("Legacy payload metadata length exceeds the decrypted payload.");
            }

            int paddingLengthOffset = metadataOffset + metadataLength;
            if (plaintext.Length - paddingLengthOffset < sizeof(int))
            {
                throw new InvalidDataException("Legacy payload is missing padding length.");
            }

            int paddingLength = BinaryPrimitives.ReadInt32LittleEndian(plaintext.Slice(paddingLengthOffset, sizeof(int)));
            if (paddingLength < 0)
            {
                throw new InvalidDataException("Legacy payload contains an invalid padding length.");
            }

            int fileDataOffset = paddingLengthOffset + sizeof(int);
            if (paddingLength > plaintext.Length - fileDataOffset)
            {
                throw new InvalidDataException("Legacy payload padding length exceeds the decrypted payload.");
            }

            fileDataOffset += paddingLength;
            return (metadataOffset, metadataLength, paddingLength, fileDataOffset);
        }

        private static void ClearSensitiveBuffer(byte[]? buffer)
        {
            if (buffer is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }

        private static void ReadExact(MemoryStream fs, byte[] buffer, int offset, int count)
        {
            int readTotal = 0;
            while (readTotal < count)
            {
                int read = fs.Read(buffer, offset + readTotal, count - readTotal);
                if (read == 0) throw new EndOfStreamException();
                readTotal += read;
            }
        }

        private static void CleanupTemporaryFile(string? tempPath)
        {
            _ = FileCleanupService.DeleteTemporaryFiles(tempPath);
        }

        private static void PromoteTemporaryOutput(string tempPath, string finalPath)
        {
            FileWriteService.ReplaceFileWithTemporaryFile(tempPath, finalPath);
        }

        private static void VerifyWrittenFile(string path, byte[] expectedBytes)
        {
            byte[] expectedDigest = SHA256.HashData(expectedBytes);
            using FileStream stream = File.OpenRead(path);
            byte[] actualDigest = SHA256.HashData(stream);
            if (!actualDigest.AsSpan().SequenceEqual(expectedDigest))
            {
                throw new IOException("Output verification failed after writing the file.");
            }
        }

        private static void RestoreFileMetadata(string path, FileMetadata metadata)
        {
            FileCleanupService.ClearReadOnlyAttribute(path);

            if (metadata.CreationTime.Kind == DateTimeKind.Utc)
            {
                File.SetCreationTimeUtc(path, metadata.CreationTime);
            }
            else
            {
                File.SetCreationTime(path, metadata.CreationTime);
            }

            if (metadata.LastWriteTime.Kind == DateTimeKind.Utc)
            {
                File.SetLastWriteTimeUtc(path, metadata.LastWriteTime);
            }
            else
            {
                File.SetLastWriteTime(path, metadata.LastWriteTime);
            }

            if (metadata.LastAccessTime != default)
            {
                if (metadata.LastAccessTime.Kind == DateTimeKind.Utc)
                {
                    File.SetLastAccessTimeUtc(path, metadata.LastAccessTime);
                }
                else
                {
                    File.SetLastAccessTime(path, metadata.LastAccessTime);
                }
            }

            File.SetAttributes(path, NormalizeRestoredFileAttributes(metadata.OriginalAttributes));
        }

        internal static System.IO.FileAttributes NormalizeRestoredFileAttributes(System.IO.FileAttributes attributes)
        {
            const System.IO.FileAttributes restorableFileAttributes =
                System.IO.FileAttributes.Archive |
                System.IO.FileAttributes.ReadOnly |
                System.IO.FileAttributes.Hidden |
                System.IO.FileAttributes.System |
                System.IO.FileAttributes.Temporary |
                System.IO.FileAttributes.NotContentIndexed;

            System.IO.FileAttributes normalized = attributes & restorableFileAttributes;
            return normalized == 0 ? System.IO.FileAttributes.Normal : normalized;
        }

        private void ApplyOutputTimestampPolicy(string outputPath, string sourcePath, string policy)
        {
            policy = AppPreferencesStore.NormalizeOutputTimestampPolicy(policy);
            if (string.Equals(policy, AppPreferencesStore.PreserveSourceTimestampsPolicy, StringComparison.OrdinalIgnoreCase))
            {
                File.SetCreationTimeUtc(outputPath, File.GetCreationTimeUtc(sourcePath));
                File.SetLastWriteTimeUtc(outputPath, File.GetLastWriteTimeUtc(sourcePath));
                return;
            }

            if (string.Equals(policy, AppPreferencesStore.RandomizeTimestampPolicy, StringComparison.OrdinalIgnoreCase))
            {
                (DateTime created, DateTime modified) = GenerateRandomizedDates();
                File.SetCreationTimeUtc(outputPath, created);
                File.SetLastWriteTimeUtc(outputPath, modified);
                return;
            }

            DateTime utcNow = DateTime.UtcNow;
            File.SetCreationTimeUtc(outputPath, utcNow);
            File.SetLastWriteTimeUtc(outputPath, utcNow);
        }

        internal static string CreateBackupCopy(string sourcePath, string backupFolderPath)
        {
            ValidateSourceDeleteTargetPath(sourcePath);
            ValidateNormalOutputPath(backupFolderPath, allowDirectoryPath: true);
            if (IsReparsePointPath(sourcePath))
            {
                throw new IOException("Backup copy does not copy file reparse points.");
            }

            Directory.CreateDirectory(backupFolderPath);
            string destination = ResolveBackupCopyPath(sourcePath, backupFolderPath, DateTime.Now);

            File.Copy(sourcePath, destination);
            return destination;
        }

        internal static string ResolveBackupCopyPath(string sourcePath, string backupFolderPath, DateTime timestamp)
        {
            string fileName = Path.GetFileNameWithoutExtension(sourcePath);
            string extension = Path.GetExtension(sourcePath);
            return FileWriteService.ResolveAvailablePath(Path.Combine(
                backupFolderPath,
                $"{fileName}_{timestamp:yyyyMMdd_HHmmss}{extension}"));
        }

        internal static void DeleteSourceFile(string sourcePath, bool secureDelete, int secureDeletePasses = 3)
        {
            ValidateSourceDeleteTargetPath(sourcePath);
            if (secureDelete)
            {
                SecureDelete(sourcePath, secureDeletePasses);
            }
            else
            {
                FileCleanupService.ClearReadOnlyAttribute(sourcePath);
                File.Delete(sourcePath);
            }
        }

        internal static void ValidateSecureDeleteSourceFile(string sourcePath, bool removeOriginalsAfterSuccess, bool secureDeleteOriginals)
        {
            if (!removeOriginalsAfterSuccess || !secureDeleteOriginals)
            {
                return;
            }

            if (IsReparsePointPath(sourcePath))
            {
                throw new IOException("Secure delete cannot overwrite file symlinks or junctions. Choose the target file directly or turn off best-effort overwrite.");
            }
        }

        private void AppendHistory(string operation, ProcessingRunOptions options, List<FileOperationResult> results, bool cancelled)
        {
            OperationMetricsSummary metrics = OperationHistoryMetrics.Calculate(results);
            (string historyAlgorithm, int historyKeySizeBits) = ResolveHistoryAlgorithm(options, results);
            var entry = new OperationHistoryEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                TimestampUtc = DateTime.UtcNow,
                Operation = operation,
                ProfileName = options.ProfileName,
                Algorithm = historyAlgorithm,
                Mode = options.Mode,
                KeySizeBits = historyKeySizeBits,
                UsedKeyfile = options.KeyfileBytes is { Length: > 0 },
                RemoveOriginalsAfterSuccess = options.RemoveOriginalsAfterSuccess,
                SecureDeleteOriginals = options.SecureDeleteOriginals,
                VerifyAfterWrite = options.VerifyAfterWrite,
                BackupFolderPath = options.BackupFolderPath,
                Cancelled = cancelled,
                SuccessCount = results.Count(result => OperationHistoryMetrics.IsSuccessfulStatus(result.Status)),
                FailureCount = results.Count(result => OperationHistoryMetrics.IsFailedStatus(result.Status)),
                TotalOriginalSizeBytes = metrics.TotalOriginalSizeBytes,
                TotalOutputSizeBytes = metrics.TotalOutputSizeBytes,
                TotalStorageSavedBytes = metrics.TotalStorageSavedBytes,
                TotalStorageAddedBytes = metrics.TotalStorageAddedBytes,
                ElapsedMilliseconds = metrics.ElapsedMilliseconds,
                CompressionRequestedCount = metrics.CompressionRequestedCount,
                CompressionAppliedCount = metrics.CompressionAppliedCount,
                CompressionSkippedCount = metrics.CompressionSkippedCount,
                FailureCategorySummary = metrics.FailureCategorySummary,
                Results = results
            };

            _operationHistory.Insert(0, entry);
            while (_operationHistory.Count > MaxHistoryEntries)
            {
                _operationHistory.RemoveAt(_operationHistory.Count - 1);
            }

            SaveHistory();
            RefreshHistoryItems();
        }

        private static (string Algorithm, int KeySizeBits) ResolveHistoryAlgorithm(
            ProcessingRunOptions options,
            IReadOnlyCollection<FileOperationResult> results)
        {
            string[] algorithms = results
                .Select(result => result.Algorithm)
                .Where(algorithm => !string.IsNullOrWhiteSpace(algorithm))
                .Select(algorithm => algorithm!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (algorithms.Length == 0)
            {
                return (options.Algorithm, options.KeySizeBits);
            }

            if (algorithms.Length > 1)
            {
                return ("Mixed payload algorithms", 0);
            }

            int[] keySizes = results
                .Where(result => string.Equals(result.Algorithm, algorithms[0], StringComparison.OrdinalIgnoreCase))
                .Select(result => result.KeySizeBits.GetValueOrDefault())
                .Where(keySize => keySize > 0)
                .Distinct()
                .ToArray();

            if (keySizes.Length == 1)
            {
                return (algorithms[0], keySizes[0]);
            }

            return EncryptionAlgorithmCatalog.TryNormalize(algorithms[0], out string normalizedAlgorithm)
                ? (normalizedAlgorithm, EncryptionAlgorithmCatalog.GetKeySizeBits(normalizedAlgorithm))
                : (algorithms[0], options.KeySizeBits);
        }

        private sealed record PreflightIssue(PreflightSeverity Severity, string Message);

        private List<PreflightSnapshotItem> CapturePreflightSnapshot()
        {
            return FileList
                .Select(item => new PreflightSnapshotItem(
                    item.SourcePath,
                    item.SourceRootPath,
                    item.SourceRootIsFolder,
                    item.Status))
                .ToList();
        }

        private PreflightEvaluationResult BuildPreflightEvaluation(
            List<PreflightSnapshotItem> allFiles,
            ProcessingIntent intent,
            ProcessingRunOptions? options,
            UserExperienceLevel experienceLevel)
        {
            var result = new PreflightEvaluationResult();
            bool encrypt = intent == ProcessingIntent.Encrypt;
            bool decryptLike = intent is ProcessingIntent.Decrypt or ProcessingIntent.Verify;

            if (allFiles.Count == 0)
            {
                result.Issues.Add(new PreflightIssue(PreflightSeverity.Info, "Queue is empty."));
                return result;
            }

            if (options != null && !string.IsNullOrWhiteSpace(options.BackupFolderPath))
            {
                try
                {
                    ValidateNormalOutputPath(options.BackupFolderPath, allowDirectoryPath: true);
                    Directory.CreateDirectory(options.BackupFolderPath);
                    if (encrypt && options.PackageFolders)
                    {
                        foreach (string sourceRoot in allFiles
                                     .Where(item => item.SourceRootIsFolder)
                                     .Select(item => item.SourceRootPath)
                                     .Where(path => !string.IsNullOrWhiteSpace(path))
                                     .Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            if (IsBackupFolderInsideSource(sourceRoot, options.BackupFolderPath))
                            {
                                result.Issues.Add(new PreflightIssue(PreflightSeverity.Error, "Choose a backup folder outside the source folder before removing originals."));
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Issues.Add(new PreflightIssue(PreflightSeverity.Error, $"Backup folder is not available: {GetFriendlyExceptionMessage(ex, "Backup folder check failed.")}"));
                }
            }

            if (options != null && encrypt && options.UseCustomEncryptOutputDirectory)
            {
                if (string.IsNullOrWhiteSpace(options.EncryptOutputDirectory))
                {
                    result.Issues.Add(new PreflightIssue(PreflightSeverity.Error, "Choose a custom output folder or switch back to Same folder as source."));
                }
                else
                {
                    try
                    {
                        ValidateNormalOutputPath(options.EncryptOutputDirectory, allowDirectoryPath: true);
                        Directory.CreateDirectory(options.EncryptOutputDirectory);
                        if (options.RemoveOriginalsAfterSuccess && options.PackageFolders)
                        {
                            foreach (string sourceRoot in allFiles
                                         .Where(item => item.SourceRootIsFolder)
                                         .Select(item => item.SourceRootPath)
                                         .Where(path => !string.IsNullOrWhiteSpace(path))
                                         .Distinct(StringComparer.OrdinalIgnoreCase))
                            {
                                if (IsDirectoryInsideSource(sourceRoot, options.EncryptOutputDirectory))
                                {
                                    result.Issues.Add(new PreflightIssue(PreflightSeverity.Error, "Choose an encrypt output folder outside the source folder before removing originals."));
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Issues.Add(new PreflightIssue(PreflightSeverity.Error, $"Custom output folder is not available: {GetFriendlyExceptionMessage(ex, "Output folder check failed.")}"));
                    }
                }
            }

            if (options != null && decryptLike && options.UseCustomDecryptOutputDirectory)
            {
                if (string.IsNullOrWhiteSpace(options.DecryptOutputDirectory))
                {
                    result.Issues.Add(new PreflightIssue(PreflightSeverity.Error, "Choose a decrypt output folder or save output next to encrypted files."));
                }
                else
                {
                    try
                    {
                        ValidateNormalOutputPath(options.DecryptOutputDirectory, allowDirectoryPath: true);
                        Directory.CreateDirectory(options.DecryptOutputDirectory);
                    }
                    catch (Exception ex)
                    {
                        result.Issues.Add(new PreflightIssue(PreflightSeverity.Error, $"Decrypt output folder is not available: {GetFriendlyExceptionMessage(ex, "Output folder check failed.")}"));
                    }
                }
            }

            if (options != null && !string.IsNullOrWhiteSpace(options.KeyfilePath) && options.KeyfileBytes is null)
            {
                result.Issues.Add(new PreflightIssue(PreflightSeverity.Error, "The selected keyfile could not be loaded."));
            }

            if (options != null && encrypt && options.PackageFolders && options.UseSteganography)
            {
                result.Issues.Add(new PreflightIssue(PreflightSeverity.Error, "PNG carrier mode is not available for folder packages."));
            }

            if (options != null && encrypt && options.PackageFolders)
            {
                foreach (string sourceRoot in allFiles
                             .Where(item => item.SourceRootIsFolder)
                             .Select(item => item.SourceRootPath)
                             .Where(path => !string.IsNullOrWhiteSpace(path))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (IsReparsePointPath(sourceRoot))
                    {
                        result.Issues.Add(new PreflightIssue(PreflightSeverity.Error, "Folder package source root cannot be a symlink or junction."));
                        break;
                    }
                }
            }

            if (options != null && encrypt && !string.IsNullOrWhiteSpace(options.RecoveryKey))
            {
                result.Issues.Add(new PreflightIssue(PreflightSeverity.Info, "New payloads will include a recovery-key slot for future password rotation."));
            }

            if (options != null && experienceLevel != UserExperienceLevel.Advanced)
            {
                var hiddenSettings = new List<string>();
                if (options.RemoveOriginalsAfterSuccess) hiddenSettings.Add("remove originals");
                if (options.SecureDeleteOriginals) hiddenSettings.Add("best-effort overwrite");
                if (options.KeyfileBytes is { Length: > 0 }) hiddenSettings.Add("keyfile");
                if (!string.IsNullOrWhiteSpace(options.RecoveryKey)) hiddenSettings.Add("recovery key");
                if (options.ScrambleNames) hiddenSettings.Add("scrambled names");
                if (options.UseSteganography) hiddenSettings.Add("PNG carrier");
                if (options.PackageFolders) hiddenSettings.Add("folder package mode");
                if (!string.IsNullOrWhiteSpace(options.BackupFolderPath)) hiddenSettings.Add("backup folder");

                if (hiddenSettings.Count > 0)
                {
                    result.Issues.Add(new PreflightIssue(
                        PreflightSeverity.Warning,
                        $"Advanced settings remain active while the advanced pane is collapsed: {string.Join(", ", hiddenSettings)}."));
                }
            }

            foreach (PreflightSnapshotItem item in allFiles)
            {
                string filePath = item.SourcePath;
                long sourceLength = 0;
                if (!File.Exists(filePath))
                {
                    result.Issues.Add(new PreflightIssue(PreflightSeverity.Error, $"Missing file: {filePath}"));
                    continue;
                }

                if (options != null &&
                    options.RemoveOriginalsAfterSuccess &&
                    options.SecureDeleteOriginals &&
                    IsReparsePointPath(filePath))
                {
                    result.Issues.Add(new PreflightIssue(PreflightSeverity.Error, $"Secure delete cannot overwrite linked file: {Path.GetFileName(filePath)}."));
                    continue;
                }

                try
                {
                    using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    sourceLength = stream.Length;
                }
                catch (Exception ex)
                {
                    result.Issues.Add(new PreflightIssue(PreflightSeverity.Error, $"Unable to read {Path.GetFileName(filePath)}: {GetFriendlyExceptionMessage(ex, "File read check failed.")}"));
                    continue;
                }

                if (encrypt)
                {
                    if (options?.UseSteganography == true && sourceLength > MaxPngCarrierSourceBytes)
                    {
                        result.Issues.Add(new PreflightIssue(PreflightSeverity.Error, $"{Path.GetFileName(filePath)} is too large for PNG carrier mode. {GetPngCarrierSizeLimitMessage()}"));
                    }

                    if (filePath.EndsWith(ENCRYPTED_EXTENSION, StringComparison.OrdinalIgnoreCase) ||
                        ContainsStegoPayload(filePath))
                    {
                        result.Issues.Add(new PreflightIssue(PreflightSeverity.Warning, $"{Path.GetFileName(filePath)} already looks encrypted."));
                    }
                }
                else if (decryptLike &&
                         !filePath.EndsWith(ENCRYPTED_EXTENSION, StringComparison.OrdinalIgnoreCase) &&
                         !ContainsStegoPayload(filePath))
                {
                    result.Issues.Add(new PreflightIssue(PreflightSeverity.Error, $"{Path.GetFileName(filePath)} does not look like a FileLocker payload."));
                }

                if (options != null)
                {
                    string predictedPath = intent switch
                    {
                        ProcessingIntent.Encrypt when options.PackageFolders && item.SourceRootIsFolder =>
                            ResolveAvailablePath(BuildFolderPackageOutputPath(item.SourceRootPath, options.ScrambleNames, options.UseCustomEncryptOutputDirectory ? options.EncryptOutputDirectory : null)),
                        ProcessingIntent.Encrypt => ResolveAvailablePath(BuildOutputPath(
                            filePath,
                            options.ScrambleNames,
                            options.UseSteganography,
                            options.UseCustomEncryptOutputDirectory ? options.EncryptOutputDirectory : null,
                            item.SourceRootPath,
                            item.SourceRootIsFolder)),
                        ProcessingIntent.Decrypt => ResolveAvailablePath(PredictDecryptedOutputPath(filePath, options)),
                        _ => string.Empty
                    };

                    result.PredictedPaths[filePath] = predictedPath;

                    if (!string.IsNullOrWhiteSpace(predictedPath) &&
                        !string.Equals(predictedPath, BuildExistingOutputPath(intent, filePath, options, item.SourceRootPath, item.SourceRootIsFolder), StringComparison.OrdinalIgnoreCase))
                    {
                        result.Issues.Add(new PreflightIssue(PreflightSeverity.Warning, $"Output will be renamed because {Path.GetFileName(predictedPath)} already exists."));
                    }
                }
            }

            if (allFiles.Count > 1)
            {
                result.Issues.Add(new PreflightIssue(PreflightSeverity.Info, $"Batch contains {allFiles.Count} file(s). You can cancel between items if needed."));
            }

            return result;
        }

        private List<PreflightIssue> BuildPreflightIssues(ProcessingIntent intent, ProcessingRunOptions? options = null)
        {
            return BuildPreflightEvaluation(CapturePreflightSnapshot(), intent, options, _currentExperienceLevel).Issues;
        }

        private void DisplayPreflightIssues(List<PreflightIssue> issues)
        {
            _preflightItems.Clear();

            if (issues.Count == 0)
            {
                _preflightItems.Add(new PreflightIssueItem
                {
                    IconGlyph = "\uE73E",
                    SeverityText = "Ready",
                    Message = "No preflight issues detected."
                });
                PreflightSummaryText.Text = "Ready to run";
                return;
            }

            int errorCount = issues.Count(issue => issue.Severity == PreflightSeverity.Error);
            int warningCount = issues.Count(issue => issue.Severity == PreflightSeverity.Warning);
            PreflightSummaryText.Text = errorCount > 0
                ? $"{errorCount} error(s), {warningCount} warning(s)"
                : warningCount > 0
                    ? $"{warningCount} warning(s)"
                    : "Informational checks only";

            foreach (var issue in issues.Take(8))
            {
                _preflightItems.Add(new PreflightIssueItem
                {
                    IconGlyph = issue.Severity switch
                    {
                        PreflightSeverity.Error => "\uEA39",
                        PreflightSeverity.Warning => "\uE7BA",
                        _ => "\uE946"
                    },
                    SeverityText = issue.Severity.ToString(),
                    Message = issue.Message
                });
            }
        }

        private void ApplyPredictedPaths(Dictionary<string, string> predictedPaths)
        {
            foreach (QueuedFileItem item in FileList)
            {
                if (predictedPaths.TryGetValue(item.SourcePath, out string? predictedPath))
                {
                    item.PredictedOutputPath = predictedPath;
                    if (string.Equals(item.Status, "Queued", StringComparison.OrdinalIgnoreCase))
                    {
                        item.DetailSummary = string.IsNullOrWhiteSpace(predictedPath)
                            ? "Ready to verify without writing output."
                            : $"Next output: {predictedPath}";
                    }
                }
            }
        }

        private void RefreshPreflightPreview()
        {
            if (_isApplyingProfile)
            {
                return;
            }

            _preflightRefreshCancellation?.Cancel();
            _preflightRefreshCancellation?.Dispose();
            _preflightRefreshCancellation = new CancellationTokenSource();
            CancellationToken token = _preflightRefreshCancellation.Token;

            List<PreflightSnapshotItem> snapshot;
            ProcessingRunOptions options;
            ProcessingIntent intent;

            try
            {
                snapshot = CapturePreflightSnapshot();
                options = CaptureProcessingRunOptions();
                string modeText = GetComboContent(OperationModeCombo) ?? string.Empty;
                intent = modeText.Contains("Hash", StringComparison.OrdinalIgnoreCase)
                    ? ProcessingIntent.Verify
                    : ProcessingIntent.Encrypt;
            }
            catch (Exception ex)
            {
                DisplayPreflightIssues([new PreflightIssue(PreflightSeverity.Error, GetFriendlyExceptionMessage(ex, "Preflight check failed."))]);
                return;
            }

            _ = RefreshPreflightPreviewAsync(snapshot, intent, options, token);
        }

        private async Task RefreshPreflightPreviewAsync(
            List<PreflightSnapshotItem> snapshot,
            ProcessingIntent intent,
            ProcessingRunOptions options,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(250, cancellationToken);
                PreflightEvaluationResult evaluation = await Task.Run(
                    () => BuildPreflightEvaluation(snapshot, intent, options, _currentExperienceLevel),
                    cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    ApplyPredictedPaths(evaluation.PredictedPaths);
                    DisplayPreflightIssues(evaluation.Issues);
                });
            }
            catch (OperationCanceledException)
            {
                // Ignore debounced refresh cancellations.
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    DisplayPreflightIssues([new PreflightIssue(PreflightSeverity.Error, GetFriendlyExceptionMessage(ex, "Preflight check failed."))]);
                });
            }
        }

        private static string PredictDecryptedOutputPath(
            string filePath,
            ProcessingRunOptions? options = null,
            string? relativeOutputDirectory = null)
        {
            string directory = options == null
                ? Path.GetDirectoryName(filePath) ?? throw new InvalidOperationException("File directory is null.")
                : ResolveDecryptOutputDirectory(filePath, options, relativeOutputDirectory);

            string fileName = BuildFallbackDecryptedFileName(filePath);

            return Path.Combine(directory, fileName);
        }

        private static string ResolveDecryptOutputDirectory(
            string encryptedFilePath,
            ProcessingRunOptions options,
            string? relativeOutputDirectory = null)
        {
            string baseDirectory = options.UseCustomDecryptOutputDirectory && !string.IsNullOrWhiteSpace(options.DecryptOutputDirectory)
                ? options.DecryptOutputDirectory.Trim()
                : Path.GetDirectoryName(encryptedFilePath) ?? throw new InvalidOperationException("File directory is null.");

            if (options.UseCustomDecryptOutputDirectory &&
                options.PreserveFolderStructure &&
                !string.IsNullOrWhiteSpace(relativeOutputDirectory))
            {
                string safeRelativeDirectory = SanitizeRelativeDirectory(relativeOutputDirectory);
                if (!string.IsNullOrWhiteSpace(safeRelativeDirectory))
                {
                    baseDirectory = Path.Combine(baseDirectory, safeRelativeDirectory);
                }
            }

            Directory.CreateDirectory(baseDirectory);
            return baseDirectory;
        }

        private static string SanitizeRelativeDirectory(string relativeDirectory)
        {
            string normalized = relativeDirectory
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .Trim(Path.DirectorySeparatorChar);

            if (string.IsNullOrWhiteSpace(normalized) || normalized == ".")
            {
                return string.Empty;
            }

            string[] segments = normalized
                .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                .Where(segment => segment != "." && segment != "..")
                .Select(SanitizeRelativeDirectorySegment)
                .Where(segment => !string.IsNullOrWhiteSpace(segment))
                .ToArray();

            if (segments.Length == 0)
            {
                return string.Empty;
            }

            return Path.Combine(segments);
        }

        private static string SanitizeRelativeDirectorySegment(string segment)
        {
            string candidate = string.Concat(segment.Select(ch =>
                Path.GetInvalidFileNameChars().Contains(ch) ||
                CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format
                    ? '_'
                    : ch)).Trim(' ', '.');

            if (candidate.Length > MaxRestoredFileNameChars)
            {
                candidate = candidate[..MaxRestoredFileNameChars].Trim(' ', '.');
            }

            return WindowsFileNameRules.IsReservedDeviceName(candidate)
                ? $"_{candidate}"
                : candidate;
        }

        internal static string ResolveDecryptedFileName(string encryptedFilePath, string? originalFileName, bool restoreOriginalFilename)
        {
            if (restoreOriginalFilename && !string.IsNullOrWhiteSpace(originalFileName))
            {
                string safeOriginalName = Path.GetFileName(originalFileName);
                if (string.Equals(safeOriginalName, originalFileName, StringComparison.Ordinal) &&
                    IsSafeRestoredFileName(safeOriginalName))
                {
                    return safeOriginalName;
                }
            }

            return BuildFallbackDecryptedFileName(encryptedFilePath);
        }

        internal static bool IsSafeRestoredFileName(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            string candidate = fileName.Trim();
            if (!string.Equals(fileName, candidate, StringComparison.Ordinal) ||
                candidate.Length > MaxRestoredFileNameChars ||
                candidate is "." or ".." ||
                candidate.Any(character => CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format) ||
                !string.Equals(candidate, candidate.TrimEnd(' ', '.'), StringComparison.Ordinal) ||
                candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return false;
            }

            return !WindowsFileNameRules.IsReservedDeviceName(candidate);
        }

        internal static string ResolveFolderPackageRootName(string encryptedFilePath, string? rootFolderName, bool restoreOriginalFilename)
        {
            if (restoreOriginalFilename && !string.IsNullOrWhiteSpace(rootFolderName))
            {
                string trimmedRootName = rootFolderName.Trim();
                if (string.Equals(rootFolderName, trimmedRootName, StringComparison.Ordinal))
                {
                    string safeRootName = Path.GetFileName(rootFolderName);
                    if (IsSafeRestoredFileName(safeRootName))
                    {
                        return safeRootName;
                    }
                }
            }

            string fallback = BuildFallbackDecryptedFileName(encryptedFilePath);
            return string.IsNullOrWhiteSpace(fallback) ? "Restored" : fallback;
        }

        private static string BuildFallbackDecryptedFileName(string encryptedFilePath)
        {
            string fileName = Path.GetFileName(encryptedFilePath);
            if (fileName.EndsWith(ENCRYPTED_EXTENSION, StringComparison.OrdinalIgnoreCase))
            {
                string stripped = fileName[..^ENCRYPTED_EXTENSION.Length];
                return string.IsNullOrWhiteSpace(stripped) ? "output" : stripped;
            }

            if (fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                string withoutExtension = Path.GetFileNameWithoutExtension(fileName);
                string stripped = withoutExtension.Replace("_secure", string.Empty, StringComparison.OrdinalIgnoreCase);
                return string.IsNullOrWhiteSpace(stripped) ? "output" : stripped;
            }

            string fallback = Path.GetFileNameWithoutExtension(fileName);
            return string.IsNullOrWhiteSpace(fallback) ? "output" : fallback;
        }

        private static string BuildExistingOutputPath(
            ProcessingIntent intent,
            string filePath,
            ProcessingRunOptions options,
            string sourceRootPath,
            bool sourceRootIsFolder)
        {
            return intent == ProcessingIntent.Encrypt
                ? options.PackageFolders && sourceRootIsFolder
                    ? BuildFolderPackageOutputPath(sourceRootPath, options.ScrambleNames, options.UseCustomEncryptOutputDirectory ? options.EncryptOutputDirectory : null)
                    : BuildOutputPath(
                        filePath,
                        options.ScrambleNames,
                        options.UseSteganography,
                        options.UseCustomEncryptOutputDirectory ? options.EncryptOutputDirectory : null,
                        sourceRootPath,
                        sourceRootIsFolder)
                : PredictDecryptedOutputPath(filePath, options);
        }

        private static string ResolveAvailablePath(string preferredPath)
        {
            ValidateNormalOutputPath(preferredPath, allowDirectoryPath: false);
            if (!File.Exists(preferredPath) && !Directory.Exists(preferredPath))
            {
                return preferredPath;
            }

            string directory = Path.GetDirectoryName(preferredPath)
                ?? throw new InvalidOperationException("File directory is null.");
            string fileName = Path.GetFileNameWithoutExtension(preferredPath);
            string extension = Path.GetExtension(preferredPath);

            for (int counter = 1; counter <= MaxResolveAvailablePathAttempts; counter++)
            {
                string candidate = Path.Combine(directory, $"{fileName}_{counter}{extension}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new IOException("Could not find an available file name near the requested path.");
        }

        internal static FileStream CreateNewOutputFileStream(string path)
        {
            ValidateNormalOutputPath(path, allowDirectoryPath: false);
            return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }

        internal static string ResolveTemporaryOutputPath(string finalPath, string suffix = ".tmp")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(suffix);

            return ResolveAvailablePath(finalPath + suffix);
        }

        private static void ValidateNormalOutputPath(string path, bool allowDirectoryPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (path.Any(character => char.IsControl(character) || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format))
            {
                throw new ArgumentException("An output path must not contain control characters or Unicode format characters.", nameof(path));
            }

            if (!allowDirectoryPath && Path.EndsInDirectorySeparator(path))
            {
                throw new ArgumentException("An output path must include a name.", nameof(path));
            }

            string trimmedPath = path.Trim();
            string name = Path.GetFileName(trimmedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("An output path must include a name.", nameof(path));
            }

            if (!Path.IsPathFullyQualified(trimmedPath))
            {
                throw new ArgumentException("An output path must be fully qualified.", nameof(path));
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(trimmedPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new ArgumentException("An output path is invalid.", nameof(path), ex);
            }

            string root = Path.GetPathRoot(fullPath) ?? string.Empty;
            string pathWithoutRoot = fullPath.Length > root.Length ? fullPath[root.Length..] : string.Empty;
            if (pathWithoutRoot.Contains(':', StringComparison.Ordinal))
            {
                throw new ArgumentException("An output path must not target an alternate data stream.", nameof(path));
            }
        }

        private static string GenerateRandomString(int length)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            return RandomNumberGenerator.GetString(chars, length);
        }

        private static byte[] CompressData(byte[] data, out bool compressed)
        {
            using var output = new MemoryStream();
            try
            {
                using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
                {
                    gzip.Write(data, 0, data.Length);
                }

                byte[] compressedBytes = output.ToArray();
                compressed = CompressionAdvisor.HasUsefulSavings(data.LongLength, compressedBytes.LongLength);
                if (compressed)
                {
                    return compressedBytes;
                }

                ClearSensitiveBuffer(compressedBytes);
                return data;
            }
            finally
            {
                ClearMemoryStreamBuffer(output);
            }
        }

        internal static byte[] DecompressData(byte[] compressedData, CancellationToken cancellationToken = default)
        {
            using var input = new MemoryStream(compressedData);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            try
            {
                CopyStreamWithProgress(gzip, output, cancellationToken, totalLength: 0, progress: null, startPercent: 0, endPercent: 100, status: "Decompressing");
                return output.ToArray();
            }
            finally
            {
                ClearMemoryStreamBuffer(output);
            }
        }

        private static byte[] BuildEncryptedPayload(byte[] salt, byte[] iv, byte[] tag, byte[] ciphertext)
        {
            byte[] payload = new byte[1 + salt.Length + iv.Length + tag.Length + ciphertext.Length];
            int offset = 0;
            payload[offset++] = LegacyPayloadFormatVersion;
            Buffer.BlockCopy(salt, 0, payload, offset, salt.Length);
            offset += salt.Length;
            Buffer.BlockCopy(iv, 0, payload, offset, iv.Length);
            offset += iv.Length;
            Buffer.BlockCopy(tag, 0, payload, offset, tag.Length);
            offset += tag.Length;
            Buffer.BlockCopy(ciphertext, 0, payload, offset, ciphertext.Length);
            return payload;
        }

        private static string BuildOutputPath(
            string filePath,
            bool scrambleNames,
            bool useSteganography,
            string? customOutputDirectory,
            string? sourceRootPath = null,
            bool sourceRootIsFolder = false)
        {
            string directory = ResolveEncryptOutputDirectory(filePath, customOutputDirectory, sourceRootPath, sourceRootIsFolder);
            string baseName = Path.GetFileName(filePath);

            if (useSteganography)
            {
                string name = scrambleNames ? GenerateRandomString(12) : Path.GetFileNameWithoutExtension(baseName) + "_secure";
                return Path.Combine(directory, name + ".png");
            }

            if (scrambleNames)
            {
                return Path.Combine(directory, GenerateRandomString(16) + ENCRYPTED_EXTENSION);
            }

            return Path.Combine(directory, baseName + ENCRYPTED_EXTENSION);
        }

        private static byte[] EmbedInPngContainer(byte[] payload)
        {
            ValidatePngCarrierPayloadSize(payload.LongLength);
            int iendIndex = FindIendChunkIndex(StegoCarrierPng);
            if (iendIndex <= 0)
            {
                throw new InvalidDataException("Invalid PNG carrier for steganography mode.");
            }

            byte[]? chunk = null;
            try
            {
                chunk = BuildCustomPngChunk(STEGO_CHUNK_TYPE, payload);
                byte[] result = new byte[StegoCarrierPng.Length + chunk.Length];
                Buffer.BlockCopy(StegoCarrierPng, 0, result, 0, iendIndex);
                Buffer.BlockCopy(chunk, 0, result, iendIndex, chunk.Length);
                Buffer.BlockCopy(StegoCarrierPng, iendIndex, result, iendIndex + chunk.Length, StegoCarrierPng.Length - iendIndex);
                return result;
            }
            finally
            {
                ClearSensitiveBuffer(chunk);
            }
        }

        internal static byte[]? TryExtractStegoPayload(string filePath)
        {
            string safePath = RequireExistingFile(filePath);
            using FileStream stream = new(safePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TryReadStegoPayload(stream, readPayload: true, out byte[]? payload) ? payload : null;
        }

        internal static bool ContainsStegoPayload(string filePath)
        {
            string safePath = RequireExistingFile(filePath);
            using FileStream stream = new(safePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TryReadStegoPayload(stream, readPayload: false, out _);
        }

        private static bool TryReadStegoPayload(Stream stream, bool readPayload, out byte[]? payload)
        {
            payload = null;
            Span<byte> signature = stackalloc byte[8];
            if (stream.Length < signature.Length || !TryReadExact(stream, signature))
            {
                return false;
            }

            if (!IsPng(signature))
            {
                return false;
            }

            Span<byte> chunkHeader = stackalloc byte[8];
            while (stream.Length - stream.Position >= chunkHeader.Length)
            {
                if (!TryReadExact(stream, chunkHeader))
                {
                    break;
                }

                int length = BinaryPrimitives.ReadInt32BigEndian(chunkHeader[..4]);
                long bytesRemaining = stream.Length - stream.Position;
                if (length < 0 || bytesRemaining < 4 || length > bytesRemaining - 4)
                {
                    break;
                }

                if (IsChunkType(chunkHeader.Slice(4, 4), STEGO_CHUNK_TYPE))
                {
                    if (length > MaxPngCarrierPayloadBytes)
                    {
                        return false;
                    }

                    if (!readPayload)
                    {
                        return true;
                    }

                    ValidatePngCarrierPayloadSize(length);
                    payload = new byte[length];
                    return TryReadExact(stream, payload);
                }

                stream.Seek(length + 4L, SeekOrigin.Current);
            }

            return false;
        }

        private static bool TryReadExact(Stream stream, Span<byte> buffer)
        {
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = stream.Read(buffer[totalRead..]);
                if (read == 0)
                {
                    return false;
                }

                totalRead += read;
            }

            return true;
        }

        private static bool IsPng(ReadOnlySpan<byte> data)
        {
            byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
            return data.Length >= signature.Length && data[..signature.Length].SequenceEqual(signature);
        }

        private static bool IsChunkType(ReadOnlySpan<byte> actual, string expected)
        {
            return actual.Length == 4 &&
                expected.Length == 4 &&
                actual[0] == (byte)expected[0] &&
                actual[1] == (byte)expected[1] &&
                actual[2] == (byte)expected[2] &&
                actual[3] == (byte)expected[3];
        }

        private static int FindIendChunkIndex(byte[] png)
        {
            int index = 8; // skip signature
            while (index + 8 <= png.Length)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(index, 4));
                string type = Encoding.ASCII.GetString(png, index + 4, 4);
                if (type == "IEND")
                {
                    return index;
                }

                index += 8 + length + 4;
            }

            return -1;
        }

        private static byte[] BuildCustomPngChunk(string type, byte[] data)
        {
            byte[]? typeBytes = null;
            byte[]? chunk = null;
            byte[]? crcInput = null;
            bool returnsChunk = false;

            try
            {
                typeBytes = Encoding.ASCII.GetBytes(type);
                chunk = new byte[4 + 4 + data.Length + 4];
                BinaryPrimitives.WriteInt32BigEndian(chunk.AsSpan(0, 4), data.Length);
                Buffer.BlockCopy(typeBytes, 0, chunk, 4, 4);
                Buffer.BlockCopy(data, 0, chunk, 8, data.Length);

                crcInput = [.. typeBytes, .. data];
                uint crcValue = ComputeCrc32(crcInput);
                BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length, 4), crcValue);

                returnsChunk = true;
                return chunk;
            }
            finally
            {
                ClearSensitiveBuffer(typeBytes);
                ClearSensitiveBuffer(crcInput);
                if (!returnsChunk)
                {
                    ClearSensitiveBuffer(chunk);
                }
            }
        }

        private static byte[] SerializeMetadata(FileMetadata metadata)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            try
            {
                writer.Write(metadata.OriginalFileName);
                writer.Write(metadata.OriginalSize);
                writer.Write(metadata.CreationTime.ToBinary());
                writer.Write(metadata.LastWriteTime.ToBinary());
                writer.Write(metadata.IsCompressed);
                writer.Write(metadata.IsSteganographyContainer);
                writer.Write(metadata.ContentHash.Length);
                writer.Write(metadata.ContentHash);
                writer.Write(metadata.Algorithm ?? string.Empty);
                writer.Write(metadata.Mode ?? string.Empty);
                writer.Write(metadata.KeySizeBits);
                writer.Write(metadata.CustomNote ?? string.Empty);
                writer.Write(metadata.MetadataLabel ?? string.Empty);
                writer.Write(metadata.LastAccessTime.ToBinary());
                writer.Write((int)metadata.OriginalAttributes);
                return stream.ToArray();
            }
            finally
            {
                ClearMemoryStreamBuffer(stream);
            }
        }

        private static FileMetadata DeserializeMetadata(byte[] data)
        {
            try
            {
                using var stream = new MemoryStream(data);
                using var reader = new BinaryReader(stream);
                var metadata = new FileMetadata
                {
                    OriginalFileName = reader.ReadString(),
                    OriginalSize = reader.ReadInt64(),
                    CreationTime = DateTime.FromBinary(reader.ReadInt64()),
                    LastWriteTime = DateTime.FromBinary(reader.ReadInt64()),
                    IsCompressed = reader.ReadBoolean()
                };

                if (stream.Position < stream.Length)
                {
                    metadata.IsSteganographyContainer = reader.ReadBoolean();
                }

                if (stream.Position < stream.Length)
                {
                    int hashLength = reader.ReadInt32();
                    if (hashLength > 0 && hashLength <= stream.Length - stream.Position)
                    {
                        metadata.ContentHash = reader.ReadBytes(hashLength);
                    }
                }

                if (TryReadString(reader, stream, out string algorithm))
                {
                    metadata.Algorithm = algorithm;
                }

                if (TryReadString(reader, stream, out string mode))
                {
                    metadata.Mode = mode;
                }

                if (stream.Position + sizeof(int) <= stream.Length)
                {
                    metadata.KeySizeBits = reader.ReadInt32();
                }

                if (TryReadString(reader, stream, out string note))
                {
                    metadata.CustomNote = note;
                }

                if (TryReadString(reader, stream, out string label))
                {
                    metadata.MetadataLabel = label;
                }

                if (stream.Position + sizeof(long) <= stream.Length)
                {
                    metadata.LastAccessTime = DateTime.FromBinary(reader.ReadInt64());
                }

                if (stream.Position + sizeof(int) <= stream.Length)
                {
                    metadata.OriginalAttributes = (System.IO.FileAttributes)reader.ReadInt32();
                }

                return metadata;
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentException or DecoderFallbackException or FormatException)
            {
                throw new InvalidDataException("Legacy payload metadata is corrupted.", ex);
            }
        }

        private static bool TryReadString(BinaryReader reader, Stream stream, out string value)
        {
            if (stream.Position < stream.Length)
            {
                int byteLength = ReadBinaryStringByteLength(reader, stream);
                if (byteLength > MaxLegacyMetadataStringBytes ||
                    byteLength > stream.Length - stream.Position)
                {
                    throw new InvalidDataException("Legacy payload metadata contains an invalid text field.");
                }

                byte[] bytes = reader.ReadBytes(byteLength);
                if (bytes.Length != byteLength)
                {
                    throw new EndOfStreamException();
                }

                value = StrictUtf8.GetString(bytes);
                return true;
            }

            value = string.Empty;
            return false;
        }

        private static int ReadBinaryStringByteLength(BinaryReader reader, Stream stream)
        {
            int count = 0;
            int shift = 0;
            while (shift < 35)
            {
                if (stream.Position >= stream.Length)
                {
                    throw new EndOfStreamException();
                }

                byte current = reader.ReadByte();
                int chunk = current & 0x7F;
                if (shift == 28 && chunk > 0x0F)
                {
                    throw new FormatException("Legacy payload metadata contains an invalid string length.");
                }

                count |= chunk << shift;
                if ((current & 0x80) == 0)
                {
                    return count;
                }

                shift += 7;
            }

            throw new FormatException("Legacy payload metadata contains an invalid string length.");
        }

        private static byte[] GenerateRandomBytes(int size)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(size);
            byte[] bytes = new byte[size];

            RandomNumberGenerator.Fill(bytes);
            return bytes;
        }

        private static byte[] DeriveArgon2idKey(string password, byte[] salt, byte[]? keyfileBytes = null)
        {
            ValidateKdfSecretTextLength(password);
            byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
            byte[]? combinedSecret = null;
            byte[] secret = KdfSecretMaterial.Build(passwordBytes, keyfileBytes, out combinedSecret);

            try
            {
                var argon2 = new Argon2id(secret)
                {
                    Salt = salt,
                    DegreeOfParallelism = KdfSettings.Argon2IdParallelism,
                    Iterations = KdfSettings.Argon2IdIterations,
                    MemorySize = KdfSettings.Argon2IdMemoryKb
                };

                return argon2.GetBytes(KEY_SIZE);
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

        private static byte[] DeriveKey(string password, byte[] salt, byte[]? keyfileBytes, int keySize)
        {
            ValidateKdfSecretTextLength(password);
            byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
            byte[]? combinedSecret = null;
            byte[] secret = KdfSecretMaterial.Build(passwordBytes, keyfileBytes, out combinedSecret);

            try
            {
                using var pbkdf2 = new Rfc2898DeriveBytes(secret, salt, KdfSettings.Pbkdf2FallbackIterations, HashAlgorithmName.SHA256);
                return pbkdf2.GetBytes(keySize);
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

        internal static void ValidateKdfSecretTextLength(string? secretText)
        {
            KdfSecretValidator.Validate(secretText, "Password or recovery key");
        }

        private static byte[] ComputeSha256(byte[] data)
        {
            return SHA256.HashData(data);
        }

        private static void EnsureHashMatch(byte[] expectedHash, byte[] data)
        {
            byte[] actualHash = ComputeSha256(data);
            if (!actualHash.SequenceEqual(expectedHash))
            {
                throw new UnauthorizedAccessException("File failed integrity validation after decryption.");
            }
        }

        internal static void SecureDelete(string filePath, int passes = 3)
        {
            ValidateSourceDeleteTargetPath(filePath);
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("File could not be found for secure delete.", filePath);
            }

            Exception? overwriteFailure = null;
            System.IO.FileAttributes? originalAttributes = null;
            try
            {
                passes = Math.Clamp(passes, 1, 35);
                originalAttributes = File.GetAttributes(filePath);
                if ((originalAttributes.Value & System.IO.FileAttributes.ReparsePoint) == System.IO.FileAttributes.ReparsePoint)
                {
                    throw new IOException("Secure delete does not overwrite file reparse points.");
                }

                FileCleanupService.ClearReadOnlyAttribute(filePath);
                var fileInfo = new FileInfo(filePath);
                long fileSize = fileInfo.Length;
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write))
                {
                    byte[] randomData = new byte[4096];
                    try
                    {
                        for (int pass = 0; pass < passes; pass++)
                        {
                            fs.Seek(0, SeekOrigin.Begin);
                            long written = 0;
                            while (written < fileSize)
                            {
                                int toWrite = (int)Math.Min(randomData.Length, fileSize - written);
                                RandomNumberGenerator.Fill(randomData.AsSpan(0, toWrite));
                                fs.Write(randomData, 0, toWrite);
                                written += toWrite;
                            }
                            fs.Flush(flushToDisk: true);
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(randomData);
                    }
                }
            }
            catch (Exception ex)
            {
                overwriteFailure = ex;
            }

            if (overwriteFailure != null)
            {
                RestoreFileAttributesBestEffort(filePath, originalAttributes);
                throw new IOException("Secure delete could not overwrite the file before removal.", overwriteFailure);
            }

            try
            {
                File.Delete(filePath);
            }
            catch (Exception ex)
            {
                RestoreFileAttributesBestEffort(filePath, originalAttributes);
                if (overwriteFailure == null)
                {
                    throw new IOException("Secure delete could not remove the file after overwriting it.", ex);
                }

                throw new IOException("Secure delete could not overwrite or remove the file.", new AggregateException(overwriteFailure, ex));
            }
        }

        private static void ValidateSourceDeleteTargetPath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) ||
                filePath.Any(character => char.IsControl(character) || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format))
            {
                throw new ArgumentException("Source file operations require a valid file path.", nameof(filePath));
            }

            string trimmedPath = filePath.Trim();
            if (!Path.IsPathFullyQualified(trimmedPath))
            {
                throw new ArgumentException("Source file operations require a fully qualified file path.", nameof(filePath));
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(trimmedPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new ArgumentException("Source file operations require a valid file path.", nameof(filePath), ex);
            }

            string fileName = Path.GetFileName(fullPath);
            string root = Path.GetPathRoot(fullPath) ?? string.Empty;
            string pathWithoutRoot = fullPath.Length > root.Length ? fullPath[root.Length..] : string.Empty;
            if (string.IsNullOrWhiteSpace(fileName) || pathWithoutRoot.Contains(':', StringComparison.Ordinal))
            {
                throw new ArgumentException("Source file operations require a normal file path.", nameof(filePath));
            }
        }

        private static void RestoreFileAttributesBestEffort(string path, System.IO.FileAttributes? attributes)
        {
            if (attributes is null || !File.Exists(path))
            {
                return;
            }

            try
            {
                File.SetAttributes(path, attributes.Value);
            }
            catch
            {
                // Preserve the original secure-delete failure for the caller.
            }
        }

        private void SetUIEnabled(bool enabled)
        {
            EncryptButton.IsEnabled = enabled;
            DecryptButton.IsEnabled = enabled;
            InspectButton.IsEnabled = enabled;
            RotateKeysButton.IsEnabled = enabled;
            PasswordBox.IsEnabled = enabled;
            ClearListButton.IsEnabled = enabled;
            DropPanel.AllowDrop = enabled;
            BrowseFilesButton.IsEnabled = enabled;
            BrowseFolderButton.IsEnabled = enabled;
            BrowseKeyfileButton.IsEnabled = enabled;
            GenerateRecoveryKeyButton.IsEnabled = enabled;
            BrowseBackupFolderButton.IsEnabled = enabled;
            SaveProfileButton.IsEnabled = enabled;
            ProfileCombo.IsEnabled = enabled;
            ExperienceModeCombo.IsEnabled = enabled;
            EncryptBrowseFilesButton.IsEnabled = enabled;
            EncryptBrowseFolderButton.IsEnabled = enabled;
            EncryptPasswordBox.IsEnabled = enabled;
            ConfirmPasswordBox.IsEnabled = enabled;
            EncryptOutputLocationBox.IsEnabled = enabled;
            EncryptOutputBrowseButton.IsEnabled = enabled;
            EncryptSaveNextToSourceToggle.IsEnabled = enabled;
            EncryptCompressBeforeEncryptionToggle.IsEnabled = enabled;
            EncryptDeleteOriginalsToggle.IsEnabled = enabled;
            EncryptPreserveFolderStructureToggle.IsEnabled = enabled;
            EncryptClearSelectionButton.IsEnabled = enabled && FileList.Count > 0;
            EncryptPanelClearSelectionButton.IsEnabled = enabled && FileList.Count > 0;
            StartEncryptionButton.IsEnabled = enabled && CanStartEncryptFiles();
            DecryptBrowseFilesButton.IsEnabled = enabled;
            DecryptBrowseFolderButton.IsEnabled = enabled;
            DecryptDropPanel.AllowDrop = enabled;
            DecryptPasswordBox.IsEnabled = enabled;
            DecryptSaveNextToEncryptedToggle.IsEnabled = enabled;
            DecryptRestoreOriginalFilenamesToggle.IsEnabled = enabled;
            DecryptPreserveFolderStructureToggle.IsEnabled = enabled;
            DecryptDeleteEncryptedAfterSuccessToggle.IsEnabled = enabled;
            DecryptOutputLocationBox.IsEnabled = enabled && !DecryptSaveNextToEncryptedToggle.IsOn;
            DecryptOutputBrowseButton.IsEnabled = enabled && !DecryptSaveNextToEncryptedToggle.IsOn;
            DecryptClearSelectionButton.IsEnabled = enabled && DecryptSelectedFiles.Count > 0;
            DecryptPanelClearSelectionButton.IsEnabled = enabled && DecryptSelectedFiles.Count > 0;
            StartDecryptionButton.IsEnabled = enabled && CanStartDecryption();
            HashBrowseFileButton.IsEnabled = enabled && !_isHashingFile;
            HashDropPanel.AllowDrop = enabled && !_isHashingFile;
            HashAlgorithmCombo.IsEnabled = enabled && !_isHashingFile;
            ExpectedHashBox.IsEnabled = enabled && !_isHashingFile;
            VerifyHashButton.IsEnabled = enabled && !_isHashingFile && CanVerifyCurrentHash();
            GenerateFileHashButton.IsEnabled = enabled && !_isHashingFile && _hashSelectedFile != null;
            ClearHashFilesButton.IsEnabled = enabled && !_isHashingFile;
            HashCopyButton.IsEnabled = enabled && CanCopyCurrentHash();
            HashInlineCopyButton.IsEnabled = enabled && CanCopyCurrentHash();
            HashSaveResultButton.IsEnabled = enabled && CanCopyCurrentHash();
            HashCopyResultsHeaderButton.IsEnabled = enabled && CanCopyCurrentHash();
        }

        private void AnimateDropPanel(bool highlight)
        {
            DropPanel.Background = highlight
                ? GetBrushResource("DropPanelActiveBrush")
                : GetBrushResource("HeroSurfaceBrush");

            DropPanel.BorderBrush = GetBrushResource("DropPanelBorderBrush");

            if (DropPanelDashBorder != null)
            {
                DropPanelDashBorder.Stroke = GetBrushResource("DropPanelBorderBrush");
            }

            DropIconTile.Background = GetBrushResource("AccentSoftBrush");
            DropIconTile.BorderBrush = highlight
                ? GetBrushResource("DropPanelBorderBrush")
                : GetBrushResource("AppBorderBrush");

            if (DropPanelScaleTransform != null)
            {
                DropPanelScaleTransform.ScaleX = 1.0;
                DropPanelScaleTransform.ScaleY = 1.0;
            }
        }

        private void AnimateDropPanelScale(double targetScale)
        {
            if (DropPanelScaleTransform == null)
            {
                return;
            }

            var storyboard = new Storyboard();
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = new Duration(TimeSpan.FromMilliseconds(160));

            var xAnimation = new DoubleAnimation
            {
                To = targetScale,
                Duration = duration,
                EasingFunction = ease,
                EnableDependentAnimation = true
            };

            var yAnimation = new DoubleAnimation
            {
                To = targetScale,
                Duration = duration,
                EasingFunction = ease,
                EnableDependentAnimation = true
            };

            Storyboard.SetTarget(xAnimation, DropPanelScaleTransform);
            Storyboard.SetTarget(yAnimation, DropPanelScaleTransform);
            Storyboard.SetTargetProperty(xAnimation, "ScaleX");
            Storyboard.SetTargetProperty(yAnimation, "ScaleY");

            storyboard.Children.Add(xAnimation);
            storyboard.Children.Add(yAnimation);
            storyboard.Begin();
        }

        private static Brush GetBrushResource(string key)
        {
            if (Application.Current.Resources.TryGetValue(key, out object? resource) &&
                resource is Brush brush)
            {
                return brush;
            }

            return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        private class FileMetadata
        {
            public string OriginalFileName { get; set; } = string.Empty;
            public long OriginalSize { get; set; }
            public DateTime CreationTime { get; set; }
            public DateTime LastWriteTime { get; set; }
            public DateTime LastAccessTime { get; set; }
            public System.IO.FileAttributes OriginalAttributes { get; set; } = System.IO.FileAttributes.Normal;
            public bool IsCompressed { get; set; }
            public bool IsSteganographyContainer { get; set; }
            public byte[] ContentHash { get; set; } = [];
            public string Algorithm { get; set; } = string.Empty;
            public string Mode { get; set; } = string.Empty;
            public int KeySizeBits { get; set; }
            public string CustomNote { get; set; } = string.Empty;
            public string MetadataLabel { get; set; } = string.Empty;
        }

    }
}

