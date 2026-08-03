# 使用 FRP 穿透 Windows 远程桌面

这套文件用于把一台具有可用 RDP 服务的 Windows 被控电脑，通过带公网 IPv4 的 Linux 云服务器转发给控制电脑。基础部分只包含最简流程；域名、证书、分辨率和安全优化放在后半部分。

本文固定使用 FRP `0.65.0`，不提供其他版本选择。示例不包含真实公网地址、token 或账号信息。

## 一、适用条件

- 云服务器：Linux，具有公网 IPv4，使用 systemd。
- 被控电脑：64 位 Windows，并且本机 `127.0.0.1:3389` 已有可用的 RDP 服务。微软官方没有给家庭版提供系统自带的 RDP 被控能力，自行决定如何获得 RDP 被控能力。
- 控制电脑：能够运行微软远程桌面客户端 `mstsc.exe`。
- 被控电脑能够主动访问云服务器所选的 FRPS 控制端口（默认 `7000/TCP`）。
- 云服务器安全组允许控制电脑访问 RDP 公网转发端口。

FRP 角色：

- 云服务器运行 `frps`。
- 被控 Windows 电脑运行 `frpc`。
- 控制电脑不需要安装 FRP，只需运行 MSTSC。

## 二、目录结构

```text
Fridge/
├─ FRP-RDP部署教程.md
├─ Fridge-需求功能说明.md
├─ Server/
│  ├─ Setup-Server.sh
│  ├─ Check-Server.sh
│  ├─ frps.example.yaml
│  └─ scripts/
│     └─ install-frps.sh
└─ Client/
   ├─ Setup-Client.cmd
   ├─ Setup-Client.ps1
   ├─ Check-Client.cmd
   ├─ Check-Client.ps1
   ├─ Install-FrpcStartup.cmd
   ├─ Install-FrpcStartup.ps1
   ├─ frpc.example.toml
   ├─ scripts/
   │  ├─ Install-Frpc.ps1
   │  └─ Configure-Rdp.ps1
   └─ Control-PC-Tools/
      ├─ Clear-RdpHistory.cmd
      ├─ Create-1440p-RdpFile.cmd
      └─ Create-1440p-RdpFile.ps1
```

## 三、FRP 版本

服务端和客户端固定使用 `0.65.0`，避免版本差异带来额外变量：

- Intel/AMD Linux：`frp_0.65.0_linux_amd64.tar.gz`
- ARM64 Linux：`frp_0.65.0_linux_arm64.tar.gz`
- 64 位 Windows：`frp_0.65.0_windows_amd64.zip`

脚本会自动判断 Linux 是 `amd64` 还是 `arm64`。如果服务器无法访问 GitHub，可以提前下载相应压缩包，放在 `Server/Setup-Server.sh` 同一目录。Windows 压缩包可以放在 `Client/Setup-Client.cmd` 同一目录。

# 基础流程

## 四、配置云服务器

把整个 `Server` 目录上传到云服务器，例如 `/root/Fridge/Server`：

```bash
cd /root/Fridge/Server
chmod +x Setup-Server.sh Check-Server.sh scripts/*.sh
sudo ./Setup-Server.sh
```

入口依次询问 FRPS 控制端口（默认 `7000`）、RDP 公网转发端口（默认 `3389`）和 FRP token。端口留空会采用显示的默认值；token 留空会生成一个随机值。记录安装结束时显示的两个端口和 token，Windows 客户端必须使用相同值。

脚本自动完成：

- 根据 CPU 架构安装固定版本的 `frps 0.65.0` 到 `/opt/frp/frp_0.65.0_linux_amd64` 或对应的 `arm64` 目录。
- 在 FRP 目录内生成 `frps.yaml`。
- 将控制台输出持续写入同目录的 `frps.log`。
- 配置日志每日轮转，保留 7 份压缩日志。
- 创建 `frps.service`，后台常驻、异常自动重启并开机自启。
- 执行 `frps verify` 并确认服务已经运行。

脚本会把所选控制端口显式写入 `frps.yaml` 的 `bindPort`。RDP 公网转发端口由被控端 FRPC 注册代理时创建，服务端入口询问它是为了给出一致的安全组配置结果。

检查服务：

```bash
cd /root/Fridge/Server
sudo ./Check-Server.sh
```

也可以手动查看：

```bash
sudo systemctl status frps --no-pager
sudo systemctl is-enabled frps
sudo tail -n 50 /opt/frp/frp_0.65.0_linux_amd64/frps.log
```

ARM64 服务器需要把最后一条命令中的目录改为 `frp_0.65.0_linux_arm64`。

## 五、配置云服务器安全组

下面以默认控制端口 `7000` 和默认公网 RDP 端口 `3389` 为例：

| 端口 | 协议 | 建议来源 | 用途 |
|---|---|---|---|
| `7000` | TCP | 被控电脑的出口公网 IP；不固定时可适当扩大 | FRPC 连接 FRPS |
| `3389` | TCP | 控制电脑的出口公网 IP `/32` | RDP 建连和主要传输 |
| `3389` | UDP | 控制电脑的出口公网 IP `/32` | RDP UDP 传输 |
| `22` | TCP | 管理电脑的出口公网 IP `/32` | SSH 管理服务器 |

如果引导时选择了其他端口，安全组必须同步使用实际值。控制端口只开放 TCP；RDP 公网转发端口同时开放 TCP 和 UDP。两者不能使用同一个端口。

不要把 RDP 转发端口长期开放给 `0.0.0.0/0`。一个公网出口 IP 可能由多台设备共用，但限制到控制端出口仍比全网开放安全得多。

本套服务端脚本不修改 `ufw`、`firewalld` 或其他 Linux 防火墙。如果服务器额外启用了系统防火墙，需要自行允许相同端口。云安全组与 Linux 防火墙任意一层阻止，连接都会失败。

## 六、配置被控 Windows 电脑

把整个 `Client` 目录放到被控电脑，双击：

```text
Setup-Client.cmd
```

入口首先提示：Windows Defender 可能把官方 `frpc.exe` 检测为“可能不需要的应用”。网络隧道工具出现此类误报并不罕见。确认文件来自 FRP 官方发布后，如果 Defender 阻止，可选择允许或恢复；来源不明时不要放行。

批准 UAC 后，依次输入：

1. FRPC 安装目录：默认 `C:\Program Files\FRP`，一般直接回车。
2. 云服务器公网 IPv4 或域名：不要带协议、端口或路径。
3. FRPS 控制端口：默认 `7000`，必须与服务端一致。
4. RDP 公网转发端口：默认 `3389`，必须与服务端部署后配置的安全组一致。
5. token：必须与服务端完全一致，输入时不显示字符。

以下项目固定，不再询问：

- FRP 版本：`0.65.0`。
- FRPC 本地 RDP 端口：`3389`。
- 同时生成 TCP 和 UDP 代理。

入口会自动：

- 下载或复用 `frpc.exe 0.65.0`。
- 备份已有 `frpc.toml`。
- 生成已经验证过的最小 TOML 配置。
- 执行 `frpc verify`。
- 尝试启用系统内置 RDP、NLA、TLS 安全层和防火墙规则。
- 最终检查本机 `127.0.0.1:3389` 是否真的在监听。
- 注册 `FRP Client` 开机任务。

脚本不根据 Windows 版本直接判定成功或失败。无论 RDP 能力来自系统内置服务还是用户自行准备的方案，最终 3389 可用即可继续；如果没有监听器，脚本会停止并提示先解决 RDP 被控能力。

如果 FRPC 已由手动命令运行，安装器不会结束当前进程；新的配置和计划任务在下次重启后接管。

重启后可双击 `Check-Client.cmd`。正常结果包括：

- 计划任务为 `Running`。
- 运行身份为 `SYSTEM`。
- 能看到 `frpc.exe` 的 PID。
- RDP 服务正在运行。
- 本机 TCP 3389 为 `True`。

## 七、从控制电脑连接

先测试公网 TCP：

```powershell
Test-NetConnection <云服务器公网IP> -Port <所选RDP公网端口>
```

结果为 `TcpTestSucceeded: True` 后运行：

```cmd
mstsc /v:<云服务器公网IP>:<所选RDP公网端口>
```

第一次使用 IP 连接时出现服务器身份警告是正常的，因为被控端的 Windows 自动证书通常不包含云服务器公网 IP。

登录时注意：

- 微软账号可以输入邮箱，必要时使用 `MicrosoftAccount\邮箱`。
- 使用账号密码，不是 Windows Hello PIN。
- 修改微软账号云端密码后，本机可能仍缓存旧密码，相关说明见第十四节。

至此基础连接完成。以下内容均为解释、优化或维护。

# 配置与优化

## 八、已验证的最小配置

### 8.1 服务端 `frps.yaml`

服务端只保留控制端口和 token 认证配置，不添加其他选项：

```yaml
bindPort: 7000

auth:
  method: "token"
  token: "两端相同的token"
```

配置位于 FRPS 安装目录内，例如：

```text
/opt/frp/frp_0.65.0_linux_amd64/frps.yaml
```

`bindPort` 由服务端引导写入，默认是 `7000`；如果选择其他端口，客户端的 `serverPort` 必须使用同一个值。

### 8.2 被控端 `frpc.toml`

客户端使用如下结构，不额外添加 `loginFailExit`、`auth.method`、`transport.tls` 等配置：

```toml
serverAddr = "云服务器公网IP或域名"
serverPort = 7000 # 服务端引导中选择的 FRPS 控制端口

[auth]
token = "两端相同的token"

[[proxies]]
name = "rdp-tcp"
type = "tcp"
localIP = "127.0.0.1"
localPort = 3389
remotePort = 3389 # 引导中选择的 RDP 公网转发端口

[[proxies]]
name = "rdp-udp"
type = "udp"
localIP = "127.0.0.1"
localPort = 3389
remotePort = 3389 # 必须与 TCP 代理使用相同端口
```

手动校验和运行：

```powershell
cd 'C:\Program Files\FRP'
.\frpc.exe verify -c .\frpc.toml
.\frpc.exe -c .\frpc.toml
```

TCP 是必须的，RDP 初始 TLS/NLA 建连依赖 TCP。UDP 改善连接后的画面、输入和弱网体验，不能修复 TCP 握手错误。

## 九、安全建议

### 9.1 使用长随机 token

FRPS 和 FRPC 的 token 必须完全一致。不要把真实 token 写入教程、截图、聊天记录或代码仓库。修改 token 后，两端都要同步修改并重启。

### 9.2 限制公网来源

- RDP 公网端口只允许控制端出口公网 IP。
- FRPS 控制端口优先只允许被控端出口公网 IP；地址经常变化时再根据实际情况扩大。
- 不启用不需要的 Dashboard。
- RDP 账号使用强密码，并优先启用 NLA。

### 9.3 选择非默认端口

部署引导允许选择非默认端口。公网 RDP 端口可以使用同一个未占用的高位端口，例如 `43389`；FRPC 两个代理的 `remotePort` 必须相同，并同步修改：

- 云安全组 TCP/UDP 规则。
- MSTSC 连接目标中的端口。

FRPS 控制端口也可以改为其他未占用端口，但服务端 `bindPort`、客户端 `serverPort` 和安全组 TCP 规则必须一致。修改端口只能减少无差别扫描，不能替代来源 IP 限制、NLA 和强密码；FRPS 配置不需要写入 RDP 公网转发端口。

## 十、域名与 RDP 证书

### 10.1 域名更容易受网络策略影响

最简单可靠的连接目标仍然是云服务器公网 IP。域名只改善记忆和证书名称匹配，不会改善 FRP 转发。

域名可能被控制端网络、DNS、终端安全软件、SNI 检查、顶级域分类或信誉策略区别处理。即使域名解析到与 IP 完全相同的地址，也可能出现“IP 连接成功、域名报内部错误”。因此：

- 基础部署先用公网 IP 验证。
- 域名失败时立即做 IP 对照测试。
- 证书名称匹配不能解决网络策略阻断。
- 不建议用永久伪装 hostname 的方式绕过组织安全策略。

如果仍需域名：

1. 添加 A 记录指向云服务器公网 IPv4。
2. 确认没有错误的 AAAA 记录。
3. 使用 `Test-NetConnection 域名 -Port 端口`。
4. 再考虑证书名称匹配。

### 10.2 自签名 RDP 证书

只有确实需要域名且能管理所有控制电脑时才适合自签名方案。在被控电脑管理员 PowerShell 中执行：

```powershell
$domain = 'rdp.example.com'
$cert = New-SelfSignedCertificate `
    -Type SSLServerAuthentication `
    -Subject "CN=$domain" `
    -DnsName $domain `
    -CertStoreLocation 'Cert:\LocalMachine\My' `
    -FriendlyName "RDP $domain" `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -HashAlgorithm SHA256 `
    -Provider 'Microsoft RSA SChannel Cryptographic Provider' `
    -KeySpec KeyExchange `
    -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears(2)

$cerPath = Join-Path ([Environment]::GetFolderPath('Desktop')) "$domain.cer"
Export-Certificate -Cert $cert -FilePath $cerPath -Force
```

授权 RDP 服务读取私钥：

```powershell
$keyContainer = $cert.PrivateKey.CspKeyContainerInfo.UniqueKeyContainerName
$keyPath = Join-Path "$env:ProgramData\Microsoft\Crypto\RSA\MachineKeys" $keyContainer
& icacls.exe $keyPath /grant '*S-1-5-20:R'
```

绑定证书：

```powershell
$rdp = Get-CimInstance `
    -Namespace 'root/cimv2/TerminalServices' `
    -ClassName Win32_TSGeneralSetting `
    -Filter "TerminalName='RDP-tcp'"

Set-CimInstance `
    -InputObject $rdp `
    -Property @{ SSLCertificateSHA1Hash = $cert.Thumbprint } | Out-Null
```

把导出的 `.cer` 复制到控制电脑，在控制电脑管理员 PowerShell 中导入：

```powershell
Import-Certificate `
    -FilePath 'C:\Path\rdp.example.com.cer' `
    -CertStoreLocation 'Cert:\LocalMachine\Root'
```

重启 `TermService` 会断开现有 RDP，必须在本地操作或保留备用远控通道：

```powershell
Restart-Service TermService -Force
```

回退到 Windows 自动证书：

```powershell
$rdpReg = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp'
Remove-ItemProperty `
    -LiteralPath $rdpReg `
    -Name SSLCertificateSHA1Hash `
    -ErrorAction SilentlyContinue
Restart-Service TermService -Force
```

确认自动证书恢复后，再删除自签名证书和控制电脑中的受信任根证书。

## 十一、在 1080p 控制端使用 1440p 工作区

控制电脑是 `1920x1080` 时，RDP 默认会创建 1080p 会话。要保留 `2560x1440` 逻辑工作区，在控制电脑双击：

```text
Client\Control-PC-Tools\Create-1440p-RdpFile.cmd
```

输入公网 IP/域名和 RDP 端口后，桌面会生成 `FRP-RDP-2560x1440.rdp`。核心设置：

```text
desktopwidth:i:2560
desktopheight:i:1440
smart sizing:i:1
dynamic resolution:i:0
```

它创建固定 1440p 的远程逻辑桌面，再缩放到 1080p 窗口。关闭动态分辨率可以避免调整窗口时把远程会话改回 1080p。

## 十二、后台运行、日志和开机自启

### 12.1 Linux FRPS

服务端安装脚本已经创建并启用 `frps.service`：

```bash
sudo systemctl is-enabled frps
sudo systemctl status frps --no-pager
```

FRPS 在后台运行，不依赖 SSH 会话。标准输出和错误都追加到 FRP 安装目录的 `frps.log`，systemd 在进程异常退出后 5 秒重启，系统启动时自动运行。

### 12.2 Windows FRPC

`Setup-Client.cmd` 已自动注册 `FRP Client` 任务。单独重建时可双击 `Install-FrpcStartup.cmd`，其默认目录为：

```text
C:\Program Files\FRP
```

计划任务使用 `SYSTEM` 身份，在系统启动时运行，不依赖用户登录，不弹终端窗口，异常退出后每分钟重试。如果安装时已有手动 FRPC 进程，脚本不会结束它；下次重启后由任务接管。

## 十三、清理 MSTSC 目标历史

在控制电脑编辑 `Client\Control-PC-Tools\Clear-RdpHistory.cmd` 顶部：

```bat
set "TARGET=云服务器IP或域名"
```

不要带协议、端口或路径。关闭全部 MSTSC 窗口后双击运行。

脚本按 IP 或域名精确匹配，并兼容历史中的 `目标:端口`；不会按 MRU 新旧顺序删除，也不会清空其他目标。只填写域名时，会同时清理同一域名的所有数字端口记录。

## 十四、微软账号新旧密码缓存

修改微软账号云端密码后，如果被控电脑一直使用 PIN、指纹或 Windows Hello 登录，本机离线凭据可能仍是旧密码，因此 RDP 可能继续接受旧密码。

需要刷新时，在被控电脑联网状态执行：

```cmd
runas /user:MicrosoftAccount\your-email@example.com cmd.exe
```

输入新密码；或者注销 Windows，在登录界面选择“密码”而不是 PIN，用新密码登录一次。

## 十五、排障顺序

不要同时修改 FRP、证书、NLA、UDP 和密码。按链路逐层检查。

### 15.1 控制电脑到云服务器

```powershell
Resolve-DnsName <域名> -Type A
Test-NetConnection <云服务器IP或域名> -Port <所选RDP公网端口>
```

- `False`：检查云安全组、Linux 防火墙、FRPS 和 FRPC。
- `True`：TCP 已到达公网端口，继续检查 RDP TLS/NLA/凭据。

### 15.2 云服务器

```bash
sudo systemctl status frps --no-pager
sudo tail -n 100 /opt/frp/frp_0.65.0_linux_amd64/frps.log
sudo ss -lntup
```

也可以运行 `Server/Check-Server.sh`。重点确认 FRPS 监听所选控制端口、FRPC 已登录、所选 RDP 公网端口的 TCP/UDP 代理创建成功，没有 token 或端口占用错误。

### 15.3 被控 Windows 电脑

优先双击 `Client/Check-Client.cmd`，或执行：

```powershell
Test-NetConnection 127.0.0.1 -Port 3389
Get-Process frpc -ErrorAction SilentlyContinue
Get-ScheduledTask -TaskName 'FRP Client'
Get-CimInstance Win32_Process -Filter "Name='frpc.exe'" |
    Select-Object ProcessId, ExecutablePath, CommandLine
```

无论 Windows 版本或 RDP 方案是什么，本机 3389 必须为 `True`。

### 15.4 TCP 为 True 但出现“内部错误”

这通常发生在 RDP TLS/NLA 阶段，不等同于 FRP 断线：

1. 先用公网 IP 测试。
2. 再用解析到相同 IP 的域名测试。
3. 查看控制端 `TerminalServices-RDPClient/Operational`。
4. 查看被控端 `RemoteConnectionManager`、`LocalSessionManager`、`RdpCoreTS`、Schannel 和 Security 日志。

如果 IP 连续成功而特定域名失败，优先考虑 DNS、域名信誉、SNI 或控制端网络策略，不要反复重启 FRPC。

## 十六、修改配置

修改服务端 YAML 后：

```bash
FRP_DIR=/opt/frp/frp_0.65.0_linux_amd64
sudo "$FRP_DIR/frps" verify -c "$FRP_DIR/frps.yaml"
sudo systemctl restart frps
```

修改 Windows TOML 后：

```powershell
cd 'C:\Program Files\FRP'
.\frpc.exe verify -c .\frpc.toml
```

让客户端新配置生效需要重启 FRPC。该操作会中断当前远程通道，必须本地操作或保留备用远控方式：

```powershell
Stop-ScheduledTask -TaskName 'FRP Client'
Start-Sleep -Seconds 2
Start-ScheduledTask -TaskName 'FRP Client'
```

本套脚本和教程只针对 `0.65.0` 验证。需要升级时应另行验证服务端 YAML、客户端 TOML、后台服务和完整 RDP 链路，不要直接套用未验证的新版本。
