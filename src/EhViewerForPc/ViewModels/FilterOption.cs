using CommunityToolkit.Mvvm.ComponentModel;
using EhViewerForPc.Core.Models;

namespace EhViewerForPc.ViewModels;

public sealed class FilterOption : ObservableObject
{
    private bool _isActive;

    public FilterOption(LibraryFilter value, string label)
    {
        Value = value;
        Label = label;
    }

    public LibraryFilter Value { get; }
    public string Label { get; }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}
