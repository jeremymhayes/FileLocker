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
            byte[]? keyfileBytes = ReadKeyfileBytesIfConfigured(keyfilePath);
            bool useDecryptPageOutputOptions = _currentSection == AppSection.DecryptFiles;

            ProcessingRunOptions rawOptions = new(
                IsCompressModeEnabled,
                IsScrambleNamesEnabled,
                IsSteganographyEnabled,
                GetComboContent(AlgorithmCombo) ?? "AES-GCM",
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
                (OutputTimestampPolicyCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "Current time",
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

            return NormalizeRunOptionsForCurrentMode(rawOptions);
        }

        private ProcessingRunOptions NormalizeRunOptionsForCurrentMode(ProcessingRunOptions options)
        {
            ProcessingRunOptions normalized = options with
            {
                Algorithm = "AES-GCM",
                KeySizeBits = 256
            };

            if (_currentExperienceLevel == UserExperienceLevel.Beginner)
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
            else if (_currentExperienceLevel == UserExperienceLevel.Intermediate)
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

            return normalized;
        }

        private static bool CanUseChunkedPayload(string sourcePath, ProcessingRunOptions options)
        {
            if (options.UseSteganography)
            {
                return false;
            }

            return File.Exists(sourcePath);
        }

        private static byte[] ComputeSha256ForFile(string filePath)
        {
            using FileStream stream = File.OpenRead(filePath);
            return SHA256.HashData(stream);
        }

        private static long CalculateSizeHidingPadding(long originalSize)
        {
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
            long nextBucket = ((originalSize + largeBucket - 1) / largeBucket) * largeBucket;
            return Math.Max(0, nextBucket - originalSize);
        }

        private static void WriteRandomPadding(Stream stream, long paddingLength, CancellationToken cancellationToken)
        {
            if (paddingLength <= 0)
            {
                return;
            }

            byte[] buffer = new byte[131072];
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

        private static void SkipBytes(Stream stream, long byteCount, CancellationToken cancellationToken)
        {
            if (byteCount <= 0)
            {
                return;
            }

            byte[] buffer = new byte[131072];
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

        private static string ComputeSha256Base64ForFile(string filePath)
        {
            return Convert.ToBase64String(ComputeSha256ForFile(filePath));
        }

        private static void CopyStreamWithProgress(
            Stream input,
            Stream output,
            CancellationToken cancellationToken,
            long totalLength,
            Action<double, string>? progress,
            double startPercent,
            double endPercent,
            string status)
        {
            byte[] buffer = new byte[131072];
            long processed = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                processed += read;
                if (totalLength > 0)
                {
                    double percent = startPercent + ((double)processed / totalLength) * (endPercent - startPercent);
                    progress?.Invoke(percent, status);
                }
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
            using FileStream input = File.OpenRead(filePath);
            using var memory = new MemoryStream();
            CopyStreamWithProgress(input, memory, cancellationToken, input.Length, progress, startPercent, endPercent, status);
            return memory.ToArray();
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
            using FileStream output = new(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var input = new MemoryStream(data, writable: false);
            CopyStreamWithProgress(input, output, cancellationToken, data.LongLength, progress, startPercent, endPercent, status);
        }

        private static bool IsPayloadV3File(string filePath)
        {
            using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
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
                    string normalizedRootPath = Path.GetFullPath(sourceRootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
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
                : $"{Path.GetFileName(folderRootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}_package";
            return Path.Combine(parentDirectory, baseName + ENCRYPTED_EXTENSION);
        }

        private static string GetRelativePathSafe(string rootFolderPath, string filePath)
        {
            string relative = Path.GetRelativePath(rootFolderPath, filePath);
            return relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
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
            if (CanUseChunkedPayload(filePath, options))
            {
                return EncryptFileAdvancedV3Core(filePath, password, options, sourceRootPath, sourceRootIsFolder, progress);
            }

            var elapsed = Stopwatch.StartNew();
            string? backupPath = null;
            string encryptedPath = string.Empty;
            string tempPath = string.Empty;

            try
            {
                progress?.Invoke(5, "Reading source");
                byte[] salt = GenerateRandomBytes(SALT_SIZE);
                byte[] iv = GenerateRandomBytes(IV_SIZE);
                byte[] key = DeriveArgon2idKey(password, salt, options.KeyfileBytes);
                byte[] fileData = ReadAllBytesWithProgress(filePath, _processingCancellation?.Token ?? CancellationToken.None, progress, 5, 30, "Reading source");
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

                byte[] dataToEncrypt = fileData;
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

                byte[] padding = GenerateRandomBytes((int)Math.Min(int.MaxValue, CalculateSizeHidingPadding(fileData.LongLength)));
                byte[] metadataBytes = SerializeMetadata(metadata);
                byte[] combined = new byte[4 + metadataBytes.Length + 4 + padding.Length + dataToEncrypt.Length];
                int offset = 0;
                Buffer.BlockCopy(BitConverter.GetBytes(metadataBytes.Length), 0, combined, offset, 4);
                offset += 4;
                Buffer.BlockCopy(metadataBytes, 0, combined, offset, metadataBytes.Length);
                offset += metadataBytes.Length;
                Buffer.BlockCopy(BitConverter.GetBytes(padding.Length), 0, combined, offset, 4);
                offset += 4;
                Buffer.BlockCopy(padding, 0, combined, offset, padding.Length);
                offset += padding.Length;
                Buffer.BlockCopy(dataToEncrypt, 0, combined, offset, dataToEncrypt.Length);

                byte[] ciphertext = new byte[combined.Length];
                byte[] tag = new byte[TAG_SIZE];
                progress?.Invoke(60, "Encrypting");
                using (var aes = new AesGcm(key, TAG_SIZE))
                {
                    aes.Encrypt(iv, combined, ciphertext, tag);
                }

                byte[] payload = BuildEncryptedPayload(salt, iv, tag, ciphertext);
                encryptedPath = ResolveAvailablePath(BuildOutputPath(
                    filePath,
                    options.ScrambleNames,
                    options.UseSteganography,
                    options.UseCustomEncryptOutputDirectory ? options.EncryptOutputDirectory : null,
                    sourceRootPath,
                    sourceRootIsFolder));
                tempPath = encryptedPath + ".tmp";
                byte[] outputBytes = options.UseSteganography ? EmbedInPngContainer(payload) : payload;

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

                File.Move(tempPath, encryptedPath, overwrite: false);
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
                    ElapsedMilliseconds = elapsed.ElapsedMilliseconds
                };
            }
            catch (Exception ex)
            {
                CleanupTemporaryFile(tempPath);
                throw new InvalidOperationException($"Encryption failed: {GetFriendlyExceptionMessage(ex, "Unknown error while encrypting.")}", ex);
            }
        }

        private FileOperationResult DecryptFileAdvanced(
            string filePath,
            string password,
            ProcessingRunOptions options,
            Action<double, string>? progress = null,
            string? relativeOutputDirectory = null)
        {
            var elapsed = Stopwatch.StartNew();
            if (IsPayloadV3File(filePath))
            {
                return DecryptFileAdvancedV3(filePath, password, options, progress, relativeOutputDirectory);
            }

            string? backupPath = null;
            string finalPath = string.Empty;
            string tempPath = string.Empty;
            try
            {
                long encryptedInputSize = new FileInfo(filePath).Length;
                progress?.Invoke(5, "Reading payload");
                (FileMetadata metadata, byte[] fileData) = UnlockFilePayload(filePath, password, options, progress);

                string directory = ResolveDecryptOutputDirectory(filePath, options, relativeOutputDirectory);
                string outputFileName = ResolveDecryptedFileName(filePath, metadata.OriginalFileName, options.RestoreOriginalFilenames);
                string originalPath = Path.Combine(directory, outputFileName);
                finalPath = ResolveAvailablePath(originalPath);
                tempPath = finalPath + ".tmp";

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

                File.Move(tempPath, finalPath, overwrite: false);
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
                    ElapsedMilliseconds = elapsed.ElapsedMilliseconds
                };
            }
            catch (Exception ex)
            {
                CleanupTemporaryFile(tempPath);
                throw new InvalidOperationException($"Decryption failed: {GetFriendlyExceptionMessage(ex, "Unknown error while decrypting.")}", ex);
            }
        }

        private FileOperationResult VerifyLockedFile(string filePath, string password, ProcessingRunOptions options, Action<double, string>? progress = null)
        {
            if (IsPayloadV3File(filePath))
            {
                return VerifyLockedFileV3(filePath, password, options, progress);
            }

            progress?.Invoke(10, "Reading payload");
            (FileMetadata metadata, byte[] fileData) = UnlockFilePayload(filePath, password, options, progress);
            progress?.Invoke(100, "Verified");
            return new FileOperationResult
            {
                SourcePath = filePath,
                OutputPath = null,
                BackupPath = null,
                Status = "Completed",
                OriginalRetained = true,
                OutputVerified = true,
                Message = $"Verified {metadata.OriginalFileName} ({FormatFileSize(fileData.LongLength)}) without writing output."
            };
        }

        private FileOperationResult EncryptFileAdvancedV3(string filePath, string password, ProcessingRunOptions options, Action<double, string>? progress = null)
        {
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

            try
            {
                var fileInfo = new FileInfo(filePath);
                byte[] contentHash = ComputeSha256ForFile(filePath);
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
                    Algorithm = options.Algorithm,
                    Mode = options.Mode,
                    KeySizeBits = options.KeySizeBits,
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

                byte[] metadataBytes = JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions);
                encryptedPath = ResolveAvailablePath(BuildOutputPath(
                    filePath,
                    options.ScrambleNames,
                    false,
                    options.UseCustomEncryptOutputDirectory ? options.EncryptOutputDirectory : null,
                    sourceRootPath,
                    sourceRootIsFolder));
                tempPath = encryptedPath + ".tmp";
                long? compressedSizeBytes = null;

                if (!string.IsNullOrWhiteSpace(options.BackupFolderPath))
                {
                    backupPath = CreateBackupCopy(filePath, options.BackupFolderPath);
                }

                using (FileStream output = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
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
                            0b0000_0001),
                        _processingCancellation?.Token ?? CancellationToken.None);
                }

                if (options.VerifyAfterWrite)
                {
                    progress?.Invoke(95, "Verifying");
                    VerifyLockedFileV3(tempPath, password, options, progress);
                }

                File.Move(tempPath, encryptedPath, overwrite: false);
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
                    ElapsedMilliseconds = elapsed.ElapsedMilliseconds
                };
            }
            catch (Exception ex)
            {
                CleanupTemporaryFile(tempPath);
                throw new InvalidOperationException($"Encryption failed: {GetFriendlyExceptionMessage(ex, "Unknown error while encrypting.")}", ex);
            }
        }

        private FileOperationResult DecryptFileAdvancedV3(
            string filePath,
            string password,
            ProcessingRunOptions options,
            Action<double, string>? progress = null,
            string? relativeOutputDirectory = null)
        {
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

                    string metadataJson = Encoding.UTF8.GetString(payload.MetadataBytes);
                    if (metadataJson.Contains($"\"Kind\":\"{PayloadKinds.FolderPackage}\"", StringComparison.Ordinal))
                    {
                        FolderPackageMetadata packageMetadata = JsonSerializer.Deserialize<FolderPackageMetadata>(payload.MetadataBytes, JsonOptions)
                            ?? throw new InvalidDataException("Folder package metadata is invalid.");
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

                        string directory = ResolveDecryptOutputDirectory(filePath, options, relativeOutputDirectory);
                        string outputFileName = ResolveDecryptedFileName(filePath, metadata.OriginalFileName, options.RestoreOriginalFilenames);
                        finalPath = ResolveAvailablePath(Path.Combine(directory, outputFileName));
                        tempPath = finalPath + ".tmp";

                        if (!string.IsNullOrWhiteSpace(options.BackupFolderPath))
                        {
                            backupPath = CreateBackupCopy(filePath, options.BackupFolderPath);
                        }

                        using (FileStream output = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            if (metadata.IsCompressed)
                            {
                                using var gzip = new GZipStream(payload.PlaintextStream, CompressionMode.Decompress, leaveOpen: true);
                                CopyStreamWithProgress(gzip, output, _processingCancellation?.Token ?? CancellationToken.None, metadata.OriginalSize, progress, 15, 90, "Decrypting");
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
                            byte[] actualHash = ComputeSha256ForFile(tempPath);
                            if (!expectedHash.AsSpan().SequenceEqual(actualHash))
                            {
                                throw new UnauthorizedAccessException("The restored file failed integrity validation.");
                            }
                        }

                        File.Move(tempPath, finalPath, overwrite: false);
                        long outputSizeBytes = new FileInfo(finalPath).Length;
                        RestoreFileMetadata(
                            finalPath,
                            new FileMetadata
                            {
                                OriginalFileName = metadata.OriginalFileName,
                                OriginalSize = metadata.OriginalSize,
                                CreationTime = metadata.CreationTimeUtc,
                                LastWriteTime = metadata.LastWriteTimeUtc,
                                LastAccessTime = metadata.LastAccessTimeUtc,
                                OriginalAttributes = (System.IO.FileAttributes)metadata.OriginalAttributes
                            });

                        result = new FileOperationResult
                        {
                            SourcePath = filePath,
                            OutputPath = finalPath,
                            BackupPath = backupPath,
                            Status = "Completed",
                            OriginalRetained = true,
                            OutputVerified = options.VerifyAfterWrite,
                            Message = "Decrypted v3 payload and retained source payload.",
                            OriginalSizeBytes = encryptedInputSize,
                            OutputSizeBytes = outputSizeBytes,
                            CompressionApplied = metadata.IsCompressed,
                            CompressionReason = metadata.IsCompressed
                                ? "Source payload was compressed before encryption."
                                : "Source payload was not compressed.",
                            ElapsedMilliseconds = elapsed.ElapsedMilliseconds
                        };
                    }
                }

                if (options.RemoveOriginalsAfterSuccess)
                {
                    DeleteSourceFile(filePath, options.SecureDeleteOriginals);
                    result.OriginalRetained = false;
                    result.Message = result.OutputPath is not null && Directory.Exists(result.OutputPath)
                        ? $"{result.Message} Source payload removed."
                        : "Decrypted v3 payload and removed source payload.";
                }

                progress?.Invoke(100, "Completed");
                return result;
            }
            catch (Exception ex)
            {
                CleanupTemporaryFile(tempPath);
                throw new InvalidOperationException($"Decryption failed: {GetFriendlyExceptionMessage(ex, "Unknown error while decrypting.")}", ex);
            }
        }

        private FileOperationResult VerifyLockedFileV3(string filePath, string password, ProcessingRunOptions options, Action<double, string>? progress = null)
        {
            using FileStream input = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using OpenPayloadResult payload = PayloadChunkedService.OpenPayload(
                input,
                new PayloadUnlockInputs(password, options.KeyfileBytes, options.RecoveryKey),
                _processingCancellation?.Token ?? CancellationToken.None);
            progress?.Invoke(10, "Inspecting");

            string metadataJson = Encoding.UTF8.GetString(payload.MetadataBytes);
            if (metadataJson.Contains($"\"Kind\":\"{PayloadKinds.FolderPackage}\"", StringComparison.Ordinal))
            {
                FolderPackageMetadata packageMetadata = JsonSerializer.Deserialize<FolderPackageMetadata>(payload.MetadataBytes, JsonOptions)
                    ?? throw new InvalidDataException("Folder package metadata is invalid.");
                VerifyFolderPackagePayloadV3(payload, packageMetadata, progress);
                progress?.Invoke(100, "Verified");
                return new FileOperationResult
                {
                    SourcePath = filePath,
                    Status = "Completed",
                    OriginalRetained = true,
                    OutputVerified = true,
                    Message = $"Verified folder package {packageMetadata.RootFolderName} with {packageMetadata.Entries.Count} entries."
                };
            }

            FilePayloadMetadata metadata = JsonSerializer.Deserialize<FilePayloadMetadata>(payload.MetadataBytes, JsonOptions)
                ?? throw new InvalidDataException("File payload metadata is invalid.");

            byte[] verifiedHash;
            if (metadata.IsCompressed)
            {
                using var gzip = new GZipStream(payload.PlaintextStream, CompressionMode.Decompress, leaveOpen: true);
                verifiedHash = ComputeStreamHash(gzip, _processingCancellation?.Token ?? CancellationToken.None, progress, 15, 95, "Verifying");
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
            return new FileOperationResult
            {
                SourcePath = filePath,
                Status = "Completed",
                OriginalRetained = true,
                OutputVerified = true,
                Message = $"Verified {metadata.OriginalFileName} ({FormatFileSize(metadata.OriginalSize)}) without writing output."
            };
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

            try
            {
                long packageOriginalSizeBytes = workItem.QueueItems.Sum(item => item.SizeBytes);
                var manifest = new FolderPackageMetadata
                {
                    RootFolderPath = rootFolderPath,
                    RootFolderName = Path.GetFileName(rootFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    PackageLabel = string.IsNullOrWhiteSpace(options.Metadata.Label)
                        ? Path.GetFileName(rootFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                        : options.Metadata.Label,
                    PackageNote = options.Metadata.Notes,
                    Algorithm = options.Algorithm,
                    KeySizeBits = options.KeySizeBits,
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
                        ContentHashBase64 = ComputeSha256Base64ForFile(queueItem.SourcePath)
                    });
                }

                byte[] metadataBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
                outputPath = ResolveAvailablePath(BuildFolderPackageOutputPath(rootFolderPath, options.ScrambleNames, options.UseCustomEncryptOutputDirectory ? options.EncryptOutputDirectory : null));
                tempPath = outputPath + ".tmp";

                if (!string.IsNullOrWhiteSpace(options.BackupFolderPath))
                {
                    backupPath = CreateBackupFolderCopy(rootFolderPath, options.BackupFolderPath);
                }

                using (FileStream output = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
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
                                string entrySourcePath = Path.Combine(rootFolderPath, entry.RelativePath);
                                WriteInt64(payloadStream, entry.OriginalSize);
                                using FileStream source = File.OpenRead(entrySourcePath);
                                CopyStreamWithProgress(
                                    source,
                                    payloadStream,
                                    cancellationToken,
                                    source.Length,
                                    (percent, status) =>
                                    {
                                        double adjustedPercent = 10 + ((processedBytes + (percent / 100.0 * entry.OriginalSize)) / totalBytes) * 70;
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
                            0b0000_0011),
                        _processingCancellation?.Token ?? CancellationToken.None);
                }

                if (options.VerifyAfterWrite)
                {
                    progress?.Invoke(95, "Verifying");
                    VerifyLockedFileV3(tempPath, password, options, progress);
                }

                File.Move(tempPath, outputPath, overwrite: false);
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
                    ElapsedMilliseconds = elapsed.ElapsedMilliseconds
                };
            }
            catch (Exception ex)
            {
                CleanupTemporaryFile(tempPath);
                throw new InvalidOperationException($"Folder package encryption failed: {GetFriendlyExceptionMessage(ex, "Unknown error while encrypting the folder package.")}", ex);
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
            var elapsed = Stopwatch.StartNew();
            string directory = ResolveDecryptOutputDirectory(filePath, options, relativeOutputDirectory);
            string packageFolderName = options.RestoreOriginalFilenames
                ? Path.GetFileName(packageMetadata.RootFolderName)
                : BuildFallbackDecryptedFileName(filePath);
            if (string.IsNullOrWhiteSpace(packageFolderName))
            {
                packageFolderName = "Restored";
            }

            string restoreRoot = ResolveAvailablePath(Path.Combine(directory, packageFolderName));

            Directory.CreateDirectory(restoreRoot);

            long totalBytes = packageMetadata.Entries.Sum(entry => entry.OriginalSize);
            long processedBytes = 0;
            foreach (FolderPackageEntryMetadata entry in packageMetadata.Entries)
            {
                string targetPath = Path.Combine(restoreRoot, entry.RelativePath);
                string? targetDirectory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                long entryLength = ReadInt64(payload.PlaintextStream);
                string finalTargetPath = ResolveAvailablePath(targetPath);
                using FileStream output = new(finalTargetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                CopyFixedLengthStream(
                    payload.PlaintextStream,
                    output,
                    entryLength,
                    _processingCancellation?.Token ?? CancellationToken.None,
                    (percent, status) =>
                    {
                        double adjustedPercent = 10 + ((processedBytes + (percent / 100.0 * entry.OriginalSize)) / totalBytes) * 80;
                        progress?.Invoke(adjustedPercent, "Decrypting package");
                    },
                    0,
                    100,
                    "Decrypting package");
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
                Message = $"Restored folder package to {restoreRoot} with {packageMetadata.Entries.Count} files.",
                OriginalSizeBytes = new FileInfo(filePath).Length,
                OutputSizeBytes = totalBytes,
                CompressionApplied = false,
                CompressionReason = "Folder package payload was not compressed.",
                ElapsedMilliseconds = elapsed.ElapsedMilliseconds
            };
        }

        private static void VerifyFolderPackagePayloadV3(OpenPayloadResult payload, FolderPackageMetadata packageMetadata, Action<double, string>? progress = null)
        {
            long totalBytes = packageMetadata.Entries.Sum(entry => entry.OriginalSize);
            long processedBytes = 0;
            foreach (FolderPackageEntryMetadata entry in packageMetadata.Entries)
            {
                long entryLength = ReadInt64(payload.PlaintextStream);
                byte[] hash = ComputeFixedLengthStreamHash(
                    payload.PlaintextStream,
                    entryLength,
                    CancellationToken.None,
                    (percent, status) =>
                    {
                        double adjustedPercent = 10 + ((processedBytes + (percent / 100.0 * entry.OriginalSize)) / totalBytes) * 85;
                        progress?.Invoke(adjustedPercent, "Verifying package");
                    },
                    0,
                    100,
                    "Verifying package");
                processedBytes += entry.OriginalSize;
                if (!string.IsNullOrWhiteSpace(entry.ContentHashBase64) &&
                    !hash.AsSpan().SequenceEqual(Convert.FromBase64String(entry.ContentHashBase64)))
                {
                    throw new UnauthorizedAccessException($"Entry failed integrity validation: {entry.RelativePath}");
                }
            }
        }

        private static byte[] ComputeStreamHash(
            Stream stream,
            CancellationToken cancellationToken,
            Action<double, string>? progress = null,
            double startPercent = 0,
            double endPercent = 100,
            string status = "Processing")
        {
            using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[131072];
            long totalLength = stream.CanSeek ? Math.Max(1, stream.Length - stream.Position) : 0;
            long processed = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
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

        private static byte[] ComputeFixedLengthStreamHash(
            Stream stream,
            long length,
            CancellationToken cancellationToken,
            Action<double, string>? progress = null,
            double startPercent = 0,
            double endPercent = 100,
            string status = "Processing")
        {
            using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[131072];
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

        private static void CopyFixedLengthStream(
            Stream input,
            Stream output,
            long length,
            CancellationToken cancellationToken,
            Action<double, string>? progress = null,
            double startPercent = 0,
            double endPercent = 100,
            string status = "Processing")
        {
            byte[] buffer = new byte[131072];
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

        private static string CreateBackupFolderCopy(string sourceFolderPath, string backupFolderPath)
        {
            Directory.CreateDirectory(backupFolderPath);
            string destinationRoot = Path.Combine(
                backupFolderPath,
                $"{Path.GetFileName(sourceFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}_{DateTime.Now:yyyyMMdd_HHmmss}");
            CopyDirectory(sourceFolderPath, destinationRoot);
            return destinationRoot;
        }

        private static void CopyDirectory(string sourceFolderPath, string destinationFolderPath)
        {
            Directory.CreateDirectory(destinationFolderPath);
            foreach (string directory in Directory.EnumerateDirectories(sourceFolderPath, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(sourceFolderPath, directory);
                Directory.CreateDirectory(Path.Combine(destinationFolderPath, relative));
            }

            foreach (string file in Directory.EnumerateFiles(sourceFolderPath, "*", SearchOption.AllDirectories))
            {
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

        private static void DeleteSourceDirectory(string directoryPath, bool secureDelete)
        {
            foreach (string file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                DeleteSourceFile(file, secureDelete);
            }

            Directory.Delete(directoryPath, recursive: true);
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
            string preferredRoot = Path.Combine(previewDirectory, metadata.RootFolderName);
            string previewRoot = ResolveAvailablePath(preferredRoot);
            bool rootWillBeRenamed = !string.Equals(preferredRoot, previewRoot, StringComparison.OrdinalIgnoreCase);

            List<FolderPackageEntryMetadata> conflictingEntries = metadata.Entries
                .Where(entry =>
                    File.Exists(Path.Combine(preferredRoot, entry.RelativePath)) ||
                    Directory.Exists(Path.Combine(preferredRoot, entry.RelativePath)))
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
            using FileStream input = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using OpenPayloadResult payload = PayloadChunkedService.OpenPayload(
                input,
                new PayloadUnlockInputs(password, options.KeyfileBytes, options.RecoveryKey),
                CancellationToken.None);

            string metadataJson = Encoding.UTF8.GetString(payload.MetadataBytes);
            if (!metadataJson.Contains($"\"Kind\":\"{PayloadKinds.FolderPackage}\"", StringComparison.Ordinal))
            {
                return null;
            }

            return JsonSerializer.Deserialize<FolderPackageMetadata>(payload.MetadataBytes, JsonOptions);
        }

        private void RotatePayloadKeys(string filePath, string newPassword, string? newRecoveryKey)
        {
            string tempPath = filePath + ".rotate.tmp";
            try
            {
                using FileStream input = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using FileStream output = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                PayloadChunkedService.RotateKeys(
                    input,
                    output,
                    new PayloadRotateInputs(
                        new PayloadUnlockInputs(PasswordBox.Password, ReadKeyfileBytesIfConfigured(KeyfilePathBox.Text), RecoveryKeyBox.Text),
                        newPassword,
                        ReadKeyfileBytesIfConfigured(KeyfilePathBox.Text),
                        newRecoveryKey));

                output.Flush();
                input.Dispose();
                File.Move(tempPath, filePath, overwrite: true);
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
            byte[] encryptedBytes = TryExtractStegoPayload(filePath) ?? ReadAllBytesWithProgress(
                filePath,
                _processingCancellation?.Token ?? CancellationToken.None,
                progress,
                5,
                35,
                "Reading payload");

            using var fs = new MemoryStream(encryptedBytes);
            byte version = (byte)fs.ReadByte();
            if (version != FORMAT_VERSION)
            {
                throw new InvalidDataException("Unsupported file format version.");
            }

            byte[] salt = new byte[SALT_SIZE];
            byte[] iv = new byte[IV_SIZE];
            byte[] tag = new byte[TAG_SIZE];
            ReadExact(fs, salt, 0, SALT_SIZE);
            ReadExact(fs, iv, 0, IV_SIZE);
            ReadExact(fs, tag, 0, TAG_SIZE);

            byte[] ciphertext = new byte[fs.Length - 1 - SALT_SIZE - IV_SIZE - TAG_SIZE];
            ReadExact(fs, ciphertext, 0, ciphertext.Length);
            byte[] key = DeriveArgon2idKey(password, salt, options.KeyfileBytes);
            byte[] plaintext = new byte[ciphertext.Length];

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

            int offset = 0;
            int metadataLength = BitConverter.ToInt32(plaintext, offset);
            offset += 4;
            byte[] metadataBytes = new byte[metadataLength];
            Buffer.BlockCopy(plaintext, offset, metadataBytes, 0, metadataLength);
            offset += metadataLength;
            FileMetadata metadata = DeserializeMetadata(metadataBytes);
            int paddingLength = BitConverter.ToInt32(plaintext, offset);
            offset += 4 + paddingLength;
            byte[] fileData = new byte[plaintext.Length - offset];
            Buffer.BlockCopy(plaintext, offset, fileData, 0, fileData.Length);
            if (metadata.IsCompressed)
            {
                fileData = DecompressData(fileData);
            }

            if (metadata.ContentHash.Length > 0)
            {
                EnsureHashMatch(metadata.ContentHash, fileData);
            }

            return (metadata, fileData);
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
            File.SetAttributes(path, System.IO.FileAttributes.Normal);

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

            File.SetAttributes(path, metadata.OriginalAttributes == 0 ? System.IO.FileAttributes.Normal : metadata.OriginalAttributes);
        }

        private void ApplyOutputTimestampPolicy(string outputPath, string sourcePath, string policy)
        {
            if (string.Equals(policy, "Preserve source timestamps", StringComparison.OrdinalIgnoreCase))
            {
                File.SetCreationTimeUtc(outputPath, File.GetCreationTimeUtc(sourcePath));
                File.SetLastWriteTimeUtc(outputPath, File.GetLastWriteTimeUtc(sourcePath));
                return;
            }

            if (string.Equals(policy, "Randomize", StringComparison.OrdinalIgnoreCase))
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

        private static string CreateBackupCopy(string sourcePath, string backupFolderPath)
        {
            Directory.CreateDirectory(backupFolderPath);
            string fileName = Path.GetFileNameWithoutExtension(sourcePath);
            string extension = Path.GetExtension(sourcePath);
            string destination = Path.Combine(
                backupFolderPath,
                $"{fileName}_{DateTime.Now:yyyyMMdd_HHmmss}{extension}");

            int counter = 1;
            while (File.Exists(destination))
            {
                destination = Path.Combine(
                    backupFolderPath,
                    $"{fileName}_{DateTime.Now:yyyyMMdd_HHmmss}_{counter}{extension}");
                counter++;
            }

            File.Copy(sourcePath, destination);
            return destination;
        }

        private static void DeleteSourceFile(string sourcePath, bool secureDelete)
        {
            if (secureDelete)
            {
                SecureDelete(sourcePath);
            }
            else
            {
                File.Delete(sourcePath);
            }
        }

        private void AppendHistory(string operation, ProcessingRunOptions options, List<FileOperationResult> results, bool cancelled)
        {
            OperationMetricsSummary metrics = OperationHistoryMetrics.Calculate(results);
            var entry = new OperationHistoryEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                TimestampUtc = DateTime.UtcNow,
                Operation = operation,
                ProfileName = options.ProfileName,
                Algorithm = options.Algorithm,
                Mode = options.Mode,
                KeySizeBits = options.KeySizeBits,
                UsedKeyfile = options.KeyfileBytes is { Length: > 0 },
                RemoveOriginalsAfterSuccess = options.RemoveOriginalsAfterSuccess,
                SecureDeleteOriginals = options.SecureDeleteOriginals,
                VerifyAfterWrite = options.VerifyAfterWrite,
                BackupFolderPath = options.BackupFolderPath,
                Cancelled = cancelled,
                SuccessCount = results.Count(result => string.Equals(result.Status, "Completed", StringComparison.OrdinalIgnoreCase)),
                FailureCount = results.Count(result => string.Equals(result.Status, "Failed", StringComparison.OrdinalIgnoreCase)),
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
                    Directory.CreateDirectory(options.BackupFolderPath);
                }
                catch (Exception ex)
                {
                    result.Issues.Add(new PreflightIssue(PreflightSeverity.Error, $"Backup folder is not available: {ex.Message}"));
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
                        Directory.CreateDirectory(options.EncryptOutputDirectory);
                    }
                    catch (Exception ex)
                    {
                        result.Issues.Add(new PreflightIssue(PreflightSeverity.Error, $"Custom output folder is not available: {ex.Message}"));
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
                        Directory.CreateDirectory(options.DecryptOutputDirectory);
                    }
                    catch (Exception ex)
                    {
                        result.Issues.Add(new PreflightIssue(PreflightSeverity.Error, $"Decrypt output folder is not available: {ex.Message}"));
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
                if (!File.Exists(filePath))
                {
                    result.Issues.Add(new PreflightIssue(PreflightSeverity.Error, $"Missing file: {filePath}"));
                    continue;
                }

                try
                {
                    using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                catch (Exception ex)
                {
                    result.Issues.Add(new PreflightIssue(PreflightSeverity.Error, $"Unable to read {Path.GetFileName(filePath)}: {ex.Message}"));
                    continue;
                }

                if (encrypt)
                {
                    if (filePath.EndsWith(ENCRYPTED_EXTENSION, StringComparison.OrdinalIgnoreCase) ||
                        TryExtractStegoPayload(filePath) != null)
                    {
                        result.Issues.Add(new PreflightIssue(PreflightSeverity.Warning, $"{Path.GetFileName(filePath)} already looks encrypted."));
                    }
                }
                else if (decryptLike &&
                         !filePath.EndsWith(ENCRYPTED_EXTENSION, StringComparison.OrdinalIgnoreCase) &&
                         TryExtractStegoPayload(filePath) == null)
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
                DisplayPreflightIssues([new PreflightIssue(PreflightSeverity.Error, ex.Message)]);
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
                    DisplayPreflightIssues([new PreflightIssue(PreflightSeverity.Error, ex.Message)]);
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
                .Select(segment => string.Concat(segment.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)))
                .Where(segment => !string.IsNullOrWhiteSpace(segment))
                .ToArray();

            if (segments.Length == 0)
            {
                return string.Empty;
            }

            return Path.Combine(segments);
        }

        private static string ResolveDecryptedFileName(string encryptedFilePath, string? originalFileName, bool restoreOriginalFilename)
        {
            if (restoreOriginalFilename && !string.IsNullOrWhiteSpace(originalFileName))
            {
                string safeOriginalName = Path.GetFileName(originalFileName.Trim());
                if (!string.IsNullOrWhiteSpace(safeOriginalName))
                {
                    return safeOriginalName;
                }
            }

            return BuildFallbackDecryptedFileName(encryptedFilePath);
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
            if (!File.Exists(preferredPath) && !Directory.Exists(preferredPath))
            {
                return preferredPath;
            }

            string directory = Path.GetDirectoryName(preferredPath)
                ?? throw new InvalidOperationException("File directory is null.");
            string fileName = Path.GetFileNameWithoutExtension(preferredPath);
            string extension = Path.GetExtension(preferredPath);

            for (int counter = 1; ; counter++)
            {
                string candidate = Path.Combine(directory, $"{fileName}_{counter}{extension}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    return candidate;
                }
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
            using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                gzip.Write(data, 0, data.Length);
            }

            byte[] compressedBytes = output.ToArray();
            compressed = CompressionAdvisor.HasUsefulSavings(data.LongLength, compressedBytes.LongLength);
            return compressed ? compressedBytes : data;
        }

        private static byte[] DecompressData(byte[] compressedData)
        {
            using var input = new MemoryStream(compressedData);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }

        private static byte[] BuildEncryptedPayload(byte[] salt, byte[] iv, byte[] tag, byte[] ciphertext)
        {
            byte[] payload = new byte[1 + salt.Length + iv.Length + tag.Length + ciphertext.Length];
            int offset = 0;
            payload[offset++] = FORMAT_VERSION;
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
            int iendIndex = FindIendChunkIndex(StegoCarrierPng);
            if (iendIndex <= 0)
            {
                throw new InvalidDataException("Invalid PNG carrier for steganography mode.");
            }

            byte[] chunk = BuildCustomPngChunk(STEGO_CHUNK_TYPE, payload);
            byte[] result = new byte[StegoCarrierPng.Length + chunk.Length];
            Buffer.BlockCopy(StegoCarrierPng, 0, result, 0, iendIndex);
            Buffer.BlockCopy(chunk, 0, result, iendIndex, chunk.Length);
            Buffer.BlockCopy(StegoCarrierPng, iendIndex, result, iendIndex + chunk.Length, StegoCarrierPng.Length - iendIndex);
            return result;
        }

        private static byte[]? TryExtractStegoPayload(string filePath)
        {
            using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] signature = new byte[8];
            if (stream.Length < signature.Length || stream.Read(signature, 0, signature.Length) != signature.Length)
            {
                return null;
            }

            if (!IsPng(signature))
            {
                return null;
            }

            int index = 8; // skip signature
            byte[] fileBytes = File.ReadAllBytes(filePath);
            while (index + 8 <= fileBytes.Length)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(fileBytes.AsSpan(index, 4));
                string type = Encoding.ASCII.GetString(fileBytes, index + 4, 4);
                int dataStart = index + 8;
                if (length < 0 || dataStart + length + 4 > fileBytes.Length)
                {
                    break;
                }
                if (type == STEGO_CHUNK_TYPE)
                {
                    byte[] payload = new byte[length];
                    Buffer.BlockCopy(fileBytes, dataStart, payload, 0, length);
                    return payload;
                }
                index = dataStart + length + 4; // move past data and CRC
            }

            return null;
        }

        private static bool IsPng(ReadOnlySpan<byte> data)
        {
            byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
            return data.Length >= signature.Length && data[..signature.Length].SequenceEqual(signature);
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
            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            byte[] chunk = new byte[4 + 4 + data.Length + 4];
            BinaryPrimitives.WriteInt32BigEndian(chunk.AsSpan(0, 4), data.Length);
            Buffer.BlockCopy(typeBytes, 0, chunk, 4, 4);
            Buffer.BlockCopy(data, 0, chunk, 8, data.Length);

            byte[] crcInput = [.. typeBytes, .. data];
            uint crcValue = ComputeCrc32(crcInput);
            byte[] crcBytes = BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(crcValue));
            Buffer.BlockCopy(crcBytes, 0, chunk, 8 + data.Length, 4);

            return chunk;
        }

        private static byte[] SerializeMetadata(FileMetadata metadata)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
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

        private static FileMetadata DeserializeMetadata(byte[] data)
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

        private static bool TryReadString(BinaryReader reader, Stream stream, out string value)
        {
            if (stream.Position < stream.Length)
            {
                value = reader.ReadString();
                return true;
            }

            value = string.Empty;
            return false;
        }

        private static byte[] GenerateRandomBytes(int size)
        {
            byte[] bytes = new byte[size];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return bytes;
        }

        private static byte[] DeriveArgon2idKey(string password, byte[] salt, byte[]? keyfileBytes = null)
        {
            byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
            byte[]? combinedSecret = null;
            byte[] secret = BuildKdfSecret(passwordBytes, keyfileBytes, out combinedSecret);

            try
            {
                var argon2 = new Argon2id(secret)
                {
                    Salt = salt,
                    DegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8),
                    Iterations = ARGON2_ITERATIONS,
                    MemorySize = ARGON2_MEMORY_SIZE_KB
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
            byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
            byte[]? combinedSecret = null;
            byte[] secret = BuildKdfSecret(passwordBytes, keyfileBytes, out combinedSecret);

            try
            {
                using var pbkdf2 = new Rfc2898DeriveBytes(secret, salt, PBKDF2_FALLBACK_ITERATIONS, HashAlgorithmName.SHA256);
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

        private static void SecureDelete(string filePath)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                long fileSize = fileInfo.Length;
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write))
                {
                    byte[] randomData = GenerateRandomBytes(4096);
                    for (int pass = 0; pass < 3; pass++)
                    {
                        fs.Seek(0, SeekOrigin.Begin);
                        long written = 0;
                        while (written < fileSize)
                        {
                            int toWrite = (int)Math.Min(randomData.Length, fileSize - written);
                            fs.Write(randomData, 0, toWrite);
                            written += toWrite;
                        }
                        fs.Flush();
                        randomData = GenerateRandomBytes(4096);
                    }
                }
                File.Delete(filePath);
            }
            catch
            {
                try { File.Delete(filePath); } catch { }
            }
        }

        private static void ReadExact(FileStream fs, byte[] buffer, int offset, int count)
        {
            int readTotal = 0;
            while (readTotal < count)
            {
                int read = fs.Read(buffer, offset + readTotal, count - readTotal);
                if (read == 0) throw new EndOfStreamException();
                readTotal += read;
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
            EncryptCardClearSelectionButton.IsEnabled = enabled && FileList.Count > 0;
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
            DecryptCardClearSelectionButton.IsEnabled = enabled && DecryptSelectedFiles.Count > 0;
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

