using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerReader.Core.Format;
using EhViewerReader.Core.Models;
using EhViewerReader.Services;

namespace EhViewerReader.ViewModels;

public sealed record FitOption(FitMode Value, string Label);

public sealed partial class ReaderViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly GalleryInfo _gallery;
    private readonly List<GalleryPage> _pages;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _hintTimer;
    private IReadOnlyList<ContinuousPageItem> _continuousItems = [];
    private IReadOnlyList<FitOption> _fitOptions;
    private FitOption _selectedFit;
    private ReadingMode _mode;
    private bool _rightToLeft;
    private bool _isLoadingDimensions;
    private double _dimensionProgress;
    private double _continuousWidth = 1200;
    private int _index;
    private double _pageOffset;
    private bool _completed;
    private IImageRequest? _singleRequest;
    private IImageRequest? _preloadRequest;
    private string _pageLabel = string.Empty;
    private string _originalPageLabel = string.Empty;
    private string _restoreHint = string.Empty;
    private bool _showRestoreHint;
    private double _sliderValue = 1;
    private IBrush _backgroundBrush = Brushes.Black;
    private bool _disposed;
    private Task? _dimensionTask;

    public ReaderViewModel(AppServices services, GalleryInfo gallery)
    {
        _services = services;
        _gallery = gallery;
        _pages = PageFileParser.Scan(gallery.FolderPath, null, out string? scanError)
            .Pages
            .ToList();
        if (scanError is not null)
        {
            services.Log.Warn("Reader page scan failed: " + gallery.FolderPath + " | " + scanError);
        }

        _fitOptions =
        [
            new FitOption(FitMode.FitPage, "适应页面"),
            new FitOption(FitMode.FitWidth, "适应宽度"),
            new FitOption(FitMode.FitHeight, "适应高度"),
            new FitOption(FitMode.Original, "原始大小"),
        ];
        _selectedFit = _fitOptions.First(o => o.Value == services.Settings.DefaultFitMode);
        _mode = services.Settings.DefaultReadingMode == ReadingMode.Double
            ? ReadingMode.Single
            : services.Settings.DefaultReadingMode;
        _rightToLeft = services.Settings.RightToLeft;
        ApplyBackground(services.Settings.ReaderBackground);

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveProgress();
        };
        _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer.Stop();
            ShowRestoreHint = false;
        };

        RestoreProgress();
        UpdateCurrentRequests();
        UpdateLabels();
    }

    public event EventHandler<double>? ScrollToRequested;

    public GalleryInfo Gallery => _gallery;
    public AppServices Services => _services;
    public string Title => _gallery.DisplayTitle;

    /// <summary>Source label shown in the toolbar; empty for a single source.</summary>
    public string SourceText => _gallery.SourceName;
    public bool HasSourceText => !string.IsNullOrEmpty(_gallery.SourceName);
    public int PageCount => _pages.Count;
    public bool HasPages => _pages.Count > 0;
    public ReadingMode Mode => _mode;
    public bool IsSingleMode => _mode == ReadingMode.Single;
    public bool IsContinuousMode => _mode == ReadingMode.Continuous;
    public bool RightToLeft => _rightToLeft;
    public string DirectionLabel => _rightToLeft ? "右→左" : "左→右";
    public FitMode FitMode => _selectedFit.Value;
    public int MaxMegapixels => _services.Settings.MaxDecodeMegapixels;
    public int MaxDimension => _services.Settings.MaxDecodeDimension;
    public IBrush BackgroundBrush => _backgroundBrush;

    public IReadOnlyList<FitOption> FitOptions => _fitOptions;

    public FitOption SelectedFit
    {
        get => _selectedFit;
        set
        {
            if (!SetProperty(ref _selectedFit, value) || value is null) return;
            OnPropertyChanged(nameof(FitMode));
        }
    }

    public IImageRequest? SingleRequest
    {
        get => _singleRequest;
        private set => SetProperty(ref _singleRequest, value);
    }

    public IImageRequest? PreloadRequest
    {
        get => _preloadRequest;
        private set => SetProperty(ref _preloadRequest, value);
    }

    public IReadOnlyList<ContinuousPageItem> ContinuousItems
    {
        get => _continuousItems;
        private set => SetProperty(ref _continuousItems, value);
    }

    public bool IsLoadingDimensions
    {
        get => _isLoadingDimensions;
        private set => SetProperty(ref _isLoadingDimensions, value);
    }

    public double DimensionProgress
    {
        get => _dimensionProgress;
        private set => SetProperty(ref _dimensionProgress, value);
    }

    public string PageLabel
    {
        get => _pageLabel;
        private set => SetProperty(ref _pageLabel, value);
    }

    public string OriginalPageLabel
    {
        get => _originalPageLabel;
        private set => SetProperty(ref _originalPageLabel, value);
    }

    public string RestoreHint
    {
        get => _restoreHint;
        private set => SetProperty(ref _restoreHint, value);
    }

    public bool ShowRestoreHint
    {
        get => _showRestoreHint;
        private set => SetProperty(ref _showRestoreHint, value);
    }

    public double SliderValue
    {
        get => _sliderValue;
        set
        {
            if (!SetProperty(ref _sliderValue, value)) return;
            int target = (int)Math.Round(value) - 1;
            if (target != _index) GoToIndex(target, scrollContinuous: true);
        }
    }

    public bool CanPrev => _index > 0;
    public bool CanNext => _index < _pages.Count - 1;
    public int SliderMaximum => Math.Max(1, _pages.Count);
    public int CurrentPageNumber => _pages.Count == 0 ? 0 : _pages[Math.Clamp(_index, 0, _pages.Count - 1)].Number;

    public string MissingSummary
    {
        get
        {
            if (_gallery.MissingRanges.Count == 0) return string.Empty;
            return "缺页 " + string.Join(", ", _gallery.MissingRanges.Select(r => r.ToString()));
        }
    }

    public string DebugStatus =>
        $"{_gallery.StableId} · {_pages.Count} 页 · {_gallery.ExtensionSummary}";

    private void RestoreProgress()
    {
        if (_pages.Count == 0) return;
        ReadingProgress? progress =
            _services.Progress.Get(_gallery.WorkId) ?? _services.Progress.Get(_gallery.StableId);
        if (progress is null)
        {
            _index = 0;
            return;
        }

        if (progress.Completed)
        {
            _index = 0;
            RestoreHint = "上次已读完，本次从头开始";
            ShowRestoreHint = true;
            _hintTimer.Start();
            return;
        }

        int byNumber = _pages.FindIndex(p => p.Number == progress.PageNumber);
        if (byNumber >= 0)
        {
            _index = byNumber;
            _pageOffset = Math.Clamp(progress.PageOffset, 0, 1);
            if (_index > 0)
            {
                RestoreHint = $"已恢复到第 {_pages[_index].Number} 页";
                ShowRestoreHint = true;
                _hintTimer.Start();
            }

            return;
        }

        // Saved page vanished (missing/removed): fall back to the nearest lower page.
        int fallback = -1;
        for (int i = 0; i < _pages.Count; i++)
        {
            if (_pages[i].Number <= progress.PageNumber) fallback = i;
            else break;
        }

        if (fallback < 0) fallback = 0;
        _index = fallback;
        _pageOffset = Math.Clamp(progress.PageOffset, 0, 1);
        RestoreHint = $"原第 {progress.PageNumber} 页不存在，已定位到第 {_pages[fallback].Number} 页";
        ShowRestoreHint = true;
        _hintTimer.Start();
    }

    private void UpdateCurrentRequests()
    {
        if (_pages.Count == 0)
        {
            SingleRequest = null;
            PreloadRequest = null;
            return;
        }

        GalleryPage current = _pages[Math.Clamp(_index, 0, _pages.Count - 1)];
        SingleRequest = new PageImageRequest(current.Candidates, _services.Images);

        GalleryPage? next = _index + 1 < _pages.Count ? _pages[_index + 1] : null;
        PreloadRequest = next is null ? null : new PageImageRequest(next.Candidates, _services.Images);
    }

    private void UpdateLabels()
    {
        if (_pages.Count == 0)
        {
            PageLabel = "无页面";
            OriginalPageLabel = string.Empty;
            SliderValue = 1;
            return;
        }

        GalleryPage page = _pages[_index];
        PageLabel = $"{_index + 1} / {_pages.Count}";
        OriginalPageLabel = page.Number == _index + 1 ? string.Empty : $"原始第 {page.Number} 页";
        SliderValue = _index + 1;
        OnPropertyChanged(nameof(CurrentPageNumber));
        OnPropertyChanged(nameof(CanPrev));
        OnPropertyChanged(nameof(CanNext));
    }

    private void SetIndex(int index, bool scrollContinuous, double offset = 0, bool save = true)
    {
        if (_pages.Count == 0) return;
        index = Math.Clamp(index, 0, _pages.Count - 1);
        bool changed = index != _index;
        _index = index;
        _pageOffset = Math.Clamp(offset, 0, 1);
        _completed = index == _pages.Count - 1;
        if (IsSingleMode)
        {
            UpdateCurrentRequests();
        }

        UpdateLabels();

        if (scrollContinuous && IsContinuousMode)
        {
            ScrollToRequested?.Invoke(this, GetScrollOffsetForPage(_index, _pageOffset));
        }

        if (changed && save) ScheduleSave();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveProgress()
    {
        if (_pages.Count == 0) return;
        GalleryPage page = _pages[_index];
        _services.Progress.RecordRead(
            _gallery.WorkId,
            page.Number,
            _index,
            _pageOffset,
            _pages.Count,
            completed: _index == _pages.Count - 1);
        _services.Progress.Save(out _);
    }

    [RelayCommand]
    private void NextPage() => Step(1);

    [RelayCommand]
    private void PrevPage() => Step(-1);

    [RelayCommand]
    private void FirstPage() => GoToIndex(0, scrollContinuous: true);

    [RelayCommand]
    private void LastPage() => GoToIndex(_pages.Count - 1, scrollContinuous: true);

    public void Step(int delta)
    {
        if (_pages.Count == 0) return;
        GoToIndex(_index + delta, scrollContinuous: true);
    }

    public void GoToIndex(int index, bool scrollContinuous)
    {
        if (_pages.Count == 0) return;
        SetIndex(index, scrollContinuous);
    }

    public async Task SetModeAsync(ReadingMode mode)
    {
        if (_mode == mode) return;
        _mode = mode == ReadingMode.Double ? ReadingMode.Single : mode;
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsSingleMode));
        OnPropertyChanged(nameof(IsContinuousMode));

        if (_mode == ReadingMode.Continuous)
        {
            await EnsureDimensionsAsync().ConfigureAwait(true);
            double offset = GetScrollOffsetForPage(_index, _pageOffset);
            ScrollToRequested?.Invoke(this, offset);
        }
        else
        {
            UpdateCurrentRequests();
            SetIndex(_index, scrollContinuous: false);
        }
    }

    public void ToggleDirection()
    {
        _rightToLeft = !_rightToLeft;
        _services.Settings.RightToLeft = _rightToLeft;
        _services.TrySaveSettings(out _);
        OnPropertyChanged(nameof(RightToLeft));
        OnPropertyChanged(nameof(DirectionLabel));
    }

    public async Task InitializeAsync()
    {
        if (!IsContinuousMode) return;
        await EnsureDimensionsAsync().ConfigureAwait(true);
        ScrollToRequested?.Invoke(this, GetScrollOffsetForPage(_index, _pageOffset));
    }

    public void ToggleBackground()
    {
        _services.Settings.ReaderBackground = _services.Settings.ReaderBackground switch
        {
            ReaderBackground.Black => ReaderBackground.DarkGray,
            ReaderBackground.DarkGray => ReaderBackground.Gray,
            ReaderBackground.Gray => ReaderBackground.White,
            _ => ReaderBackground.Black,
        };
        ApplyBackground(_services.Settings.ReaderBackground);
        _services.TrySaveSettings(out _);
    }

    private void ApplyBackground(ReaderBackground background)
    {
        _backgroundBrush = background switch
        {
            ReaderBackground.DarkGray => new SolidColorBrush(Color.Parse("#141414")),
            ReaderBackground.Gray => new SolidColorBrush(Color.Parse("#3A3A3A")),
            ReaderBackground.White => new SolidColorBrush(Color.Parse("#F5F5F5")),
            _ => new SolidColorBrush(Color.Parse("#000000")),
        };
        OnPropertyChanged(nameof(BackgroundBrush));
    }

    private async Task EnsureDimensionsAsync()
    {
        if (_pages.Count == 0) return;
        if (ContinuousItems.Count == _pages.Count)
        {
            return;
        }

        if (_dimensionTask is not null)
        {
            await _dimensionTask.ConfigureAwait(true);
            return;
        }

        _dimensionTask = LoadDimensionsAsync();
        try
        {
            await _dimensionTask.ConfigureAwait(true);
        }
        finally
        {
            _dimensionTask = null;
        }
    }

    private async Task LoadDimensionsAsync()
    {
        IsLoadingDimensions = true;
        DimensionProgress = 0;
        var progress = new Progress<PageDimensionProgress>(p =>
        {
            DimensionProgress = p.Total <= 0 ? 0 : Math.Clamp(p.Processed * 100.0 / p.Total, 0, 100);
        });

        try
        {
            IReadOnlyList<PageDimension> dimensions = await _services.Dimensions
                .LoadAsync(_gallery, _pages, progress, _lifetime.Token)
                .ConfigureAwait(true);

            var items = new List<ContinuousPageItem>(_pages.Count);
            for (int i = 0; i < _pages.Count; i++)
            {
                PageDimension dimension = dimensions[i];
                var request = new PageImageRequest(_pages[i].Candidates, _services.Images);
                items.Add(new ContinuousPageItem(
                    _pages[i],
                    request,
                    dimension.Width,
                    dimension.Height,
                    dimension.Known));
            }

            ContinuousItems = items;
            ApplyContinuousWidth();
            DimensionProgress = 100;
        }
        catch (OperationCanceledException)
        {
            // Closing the reader cancels the measurement pass.
        }
        catch (Exception ex)
        {
            _services.Log.Warn("Page dimension load failed: " + _gallery.FolderPath, ex);
        }
        finally
        {
            IsLoadingDimensions = false;
        }
    }

    public void SetContinuousWidth(double width)
    {
        double target = Math.Clamp(width, 320, 1400);
        if (Math.Abs(target - _continuousWidth) < 2 && ContinuousItems.Count > 0)
        {
            return;
        }

        _continuousWidth = target;
        if (ContinuousItems.Count == 0) return;

        foreach (ContinuousPageItem item in ContinuousItems)
        {
            item.Resize(target);
        }

        RecomputeTops();
        if (IsContinuousMode)
        {
            ScrollToRequested?.Invoke(this, GetScrollOffsetForPage(_index, _pageOffset));
        }
    }

    private void ApplyContinuousWidth()
    {
        foreach (ContinuousPageItem item in ContinuousItems)
        {
            item.Resize(_continuousWidth);
        }

        RecomputeTops();
    }

    private void RecomputeTops()
    {
        double top = 0;
        foreach (ContinuousPageItem item in ContinuousItems)
        {
            item.Top = top;
            top += item.SlotHeight;
        }
    }

    public double GetScrollOffsetForPage(int index, double fraction)
    {
        if (ContinuousItems.Count == 0) return 0;
        ContinuousPageItem item = ContinuousItems[Math.Clamp(index, 0, ContinuousItems.Count - 1)];
        return item.Top + Math.Clamp(fraction, 0, 1) * item.DisplayHeight;
    }

    public void UpdateScrollOffset(double offsetY, double viewportHeight)
    {
        if (ContinuousItems.Count == 0 || !IsContinuousMode) return;

        double probe = offsetY + Math.Max(40, viewportHeight * 0.32);
        int low = 0;
        int high = ContinuousItems.Count - 1;
        int found = 0;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (ContinuousItems[mid].Top <= probe)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        ContinuousPageItem item = ContinuousItems[found];
        double fraction = item.DisplayHeight <= 0
            ? 0
            : Math.Clamp((probe - item.Top) / item.DisplayHeight, 0, 1);

        if (found != _index)
        {
            SetIndex(found, scrollContinuous: false, offset: fraction);
        }
        else
        {
            _pageOffset = fraction;
            ScheduleSave();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveTimer.Stop();
        _hintTimer.Stop();
        SaveProgress();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
