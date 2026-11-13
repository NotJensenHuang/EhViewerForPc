using System.Diagnostics;

namespace EhViewerForPc.Services;

/// <summary>
/// Drives GIF/WebP animation playback for a control that owns the decoded
/// bitmap. The caller's onFrame callback decides whether the loop continues
/// (page changed, control detached, cancellation...).
///
/// Pacing: frames are scheduled against a clock instead of "sleep(delay) then
/// decode". The successor frame is decoded on the thread pool while the current
/// frame is still on screen, and the loop waits for an absolute deadline. The
/// previous implementation added decode time on top of the delay and paid the
/// Windows timer quantum (Task.Delay(80) fires at ~93 ms), so a 100 ms GIF ran
/// at roughly 8-9 fps instead of 10.
///
/// Decoding: playback keeps one <see cref="AnimationCursor"/> alive, so the file
/// is opened and its block structure parsed once per session instead of per frame.
///
/// Memory: at most two frames are alive (the one on screen and the one being
/// decoded); each is handed to the control, which disposes the outgoing bitmap.
/// </summary>
public static class AnimationPlayer
{
    /// <summary>Floor for absurdly short frame delays; keeps the UI thread fed.</summary>
    private const int MinFrameMs = 20;

    /// <summary>Ceiling so a corrupt duration cannot park the loop for minutes.</summary>
    private const int MaxFrameMs = 5000;

    /// <summary>Clock-resolution tail that is spun rather than slept (see below).</summary>
    private const int SpinTailMs = 3;

    /// <summary>
    /// Runs the playback loop and resolves to a diagnostic reason describing
    /// why it ended ("cancelled", "decode-null", "onframe-throw:...").
    /// </summary>
    public static async Task<string> PlayAsync(
        DecodedImage first,
        Func<DecodedImage, Task<bool>> onFrame,
        CancellationToken token)
    {
        DecodedImage current = first;
        AnimationCursor? cursor = null;
        try
        {
            var clock = Stopwatch.StartNew();

            // One codec for the whole session; falls back to the per-frame path
            // when Skia refuses to keep the file open (very rare).
            //
            // Opening the cursor parses the whole container (59 ms on a 10 MB
            // 75-frame GIF), so it must never run on the caller's thread: the
            // caller is the UI thread right after the page was decoded, and a
            // synchronous parse there is a visible hitch on page open.
            if (first.Owner is { } owner)
            {
                cursor = await Task.Run(
                    () => owner.TryOpenAnimationCursor(
                        first.Path, first.TargetWidth, first.TargetHeight, first.MaxMegapixels, first.MaxDimension),
                    token).ConfigureAwait(false);
            }


            // Absolute schedule. Pacing from the previous presentation would add
            // the UI dispatch latency (marshal + bitmap swap, ~8 ms) to every
            // frame; an absolute deadline compensates on the next wait, so the
            // observed interval matches the container duration.
            long deadlineMs = 0;

            while (!token.IsCancellationRequested)
            {
                int period = ClampDuration(current.FrameDurationMs);
                deadlineMs += period;

                // Start the decode before waiting: it overlaps the display time
                // of the current frame instead of extending it.
                Task<DecodedImage?> pending = DecodeNextAsync(cursor, current, token);

                if (!await WaitForDeadlineAsync(clock, deadlineMs, token).ConfigureAwait(false))
                {
                    await DiscardAsync(pending).ConfigureAwait(false);
                    return "cancelled";
                }

                DecodedImage? next;
                try
                {
                    next = await pending.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return "cancelled";
                }
                catch (Exception ex)
                {
                    return "decode-throw:" + ex.GetType().Name;
                }

                if (next is null) return "decode-null";
                if (token.IsCancellationRequested)
                {
                    next.Dispose();
                    return "cancelled";
                }

                bool keepGoing;
                try
                {
                    keepGoing = await onFrame(next).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    next.Dispose();
                    return "onframe-throw:" + ex.GetType().Name + ":" + ex.Message;
                }

                if (!keepGoing)
                {
                    next.Dispose();
                    return "stopped-by-callback";
                }

                current = next;

                // A frame that landed more than a period late (UI stall, slow
                // decode) resyncs instead of triggering a catch-up burst.
                long now = clock.ElapsedMilliseconds;
                if (now > deadlineMs + period) deadlineMs = now;
            }

            return "cancelled";
        }
        finally
        {
            cursor?.Dispose();
        }
    }

    private static int ClampDuration(int durationMs) =>
        Math.Clamp(durationMs <= 0 ? MinFrameMs : durationMs, MinFrameMs, MaxFrameMs);

    /// <summary>
    /// Waits until <paramref name="deadlineMs"/> on <paramref name="clock"/>.
    /// Returns false when playback was cancelled.
    ///
    /// The tail is spun instead of slept: the Windows timer quantum is ~15.6 ms,
    /// so Task.Delay(80) actually returns at ~93 ms (an 80 ms GIF would run at
    /// 10.5 fps instead of 12.5). Spinning the last couple of milliseconds costs
    /// a few percent of one core and keeps the frame rate on the container's pace.
    /// </summary>
    private static async Task<bool> WaitForDeadlineAsync(
        Stopwatch clock,
        long deadlineMs,
        CancellationToken token)
    {
        while (true)
        {
            if (token.IsCancellationRequested) return false;
            long remaining = deadlineMs - clock.ElapsedMilliseconds;
            if (remaining <= 0) return true;

            if (remaining > SpinTailMs)
            {
                try
                {
                    await Task.Delay((int)(remaining - SpinTailMs), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }

                continue;
            }

            Thread.SpinWait(400);
        }
    }

    private static Task<DecodedImage?> DecodeNextAsync(
        AnimationCursor? cursor,
        DecodedImage current,
        CancellationToken token)
    {
        if (!current.IsAnimated || current.FrameCount < 2) return Task.FromResult<DecodedImage?>(null);
        int next = (current.FrameIndex + 1) % current.FrameCount;
        return cursor is not null
            ? cursor.DecodeFrameAsync(next, token)
            : current.DecodeNextFrameAsync(token);
    }

    /// <summary>
    /// Releases a decode that finished after playback was cancelled; without
    /// this the bitmap would leak (it was never handed to a control).
    /// </summary>
    private static async Task DiscardAsync(Task<DecodedImage?> pending)
    {
        try
        {
            DecodedImage? image = await pending.ConfigureAwait(false);
            image?.Dispose();
        }
        catch
        {
            // Cancelled or failed: nothing was allocated for the caller.
        }
    }
}
