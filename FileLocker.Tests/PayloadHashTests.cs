using System.Security.Cryptography;
using System.Text;

namespace FileLocker.Tests;

public sealed class PayloadHashTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "FileLocker.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ComputeSha256ForFile_MatchesSha256HashData()
    {
        Directory.CreateDirectory(_rootPath);
        string filePath = Path.Combine(_rootPath, "payload-source.txt");
        byte[] contents = Encoding.UTF8.GetBytes("payload contents");
        File.WriteAllBytes(filePath, contents);

        byte[] actual = MainWindow.ComputeSha256ForFile(filePath, TestContext.Current.CancellationToken);

        Assert.Equal(SHA256.HashData(contents), actual);
    }

    [Fact]
    public void ComputeSha256ForFile_ObservesCancellation()
    {
        Directory.CreateDirectory(_rootPath);
        string filePath = Path.Combine(_rootPath, "payload-source.txt");
        File.WriteAllText(filePath, "payload contents");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => MainWindow.ComputeSha256ForFile(filePath, cancellation.Token));
    }

    [Fact]
    public void ComputeStreamHash_DoesNotReadWhenAlreadyCanceled()
    {
        using var stream = new ThrowOnReadStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => MainWindow.ComputeStreamHash(stream, cancellation.Token));
        Assert.False(stream.ReadCalled);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private sealed class ThrowOnReadStream : MemoryStream
    {
        public bool ReadCalled { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalled = true;
            throw new InvalidOperationException("Read should not be called after cancellation.");
        }
    }
}
