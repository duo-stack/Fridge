# Fridge

Fridge 是一个便携的 FRP + Windows RDP 图形化部署工具：

- 通过 SSH 将内嵌的 FRPS 部署到 Linux 服务器。
- 在当前 Windows 电脑释放 FRPC、配置 RDP 并注册开机任务。不包含安装、激活 RDP 功能。

## 使用

最终用户只需要复制 `publish/Fridge.exe`。

部署本机被控端时，请以管理员身份运行。工具不会自动修改云厂商安全组，部署完成后仍需按提示放行 FRPS TCP 端口和 RDP TCP/UDP 端口。

## 构建

```powershell
dotnet build .\Fridge.csproj
dotnet publish .\Fridge.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o .\publish
```

FRP 0.65.0 的 Windows 客户端、Linux amd64/arm64 服务端和部署脚本均位于 `Assets`，并编译进最终 EXE。

`Legacy` 仅包含旧版手工部署教程和脚本，可随时删除，不参与项目构建和运行。

## 发布产物

发布成功后，可分发的最终程序位于：

```text
publish\Fridge.exe
```

将这一个 EXE 文件复制到其他 Windows x64 电脑即可使用，无需复制 `bin`、`obj`、`Assets`、`Legacy` 或其他源码文件。

## 开发声明

本项目 100% 由 Codex GPT-5.6-Sol 在高推理强度下实现。
