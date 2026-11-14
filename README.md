# 打π优化

打π优化是一个 Windows 桌面工具。它可以在开游戏前一键切换常用的系统设置，并启动加速器、语音软件等程序。应用使用 C# 和 WPF 开发，支持从系统托盘操作。

## 功能

- **键盘和输入法**：按需屏蔽 Win 键、Ctrl+Esc 和 Alt+Shift，并将前台窗口切换到英文输入。
- **显示**：在仅电脑屏幕、复制、扩展和仅第二屏幕之间切换。
- **音频**：将 Windows 默认音频输出切换到选定的设备。
- **快捷启动**：按列表顺序启动已启用的程序，跳过已经在运行的程序。
- **一键恢复**：恢复应用记录的键盘屏蔽状态、显示模式和输入法布局。音频输出如需切回，可在 Windows 声音设置中调整。

## 从源码运行

在 Windows 上安装 [**.NET 10 SDK**](https://dotnet.microsoft.com/download/dotnet/10.0)，然后在 PowerShell 中运行：

```powershell
git clone https://github.com/Aekvio-xx/ImporveOneClickOptimization.git
cd ImporveOneClickOptimization
dotnet run --project Dapai/Dapai.csproj -c Release
```

如果需要生成包含 .NET 运行时的 Windows x64 版本，可在仓库根目录运行：

```powershell
dotnet publish Dapai/Dapai.csproj -c Release -r win-x64 --self-contained true -o dist/win-x64
```

发布完成后，运行 `dist/win-x64/打π优化.exe`。请保留同目录的运行文件。

## 使用

1. 在「优化设置」中勾选需要的项目。启用音频切换时，选择要使用的输出设备。
2. 在「快捷启动」中添加程序，浏览选择可执行文件，并启用需要一同启动的项目。用每行的 ↑、↓ 调整启动顺序，顺序会自动保存。
3. 点击「一键优化」应用设置。游戏结束后，点击「一键恢复」。这两个操作也可以通过托盘菜单执行。

首次运行时使用内置默认设置。应用将设置保存到运行目录的 `config.json`，日志写入 `logs/`。

只有一块可用屏幕时，优化会保留当前显示状态。使用多块屏幕时，恢复会检查优化前启用的屏幕是否已重新启用，并核对扩展和复制关系。退出时若恢复失败，可继续留在程序中，连接或唤醒屏幕后重试。
