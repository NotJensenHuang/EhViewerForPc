using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerForPc.Core.Format;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Services;

namespace EhViewerForPc.ViewModels;

public sealed record FitOption(FitMode Value, string Label);

public sealed partial class ReaderViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly IReaderSource _source;
    private readonly List<GalleryPage> _pages;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _hintTimer;
    private IReadOnlyList<ContinuousPageItem> _continuousItems = [];
    private IReadOnlyList<ContinuousPageItem> _horizontalItems = [];
    private IReadOnlyList<ContinuousPageItem> _horizontalDisplayItems = [];
    private IReadOnlyList<FitOption> _fitOptions;
    private FitOption _selectedFit;
    private ReadingMode _mode;
    private bool _rightToLeft;
    private bool _isLoadingDimensions;
    private double _dimensionProgress;
    private double _continuousWidth = 1200;
    private double _continuousHeight = 800;
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

    /// <summary>
    /// While the view is being (re)positioned after a mode switch or resize,
    /// the ScrollViewer still sits at offset 0 and its ScrollChanged probe
    /// would mistake the first display slot for the current page — in RTL
    /// horizontal mode that is the LAST page. Suppressed until the viewport
    /// settles at the requested offset.
    /// </summary>
    private bool _suppressScrollSync;
    private int _relocationVersion;

    public ReaderViewModel(AppServices services, GalleryInfo gallery, int? initialPageNumber = null)
        : this(services, new LocalReaderSource(services, gallery), initialPageNumber)
    {
    }

    public ReaderViewModel(AppServices services, IReaderSource source, int? initialPageNumber = null)
    {
        _services = services;
        _source = source;
        _pages = source.Pages.ToList();
        if (initialPageNumber is int requested)
        {
            _index = _pages.FindIndex(p => p.Number == requested);
            if (_index < 0) throw new FileNotFoundException("命中页已不在该副本中。");
        }

        _fitOptions =
        [
            new FitOption(FitMode.FitPage, "适应页面"),
            new FitOption(FitMode.FitWidth, "适应宽度"),
            new FitOption(FitMode.FitHeight, "适应高度"),
            new FitOption(FitMode.Original, "原始大小"),
        ];
        _selectedFit = _fitOptions.First(o => o.Value == services.Settings.DefaultFitMode);
        _mode = services.Settings.DefaultReadingMode;
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

        if (initialPageNumber is null) RestoreProgress();
        UpdateCurrentRequests();
        UpdateLabels();

        if (source is OnlineReaderSource online)
        {
            // Continuous mode starts with estimated slots; once a page decodes,
            // its real aspect ratio replaces the estimate.
            online.DimensionResolved = OnOnlineDimensionResolved;
            online.QualityChanged = () => Dispatcher.UIThread.Post(() => { if (!_disposed) OnPropertyChanged(nameof(SourceText)); });
        }
    }

    public event EventHandler<double>? ScrollToRequested;

    /// <summary>Raised in horizontal mode: the target X offset the view should scroll to.</summary>
    public event EventHandler<double>? HorizontalScrollToRequested;

    public AppServices Services => _services;
    public IReaderSource Source => _source;
    public string Title => _source.Title;

    /// <summary>Source label shown in the toolbar; empty for a single source.</summary>
    public string SourceText => _source.SourceLabel;
    public bool HasSourceText => !string.IsNullOrEmpty(_source.SourceLabel);
    public int PageCount => _pages.Count;
    public bool HasPages => _pages.Count > 0;
    public bool IsCurrentPageMissing => _pages.Count > 0 &&
        _pages[_index] is { State: PageState.Empty, Candidates.Count: 0 };
    public bool ShowSingleMissingPage => IsSingleMode && IsCurrentPageMissing;
    public string MissingPageText => $"第 {CurrentPageNumber} 页缺失";
    public ReadingMode Mode => _mode;
    public bool IsSingleMode => _mode == ReadingMode.Single;
    public bool IsContinuousMode => _mode == ReadingMode.Continuous;

    /// <summary>Horizontal continuous mode: pages laid out left-to-right (or
    /// right-to-left) at a common height, so merged two-page images and long
    /// strips simply occupy their natural width.</summary>
    public bool IsHorizontalMode => _mode == ReadingMode.Double;
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
            _services.Settings.DefaultFitMode = value.Value;
            _services.TrySaveSettings(out _);
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
        get => IsContinuousMode ? _continuousItems : [];
        private set => SetProperty(ref _continuousItems, value);
    }

    /// <summary>Horizontal slots in reading order (page 0 first).</summary>
    public IReadOnlyList<ContinuousPageItem> HorizontalItems
    {
        get => _horizontalItems;
        private set => SetProperty(ref _horizontalItems, value);
    }

    /// <summary>Horizontal slots in display order (reversed for right-to-left
    /// reading, so the first page sits at the right edge). Bound by the view.</summary>
    public IReadOnlyList<ContinuousPageItem> HorizontalDisplayItems
    {
        get => IsHorizontalMode ? _horizontalDisplayItems : [];
        private set => SetProperty(ref _horizontalDisplayItems, value);
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
            if (_pages.Count == 0) return;

            // Continuous position: integer part = page, fraction = offset
            // inside the page, so dragging maps 1:1 to scroll pixels.
            double target = Math.Clamp(value - 1, 0, _pages.Count - 1);
            int index = (int)target;
            double fraction = target - index;
            if (index == _index && Math.Abs(fraction - _pageOffset) < 0.002) return;
            SetIndex(index, scrollContinuous: true, offset: fraction);
        }
    }

    public bool CanPrev => _index > 0;
    public bool CanNext => _index < _pages.Count - 1;
    public int SliderMaximum => Math.Max(1, _pages.Count);
    public int CurrentPageNumber => _pages.Count == 0 ? 0 : _pages[Math.Clamp(_index, 0, _pages.Count - 1)].Number;

    public string MissingSummary => _source.MissingSummary;

    public string DebugStatus => _source.DebugInfo;

    private void RestoreProgress()
    {
        if (_pages.Count == 0) return;
        ReadingProgress? progress =
            _services.Progress.Get(_source.ProgressKey) ?? _services.Progress.Get(_source.StableId);
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

    private void UpdateCurrentRequests(bool refresh = false)
    {
        if (_pages.Count == 0)
        {
            SingleRequest = null;
            PreloadRequest = null;
            return;
        }

        int index = Math.Clamp(_index, 0, _pages.Count - 1);
        SingleRequest = _source.CreateRequest(index, refresh);
        PreloadRequest = _index + 1 < _pages.Count ? _source.CreateRequest(_index + 1, refresh) : null;
    }

    /// <summary>Re-creates the current page request so a failed load retries.</summary>
    [RelayCommand]
    private void RetryCurrent()
    {
        if (_pages.Count == 0) return;
        int index = Math.Clamp(_index, 0, _pages.Count - 1);
        if (IsSingleMode)
        {
            UpdateCurrentRequests(refresh: true);
            OnPropertyChanged(nameof(SingleRequest));
            OnPropertyChanged(nameof(PreloadRequest));
            return;
        }

        if (index < _continuousItems.Count)
        {
            _continuousItems[index].UpdateRequest(_source.CreateRequest(index, refresh: true));
        }

        if (index < _horizontalItems.Count)
        {
            _horizontalItems[index].UpdateRequest(_source.CreateRequest(index, refresh: true));
        }
    }

    private void UpdateLabels()
    {
        OnPropertyChanged(nameof(IsCurrentPageMissing));
        OnPropertyChanged(nameof(ShowSingleMissingPage));
        OnPropertyChanged(nameof(MissingPageText));
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
        // Keep the slider in sync without re-entering its navigation setter.
        _sliderValue = _index + 1 + _pageOffset;
        OnPropertyChanged(nameof(SliderValue));
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
            SuppressSyncWhileRelocating(() =>
                ScrollToRequested?.Invoke(this, GetScrollOffsetForPage(_index, _pageOffset)));
        }

        if (scrollContinuous && IsHorizontalMode)
        {
            double target = GetHorizontalOffsetForPage(_index, _pageOffset);
            LastHorizontalTarget = target;
            SuppressSyncWhileRelocating(() =>
                HorizontalScrollToRequested?.Invoke(this, target));
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
            _source.ProgressKey,
            page.Number,
            _index,
            _pageOffset,
            _source.ExpectedPageCount,
            completed: _source.IsComplete && _index == _pages.Count - 1);
        _services.TrySaveProgress(out _);
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

    /// <summary>Navigates to an exact original page and clears historical offsets, including a repeated current-page hit.</summary>
    public bool TryNavigateToPageNumber(int pageNumber)
    {
        if (_disposed) return false;
        int index = _pages.FindIndex(page => page.Number == pageNumber);
        if (index < 0) return false;
        RestoreHint = string.Empty; ShowRestoreHint = false;
        SetIndex(index, scrollContinuous: true, offset: 0);
        ScheduleSave();
        return true;
    }

    public async Task SetModeAsync(ReadingMode mode)
    {
        if (_mode == mode) return;
        _suppressScrollSync = true;
        _relocationVersion++;
        _mode = mode;
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsSingleMode));
        OnPropertyChanged(nameof(ShowSingleMissingPage));
        OnPropertyChanged(nameof(IsContinuousMode));
        OnPropertyChanged(nameof(IsHorizontalMode));
        OnPropertyChanged(nameof(ContinuousItems));
        OnPropertyChanged(nameof(HorizontalDisplayItems));

        // The reading mode chosen in the reader persists as the default.
        _services.Settings.DefaultReadingMode = mode;
        _services.TrySaveSettings(out _);

        if (IsSingleMode)
        {
            UpdateCurrentRequests();
            SetIndex(_index, scrollContinuous: false);
            SuppressSyncWhileRelocating(static () => { });
        }
        else
        {
            await EnsureDimensionsAsync().ConfigureAwait(true);
            if (IsContinuousMode)
            {
                SuppressSyncWhileRelocating(() =>
                    ScrollToRequested?.Invoke(this, GetScrollOffsetForPage(_index, _pageOffset)));
            }
            else
            {
                SuppressSyncWhileRelocating(() =>
                    HorizontalScrollToRequested?.Invoke(this, GetHorizontalOffsetForPage(_index, _pageOffset)));
            }
        }
    }

    /// <summary>
    /// Raises a repositioning request and keeps the scroll probe suppressed
    /// until the view has applied it. The window posts the scroll at Loaded
    /// and retries at Background (a fresh layout may clamp the first attempt);
    /// the release therefore waits until SystemIdle, after both.
    /// </summary>
    private void SuppressSyncWhileRelocating(Action raise)
    {
        _suppressScrollSync = true;
        int version = ++_relocationVersion;
        raise();
        Dispatcher.UIThread.Post(() => { if (version == _relocationVersion) _suppressScrollSync = false; }, DispatcherPriority.SystemIdle);
    }

    public void ToggleDirection()
        => SetDirection(!_rightToLeft);

    public void SetDirection(bool rightToLeft)
    {
        if (!IsHorizontalMode || _rightToLeft == rightToLeft) return;
        _suppressScrollSync = true;
        _relocationVersion++;
        _rightToLeft = rightToLeft;
        _services.Settings.RightToLeft = _rightToLeft;
        _services.TrySaveSettings(out _);
        OnPropertyChanged(nameof(RightToLeft));
        OnPropertyChanged(nameof(DirectionLabel));
        if (_horizontalItems.Count > 0)
        {
            RebuildHorizontalDisplay();
            SuppressSyncWhileRelocating(() =>
                HorizontalScrollToRequested?.Invoke(this, GetHorizontalOffsetForPage(_index, _pageOffset)));
        }
        else
        {
            SuppressSyncWhileRelocating(static () => { });
        }
    }

    public async Task InitializeAsync()
    {
        if (IsSingleMode) return;
        _suppressScrollSync = true;
        _relocationVersion++;
        await EnsureDimensionsAsync().ConfigureAwait(true);
        if (IsContinuousMode)
        {
            SuppressSyncWhileRelocating(() =>
                ScrollToRequested?.Invoke(this, GetScrollOffsetForPage(_index, _pageOffset)));
        }
        else
        {
            SuppressSyncWhileRelocating(() =>
                HorizontalScrollToRequested?.Invoke(this, GetHorizontalOffsetForPage(_index, _pageOffset)));
        }
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
        if (_continuousItems.Count == _pages.Count)
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
            int focus = _pages.Count == 0 ? 1 : _pages[Math.Clamp(_index, 0, _pages.Count - 1)].Number;
            IReadOnlyList<PageDimension> dimensions = await _source
                .LoadDimensionsAsync(focus, progress, _lifetime.Token)
                .ConfigureAwait(true);

            var vertical = new List<ContinuousPageItem>(_pages.Count);
            var horizontal = new List<ContinuousPageItem>(_pages.Count);
            for (int i = 0; i < _pages.Count; i++)
            {
                PageDimension dimension = dimensions[i];
                vertical.Add(new ContinuousPageItem(
                    _pages[i],
                    _source.CreateRequest(i),
                    dimension.Width,
                    dimension.Height,
                    dimension.Known));
                horizontal.Add(new ContinuousPageItem(
                    _pages[i],
                    _source.CreateRequest(i),
                    dimension.Width,
                    dimension.Height,
                    dimension.Known)
                {
                    ReadIndex = i,
                });
            }

            ContinuousItems = vertical;
            HorizontalItems = horizontal;
            RebuildHorizontalDisplay();
            ApplyContinuousWidth();
            ApplyContinuousHeight();
            DimensionProgress = 100;
        }
        catch (OperationCanceledException)
        {
            // Closing the reader cancels the measurement pass.
        }
        catch (Exception ex)
        {
            _services.Log.Warn("Page dimension load failed: " + _source.DebugInfo, ex);
        }
        finally
        {
            IsLoadingDimensions = false;
        }
    }

    /// <summary>Called from an image decode thread when an online page's real size is known.</summary>
    private void OnOnlineDimensionResolved(int index, int width, int height)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            RefineDimension(index, width, height);
        }, DispatcherPriority.Background);
    }

    private void RefineDimension(int index, int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        bool changed = false;
        if (index >= 0 && index < _continuousItems.Count)
        {
            _continuousItems[index].SetNaturalSize(width, height, keepWidth: true);
            changed = true;
        }

        if (index >= 0 && index < _horizontalItems.Count)
        {
            _horizontalItems[index].SetNaturalSize(width, height, keepWidth: false);
            changed = true;
        }

        if (!changed) return;

        RecomputeTops();
        RecomputeLefts();
        if (IsContinuousMode)
        {
            SuppressSyncWhileRelocating(() =>
                ScrollToRequested?.Invoke(this, GetScrollOffsetForPage(_index, _pageOffset)));
        }
        else if (IsHorizontalMode)
        {
            SuppressSyncWhileRelocating(() =>
                HorizontalScrollToRequested?.Invoke(this, GetHorizontalOffsetForPage(_index, _pageOffset)));
        }
    }

    public void SetContinuousWidth(double width)
    {
        double target = Math.Clamp(width, 320, 1400);
        if (Math.Abs(target - _continuousWidth) < 2 && _continuousItems.Count > 0)
        {
            return;
        }

        _continuousWidth = target;
        if (_continuousItems.Count == 0) return;

        foreach (ContinuousPageItem item in _continuousItems)
        {
            item.Resize(target);
        }

        RecomputeTops();
        if (IsContinuousMode)
        {
            SuppressSyncWhileRelocating(() =>
                ScrollToRequested?.Invoke(this, GetScrollOffsetForPage(_index, _pageOffset)));
        }
    }

    private void ApplyContinuousWidth()
    {
        foreach (ContinuousPageItem item in _continuousItems)
        {
            item.Resize(_continuousWidth);
        }

        RecomputeTops();
    }

    private void RecomputeTops()
    {
        double top = 0;
        foreach (ContinuousPageItem item in _continuousItems)
        {
            item.Top = top;
            top += item.SlotHeight;
        }
    }

    public double GetScrollOffsetForPage(int index, double fraction)
    {
        if (_continuousItems.Count == 0) return 0;
        ContinuousPageItem item = _continuousItems[Math.Clamp(index, 0, _continuousItems.Count - 1)];
        return item.Top + Math.Clamp(fraction, 0, 1) * item.DisplayHeight;
    }

    public void UpdateScrollOffset(double offsetY, double viewportHeight)
    {
        if (_continuousItems.Count == 0 || !IsContinuousMode || _suppressScrollSync) return;

        double probe = offsetY + Math.Max(40, viewportHeight * 0.32);
        int low = 0;
        int high = _continuousItems.Count - 1;
        int found = 0;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (_continuousItems[mid].Top <= probe)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        ContinuousPageItem item = _continuousItems[found];
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
            _sliderValue = _index + 1 + fraction;
            OnPropertyChanged(nameof(SliderValue));
            ScheduleSave();
        }
    }

    /// <summary>Test hook: last horizontal target raised to the view.</summary>
    internal double? LastHorizontalTarget { get; private set; }

    /// <summary>Test hook: current internal page offset.</summary>
    internal double PageOffsetForTest => _pageOffset;

    /// <summary>Rebuilds the display-ordered list (reversed when RTL) and slots.</summary>
    private void RebuildHorizontalDisplay()
    {
        List<ContinuousPageItem> display = _horizontalItems.ToList();
        if (_rightToLeft) display.Reverse();
        HorizontalDisplayItems = display;
        RecomputeLefts();
    }

    private void RecomputeLefts()
    {
        double x = 0;
        foreach (ContinuousPageItem item in _horizontalDisplayItems)
        {
            item.Left = x;
            x += item.DisplayWidth + ContinuousPageItem.Gap;
        }
    }

    public void SetContinuousHeight(double height)
    {
        double target = Math.Clamp(height, 280, 2400);
        if (Math.Abs(target - _continuousHeight) < 2 && _horizontalItems.Count > 0)
        {
            return;
        }

        _continuousHeight = target;
        if (_horizontalItems.Count == 0) return;
        if (IsHorizontalMode) { _suppressScrollSync = true; _relocationVersion++; }
        ApplyContinuousHeight();
        if (IsHorizontalMode)
        {
            SuppressSyncWhileRelocating(() =>
                HorizontalScrollToRequested?.Invoke(this, GetHorizontalOffsetForPage(_index, _pageOffset)));
        }
    }

    private void ApplyContinuousHeight()
    {
        foreach (ContinuousPageItem item in _horizontalItems)
        {
            item.ResizeToHeight(_continuousHeight);
        }

        RecomputeLefts();
    }

    public double GetHorizontalOffsetForPage(int index, double fraction)
    {
        if (_horizontalItems.Count == 0) return 0;
        ContinuousPageItem item = _horizontalItems[Math.Clamp(index, 0, _horizontalItems.Count - 1)];
        return item.Left + Math.Clamp(fraction, 0, 1) * item.DisplayWidth;
    }

    /// <summary>Horizontal counterpart of <see cref="UpdateScrollOffset"/>: maps the
    /// scroll X offset back to the page under the probe point.</summary>
    public void UpdateScrollOffsetX(double offsetX, double viewportWidth)
    {
        if (_horizontalDisplayItems.Count == 0 || !IsHorizontalMode || _suppressScrollSync) return;

        double probe = offsetX + Math.Max(40, viewportWidth * 0.32);
        int low = 0;
        int high = _horizontalDisplayItems.Count - 1;
        int found = 0;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (_horizontalDisplayItems[mid].Left <= probe)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        ContinuousPageItem item = _horizontalDisplayItems[found];
        double fraction = item.DisplayWidth <= 0
            ? 0
            : Math.Clamp((probe - item.Left) / item.DisplayWidth, 0, 1);

        if (item.ReadIndex != _index)
        {
            SetIndex(item.ReadIndex, scrollContinuous: false, offset: fraction);
        }
        else
        {
            _pageOffset = fraction;
            _sliderValue = _index + 1 + fraction;
            OnPropertyChanged(nameof(SliderValue));
            ScheduleSave();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_source is OnlineReaderSource online) { online.DimensionResolved = null; online.QualityChanged = null; }
        _saveTimer.Stop();
        _hintTimer.Stop();
        SaveProgress();
        _lifetime.Cancel();
        _lifetime.Dispose();
        if (_source is IDisposable disposable) disposable.Dispose();
    }
}
