$ErrorActionPreference = 'Stop'

function Read-WithDefault {
    param(
        [Parameter(Mandatory)] [string] $Prompt,
        [Parameter(Mandatory)] [string] $Default
    )

    $value = Read-Host "$Prompt [$Default]"
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $Default
    }
    return $value.Trim()
}

function Read-Port {
    param(
        [Parameter(Mandatory)] [string] $Prompt,
        [Parameter(Mandatory)] [int] $Default
    )

    while ($true) {
        $rawValue = Read-WithDefault -Prompt $Prompt -Default ([string]$Default)
        $port = 0
        if ([int]::TryParse($rawValue, [ref]$port) -and $port -ge 1 -and $port -le 65535) {
            return $port
        }
        Write-Host 'Enter a port from 1 to 65535.' -ForegroundColor Yellow
    }
}

function ConvertTo-PlainText {
    param([Parameter(Mandatory)] [Security.SecureString] $SecureString)

    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Administrator permission is required.'
    }

    $clientDirectory = $PSScriptRoot
    $installScript = Join-Path $clientDirectory 'scripts\Install-Frpc.ps1'
    $rdpScript = Join-Path $clientDirectory 'scripts\Configure-Rdp.ps1'
    $startupScript = Join-Path $clientDirectory 'Install-FrpcStartup.ps1'

    foreach ($requiredScript in @($installScript, $rdpScript, $startupScript)) {
        if (-not (Test-Path -LiteralPath $requiredScript -PathType Leaf)) {
            throw "Required script was not found: $requiredScript"
        }
    }

    $frpVersion = '0.65.0'
    $defaultInstallDirectory = 'C:\Program Files\FRP'
    $installDirectory = Read-WithDefault -Prompt 'FRPC install directory' -Default $defaultInstallDirectory

    while ($true) {
        $serverAddress = (Read-Host 'FRPS public IPv4 or hostname (without port)').Trim()
        $parsedIp = $null
        $validIp = [Net.IPAddress]::TryParse($serverAddress, [ref]$parsedIp) -and
            $parsedIp.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork
        $validHost = [Uri]::CheckHostName($serverAddress) -eq [UriHostNameType]::Dns
        if ($validIp -or $validHost) {
            break
        }
        Write-Host 'Enter one IPv4 address or hostname without a scheme, port, or path.' -ForegroundColor Yellow
    }

    $serverPort = Read-Port -Prompt 'FRPS control port' -Default 7000
    while ($true) {
        $remoteRdpPort = Read-Port -Prompt 'Public RDP forwarding port (TCP/UDP)' -Default 3389
        if ($remoteRdpPort -ne $serverPort) {
            break
        }
        Write-Host 'The control port and RDP forwarding port must be different.' -ForegroundColor Yellow
    }

    $secureToken = Read-Host 'Authentication token (same as FRPS)' -AsSecureString
    $token = ConvertTo-PlainText -SecureString $secureToken
    if ($token -notmatch '^[A-Za-z0-9._-]{16,128}$') {
        throw 'The token must contain 16-128 letters, numbers, dots, underscores, or hyphens.'
    }

    & $installScript `
        -InstallDirectory $installDirectory `
        -ServerAddress $serverAddress `
        -ServerPort $serverPort `
        -Token $token `
        -RemoteRdpPort $remoteRdpPort

    & $rdpScript

    $frpcPath = Join-Path $installDirectory 'frpc.exe'
    & powershell.exe `
        -NoLogo `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $startupScript `
        -FrpDirectory $installDirectory `
        -TaskName 'FRP Client'
    if ($LASTEXITCODE -ne 0) {
        throw "Startup task installation failed with exit code $LASTEXITCODE."
    }

    Write-Host
    Write-Host '[OK] FRP RDP client setup completed.' -ForegroundColor Green
    Write-Host ('FRPC:              ' + $frpcPath)
    Write-Host ('FRP version:       ' + $frpVersion)
    Write-Host ('FRPS control port: ' + $serverPort)
    Write-Host ('Configuration:     ' + (Join-Path $installDirectory 'frpc.toml'))
    Write-Host ('RDP public target: ' + $serverAddress + ':' + $remoteRdpPort)
    Write-Host 'Use this target from the controlling computer after the cloud security group is ready.'
    exit 0
}
catch {
    Write-Host ('[ERROR] ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
