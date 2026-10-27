$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
if (Test-Path (Join-Path $Root 'data/STOP')) { throw 'Paused by data/STOP. Remove that file to resume intentionally.' }
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'start-model.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Model startup failed.' }
$Python = Join-Path $Root '.venv/Scripts/python.exe'
$Agent = Join-Path $Root 'src/agent.py'
$PidPath = Join-Path $Root 'logs/agent.pid'
if (Test-Path $PidPath) {
    $OldPid = [int](Get-Content $PidPath)
    $Old = Get-CimInstance Win32_Process -Filter "ProcessId = $OldPid"
    if ($Old -and $Old.CommandLine -like '*bosinai*src*agent.py*') { Write-Output 'Agent already running'; exit }
}
$Process = Start-Process -FilePath $Python -ArgumentList @('-u',('"' + $Agent + '"')) -WorkingDirectory $Root -WindowStyle Hidden -RedirectStandardOutput (Join-Path $Root 'logs/agent.stdout.log') -RedirectStandardError (Join-Path $Root 'logs/agent.stderr.log') -PassThru
$Process.Id | Set-Content $PidPath
Write-Output "Paper/research agent started (PID $($Process.Id)), five-minute cycles."
