[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('ppocr', 'baberu', 'hayai', 'manga-ocr')]
    [string] $Candidate,
    [Parameter(Mandatory)]
    [string] $Destination
)

$ErrorActionPreference = 'Stop'
$assets = Import-Csv -LiteralPath (Join-Path $PSScriptRoot 'ocr-artifacts.tsv') -Delimiter "`t" |
    Where-Object { $_.Candidate -eq $Candidate }

function Test-Asset([string] $Path, [pscustomobject] $Expected) {
    $item = Get-Item -LiteralPath $Path -ErrorAction SilentlyContinue
    return $null -ne $item -and !$item.PSIsContainer -and $item.Length -eq [long] $Expected.Bytes -and
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ieq $Expected.Sha256
}

foreach ($asset in $assets) {
    $path = Join-Path $Destination $asset.Path
    if (Test-Asset $path $asset) {
        Write-Output "Verified $path"
        continue
    }
    if (Test-Path -LiteralPath $path) { throw "Existing file failed verification: $path" }
    $partial = "$path.partial"
    New-Item -ItemType Directory -Force (Split-Path $path -Parent) | Out-Null
    if (-not (Test-Asset $partial $asset)) {
        $url = "https://huggingface.co/$($asset.Repository)/resolve/$($asset.Revision)/$($asset.Path)?download=true"
        & curl.exe --fail --silent --show-error --location --max-time 600 --continue-at - --output $partial $url
        if ($LASTEXITCODE -ne 0) { throw "Download interrupted ($LASTEXITCODE): $partial" }
        if (-not (Test-Asset $partial $asset)) { throw "Downloaded file failed verification: $partial" }
    }
    Move-Item -LiteralPath $partial -Destination $path
    Write-Output "Verified $path"
}
