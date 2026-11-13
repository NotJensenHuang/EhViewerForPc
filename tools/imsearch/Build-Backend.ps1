param([switch]$Aot, [string]$OutputDirectory = '.qa/image-search/build')
. (Join-Path $PSScriptRoot 'Process.ps1')
$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location $repository
try {
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $arguments = @('build', 'src/EhViewerReader.ImageSearch.Host/EhViewerReader.ImageSearch.Host.csproj', '-c', 'Debug')
    if ($Aot) { $arguments = @('publish', 'src/EhViewerReader.ImageSearch.Host/EhViewerReader.ImageSearch.Host.csproj', '-c', 'Release', '-r', 'win-x64', '-p:PublishAot=true', '-o', 'artifacts/image-search-host-aot') }
    $exitCode = Invoke-ImsearchProcess -Executable 'dotnet' -Arguments $arguments -LogPrefix (Join-Path $OutputDirectory 'backend') -TimeoutSeconds 1200
    $hostFile = if ($Aot) { 'artifacts/image-search-host-aot/EhViewerReader.ImageSearch.Host.exe' } else { 'src/EhViewerReader.ImageSearch.Host/bin/Debug/net11.0/EhViewerReader.ImageSearch.Host.exe' }
    $binaryHash = if ($exitCode -eq 0 -and (Test-Path -LiteralPath $hostFile)) { (Get-FileHash -LiteralPath $hostFile -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
    [ordered]@{ aot=[bool]$Aot; exitCode=$exitCode; executableSha256=$binaryHash; finishedUtc=[DateTime]::UtcNow.ToString('o'); logPrefix=(Join-Path $OutputDirectory 'backend') } | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'build.json')
    if ($exitCode -ne 0) { throw "C# backend build failed ($exitCode). Read the build logs." }
}
finally { Pop-Location }
