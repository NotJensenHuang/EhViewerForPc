using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using EhViewerForPc.Controls;
using EhViewerForPc.Core.Format;
using EhViewerForPc.Services;

namespace EhViewerForPc.Views;

public partial class ReaderPage
{
    private bool _imageActionRunning;

    private void OnImageContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        ReaderTranslationImage? image = sender switch
        {
            ZoomPanView single => single.TranslationImage,
            AsyncImage continuous => continuous.TranslationImage,
            _ => null,
        };
        if (_disposed || _imageActionRunning || sender is not Control target || image is not { } source) return;

        // 固定右键点中的图片，连续阅读滚动或回收控件后仍使用打开菜单时的源页。
        var copy = new MenuItem { Header = "复制图片" };
        copy.Click += async (_, _) => await RunImageActionAsync("复制图片", () => CopyImageAsync(source));
        var save = new MenuItem { Header = "另存为…" };
        save.Click += async (_, _) => await RunImageActionAsync("另存为", () => SaveImageAsync(source));
        var reveal = new MenuItem { Header = "打开图片位置" };
        reveal.Click += async (_, _) => await RunImageActionAsync("打开图片位置", () => Task.Run(() =>
        {
            using FileStream input = OpenImage(source);
            PlatformLauncher.RevealFile(source.Path);
        }));

        var flyout = new MenuFlyout { ItemsSource = new[] { copy, save, reveal } };
        flyout.ShowAt(target, showAtPointer: e.TryGetPosition(target, out _));
        e.Handled = true;
    }

    private async Task RunImageActionAsync(string action, Func<Task> execute)
    {
        if (_disposed || _imageActionRunning) return;
        _imageActionRunning = true;
        var shell = (TopLevel.GetTopLevel(this) as MainWindow)?.Shell;
        try { await execute(); }
        catch (Exception error)
        {
            _viewModel.Services.Log.Warn($"阅读器图片操作失败：{action}", error);
            if (!_disposed) shell?.Notify($"{action}失败：{error.Message}");
        }
        finally { _imageActionRunning = false; }
    }

    private async Task CopyImageAsync(ReaderTranslationImage source)
    {
        var clipboard = Clipboard ?? throw new InvalidOperationException("剪贴板不可用。");
        using DecodedImage image = await Task.Run(() =>
        {
            using FileStream input = OpenImage(source);
            return _viewModel.Services.Decoder.DecodeReaderImage(source.Path, input, CancellationToken.None);
        });
        if (_disposed) return;
        await clipboard.SetBitmapAsync(image.Bitmap);
        await clipboard.FlushAsync();
        if (!_disposed) (TopLevel.GetTopLevel(this) as MainWindow)?.Shell?.Notify("图片已复制");
    }

    private async Task SaveImageAsync(ReaderTranslationImage source)
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("文件选择器不可用。");
        string extension = await Task.Run(() => GetImageExtension(source));
        if (_disposed) return;
        using IStorageFile? file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "另存图片",
            SuggestedFileName = Path.GetFileNameWithoutExtension(source.Path) + extension,
            DefaultExtension = extension.TrimStart('.'),
            ShowOverwritePrompt = false,
        });
        if (_disposed || file is null) return;
        string path = file.TryGetLocalPath() ?? throw new IOException("请选择本地文件位置。");
        await Task.Run(() => SaveOriginalAsync(source, path));
        if (!_disposed) (TopLevel.GetTopLevel(this) as MainWindow)?.Shell?.Notify("图片已保存");
    }

    private static string GetImageExtension(ReaderTranslationImage source)
    {
        using FileStream input = OpenImage(source);
        Span<byte> header = stackalloc byte[16];
        int length = input.ReadAtLeast(header, header.Length, false);
        return ImageSignature.Detect(header[..length]) switch
        {
            ImageKind.Jpeg => ".jpg",
            ImageKind.Png => ".png",
            ImageKind.Gif => ".gif",
            ImageKind.Webp => ".webp",
            _ => Path.GetExtension(source.Path),
        };
    }

    private static async Task SaveOriginalAsync(ReaderTranslationImage source, string path)
    {
        using FileStream input = OpenImage(source);
        if (File.Exists(path)) throw new IOException("此文件已存在，请选择新文件名。");
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".ehviewer-{Guid.NewGuid():N}.tmp");
        var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        try
        {
            await using (output.ConfigureAwait(false))
                await input.CopyToAsync(output).ConfigureAwait(false);
            // 禁止覆盖同名文件，包括源页经别名路径或其他进程同时创建的文件。
            File.Move(temporary, path);
        }
        finally { File.Delete(temporary); }
    }

    private static FileStream OpenImage(ReaderTranslationImage source) => source.TryOpenCurrentSource() ??
        throw new IOException("本页图片已变更，请重新打开这一页。");
}
