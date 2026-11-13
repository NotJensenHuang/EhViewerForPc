using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace EhViewerForPc.Services;

/// <summary>Accounts native presentation surfaces and decoder scratch canvases.
/// Reservations fail before allocation; a full viewport must not deadlock waiting
/// for its own currently displayed images to be released.</summary>
public static class ImageMemoryBudget
{
    private static readonly object Gate = new();
    private static readonly ConditionalWeakTable<Bitmap, Reservation> Bitmaps = new();
    private static long _used;
    private static long _limit = 256L * 1024 * 1024;
    public static long UsedBytes { get { lock (Gate) return _used; } }
    public static long LimitBytes { get { lock (Gate) return _limit; } }
    public static void Configure(int megabytes) { lock (Gate) _limit = Math.Clamp(megabytes, 64, 1536) * 1024L * 1024; }

    public static IDisposable Reserve(long bytes, bool scratch = false)
    {
        lock (Gate)
        {
            if (bytes < 0 || bytes > _limit - _used || (scratch && bytes > _limit / 2))
                throw new InvalidOperationException($"图像解码需要 {bytes / 1024 / 1024} MB，超出图像内存预算。可选择压缩图或在设置中增加预算。");
            _used += bytes;
            return new Reservation(bytes);
        }
    }

    public static WriteableBitmap CreateBitmap(int width, int height)
    {
        var reservation = (Reservation)Reserve(checked((long)width * height * 4));
        try
        {
            var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            Bitmaps.Add(bitmap, reservation);
            return bitmap;
        }
        catch { reservation.Dispose(); throw; }
    }

    public static void Release(Bitmap bitmap)
    {
        bitmap.Dispose();
        if (Bitmaps.TryGetValue(bitmap, out var lease)) { Bitmaps.Remove(bitmap); lease.Dispose(); }
    }

    private sealed class Reservation(long bytes) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (Gate) _used -= bytes;
            GC.SuppressFinalize(this);
        }
        ~Reservation() => Dispose();
    }
}
