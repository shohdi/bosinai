param([int]$Context = 0, [int]$GpuLayers = 99)
$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
$Settings = Get-Content (Join-Path $Root 'config/settings.json') -Raw | ConvertFrom-Json
if ($Context -eq 0) { $Context = [int]$Settings.context_tokens }
$Exe = Join-Path $Root 'runtime/cuda/llama-server.exe'
$Model = Join-Path $Root 'models/Ternary-Bonsai-2-27B-PTQ1_0.gguf'
if (!(Test-Path $Exe) -or !(Test-Path $Model)) { throw 'Runtime/model missing. See README.' }
$PidPath = Join-Path $Root 'logs/model.pid'
if (Test-Path $PidPath) {
    $ExistingPid = [int](Get-Content $PidPath)
    $Existing = Get-Process -Id $ExistingPid -ErrorAction SilentlyContinue
    if ($Existing -and $Existing.Path -eq $Exe.Replace('/','\')) {
        Write-Output "Owned model process already running or loading (PID $ExistingPid)"
        exit
    }
}
try { $Health = Invoke-RestMethod 'http://127.0.0.1:8080/health' -TimeoutSec 2 } catch { $Health = $null }
if ($Health) { throw 'Port 8080 is occupied by a server not identified by logs/model.pid.' }
$KeyPath = Join-Path $Root 'data/model-api.key'
New-Item -ItemType Directory -Force (Join-Path $Root 'data') | Out-Null
if (!(Test-Path $KeyPath)) {
    $Bytes = New-Object byte[] 32
    $Rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $Rng.GetBytes($Bytes)
    $Rng.Dispose()
    [Convert]::ToBase64String($Bytes) | Set-Content $KeyPath -Encoding Ascii
}
$Arguments = @('-m', ('"' + $Model + '"'), '--alias','bonsai-local','--host','127.0.0.1','--port','8080','--cors-origins','http://127.0.0.1:8080','--api-key-file',('"' + $KeyPath + '"'),'--no-slots','-ngl',"$GpuLayers",'-fa','on','-c',"$Context",'-np','1','-b','256','-ub','128','--jinja','--temp','0.7','--top-p','0.8','--top-k','20','--min-p','0','--reasoning-budget','1024')
$Process = Start-Process -FilePath $Exe -ArgumentList $Arguments -WorkingDirectory $Root -WindowStyle Hidden -RedirectStandardOutput (Join-Path $Root 'logs/model.stdout.log') -RedirectStandardError (Join-Path $Root 'logs/model.stderr.log') -PassThru
$Process.Id | Set-Content (Join-Path $Root 'logs/model.pid')
Write-Output "Model starting (PID $($Process.Id)); logs/model.stderr.log contains loading progress."
