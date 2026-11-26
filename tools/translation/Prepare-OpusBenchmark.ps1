param(
    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$revision = '8d2be98afc1636a4304936a2c5a013d10e02720e'
$baseUri = "https://huggingface.co/Helsinki-NLP/opus-mt-en-zh/resolve/$revision"
$files = [ordered]@{
    'config.json' = 'BCAE8ED74FED77FB51C58462B62397FEE6B1A1A34AECE79183A0DD02AD329E71'
    'generation_config.json' = '837839ED0534A27084F9B980FC33F47729052DD89CB26CCA7AC830765ED30E49'
    'source.spm' = '5775DDC9E3FF2FAE91554DA56468AD35FF56EDABA870FEA74447BC7234BFDAA8'
    'target.spm' = '81DC94EFA84E4025EF38D25D5D07429FE41E3EB29D44003F1DB6FE98487B0052'
    'tokenizer_config.json' = '5DF181FBA586B7CB37D429C13C4F96CB43FB11DE7BB6489733D9EDED32418179'
    'vocab.json' = 'F1832CA38F7AA158B9A944FF583E4901E6B37D9D4AD9241F03838E636E9CFB03'
    'model.safetensors' = 'FF99AAF052D1DB882CFBC1DC8DBA7D0978A30B128409721A6F8B979532893034'
}

$null = [IO.Directory]::CreateDirectory($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination))
foreach ($file in $files.Keys) {
    $final = Join-Path $Destination $file
    if (Test-Path -LiteralPath $final) {
        if ((Get-FileHash -LiteralPath $final -Algorithm SHA256).Hash -ne $files[$file]) {
            throw "目标目录已存在摘要不同的文件：$final"
        }
        continue
    }

    $temporary = "$final.part"
    curl.exe -fsSL --continue-at - --connect-timeout 30 --output $temporary "$baseUri/$file"
    if ($LASTEXITCODE -ne 0) {
        throw "下载失败，保留可续传文件：$temporary"
    }
    if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $files[$file]) {
        throw "下载的文件摘要不符：$temporary"
    }
    Move-Item -LiteralPath $temporary -Destination $final
}

Write-Output "已核验 $revision 的 OPUS-MT 推理文件。"
