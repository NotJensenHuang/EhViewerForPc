using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EhViewerForPc.Services;

if (args is not [var runtime, var models, var provider, var images] || provider is not ("cpu" or "dml"))
{
    Console.Error.WriteLine("Usage: OcrBenchmark <runtime dir> <model dir> <cpu|dml> <directory with PNG snapshots>");
    return 2;
}

Console.OutputEncoding = new UTF8Encoding(false);
string modelDirectory = Path.GetFullPath(models);
string dictionaryPath = Path.Combine(modelDirectory, "rec-dict.txt");
using var engine = new MangaOcr.Engine(modelDirectory, Path.GetFullPath(runtime),
    useDirectMl: provider == "dml", dictionaryBytes: new FileInfo(dictionaryPath).Length);
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};
foreach (string image in Directory.EnumerateFiles(Path.GetFullPath(images), "*.png").Order(StringComparer.Ordinal))
{
    long start = Stopwatch.GetTimestamp();
    MangaOcrResult result = engine.Read(image, cancel.Token);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        File = Path.GetFileName(image),
        Milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
        Result = result,
    }));
}
return 0;

namespace EhViewerForPc.Services
{
    // 独立评测链接生产识别代码，模型目录只指向本地实验资源。
    internal static class MangaTranslationBackendService
    {
        internal static bool IsLocalFile(string path, long? length = null)
        {
            var file = new FileInfo(path);
            return Path.IsPathFullyQualified(path) && file.Exists && (length is null || file.Length == length);
        }
    }

    internal sealed class MangaTranslationException(string message, bool requiresSetup = false) : Exception(message)
    {
        public bool RequiresSetup { get; } = requiresSetup;
    }
}
