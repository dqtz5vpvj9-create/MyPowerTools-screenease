# ScreenEase

[![CI](https://github.com/dqtz5vpvj9-create/ScreenEase/actions/workflows/ci.yml/badge.svg)](https://github.com/dqtz5vpvj9-create/ScreenEase/actions/workflows/ci.yml)
[![Release](https://github.com/dqtz5vpvj9-create/ScreenEase/actions/workflows/release.yml/badge.svg)](https://github.com/dqtz5vpvj9-create/ScreenEase/actions/workflows/release.yml)

ScreenEase 是一个开源的 Windows 护眼工具，用原生 WPF 界面调节屏幕色温和亮度，并提供休息提醒。

它适合长时间看屏幕的人：写代码、阅读文档、办公、看视频时，可以快速切换更舒服的显示模式，也可以保存自己的常用配置。

## 功能

- 调节屏幕色温和亮度
- 内置多种显示模式：日间办公、长读柔光、细节清晰、影音暖光、高亮专注、夜间低蓝、我的方案
- 默认模式基于公开论文中的数字眼疲劳、短波长光和夜间屏幕使用证据，参数说明见 `docs/PRESET_RATIONALE.md`
- 保存当前调节为自定义模式
- 新增多个自定义模式
- 休息提醒：专注、短休、长休、暂停、继续、重置
- 支持多显示器
- 使用 Windows 原生命名管道通信，桌面使用时无需占用 TCP 端口
- 原生 WPF/.NET 8 桌面界面，未使用 WebView

## 当前状态

ScreenEase 目前处于开发阶段，已经可以在 Windows 上运行核心护眼功能和桌面 UI。

已经实现：

- core service
- WPF 桌面客户端
- 色温/亮度调节
- 多显示器应用
- 自定义模式保存和新增
- 休息提醒设置
- 命名管道 IPC
- 测试用 memory driver

后续计划：

- 托盘图标
- 安装包
- 开机自启动
- 更多显示器环境测试

## 系统要求

- Windows 10 或 Windows 11
- .NET 8 SDK
- Visual Studio 2022，或任意支持 .NET 8 的编辑器

## 快速运行

在仓库根目录执行：

```powershell
dotnet build .\ScreenEase.sln -c Release
```

启动桌面 UI。桌面会自动检查并启动本机 core service：

```powershell
dotnet run --project .\src\ScreenEase.Desktop\ScreenEase.Desktop.csproj -c Release
```

也可以单独启动 core service 做调试：

```powershell
$env:ScreenEase__Driver = 'windows'
$env:ScreenEase__SettingsPath = "$PWD\.local\settings.json"
dotnet run --project .\src\ScreenEase.CoreService\ScreenEase.CoreService.csproj -c Release -- --pipe-only
```

桌面 UI 默认连接：

```text
pipe:screenease.core
```

## 安全测试模式

如果只想测试程序流程，使用 memory driver。这个模式不会修改屏幕显示效果。

```powershell
$env:ScreenEase__Driver = 'memory'
$env:ScreenEase__SettingsPath = "$PWD\.local\settings.json"
dotnet run --project .\src\ScreenEase.CoreService\ScreenEase.CoreService.csproj -c Release -- --pipe-only
```

## 测试

```powershell
dotnet run --project .\tests\ScreenEase.Tests\ScreenEase.Tests.csproj -c Release
```

## 发布

推送 `v*` 标签会自动创建 GitHub Release，并上传 `win-x64` 压缩包。发布包中直接运行 `ScreenEase.Desktop\ScreenEase.Desktop.exe` 即可，桌面端会自动启动后端。

```powershell
git tag v0.1.0
git push origin v0.1.0
```

也可以在 GitHub Actions 页面手动运行 Release workflow。

## 项目结构

```text
src/ScreenEase.Core         核心模型、显示驱动、设置、休息提醒
src/ScreenEase.CoreService  后台服务、命名管道、HTTP 调试接口
src/ScreenEase.Desktop      WPF 桌面客户端
src/ScreenEase.NativeHost   Chromium native messaging host
tests/ScreenEase.Tests      无额外测试框架的测试程序
docs/                       API、IPC、桌面 UI 和当前状态文档
tools/                      辅助安装脚本
```

## 开发说明

桌面客户端和 core service 分离。桌面 UI 通过命名管道发送命令，core service 负责读取设置、应用显示效果、维护休息提醒状态。

默认桌面使用路径无需 HTTP 服务。HTTP API 主要用于调试和其他前端集成，文档见 `docs/API.md`。

## License

MIT
