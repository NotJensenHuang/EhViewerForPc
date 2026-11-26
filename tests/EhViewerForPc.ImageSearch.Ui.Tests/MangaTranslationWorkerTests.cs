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
        Assert.Equal(MangaTranslationWorker.ResourcesUnavailable, MangaTranslationWorker.Run(["page.png", "models", "native"]));
        Assert.Equal(MangaTranslationWorker.ResourcesUnavailable,
            MangaTranslationWorker.Run(["page.png", "models", "native", "hy-mt2-7b", "zh-CN"]));
    }

    [Fact]
    public void CandidateCatalogRejectsUnsupportedDirectionAndKeepsGatedWeightsEvaluationOnly()
    {
        var english = MangaTranslationLanguageOption.Resolve("en");
        var chinese = MangaTranslationLanguageOption.Resolve("zh-CN");
        Assert.All(MangaTranslationModelOption.Choices, model =>
        {
            Assert.True(model.Supports(chinese));
            Assert.False(model.Supports(english));
            Assert.Equal(MangaTranslationWorker.ResourcesUnavailable,
                MangaTranslationWorker.Run(["--session", "model-dir", "native-dir", model.Id, "en"]));
        });
        foreach (string id in new[] { "qwen3-4b-ja2zh-eval", "qwen3-8b-ja2zh-eval" })
        {
            Assert.Null(MangaTranslationModelOption.Find(id));
            Assert.NotNull(MangaTranslationModelOption.FindForEvaluation(id));
        }
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
        MangaTranslationRegion translated = decoded.Regions[0];
        Assert.Equal((1, "あいう", "你好", 12, 23, 30, 45),
            (translated.Id, translated.SourceText, translated.TranslatedText, translated.X, translated.Y, translated.Width, translated.Height));
        MangaTranslationRegion unchanged = decoded.Regions[1];
        Assert.Equal((2, "ＡＢＣ", "", MangaTranslationRegionReview.Unchanged, "abc"),
            (unchanged.Id, unchanged.SourceText, unchanged.TranslatedText, unchanged.Review, unchanged.RejectedOutput));
    }

    [Fact]
    public void ModelAndTargetLanguageReachWorkerAndSurviveJson()
    {
        var model = TestModel;
        MangaTranslationLanguageOption language = MangaTranslationLanguageOption.Resolve("en");
        var resources = new MangaTranslationBackendPaths("runtime", "models", model, "verified");
        ProcessStartInfo start = MangaTranslationWorker.CreateStartInfo(
            Path.Combine(AppContext.BaseDirectory, "page.png"), resources, language);
        Assert.Equal(new[] { "test-model", "en" }, start.ArgumentList.TakeLast(2));
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
        Assert.Equal(MangaTranslationRegionReview.EmptyOrOverLimit, response.Regions[1].Review);
        Assert.Throws<MangaTranslationException>(() => MangaTranslationWorker.BuildResponse(page, "Vulkan0",
            _ => throw new MangaTranslationException("GPU failure")));
    }

    [Fact]
    public void PageWithOnlyLowConfidenceOcrRetainsCoordinatesAndOriginalReadingThroughIpc()
    {
        var observation = new MangaOcrObservation(1, [new(5, 6), new(25, 6), new(25, 40), new(5, 40)],
            0.90f, "サ", 0.21f, "ザ", "ザ", 0.33f, MangaOcrDirection.Vertical, MangaOcrRegionType.Unclassified, true)
        {
            OriginalDetectionIds = [1, 2],
        };
        var page = new MangaOcrResult(80, 80, [], MangaOcrOrt.Provider)
        {
            RawDetections =
            [
                new(1, [new(5, 6), new(25, 6), new(25, 30), new(5, 30)], 0.92f),
                new(2, [new(10, 35), new(15, 35), new(15, 40), new(10, 40)], 0.90f),
            ],
            Observations = [observation],
        };
        var response = MangaTranslationWorker.BuildResponse(page, null, null);
        string json = JsonSerializer.Serialize(response, MangaTranslationJsonContext.Default.MangaTranslationResponse);
        var decoded = MangaTranslationService.ParseResponse(json, 80, 80);

        Assert.Empty(decoded.Regions);
        Assert.Null(decoded.TranslationBackend);
        var decodedObservation = Assert.Single(decoded.OcrObservations);
        Assert.Equal(observation.Polygon, decodedObservation.Polygon);
        Assert.Equal(new[] { 1, 2 }, decodedObservation.OriginalDetectionIds);
        Assert.Equal(observation with
        {
            Polygon = decodedObservation.Polygon,
            OriginalDetectionIds = decodedObservation.OriginalDetectionIds,
        }, decodedObservation);
        Assert.Equal(2, decoded.RawDetections.Length);
        Assert.Equal(page.RawDetections[1].Polygon, decoded.RawDetections[1].Polygon);
        Assert.Throws<MangaTranslationException>(() => MangaTranslationService.ParseResponse(
            json.Replace("[1,2]", "[1,3]", StringComparison.Ordinal), 80, 80));
        Assert.Throws<MangaTranslationException>(() => MangaTranslationService.ParseResponse(
            json.Replace("""{"x":5,"y":6}""", """{"x":80,"y":6}"""), 80, 80));
    }

    [Fact]
    public void AmbiguousOutputsMarkEveryRegionAndKanaOnlyResidueDoesNotOverlay()
    {
        var page = new MangaOcrResult(100, 100,
            [new(1, 1, 5, 5, "賛成"), new(10, 10, 5, 5, "賛成"), new(20, 20, 5, 5, "反対"),
                new(30, 30, 5, 5, "ここはどこ？")], MangaOcrOrt.Provider);
        var result = MangaTranslationWorker.BuildResponse(page, "Vulkan0", line => line == "ここはどこ？" ? "わからない" : "同意");
        Assert.All(result.Regions.Take(3), region => Assert.Equal(MangaTranslationRegionReview.Duplicate, region.Review));
        Assert.Equal(MangaTranslationRegionReview.SourceLanguageResidue, result.Regions[3].Review);
        Assert.Equal("", result.Regions[3].TranslatedText);
        Assert.Equal("わからない", result.Regions[3].RejectedOutput);
        Assert.Equal(MangaTranslationRegionReview.EmptyOrOverLimit, MangaTranslationWorker.ReviewOutput("原文", "  "));
        Assert.Equal(MangaTranslationRegionReview.Unchanged, MangaTranslationWorker.ReviewOutput("ＡＢＣ", "abc"));
        Assert.Equal(MangaTranslationRegionReview.SymbolOnly, MangaTranslationWorker.ReviewOutput("ガタンッ！", "！！！！"));
        Assert.Equal(MangaTranslationRegionReview.None, MangaTranslationWorker.ReviewOutput("…", "……"));
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
    public async Task ScopedRequestsSendAReaderIdAndPageWithoutChangingTheSnapshotPath()
    {
        Guid reader = Guid.Parse("147643f0-34dd-485e-84f8-7a640bee2a99");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var connection = ConnectWorker(
            $"$expected = 'page.png' + [char]9 + '{reader:N}' + [char]9 + '12' + [char]9 + '0'; " +
            $"if ([Console]::ReadLine() -eq $expected) {{ [Console]::WriteLine('0 {BlankResponse}') }} " +
            "else { [Console]::WriteLine('4') }");
        var result = await connection.TranslateAsync("page.png", 3, 4, timeout.Token, new(reader, 12));
        Assert.Empty(result.Regions);
    }

    [Fact]
    public async Task PriorPageMustBeDeliveredInTheSameReaderAndRemainUncanceled()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        Guid reader = Guid.Parse("147643f0-34dd-485e-84f8-7a640bee2a99");
        Guid other = Guid.Parse("0bbb4e70-4a5c-4965-9ff1-1cb2c74ab19f");
        await using var connection = ConnectWorker(
            "$expected = @(0, 1, 0, 0); foreach ($flag in $expected) { " +
            "if ([Console]::ReadLine().Split([char]9)[-1] -ne $flag) { [Console]::WriteLine('4'); break } " +
            $"[Console]::WriteLine('0 {BlankResponse}') }}");
        await connection.TranslateAsync("first.png", 3, 4, timeout.Token, new(reader, 1));
        using var second = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await connection.TranslateAsync("second.png", 3, 4, second.Token, new(reader, 2));
        await second.CancelAsync();
        await connection.TranslateAsync("third.png", 3, 4, timeout.Token, new(reader, 3));
        await connection.TranslateAsync("other.png", 3, 4, timeout.Token, new(other, 4));
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

    private static MangaTranslationModelOption TestModel { get; } = new("test-model", "Test model", "test.gguf", 1);

    private static MangaTranslationConnection ConnectWorker(string script)
    {
        var model = TestModel;
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
