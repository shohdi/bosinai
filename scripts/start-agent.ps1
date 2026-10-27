param([switch]$Once)
$Root = Split-Path $PSScriptRoot -Parent
Set-Location $Root
if ($Once) { & "$Root/.venv/Scripts/python.exe" "$Root/src/agent.py" --once }
else { & "$Root/.venv/Scripts/python.exe" "$Root/src/agent.py" }
