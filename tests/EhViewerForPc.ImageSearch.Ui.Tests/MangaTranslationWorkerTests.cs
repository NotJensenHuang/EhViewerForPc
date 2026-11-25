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
    public void ModelAndTargetLanguageReachWorkerAndSurviveJson()
    {
        MangaTranslationModelOption model = MangaTranslationModelOption.Resolve("hy-mt2-7b");
        MangaTranslationLanguageOption language = MangaTranslationLanguageOption.Resolve("en");
        var resources = new MangaTranslationBackendPaths("runtime", "models", model, "verified");
        ProcessStartInfo start = MangaTranslationWorker.CreateStartInfo(
            Path.Combine(AppContext.BaseDirectory, "page.png"), resources, language);
        Assert.Equal(new[] { "hy-mt2-7b", "en" }, start.ArgumentList.TakeLast(2));
        Assert.Equal("Hy-MT2-7B-Q4_K_M.gguf", model.FileName);
        Assert.Contains("into English", language.CreatePrompt("行こう！"));
        Assert.Contains("译为繁体中文", MangaTranslationLanguageOption.Resolve("zh-Hant").CreatePrompt("行こう！"));
        Assert.Contains("into Arabic", MangaTranslationLanguageOption.Resolve("ar").CreatePrompt("行こう！"));

        var page = new MangaOcrResult(8, 8, [], MangaOcrOrt.Provider);
        var response = MangaTranslationWorker.BuildResponse(page, null, null, model, language);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(response, MangaTranslationJsonContext.Default.MangaTranslationResponse);
        var decoded = JsonSerializer.Deserialize(json, MangaTranslationJsonContext.Default.MangaTranslationResponse)!;
        Assert.Equal(model.Id, decoded.ModelId);
        Assert.Equal(language.Id, decoded.TargetLanguage);
    }

    [Fact]
    public void OversizedDialoguePreservesItsSourceAndKeepsNeighboringTranslations()
    {
        var page = new MangaOcrResult(80, 80,
            [new(1, 1, 5, 5, "前"), new(8, 8, 5, 5, "長いセリフ"), new(16, 16, 5, 5, "後")], MangaOcrOrt.Provider);
        var response = MangaTranslationWorker.BuildResponse(page, "Vulkan0",
            text => text switch
            {
                "前" => "First",
                "後" => "Last",
                _ => throw new MangaTranslationRegionLimitException(),
            });
        Assert.Equal(new[] { "First", "", "Last" }, response.Regions.Select(region => region.TranslatedText));
        Assert.Equal("長いセリフ", response.Regions[1].SourceText);
        Assert.Throws<MangaTranslationException>(() => MangaTranslationWorker.BuildResponse(page, "Vulkan0",
            _ => throw new MangaTranslationException("GPU failure")));
    }

    private const string BlankResponse = """{"schemaVersion":1,"width":3,"height":4,"ocrProvider":"DmlExecutionProvider","translationBackend":null,"regions":[]}""";

    [Fact]
    public async Task ProtocolProcessesTwoPagesAndRejectsInvalidJson()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        // 让含 Windows 换行的一帧正好是 1024 字节，父进程仍应在进程存活时返回。
        string frame = ("0 " + BlankResponse).PadRight(1022);
        await using var connection = ConnectWorker(
            $"while ($null -ne [Console]::ReadLine()) {{ [Console]::WriteLine('{frame}') }}");
        for (int i = 0; i < 2; i++)
        {
            var accepted = await connection.TranslateAsync($"page-{i}.png", 3, 4, timeout.Token);
            Assert.Empty(accepted.Regions);
            Assert.True(connection.IsUsable);
        }

        await using var invalid = ConnectWorker("[void][Console]::ReadLine(); [Console]::WriteLine('0 invalid-json')");
        var failure = await Assert.ThrowsAsync<MangaTranslationException>(() => invalid.TranslateAsync("page.png", 3, 4, timeout.Token));
        Assert.Contains("无法使用的结果", failure.Message);
    }

    [Fact]
    public async Task AcknowledgedCancellationKeepsTheWorkerForTheNextPage()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var connection = ConnectWorker(
            "[void][Console]::ReadLine(); if ([Console]::ReadLine() -eq '') { [Console]::WriteLine('7') }; " +
            $"[void][Console]::ReadLine(); [Console]::WriteLine('0 {BlankResponse}'); [void][Console]::ReadLine()");
        using var page = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        Task<MangaTranslationResponse> waiting = connection.TranslateAsync("old-page.png", 3, 4, page.Token);
        page.CancelAfter(TimeSpan.FromMilliseconds(350));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.True(connection.IsUsable);
        Assert.Empty((await connection.TranslateAsync("new-page.png", 3, 4, timeout.Token)).Regions);
    }

    [Fact]
    public async Task UnacknowledgedCancellationStopsTheWorkerAndCompletesPipeCleanup()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stopwatch = Stopwatch.StartNew();
        await using var connection = ConnectWorker("[void][Console]::ReadLine(); Start-Sleep -Seconds 30");
        Task<MangaTranslationResponse> waiting = connection.TranslateAsync("page.png", 3, 4, timeout.Token);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(350));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken));
        Assert.False(connection.IsUsable);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4));
    }

    private static MangaTranslationConnection ConnectWorker(string script)
    {
        var model = MangaTranslationModelOption.Choices[0];
        var language = MangaTranslationLanguageOption.Choices[0];
        return new MangaTranslationConnection(WorkerProcess(script),
            new MangaTranslationBackendPaths("runtime", "models", model, "verified"), language);
    }

    private static ProcessStartInfo WorkerProcess(string script)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            StandardInputEncoding = MangaTranslationWorker.PipeEncoding,
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
