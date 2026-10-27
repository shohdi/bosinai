param([int]$Port = 8000, [switch]$NoBrowser)
$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path $PSScriptRoot -Parent
$Python = Join-Path $ProjectRoot '.venv/Scripts/python.exe'
if (!(Test-Path -LiteralPath $Python)) { throw 'Project .venv is missing. See README.md.' }
if ($Port -lt 1 -or $Port -gt 65535) { throw 'Port must be between 1 and 65535.' }

# Reuses the model process if it is already running.
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'start-model.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Could not start the model. Check logs/model.stderr.log.' }
$WebUrl = "http://127.0.0.1:$Port"
try { $ExistingChat = Invoke-RestMethod "$WebUrl/api/status" -TimeoutSec 3 } catch { $ExistingChat = $null }
if ($ExistingChat -and $ExistingChat.model.name -eq 'Bonsai') {
    Write-Output "Bonsai web chat is already running: $WebUrl"
    if (!$NoBrowser) { & $Python -c "import webbrowser; webbrowser.open('$WebUrl')" }
    exit 0
}
$ChatArgs = @((Join-Path $ProjectRoot 'chat_tester.py'), '--port', "$Port")
if (!$NoBrowser) { $ChatArgs += '--open' }
& $Python @ChatArgs
exit $LASTEXITCODE
