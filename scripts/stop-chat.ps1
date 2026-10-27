param([int]$Port = 8000)
$ErrorActionPreference = 'Stop'
if ($Port -lt 1 -or $Port -gt 65535) { throw 'Port must be between 1 and 65535.' }
$ProjectRoot = Split-Path $PSScriptRoot -Parent
$ChatScript = Join-Path $ProjectRoot 'chat_tester.py'
$Listeners = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)
if (!$Listeners) { Write-Output "No web server is listening on port $Port."; exit 0 }
$OwnerIds = @($Listeners | Select-Object -ExpandProperty OwningProcess -Unique)

# Verify every process before stopping anything; never stop an unrelated port owner.
foreach ($OwnerId in $OwnerIds) {
    $WebOwner = Get-CimInstance Win32_Process -Filter "ProcessId = $OwnerId"
    if (!$WebOwner -or !$WebOwner.CommandLine -or
        $WebOwner.CommandLine.IndexOf($ChatScript, [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
        [IO.Path]::GetFileName($WebOwner.ExecutablePath) -notin @('python.exe', 'pythonw.exe')) {
        throw "Port $Port belongs to another application. Stop that application yourself."
    }
}
foreach ($OwnerId in $OwnerIds) { Stop-Process -Id $OwnerId -ErrorAction Stop }
Write-Output 'Bonsai web server stopped. Saved chats and the local model remain available.'
