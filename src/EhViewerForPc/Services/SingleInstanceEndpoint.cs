using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;

namespace EhViewerForPc.Services;

/// <summary>
/// 单实例激活通道：命名管道承载「显示窗口 / 打开作品」请求，取代「注册表存 HWND」方案。
/// 管道名带会话号，避免快速用户切换或远程桌面下不同会话互相串台；协议是换行分隔的
/// UTF-8 纯文本，不引入序列化上下文。
/// </summary>
internal sealed partial class SingleInstanceEndpoint : IDisposable
{
    private const int ConnectTimeoutMs = 300;
    private const int SignalAttempts = 4;
    private const int RetryDelayMs = 250;
    private const int AckTimeoutMs = 2000;
    private const int MaxRequestBytes = 512;

    private readonly Window _window;
    private readonly Func<bool> _restoreMaximized;
    private readonly Action<string?> _openGallery;
    private readonly LogService _log;
    private readonly CancellationTokenSource _lifetime = new();
    private NamedPipeServerStream? _server;
    private Task? _loop;
    private int _activations;
    private bool _disposed;

    internal SingleInstanceEndpoint(Window window, Func<bool> restoreMaximized, Action<string?> openGallery, LogService log)
    {
        _window = window;
        _restoreMaximized = restoreMaximized;
        _openGallery = openGallery;
        _log = log;
    }

    /// <summary>第二实例通过同名管道投递请求；前缀沿用旧进程名，便于日志检索。</summary>
    internal static string PipeName { get; } = BuildPipeName();

    /// <summary>已处理的激活请求数，供无人值守探针断言。</summary>
    internal int Activations => Volatile.Read(ref _activations);

    internal void Start()
    {
        if (_disposed || _loop is not null || !OperatingSystem.IsWindows()) return;
        // 先同步建好服务端再进入等待：Start 返回后管道即可连接，第二次启动不会踩空。
        try
        {
            _server = CreateServer();
        }
        catch (Exception error)
        {
            _log.Warn("Single-instance channel unavailable", error);
            return;
        }

        _loop = Task.Run(RunAsync);
    }

    /// <summary>
    /// 向已运行的实例投递请求。连接成功后先授予前台许可再发送请求：
    /// AllowSetForegroundWindow 只对「下一次」SetForegroundWindow 生效，顺序不能颠倒。
    /// </summary>
    /// <returns>运行中的实例确认已处理请求。</returns>
    internal static bool TrySignal(string? galleryId)
    {
        if (!OperatingSystem.IsWindows()) return false;

        string request = string.IsNullOrWhiteSpace(galleryId) ? "SHOW" : "OPEN " + galleryId.Trim();
        for (int attempt = 0; attempt < SignalAttempts; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                client.Connect(ConnectTimeoutMs);
                GrantForeground(client);
                byte[] payload = Encoding.UTF8.GetBytes(request + "\n");
                client.Write(payload, 0, payload.Length);
                client.Flush();
                return string.Equals(ReadAck(client), "OK", StringComparison.Ordinal);
            }
            catch (Exception error) when (error is TimeoutException or IOException or UnauthorizedAccessException or AggregateException)
            {
                // 首实例可能仍在初始化、服务端尚未开始监听；重试覆盖这段窗口期。
                Thread.Sleep(RetryDelayMs);
            }
        }

        return false;
    }

    private static void GrantForeground(NamedPipeClientStream client)
    {
        try
        {
            if (!GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out uint processId)) return;
            WindowActivation.GrantForeground(processId);
        }
        catch (Exception)
        {
            // 授权失败只是退回「仅恢复窗口、可能不抢焦点」，不阻断请求投递。
        }
    }

    private static string ReadAck(NamedPipeClientStream client)
    {
        byte[] buffer = new byte[32];
        int length = 0;
        while (length < buffer.Length)
        {
            Task<int> read = client.ReadAsync(buffer.AsMemory(length), CancellationToken.None).AsTask();
            if (!read.Wait(AckTimeoutMs)) return string.Empty;
            int count = read.GetAwaiter().GetResult();
            if (count == 0) break;
            length += count;
            if (buffer[length - 1] == (byte)'\n') break;
        }

        return Encoding.UTF8.GetString(buffer, 0, length).Trim();
    }

    private async Task RunAsync()
    {
        CancellationToken token = _lifetime.Token;
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = _server;
            try
            {
                if (server is null)
                {
                    server = CreateServer();
                    _server = server;
                }

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                // 连接方迟迟不发请求时丢弃本次连接：否则单个占位连接会一直阻塞后续激活。
                using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                    string request = await ReadLineAsync(server, readTimeout.Token).ConfigureAwait(false);
                    if (request.Length == 0) continue;

                    bool handled = await HandleAsync(request, token).ConfigureAwait(false);
                    await WriteLineAsync(server, handled ? "OK" : "BUSY", token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested) break;

                // 只取消了一次连接读取：记录后重建服务端继续监听。
                _log.Warn("Single-instance request timed out");
            }
            catch (ObjectDisposedException)
            {
                // Dispose 关闭等待中的连接是取消监听的手段，不是故障。
                break;
            }
            catch (Exception error)
            {
                // 单次连接失败不能终止监听：记录后重建服务端继续等待。
                _log.Warn("Single-instance request failed", error);
                try
                {
                    await Task.Delay(RetryDelayMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                _server = null;
                try
                {
                    server?.Dispose();
                }
                catch (Exception)
                {
                    // 释放失败不阻断后续资源；下一次循环会重建服务端。
                }
            }
        }
    }

    private async Task<bool> HandleAsync(string request, CancellationToken token)
    {
        string? gallery = null;
        if (request.StartsWith("OPEN ", StringComparison.Ordinal))
        {
            gallery = request[5..].Trim();
            if (gallery.Length == 0) return false;
        }
        else if (!string.Equals(request, "SHOW", StringComparison.Ordinal))
        {
            return false;
        }

        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed) return false;
            WindowActivation.Result result = WindowActivation.Restore(_window, _restoreMaximized());
            if (result.Fallback)
            {
                _log.Warn("Foreground lock rejected activation; window was raised without focus");
            }

            if (gallery is not null) _openGallery(gallery);
            Interlocked.Increment(ref _activations);
            return true;
        }, DispatcherPriority.Send, token);
    }

    private static NamedPipeServerStream CreateServer() =>
        new(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken token)
    {
        byte[] buffer = new byte[MaxRequestBytes];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
            if (buffer[length - 1] == (byte)'\n') break;
        }

        return Encoding.UTF8.GetString(buffer, 0, length).TrimEnd('\r', '\n');
    }

    private static async Task WriteLineAsync(Stream stream, string line, CancellationToken token)
    {
        byte[] payload = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static string BuildPipeName()
    {
        // 进程间通信按会话隔离：每个登录会话各自只有一个「首实例」。
        int session = OperatingSystem.IsWindows() ? Process.GetCurrentProcess().SessionId : 0;
        return $"EhViewerReader.Activation.v1.{session}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        try
        {
            _server?.Dispose();
        }
        catch (Exception)
        {
            // 取消监听的手段本身失败不影响退出。
        }

        Task? loop = _loop;
        _loop = null;
        if (loop is null)
        {
            _lifetime.Dispose();
            return;
        }

        bool completed;
        try
        {
            completed = loop.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException error)
        {
            _log.Warn("Single-instance channel shutdown failed", error.InnerException ?? error);
            completed = true;
        }

        // 只有在监听循环确实结束后才释放令牌源；否则保留到进程退出，避免未定义访问。
        if (completed) _lifetime.Dispose();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(nint pipe, out uint serverProcessId);
}
