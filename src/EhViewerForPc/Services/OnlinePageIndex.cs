using EhViewerForPc.Core.Online;

namespace EhViewerForPc.Services;

/// <summary>
/// Resolves viewer page references by 1-based page number, fetching only the
/// preview pagination that contains the requested page. Group fetches are
/// shared (one HTTP request per preview page, no matter how many callers) while
/// each caller can cancel its own wait — cancelling one observer must not cancel
/// the transfer another page still needs.
///
/// The reference cache lives for one reading session: direct image URLs are
/// short-lived, so nothing here is persisted.
/// </summary>
public sealed class OnlinePageIndex : IDisposable
{
    private readonly Func<int, CancellationToken, Task<EhPageList>> _fetch;
    private readonly Dictionary<int, EhPageRef> _pages = new();
    private readonly Dictionary<int, Task<bool>> _groups = new();
    private readonly SemaphoreSlim _groupGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly int _expectedPages;
    private int _pageSize;
    private int _totalPages;
    private int _lastGroupSeen = -1;
    private bool _complete;
    private bool _disposed;

    public OnlinePageIndex(
        int expectedPages,
        Func<int, CancellationToken, Task<EhPageList>> fetch)
    {
        _expectedPages = Math.Max(0, expectedPages);
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
    }

    /// <summary>Builds an index backed by the live client (one gallery, one token).</summary>
    public static OnlinePageIndex ForClient(EhClient client, long gid, string token, int expectedPages) =>
        new(expectedPages, (pageIndex, ct) => client.GetPageListAsync(gid, token, pageIndex, ct));

    /// <summary>Best known page count: the detail page's value, refined by page lists.</summary>
    public int PageCount
    {
        get
        {
            lock (_gate)
            {
                return _complete
                    ? Math.Max(_totalPages, _pages.Count)
                    : Math.Max(Math.Max(_expectedPages, _totalPages),
                        _pageSize > 0 && _lastGroupSeen >= 0 ? (_lastGroupSeen + 1) * _pageSize : 0);
            }
        }
    }

    /// <summary>Pages per preview pagination, 0 until the first list is parsed.</summary>
    public int PageSize
    {
        get { lock (_gate) return _pageSize; }
    }

    public bool IsComplete
    {
        get { lock (_gate) return _complete; }
    }

    /// <summary>True when the reference for this page is already cached.</summary>
    public bool TryGetKnown(int pageNumber, out EhPageRef? page)
    {
        lock (_gate) return _pages.TryGetValue(pageNumber, out page);
    }

    /// <summary>
    /// Returns the page reference, fetching its preview pagination when needed.
    /// Null means the page does not exist (gallery shrank) or the list could not
    /// be parsed; callers surface that as a per-page error.
    /// </summary>
    public async Task<EhPageRef?> GetPageAsync(int pageNumber, CancellationToken cancellationToken)
    {
        if (pageNumber <= 0) return null;
        lock (_gate)
        {
            if (_pages.TryGetValue(pageNumber, out EhPageRef? cached)) return cached;
        }

        int group = ResolveGroup(pageNumber);
        await EnsureGroupAsync(group, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (_pages.TryGetValue(pageNumber, out EhPageRef? found)) return found;
        }

        // Group 0 already covers this page: either it does not exist or the
        // request failed. Do not fire a second identical request.
        if (group == 0) return null;

        // The initial guess may have used the wrong pagination (the site's
        // preview size is a per-account setting). Page group 0 is authoritative,
        // and it teaches the real page size for the retry.
        await EnsureGroupAsync(0, cancellationToken).ConfigureAwait(false);
        int corrected = ResolveGroup(pageNumber);
        if (corrected == group) return null;

        await EnsureGroupAsync(corrected, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return _pages.TryGetValue(pageNumber, out EhPageRef? retried) ? retried : null;
        }
    }

    /// <summary>Warms the pagination that contains the first page.</summary>
    public Task PrimeAsync(CancellationToken cancellationToken) => GetPageAsync(1, cancellationToken);

    private int ResolveGroup(int pageNumber)
    {
        lock (_gate)
        {
            return _pageSize > 0 ? (pageNumber - 1) / _pageSize : 0;
        }
    }

    private async Task EnsureGroupAsync(int groupIndex, CancellationToken cancellationToken)
    {
        Task<bool> task;
        lock (_gate)
        {
            if (_disposed) return;
            if (_complete && groupIndex * Math.Max(1, _pageSize) >= _totalPages && groupIndex > 0) return;
            if (!_groups.TryGetValue(groupIndex, out task!))
            {
                // The transfer itself is not tied to a caller's token: several
                // observers may share it and only stop waiting individually.
                task = FetchGroupAsync(groupIndex);
                _groups[groupIndex] = task;
            }
        }

        await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> FetchGroupAsync(int groupIndex)
    {
        await _groupGate.WaitAsync().ConfigureAwait(false);
        try
        {
            EhPageList list = await _fetch(groupIndex, _lifetime.Token).ConfigureAwait(false);
            if (list.Pages.Count == 0)
            {
                lock (_gate) _groups.Remove(groupIndex);
                return false;
            }

            int maxPage = list.Pages.Max(p => p.Page);
            int minPage = list.Pages.Min(p => p.Page);
            lock (_gate)
            {
                foreach (EhPageRef page in list.Pages) _pages[page.Page] = page;

                if (groupIndex == 0)
                {
                    // Pages 1..maxPage came from the first pagination; that is the
                    // site's effective page size, whatever it is (20/40/50/…).
                    _pageSize = list.HasMore ? Math.Max(1, maxPage) : Math.Max(maxPage, _expectedPages);
                }
                else if (_pageSize <= 0)
                {
                    _pageSize = Math.Max(1, maxPage - minPage + 1);
                }

                _totalPages = Math.Max(_totalPages, maxPage);
                _lastGroupSeen = Math.Max(_lastGroupSeen, groupIndex);
                if (list.LastPageIndex > _lastGroupSeen) _lastGroupSeen = list.LastPageIndex;
                if (!list.HasMore)
                {
                    _complete = true;
                    _totalPages = Math.Max(_totalPages, maxPage);
                }
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            lock (_gate) _groups.Remove(groupIndex);
            throw;
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException or TaskCanceledException)
        {
            // Drop the failed task so a later page (or a retry) can ask again.
            lock (_gate) _groups.Remove(groupIndex);
            return false;
        }
        finally
        {
            _groupGate.Release();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pages.Clear();
            _groups.Clear();
        }

        // Cancel background pagination fetches; the semaphore and token source are
        // left to the GC so a late continuation cannot hit ObjectDisposedException.
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
    }
}
