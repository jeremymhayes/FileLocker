using System;
using System.Collections.Generic;

namespace FileLocker;

internal static class PayloadKinds
{
    internal const string File = "file";
    internal const string FolderPackage = "folder-package";
}

internal sealed class FilePayloadMetadata
{
    public string Kind { get; set; } = PayloadKinds.File;
    public string OriginalFileName { get; set; } = string.Empty;
    public long OriginalSize { get; set; }
    public DateTime CreationTimeUtc { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public DateTime LastAccessTimeUtc { get; set; }
    public int OriginalAttributes { get; set; }
    public bool IsCompressed { get; set; }
    public bool IsSteganographyContainer { get; set; }
    public string ContentHashBase64 { get; set; } = string.Empty;
    public string Algorithm { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public int KeySizeBits { get; set; }
    public string CustomNote { get; set; } = string.Empty;
    public string MetadataLabel { get; set; } = string.Empty;
    public long ContentPaddingLength { get; set; }
}

internal sealed class FolderPackageMetadata
{
    public string Kind { get; set; } = PayloadKinds.FolderPackage;
    public string RootFolderPath { get; set; } = string.Empty;
    public string RootFolderName { get; set; } = string.Empty;
    public string PackageLabel { get; set; } = string.Empty;
    public string PackageNote { get; set; } = string.Empty;
    public string Algorithm { get; set; } = string.Empty;
    public int KeySizeBits { get; set; }
    public long PackagePaddingLength { get; set; }
    public List<FolderPackageEntryMetadata> Entries { get; set; } = [];
}

internal sealed class FolderPackageEntryMetadata
{
    public string RelativePath { get; set; } = string.Empty;
    public long OriginalSize { get; set; }
    public DateTime CreationTimeUtc { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public DateTime LastAccessTimeUtc { get; set; }
    public int OriginalAttributes { get; set; }
    public string ContentHashBase64 { get; set; } = string.Empty;
}
