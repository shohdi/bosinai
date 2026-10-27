$Root = Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Force (Join-Path $Root 'data') | Out-Null
New-Item -ItemType File -Force (Join-Path $Root 'data/STOP') | Out-Null
$PidPath = Join-Path $Root 'logs/model.pid'
if (Test-Path $PidPath) {
    $ModelPid = [int](Get-Content $PidPath)
    $Process = Get-Process -Id $ModelPid -ErrorAction SilentlyContinue
    if ($Process -and $Process.Path -eq (Join-Path $Root 'runtime/cuda/llama-server.exe').Replace('/','\')) { Stop-Process -Id $ModelPid }
}
Write-Output 'Agent stop flag set. Owned model process stopped if present.'
