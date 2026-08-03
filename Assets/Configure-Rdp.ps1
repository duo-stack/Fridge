$ErrorActionPreference = 'Stop'

$terminalServerKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
$rdpTcpKey = Join-Path $terminalServerKey 'WinStations\RDP-Tcp'

if (Test-Path -LiteralPath $terminalServerKey) {
    Set-ItemProperty -LiteralPath $terminalServerKey -Name fDenyTSConnections -Type DWord -Value 0
}
if (Test-Path -LiteralPath $rdpTcpKey) {
    Set-ItemProperty -LiteralPath $rdpTcpKey -Name UserAuthentication -Type DWord -Value 1
    Set-ItemProperty -LiteralPath $rdpTcpKey -Name SecurityLayer -Type DWord -Value 2
}

$firewallRules = @(Get-NetFirewallRule -Name 'RemoteDesktop*' -ErrorAction SilentlyContinue)
if ($firewallRules.Count -gt 0) {
    $firewallRules | Enable-NetFirewallRule
}
else {
    Write-Host '[WARN] Built-in Remote Desktop firewall rules were not found.' -ForegroundColor Yellow
}

$termService = Get-Service -Name TermService -ErrorAction SilentlyContinue
if ($null -ne $termService) {
    Set-Service -Name TermService -StartupType Automatic
    if ($termService.Status -ne 'Running') {
        Start-Service -Name TermService
    }
}

$portCheck = Test-NetConnection 127.0.0.1 -Port 3389 -WarningAction SilentlyContinue
if (-not $portCheck.TcpTestSucceeded) {
    throw 'No RDP service is listening on localhost port 3389. Resolve the RDP host capability for this Windows installation, then run setup again.'
}

Write-Host '[OK] A usable RDP service is available on this computer.' -ForegroundColor Green
Write-Host '[INFO] Built-in RDP, NLA, TLS, and firewall settings were applied when available.'
Write-Host '[OK] Local TCP port 3389 is listening.' -ForegroundColor Green
