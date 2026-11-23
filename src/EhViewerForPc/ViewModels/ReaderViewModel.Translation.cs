using System.Text;
using CommunityToolkit.Mvvm.Input;
using EhViewerForPc.Services;

namespace EhViewerForPc.ViewModels;

public sealed partial class ReaderViewModel
{
    private CancellationTokenSource? _translationJob;
    private bool _isTranslationOpen;
    private bool _isTranslationRunning;
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
        private set => SetProperty(ref _translationText, value);
    }

    internal async Task TranslateCurrentPageAsync(ReaderTranslationImage? image)
    {
        if (!CanTranslateCurrentPage) return;

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
            for (int i = 0; i < result.Regions.Length; i++)
            {
                if (i != 0) text.AppendLine().AppendLine();
                MangaTranslationRegion region = result.Regions[i];
                text.Append(i + 1).Append("  ").AppendLine(region.SourceText)
                    .Append(region.TranslatedText);
            }

            TranslationText = text.ToString();
            TranslationStatus = $"识别出 {result.Regions.Length} 个区块 · GPU 已启用";
        }
        catch (OperationCanceledException) when (job.IsCancellationRequested)
        {
            if (_translationJob == job) TranslationStatus = "已取消翻译。";
        }
        catch (MangaTranslationException ex)
        {
            if (_translationJob == job) TranslationStatus = ex.Message;
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
    internal void CloseTranslation()
    {
        _translationJob?.Cancel();
        _translationJob = null;
        IsTranslationRunning = false;
        IsTranslationOpen = false;
        TranslationStatus = "";
        TranslationText = "";
    }
}
