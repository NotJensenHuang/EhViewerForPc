using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerReader.Core.Models;
using EhViewerReader.Core.Online;
using EhViewerReader.Services;

namespace EhViewerReader.ViewModels;

/// <summary>Manages ordered full-search bookmarks and compatible Android transfers.</summary>
public sealed partial class BookmarksViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Func<EhSearchRequest?> _currentRequest;
    [ObservableProperty] private SavedSearchEntry? _selected;
    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private string _editQuery = string.Empty;
    [ObservableProperty] private string _statusText = "支持导入原版“导出数据”的 .db 文件和 JSON 书签。";
    [ObservableProperty] private bool _isBusy;

    /// <summary>Initializes a manager for the current application's saved search collection.</summary>
    public BookmarksViewModel(AppServices services, Func<EhSearchRequest?> currentRequest)
    { _services = services; _currentRequest = currentRequest; Reload(); }
    /// <summary>Gets the displayed bookmarks in their saved order.</summary>
    public ObservableCollection<SavedSearchEntry> Items { get; } = [];
    /// <summary>Gets whether a bookmark can be edited.</summary>
    public bool HasSelection => Selected is not null;
    /// <summary>Gets whether transfer controls can be used.</summary>
    public bool IsIdle => !IsBusy;
    /// <summary>Raised to execute a saved request.</summary>
    public event EventHandler<SavedSearchEntry>? OpenRequested;
    /// <summary>Raised to edit the full request on the search form.</summary>
    public event EventHandler<SavedSearchEntry>? EditRequested;
    /// <summary>Raised when bookmarks have been persisted.</summary>
    public event EventHandler? Changed;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsIdle));
    partial void OnSelectedChanged(SavedSearchEntry? value)
    { EditName = value?.Name ?? ""; EditQuery = value?.ToRequest().Query ?? ""; OnPropertyChanged(nameof(HasSelection)); }
    /// <summary>Refreshes the list without losing selection when another page adds a bookmark.</summary>
    public void Reload()
    {
        string? id = Selected?.Id; Items.Clear();
        foreach (var item in _services.Settings.SavedSearches) Items.Add(item);
        Selected = Items.FirstOrDefault(i => i.Id == id);
    }
    [RelayCommand] private void AddCurrent()
    {
        var request = _currentRequest();
        if (IsBusy) return;
        if (request is null) { StatusText = "图片搜索不能保存为书签，请先浏览或执行普通搜索。"; return; }
        if (Items.Count >= EhBookmarkTransfer.MaxEntries) { StatusText = "书签数量已达上限。"; return; }
        string name = request.EffectiveQuery;
        if (name.Length == 0) name = request.Mode switch
        { EhSearchMode.Subscription => "订阅", EhSearchMode.Popular => "热门", EhSearchMode.Toplist => "排行榜", _ => "首页" };
        name = name.Length > 80 ? name[..80] : name;
        string original = name; int suffix = 2;
        while (Items.Any(i => i.Name == name)) name = original + " (" + suffix++ + ")";
        var entry = SavedSearchEntry.Create(name, request);
        _services.Settings.SavedSearches.Add(entry); Persist("已添加书签，可在下方修改名称。"); Selected = entry;
    }
    [RelayCommand] private void Open(SavedSearchEntry? entry)
    { if ((entry ?? Selected) is { } current) OpenRequested?.Invoke(this, current); }
    [RelayCommand] private void EditOptions() { if (Selected is { } entry) EditRequested?.Invoke(this, entry); }
    [RelayCommand] private void SaveEdit()
    {
        if (Selected is not { } entry) return;
        string name = EditName.Trim();
        if (name.Length is < 1 or > 1024) { StatusText = "请输入 1–1024 字的书签名称。"; return; }
        if (Items.Any(e => !ReferenceEquals(e, entry) && e.Name == name)) { StatusText = "书签名称已存在。"; return; }
        var request = entry.ToRequest(); request.Query = EditQuery.Trim();
        entry.Name = name; entry.Query = request.Query; entry.Search = request;
        Persist("书签已保存。");
    }
    [RelayCommand] private void Delete()
    {
        if (Selected is not { } entry) return;
        _services.Settings.SavedSearches.Remove(entry); Persist("书签已删除。");
    }
    [RelayCommand] private void MoveUp() => Move(-1);
    [RelayCommand] private void MoveDown() => Move(1);
    private void Move(int direction)
    {
        if (Selected is not { } entry) return;
        var list = _services.Settings.SavedSearches; int index = list.IndexOf(entry), target = index + direction;
        if (index < 0 || target < 0 || target >= list.Count) return;
        list.RemoveAt(index); list.Insert(target, entry); Persist("书签顺序已保存。");
    }
    private void Persist(string message)
    { _services.TrySaveSettings(out _); Reload(); StatusText = message; Changed?.Invoke(this, EventArgs.Empty); }

    /// <summary>Parses the entire backup in the background before merging, so errors leave settings intact.</summary>
    public async Task ImportAsync(string path)
    {
        if (IsBusy) return; IsBusy = true; StatusText = "正在读取书签…";
        try
        {
            var entries = await Task.Run(() => EhBookmarkTransfer.Read(path));
            var result = EhBookmarkTransfer.Merge(_services.Settings.SavedSearches, entries);
            Persist($"导入 {result.Added} 项，同名跳过 {result.Skipped} 项。");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or
            Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or FormatException or OverflowException or InvalidCastException)
        { StatusText = "导入失败：" + error.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>Writes a snapshot to a selected stream using the original database or JSON format.</summary>
    public async Task<bool> ExportAsync(Stream output, bool json)
    {
        if (IsBusy) return false; IsBusy = true;
        var snapshot = Items.Select(i => { var clone = SavedSearchEntry.Create(i.Name, i.ToRequest()); clone.Time = i.Time; return clone; }).ToArray();
        string temporary = Path.Combine(_services.Paths.BaseDirectory, "bookmarks-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await Task.Run(async () =>
            {
                if (json) EhBookmarkTransfer.WriteJson(output, snapshot);
                else
                {
                    EhBookmarkTransfer.WriteDatabase(temporary, snapshot);
                    await using var input = File.OpenRead(temporary); await input.CopyToAsync(output);
                }
                await output.FlushAsync();
            });
            StatusText = $"已导出 {snapshot.Length} 项书签。";
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { StatusText = "导出失败：" + error.Message; return false; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            IsBusy = false;
        }
    }

    /// <summary>Saves a complete request back to the bookmark being edited.</summary>
    public void UpdateOptions(SavedSearchEntry entry, EhSearchRequest request)
    {
        if (!_services.Settings.SavedSearches.Contains(entry)) return;
        entry.Search = request.Clone(); entry.Query = request.Query; Persist("搜索条件已更新。"); Selected = entry;
    }
}
