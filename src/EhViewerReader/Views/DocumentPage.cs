using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace EhViewerReader.Views;

/// <summary>Content that can be hosted in the shell or detached into a desktop window.</summary>
public class DocumentPage : UserControl, IDisposable
{
    public string Title { get; set; } = string.Empty;
    public event EventHandler? CloseRequested;
    protected Window? HostWindow => TopLevel.GetTopLevel(this) as Window;
    protected IClipboard? Clipboard => TopLevel.GetTopLevel(this)?.Clipboard;
    protected IStorageProvider StorageProvider => TopLevel.GetTopLevel(this)!.StorageProvider;
    protected WindowState WindowState
    {
        get => HostWindow?.WindowState ?? WindowState.Normal;
        set { if (HostWindow is { } window) window.WindowState = value; }
    }
    public void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
    public virtual void Dispose() { }
}

