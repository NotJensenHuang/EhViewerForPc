using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace EhViewerReader.Services;

/// <summary>
/// Recycles the presentation bitmaps used by animated pages.
///
/// A GIF/WebP frame is a full-size BGRA surface. Allocating one per frame put
/// 4-10 MB on the large object heap ten-plus times a second, which surfaced as
/// periodic GC pauses (a visible freeze) plus dropped frames; it also created and
/// destroyed a GPU texture per frame. The pool keeps a handful of buffers alive
/// and lends them out again, so steady-state playback allocates nothing.
///
/// Ownership stays with the control: it releases the outgoing bitmap through
/// <see cref="Release"/>, which recycles pool bitmaps and disposes everything
/// else exactly as before.
/// </summary>
internal static class AnimationFramePool
{
    /// <summary>Idle buffers kept alive; each one is a full frame in VRAM.</summary>
    private const int MaxIdle = 4;

    private static readonly object Gate = new();

    // Counters for the animation probe: a growing "new" count means the pool is
    // being starved (buffers still in use) and frames are allocating again.
    private static long _rentals;
    private static long _hits;
    private static long _allocations;
    private static long _returns;

    public static string Describe() =>
        $"rent={Interlocked.Read(ref _rentals)} hit={Interlocked.Read(ref _hits)} " +
        $"new={Interlocked.Read(ref _allocations)} ret={Interlocked.Read(ref _returns)}";

    public static void ResetCounters()
    {
        Interlocked.Exchange(ref _rentals, 0);
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _allocations, 0);
        Interlocked.Exchange(ref _returns, 0);
    }
    private static readonly HashSet<WriteableBitmap> Known = [];
    private static readonly Queue<WriteableBitmap> Idle = new();

    public static WriteableBitmap Rent(int width, int height)
    {
        Interlocked.Increment(ref _rentals);
        lock (Gate)
        {
            while (Idle.Count > 0)
            {
                WriteableBitmap candidate = Idle.Dequeue();
                if (!Known.Contains(candidate)) continue;
                if (candidate.PixelSize.Width == width && candidate.PixelSize.Height == height)
                {
                    Interlocked.Increment(ref _hits);
                    return candidate;
                }

                Known.Remove(candidate);
                ImageMemoryBudget.Release(candidate);
            }
        }

        Interlocked.Increment(ref _allocations);
        var created = ImageMemoryBudget.CreateBitmap(width, height);
        lock (Gate)
        {
            Known.Add(created);
        }

        return created;
    }

    /// <summary>
    /// Returns a bitmap to the pool. False means the instance was not created by
    /// this pool and the caller must dispose it the usual way.
    /// </summary>
    public static bool TryReturn(WriteableBitmap bitmap)
    {
        lock (Gate)
        {
            if (!Known.Contains(bitmap)) return false;
            if (Idle.Contains(bitmap)) return true; // already returned; ignore a duplicate

            Interlocked.Increment(ref _returns);
            if (Idle.Count >= MaxIdle || (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4 > ImageMemoryBudget.LimitBytes / 8)
            {
                Known.Remove(bitmap);
                ImageMemoryBudget.Release(bitmap);
                return true;
            }

            Idle.Enqueue(bitmap);
            return true;
        }
    }

    /// <summary>Single entry point for "this frame is no longer displayed".</summary>
    public static void Release(Bitmap? bitmap)
    {
        if (bitmap is null) return;
        if (bitmap is WriteableBitmap writeable && TryReturn(writeable)) return;
        ImageMemoryBudget.Release(bitmap);
    }

    public static void Clear()
    {
        lock (Gate)
        {
            while (Idle.Count > 0) { var bitmap = Idle.Dequeue(); Known.Remove(bitmap); ImageMemoryBudget.Release(bitmap); }
        }
    }
}
