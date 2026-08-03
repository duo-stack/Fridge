param(
    [string] $FrpDirectory = 'C:\Program Files\FRP',
    [string] $TaskName = 'FRP Client'
)

$ErrorActionPreference = 'Stop'

$frpcPath = Join-Path $frpDirectory 'frpc.exe'
$configPath = Join-Path $frpDirectory 'frpc.toml'

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

    $action = New-ScheduledTaskAction `
        -Execute $frpcPath `
        -Argument ('-c "{0}"' -f $configPath) `
        -WorkingDirectory $frpDirectory

    $trigger = New-ScheduledTaskTrigger -AtStartup
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
        -Description 'Start the FRP client at Windows startup.'

    Register-ScheduledTask `
        -TaskName $taskName `
        -InputObject $task `
        -Force | Out-Null

    $registered = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
    $registeredAction = @($registered.Actions)[0]

    if ($registeredAction.Execute -ne $frpcPath) {
        throw 'Task verification failed: executable path does not match.'
    }
    if ($registeredAction.Arguments -notlike ('*' + $configPath + '*')) {
        throw 'Task verification failed: configuration path does not match.'
    }

    $runningFrpc = @(Get-CimInstance Win32_Process -Filter "Name='frpc.exe'" |
        Where-Object { $_.ExecutablePath -eq $frpcPath })

    Write-Host ('Task name:          ' + $registered.TaskName)
    Write-Host ('Run as:             ' + $registered.Principal.UserId)
    Write-Host ('Executable:         ' + $registeredAction.Execute)
    Write-Host ('Arguments:          ' + $registeredAction.Arguments)
    Write-Host ('Working directory:  ' + $registeredAction.WorkingDirectory)
    Write-Host '[OK] Startup task registered and verified.' -ForegroundColor Green

    if ($runningFrpc.Count -gt 0) {
        Write-Host ('[OK] frpc is already running (PID ' + ($runningFrpc.ProcessId -join ', ') + ').') -ForegroundColor Green
        Write-Host 'The current process was left unchanged. The task will take over after the next Windows restart.'
    }
    else {
        Start-ScheduledTask -TaskName $taskName
        Start-Sleep -Seconds 3

        $startedFrpc = @(Get-CimInstance Win32_Process -Filter "Name='frpc.exe'" |
            Where-Object { $_.ExecutablePath -eq $frpcPath })
        if ($startedFrpc.Count -eq 0) {
            $taskInfo = Get-ScheduledTaskInfo -TaskName $taskName
            throw ('Task was registered but frpc did not start. LastTaskResult: ' + $taskInfo.LastTaskResult)
        }

        Write-Host ('[OK] frpc started by the task (PID ' + ($startedFrpc.ProcessId -join ', ') + ').') -ForegroundColor Green
    }

    exit 0
}
catch {
    Write-Host ('[ERROR] ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
