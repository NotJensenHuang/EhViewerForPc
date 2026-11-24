#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $OrtPackage,
    [string] $DirectMlArchive = (Join-Path $PSScriptRoot '../../.qa/translation-backend/downloads/onnxruntime_directml-1.24.4-cp311-cp311-win_amd64.whl'),
    [string] $LlamaArchive = (Join-Path $PSScriptRoot '../../.qa/translation-backend/downloads/llama.zip'),
    [string] $VcRedistDirectory,
    [string] $Destination = (Join-Path $PSScriptRoot '../../artifacts/translation-native')
)

$ErrorActionPreference = 'Stop'
$Destination = [IO.Path]::GetFullPath($Destination)

function Assert-Archive([string] $Path, [string] $Sha256) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-FileHash -LiteralPath $Path).Hash -ne $Sha256) {
        throw "缺少固定版本的归档文件，或 SHA-256 不符：$Path"
    }
}

function Export-Entry($Archive, [string] $Entry, [string] $OutputDirectory, [string] $Name) {
    $item = $Archive.GetEntry($Entry)
    if ($null -eq $item) { throw "已校验归档缺少 $Entry" }
    $target = Join-Path $OutputDirectory $Name
    $previous = "$target.new"
    try {
        [IO.Compression.ZipFileExtensions]::ExtractToFile($item, $previous, $true)
        Move-Item -LiteralPath $previous -Destination $target -Force
    }
    finally {
        Remove-Item -LiteralPath $previous -ErrorAction SilentlyContinue
    }
    return $target
}

if ([string]::IsNullOrWhiteSpace($OrtPackage)) {
    $packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
    $OrtPackage = Join-Path $packages 'microsoft.ml.onnxruntime.directml/1.23.0/microsoft.ml.onnxruntime.directml.1.23.0.nupkg'
}
# https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.DirectML/1.23.0
Assert-Archive $OrtPackage 'a33ec2382b3c440bab74042a135733bb6e5085f293b908d3997688a58fe307e7'
# 该已锁定的官方归档仅用于取出 DirectML 1.15.4 DLL。
# https://pypi.org/project/onnxruntime-directml/1.24.4/#files
Assert-Archive $DirectMlArchive '96642a787e5a6f33bf043521c0f06eb1eb663f6b830e5862a2026d03f9c90543'
# https://github.com/ggml-org/llama.cpp/releases/tag/b12650
Assert-Archive $LlamaArchive '6dea3c0aa58319337bfbe32046d3b36573e8ac742ebefbccc6dedd9b283b6876'

if ([string]::IsNullOrWhiteSpace($VcRedistDirectory)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
        $match = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Redist.14.Latest -find 'VC\Redist\MSVC\*\x64\Microsoft.VC145.CRT\msvcp140.dll' | Select-Object -First 1
        if ($match) { $VcRedistDirectory = Split-Path -Parent $match }
    }
}
if ([string]::IsNullOrWhiteSpace($VcRedistDirectory)) {
    throw '需要已获得授权的稳定版 Visual Studio 2026 x64 Microsoft.VC145.CRT 再分发目录。'
}
$crtNames = @('msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')
foreach ($name in $crtNames) {
    if (!(Test-Path -LiteralPath (Join-Path $VcRedistDirectory $name) -PathType Leaf)) { throw "再分发目录缺少 $name" }
}

$ort = Join-Path $Destination 'ort'
$gguf = Join-Path $Destination 'gguf'
New-Item -ItemType Directory -Path $ort, $gguf -Force | Out-Null
$prepared = [Collections.Generic.List[string]]::new()

$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($OrtPackage))
try {
    foreach ($name in @('onnxruntime.dll', 'onnxruntime_providers_shared.dll')) {
        $prepared.Add((Export-Entry $archive "runtimes/win-x64/native/$name" $ort $name))
    }
    $prepared.Add((Export-Entry $archive 'LICENSE' $ort 'onnxruntime-LICENSE'))
    $prepared.Add((Export-Entry $archive 'ThirdPartyNotices.txt' $ort 'onnxruntime-ThirdPartyNotices.txt'))
}
finally { $archive.Dispose() }

$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($DirectMlArchive))
try { $prepared.Add((Export-Entry $archive 'onnxruntime/capi/DirectML.dll' $ort 'DirectML.dll')) }
finally { $archive.Dispose() }

$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($LlamaArchive))
try {
    foreach ($name in @('llama.dll', 'ggml.dll', 'ggml-base.dll', 'ggml-vulkan.dll', 'ggml-cpu-x64.dll', 'libomp.dll', 'LICENSE-LLVM-OpenMP')) {
        $prepared.Add((Export-Entry $archive $name $gguf $name))
    }
}
finally { $archive.Dispose() }

foreach ($directory in @($ort, $gguf)) {
    foreach ($name in $crtNames) {
        $target = Join-Path $directory $name
        Copy-Item -LiteralPath (Join-Path $VcRedistDirectory $name) -Destination $target -Force
        $prepared.Add($target)
    }
}
foreach ($notice in @(@('DirectML-NOTICE.txt', $ort), @('llama-LICENSE.txt', $gguf), @('VC-REDIST-NOTICE.txt', $Destination))) {
    $target = Join-Path $notice[1] $notice[0]
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $notice[0]) -Destination $target -Force
    $prepared.Add($target)
}

$rows = foreach ($file in $prepared) {
    $relative = [IO.Path]::GetRelativePath($Destination, $file).Replace('\', '/')
    '{0}  {1}' -f (Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant(), $relative
}
Set-Content -LiteralPath (Join-Path $Destination 'sha256.txt') -Value $rows -Encoding ascii
Write-Output $Destination
