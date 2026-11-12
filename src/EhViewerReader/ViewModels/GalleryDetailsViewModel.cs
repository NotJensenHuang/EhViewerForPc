using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerReader.Core.Models;
using EhViewerReader.Services;

namespace EhViewerReader.ViewModels;

public sealed record GalleryMetadataRow(string Label, string Value);

/// <summary>The presentation shared by online and local gallery details.</summary>
public sealed partial class GalleryDetailsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Action<string> _search;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasJapaneseTitle))] private string _japaneseTitle = string.Empty;
    [ObservableProperty] private string _pagesText = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasStatus))] private string _statusText = string.Empty;
    [ObservableProperty] private IImageRequest? _coverRequest;
    [ObservableProperty] private bool _isLoading;

    public GalleryDetailsViewModel(AppServices services, Action<string> search)
    {
        _services = services;
        _search = search;
    }

    public ObservableCollection<TagChip> Tags { get; } = [];
    public ObservableCollection<GalleryMetadataRow> Metadata { get; } = [];
    public bool HasJapaneseTitle => JapaneseTitle.Length > 0;
    public bool HasStatus => StatusText.Length > 0;

    public void Apply(WorkRecord work, IImageRequest? cover, string? pagesText = null,
        IEnumerable<string>? fallbackTags = null)
    {
        Title = work.Title;
        JapaneseTitle = work.Metadata?.JapaneseTitle is { } japanese && japanese != Title ? japanese : string.Empty;
        PagesText = pagesText ?? (work.PageCount > 0 ? $"{work.PageCount} 页" : string.Empty);
        CoverRequest = cover;
        Metadata.Clear();
        Add("分类", work.Metadata?.Category);
        Add("语言", work.Metadata?.Language);
        Add("评分", work.Metadata is { Rating: > 0 } m ? m.Rating.ToString("F2") : null);
        Add("发布", work.Metadata?.Posted);
        Add("上传者", work.Metadata?.Uploader);
        Add("大小", work.Metadata?.FileSize);
        Add("GID", work.Gid?.ToString());
        Tags.Clear();
        foreach (string term in work.Metadata?.Tags ?? fallbackTags ?? [])
        {
            int colon = term.IndexOf(':');
            string ns = colon < 0 ? string.Empty : term[..colon];
            string name = colon < 0 ? term : term[(colon + 1)..];
            string? translated = _services.Settings.Online.ShowTagTranslations
                ? _services.TagTranslation.Translate(ns, name) : null;
            string label = translated is null ? term : (_services.TagTranslation.Namespace(ns) is { Length: > 0 } n ? n + ":" : string.Empty) + translated;
            Tags.Add(new TagChip { Term = term, Display = label, NamespaceLabel = ns, Command = new RelayCommand(() => _search(term)) });
        }
    }

    private void Add(string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) Metadata.Add(new(label, value));
    }
}
