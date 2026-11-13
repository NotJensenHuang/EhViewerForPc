using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EhViewerForPc.Core.Online;
using EhViewerForPc.Services;

namespace EhViewerForPc.ViewModels;

/// <summary>
/// Archive download (archiver.php). The site builds a ZIP from the original
/// files or a resampled set; large archives additionally require an assigned
/// H@H client. The flow is: load options → request → save the returned link.
/// </summary>
public sealed partial class ArchiveViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly EhGalleryDetails _details;
    private readonly IOnlineHost _host;

    private bool _loading;
    private bool _working;
    private string _statusText = "载入归档选项…";
    private string _detailText = string.Empty;
    private bool _needsSignIn;
    private bool _needsHath;
    private string _downloadUrl = string.Empty;
    private FeedOption? _selectedOption;
    private double _progress;
    private string _progressText = string.Empty;
    private EhArchivePage? _page;
    private string? _savedFolder;

    public ArchiveViewModel(AppServices services, IOnlineHost host, EhGalleryDetails details)
    {
        _services = services;
        _host = host;
        _details = details;
        Title = "归档下载 · " + details.DisplayTitle;
    }

    public ObservableCollection<FeedOption> Options { get; } = [];

    private readonly CancellationTokenSource _lifetime = new();
    public EhGalleryDetails Details => _details;
    public string Title { get; }

    /// <summary>Gallery title without the window's action prefix.</summary>
    public string GalleryTitle => _details.DisplayTitle;
    public string Subtitle => $"#{_details.Gid} · 归档由站点生成，可能需要 H@H 客户端";

    public bool IsLoading
    {
        get => _loading;
        private set
        {
            if (SetProperty(ref _loading, value)) OnPropertyChanged(nameof(CanRequest));
        }
    }

    public bool IsWorking
    {
        get => _working;
        private set
        {
            if (!SetProperty(ref _working, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanRequest));
            OnPropertyChanged(nameof(ShowProgress));
        }
    }

    public bool ShowProgress => _working;

    public bool CanRequest => !_loading && !_working && Options.Count > 0 && !_needsSignIn;

    public bool HasOptions => Options.Count > 0;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set
        {
            if (SetProperty(ref _detailText, value)) OnPropertyChanged(nameof(HasDetail));
        }
    }

    public bool HasDetail => _detailText.Length > 0;

    public bool NeedsSignIn
    {
        get => _needsSignIn;
        private set
        {
            if (SetProperty(ref _needsSignIn, value)) OnPropertyChanged(nameof(CanRequest));
        }
    }

    public bool NeedsHath
    {
        get => _needsHath;
        private set => SetProperty(ref _needsHath, value);
    }

    public string DownloadUrl
    {
        get => _downloadUrl;
        private set
        {
            if (SetProperty(ref _downloadUrl, value)) OnPropertyChanged(nameof(HasDownloadUrl));
        }
    }

    public bool HasDownloadUrl => _downloadUrl.Length > 0;

    public FeedOption? SelectedOption
    {
        get => _selectedOption;
        set => SetProperty(ref _selectedOption, value);
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public async Task InitializeAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        StatusText = "载入归档选项…";
        try
        {
            EhArchivePage page = await _services.Online
                .GetArchivePageAsync(_details, _lifetime.Token)
                .ConfigureAwait(true);

            ApplyPage(page);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { StatusText = "已取消"; }
        catch (Exception ex) when (ex is EhException or HttpRequestException)
        {
            StatusText = "载入失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal void ApplyPage(EhArchivePage page)
    {
        _page = page;
        NeedsSignIn = page.RequiresSignIn;
        // The sign-in card already explains the state; do not repeat it.
        DetailText = page.RequiresSignIn ? string.Empty : page.Message;

        Options.Clear();
        foreach (EhArchiveOption option in page.Options)
        {
            Options.Add(new FeedOption(option.Key, option.Label));
        }

        SelectedOption = Options.FirstOrDefault();
        OnPropertyChanged(nameof(HasOptions));

        if (page.Generated && page.DownloadUrl.Length > 0)
        {
            DownloadUrl = page.DownloadUrl;
            StatusText = "站点已生成归档，可直接下载";
        }
        else if (page.RequiresSignIn)
        {
            StatusText = "归档下载需要登录 E-Hentai 账号";
        }
        else
        {
            StatusText = Options.Count > 0
                ? "选择格式后请求归档；站点生成链接后保存"
                : page.Message.Length > 0 ? page.Message : "没有可用的归档格式";
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await InitializeAsync();

    [RelayCommand]
    private async Task RequestAsync()
    {
        if (_page is null || SelectedOption is null) return;
        if (NeedsSignIn)
        {
            StatusText = "归档下载需要登录 E-Hentai 账号";
            return;
        }

        IsWorking = true;
        NeedsHath = false;
        DetailText = string.Empty;
        StatusText = "正在请求归档…";
        Progress = 0;
        ProgressText = string.Empty;

        try
        {
            EhArchiveResult result = await _services.Online
                .RequestArchiveAsync(_details, _page.Or, SelectedOption.Key, _lifetime.Token)
                .ConfigureAwait(true);

            NeedsHath = result.NeedsHathClient;
            DetailText = result.Message;
            if (result.DownloadUrl.Length > 0)
            {
                DownloadUrl = result.DownloadUrl;
                StatusText = "归档已生成，开始下载…";
                await DownloadAsync(result.DownloadUrl).ConfigureAwait(true);
            }
            else if (result.Success)
            {
                StatusText = result.Queued ? "归档请求已提交" : result.Message;
            }
            else
            {
                StatusText = result.Message.Length > 0 ? result.Message : "归档请求失败";
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { StatusText = "已取消"; }
        catch (Exception ex) when (ex is EhException or HttpRequestException or IOException)
        {
            StatusText = "归档失败：" + ex.Message;
        }
        finally
        {
            IsWorking = false;
        }
    }

    public async Task DownloadAsync(string url)
    {
        IsWorking = true;
        Progress = 0;
        try
        {
            using var storageLease = _services.Downloads.AcquireArchiveWrite();
            string folder = Path.Combine(_services.Downloads.RootPath, "archives");
            _savedFolder = folder;
            Directory.CreateDirectory(folder);
            string fileName = FileNameFromUrl(url);
            string target = Path.Combine(folder, fileName);
            string unique = target;
            for (int index = 2; File.Exists(unique); index++)
            {
                unique = Path.Combine(folder, Path.GetFileNameWithoutExtension(fileName) + " (" + index + ").zip");
            }

            var progress = new Progress<long>(bytes =>
            {
                ProgressText = (bytes / 1024d / 1024d).ToString("F1", CultureInfo.InvariantCulture) + " MB";
            });

            StatusText = "正在下载归档…";
            await _services.Online
                .DownloadArchiveToFileAsync(url, unique, progress, _lifetime.Token)
                .ConfigureAwait(true);

            var info = new FileInfo(unique);
            StatusText = $"归档已保存：{unique}";
            DetailText = $"大小 {(info.Length / 1024d / 1024d):F1} MB";
            Progress = 100;
            ProgressText = "完成";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { StatusText = "已取消"; }
        catch (Exception ex) when (ex is EhException or IOException or HttpRequestException)
        {
            StatusText = "下载失败：" + ex.Message;
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task SaveAgainAsync()
    {
        if (DownloadUrl.Length == 0) return;
        await DownloadAsync(DownloadUrl);
    }

    [RelayCommand]
    private void OpenInBrowser()
    {
        string url = DownloadUrl.Length > 0
            ? DownloadUrl
            : _details.ArchiveUrl.Length > 0
                ? _details.ArchiveUrl
                : EhUrls.Archiver(_host.Site, _details.Gid, _details.Token);
        _host.OpenExternal(url);
    }

    [RelayCommand]
    private void OpenHathPage() => _host.OpenExternal(EhUrls.HathClient(_host.Site));

    [RelayCommand]
    private void OpenDownloadFolder()
    {
        string folder = _savedFolder ?? Path.Combine(_services.Downloads.RootPath, "archives");
        try
        {
            Directory.CreateDirectory(folder);
            _host.OpenExternal(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "无法打开目录：" + ex.Message;
        }
    }

    private static string FileNameFromUrl(string url)
    {
        string name = Path.GetFileName(Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.AbsolutePath : url);
        if (string.IsNullOrWhiteSpace(name)) name = "archive.zip";
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name += ".zip";
        return name;
    }
    public void Dispose() { _lifetime.Cancel(); }
}
