using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Fridge;

internal sealed class ServerDeployer
{
    private const string FrpVersion = "0.65.0";
    private readonly Action<string> _log;
    private readonly Func<string, bool> _confirmFingerprint;

    public ServerDeployer(Action<string> log, Func<string, bool> confirmFingerprint)
    {
        _log = log;
        _confirmFingerprint = confirmFingerprint;
    }

    public async Task DeployAsync(DeploymentSettings settings, CancellationToken cancellationToken)
    {
        settings.Validate();
        if (string.IsNullOrWhiteSpace(settings.SshPassword))
        {
            throw new ArgumentException("请输入 SSH 密码。");
        }
        cancellationToken.ThrowIfCancellationRequested();

        _log($"正在连接 {settings.ServerAddress}:{settings.SshPort} ...");
        var connection = CreateConnection(settings);
        string? approvedFingerprint = null;

        using var ssh = new SshClient(connection);
        ssh.HostKeyReceived += (_, args) =>
        {
            var fingerprint = FormatFingerprint(args.HostKey);
            args.CanTrust = fingerprint == approvedFingerprint || _confirmFingerprint(fingerprint);
            if (args.CanTrust)
            {
                approvedFingerprint = fingerprint;
            }
        };

        await Task.Run(ssh.Connect, cancellationToken);
        if (!ssh.IsConnected)
        {
            throw new InvalidOperationException("SSH 连接没有成功建立。");
        }

        _log("SSH 连接成功，正在检测服务器环境...");
        var architecture = (await ExecuteAsync(ssh, "uname -m", cancellationToken)).Trim();
        var archiveResource = architecture switch
        {
            "x86_64" or "amd64" => "frps-linux-amd64.tar.gz",
            "aarch64" or "arm64" => "frps-linux-arm64.tar.gz",
            _ => throw new NotSupportedException($"暂不支持服务器架构：{architecture}")
        };

        if (!EmbeddedFiles.Exists(archiveResource))
        {
            throw new InvalidOperationException($"当前构建未包含 {archiveResource}，请重新执行资源打包。");
        }

        var userId = (await ExecuteAsync(ssh, "id -u", cancellationToken)).Trim();
        var rootPrefix = string.Empty;
        if (userId != "0")
        {
            await ExecuteAsync(ssh, "sudo -n true", cancellationToken);
            rootPrefix = "sudo -n ";
            _log("已确认当前账号可以免密执行 sudo。");
        }

        var environmentOutput = await ExecuteAsync(
            ssh,
            BuildServerEnvironmentCheckCommand(settings.FrpsPort, settings.RemoteRdpPort, rootPrefix),
            cancellationToken);
        var environment = ParseServerEnvironment(environmentOutput);
        foreach (var detail in environment.Details)
        {
            _log(detail);
        }

        if (environment.Conflicts.Count > 0)
        {
            throw new InvalidOperationException(
                "服务器环境检查未通过，检测到已有非 Fridge 的 FRPS 部署或端口冲突。\n" +
                "请先停止/卸载现有 FRPS，并确认目标端口未被占用。\n" +
                string.Join("\n", environment.Conflicts.Distinct(StringComparer.OrdinalIgnoreCase)));
        }

        _log(environment.HasManagedInstallation
            ? "检测到已有 Fridge FRPS 部署，将在原服务基础上更新配置。"
            : "服务器环境检查通过：未发现已有 FRPS 服务、进程或端口冲突。");

        var remoteDirectory = $"/tmp/fridge-{Guid.NewGuid():N}";
        await ExecuteAsync(ssh, $"mkdir -m 700 {remoteDirectory}", cancellationToken);

        try
        {
            _log($"服务器架构：{architecture}，正在上传内嵌部署文件...");
            using var sftp = new SftpClient(CreateConnection(settings));
            sftp.HostKeyReceived += (_, args) =>
            {
                var fingerprint = FormatFingerprint(args.HostKey);
                args.CanTrust = fingerprint == approvedFingerprint;
            };
            await Task.Run(sftp.Connect, cancellationToken);

            await using (var script = EmbeddedFiles.Open("deploy-frps.sh"))
            {
                using var reader = new StreamReader(script, Encoding.UTF8, true, leaveOpen: true);
                var normalizedScript = (await reader.ReadToEndAsync(cancellationToken)).ReplaceLineEndings("\n");
                await using var normalizedStream = new MemoryStream(Encoding.UTF8.GetBytes(normalizedScript));
                await Task.Run(
                    () => sftp.UploadFile(normalizedStream, $"{remoteDirectory}/deploy-frps.sh", true),
                    cancellationToken);
            }

            await using (var archive = EmbeddedFiles.Open(archiveResource))
            {
                await Task.Run(() => sftp.UploadFile(archive, $"{remoteDirectory}/frps.tar.gz", true), cancellationToken);
            }
            sftp.Disconnect();

            _log("文件上传完成，开始安装 FRPS...");
            var command = $"chmod 700 {remoteDirectory}/deploy-frps.sh && " +
                          $"{rootPrefix}bash {remoteDirectory}/deploy-frps.sh {settings.FrpsPort} {settings.Token} {FrpVersion}";
            var output = await ExecuteAsync(ssh, command, cancellationToken);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                _log(line.TrimEnd());
            }

            _log($"服务器部署完成。请在云安全组放行 TCP {settings.FrpsPort}、TCP/UDP {settings.RemoteRdpPort}。");
        }
        finally
        {
            try
            {
                await ExecuteAsync(ssh, $"{rootPrefix}rm -rf -- {remoteDirectory}", CancellationToken.None);
            }
            catch
            {
                _log($"警告：未能自动清理服务器临时目录 {remoteDirectory}");
            }

            ssh.Disconnect();
        }
    }

    private static ConnectionInfo CreateConnection(DeploymentSettings settings)
    {
        var authentication = new PasswordAuthenticationMethod(settings.SshUser, settings.SshPassword);
        return new ConnectionInfo(settings.ServerAddress, settings.SshPort, settings.SshUser, authentication)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    private static string BuildServerEnvironmentCheckCommand(int frpsPort, int remoteRdpPort, string rootPrefix)
    {
        return $$"""
            set +e
            managed_active=0
            managed_pid=0

            if command -v systemctl >/dev/null 2>&1; then
              if ! systemctl show --property=Version >/dev/null 2>&1; then
                echo "__CONFLICT_ENV__ systemd unavailable"
              fi
              if systemctl is-active --quiet fridge-frps.service; then
                managed_active=1
                echo "__FRIDGE_ACTIVE__"
              fi
              if systemctl is-enabled --quiet fridge-frps.service; then
                echo "__FRIDGE_ENABLED__"
              fi
              if systemctl cat fridge-frps.service >/dev/null 2>&1; then
                echo "__FRIDGE_INSTALLED__"
              fi
              managed_pid="$(systemctl show -p MainPID --value fridge-frps.service 2>/dev/null || true)"
              case "$managed_pid" in
                ''|*[!0-9]*) managed_pid=0 ;;
              esac

              if systemctl is-active --quiet frps.service; then
                echo "__CONFLICT_SERVICE__ frps.service active"
              elif systemctl is-enabled --quiet frps.service; then
                echo "__CONFLICT_SERVICE__ frps.service enabled"
              elif systemctl cat frps.service >/dev/null 2>&1; then
                echo "__CONFLICT_SERVICE__ frps.service installed"
              fi

              for service in frp.service frp-server.service; do
                if systemctl is-active --quiet "$service"; then
                  echo "__CONFLICT_SERVICE__ $service active"
                elif systemctl is-enabled --quiet "$service"; then
                  echo "__CONFLICT_SERVICE__ $service enabled"
                elif systemctl cat "$service" >/dev/null 2>&1; then
                  echo "__CONFLICT_SERVICE__ $service installed"
                fi
              done

              grep_command="{{rootPrefix}}grep"
              for unit_path in /etc/systemd/system/*.service /lib/systemd/system/*.service; do
                [ -f "$unit_path" ] || continue
                case "$unit_path" in
                  */fridge-frps.service) continue ;;
                esac
                if $grep_command -Eq '^[[:space:]]*ExecStart=.*[/=[:space:]]frps([[:space:]]|$)' "$unit_path" 2>/dev/null; then
                  echo "__CONFLICT_SERVICE__ custom unit $unit_path"
                fi
              done
            else
              echo "__CONFLICT_ENV__ systemctl not found"
            fi

            readlink_command="{{rootPrefix}}readlink"
            for frps_path in "$(command -v frps 2>/dev/null || true)" /usr/local/bin/frps /usr/bin/frps /opt/frp/frps /opt/frp/*/frps; do
              [ -x "$frps_path" ] || continue
              resolved_path="$($readlink_command -f "$frps_path" 2>/dev/null || printf '%s' "$frps_path")"
              case "$resolved_path" in
                /opt/fridge/frp/*/frps) ;;
                *) echo "__CONFLICT_BINARY__ $resolved_path" ;;
              esac
            done

            if command -v pgrep >/dev/null 2>&1; then
              frps_pids="$({{rootPrefix}}pgrep -x frps 2>/dev/null || true)"
            else
              frps_pids="$({{rootPrefix}}ps -eo pid=,comm= 2>/dev/null | awk '$2 == "frps" { print $1 }' || true)"
            fi
            for pid in $frps_pids; do
              process_path="$($readlink_command -f "/proc/$pid/exe" 2>/dev/null || true)"
              case "$process_path" in
                /opt/fridge/frp/*/frps) echo "__FRIDGE_PROCESS__ pid=$pid" ;;
                *) echo "__CONFLICT_PROCESS__ frps pid=$pid path=${process_path:-unknown}" ;;
              esac
            done

            report_listener() {
              protocol="$1"
              port="$2"
              listener_line="$3"
              [ -n "$listener_line" ] || return 0
              if [ "$managed_pid" -gt 0 ] 2>/dev/null && printf '%s' "$listener_line" | grep -Eq "pid=$managed_pid([,)/]|$)"; then
                echo "__FRIDGE_PORT__ $protocol $port"
              else
                echo "__CONFLICT_PORT__ $protocol $port $listener_line"
              fi
            }

            if command -v ss >/dev/null 2>&1; then
              ss_command="{{rootPrefix}}ss"
              report_listener TCP "{{frpsPort}}" "$($ss_command -H -ltnp 2>/dev/null | awk -v port="{{frpsPort}}" '$4 ~ (":" port "$") { print; exit }')"
              report_listener TCP "{{remoteRdpPort}}" "$($ss_command -H -ltnp 2>/dev/null | awk -v port="{{remoteRdpPort}}" '$4 ~ (":" port "$") { print; exit }')"
              report_listener UDP "{{remoteRdpPort}}" "$($ss_command -H -lunp 2>/dev/null | awk -v port="{{remoteRdpPort}}" '$4 ~ (":" port "$") { print; exit }')"
            elif command -v netstat >/dev/null 2>&1; then
              netstat_command="{{rootPrefix}}netstat"
              report_listener TCP "{{frpsPort}}" "$($netstat_command -lntp 2>/dev/null | awk -v port="{{frpsPort}}" '$4 ~ (":" port "$") { print; exit }')"
              report_listener TCP "{{remoteRdpPort}}" "$($netstat_command -lntp 2>/dev/null | awk -v port="{{remoteRdpPort}}" '$4 ~ (":" port "$") { print; exit }')"
              report_listener UDP "{{remoteRdpPort}}" "$($netstat_command -lnup 2>/dev/null | awk -v port="{{remoteRdpPort}}" '$4 ~ (":" port "$") { print; exit }')"
            else
              echo "__PORT_CHECK_UNAVAILABLE__ ss/netstat"
            fi

            exit 0
            """;
    }

    private static ServerEnvironmentResult ParseServerEnvironment(string output)
    {
        var details = new List<string>();
        var conflicts = new List<string>();
        var hasManagedInstallation = false;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("__FRIDGE_", StringComparison.Ordinal))
            {
                hasManagedInstallation = true;
                if (line.StartsWith("__FRIDGE_PORT__", StringComparison.Ordinal))
                {
                    details.Add("检测到目标端口已由 Fridge FRPS 占用，更新时会由 systemd 安全重启。");
                }

                continue;
            }

            if (line.StartsWith("__CONFLICT_SERVICE__ ", StringComparison.Ordinal))
            {
                conflicts.Add("已有 systemd 服务：" + line["__CONFLICT_SERVICE__ ".Length..]);
            }
            else if (line.StartsWith("__CONFLICT_BINARY__ ", StringComparison.Ordinal))
            {
                conflicts.Add("已有 frps 可执行文件：" + line["__CONFLICT_BINARY__ ".Length..]);
            }
            else if (line.StartsWith("__CONFLICT_PROCESS__ ", StringComparison.Ordinal))
            {
                conflicts.Add("已有 frps 进程：" + line["__CONFLICT_PROCESS__ ".Length..]);
            }
            else if (line.StartsWith("__CONFLICT_PORT__ ", StringComparison.Ordinal))
            {
                conflicts.Add("目标端口已被占用：" + line["__CONFLICT_PORT__ ".Length..]);
            }
            else if (line.StartsWith("__CONFLICT_ENV__ ", StringComparison.Ordinal))
            {
                conflicts.Add("服务器环境不满足 systemd 部署要求：" + line["__CONFLICT_ENV__ ".Length..]);
            }
            else if (line.StartsWith("__PORT_CHECK_UNAVAILABLE__ ", StringComparison.Ordinal))
            {
                conflicts.Add("服务器未提供 ss 或 netstat，无法确认 FRPS 端口是否已被其他程序占用。请先安装其中一个端口检查工具。");
            }
        }

        return new ServerEnvironmentResult(hasManagedInstallation, details, conflicts);
    }

    private sealed record ServerEnvironmentResult(
        bool HasManagedInstallation,
        IReadOnlyList<string> Details,
        IReadOnlyList<string> Conflicts);

    private static async Task<string> ExecuteAsync(SshClient client, string commandText, CancellationToken cancellationToken)
    {
        using var command = client.CreateCommand(commandText.ReplaceLineEndings("\n"));
        var result = await Task.Run(command.Execute, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (command.ExitStatus != 0)
        {
            var error = string.IsNullOrWhiteSpace(command.Error) ? result : command.Error;
            throw new InvalidOperationException($"远程命令失败（退出码 {command.ExitStatus}）：{error.Trim()}");
        }

        return result;
    }

    private static string FormatFingerprint(byte[] hostKey)
    {
        var hash = SHA256.HashData(hostKey);
        return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
    }
}
