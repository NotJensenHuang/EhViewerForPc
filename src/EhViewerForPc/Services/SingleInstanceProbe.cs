using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;

namespace EhViewerForPc.Services;

/// <summary>
/// 无人值守验证单实例激活：隐藏主窗（模拟「关闭到托盘」）→ 启动第二个进程投递请求 →
/// 断言窗口恢复可见且请求被记录。前台焦点取决于桌面状态（是否有其他窗口抢焦点），
/// 只记录不断言，断言集中在信令投递与窗口恢复。
/// </summary>
internal sealed class SingleInstanceProbe(string output)
{
    private const string GalleryId = "activation-probe-gallery";

    private readonly ConcurrentQueue<string> _opened = new();

    internal void RecordOpen(string? gallery)
    {
        if (!string.IsNullOrWhiteSpace(gallery)) _opened.Enqueue(gallery);
    }

    internal async Task<int> RunAsync(Window window, SingleInstanceEndpoint endpoint)
    {
        var checks = new List<(string Id, bool Passed)>();
        string? error = null;
        bool foreground = false;
        try
        {
            window.Hide();
            checks.Add(("window-hidden", !window.IsVisible));

            int exitCode = await RunSecondInstanceAsync().ConfigureAwait(true);
            checks.Add(("second-instance-signalled", exitCode == 0));

            // 服务端在 UI 线程完成恢复后才回 ack，因此子进程退出时窗口应已可见。
            bool restored = await WaitForAsync(
                () => window.IsVisible && window.WindowState != WindowState.Minimized,
                TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            checks.Add(("window-restored", restored));
            checks.Add(("request-recorded", endpoint.Activations > 0));
            checks.Add(("gallery-forwarded", _opened.Contains(GalleryId)));
            foreground = window.IsActive;
        }
        catch (Exception failure)
        {
            error = failure.GetType().Name + ": " + failure.Message;
            checks.Add(("unhandled", false));
        }

        bool passed = checks.All(check => check.Passed);
        WriteReport(passed, checks, foreground, error);
        return passed ? 0 : 3;
    }

    private static async Task<int> RunSecondInstanceAsync()
    {
        string? path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)) return -1;

        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--single-instance-probe-signal");
        start.ArgumentList.Add("--gallery");
        start.ArgumentList.Add(GalleryId);

        using Process? child = Process.Start(start);
        if (child is null) return -1;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await child.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
            return child.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try
            {
                child.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 子进程可能已在退出，取消失败不影响结论。
            }

            return -1;
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50).ConfigureAwait(true);
        }

        return condition();
    }

    private void WriteReport(bool passed, List<(string Id, bool Passed)> checks, bool foreground, string? error)
    {
        try
        {
            string full = Path.GetFullPath(output);
            if (Path.GetDirectoryName(full) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
            using var stream = File.Create(full);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            writer.WriteBoolean("passed", passed);
            writer.WriteBoolean("foreground", foreground);
            writer.WriteStartArray("opened");
            foreach (string id in _opened) writer.WriteStringValue(id);
            writer.WriteEndArray();
            writer.WriteStartArray("checks");
            foreach (var check in checks)
            {
                writer.WriteStartObject();
                writer.WriteString("id", check.Id);
                writer.WriteBoolean("passed", check.Passed);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (error is not null) writer.WriteString("error", error);
            writer.WriteEndObject();
            writer.Flush();
        }
        catch (Exception)
        {
            // 报告写失败不能掩盖退出码；调用方以退出码为准。
        }
    }
}
