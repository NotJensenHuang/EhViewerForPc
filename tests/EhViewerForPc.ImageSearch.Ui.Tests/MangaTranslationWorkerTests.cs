using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EhViewerForPc.Services;

namespace EhViewerForPc.ImageSearch.Ui.Tests;

public sealed class MangaTranslationWorkerTests
{
    [Fact]
    public void WorkerArgumentsAreOpaqueToTheNormalApplication()
    {
        var options = StartupOptions.Parse([MangaTranslationWorker.Invocation, "--ui-smoke", "--data-dir", "wrong"]);
        Assert.Equal(["--ui-smoke", "--data-dir", "wrong"], Assert.IsType<string[]>(options.MangaTranslationWorkerArguments));
        Assert.Null(options.DataDirectory);
        Assert.False(options.UiSmoke);
        Assert.Equal(MangaTranslationWorker.InvalidImage, MangaTranslationWorker.Run([]));
    }

    [Fact]
    public void PageIpcCarriesPixelBoundsAndOnlyOverlaysChangedTranslations()
    {
        var page = new MangaOcrResult(200, 300,
            [new MangaOcrRegion(12, 23, 30, 45, "あいう"), new MangaOcrRegion(60, 50, 10, 70, "ＡＢＣ")], MangaOcrOrt.Provider);
        var response = MangaTranslationWorker.BuildResponse(page, "Vulkan0", text => text == "あいう" ? "你好" : "abc  ");
        string json = JsonSerializer.Serialize(response, MangaTranslationJsonContext.Default.MangaTranslationResponse);
        var decoded = JsonSerializer.Deserialize(json, MangaTranslationJsonContext.Default.MangaTranslationResponse)!;

        Assert.Equal((200, 300, "Vulkan0"), (decoded.Width, decoded.Height, decoded.TranslationBackend));
        Assert.Equal(new MangaTranslationRegion("あいう", "你好", 12, 23, 30, 45), decoded.Regions[0]);
        Assert.Equal(new MangaTranslationRegion("ＡＢＣ", "", 60, 50, 10, 70), decoded.Regions[1]);
    }

    [Fact]
    public async Task ProtocolAcceptsBlankPageAndRejectsInvalidJson()
    {
        const string response = """{"schemaVersion":1,"width":3,"height":4,"ocrProvider":"DmlExecutionProvider","translationBackend":null,"regions":[]}""";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var accepted = await MangaTranslationService.RunWorkerAsync(WorkerProcess($"[Console]::WriteLine('{response}')"), 3, 4, timeout.Token);
        Assert.Empty(accepted.Regions);
        var failure = await Assert.ThrowsAsync<MangaTranslationException>(() => MangaTranslationService.RunWorkerAsync(
            WorkerProcess("[Console]::WriteLine('invalid-json')"), 3, 4, timeout.Token));
        Assert.Contains("无法使用的结果", failure.Message);
    }

    [Fact]
    public async Task TimeoutStopsTheWorkerAndCompletesPipeCleanup()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stopwatch = Stopwatch.StartNew();
        Task<MangaTranslationResponse> waiting = MangaTranslationService.RunWorkerAsync(
            WorkerProcess("Start-Sleep -Seconds 30"), 3, 4, timeout.Token);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(350));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4));
    }

    private static ProcessStartInfo WorkerProcess(string script)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        return start;
    }
}
