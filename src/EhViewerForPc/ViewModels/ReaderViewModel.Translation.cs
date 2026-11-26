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
                    overlays.Add(new MangaTranslationOverlayRegion(
                        new Rect(region.X, region.Y, region.Width, region.Height), region.TranslatedText));
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
                    ? $"图上显示 {overlays.Count} 个区块的译文 · {reviewCount} 个识别区块待核对"
                    : $"图上显示 {overlays.Count} 个区块的译文 · GPU 已启用";
                _translationOverlay = new MangaTranslationOverlay(result.Width, result.Height, overlays, result.TargetLanguage)
                {
                    ImageSource = image.Value,
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
                    ? $"已翻译 {translatedCount} 个区块 · GPU 已启用"
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
