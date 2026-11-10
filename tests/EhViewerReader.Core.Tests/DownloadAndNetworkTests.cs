using EhViewerReader.Core.Download;
using EhViewerReader.Core.Models;
using EhViewerReader.Core.Online;
using EhViewerReader.Core.Storage;

namespace EhViewerReader.Core.Tests;

public sealed class DownloadAndNetworkTests
{
    [Fact]
    public void FolderNameIsSanitizedAndBounded()
    {
        string name = DownloadManager.SanitizeFolderName("123-a:b/c*d?e\"f<g>h|i\u0001");

        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('*', name);
        Assert.DoesNotContain('?', name);
        Assert.DoesNotContain('"', name);
        Assert.DoesNotContain('<', name);
        Assert.DoesNotContain('>', name);
        Assert.DoesNotContain('|', name);
        Assert.True(name.Length <= 120);
        Assert.StartsWith("123-", name);
    }

    [Fact]
    public void BlankFolderNameFallsBack()
    {
        Assert.Equal("untitled", DownloadManager.SanitizeFolderName("   "));
        Assert.Equal("untitled", DownloadManager.SanitizeFolderName("..."));
    }

    [Fact]
    public void LabelBecomesSubFolder()
    {
        var task = new DownloadTask { Gid = 4319211, Title = "Some: Gallery", Label = "art" };
        string root = Path.Combine("C:", "Downloads");

        string path = DownloadManager.BuildFolderPath(task, root);

        Assert.Equal(Path.Combine(root, "art", "4319211-Some_ Gallery"), path);
    }

    [Fact]
    public void DownloadFileRoundTripsThroughSourceGenContext()
    {
        string directory = Path.Combine(Path.GetTempPath(), "EhDownloadTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "downloads.json");

        try
        {
            var file = new DownloadFile();
            file.Tasks.Add(new DownloadTask
            {
                Gid = 42,
                Token = "abc",
                Title = "T",
                State = DownloadState.Paused,
                PageCount = 10,
                DownloadedPages = 4,
                FailedPages = [3, 7],
            });
            file.Archive.Add(new DownloadArchiveEntry { Gid = 7, Title = "done", PageCount = 3 });

            Assert.True(JsonFileStore.Save(path, file, StorageJsonContext.Default.DownloadFile, out string? error), error);
            Assert.Contains("\"Paused\"", File.ReadAllText(path));

            DownloadFile loaded = JsonFileStore.LoadOrDefault(
                path, StorageJsonContext.Default.DownloadFile, () => new DownloadFile(), out error);

            DownloadTask task = Assert.Single(loaded.Tasks);
            Assert.Equal(DownloadState.Paused, task.State);
            Assert.Equal([3, 7], task.FailedPages);
            Assert.Single(loaded.Archive);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void QueuePersistsAndReloadsWithoutStarting()
    {
        string directory = Path.Combine(Path.GetTempPath(), "EhQueueTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var paths = new AppPaths(directory);
            paths.EnsureCreated();

            var settings = new AppSettings();
            settings.Download.AutoStart = false;
            settings.Download.RootPath = Path.Combine(directory, "downloads");

            using var client = new EhClient(settings);
            using (var manager = new DownloadManager(paths, settings, client))
            {
                DownloadTask task = manager.Enqueue(
                    new EhGalleryDetails { Gid = 1234, Token = "tok", Title = "Queue: Test", Pages = 12 },
                    "label",
                    startImmediately: false);

                Assert.Equal(DownloadState.Pending, task.State);
                Assert.EndsWith(Path.Combine("label", "1234-Queue_ Test"), task.FolderPath);
                Assert.True(File.Exists(paths.DownloadTasksFile));
            }

            using (var reloaded = new DownloadManager(paths, settings, client))
            {
                DownloadTask task = Assert.Single(reloaded.Tasks);
                Assert.Equal(1234, task.Gid);
                Assert.Equal("tok", task.Token);
                Assert.Equal(12, task.PageCount);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CookieStringParsesPairs()
    {
        Dictionary<string, string> cookies = EhSession.ParseCookieString(
            "ipb_member_id=123; ipb_pass_hash=deadbeef; igneous=xyz; malformed");

        Assert.Equal(3, cookies.Count);
        Assert.Equal("123", cookies["ipb_member_id"]);
        Assert.Equal("deadbeef", cookies["ipb_pass_hash"]);
        Assert.Equal("xyz", cookies["igneous"]);
    }

    [Fact]
    public void HostsFileAcceptsBothOrders()
    {
        Dictionary<string, System.Net.IPAddress[]> hosts = EhHttp.ParseHosts("""
            # comment
            1.2.3.4 e-hentai.org
            exhentai.org 5.6.7.8
            bad line
            """);

        Assert.Equal(2, hosts.Count);
        Assert.Equal("1.2.3.4", hosts["e-hentai.org"][0].ToString());
        Assert.Equal("5.6.7.8", hosts["exhentai.org"][0].ToString());
    }
}
