using System.Diagnostics;
using System.Net.Sockets;
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

        Directory.CreateDirectory(InstallDirectory);
        var frpcPath = Path.Combine(InstallDirectory, "frpc.exe");
        var configPath = Path.Combine(InstallDirectory, "frpc.toml");
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
            if (processes.Length == 0)
            {
                throw new InvalidOperationException("FRPC 开机任务已经创建，但没有检测到 frpc.exe 进程。");
            }

            foreach (var process in processes)
            {
                process.Dispose();
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
