using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Fridge;

internal sealed record DeploymentSettings(
    string ServerAddress,
    int SshPort,
    string SshUser,
    string SshPassword,
    int FrpsPort,
    int RemoteRdpPort,
    string Token)
{
    private static readonly Regex TokenPattern = new("^[A-Za-z0-9._-]{16,128}$", RegexOptions.CultureInvariant);

    public string RdpTarget => $"{ServerAddress}:{RemoteRdpPort}";

    public void Validate()
    {
        if (!IsHostOrIpv4(ServerAddress))
        {
            throw new ArgumentException("服务器地址必须是 IPv4 地址或有效的主机名。");
        }

        if (string.IsNullOrWhiteSpace(SshUser))
        {
            throw new ArgumentException("请输入 SSH 用户名。");
        }

        ValidatePort(SshPort, "SSH 端口");
        ValidatePort(FrpsPort, "FRPS 端口");
        ValidatePort(RemoteRdpPort, "RDP 公网端口");

        if (FrpsPort == RemoteRdpPort)
        {
            throw new ArgumentException("FRPS 端口和 RDP 公网端口不能相同。");
        }

        if (!IsValidToken(Token))
        {
            throw new ArgumentException("Token 需要包含 16-128 个字母、数字、点、下划线或连字符。");
        }
    }

    internal static bool IsHostOrIpv4(string value)
    {
        if (IPAddress.TryParse(value, out var address))
        {
            return address.AddressFamily == AddressFamily.InterNetwork;
        }

        return Uri.CheckHostName(value) == UriHostNameType.Dns;
    }

    internal static bool IsValidToken(string value) => TokenPattern.IsMatch(value);

    private static void ValidatePort(int value, string name)
    {
        if (value is < 1 or > 65535)
        {
            throw new ArgumentException($"{name}必须在 1-65535 之间。");
        }
    }
}
