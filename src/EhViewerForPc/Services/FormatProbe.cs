using System.Diagnostics;
using SkiaSharp;

namespace EhViewerForPc.Services;

/// <summary>
/// Diagnostic helper for individual files: distinguishes "Skia hangs forever on
/// this file" from "file is merely slow", and verifies whether a read timeout
/// stream can break the hang.
/// </summary>
public static class FormatProbe
{
    public static int Run(string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("file not found: " + path);
            return 2;
        }

        var info = new FileInfo(path);
        Console.WriteLine($"file: {path}");
        Console.WriteLine($"size: {info.Length:N0} bytes");
        Console.WriteLine($"magic: {Magic(path)}");

        var watch = Stopwatch.StartNew();
        Task<SKCodec?> direct = Task.Run<SKCodec?>(() => SKCodec.Create(path));
        bool finished = direct.Wait(TimeSpan.FromSeconds(6));
        Console.WriteLine($"direct: finished={finished} elapsed={watch.Elapsed.TotalSeconds:F1}s");
        if (finished && direct.Result is { } codec)
        {
            using (codec)
            {
                Console.WriteLine($"  codec: {codec.Info.Width}x{codec.Info.Height} origin={codec.EncodedOrigin}");
            }
        }
        else if (finished)
        {
            Console.WriteLine("  codec: null");
        }

        watch.Restart();
        try
        {
            using var file = File.OpenRead(path);
            using var guarded = new TimeoutReadStream(file, TimeSpan.FromSeconds(6));
            using SKCodec? guardedCodec = SKCodec.Create(guarded, out SKCodecResult result);
            Console.WriteLine($"guarded: result={result} elapsed={watch.Elapsed.TotalSeconds:F1}s " +
                              $"size={(guardedCodec is null ? "null" : $"{guardedCodec.Info.Width}x{guardedCodec.Info.Height}")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"guarded: {ex.GetType().Name}: {ex.Message} elapsed={watch.Elapsed.TotalSeconds:F1}s");
        }

        return 0;
    }

    private static string Magic(string path)
    {
        Span<byte> buffer = stackalloc byte[16];
        using var stream = File.OpenRead(path);
        int read = stream.Read(buffer);
        return Convert.ToHexString(buffer[..read]);
    }
}

/// <summary>
/// Read-only stream that aborts a native parser which spends too long reading.
/// </summary>
public sealed class TimeoutReadStream : Stream
{
    private readonly Stream _inner;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly TimeSpan _timeout;

    public TimeoutReadStream(Stream inner, TimeSpan timeout)
    {
        _inner = inner;
        _timeout = timeout;
    }

    private void Check()
    {
        if (_watch.Elapsed > _timeout)
        {
            throw new IOException($"Read exceeded {_timeout.TotalSeconds:F0}s (decoder watchdog).");
        }
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count)
    {
        Check();
        return _inner.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        Check();
        return _inner.Read(buffer);
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
