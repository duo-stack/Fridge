param(
    [string] $FrpDirectory = 'C:\Program Files\FRP',
    [string] $TaskName = 'FRP Client'
)

$ErrorActionPreference = 'Stop'

$frpcPath = Join-Path $FrpDirectory 'frpc.exe'
$configPath = Join-Path $FrpDirectory 'frpc.toml'
$watchdogPath = Join-Path $FrpDirectory 'frpc-watchdog.ps1'

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principalCheck = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principalCheck.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Administrator permission is required.'
    }

    if (-not (Test-Path -LiteralPath $frpcPath -PathType Leaf)) {
        throw "frpc.exe was not found: $frpcPath"
    }
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        throw "frpc.toml was not found: $configPath"
    }

    $watchdogContent = @'
param(
    [Parameter(Mandatory)] [string] $FrpDirectory,
    [int] $RetrySeconds = 15
)

$ErrorActionPreference = 'Continue'
$frpcPath = Join-Path $FrpDirectory 'frpc.exe'
$configPath = Join-Path $FrpDirectory 'frpc.toml'
$logPath = Join-Path $FrpDirectory 'frpc-watchdog.log'
$oldLogPath = Join-Path $FrpDirectory 'frpc-watchdog.previous.log'
$utf8 = [Text.UTF8Encoding]::new($false)

function Write-WatchdogLog {
    param([string] $Message)

    $line = '{0} {1}{2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'), $Message, [Environment]::NewLine
    [IO.File]::AppendAllText($logPath, $line, $utf8)
}

while ($true) {
    try {
        if ((Test-Path -LiteralPath $logPath) -and
            (Get-Item -LiteralPath $logPath).Length -ge 5MB) {
            Remove-Item -LiteralPath $oldLogPath -Force -ErrorAction SilentlyContinue
            Move-Item -LiteralPath $logPath -Destination $oldLogPath -Force
        }

        if (-not (Test-Path -LiteralPath $frpcPath -PathType Leaf)) {
            Write-WatchdogLog "[ERROR] frpc.exe was not found: $frpcPath"
            Start-Sleep -Seconds $RetrySeconds
            continue
        }
        if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
            Write-WatchdogLog "[ERROR] frpc.toml was not found: $configPath"
            Start-Sleep -Seconds $RetrySeconds
            continue
        }

        Write-WatchdogLog '[INFO] Starting frpc.'
        & $frpcPath -c $configPath 2>&1 | ForEach-Object {
            Write-WatchdogLog ([string]$_)
        }
        $exitCode = $LASTEXITCODE
        Write-WatchdogLog "[WARN] frpc exited with code $exitCode; retrying in $RetrySeconds seconds."
    }
    catch {
        Write-WatchdogLog ('[ERROR] Watchdog exception: ' + $_.Exception.Message)
    }

    Start-Sleep -Seconds $RetrySeconds
}
'@
    [IO.File]::WriteAllText($watchdogPath, $watchdogContent, [Text.UTF8Encoding]::new($false))

    $powerShellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $actionArguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden ' +
        '-File "{0}" -FrpDirectory "{1}"' -f $watchdogPath, $FrpDirectory
    $action = New-ScheduledTaskAction `
        -Execute $powerShellPath `
        -Argument $actionArguments `
        -WorkingDirectory $FrpDirectory

    $trigger = New-ScheduledTaskTrigger -AtStartup
    $trigger.Delay = 'PT30S'
    $principal = New-ScheduledTaskPrincipal `
        -UserId 'SYSTEM' `
        -LogonType ServiceAccount `
        -RunLevel Highest

    $settings = New-ScheduledTaskSettingsSet `
        -StartWhenAvailable `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -RestartCount 999 `
        -RestartInterval (New-TimeSpan -Minutes 1) `
        -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -MultipleInstances IgnoreNew

    $task = New-ScheduledTask `
        -Action $action `
        -Trigger $trigger `
        -Principal $principal `
        -Settings $settings `
        -Description 'Start FRPC after networking initializes and continuously restart it after failures.'

    Register-ScheduledTask `
        -TaskName $TaskName `
        -InputObject $task `
        -Force | Out-Null

    $registered = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
    $registeredAction = @($registered.Actions)[0]

    if ($registeredAction.Execute -ne $powerShellPath) {
        throw 'Task verification failed: watchdog executable path does not match.'
    }
    if ($registeredAction.Arguments -notlike ('*' + $watchdogPath + '*')) {
        throw 'Task verification failed: watchdog script path does not match.'
    }

    $runningFrpc = @(Get-CimInstance Win32_Process -Filter "Name='frpc.exe'" |
        Where-Object { $_.ExecutablePath -eq $frpcPath })

    Write-Host ('Task name:          ' + $registered.TaskName)
    Write-Host ('Run as:             ' + $registered.Principal.UserId)
    Write-Host ('Watchdog:           ' + $watchdogPath)
    Write-Host ('FRPC:               ' + $frpcPath)
    Write-Host ('Configuration:      ' + $configPath)
    Write-Host ('Log:                ' + (Join-Path $FrpDirectory 'frpc-watchdog.log'))
    Write-Host '[OK] Resilient startup task registered and verified.' -ForegroundColor Green

    if ($runningFrpc.Count -gt 0) {
        Write-Host ('[OK] frpc is already running (PID ' + ($runningFrpc.ProcessId -join ', ') + ').') -ForegroundColor Green
        Write-Host 'The current process was left unchanged. The watchdog will take over after the next Windows restart.'
    }
    else {
        Start-ScheduledTask -TaskName $TaskName

        $startedFrpc = @()
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            Start-Sleep -Seconds 1
            $startedFrpc = @(Get-CimInstance Win32_Process -Filter "Name='frpc.exe'" |
                Where-Object { $_.ExecutablePath -eq $frpcPath })
            if ($startedFrpc.Count -gt 0) {
                break
            }
        }

        if ($startedFrpc.Count -eq 0) {
            $taskInfo = Get-ScheduledTaskInfo -TaskName $TaskName
            throw ('Task was registered but frpc did not start. LastTaskResult: ' + $taskInfo.LastTaskResult)
        }

        Write-Host ('[OK] frpc started under the watchdog (PID ' + ($startedFrpc.ProcessId -join ', ') + ').') -ForegroundColor Green
    }

    exit 0
}
catch {
    Write-Host ('[ERROR] ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
