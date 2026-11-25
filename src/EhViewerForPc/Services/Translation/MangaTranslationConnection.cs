using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace EhViewerForPc.Services;

/// <summary>通过匿名标准输入输出管道管理一个顺序处理页图的工作进程。</summary>
internal sealed class MangaTranslationConnection : IAsyncDisposable
{
    private static readonly TimeSpan CancellationGrace = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(2);
    private readonly MangaTranslationBackendPaths _resources;
    private readonly MangaTranslationLanguageOption _language;
    private readonly Process _process;
    private readonly Task _stderr;
    private readonly byte[] _buffer = new byte[4096];
    private bool _usable = true;

    internal MangaTranslationConnection(ProcessStartInfo start, MangaTranslationBackendPaths resources,
        MangaTranslationLanguageOption language)
    {
        _resources = resources;
        _language = language;
        try { _process = Process.Start(start) ?? throw MangaTranslationService.WorkerError(-1); }
        catch (Win32Exception) { throw MangaTranslationService.WorkerError(-1); }
        _stderr = _process.StandardError.BaseStream.CopyToAsync(Stream.Null);
    }

    internal bool IsUsable => _usable && !_process.HasExited;

    internal bool Matches(MangaTranslationBackendPaths resources, MangaTranslationLanguageOption language) =>
        IsUsable && _resources == resources && _language == language;

    internal async Task<MangaTranslationResponse> TranslateAsync(string snapshot, int width, int height, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            await _process.StandardInput.WriteLineAsync(snapshot.AsMemory(), token).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Windows 管道写入取消时可能已经写入部分帧，丢弃连接。
            _usable = false;
            throw;
        }
        catch (IOException)
        {
            _usable = false;
            throw MangaTranslationService.WorkerError(-1);
        }

        Task<string> read = ReadFrameAsync();
        string frame;
        try
        {
            frame = await read.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await CancelPageAsync(read).ConfigureAwait(false);
            throw;
        }
        catch (IOException)
        {
            _usable = false;
            throw MangaTranslationService.WorkerError(-1);
        }
        catch (DecoderFallbackException)
        {
            _usable = false;
            throw MangaTranslationService.InvalidResponse();
        }

        if (frame.StartsWith("0 ", StringComparison.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            return MangaTranslationService.ParseResponse(frame[2..], width, height);
        }
        if (int.TryParse(frame, NumberStyles.None, CultureInfo.InvariantCulture, out int error))
            throw MangaTranslationService.WorkerError(error);

        // 资源校验失败可能在工作进程开始读取管道之前结束。
        if (frame.Length == 0)
        {
            await _process.WaitForExitAsync(token).ConfigureAwait(false);
            throw MangaTranslationService.WorkerError(_process.ExitCode);
        }
        throw MangaTranslationService.InvalidResponse();
    }

    private async Task CancelPageAsync(Task<string> read)
    {
        using var timeout = new CancellationTokenSource(CancellationGrace);
        try
        {
            // 空行仅取消当前请求。读完响应后才允许提交下一页或移除正在读取的快照。
            await _process.StandardInput.WriteLineAsync(ReadOnlyMemory<char>.Empty, timeout.Token).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            string frame = await read.WaitAsync(timeout.Token).ConfigureAwait(false);
            _usable = frame == "7" || frame.StartsWith("0 ", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or MangaTranslationException or DecoderFallbackException)
        {
            _usable = false;
        }

        if (_usable) return;

        try
        {
            Kill();
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        finally
        {
            try { await read.ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or MangaTranslationException or DecoderFallbackException) { }
        }
    }

    private async Task<string> ReadFrameAsync()
    {
        Stream pipe = _process.StandardOutput.BaseStream;
        using var result = new MemoryStream();
        int read;
        // 直接读管道。StreamReader 的大块读可能在完整的一行之后继续等下一个响应。
        while ((read = await pipe.ReadAsync(_buffer.AsMemory()).ConfigureAwait(false)) != 0)
        {
            int newline = Array.IndexOf(_buffer, (byte)'\n', 0, read);
            int length = newline < 0 ? read : newline;
            if (length > MangaTranslationService.MaxResponseBytes + 3 - result.Length)
                throw new MangaTranslationException("本页翻译结果超出大小限制。");
            result.Write(_buffer, 0, length);
            if (newline < 0) continue;
            if (newline != read - 1) throw MangaTranslationService.InvalidResponse();
            ReadOnlySpan<byte> frame = result.GetBuffer().AsSpan(0, (int)result.Length);
            if (!frame.IsEmpty && frame[^1] == (byte)'\r') frame = frame[..^1];
            if (frame.Length > MangaTranslationService.MaxResponseBytes + 2)
                throw new MangaTranslationException("本页翻译结果超出大小限制。");
            return MangaTranslationWorker.PipeEncoding.GetString(frame);
        }

        if (result.Length != 0) throw MangaTranslationService.InvalidResponse();
        return "";
    }

    private void Kill()
    {
        if (_process.HasExited) return;
        try { _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (_process.HasExited) { }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_usable && !_process.HasExited)
            {
                try { _process.StandardInput.Close(); }
                catch (IOException) { _usable = false; }
                if (_usable)
                {
                    using var timeout = new CancellationTokenSource(ShutdownGrace);
                    try { await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested) { _usable = false; }
                }
            }

            Kill();
            await _process.WaitForExitAsync().ConfigureAwait(false);
            await _stderr.ConfigureAwait(false);
        }
        finally { _process.Dispose(); }
    }
}
