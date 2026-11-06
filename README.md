# FlashDesktop（FD 桌面）

Windows 桌面的「分类卡片面板」：把开始菜单里的程序按用途收进几张卡片，卡片之间留空、不遮挡桌面，
双击卡片图标即可启动。纯本地、绿色免安装，不修改任何系统设置。

> 架构代号 **B+**：每个分类一张独立窗口，窗口只覆盖卡片本身，空白区域完全透传桌面。
> 不使用 WorkerW / SetParent / 全局钩子 / DLL 注入，不隐藏原生桌面图标。

## 功能

- **分类收录**：扫描开始菜单（当前用户 + 所有用户）与 UWP AppsFolder，按关键词预判分类（办公 / 开发 / 工具 / 娱乐 / 其他）。
- **图标缓存**：SHA1(路径 + 修改时间) 为键，降采样 PNG 落盘；二次启动 0 次 Shell 调用。
- **拖放收录**：从资源管理器拖入文件/文件夹只记录路径，仅声明 Copy/Link，**绝不移动原文件**。
- **卡片交互**：双击启动、分类间拖拽移动、卡片内手动排序、常用置顶（按使用次数排序）。
- **客户端**（v0.5.0）：托盘右键 → 设置中心 / 检查更新 / 关于
  - 外观风格：主题（跟随系统/深/浅）、6 种主色调、卡片风格（纯色/半透明/渐变）、圆角、磁贴大小
  - 常规设置：动画、常用置顶、Alt+Space 浮层、显隐面板、重建卡片/图标缓存、打开数据目录
  - 检查更新：手动点按钮，读取 `{ "version", "notes", "url" }` 清单
  - 关于：版本、构建时间、收录统计、数据路径、设计原则

## 三条红线（冲突时 1 > 2 > 3）

1. **原桌面布局任何情况下都能完整找回**：不移动文件、不改系统设置、不写注册表、不写桌面目录。
2. **流畅轻量化**：暖启动空闲私有字节 ~86MB、托管堆 <10MB、数据+图标缓存 <5MB。
3. **省电**：全事件驱动，无定时器、无轮询、无全局钩子。

## 构建

需要 .NET 8 SDK（Desktop Runtime 即可运行成品）。

```bash
dotnet build FlashDesktop.csproj -c Release
dotnet publish FlashDesktop.csproj -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -o publish
```

产物：`publish/FlashDesktop.exe`（单文件、framework-dependent，约 264KB）。

## 自测

```bash
publish/FlashDesktop.exe --self-test
```

退出后读取 `Data/selftest.json`，覆盖窗口层级、Z 序、图标缓存、拖放不改原文件、无驻留定时器、
隐藏→显示可逆、客户端四页构建等检查项。

## 更新清单

客户端「检查更新」默认读取本仓库根目录的 `version.json`：

```
https://raw.githubusercontent.com/startime-ltk/FlashDesktop/main/version.json
```

格式：`{ "version": "0.5.0", "notes": "更新说明", "url": "下载页" }`。
发布新版时改这个文件即可，客户端按版本号比较。

## 数据

全部位于 exe 同级 `Data/` 目录：`items.json`、`settings.json`、`layout.json`、`icon-cache/`。
不含任何用户真实文件；删除该目录即重置为首次运行状态。

## 结构

```
Program.cs            入口：单实例、加载数据、构建窗口层、托盘、自测
Core/
  AppIndexer.cs       开始菜单 + UWP 扫描、分类预判
  IconLoader.cs       SHGetFileInfo 取图 + 磁盘缓存 + LRU
  ItemStore.cs        条目存储（原子写 + .bak 回退）
  Launcher.cs         启动解析（含 UWP shell:AppsFolder）
  LayoutConfig.cs     卡片几何参数化
  Settings.cs         偏好设置（schema v2）
  Updater.cs          版本清单检查
  WindowLayer.cs      每分类一窗口、Z 序、显隐重建
  DesktopState.cs     单一状态源（预留图标显隐位）
  SelfTest.cs         一次性事件驱动自检
UI/
  CardView.cs         卡片渲染、拖放、双击
  ClientWindow.cs     客户端壳（四页）
  TrayIcon.cs         纯 Win32 托盘 + RegisterHotKey
  HotkeyLauncher.cs   Alt+Space 浮层
  Theme.cs            调色板 / 主色调 / 卡片风格
S0/                   B+ 架构可行性探针（Python + ctypes）
```
