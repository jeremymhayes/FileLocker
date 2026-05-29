namespace FileLocker.Tests;

public sealed class EncryptOutputPathAdvisorTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SuggestForSelectedPaths_SingleFolder_ReturnsSiblingEncryptedFolder()
    {
        string sourceFolder = Directory.CreateDirectory(Path.Combine(_rootPath, "Demo Folder")).FullName;

        string? suggestedPath = EncryptOutputPathAdvisor.SuggestForSelectedPaths([sourceFolder]);

        Assert.Equal(Path.Combine(_rootPath, "Demo Folder (Encrypted)"), suggestedPath);
    }

    [Fact]
    public void SuggestForFolderRoots_MultipleFoldersWithSameParent_ReturnsSharedOutputFolder()
    {
        string parent = Directory.CreateDirectory(Path.Combine(_rootPath, "Parent")).FullName;
        string firstFolder = Directory.CreateDirectory(Path.Combine(parent, "One")).FullName;
        string secondFolder = Directory.CreateDirectory(Path.Combine(parent, "Two")).FullName;

        string? suggestedPath = EncryptOutputPathAdvisor.SuggestForFolderRoots([firstFolder, secondFolder]);

        Assert.Equal(Path.Combine(parent, "FileLocker Encrypted"), suggestedPath);
    }

    [Fact]
    public void SuggestForSelectedPaths_FilesOnly_ReturnsNull()
    {
        Directory.CreateDirectory(_rootPath);
        string filePath = Path.Combine(_rootPath, "sample.txt");
        File.WriteAllText(filePath, "demo");

        string? suggestedPath = EncryptOutputPathAdvisor.SuggestForSelectedPaths([filePath]);

        Assert.Null(suggestedPath);
    }

    [Fact]
    public void SuggestForFolderRoots_DriveRoot_ReturnsNull()
    {
        string driveRoot = Path.GetPathRoot(Environment.CurrentDirectory)
            ?? throw new InvalidOperationException("Current directory does not have a drive root.");

        string? suggestedPath = EncryptOutputPathAdvisor.SuggestForFolderRoots([driveRoot]);

        Assert.Null(suggestedPath);
    }

    [Fact]
    public void SuggestForFolderRoots_MixedDriveRootAndFolder_ReturnsNull()
    {
        string driveRoot = Path.GetPathRoot(Environment.CurrentDirectory)
            ?? throw new InvalidOperationException("Current directory does not have a drive root.");
        string sourceFolder = Directory.CreateDirectory(Path.Combine(_rootPath, "Source")).FullName;

        string? suggestedPath = EncryptOutputPathAdvisor.SuggestForFolderRoots([driveRoot, sourceFolder]);

        Assert.Null(suggestedPath);
    }

    [Fact]
    public void SuggestForFolderRoots_IgnoresMalformedRoots()
    {
        string sourceFolder = Directory.CreateDirectory(Path.Combine(_rootPath, "Source")).FullName;

        string? suggestedPath = EncryptOutputPathAdvisor.SuggestForFolderRoots([
            "C:\\Temp\\bad\0path",
            sourceFolder
        ]);

        Assert.Equal(Path.Combine(_rootPath, "Source (Encrypted)"), suggestedPath);
    }

    [Fact]
    public void SuggestForFolderRoots_IgnoresControlCharacterRoots()
    {
        string sourceFolder = Directory.CreateDirectory(Path.Combine(_rootPath, "Source")).FullName;

        string? suggestedPath = EncryptOutputPathAdvisor.SuggestForFolderRoots([
            "C:\\Temp\\bad\r\npath",
            sourceFolder
        ]);

        Assert.Equal(Path.Combine(_rootPath, "Source (Encrypted)"), suggestedPath);
    }

    [Fact]
    public void SuggestForFolderRoots_IgnoresUnicodeFormatRoots()
    {
        string sourceFolder = Directory.CreateDirectory(Path.Combine(_rootPath, "Source")).FullName;

        string? suggestedPath = EncryptOutputPathAdvisor.SuggestForFolderRoots([
            Path.Combine(_rootPath, "Bad" + "\u202E"),
            sourceFolder
        ]);

        Assert.Equal(Path.Combine(_rootPath, "Source (Encrypted)"), suggestedPath);
    }

    [Fact]
    public void SuggestForFolderRoots_IgnoresRelativeRoots()
    {
        string? suggestedPath = EncryptOutputPathAdvisor.SuggestForFolderRoots(["Source"]);

        Assert.Null(suggestedPath);
    }

    [Fact]
    public void SuggestForFolderRoots_IgnoresAlternateDataStreamRoots()
    {
        string sourceFolder = Directory.CreateDirectory(Path.Combine(_rootPath, "Source")).FullName;

        string? suggestedPath = EncryptOutputPathAdvisor.SuggestForFolderRoots([
            Path.Combine(_rootPath, "Bad:stream"),
            sourceFolder
        ]);

        Assert.Equal(Path.Combine(_rootPath, "Source (Encrypted)"), suggestedPath);
    }

    [Fact]
    public void SuggestForSelectedPaths_IgnoresRelativeFolders()
    {
        string relativeFolder = $"FileLocker-Relative-Selection-{Guid.NewGuid():N}";
        string fullFolder = Path.GetFullPath(relativeFolder);
        Directory.CreateDirectory(fullFolder);

        try
        {
            string? suggestedPath = EncryptOutputPathAdvisor.SuggestForSelectedPaths([relativeFolder]);

            Assert.Null(suggestedPath);
        }
        finally
        {
            if (Directory.Exists(fullFolder))
            {
                Directory.Delete(fullFolder, recursive: true);
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}
