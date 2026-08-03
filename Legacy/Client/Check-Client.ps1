$ErrorActionPreference = 'Stop'

try {
    $task = Get-ScheduledTask -TaskName 'FRP Client' -ErrorAction Stop
    $taskInfo = Get-ScheduledTaskInfo -TaskName 'FRP Client' -ErrorAction Stop
    $action = @($task.Actions)[0]
    $frpcPath = $action.Execute
    $configPath = $null

    if ($action.Arguments -match '-c\s+"([^"]+)"') {
        $configPath = $Matches[1]
    }
    elseif ($action.Arguments -match '-c\s+(\S+)') {
        $configPath = $Matches[1]
    }

    if (-not (Test-Path -LiteralPath $frpcPath -PathType Leaf)) {
        throw "Task executable was not found: $frpcPath"
    }
    if ([string]::IsNullOrWhiteSpace($configPath) -or -not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        throw "Task configuration was not found: $configPath"
    }

    $version = (& $frpcPath --version 2>&1 | Select-Object -First 1).ToString().Trim()
    $verifyOutput = & $frpcPath verify -c $configPath 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw ('Configuration verification failed: ' + ($verifyOutput -join ' '))
    }

    $frpcProcesses = @(Get-CimInstance Win32_Process -Filter "Name='frpc.exe'" |
        Where-Object { $_.ExecutablePath -eq $frpcPath })
    $termService = Get-Service -Name TermService -ErrorAction SilentlyContinue
    $portCheck = Test-NetConnection 127.0.0.1 -Port 3389 -WarningAction SilentlyContinue
    $rdpServiceStatus = if ($null -ne $termService) { [string]$termService.Status } else { 'Not found (third-party RDP service may be in use)' }

    Write-Host ('Task state:          ' + $task.State)
    Write-Host ('Task last run:       ' + $taskInfo.LastRunTime)
    Write-Host ('Task last result:    ' + $taskInfo.LastTaskResult)
    Write-Host ('Run as:              ' + $task.Principal.UserId)
    Write-Host ('FRPC version:        ' + $version)
    Write-Host ('FRPC path:           ' + $frpcPath)
    Write-Host ('Configuration:       ' + $configPath)
    Write-Host ('FRPC process IDs:    ' + ($frpcProcesses.ProcessId -join ', '))
    Write-Host ('TermService:         ' + $rdpServiceStatus)
    Write-Host ('Local TCP 3389:      ' + $portCheck.TcpTestSucceeded)

    if ($frpcProcesses.Count -eq 0) {
        throw 'FRPC is not running.'
    }
    if (-not $portCheck.TcpTestSucceeded) {
        throw 'No usable RDP service is listening on localhost port 3389.'
    }

    Write-Host '[OK] Client checks passed.' -ForegroundColor Green
    exit 0
}
catch {
    Write-Host ('[ERROR] ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
