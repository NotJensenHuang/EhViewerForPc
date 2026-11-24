using System.Text;
using Avalonia;
using CommunityToolkit.Mvvm.Input;
using EhViewerForPc.Controls;
using EhViewerForPc.Services;

namespace EhViewerForPc.ViewModels;

public sealed partial class ReaderViewModel
{
    private CancellationTokenSource? _translationJob;
    private bool _isTranslationOpen;
    private bool _isTranslationRunning;
    private bool _showTranslationSettings;
    private bool _showTranslatedImage;
    private MangaTranslationOverlay? _translationOverlay;
    private string _translationStatus = "";
    private string _translationText = "";

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
        private set => SetProperty(ref _translationStatus, value);
    }

    public string TranslationText
    {
        get => _translationText;
        private set
        {
            if (SetProperty(ref _translationText, value)) OnPropertyChanged(nameof(HasTranslationText));
        }
    }

    public bool HasTranslationText => TranslationText.Length > 0;
    public bool HasImageTranslation => _translationOverlay is not null;
    public string TranslationImageButtonText => _showTranslatedImage ? "显示原图" : "显示译文";
    public MangaTranslationOverlay? ActiveTranslationOverlay => _showTranslatedImage ? _translationOverlay : null;

    public bool ShowTranslationSettings
    {
        get => _showTranslationSettings;
        private set => SetProperty(ref _showTranslationSettings, value);
    }

    internal async Task TranslateCurrentPageAsync(ReaderTranslationImage? image)
    {
        if (!CanTranslateCurrentPage) return;

        ResetTranslationOverlay();
        ShowTranslationSettings = false;
        IsTranslationOpen = true;
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
        _translationJob = job;
        IsTranslationRunning = true;
        TranslationStatus = "正在本机识别并翻译…";
        try
        {
            MangaTranslationResponse result = await _services.MangaTranslation.TranslateAsync(image.Value, job.Token);
            if (_translationJob != job) return;
            if (job.IsCancellationRequested)
            {
                TranslationStatus = "已取消翻译。";
                return;
            }

            if (result.Regions.Length == 0)
            {
                TranslationStatus = "本页未识别到日文文字。";
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
                    text.Append("未生成译文");
                    continue;
                }

                text.Append(region.TranslatedText);
                translatedCount++;
                if (region.Width > 0 && region.Height > 0)
                {
                    overlays.Add(new MangaTranslationOverlayRegion(
                        new Rect(region.X, region.Y, region.Width, region.Height), region.TranslatedText));
                }
            }

            TranslationText = text.ToString();
            if (overlays.Count > 0)
            {
                TranslationStatus = $"图上显示 {overlays.Count} 个区块的译文 · GPU 已启用";
                _translationOverlay = new MangaTranslationOverlay(result.Width, result.Height, overlays)
                {
                    ImageSource = image.Value,
                };
                _showTranslatedImage = true;
                IsTranslationOpen = false;
                PublishTranslationOverlay();
            }
            else
            {
                TranslationStatus = translatedCount > 0
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
                IsTranslationRunning = false;
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
        else CloseTranslation();
    }

    internal void CloseTranslation()
    {
        _translationJob?.Cancel();
        _translationJob = null;
        IsTranslationRunning = false;
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
