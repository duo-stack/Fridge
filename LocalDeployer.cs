using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;

namespace Fridge;

internal sealed class LocalDeployer
{
    private const string InstallDirectory = @"C:\Program Files\Fridge";
    private readonly Action<string> _log;

    public LocalDeployer(Action<string> log)
    {
        _log = log;
    }

    public async Task DeployAsync(DeploymentSettings settings, CancellationToken cancellationToken)
    {
        settings.Validate();
        if (!EmbeddedFiles.Exists("frpc.exe"))
        {
            throw new InvalidOperationException("当前构建未包含 frpc.exe，请重新执行资源打包。");
        }

        var frpcPath = Path.Combine(InstallDirectory, "frpc.exe");
        var configPath = Path.Combine(InstallDirectory, "frpc.toml");
        await CheckLocalEnvironmentAsync(frpcPath, configPath, cancellationToken);

        Directory.CreateDirectory(InstallDirectory);
        await StopManagedFrpcAsync(frpcPath, cancellationToken);
        var backupPath = File.Exists(configPath)
            ? configPath + $".backup.{DateTime.Now:yyyyMMdd-HHmmss}"
            : null;

        if (backupPath is not null)
        {
            File.Copy(configPath, backupPath, true);
            _log($"已备份原配置：{backupPath}");
        }

        var installedVersion = await GetFrpcVersionAsync(frpcPath, cancellationToken);
        if (installedVersion == "0.65.0")
        {
            _log("检测到 FRPC 0.65.0，继续使用现有程序文件。");
        }
        else
        {
            if (File.Exists(frpcPath))
            {
                File.Copy(frpcPath, frpcPath + $".backup.{DateTime.Now:yyyyMMdd-HHmmss}", true);
            }

            _log("正在释放内嵌 frpc.exe...");
            await EmbeddedFiles.ExtractAsync("frpc.exe", frpcPath, cancellationToken);
        }

        var config = BuildConfig(settings);
        await File.WriteAllTextAsync(configPath, config, new UTF8Encoding(false), cancellationToken);

        try
        {
            _log("正在验证 FRPC 配置...");
            await RunProcessAsync(frpcPath, $"verify -c \"{configPath}\"", InstallDirectory, cancellationToken);
        }
        catch
        {
            if (backupPath is not null)
            {
                File.Copy(backupPath, configPath, true);
                _log("验证失败，已恢复上一份配置。");
            }
            else if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
            throw;
        }

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "fridge-local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var configureRdp = Path.Combine(temporaryDirectory, "Configure-Rdp.ps1");
            var installStartup = Path.Combine(temporaryDirectory, "Install-FrpcStartup.ps1");
            await EmbeddedFiles.ExtractAsync("Configure-Rdp.ps1", configureRdp, cancellationToken);
            await EmbeddedFiles.ExtractAsync("Install-FrpcStartup.ps1", installStartup, cancellationToken);

            _log("正在启用 RDP、NLA 和 Windows 防火墙规则...");
            await RunPowerShellAsync(configureRdp, string.Empty, cancellationToken);

            _log("正在注册 FRPC 开机任务并启动...");
            var arguments = $"-FrpDirectory \"{InstallDirectory}\" -TaskName \"Fridge FRP Client\"";
            await RunPowerShellAsync(installStartup, arguments, cancellationToken);

            _log("正在执行本机连通性检查...");
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", 3389, cancellationToken);

            var processes = Process.GetProcessesByName("frpc");
            var managedProcessFound = false;
            foreach (var process in processes)
            {
                try
                {
                    managedProcessFound |= PathsEqual(TryGetProcessPath(process), frpcPath);
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (!managedProcessFound)
            {
                throw new InvalidOperationException("FRPC 开机任务已经创建，但没有检测到 frpc.exe 进程。");
            }

            _log($"本机部署完成，远程桌面目标：{settings.RdpTarget}");
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, true);
            }
        }
    }

    private async Task RunPowerShellAsync(string scriptPath, string scriptArguments, CancellationToken cancellationToken)
    {
        var arguments = $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" {scriptArguments}";
        await RunProcessAsync("powershell.exe", arguments, Path.GetDirectoryName(scriptPath)!, cancellationToken);
    }

    private async Task CheckLocalEnvironmentAsync(
        string frpcPath,
        string configPath,
        CancellationToken cancellationToken)
    {
        if (!IsAdministrator())
        {
            throw new InvalidOperationException("本机环境检查未通过：请以管理员身份运行 Fridge。");
        }

        _log("正在检查本机 FRPC 环境...");
        var conflicts = new List<string>();
        var managedProcessFound = false;

        foreach (var process in Process.GetProcessesByName("frpc"))
        {
            try
            {
                var processPath = TryGetProcessPath(process);
                if (PathsEqual(processPath, frpcPath))
                {
                    managedProcessFound = true;
                }
                else
                {
                    conflicts.Add($"检测到正在运行的 frpc.exe（PID {process.Id}，路径：{processPath ?? "无法读取"}）。");
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        var taskOutput = await RunPowerShellCaptureAsync(BuildScheduledFrpcTaskCheckScript(), cancellationToken);
        foreach (var line in taskOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split('|', 3);
            if (fields.Length == 3 && string.Equals(fields[0], "TASK", StringComparison.Ordinal))
            {
                var taskName = fields[1];
                var taskPath = fields[2];
                if (string.Equals(taskName, "Fridge FRP Client", StringComparison.OrdinalIgnoreCase) &&
                    PathsEqual(taskPath, frpcPath))
                {
                    continue;
                }

                conflicts.Add($"检测到 FRPC 计划任务：{taskName}（路径：{taskPath}）。");
            }
            else if (fields.Length == 3 && string.Equals(fields[0], "SERVICE", StringComparison.Ordinal))
            {
                conflicts.Add($"检测到 FRPC Windows 服务：{fields[1]}（启动命令：{fields[2]}）。");
            }
            else if (fields.Length == 2 && string.Equals(fields[0], "PATH", StringComparison.Ordinal))
            {
                if (!PathsEqual(fields[1], frpcPath))
                {
                    conflicts.Add($"系统 PATH 中已存在其他 frpc.exe：{fields[1]}。");
                }
            }
        }

        foreach (var knownPath in new[]
                 {
                     @"C:\Program Files\FRP\frpc.exe",
                     @"C:\Program Files\frp\frpc.exe",
                     @"C:\FRP\frpc.exe"
                 })
        {
            if (File.Exists(knownPath) && !PathsEqual(knownPath, frpcPath))
            {
                conflicts.Add($"检测到其他 FRPC 安装文件：{knownPath}。");
            }
        }

        if (conflicts.Count > 0)
        {
            throw new InvalidOperationException(
                "本机环境检查未通过，检测到已有非 Fridge 的 FRPC 部署或运行。请先停止/卸载现有 FRPC、计划任务或旧安装，再重新部署。\n" +
                string.Join("\n", conflicts.Distinct(StringComparer.OrdinalIgnoreCase)));
        }

        if (managedProcessFound || File.Exists(frpcPath) || File.Exists(configPath))
        {
            _log("检测到已有 Fridge FRPC 文件或进程，将先停止旧任务并备份配置后更新。");
        }
        else
        {
            _log("本机环境检查通过：未发现已有 FRPC 进程、计划任务或冲突安装。");
        }
    }

    private static string BuildScheduledFrpcTaskCheckScript()
    {
        return """
            $ErrorActionPreference = 'SilentlyContinue'
            $ProgressPreference = 'SilentlyContinue'
            if ($null -ne (Get-Command Get-ScheduledTask -ErrorAction SilentlyContinue)) {
                try {
                    Get-ScheduledTask | ForEach-Object {
                        $task = $_
                        foreach ($action in @($task.Actions)) {
                            $execute = [string]$action.Execute
                            if ($execute -match '(?i)(^|[\\/])frpc\.exe$') {
                                'TASK|{0}|{1}' -f $task.TaskName, $execute
                            }
                        }
                    }
                }
                catch {
                    # A restricted host may not expose scheduled-task metadata.
                }
            }
            $command = Get-Command frpc.exe -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($null -ne $command -and -not [string]::IsNullOrWhiteSpace($command.Source)) {
                'PATH|{0}' -f $command.Source
            }
            if ($null -ne (Get-Command Get-CimInstance -ErrorAction SilentlyContinue)) {
                try {
                    Get-CimInstance Win32_Service | ForEach-Object {
                        $pathName = [string]$_.PathName
                        if ($pathName -match '(?i)frpc\.exe') {
                            'SERVICE|{0}|{1}' -f $_.Name, $pathName
                        }
                    }
                }
                catch {
                    # A restricted host may not expose Windows service metadata.
                }
            }
            exit 0
            """;
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static bool PathsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task<string> RunPowerShellCaptureAsync(string script, CancellationToken cancellationToken)
    {
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoLogo -NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
                WorkingDirectory = Environment.SystemDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动：powershell.exe");
        }

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(true);
            }
            catch
            {
                // The process may have completed between the checks.
            }
        });

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"本机环境检查命令失败：{error.Trim()}");
        }

        return output;
    }

    private async Task StopManagedFrpcAsync(string frpcPath, CancellationToken cancellationToken)
    {
        _log("正在停止旧的 Fridge FRPC 任务...");
        using (var process = Process.Start(new ProcessStartInfo
               {
                   FileName = "powershell.exe",
                   Arguments = "-NoLogo -NoProfile -Command \"Stop-ScheduledTask -TaskName 'Fridge FRP Client' -ErrorAction SilentlyContinue\"",
                   UseShellExecute = false,
                   CreateNoWindow = true
               }))
        {
            if (process is not null)
            {
                await process.WaitForExitAsync(cancellationToken);
            }
        }

        foreach (var process in Process.GetProcessesByName("frpc"))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, frpcPath, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(true);
                    await process.WaitForExitAsync(cancellationToken);
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited while it was being inspected.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static async Task<string?> GetFrpcVersionAsync(string frpcPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(frpcPath)) return null;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = frpcPath,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) return null;
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task RunProcessAsync(string executable, string arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            },
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log(e.Data); };

        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动：{executable}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(true);
            }
            catch
            {
                // The process may have completed between the checks.
            }
        });

        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"命令执行失败（退出码 {process.ExitCode}）：{Path.GetFileName(executable)}");
        }
    }

    private static string BuildConfig(DeploymentSettings settings)
    {
        var address = settings.ServerAddress.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $$"""
            serverAddr = "{{address}}"
            serverPort = {{settings.FrpsPort}}

            [auth]
            token = "{{settings.Token}}"

            [[proxies]]
            name = "rdp-tcp"
            type = "tcp"
            localIP = "127.0.0.1"
            localPort = 3389
            remotePort = {{settings.RemoteRdpPort}}

            [[proxies]]
            name = "rdp-udp"
            type = "udp"
            localIP = "127.0.0.1"
            localPort = 3389
            remotePort = {{settings.RemoteRdpPort}}
            """;
    }
}
