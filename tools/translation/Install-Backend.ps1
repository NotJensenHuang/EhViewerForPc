#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Destination,
    [string] $BootstrapPython = 'python',
    [switch] $OcrOnly
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$PSNativeCommandUseErrorActionPreference = $false
$destinationRoot = [IO.Path]::GetFullPath($Destination)
$downloadDirectory = Join-Path $destinationRoot 'downloads'
$modelsDirectory = Join-Path $destinationRoot 'models'
$pythonDirectory = Join-Path $destinationRoot 'python'
$llamaDirectory = Join-Path $destinationRoot 'llama'
New-Item -ItemType Directory -Path $destinationRoot, $downloadDirectory, $modelsDirectory -Force | Out-Null

function Get-Asset {
    param([string[]] $Uris, [string] $File, [string] $Sha256, [long] $Bytes = 0, [int] $ChunkMiB = 2, [int] $Workers = 4)

    if (Test-Path -LiteralPath $File -PathType Leaf) {
        if ($Sha256 -eq (Get-FileHash -LiteralPath $File).Hash) {
            return
        }
    }

    $temporaryFile = "$File.download"
    foreach ($uri in $Uris) {
        if ($Bytes -gt 0) {
            $rangeDownloader = Join-Path $PSScriptRoot 'Download-Asset.py'
            & $BootstrapPython -I $rangeDownloader $uri $temporaryFile $Sha256 $Bytes ($ChunkMiB * 1MB) $Workers
            if ($LASTEXITCODE -eq 0) {
                Move-Item -LiteralPath $temporaryFile -Destination $File -Force
                return
            }
        }
        curl.exe --fail --silent --show-error --location --proto '=https' --proto-redir '=https' --retry 2 --connect-timeout 20 --max-time 1200 --speed-time 30 --speed-limit 1024 --continue-at - --output $temporaryFile $uri
        if ($LASTEXITCODE -ne 0) {
            continue
        }
        if ($Sha256 -eq (Get-FileHash -LiteralPath $temporaryFile).Hash) {
            Move-Item -LiteralPath $temporaryFile -Destination $File -Force
            return
        }
        Remove-Item -LiteralPath $temporaryFile
    }
    throw "下载或校验失败 $File"
}

$pythonArchive = Join-Path $downloadDirectory 'python.zip'
# CPython 由 Python Software Foundation 发布，内嵌包带有许可文件。
Get-Asset 'https://www.python.org/ftp/python/3.11.9/python-3.11.9-embed-amd64.zip' $pythonArchive '009d6bf7e3b2ddca3d784fa09f90fe54336d5b60f0e0f305c37f400bf83cfd3b' -Bytes 11249023
Expand-Archive -LiteralPath $pythonArchive -DestinationPath $pythonDirectory -Force
Set-Content -LiteralPath (Join-Path $pythonDirectory 'python311._pth') -Value @('python311.zip', '.', 'Lib\site-packages') -Encoding ascii

$wheelTargets = @('--platform', 'win_amd64', '--implementation', 'cp', '--python-version', '3.11', '--abi', 'cp311', '--only-binary=:all:', '--require-hashes')
$requirements = Join-Path $PSScriptRoot 'requirements.txt'
$pipOptions = @('-I', '-m', 'pip', '--isolated', '--disable-pip-version-check', '--no-cache-dir')
$installWheels = @('install', '--no-index', '--find-links', $downloadDirectory, '--no-compile', '--upgrade', '--ignore-installed', '--no-warn-conflicts', '--target', (Join-Path $pythonDirectory 'Lib\site-packages'))
$wheelCount = @(Get-ChildItem -LiteralPath $downloadDirectory -Filter '*.whl' -File).Count
$requirementCount = @(Get-Content -LiteralPath $requirements | Where-Object { $_ -match '^[\w.-]+==' }).Count
$wheelInstallSucceeded = $false
if ($wheelCount -ge $requirementCount) {
    & $BootstrapPython @pipOptions @installWheels @wheelTargets --requirement $requirements
    $wheelInstallSucceeded = $LASTEXITCODE -eq 0
}
if (-not $wheelInstallSucceeded) {
    foreach ($index in @('https://mirrors.cloud.tencent.com/pypi/simple', 'https://pypi.org/simple')) {
        & $BootstrapPython @pipOptions download @wheelTargets --dest $downloadDirectory --index-url $index --requirement $requirements
        if ($LASTEXITCODE -eq 0) {
            break
        }
    }
    if ($LASTEXITCODE -ne 0) {
        throw '下载本地 OCR 运行时依赖失败'
    }
    & $BootstrapPython @pipOptions @installWheels @wheelTargets --requirement $requirements
}
if ($LASTEXITCODE -ne 0) {
    throw '安装本地 OCR 运行时依赖失败'
}

# RapidOCR 发布的 PaddleOCR PP-OCRv5 ONNX 转换与字典沿用 Apache-2.0。
# https://github.com/RapidAI/RapidOCR/blob/main/python/MODEL_LICENSES.md
$rapidRoot = 'https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0'
Get-Asset "$rapidRoot/onnx/PP-OCRv5/det/ch_PP-OCRv5_det_mobile.onnx" (Join-Path $modelsDirectory 'det.onnx') '4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae'
Get-Asset "$rapidRoot/onnx/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile.onnx" (Join-Path $modelsDirectory 'rec.onnx') '5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5'
Get-Asset "$rapidRoot/paddle/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile/ppocrv5_dict.txt" (Join-Path $modelsDirectory 'rec-dict.txt') 'd1979e9f794c464c0d2e0b70a7fe14dd978e9dc644c0e71f14158cdf8342af1b'

if (-not $OcrOnly) {
    $llamaArchive = Join-Path $downloadDirectory 'llama.zip'
    # llama.cpp Vulkan 的哈希取自 b12650 GitHub 正式发行，项目采用 MIT 许可。
    Get-Asset 'https://github.com/ggml-org/llama.cpp/releases/download/b12650/llama-b12650-bin-win-vulkan-x64.zip' $llamaArchive '6dea3c0aa58319337bfbe32046d3b36573e8ac742ebefbccc6dedd9b283b6876' -Bytes 38939284
    Expand-Archive -LiteralPath $llamaArchive -DestinationPath $llamaDirectory -Force
    # 腾讯 Hy-MT2 权重采用 Apache-2.0，两处下载均核对 Hugging Face 上游哈希。
    $translationModelSources = @(
        'https://www.modelscope.cn/models/Tencent-Hunyuan/Hy-MT2-1.8B-GGUF/resolve/master/Hy-MT2-1.8B-Q4_K_M.gguf'
        'https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF/resolve/main/Hy-MT2-1.8B-Q4_K_M.gguf'
    )
    Get-Asset $translationModelSources (Join-Path $modelsDirectory 'Hy-MT2-1.8B-Q4_K_M.gguf') 'dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699' -Bytes 1133080448 -ChunkMiB 8 -Workers 8
    if (-not (Test-Path -LiteralPath (Join-Path $llamaDirectory 'llama-server.exe') -PathType Leaf)) {
        throw 'llama.cpp 压缩包缺少 llama-server.exe'
    }
}

$pythonRuntime = Join-Path $pythonDirectory 'python.exe'
& $pythonRuntime -I -c 'import cv2, numpy, onnxruntime, PIL; assert "DmlExecutionProvider" in onnxruntime.get_available_providers()'
if ($LASTEXITCODE -ne 0) {
    throw '隔离的 Windows Python 缺少 DirectML 支持'
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'worker.py') -Destination (Join-Path $destinationRoot 'worker.py') -Force
Write-Output $destinationRoot
