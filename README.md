# NovaZip

基于 7-Zip 26.06 内核的现代化 Windows 压缩 / 解压工具，使用 C# / WPF（.NET 8）构建，提供浅色 / 深色主题、快速解压窗口与任务队列等界面能力。

## 功能特性

- 基于 7-Zip 26.06 原生内核（`runtime\7z.exe` / `7z.dll`），兼容 7z、zip、rar、tar、gz 等常见压缩格式
- WPF 现代化界面，支持浅色 / 深色主题与亚克力（Acrylic）材质
- 快速解压窗口（Quick Extract）与后台任务队列、进度回调
- 全局滚轮平滑滚动，悬浮叠加式细滚动条（不占用内容宽度），悬停 / 拖动时加深高亮
- 归档内文件查看、解压与压缩流程封装（`Engine` / `ArchiveEditor`）
- 资源管理器右键菜单集成（`ContextMenuIntegration`）
- 内置 GitHub Releases 更新检查：下载安装包后按 SHA-256 校验，退出后覆盖重启

## 环境要求

- Windows 10 / 11（x64）
- .NET 8.0 Desktop Runtime（发布版已自包含运行时，可直接运行）

## 从源码构建

```bash
# 还原并构建（Release, win-x64）
dotnet publish NovaZip.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

构建产物位于 `bin\Release\net8.0-windows\` 下。`runtime\` 目录中的 `7z.exe`、`7z.dll` 与许可文件会按项目配置复制到输出目录。

## 下载与安装

预编译的 Windows x64 安装包与 SHA-256 校验文件见 [Releases](https://github.com/xiao-danbao/NovaZip/releases)。

## 目录结构

| 路径 | 说明 |
| --- | --- |
| `Assets\` | 应用图标等资源 |
| `Controls\` | 自定义 WPF 控件（进度环等） |
| `Engine\` | 7-Zip 引擎封装、压缩 / 解压流程、基准与自检 |
| `runtime\` | 随程序分发的 7-Zip 内核与许可文本 |
| `Themes\` | 浅色 / 深色配色与主题资源字典 |
| `MainWindow.xaml(.cs)` | 主窗口 |
| `QuickExtractWindow.xaml(.cs)` | 快速解压窗口 |
| `SmoothScroller.cs` | 全局滚轮平滑滚动 |
| `UpdateService.cs` | GitHub Releases 更新检查与校验安装 |

## 许可与第三方声明

本项目自身源码的许可见 [LICENSE](LICENSE)。

`runtime\7z.exe`、`7z.dll` 及 `7-Zip-License.txt` 来自 7-Zip（Copyright © 1999–2026 Igor Pavlov），采用 GNU LGPL（部分代码为 LGPL + unRAR 限制、BSD 2/3-clause）；具体条款以 `runtime\7-Zip-License.txt` 为准。其中 RAR 解压引擎基于 unRAR 代码，受 unRAR 许可限制：不得用于开发与 WinRAR 兼容的压缩器。
