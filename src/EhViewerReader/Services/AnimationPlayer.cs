namespace EhViewerReader.Services;

/// <summary>
/// Drives GIF/WebP animation playback for a control that owns the decoded
/// bitmap. Frames are decoded one at a time (no frame cache), so memory stays
/// at a single frame; the caller's onFrame callback decides whether the loop
/// continues (page changed, control detached, cancellation...).
/// </summary>
public static class AnimationPlayer
{
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
        while (!token.IsCancellationRequested)
        {
            int delay = Math.Clamp(current.FrameDurationMs, 20, 5000);
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return "cancelled";
            }

            DecodedImage? next;
            try
            {
                next = await current.DecodeNextFrameAsync(token).ConfigureAwait(false);
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
        }

        return "cancelled";
    }
}
