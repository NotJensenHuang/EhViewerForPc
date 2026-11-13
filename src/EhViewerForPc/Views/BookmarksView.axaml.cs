using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EhViewerForPc.ViewModels;

namespace EhViewerForPc.Views;

/// <summary>Provides the formal bookmark manager and system file transfer pickers.</summary>
public partial class BookmarksView : UserControl
{
    /// <summary>Initializes the bookmark management surface.</summary>
    public BookmarksView() => InitializeComponent();
    private BookmarksViewModel? ViewModel => DataContext as BookmarksViewModel;
    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider || ViewModel is not { IsBusy: false } vm) return;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "导入原版书签备份", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("EhViewer 备份") { Patterns = ["*.db", "*.json"] }, FilePickerFileTypes.All] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await vm.ImportAsync(path);
    }
    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider || ViewModel is not { IsBusy: false } vm) return;
        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "导出书签", SuggestedFileName = "EhViewer-bookmarks.db",
            DefaultExtension = "db", ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("原版兼容数据库") { Patterns = ["*.db"] }, new FilePickerFileType("JSON 书签") { Patterns = ["*.json"] }] });
        if (file is null) return;
        string? temporary = null;
        try
        {
            bool json = file.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            if (file.TryGetLocalPath() is { } path)
            {
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                bool complete;
                await using (var output = File.Create(temporary)) complete = await vm.ExportAsync(output, json);
                if (complete) File.Move(temporary, path, overwrite: true);
            }
            else
            {
                await using var stream = await file.OpenWriteAsync(); stream.SetLength(0); await vm.ExportAsync(stream, json);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { vm.StatusText = "导出失败：" + error.Message; }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
    private void OnOpenSelected(object? sender, TappedEventArgs e) => ViewModel?.OpenCommand.Execute(null);
}
