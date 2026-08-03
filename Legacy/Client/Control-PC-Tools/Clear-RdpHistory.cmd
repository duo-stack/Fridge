@echo off
setlocal
title Clear One Remote Desktop Target

rem Fill in one IPv4 address or hostname between = and the closing quote.
rem Examples: 203.0.113.10 or rdp.example.com
set "TARGET="

set "RDP_CLEANER_SELF=%~f0"
set "RDP_CLEANER_TEMP=%TEMP%\Clear-RdpHistory-%RANDOM%-%RANDOM%.ps1"

echo ========================================
echo   Clear One Remote Desktop Target
echo ========================================
echo.

powershell.exe -NoLogo -NoProfile -Command "$lines = Get-Content -LiteralPath $env:RDP_CLEANER_SELF; $marker = [Array]::IndexOf($lines, '#==POWERSHELL_SCRIPT=='); if ($marker -lt 0) { exit 3 }; $body = $lines[($marker + 1)..($lines.Count - 1)]; Set-Content -LiteralPath $env:RDP_CLEANER_TEMP -Value $body -Encoding UTF8"

if errorlevel 1 (
  echo [ERROR] Could not extract the embedded cleanup script.
  set "RESULT=3"
  goto :finish
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%RDP_CLEANER_TEMP%"
set "RESULT=%ERRORLEVEL%"

del /q "%RDP_CLEANER_TEMP%" >nul 2>&1

:finish
echo.
if "%RESULT%"=="0" (
  echo Result: SUCCESS
) else if "%RESULT%"=="2" (
  echo Result: NOT RUN - see the message above
) else (
  echo Result: FAILED ^(exit code %RESULT%^)
)
echo.
echo Press any key to close this window...
pause >nul
exit /b %RESULT%

#==POWERSHELL_SCRIPT==
$ErrorActionPreference = 'Stop'

try {
    $running = @(Get-Process -Name mstsc -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        Write-Host '[BLOCKED] mstsc.exe is still running.' -ForegroundColor Yellow
        Write-Host 'Close all Remote Desktop windows, then run this file again.'
        exit 2
    }

    $target = [string]$env:TARGET
    if ([string]::IsNullOrWhiteSpace($target)) {
        throw 'TARGET is empty. Edit this CMD file and fill in one IPv4 address or hostname first.'
    }

    $target = $target.Trim()
    $parsedIp = $null
    $isValidIp = [System.Net.IPAddress]::TryParse($target, [ref]$parsedIp) -and
        $parsedIp.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork

    if ($isValidIp) {
        $target = $parsedIp.IPAddressToString
        $targetType = 'IPv4'
    }
    elseif ([System.Uri]::CheckHostName($target) -eq [System.UriHostNameType]::Dns) {
        $target = $target.TrimEnd('.').ToLowerInvariant()
        $targetType = 'Hostname'
    }
    else {
        throw 'TARGET must be one IPv4 address or hostname without a port, scheme, or path.'
    }

    $escapedTarget = [regex]::Escape($target)
    $targetPattern = '^' + $escapedTarget + '(?::\d+)?$'
    $defaultKey = 'HKCU:\Software\Microsoft\Terminal Server Client\Default'
    $serversKey = 'HKCU:\Software\Microsoft\Terminal Server Client\Servers'
    $defaultRdp = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Default.rdp'

    $mruNames = @()
    $matchedAddresses = @()
    if (Test-Path -LiteralPath $defaultKey) {
        $defaultItem = Get-Item -LiteralPath $defaultKey
        $mruNames = @($defaultItem.GetValueNames() | Where-Object {
            if ($_ -notlike 'MRU*') {
                return $false
            }

            $value = [string]$defaultItem.GetValue($_)
            return $value -match $targetPattern
        })

        $matchedAddresses += @($mruNames | ForEach-Object {
            [string]$defaultItem.GetValue($_)
        })

        foreach ($name in $mruNames) {
            Remove-ItemProperty -LiteralPath $defaultKey -Name $name -ErrorAction Stop
        }
    }

    $serverEntries = @()
    if (Test-Path -LiteralPath $serversKey) {
        $serverEntries = @(Get-ChildItem -LiteralPath $serversKey -ErrorAction Stop | Where-Object {
            $_.PSChildName -match $targetPattern
        })

        $matchedAddresses += @($serverEntries | ForEach-Object { $_.PSChildName })

        foreach ($entry in $serverEntries) {
            Remove-Item -LiteralPath $entry.PSPath -Recurse -Force -ErrorAction Stop
        }
    }

    $defaultRdpDeleted = $false
    if (Test-Path -LiteralPath $defaultRdp) {
        $savedLine = Get-Content -LiteralPath $defaultRdp -ErrorAction Stop |
            Where-Object { $_ -like 'full address:s:*' } |
            Select-Object -First 1

        if ($savedLine) {
            $savedAddress = $savedLine.Substring('full address:s:'.Length).Trim()
            if ($savedAddress -match $targetPattern) {
                $matchedAddresses += $savedAddress
                Remove-Item -LiteralPath $defaultRdp -Force -ErrorAction Stop
                $defaultRdpDeleted = $true
            }
        }
    }

    $credentialTargets = @($target) + $matchedAddresses |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique |
        ForEach-Object { 'TERMSRV/' + $_ }
    $credentialsDeleted = 0
    foreach ($credentialTarget in $credentialTargets) {
        $null = & cmdkey.exe (('/delete:' + $credentialTarget)) 2>&1
        if ($LASTEXITCODE -eq 0) {
            $credentialsDeleted++
        }
    }

    $remainingMru = 0
    if (Test-Path -LiteralPath $defaultKey) {
        $currentItem = Get-Item -LiteralPath $defaultKey
        $remainingMru = @($currentItem.GetValueNames() | Where-Object {
            if ($_ -notlike 'MRU*') {
                return $false
            }

            $value = [string]$currentItem.GetValue($_)
            return $value -match $targetPattern
        }).Count
    }

    $remainingServers = 0
    if (Test-Path -LiteralPath $serversKey) {
        $remainingServers = @(Get-ChildItem -LiteralPath $serversKey -ErrorAction Stop | Where-Object {
            $_.PSChildName -match $targetPattern
        }).Count
    }

    Write-Host ('Target:                   ' + $target)
    Write-Host ('Target type:              ' + $targetType)
    Write-Host '[OK] Cleanup completed.' -ForegroundColor Green
    Write-Host ('Matching MRUs removed:    ' + $mruNames.Count)
    Write-Host ('Server entries removed:   ' + $serverEntries.Count)
    Write-Host ('Default.rdp removed:       ' + $defaultRdpDeleted)
    Write-Host ('Saved credentials removed: ' + $credentialsDeleted)
    Write-Host ('Matching MRUs remaining:  ' + $remainingMru)
    Write-Host ('Server entries remaining: ' + $remainingServers)

    if ($remainingMru -ne 0 -or $remainingServers -ne 0) {
        Write-Host '[ERROR] Verification failed.' -ForegroundColor Red
        exit 1
    }

    Write-Host '[OK] Verification passed.' -ForegroundColor Green
    exit 0
}
catch {
    Write-Host ('[ERROR] ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
