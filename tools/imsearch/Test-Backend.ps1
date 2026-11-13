param([string]$HostPath = 'src/EhViewerForPc.ImageSearch.Host/bin/Debug/net11.0/EhViewerForPc.ImageSearch.Host.exe', [string]$OutputDirectory)
. (Join-Path $PSScriptRoot 'Process.ps1')
$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location $repository
try {
    if (-not $OutputDirectory) { $OutputDirectory = '.qa/image-search/verification/' + [Guid]::NewGuid().ToString('N') }
    $output = [System.IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $output) { throw 'Use a new output directory; existing verification evidence is preserved.' }
    New-Item -ItemType Directory -Path $output | Out-Null
    $executable = (Resolve-Path -LiteralPath $HostPath).Path
    $checks = [System.Collections.Generic.List[object]]::new()
    function Check([string]$Id, [bool]$Passed, [string]$Message) { $checks.Add([ordered]@{ id=$Id; passed=$Passed; message=$Message }) }
    function Run-Cli([string]$Id, [string[]]$Arguments, [int]$Expected = 0, [hashtable]$EnvironmentVariables = @{}) {
        $code = Invoke-ImsearchProcess -Executable $executable -Arguments $Arguments -LogPrefix (Join-Path $output $Id) -EnvironmentVariables $EnvironmentVariables
        Check $Id ($code -eq $Expected) "exit=$code; expected=$Expected"
        if ($code -ne $Expected) { throw "CLI check failed: $Id (exit=$code)" }
    }
    try {
        $synthetic = Join-Path $output 'synthetic'
        Run-Cli 'synthetic-probe' @('probe', '--conf-dir', $synthetic, '--out', (Join-Path $output 'synthetic.json'))
        $probe = Get-Content (Join-Path $output 'synthetic.json') -Raw | ConvertFrom-Json
        Check 'probe-report' ($probe.failed -eq 0 -and $probe.images -eq 64 -and $probe.backend_kind -eq 'ManagedCSharp') 'Real generated images; C# backend; complete report.'
        Run-Cli 'help' @('--help')
        Run-Cli 'version' @('--version')
        $inputs = Join-Path $output 'cli-input'
        New-Item -ItemType Directory -Path $inputs | Out-Null
        for ($i = 0; $i -lt 8; $i++) { Copy-Item -LiteralPath (Join-Path $synthetic ('images/page-{0:D3}.png' -f $i)) -Destination (Join-Path $inputs ('页图 {0:D3}.png' -f $i)) }
        $backend = Join-Path $output 'cli-backend'
        Run-Cli 'cli-add' @('add', $inputs, '--conf-dir', $backend, '--report-json', (Join-Path $output 'cli-add.jsonl'), '--append')
        Run-Cli 'cli-train' @('train', '--conf-dir', $backend, '--centers', '8', '--images', '8', '--max-iter', '4', '--init', 'kmeans-plus-plus')
        Run-Cli 'cli-pause' @('build', '--conf-dir', $backend, '--stop-before-merge', '--batch-size', '2')
        Run-Cli 'cli-build' @('build', '--conf-dir', $backend, '--clean-before-merge')
        Run-Cli 'cli-search' @('search', (Join-Path $inputs '页图 003.png'), '--conf-dir', $backend, '--nprobe', '8', '--out', (Join-Path $output 'cli-search.json'))
        $hits = Get-Content (Join-Path $output 'cli-search.json') -Raw | ConvertFrom-Json
        Check 'cli-hit' ($hits[0].aliases[0] -eq '页图 003.png') 'The requested Unicode/space path ranks first.'
        Run-Cli 'cli-clean' @('clean', '--conf-dir', $backend, '--all')
        Run-Cli 'cli-search-after-clean' @('search', (Join-Path $inputs '页图 003.png'), '--conf-dir', $backend, '--nprobe', '8', '--out', (Join-Path $output 'after-clean.json'))
        Run-Cli 'invalid-nprobe' @('search', (Join-Path $inputs '页图 003.png'), '--conf-dir', $backend, '--nprobe', '0') 1
        Run-Cli 'profile-mismatch' @('search', (Join-Path $inputs '页图 003.png'), '--conf-dir', $backend, '--orb-nfeatures', '600') 1
        $token = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        Run-Cli 'reject-nonloopback' @('server', '--conf-dir', $backend, '--addr', '0.0.0.0:8000') 1 @{ IMSEARCH_AUTH_TOKEN=$token }
        $endpointFile = Join-Path $output 'endpoint.txt'
        $metricsListener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        $metricsListener.Start()
        $metricsEndpoint = 'http://127.0.0.1:' + $metricsListener.LocalEndpoint.Port
        $server = Start-ImsearchProcess -Executable $executable -Arguments @('server', '--conf-dir', $backend, '--addr', '127.0.0.1:0', '--endpoint-file', $endpointFile, '--prometheus-push', $metricsEndpoint, '--prometheus-instance', 'CSharpProbe') -LogPrefix (Join-Path $output 'http-server') -EnvironmentVariables @{ IMSEARCH_AUTH_TOKEN=$token; IMSEARCH_PROMETHEUS_AUTH='probe:local-test' }
        try {
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            while (-not (Test-Path -LiteralPath $endpointFile) -and -not $server.Process.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
            if (-not (Test-Path -LiteralPath $endpointFile)) { throw 'HTTP backend did not become ready.' }
            $endpoint = (Get-Content -LiteralPath $endpointFile -Raw).TrimEnd('/')
            $handler = [System.Net.Http.HttpClientHandler]::new(); $handler.UseProxy = $false
            $client = [System.Net.Http.HttpClient]::new($handler); $client.Timeout = [TimeSpan]::FromSeconds(30)
            function Request-Backend([string]$Route, [bool]$Authenticate=$true, [string]$FilePath='', [hashtable]$Fields=@{}, [string]$Credential=$token) {
                $method = if ($FilePath) { [System.Net.Http.HttpMethod]::Post } else { [System.Net.Http.HttpMethod]::Get }
                $request = [System.Net.Http.HttpRequestMessage]::new($method, ($endpoint + $Route))
                try {
                    if ($Authenticate) { $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $Credential) }
                    if ($FilePath) {
                        $form = [System.Net.Http.MultipartFormDataContent]::new()
                        $form.Add([System.Net.Http.ByteArrayContent]::new([System.IO.File]::ReadAllBytes($FilePath)), 'file', 'query.png')
                        foreach ($key in $Fields.Keys) { $form.Add([System.Net.Http.StringContent]::new([string]$Fields[$key]), $key) }
                        $request.Content = $form
                    }
                    $response = $client.SendAsync($request).GetAwaiter().GetResult()
                    try { return [pscustomobject]@{ Status=[int]$response.StatusCode; Body=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult() } }
                    finally { $response.Dispose() }
                }
                finally { $request.Dispose() }
            }
            foreach ($route in @('/healthz','/metrics','/docs','/openapi.json')) { $unauthorized = Request-Backend $route $false; Check ('auth' + $route) ($unauthorized.Status -eq 401) 'No credentials are rejected.' }
            $wrongToken = Request-Backend '/healthz' $true '' @{} ('x' * 64); Check 'wrong-token' ($wrongToken.Status -eq 401) 'Incorrect credentials are rejected.'
            $identity = Request-Backend '/healthz'; $health = $identity.Body | ConvertFrom-Json
            Check 'health-identity' ($identity.Status -eq 200 -and $health.ready -and $health.indexed_assets -eq 8 -and $health.protocol -eq 'ehreader-imsearch-csharp/1') 'Loaded C# index identity.'
            $query = Join-Path $inputs '页图 003.png'; $requestId = [Guid]::NewGuid().ToString('N')
            $valid = Request-Backend '/search' $true $query @{ request_id=$requestId; generation_id=$health.generation_id; profile_id=$health.profile_id; nprobe='8' }
            $result = $valid.Body | ConvertFrom-Json
            Check 'http-search' ($valid.Status -eq 200 -and $result.request_id -eq $requestId -and $result.result[0][1] -eq '页图 003.png') 'Real multipart query returns the expected first image.'
            $blank = Request-Backend '/search' $true (Join-Path $synthetic 'blank.png') @{ request_id=$requestId }
            Check 'http-blank' ($blank.Status -eq 200 -and @((ConvertFrom-Json $blank.Body).result).Count -eq 0) 'Blank query is a successful empty result.'
            $invalid = Join-Path $output 'invalid.png'; [System.IO.File]::WriteAllBytes($invalid, [byte[]](1,2,3))
            $authFirst = Request-Backend '/search' $false $invalid @{ request_id=$requestId }
            Check 'authenticate-before-decode' ($authFirst.Status -eq 401) 'Unauthorized corrupt input is rejected before decode.'
            $bad = Request-Backend '/search' $true $invalid @{ request_id=$requestId }; Check 'http-invalid-image' ($bad.Status -eq 400) 'Corrupt image has a typed failure.'
            $mismatch = Request-Backend '/search' $true $query @{ request_id=$requestId; generation_id=('0' * 32) }; Check 'http-identity-mismatch' ($mismatch.Status -eq 409) 'Wrong generation is rejected.'
            $badProbe = Request-Backend '/search' $true $query @{ request_id=$requestId; nprobe='0' }; Check 'http-invalid-nprobe' ($badProbe.Status -eq 400) 'Invalid search configuration is rejected.'
            $schema = Request-Backend '/openapi.json'; $null = $schema.Body | ConvertFrom-Json; Check 'openapi' ($schema.Status -eq 200) 'API description is valid JSON.'
            $metrics = Request-Backend '/metrics'; Check 'metrics' ($metrics.Status -eq 200 -and $metrics.Body.Contains('imsearch_searches_total 2')) 'Metrics count normal and blank searches.'
            $pushDeadline = [DateTime]::UtcNow.AddSeconds(10)
            while (-not $metricsListener.Pending() -and [DateTime]::UtcNow -lt $pushDeadline) { Start-Sleep -Milliseconds 50 }
            $pushed = $false
            if ($metricsListener.Pending()) {
                $connection = $metricsListener.AcceptTcpClient()
                try {
                    $connection.ReceiveTimeout = 3000
                    $network = $connection.GetStream()
                    $reader = [System.IO.StreamReader]::new($network, [System.Text.Encoding]::ASCII, $false, 1024, $true)
                    $firstLine = $reader.ReadLine(); $contentLength = 0; $authorization = ''
                    while ($header = $reader.ReadLine()) {
                        if ($header.StartsWith('Content-Length:', [StringComparison]::OrdinalIgnoreCase)) { $contentLength = [int]$header.Substring(15).Trim() }
                        if ($header.StartsWith('Authorization:', [StringComparison]::OrdinalIgnoreCase)) { $authorization = $header.Substring(14).Trim() }
                    }
                    $characters = [char[]]::new($contentLength)
                    $received = 0
                    while ($received -lt $contentLength) { $chunk = $reader.Read($characters, $received, $contentLength - $received); if ($chunk -eq 0) { break }; $received += $chunk }
                    $payload = [string]::new($characters)
                    $reply = [System.Text.Encoding]::ASCII.GetBytes("HTTP/1.1 204 No Content`r`nContent-Length: 0`r`nConnection: close`r`n`r`n")
                    $network.Write($reply); $network.Flush()
                    $pushed = $firstLine.StartsWith('PUT /metrics/job/imsearch/instance/CSharpProbe ') -and $payload.Contains('imsearch_indexed_images 8') -and $authorization.StartsWith('Basic ')
                    $payload | Set-Content (Join-Path $output 'pushed-metrics.txt')
                    $reader.Dispose()
                }
                finally { $connection.Dispose() }
            }
            Check 'optional-metrics-push' $pushed 'Explicitly configured Prometheus push reaches a loopback test receiver.'
            $alive = Request-Backend '/healthz'; Check 'survives-invalid-input' ($alive.Status -eq 200) 'Backend survives all rejected requests.'
            $client.Dispose()
        }
        finally {
            $metricsListener.Stop()
            if (-not $server.Process.HasExited) { $server.Process.Kill($true) }
            $null = Complete-ImsearchProcess -Run $server
        }
    }
    catch { Check 'runner' $false $_.Exception.Message }
    $failed = @($checks | Where-Object { -not $_.passed }).Count
    $report = [ordered]@{ schemaVersion=1; hostPath=$executable; passed=($checks.Count - $failed); failed=$failed; checks=@($checks); finishedUtc=[DateTime]::UtcNow.ToString('o') }
    $report | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $output 'verification.json') -Encoding utf8
    Write-Output "C# backend verification: passed=$($report.passed), failed=$failed; $output"
    if ($failed -gt 0) { throw 'C# backend verification failed; read verification.json and process logs.' }
}
finally { Pop-Location }
