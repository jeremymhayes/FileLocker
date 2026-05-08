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

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}
