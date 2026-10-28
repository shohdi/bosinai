$ErrorActionPreference = 'Stop'

# Keep the installed site's browser files aligned with the source checkout.
# A standalone Published directory can still run without source files beside it.
$sourceRoot = Join-Path $PSScriptRoot 'Bonsai.Chat\wwwroot'
$publishedRoot = Join-Path $PSScriptRoot 'Published'
if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'index.html') -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $publishedRoot 'Bonsai.Chat.exe') -PathType Leaf)) {
    exit 0
}

$destinationRoot = Join-Path $publishedRoot 'wwwroot'
$null = New-Item -ItemType Directory -Path $destinationRoot -Force
foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -File -Recurse) {
    $relativePath = $file.FullName.Substring($sourceRoot.Length).TrimStart('\', '/')
    $destination = Join-Path $destinationRoot $relativePath
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
    if (-not (Test-Path -LiteralPath $destination -PathType Leaf) -or
        (Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }
}
