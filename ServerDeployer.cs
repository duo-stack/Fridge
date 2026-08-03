using System.Security.Cryptography;
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
                await Task.Run(() => sftp.UploadFile(script, $"{remoteDirectory}/deploy-frps.sh", true), cancellationToken);
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

    private static async Task<string> ExecuteAsync(SshClient client, string commandText, CancellationToken cancellationToken)
    {
        using var command = client.CreateCommand(commandText);
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
