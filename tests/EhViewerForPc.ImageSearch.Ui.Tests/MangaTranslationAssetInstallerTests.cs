using System.Net;
using EhViewerForPc.Services;

namespace EhViewerForPc.ImageSearch.Ui.Tests;

public sealed class MangaTranslationAssetInstallerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportsCurrentModelAndTerminatesAStalledRequest(bool receivedOneByte)
    {
        using var directory = new TestDirectory();
        using var handler = new DownloadHandler(receivedOneByte);
        using var client = new HttpClient(handler);
        var progress = new DownloadProgress();
        using var installer = new MangaTranslationAssetInstaller(null, progress, client, TimeSpan.FromMilliseconds(200));
        CancellationToken token = TestContext.Current.CancellationToken;

        MangaTranslationException error = await Assert.ThrowsAsync<MangaTranslationException>(() =>
            installer.InstallAsync(directory.Path, token).WaitAsync(TimeSpan.FromSeconds(4), token));

        Assert.Contains("det.onnx", error.Message);
        Assert.Contains("数据超时", error.Message);
        Assert.Contains(progress.Updates, update => update.Stage == MangaTranslationInstallStage.Connecting &&
            update.FileName == "det.onnx" && update.SourceHost == "www.modelscope.cn" && update.Fraction == -1);
        Assert.Equal(receivedOneByte, progress.Updates.Any(update => update.Stage == MangaTranslationInstallStage.Downloading &&
            update.FileName == "det.onnx" && update.BytesReceived == 1 && update.TotalBytes > 1 && update.Fraction > 0));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task CancelDuringDownloadLeavesNoPartialModel()
    {
        using var directory = new TestDirectory();
        using var handler = new DownloadHandler(true);
        using var client = new HttpClient(handler);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var progress = new DownloadProgress(update =>
        {
            if (update.Stage == MangaTranslationInstallStage.Downloading) stop.Cancel();
        });
        using var installer = new MangaTranslationAssetInstaller(null, progress, client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            installer.InstallAsync(directory.Path, stop.Token).WaitAsync(TimeSpan.FromSeconds(4)));

        Assert.DoesNotContain(progress.Updates, update => update.Stage == MangaTranslationInstallStage.Retrying);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Join(AppContext.BaseDirectory,
            "model-install-test-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class DownloadProgress(Action<MangaTranslationInstallProgress>? onUpdate = null)
        : IProgress<MangaTranslationInstallProgress>
    {
        public List<MangaTranslationInstallProgress> Updates { get; } = [];

        public void Report(MangaTranslationInstallProgress update)
        {
            Updates.Add(update);
            onUpdate?.Invoke(update);
        }
    }

    private sealed class DownloadHandler(bool receivedOneByte) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (!receivedOneByte) await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) };
        }
    }

    private sealed class StalledStream : Stream
    {
        private bool _readFirstByte;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_readFirstByte) await Task.Delay(Timeout.Infinite, token);
            _readFirstByte = true;
            buffer.Span[0] = 0;
            return 1;
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
