namespace FileLocker.Tests;

public sealed class ExplorerIntegrationServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void NormalizeExecutablePath_ReturnsFullyQualifiedExistingPath()
    {
        string executablePath = CreateExecutable("FileLocker.exe");

        string normalized = ExplorerIntegrationService.NormalizeExecutablePath(executablePath);

        Assert.Equal(Path.GetFullPath(executablePath), normalized);
    }

    [Fact]
    public void NormalizeExecutablePath_RejectsDriveRelativePath()
    {
        string root = Path.GetPathRoot(Environment.CurrentDirectory)
            ?? throw new InvalidOperationException("Current directory does not have a drive root.");
        string driveRelativePath = $"{root[0]}:FileLocker.exe";

        Assert.Throws<ArgumentException>(() => ExplorerIntegrationService.NormalizeExecutablePath(driveRelativePath));
    }

    [Fact]
    public void NormalizeExecutablePath_RejectsUnicodeFormatCharacterPath()
    {
        string path = Path.Combine(_rootPath, "FileLocker" + "\u202E" + ".exe");

        Assert.Throws<ArgumentException>(() => ExplorerIntegrationService.NormalizeExecutablePath(path));
    }

    [Fact]
    public void NormalizeExecutablePath_RejectsAlternateDataStreamPath()
    {
        string root = Path.GetPathRoot(Environment.CurrentDirectory)
            ?? throw new InvalidOperationException("Current directory does not have a drive root.");
        string path = Path.Combine(root, "FileLocker.Tests:ads", "FileLocker.exe");

        Assert.Throws<ArgumentException>(() => ExplorerIntegrationService.NormalizeExecutablePath(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private string CreateExecutable(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        string executablePath = Path.Combine(_rootPath, fileName);
        File.WriteAllText(executablePath, "test executable placeholder");
        return executablePath;
    }
}
