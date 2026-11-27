using System.Text;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using EhViewerForPc.Controls;
using EhViewerForPc.Services;

namespace EhViewerForPc.ViewModels;

public sealed partial class ReaderViewModel
{
    private readonly Func<ReaderTranslationImage, CancellationToken, Task<MangaTranslationResponse>> _translatePage;
    private readonly Func<bool> _hasTranslationModel;
    private readonly Guid _translationReader = Guid.NewGuid();
    private CancellationTokenSource? _translationJob;
    private ReaderTranslationImage? _attemptedTranslationImage;
    private bool _automaticTranslationJob;
    private bool _automaticTranslationStatus;
    private bool _isTranslationOpen;
    private bool _isTranslationRunning;
    private bool _showTranslationSettings;
    private bool _showTranslatedImage;
    private MangaTranslationOverlay? _translationOverlay;
    private string _translationStatus = "";
    private string _translationText = "";

    internal event EventHandler? AutoTranslationRequested;

    public bool AutoTranslateReaderPages
    {
        get => _services.Settings.AutoTranslateReaderPages;
        set => _services.SaveReaderAutoTranslation(value);
    }

    internal bool CanScheduleAutoTranslation => AutoTranslateReaderPages && _hasTranslationModel();

    public string AutomaticTranslationStatus => _automaticTranslationStatus && !HasTranslationText ? TranslationStatus : "";
    public bool ShowAutomaticTranslationSettings => _automaticTranslationStatus && ShowTranslationSettings;

    public bool CanTranslateCurrentPage => !_disposed && HasPages && !IsCurrentPageMissing && !IsTranslationRunning &&
        (!_isRetrying || _retryPageNumber != CurrentPageNumber);

    public bool IsTranslationOpen
    {
        get => _isTranslationOpen;
        private set => SetProperty(ref _isTranslationOpen, value);
    }

    public bool IsTranslationRunning
    {
        get => _isTranslationRunning;
        private set
        {
            if (SetProperty(ref _isTranslationRunning, value)) OnPropertyChanged(nameof(CanTranslateCurrentPage));
        }
    }

    public string TranslationStatus
    {
        get => _translationStatus;
        private set
        {
            if (SetProperty(ref _translationStatus, value)) OnPropertyChanged(nameof(AutomaticTranslationStatus));
        }
    }

    public string TranslationText
    {
        get => _translationText;
        private set
        {
            if (!SetProperty(ref _translationText, value)) return;
            OnPropertyChanged(nameof(HasTranslationText));
            OnPropertyChanged(nameof(AutomaticTranslationStatus));
        }
    }

    public bool HasTranslationText => TranslationText.Length > 0;
    public bool HasImageTranslation => _translationOverlay is not null;
    public string TranslationImageButtonText => _showTranslatedImage ? "显示原图" : "显示译文";
    public MangaTranslationOverlay? ActiveTranslationOverlay => _showTranslatedImage ? _translationOverlay : null;

    public bool ShowTranslationSettings
    {
        get => _showTranslationSettings;
        private set
        {
            if (SetProperty(ref _showTranslationSettings, value)) OnPropertyChanged(nameof(ShowAutomaticTranslationSettings));
        }
    }

    private Task<MangaTranslationResponse> TranslateWithReaderContextAsync(ReaderTranslationImage image, CancellationToken token) =>
        _services.MangaTranslation.TranslateAsync(image, token, new MangaTranslationPageContext(_translationReader, CurrentPageNumber));

    internal Task TranslateCurrentPageAsync(ReaderTranslationImage? image)
    {
        if (image is { } current && _attemptedTranslationImage == current && HasTranslationText)
        {
            OpenTranslationText();
            return Task.CompletedTask;
        }

        return TranslatePageAsync(image, currentImage: null);
    }

    internal Task TranslatePageAutomaticallyAsync(ReaderTranslationImage image, Func<ReaderTranslationImage?> currentImage)
    {
        if (!CanScheduleAutoTranslation || image.IsAnimated || _attemptedTranslationImage == image)
            return Task.CompletedTask;
        if (_automaticTranslationJob) CancelAutomaticTranslation();
        return TranslatePageAsync(image, currentImage);
    }

    private async Task TranslatePageAsync(ReaderTranslationImage? image, Func<ReaderTranslationImage?>? currentImage)
    {
        if (!CanTranslateCurrentPage) return;

        ResetTranslationOverlay();
        ShowTranslationSettings = false;
        bool automatic = currentImage is not null;
        SetAutomaticTranslationStatus(automatic);
        IsTranslationOpen = !automatic;
        TranslationText = "";
        if (image is null)
        {
            TranslationStatus = "请在本页图片加载完成后重试。";
            return;
        }

        if (image.Value.IsAnimated)
        {
            TranslationStatus = "本地翻译目前支持静态图片。";
            return;
        }

        using var job = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _attemptedTranslationImage = image;
        _automaticTranslationJob = automatic;
        _translationJob = job;
        IsTranslationRunning = true;
        TranslationStatus = "正在本机识别并翻译…";
        try
        {
            MangaTranslationResponse result = await _translatePage(image.Value, job.Token);
            if (_translationJob != job) return;
            if (job.IsCancellationRequested)
            {
                TranslationStatus = "已取消翻译。";
                return;
            }

            if (currentImage is not null && currentImage() != image)
            {
                _attemptedTranslationImage = null;
                TranslationStatus = "";
                return;
            }

            int reviewCount = result.OcrObservations.Count(static observation => observation.RequiresReview);
            if (result.Regions.Length == 0 && reviewCount == 0)
            {
                TranslationStatus = "本页未识别到文字。";
                return;
            }

            var text = new StringBuilder();
            var overlays = new List<MangaTranslationOverlayRegion>(result.Regions.Length);
            int translatedCount = 0;
            var coveredObservations = new HashSet<int>();
            var coveredDetections = new HashSet<int>();
            for (int i = 0; i < result.Regions.Length; i++)
            {
                if (i != 0) text.AppendLine().AppendLine();
                MangaTranslationRegion region = result.Regions[i];
                text.Append(i + 1).Append("  ").AppendLine(region.SourceText);
                if (string.IsNullOrWhiteSpace(region.TranslatedText))
                {
                    text.Append(region.Review switch
                    {
                        MangaTranslationRegionReview.Unchanged => "译文与原文相同",
                        MangaTranslationRegionReview.SourceLanguageResidue => "输出疑似仍为日语，待复核",
                        MangaTranslationRegionReview.SymbolOnly => "输出只有标点或符号，待复核",
                        _ => "未生成译文，待复核",
                    });
                    if (region.RejectedOutput is { Length: > 0 } draft && region.Review != MangaTranslationRegionReview.Unchanged)
                        text.AppendLine().Append("模型草稿 ").Append(draft);
                    continue;
                }

                text.Append(region.TranslatedText);
                if (region.Review == MangaTranslationRegionReview.Duplicate)
                {
                    text.AppendLine().Append("疑似重复译文，待复核");
                    continue;
                }
                translatedCount++;
                if (region.Width > 0 && region.Height > 0)
                {
                    Rect? safeTextBounds = GetOverlaySpace(result, region, coveredObservations, coveredDetections,
                        out Rect? balancedBounds, out Rect[]? sourceMasks);
                    overlays.Add(new MangaTranslationOverlayRegion(
                        new Rect(region.X, region.Y, region.Width, region.Height), region.TranslatedText)
                    {
                        SafeTextBounds = safeTextBounds,
                        BalancedTextBounds = balancedBounds,
                        SourceMasks = sourceMasks,
                    });
                }
            }

            foreach (MangaOcrObservation observation in result.OcrObservations)
            {
                if (!observation.RequiresReview) continue;
                if (text.Length > 0) text.AppendLine().AppendLine();
                MangaOcrPoint point = observation.Polygon[0];
                text.Append("待核对 #").Append(observation.Id)
                    .Append(" · 位置 ").Append((int)point.X).Append(", ").Append((int)point.Y)
                    .Append(" · 置信度 ").AppendLine(observation.RecognitionConfidence.ToString("P0"));
                text.Append(string.IsNullOrWhiteSpace(observation.FinalText) ? "未识别出文字" : observation.FinalText);
                if (observation.CorrectedText is not null)
                    text.AppendLine().Append("首次识别 ").Append(observation.OriginalText);
            }

            TranslationText = text.ToString();
            if (overlays.Count > 0)
            {
                TranslationStatus = reviewCount > 0
                    ? $"已翻译 {overlays.Count} 个区块，{reviewCount} 个识别区块待核对。泡内排不下的译文会尝试贴着气泡向外排。"
                    : $"已翻译 {overlays.Count} 个区块。泡内排不下的译文会尝试贴着气泡向外排，完整译文见双语文本。";
                Rect[] protectedBounds = result.OcrObservations
                    .Where(observation => !coveredObservations.Contains(observation.Id))
                    .Select(observation => BoundsOf(observation.Polygon))
                    .Concat(result.RawDetections.Where(detection => !coveredDetections.Contains(detection.Id))
                        .Select(detection => BoundsOf(detection.Polygon)))
                    .ToArray();
                _translationOverlay = new MangaTranslationOverlay(result.Width, result.Height, overlays, result.TargetLanguage)
                {
                    ImageSource = image.Value,
                    ProtectedImageBounds = protectedBounds,
                };
                _showTranslatedImage = true;
                IsTranslationOpen = reviewCount > 0 && !automatic;
                PublishTranslationOverlay();
            }
            else
            {
                TranslationStatus = reviewCount > 0
                    ? $"有 {reviewCount} 个识别区块待核对。"
                    : translatedCount > 0
                    ? $"已翻译 {translatedCount} 个区块"
                    : "本页未生成可显示的译文，可核对下方的识别结果。";
            }
        }
        catch (OperationCanceledException) when (job.IsCancellationRequested)
        {
            if (_translationJob == job) TranslationStatus = "已取消翻译。";
        }
        catch (MangaTranslationException ex)
        {
            if (_translationJob == job)
            {
                TranslationStatus = ex.Message;
                ShowTranslationSettings = ex.RequiresSetup;
            }
        }
        catch (Exception ex)
        {
            _services.Log.Warn("漫画翻译失败：" + ex.GetType().Name);
            if (_translationJob == job) TranslationStatus = "本页翻译失败，请重新加载图片或检查本地翻译后端。";
        }
        finally
        {
            if (_translationJob == job)
            {
                _translationJob = null;
                _automaticTranslationJob = false;
                IsTranslationRunning = false;
                AutoTranslationRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    [RelayCommand]
    private void CancelTranslation()
    {
        if (_translationJob is null) return;
        TranslationStatus = "正在取消翻译…";
        _translationJob.Cancel();
    }

    [RelayCommand]
    private void ToggleTranslationImage()
    {
        if (!HasImageTranslation) return;
        _showTranslatedImage = !_showTranslatedImage;
        PublishTranslationOverlay();
    }

    [RelayCommand]
    private void OpenTranslationText()
    {
        if (HasTranslationText) IsTranslationOpen = true;
    }

    [RelayCommand]
    internal void CloseTranslationPanel()
    {
        if (HasTranslationText) IsTranslationOpen = false;
        else
        {
            var attemptedImage = _attemptedTranslationImage;
            CloseTranslation();
            _attemptedTranslationImage = attemptedImage;
        }
    }

    private void OnTranslationPreferencesChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ResetTranslationPreferences);
            return;
        }

        ResetTranslationPreferences();
    }

    private void ResetTranslationPreferences()
    {
        if (_disposed) return;
        CloseTranslation();
        AutoTranslationRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnReaderAutoTranslationChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(AutoTranslateReaderPages));
        if (!AutoTranslateReaderPages)
        {
            CancelAutomaticTranslation();
            SetAutomaticTranslationStatus(false);
        }
        else if (!HasTranslationText)
        {
            _attemptedTranslationImage = null;
        }

        AutoTranslationRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SetAutomaticTranslationStatus(bool value)
    {
        if (_automaticTranslationStatus == value) return;
        _automaticTranslationStatus = value;
        OnPropertyChanged(nameof(AutomaticTranslationStatus));
        OnPropertyChanged(nameof(ShowAutomaticTranslationSettings));
    }

    internal void CancelAutomaticTranslation()
    {
        if (_automaticTranslationJob) CloseTranslation();
    }

    internal void CloseTranslation()
    {
        var job = _translationJob;
        _translationJob = null;
        _automaticTranslationJob = false;
        _attemptedTranslationImage = null;
        job?.Cancel();
        IsTranslationRunning = false;
        SetAutomaticTranslationStatus(false);
        IsTranslationOpen = false;
        TranslationStatus = "";
        TranslationText = "";
        ShowTranslationSettings = false;
        ResetTranslationOverlay();
    }

    private void ResetTranslationOverlay()
    {
        if (_translationOverlay is null) return;
        _translationOverlay = null;
        _showTranslatedImage = false;
        PublishTranslationOverlay();
    }

    internal static Rect? GetOverlaySpace(MangaTranslationResponse page, MangaTranslationRegion region,
        HashSet<int> coveredObservations, HashSet<int> coveredDetections, out Rect? balanced, out Rect[]? sourceMasks)
    {
        sourceMasks = null;
        balanced = null;
        if (region.SourceRegionIds.Length == 0) return null;
        var source = new Rect(region.X, region.Y, region.Width, region.Height);
        var masks = new Rect[region.SourceRegionIds.Length];
        var ownedDetections = new HashSet<int>();

        for (int i = 0; i < region.SourceRegionIds.Length; i++)
        {
            int id = region.SourceRegionIds[i];
            if (id < 1 || id > page.OcrObservations.Length) return null;
            MangaOcrObservation observation = page.OcrObservations[id - 1];
            masks[i] = BoundsOf(observation.Polygon);
            ownedDetections.UnionWith(observation.OriginalDetectionIds);
        }

        coveredObservations.UnionWith(region.SourceRegionIds);
        Rect? area = GetBounds(region.BubbleInterior);
        balanced = GetBounds(region.BalancedBubbleInterior);

        foreach (MangaOcrObservation observation in page.OcrObservations)
        {
            if (region.SourceRegionIds.Contains(observation.Id)) continue;
            Rect polygon = BoundsOf(observation.Polygon);
            // 已被本区原字罩住的重复低置信框仍在双语面板可查，但不应阻断排版。
            if (observation.RequiresReview && Array.Exists(masks, mask => mask.Contains(polygon)))
            {
                coveredObservations.Add(observation.Id);
                foreach (int id in observation.OriginalDetectionIds)
                {
                    // 原始检测框可能大于校正后的复核框，仍要单独检查包含关系。
                    if (id < 1 || id > page.RawDetections.Length) continue;
                    MangaOcrDetection raw = page.RawDetections[id - 1];
                    Rect rawBounds = BoundsOf(raw.Polygon);
                    if (raw.Id == id && Array.Exists(masks, mask => mask.Contains(rawBounds)))
                        ownedDetections.Add(id);
                }
                continue;
            }
            area = Clip(area, polygon);
            balanced = Clip(balanced, polygon);
        }

        coveredDetections.UnionWith(ownedDetections);
        if (area is null && balanced is null) return null;
        foreach (MangaOcrDetection detection in page.RawDetections)
        {
            if (ownedDetections.Contains(detection.Id)) continue;
            Rect polygon = BoundsOf(detection.Polygon);
            area = Clip(area, polygon);
            balanced = Clip(balanced, polygon);
            if (area is null && balanced is null) return null;
        }

        sourceMasks = masks;
        return area;

        static Rect? GetBounds(MangaOcrLayoutBounds? bounds) =>
            bounds is { } value ? new Rect(value.X, value.Y, value.Width, value.Height) : null;
        Rect? Clip(Rect? candidate, Rect obstacle) =>
            candidate is { } available ? MangaTranslationOverlay.SpaceBeside(available, source, obstacle) : null;
    }

    private static Rect BoundsOf(MangaOcrPoint[] polygon) =>
        new(new Point(polygon.Min(static point => point.X), polygon.Min(static point => point.Y)),
            new Point(polygon.Max(static point => point.X), polygon.Max(static point => point.Y)));

    private void PublishTranslationOverlay()
    {
        OnPropertyChanged(nameof(ActiveTranslationOverlay));
        OnPropertyChanged(nameof(HasImageTranslation));
        OnPropertyChanged(nameof(TranslationImageButtonText));
        UpdateTranslationOverlayItems();
    }

    private void UpdateTranslationOverlayItems()
    {
        if (_index < _continuousItems.Count) _continuousItems[_index].TranslationOverlay = ActiveTranslationOverlay;
        if (_index < _horizontalItems.Count) _horizontalItems[_index].TranslationOverlay = ActiveTranslationOverlay;
    }
}
