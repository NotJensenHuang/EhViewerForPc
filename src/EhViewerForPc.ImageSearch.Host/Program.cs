using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EhViewerForPc.ImageSearch;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace EhViewerForPc.ImageSearch.Host;

internal static class Program
{
    internal const string Protocol = "ehreader-imsearch-csharp/1";
    internal const string BuildId = "imsearch-csharp-v1";
    internal const string Upstream = "2bf2546822ca2b9b1522d868eb9f21ba441f20d2";
    private static readonly HashSet<string> Flags = ["help", "version", "no-2level", "append", "ignore", "overwrite", "all", "stop-before-merge", "clean-before-merge", "orb-not-oriented"];
    private static readonly HashSet<string> Values = ["conf-dir", "report-json", "workers", "min-keypoints", "suffix", "filter", "hash", "duplicate-distance", "centers", "images", "max-iter", "init", "seed", "batch-size", "stop-after-segments", "count", "nprobe", "k", "distance", "score-type", "addr", "generation-id", "profile-id", "endpoint-file", "orb-nfeatures", "orb-scale-factor", "orb-nlevels", "orb-ini-th-fast", "orb-min-th-fast", "max-size", "max-aspect-ratio", "max-features", "out"];

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var parsed = Parse(args); var options = parsed.Options; var position = parsed.Position;
            if (options.ContainsKey("version")) { Console.WriteLine($"{BuildId} ({Upstream}); {Protocol}; Rust indexes require rebuild."); return 0; }
            if (options.ContainsKey("help") || position.Count == 0) { PrintHelp(); return 0; }
            string command = position[0];
            if (command == "probe") return await SyntheticProbe.RunAsync(Required(options, "conf-dir"), Required(options, "out"));
            if (command is not ("add" or "train" or "build" or "search" or "clean" or "server")) throw new ArgumentException("Unknown command.");
            string directory = Required(options, "conf-dir");
            if (command == "add") ImageImporter.ValidateImportPaths(directory, Input(position), Required(options, "report-json"));
            bool writable = command is "add" or "train" or "build" or "clean";
            FeatureProfile? profile = ReadProfile(options);
            ImageHashMode? hash = options.TryGetValue("hash", out string? mode) ? mode switch { "blake3" => ImageHashMode.Blake3, "dhash" => ImageHashMode.DHash, _ => throw new ArgumentException("hash must be blake3 or dhash.") } : null;
            using var database = new ImageDatabase(directory, writable, profile, hash);
            switch (command)
            {
                case "add":
                    var import = await ImageImporter.ImportAsync(database, Input(position), Required(options, "report-json"), new()
                    {
                        Workers = Integer(options, "workers", 2), BatchSize = Integer(options, "batch-size", 10), MinFeatures = Integer(options, "min-keypoints", 30),
                        Suffixes = options.GetValueOrDefault("suffix", "jpg,jpeg,png,webp,gif").Split(','), Filter = options.GetValueOrDefault("filter"),
                        Append = !options.ContainsKey("ignore"), Overwrite = options.ContainsKey("overwrite"),
                        ReplacePattern = options.GetValueOrDefault("replace"), Replacement = options.GetValueOrDefault("replacement", ""),
                        DuplicateDistance = Integer(options, "duplicate-distance", 0)
                    });
                    Console.WriteLine(JsonSerializer.Serialize(import, HostJsonContext.Default.ImportSummary)); return import.Failed == 0 ? 0 : 2;
                case "train":
                    string init = options.GetValueOrDefault("init", "random");
                    if (init is not ("random" or "kmeans-plus-plus")) throw new ArgumentException("Invalid initialization method.");
                    var trained = ImageSearchEngine.Train(database, Integer(options, "centers", 0), Integer(options, "images", 0), Integer(options, "max-iter", 20),
                        init == "kmeans-plus-plus", !options.ContainsKey("no-2level"), Integer(options, "seed", 20261112), trainingVectorLimit: Integer(options, "training-vector-limit", 4_000_000));
                    Console.WriteLine(FormattableString.Invariant($"centers={trained.Centers.Length}, distance={trained.Distance}, imbalance={trained.Imbalance:F4}")); return 0;
                case "build":
                    int? stop = options.ContainsKey("stop-after-segments") ? Integer(options, "stop-after-segments", 0) : null;
                    var built = ImageSearchEngine.Build(database, Integer(options, "batch-size", 2000), options.ContainsKey("stop-before-merge"), options.ContainsKey("clean-before-merge"), stop);
                    Console.WriteLine(JsonSerializer.Serialize(built, HostJsonContext.Default.BuildResult)); return 0;
                case "clean": database.Clean(options.ContainsKey("all")); return 0;
                case "search":
                    var engine = new ImageSearchEngine(database);
                    var hits = engine.Search(GrayImage.ReadFile(Input(position)), ReadSearch(options, engine.Centers));
                    string json = JsonSerializer.Serialize(hits, HostJsonContext.Default.SearchHitArray);
                    if (options.TryGetValue("out", out string? output)) await File.WriteAllTextAsync(output, json);
                    else if (options.GetValueOrDefault("output-format", "json") == "json") Console.WriteLine(json);
                    else if (options["output-format"] == "table") foreach (var hit in hits) Console.WriteLine(FormattableString.Invariant($"{hit.Score:F2}\t{string.Join('\u001e', hit.Aliases)}"));
                    else throw new ArgumentException("Invalid output format.");
                    return 0;
                case "server": await ServeAsync(database, options); return 0;
                default: return 1;
            }
        }
        catch (Exception error)
        {
            // Do not expose SQL, image bytes, local paths, or environment credentials.
            Console.Error.WriteLine(JsonSerializer.Serialize(new HostError("operation_failed", error.GetType().Name), HostJsonContext.Default.HostError));
            return error is FileNotFoundException or DirectoryNotFoundException ? 2 : 1;
        }
    }

    private static (Dictionary<string, string> Options, List<string> Position) Parse(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal); var position = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith('-')) { position.Add(arg); continue; }
            string key = arg switch { "-c" => "centers", "-i" => "images", "-n" => "orb-nfeatures", "-S" => "max-size", "-A" => "max-aspect-ratio", "-h" => "help", "-k" => "k", _ => arg.TrimStart('-') };
            if (key == "phash-distance") key = "duplicate-distance";
            if (key == "force") { if (!options.TryAdd(key, "true")) throw new ArgumentException("Duplicate force flag."); continue; }
            if (key is "output-format" or "threads" or "prometheus-push" or "prometheus-instance")
            { if (++i >= args.Length || !options.TryAdd(key, args[i])) throw new ArgumentException("Expected argument value."); continue; }
            if (key == "orb-interpolation") { if (++i >= args.Length || !options.TryAdd(key, args[i])) throw new ArgumentException("Expected interpolation."); continue; }
            if (key == "replace")
            {
                if (i + 2 >= args.Length || !options.TryAdd("replace", args[++i]) || !options.TryAdd("replacement", args[++i])) throw new ArgumentException("Expected replace regex and replacement.");
                continue;
            }
            if (key == "training-vector-limit") { if (++i >= args.Length || !options.TryAdd(key, args[i])) throw new ArgumentException("Expected training-vector-limit."); continue; }
            if (!Flags.Contains(key) && !Values.Contains(key)) throw new ArgumentException("Unknown argument.");
            string value = Flags.Contains(key) ? "true" : ++i < args.Length ? args[i] : throw new ArgumentException("Missing argument value.");
            if (!options.TryAdd(key, value)) throw new ArgumentException("Duplicate argument.");
        }
        if (new[] { "append", "ignore", "overwrite" }.Count(options.ContainsKey) > 1) throw new ArgumentException("Conflicting duplicate policies.");
        return (options, position);
    }
    private static string Required(Dictionary<string, string> options, string key) => options.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("Missing " + key);
    private static string Input(List<string> position) => position.Count == 2 ? position[1] : throw new ArgumentException("Expected one input path.");
    private static int Integer(Dictionary<string, string> options, string key, int fallback) => options.TryGetValue(key, out string? value) ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;
    private static float Number(Dictionary<string, string> options, string key, float fallback) => options.TryGetValue(key, out string? value) ? float.Parse(value, CultureInfo.InvariantCulture) : fallback;
    private static FeatureProfile? ReadProfile(Dictionary<string, string> options)
    {
        if (!options.Keys.Any(k => k.StartsWith("orb-", StringComparison.Ordinal) || k is "max-size" or "max-aspect-ratio" or "max-features")) return null;
        string[] size = options.GetValueOrDefault("max-size", "1080x768").Split('x');
        if (size.Length != 2) throw new ArgumentException("Invalid max-size.");
        return new()
        {
            Features = Integer(options, "orb-nfeatures", 500), ScaleFactor = Number(options, "orb-scale-factor", 1.2f), Levels = Integer(options, "orb-nlevels", 8),
            FastThreshold = Integer(options, "orb-ini-th-fast", 20), MinFastThreshold = Integer(options, "orb-min-th-fast", 7), Oriented = !options.ContainsKey("orb-not-oriented"),
            MaxHeight = int.Parse(size[0], CultureInfo.InvariantCulture), MaxWidth = int.Parse(size[1], CultureInfo.InvariantCulture),
            MaxAspectRatio = Number(options, "max-aspect-ratio", 5), MaxFeatures = Integer(options, "max-features", 1000),
            Interpolation = options.GetValueOrDefault("orb-interpolation", "area") switch { "area" => ImageInterpolation.Area, "linear" => ImageInterpolation.Linear, "cubic" => ImageInterpolation.Cubic, "lanczos4" => ImageInterpolation.Lanczos4, _ => throw new ArgumentException("Invalid interpolation.") }
        };
    }
    private static SearchOptions ReadSearch(Dictionary<string, string> options, int centers)
    {
        string score = options.GetValueOrDefault("score-type", "wilson");
        if (score is not ("wilson" or "count")) throw new ArgumentException("Invalid score-type.");
        return new() { NProbe = Integer(options, "nprobe", Math.Min(16, centers)), K = Integer(options, "k", 3), Count = Integer(options, "count", 100),
            MaxDistance = Integer(options, "distance", 64), CountScore = score == "count", ReadThreads = Integer(options, "threads", 2) };
    }

    private static void PrintHelp() => Console.WriteLine("""
        imsearch C# backend --conf-dir <state> <command> [input] [options]
        add <directory|tar> --report-json <outside-source.jsonl> [--workers 1..6] [--batch-size 1..10] [--min-keypoints 30] [--suffix jpg,png,webp] [--filter regex] [--replace regex replacement] [--hash blake3|dhash] [--ignore|--append|--overwrite] [--phash-distance 0..64]
        train --centers K --images N [--max-iter 20] [--init random|kmeans-plus-plus] [--no-2level] [--seed 20261112]
        Training defaults to <=4000000 descriptors; --training-vector-limit N (<=64000000) explicitly raises the memory budget.
        build [--batch-size 2000] [--stop-before-merge] [--stop-after-segments N] [--clean-before-merge]
        search <image> [--nprobe 16] [-k 3] [--distance 64] [--count 100] [--score-type wilson|count] [--out result.json]
        Search also supports --output-format json|table and --threads 1|2. --out always writes structured JSON.
        clean [--all]
        server [--addr 127.0.0.1:8000] [--generation-id 32hex] [--profile-id 64hex] [--endpoint-file path]
        probe --out report.json      Runs a generated 64-image end-to-end suite in a new isolated --conf-dir.
        ORB: --orb-nfeatures 500 --orb-scale-factor 1.2 --orb-nlevels 8 --orb-ini-th-fast 20 --orb-min-th-fast 7 --orb-not-oriented --max-size 1080x768 --max-aspect-ratio 5 --max-features 1000
        Pyramid: --orb-interpolation area|linear|cubic|lanczos4 (default area).
        HTTP requires IMSEARCH_AUTH_TOKEN (>=43 encoded characters), authenticates all routes, and binds loopback only.
        Optional --prometheus-push URL --prometheus-instance NAME sends metrics only when explicitly configured; Basic auth uses IMSEARCH_PROMETHEUS_AUTH=username:password.
        clean is noninteractive; --force is accepted for existing unattended scripts.
        Format: imsearch-csharp/1; Rust/Faiss indexes are not readable. Reimport original images.
        """
    );

    private static async Task ServeAsync(ImageDatabase database, Dictionary<string, string> options)
    {
        string token = Environment.GetEnvironmentVariable("IMSEARCH_AUTH_TOKEN") ?? "";
        if (token.Length < 43) throw new ArgumentException("Missing or too short authentication token.");
        string generation = options.GetValueOrDefault("generation-id", Guid.NewGuid().ToString("N"));
        if (!IsHex(generation, 32) || options.TryGetValue("profile-id", out string? profile) && profile != database.Profile.Id)
            throw new ArgumentException("Invalid backend identity.");
        string address = options.GetValueOrDefault("addr", "127.0.0.1:8000");
        if (!Uri.TryCreate("http://" + address, UriKind.Absolute, out var uri) || !IPAddress.TryParse(uri.Host, out var ip) || !IPAddress.IsLoopback(ip)
            || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("HTTP must bind a loopback IP address.");
        var engine = new ImageSearchEngine(database); var searchOptions = ReadSearch(options, engine.Centers);
        // Validate defaults before accepting requests, including nprobe/count/k/distance.
        engine.SearchDescriptors([], searchOptions);
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(uri.ToString());
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 50 * 1024 * 1024 + 16384);
        builder.Services.Configure<FormOptions>(form => { form.MultipartBodyLengthLimit = 50 * 1024 * 1024; form.ValueLengthLimit = 128; form.ValueCountLimit = 8; form.MultipartHeadersCountLimit = 8; });
        var app = builder.Build();
        long requests = 0, errors = 0, searches = 0, elapsedMs = 0;
        string Metrics() => FormattableString.Invariant($"imsearch_requests_total {Interlocked.Read(ref requests)}\nimsearch_errors_total {Interlocked.Read(ref errors)}\nimsearch_searches_total {Interlocked.Read(ref searches)}\nimsearch_search_milliseconds_total {Interlocked.Read(ref elapsedMs)}\nimsearch_working_set_bytes {Environment.WorkingSet}\nimsearch_indexed_images {engine.IndexedImages}\n");
        using var queryGate = new SemaphoreSlim(1, 1);
        byte[] tokenBytes = Encoding.UTF8.GetBytes("Bearer " + token);
        app.Use(async (context, next) =>
        {
            Interlocked.Increment(ref requests);
            byte[] presented = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
            if (!CryptographicOperations.FixedTimeEquals(presented, tokenBytes))
            { Interlocked.Increment(ref errors); await Error(context, 401, "unauthorized"); return; }
            await next(context);
            if (context.Response.StatusCode >= 400) Interlocked.Increment(ref errors);
        });
        app.MapGet("/healthz", async context => await context.Response.WriteAsJsonAsync(new BackendIdentity(Protocol, BuildId, Upstream, generation,
            database.Profile.Id, true, searchOptions.Count, engine.IndexedImages, engine.Centers), HostJsonContext.Default.BackendIdentity));
        app.MapGet("/metrics", async context =>
        {
            context.Response.ContentType = "text/plain; version=0.0.4";
            await context.Response.WriteAsync(Metrics());
        });
        app.MapGet("/docs", async context => { context.Response.ContentType = "text/plain"; await context.Response.WriteAsync("Authenticated loopback imsearch C# API. GET /healthz /metrics /openapi.json. POST /search: multipart file, request_id (32 lowercase hex), optional nprobe and profile-locked orb_nfeatures/orb_scale_factor, generation_id/profile_id."); });
        app.MapGet("/openapi.json", async context => { context.Response.ContentType = "application/json"; await context.Response.WriteAsync(ApiDescription.Json); });
        app.MapPost("/search", async context =>
        {
            if (!await queryGate.WaitAsync(0, context.RequestAborted)) { await Error(context, 429, "busy"); return; }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            string? requestId = null;
            try
            {
                if (!context.Request.HasFormContentType) { await Error(context, 400, "invalid_input"); return; }
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                requestId = form["request_id"].ToString();
                if (!IsHex(requestId, 32) || form.Files.Count != 1 || form.Files[0].Name != "file" || form.Files[0].Length is < 1 or > 50 * 1024 * 1024
                    || form.Any(f => f.Value.Count != 1 || f.Key is not ("request_id" or "nprobe" or "orb_nfeatures" or "orb_scale_factor" or "generation_id" or "profile_id")))
                { await Error(context, 400, "invalid_input"); return; }
                if (form.ContainsKey("generation_id") && form["generation_id"] != generation || form.ContainsKey("profile_id") && form["profile_id"] != database.Profile.Id
                    || form.ContainsKey("orb_nfeatures") && (!int.TryParse(form["orb_nfeatures"], out int f) || f != database.Profile.Features)
                    || form.ContainsKey("orb_scale_factor") && (!float.TryParse(form["orb_scale_factor"], CultureInfo.InvariantCulture, out float s) || s != database.Profile.ScaleFactor))
                { await Error(context, 409, "backend_mismatch", requestId); return; }
                int nprobe = form.ContainsKey("nprobe") ? int.Parse(form["nprobe"].ToString(), CultureInfo.InvariantCulture) : searchOptions.NProbe;
                if (nprobe < 1 || nprobe > engine.Centers) { await Error(context, 400, "invalid_input", requestId); return; }
                byte[] bytes = new byte[checked((int)form.Files[0].Length)];
                await using (var file = form.Files[0].OpenReadStream()) await file.ReadExactlyAsync(bytes, context.RequestAborted);
                var hits = await Task.Run(() => engine.Search(GrayImage.Decode(bytes), searchOptions with { NProbe = nprobe }, context.RequestAborted), context.RequestAborted);
                using var response = new MemoryStream();
                using (var writer = new Utf8JsonWriter(response))
                {
                    writer.WriteStartObject(); writer.WriteString("protocol", Protocol); writer.WriteString("backend_build_id", BuildId);
                    writer.WriteString("generation_id", generation); writer.WriteString("profile_id", database.Profile.Id); writer.WriteString("request_id", requestId);
                    writer.WriteNumber("time", clock.ElapsedMilliseconds); writer.WriteStartArray("result");
                    foreach (var hit in hits) { writer.WriteStartArray(); writer.WriteNumberValue(hit.Score); writer.WriteStringValue(string.Join('\u001e', hit.Aliases)); writer.WriteEndArray(); }
                    writer.WriteEndArray(); writer.WriteEndObject();
                }
                if (response.Length > 2 * 1024 * 1024) { await Error(context, 500, "response_too_large", requestId); return; }
                context.Response.ContentType = "application/json";
                await context.Response.Body.WriteAsync(response.GetBuffer().AsMemory(0, (int)response.Length), context.RequestAborted);
                Interlocked.Increment(ref searches); Interlocked.Add(ref elapsedMs, clock.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or FormatException or OverflowException or BadHttpRequestException)
            { if (!context.Response.HasStarted) await Error(context, 400, "invalid_input", requestId); }
            catch
            { if (!context.Response.HasStarted) await Error(context, 500, "search_failed", requestId); }
            finally { queryGate.Release(); }
        });
        await app.StartAsync();
        Task? metricsPush = null;
        if (options.TryGetValue("prometheus-push", out string? pushAddress))
        {
            if (!Uri.TryCreate(pushAddress, UriKind.Absolute, out var pushUri) || pushUri.Scheme is not ("http" or "https") || pushUri.UserInfo.Length > 0)
                throw new ArgumentException("Invalid Prometheus push URL.");
            string instance = options.GetValueOrDefault("prometheus-instance", address);
            var destination = new Uri(pushUri.ToString().TrimEnd('/') + "/metrics/job/imsearch/instance/" + Uri.EscapeDataString(instance));
            metricsPush = PushMetrics(destination, Metrics, app.Lifetime.ApplicationStopping);
        }
        if (options.TryGetValue("endpoint-file", out string? endpointFile)) await File.WriteAllTextAsync(endpointFile, app.Urls.Single());
        await app.WaitForShutdownAsync();
        if (metricsPush is not null) await metricsPush;
        await app.DisposeAsync();
    }

    private static async Task PushMetrics(Uri destination, Func<string> metrics, CancellationToken cancellationToken)
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        string? basic = Environment.GetEnvironmentVariable("IMSEARCH_PROMETHEUS_AUTH");
        if (!string.IsNullOrEmpty(basic)) client.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(basic)));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var content = new StringContent(metrics(), Encoding.UTF8, "text/plain");
                    using var response = await client.PutAsync(destination, content, cancellationToken);
                    response.EnsureSuccessStatusCode();
                }
                catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { }
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static bool IsHex(string? value, int length) => value?.Length == length && value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;
    private static Task Error(HttpContext context, int status, string code, string? request = null)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new ErrorEnvelope(new ApiError(code, code.Replace('_', ' '), IsHex(request, 32) ? request : null)), HostJsonContext.Default.ErrorEnvelope);
    }
}

internal sealed record BackendIdentity(string Protocol, string BackendBuildId, string UpstreamCommit, string GenerationId, string ProfileId, bool Ready, int ResultLimit, long IndexedAssets, int Centers);
internal sealed record HostError(string Code, string Message);
internal sealed record ApiError(string Code, string Message, string? RequestId);
internal sealed record ErrorEnvelope(ApiError Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(BackendIdentity))]
[JsonSerializable(typeof(HostError))]
[JsonSerializable(typeof(ErrorEnvelope))]
[JsonSerializable(typeof(ImportSummary))]
[JsonSerializable(typeof(BuildResult))]
[JsonSerializable(typeof(SearchHit[]))]
[JsonSerializable(typeof(ProbeReport))]
internal partial class HostJsonContext : JsonSerializerContext;

internal static class ApiDescription
{
    internal const string Json = """
        {"openapi":"3.0.3","info":{"title":"imsearch C#","version":"1"},"security":[{"bearer":[]}],"components":{"securitySchemes":{"bearer":{"type":"http","scheme":"bearer"}}},"paths":{"/healthz":{"get":{"responses":{"200":{"description":"Ready identity"},"401":{"description":"Unauthorized"}}}},"/metrics":{"get":{"responses":{"200":{"description":"Prometheus metrics"}}}},"/search":{"post":{"requestBody":{"required":true,"content":{"multipart/form-data":{"schema":{"type":"object","required":["file","request_id"],"properties":{"file":{"type":"string","format":"binary"},"request_id":{"type":"string","pattern":"^[0-9a-f]{32}$"},"nprobe":{"type":"integer","minimum":1},"orb_nfeatures":{"type":"integer","description":"Must equal stored profile"},"orb_scale_factor":{"type":"number","description":"Must equal stored profile"},"generation_id":{"type":"string"},"profile_id":{"type":"string"}}}}}},"responses":{"200":{"description":"Scored alias tuples"},"400":{"description":"Invalid input"},"401":{"description":"Unauthorized"},"409":{"description":"Profile or generation mismatch"},"429":{"description":"Query busy"}}}}}}
        """;
}
