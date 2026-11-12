using Avalonia.Controls;
using EhViewerReader.ViewModels;
using FluentAvalonia.UI.Controls;

namespace EhViewerReader.Services;

internal sealed record NavigationTarget(Control View, NavItem Section);

/// <summary>Explicit page instances keep navigation compatible with NativeAOT.</summary>
internal sealed class NavigationPageFactory : IFANavigationPageFactory
{
    // FAFrame 3.0 clears history entries' instances, then falls back to GetPage(Type)
    // on a cache miss. The frame's own stack supplies the context during Navigating.
    public NavigationTarget? RestoreTarget { get; set; }
    public Control GetPage(Type srcType) => RestoreTarget?.View
        ?? throw new InvalidOperationException("No navigation context was supplied by the frame.");
    public Control GetPageFromObject(object target) => ((NavigationTarget)target).View;
}
