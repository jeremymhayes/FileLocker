namespace FileLocker.Tests;

public sealed class PayloadStreamCopyTests
{
    [Fact]
    public void CopyStreamWithProgress_DoesNotReadWhenAlreadyCanceled()
    {
        using var input = new ThrowOnReadStream();
        using var output = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            MainWindow.CopyStreamWithProgress(
                input,
                output,
                cancellation.Token,
                totalLength: 1,
                progress: null,
                startPercent: 0,
                endPercent: 100,
                status: "Copying"));

        Assert.False(input.ReadCalled);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void CopyFixedLengthStream_CopiesRequestedBytes()
    {
        byte[] inputBytes = [1, 2, 3, 4, 5];
        using var input = new MemoryStream(inputBytes);
        using var output = new MemoryStream();

        MainWindow.CopyFixedLengthStream(
            input,
            output,
            length: 3,
            TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3], output.ToArray());
        Assert.Equal(3, input.Position);
    }

    [Fact]
    public void CopyFixedLengthStream_RejectsNegativeLength()
    {
        using var input = new MemoryStream([1, 2, 3]);
        using var output = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MainWindow.CopyFixedLengthStream(
                input,
                output,
                length: -1,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CopyStreamWithProgress_ReturnsCopiedByteCount()
    {
        byte[] inputBytes = [1, 2, 3, 4, 5];
        using var input = new MemoryStream(inputBytes);
        using var output = new MemoryStream();

        long copied = MainWindow.CopyStreamWithProgress(
            input,
            output,
            TestContext.Current.CancellationToken,
            totalLength: inputBytes.Length,
            progress: null,
            startPercent: 0,
            endPercent: 100,
            status: "Copying");

        Assert.Equal(inputBytes.Length, copied);
        Assert.Equal(inputBytes, output.ToArray());
    }

    [Fact]
    public void CopyStreamWithProgress_RejectsNegativeTotalLength()
    {
        using var input = new MemoryStream([1, 2, 3]);
        using var output = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MainWindow.CopyStreamWithProgress(
                input,
                output,
                TestContext.Current.CancellationToken,
                totalLength: -1,
                progress: null,
                startPercent: 0,
                endPercent: 100,
                status: "Copying"));
    }

    [Fact]
    public void CopyFixedLengthStream_DoesNotReadWhenAlreadyCanceled()
    {
        using var input = new ThrowOnReadStream();
        using var output = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            MainWindow.CopyFixedLengthStream(
                input,
                output,
                length: 1,
                cancellation.Token));

        Assert.False(input.ReadCalled);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void SkipBytes_AdvancesByRequestedLength()
    {
        using var input = new MemoryStream([1, 2, 3, 4, 5]);

        MainWindow.SkipBytes(input, byteCount: 3, TestContext.Current.CancellationToken);

        Assert.Equal(3, input.Position);
    }

    [Fact]
    public void SkipBytes_RejectsNegativeByteCount()
    {
        using var input = new MemoryStream([1, 2, 3]);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MainWindow.SkipBytes(input, byteCount: -1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DrainBufferedPayloadPadding_RejectsNegativeByteCount()
    {
        using var input = new MemoryStream([1, 2, 3]);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MainWindow.DrainBufferedPayloadPadding(input, byteCount: -1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void SkipBytes_DoesNotReadWhenAlreadyCanceled()
    {
        using var input = new ThrowOnReadStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            MainWindow.SkipBytes(input, byteCount: 1, cancellation.Token));

        Assert.False(input.ReadCalled);
    }

    [Fact]
    public void WriteRandomPadding_WritesRequestedLength()
    {
        using var output = new MemoryStream();

        MainWindow.WriteRandomPadding(output, paddingLength: 17, TestContext.Current.CancellationToken);

        Assert.Equal(17, output.Length);
    }

    [Fact]
    public void WriteRandomPadding_RejectsNegativeLength()
    {
        using var output = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MainWindow.WriteRandomPadding(output, paddingLength: -1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void WriteRandomPadding_DoesNotWriteWhenAlreadyCanceled()
    {
        using var output = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            MainWindow.WriteRandomPadding(output, paddingLength: 17, cancellation.Token));

        Assert.Equal(0, output.Length);
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
