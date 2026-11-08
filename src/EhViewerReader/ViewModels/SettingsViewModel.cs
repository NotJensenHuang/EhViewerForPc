using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EhViewerReader.Core.Models;
using EhViewerReader.Services;

namespace EhViewerReader.ViewModels;

/// <summary>One editable row in the settings source list.</summary>
public sealed class SourceRowViewModel : ObservableObject
{
    private bool _enabled;

    public SourceRowViewModel(LibrarySource source)
    {
        Source = source;
        _enabled = source.Enabled;
    }

    public LibrarySource Source { get; }
    public string DisplayName => Source.DisplayName;
    public string RootPath => Source.RootPath;
    public bool IsMissing => !Source.Exists;
    public string StatusText => Source.Exists ? string.Empty : "目录不可用";

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetProperty(ref _enabled, value)) return;
            Source.Enabled = value;
        }
    }
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private int _themeIndex;
    private int _modeIndex;
    private int _fitIndex;
    private int _backgroundIndex;
    private bool _rightToLeft;
    private double _maxMegapixels;
    private double _pageCacheMegabytes;
    private double _coverDecodeWidth;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        AppSettings settings = services.Settings;
        _themeIndex = (int)settings.Theme;
        _modeIndex = (int)settings.DefaultReadingMode;
        _fitIndex = (int)settings.DefaultFitMode;
        _backgroundIndex = (int)settings.ReaderBackground;
        _rightToLeft = settings.RightToLeft;
        _maxMegapixels = settings.MaxDecodeMegapixels;
        _pageCacheMegabytes = settings.PageCacheMegabytes;
        _coverDecodeWidth = settings.CoverDecodeWidth;

        foreach (LibrarySource source in settings.Sources)
        {
            Sources.Add(new SourceRowViewModel(source));
        }
    }

    public string[] ThemeLabels { get; } = ["跟随系统", "深色", "浅色"];
    public string[] ModeLabels { get; } = ["单页", "纵向连续", "横向连续"];
    public string[] FitLabels { get; } = ["适应页面", "适应宽度", "适应高度", "原始大小"];
    public string[] BackgroundLabels { get; } = ["黑色", "深灰", "灰色", "白色"];

    public ObservableCollection<SourceRowViewModel> Sources { get; } = [];

    public bool SourcesChanged { get; private set; }

    public bool HasSources => Sources.Count > 0;

    public int ThemeIndex
    {
        get => _themeIndex;
        set
        {
            if (!SetProperty(ref _themeIndex, Math.Clamp(value, 0, 2))) return;
            App.ApplyTheme((AppTheme)_themeIndex);
        }
    }

    public int ModeIndex
    {
        get => _modeIndex;
        set => SetProperty(ref _modeIndex, Math.Clamp(value, 0, 2));
    }

    public int FitIndex
    {
        get => _fitIndex;
        set => SetProperty(ref _fitIndex, Math.Clamp(value, 0, 3));
    }

    public int BackgroundIndex
    {
        get => _backgroundIndex;
        set => SetProperty(ref _backgroundIndex, Math.Clamp(value, 0, 3));
    }

    public bool RightToLeft
    {
        get => _rightToLeft;
        set => SetProperty(ref _rightToLeft, value);
    }

    public double MaxMegapixels
    {
        get => _maxMegapixels;
        set
        {
            if (SetProperty(ref _maxMegapixels, Math.Round(value))) OnPropertyChanged(nameof(MaxMegapixelsLabel));
        }
    }

    public string MaxMegapixelsLabel => $"{_maxMegapixels:F0} MP";

    public double PageCacheMegabytes
    {
        get => _pageCacheMegabytes;
        set
        {
            if (SetProperty(ref _pageCacheMegabytes, Math.Round(value))) OnPropertyChanged(nameof(PageCacheLabel));
        }
    }

    public string PageCacheLabel => $"{_pageCacheMegabytes:F0} MB";

    public double CoverDecodeWidth
    {
        get => _coverDecodeWidth;
        set
        {
            if (SetProperty(ref _coverDecodeWidth, Math.Round(value))) OnPropertyChanged(nameof(CoverDecodeLabel));
        }
    }

    public string CoverDecodeLabel => $"{_coverDecodeWidth:F0} px";

    public string DataDirectory => _services.Paths.BaseDirectory;

    public void AddSource(string path, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        LibrarySource source = _services.Settings.AddSource(path, name);
        SourcesChanged = true;
        SourceRowViewModel? row = Sources.FirstOrDefault(r =>
            string.Equals(r.Source.Id, source.Id, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            Sources.Add(new SourceRowViewModel(source) { Enabled = source.Enabled });
        }
        else if (source.Enabled)
        {
            row.Enabled = true;
        }

        OnPropertyChanged(nameof(HasSources));
    }

    public void RemoveSource(SourceRowViewModel row)
    {
        if (row is null) return;
        Sources.Remove(row);
        SourcesChanged = true;
        OnPropertyChanged(nameof(HasSources));
    }

    public void Apply()
    {
        AppSettings settings = _services.Settings;

        // The rows hold the same LibrarySource instances, so enabling/disabling
        // is already applied. Removing a row must also drop it from settings and
        // from the per-work preferred-source map.
        var keep = new HashSet<string>(Sources.Select(s => s.Source.Id), StringComparer.OrdinalIgnoreCase);
        foreach (LibrarySource source in settings.Sources.ToArray())
        {
            if (!keep.Contains(source.Id))
            {
                settings.RemoveSource(source.Id);
            }
        }

        settings.Theme = (AppTheme)_themeIndex;
        settings.DefaultReadingMode = (ReadingMode)_modeIndex;
        settings.DefaultFitMode = (FitMode)_fitIndex;
        settings.ReaderBackground = (ReaderBackground)_backgroundIndex;
        settings.RightToLeft = _rightToLeft;
        settings.MaxDecodeMegapixels = (int)_maxMegapixels;
        settings.PageCacheMegabytes = (int)_pageCacheMegabytes;
        settings.CoverDecodeWidth = (int)_coverDecodeWidth;
        _services.TrySaveSettings(out _);
        App.ApplyTheme(settings.Theme);
    }

    public void OpenDataDirectory()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + _services.Paths.BaseDirectory + "\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _services.Log.Warn("Open data directory failed", ex);
        }
    }

    public void OpenSource(SourceRowViewModel row)
    {
        if (row is null || !row.Source.Exists) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + row.Source.RootPath + "\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _services.Log.Warn("Open source folder failed", ex);
        }
    }
}
