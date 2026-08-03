param(
    [string] $Target,
    [int] $Port = 0,
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'

try {
    while ($true) {
        if ([string]::IsNullOrWhiteSpace($Target)) {
            $Target = (Read-Host 'Server IPv4 or hostname (without port)').Trim()
        }
        $parsedIp = $null
        $validIp = [Net.IPAddress]::TryParse($Target, [ref]$parsedIp) -and
            $parsedIp.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork
        $validHost = [Uri]::CheckHostName($Target) -eq [UriHostNameType]::Dns
        if ($validIp -or $validHost) {
            break
        }
        Write-Host 'Enter one IPv4 address or hostname without a scheme, port, or path.' -ForegroundColor Yellow
        $Target = $null
    }

    if ($Port -eq 0) {
        while ($true) {
            $portText = Read-Host 'RDP public port [3389]'
            if ([string]::IsNullOrWhiteSpace($portText)) {
                $portText = '3389'
            }
            if ([int]::TryParse($portText, [ref]$Port) -and $Port -ge 1 -and $Port -le 65535) {
                break
            }
            Write-Host 'Enter a port from 1 to 65535.' -ForegroundColor Yellow
        }
    }
    elseif ($Port -lt 1 -or $Port -gt 65535) {
        throw 'Port must be from 1 to 65535.'
    }

    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        $desktop = [Environment]::GetFolderPath('Desktop')
        $OutputPath = Join-Path $desktop 'FRP-RDP-2560x1440.rdp'
    }
    $settings = @(
        'screen mode id:i:1'
        'use multimon:i:0'
        'desktopwidth:i:2560'
        'desktopheight:i:1440'
        'session bpp:i:32'
        'smart sizing:i:1'
        'dynamic resolution:i:0'
        'compression:i:1'
        'keyboardhook:i:2'
        'audiocapturemode:i:0'
        'audiomode:i:0'
        'redirectclipboard:i:1'
        'redirectprinters:i:0'
        'redirectsmartcards:i:0'
        'networkautodetect:i:1'
        'bandwidthautodetect:i:1'
        'authentication level:i:2'
        'prompt for credentials:i:1'
        'enablecredsspsupport:i:1'
        'gatewayusagemethod:i:4'
        "full address:s:${Target}:$Port"
    )

    Set-Content -LiteralPath $OutputPath -Value $settings -Encoding Unicode -Force
    Write-Host ('[OK] RDP file created: ' + $OutputPath) -ForegroundColor Green
    exit 0
}
catch {
    Write-Host ('[ERROR] ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
