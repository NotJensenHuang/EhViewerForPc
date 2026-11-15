using Avalonia.Headless.XUnit;
using EhViewerForPc.Core.Storage;
using EhViewerForPc.ImageSearch;
using EhViewerForPc.Services;
using EhViewerForPc.ViewModels;

namespace EhViewerForPc.ImageSearch.Ui.Tests;

public sealed class VersionIdentityIntegrationTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PageChecksPreserveDuplicateCopyIdentityAndPublishedIndexHits(bool multipleSources)
    {
        using var fixture = new ReaderRetryIntegrationTests.Fixture();
        var source = fixture.Services.Settings.Sources.Single();
        CopyFixtureTo(Path.Combine(source.RootPath, "42-another-copy"));
        if (multipleSources)
        {
            string backup = Path.Combine(Directory.GetParent(source.RootPath)!.FullName, "backup");
            CopyFixtureTo(Path.Combine(backup, "42-backup"));
            fixture.Services.Settings.AddSource(backup);
        }
        using var library = new MainViewModel(fixture.Services);
        await library.InitializeAsync();
        var before = library.SnapshotAllWorks().SelectMany(item => item.Versions).ToArray();
        var identities = before.ToDictionary(info => info.FolderPath, info => (info.StableId, info.IsDuplicateGid), PathIdentity.Comparer);
        Assert.True(identities[fixture.Folder].IsDuplicateGid);
        fixture.Services.Settings.ImageSearch.Enabled = true;
        var snapshot = before.Select(info => new ImageSearchGallery(info.SourceId, info.StableId, info.WorkId, info.FolderPath)).ToArray();
        await fixture.Services.ImageSearch.BuildAsync(snapshot, null, CancellationToken.None,
            options: new IndexBuildOptions { Workers = 2, UseGpu = false });
        byte[] query = File.ReadAllBytes(Path.Combine(fixture.Folder, "00000001.png"));
        var hitsBefore = await fixture.Services.ImageSearch.SearchAsync(query, null, before, CancellationToken.None);
        Assert.Contains(hitsBefore, hit => hit.Gallery.FolderPath == fixture.Folder);
        var item = library.SnapshotAllWorks().Single(work => work.Versions.Any(info => info.FolderPath == fixture.Folder));
        using var versions = new VersionsViewModel(item, fixture.Services, library.ApplyScannedCopy);

        await versions.CheckPagesCommand.ExecuteAsync(null);

        var after = library.SnapshotAllWorks().SelectMany(work => work.Versions).ToArray();
        Assert.All(after, info => Assert.Equal(identities[info.FolderPath], (info.StableId, info.IsDuplicateGid)));
        Assert.Equal(before.Length, after.Length);
        var hitsAfter = await fixture.Services.ImageSearch.SearchAsync(query, null, after, CancellationToken.None);
        Assert.Equal(hitsBefore.Select(hit => hit.FilePath), hitsAfter.Select(hit => hit.FilePath));

        void CopyFixtureTo(string folder)
        {
            Directory.CreateDirectory(folder);
            foreach (string file in Directory.EnumerateFiles(fixture.Folder))
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        }
    }
}
