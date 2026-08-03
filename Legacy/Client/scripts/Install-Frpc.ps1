param(
    [Parameter(Mandatory)] [string] $InstallDirectory,
    [Parameter(Mandatory)] [string] $ServerAddress,
    [Parameter(Mandatory)] [ValidateRange(1, 65535)] [int] $ServerPort,
    [Parameter(Mandatory)] [string] $Token,
    [Parameter(Mandatory)] [ValidateRange(1, 65535)] [int] $RemoteRdpPort
)

$ErrorActionPreference = 'Stop'
$Version = '0.65.0'

if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'This package currently supports 64-bit Windows only.'
}

$archiveName = "frp_${Version}_windows_amd64.zip"
$releaseDirectoryName = "frp_${Version}_windows_amd64"
$clientDirectory = Split-Path -Parent $PSScriptRoot
$localArchive = Join-Path $clientDirectory $archiveName
$downloadUrl = "https://github.com/fatedier/frp/releases/download/v${Version}/${archiveName}"
$frpcPath = Join-Path $InstallDirectory 'frpc.exe'
$configPath = Join-Path $InstallDirectory 'frpc.toml'
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ('frpc-install-' + [guid]::NewGuid().ToString('N'))

New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null

$installedVersion = $null
if (Test-Path -LiteralPath $frpcPath -PathType Leaf) {
    $installedVersion = (& $frpcPath --version 2>&1 | Select-Object -First 1).ToString().Trim()
}

if ($installedVersion -ne $Version) {
    New-Item -ItemType Directory -Path $temporaryDirectory -Force | Out-Null
    try {
        $temporaryArchive = Join-Path $temporaryDirectory $archiveName
        if (Test-Path -LiteralPath $localArchive -PathType Leaf) {
            Write-Host ('[INFO] Using local archive: ' + $localArchive)
            Copy-Item -LiteralPath $localArchive -Destination $temporaryArchive -Force
        }
        else {
            Write-Host ('[INFO] Downloading ' + $downloadUrl)
            Invoke-WebRequest -Uri $downloadUrl -OutFile $temporaryArchive -UseBasicParsing
        }

        Expand-Archive -LiteralPath $temporaryArchive -DestinationPath $temporaryDirectory -Force
        $extractedDirectory = Join-Path $temporaryDirectory $releaseDirectoryName
        $extractedFrpc = Join-Path $extractedDirectory 'frpc.exe'
        if (-not (Test-Path -LiteralPath $extractedFrpc -PathType Leaf)) {
            throw "The downloaded archive does not contain $releaseDirectoryName\frpc.exe."
        }

        if (Test-Path -LiteralPath $frpcPath -PathType Leaf) {
            $backupName = 'frpc.exe.backup.' + (Get-Date -Format 'yyyyMMdd-HHmmss')
            Copy-Item -LiteralPath $frpcPath -Destination (Join-Path $InstallDirectory $backupName)
        }

        Copy-Item -LiteralPath $extractedFrpc -Destination $frpcPath -Force
        $licensePath = Join-Path $extractedDirectory 'LICENSE'
        if (Test-Path -LiteralPath $licensePath -PathType Leaf) {
            Copy-Item -LiteralPath $licensePath -Destination (Join-Path $InstallDirectory 'LICENSE') -Force
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryDirectory) {
            Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
        }
    }
}
else {
    Write-Host ("[OK] FRPC $Version is already installed.") -ForegroundColor Green
}

if (Test-Path -LiteralPath $configPath -PathType Leaf) {
    $backupPath = $configPath + '.backup.' + (Get-Date -Format 'yyyyMMdd-HHmmss')
    Copy-Item -LiteralPath $configPath -Destination $backupPath
    Write-Host ('[INFO] Existing configuration backed up to ' + $backupPath)
}

$configLines = @(
    "serverAddr = `"$ServerAddress`""
    "serverPort = $ServerPort"
    ''
    '[auth]'
    "token = `"$Token`""
    ''
    '[[proxies]]'
    'name = "rdp-tcp"'
    'type = "tcp"'
    'localIP = "127.0.0.1"'
    'localPort = 3389'
    "remotePort = $RemoteRdpPort"
    ''
    '[[proxies]]'
    'name = "rdp-udp"'
    'type = "udp"'
    'localIP = "127.0.0.1"'
    'localPort = 3389'
    "remotePort = $RemoteRdpPort"
)

$utf8WithoutBom = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllLines($configPath, $configLines, $utf8WithoutBom)

$verifyOutput = & $frpcPath verify -c $configPath 2>&1
if ($LASTEXITCODE -ne 0) {
    throw ('FRPC configuration verification failed: ' + ($verifyOutput -join ' '))
}

Write-Host ($verifyOutput -join [Environment]::NewLine)
Write-Host '[OK] FRPC installation and configuration completed.' -ForegroundColor Green
