# Pigeon-UTSLManager

<p align="center">
  <strong>Windows 平台 UTSL 多服务器管理器 · 内置三世界地图工坊</strong>
</p>

<p align="center">
  团队：流光工作室　·　作者：<a href="https://github.com/Pigeon-X">Pigeon-X（流光-Pigeon-X）</a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/version-0.2.0-7957D5" alt="Version 0.2.0">
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4" alt="Windows 10/11">
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4" alt=".NET 10">
  <img src="https://img.shields.io/badge/UI-Avalonia%2012.1-8A2BE2" alt="Avalonia 12.1">
  <img src="https://img.shields.io/badge/license-GPL--3.0-blue" alt="GPL-3.0">
</p>

<p align="center">
  <a href="#快速开始">快速开始</a> ·
  <a href="#功能使用">功能使用</a> ·
  <a href="#程序目录">程序目录</a> ·
  <a href="#本地构建">本地构建</a> ·
  <a href="https://github.com/CNS-Team/Pigeon-UTSLManager/issues">问题反馈</a>
</p>

> [!IMPORTANT]
> **运行功能全部由 C# 与 Windows API 驱动。**
> 服务器启停、控制台、插件版本选择、沙箱和地图生成均直接调用代码与 EXE，不依赖 BAT、CMD 或 PowerShell 脚本。仓库中的脚本仅用于开发构建、打包和验收，不进入运行包。

> [!NOTE]
> 本仓库公开维护管理器源码，不包含游戏引擎二进制、私人插件、服务器配置、世界或玩家数据库。
> 界面设计参考 [PGame-TSManager](https://github.com/CNS-Team/PGame-TSManager)；地图工坊由独立仓库 [UTSL-MapStudio](https://github.com/CNS-Team/UTSL-MapStudio) 维护。

## 项目简介

Pigeon-UTSLManager 用一个暗色桌面窗口管理多套 UTSL 服务器，集中查看控制台、维护插件版本，并在同一窗口生成三世界地图。界面与后台分离，服务器加载和日志处理不占用界面线程。

| 仓库内容 | 说明 |
| --- | --- |
| 公开源码 | 管理器、Host 后台、共享运行库、测试与文档 |
| 地图工坊 | 锁定 UTSL-MapStudio 子模块，独立维护、嵌入使用 |
| 构建方式 | 本地构建与打包；CI 仅验证公开源码和编译 |
| 数据边界 | 世界、账号、数据库、日志、私人配置和令牌不进入 Git |

## 主要功能

| 功能 | 说明 |
| --- | --- |
| 多服管理 | 一个窗口管理多个 UTSL 服务端，按 `0. / 1. / 2.` 编号排序 |
| 顺序启动 | 前一服全部世界就绪后再启动下一服，失败暂停队列 |
| 保存与停止 | 验证各世界保存结果，再停止所属进程；支持单服、重启和全部停止 |
| 世界控制台 | `1 / 2 / 3` 切换世界，或同时显示三个；中文、ANSI 色彩、独立指令输入 |
| 总插件库 | 集中维护插件，为每服选择核心与插件的精确版本 |
| 独立沙箱 | 每次启动准备独立运行目录，配置和玩家数据持久保存 |
| 地图工坊 | 实时地图预览、特殊种子、连续母图与独立三世界，附接缝地图长图 |
| 后台运行 | 关闭界面仍可保留服务器，再打开可连接原后台 |

## 快速开始

1. **打开管理器**：运行根目录 `UTSl-Manager.exe`。首次启动名册为空，不会自动启动服务器。
2. **添加服务端**：点击「复制导入服务端包」，选择已停止的完整 UTSL 包；也可「登记现有目录」原地管理。
3. **核对配置**：在「启动设置 → 原生配置」检查游戏端口、世界列表和 REST。不同服务器必须使用不同端口。
4. **选择运行方式**：直接启动原包，或按下文在「总插件库」配置独立沙箱。
5. **启动服务器**：点击「启动」或「全部启动」，等待状态栏确认各世界就绪，再连接游戏。

复制导入保留原目录；登记现有目录仅保存引用。已在外部运行的进程显示为 `EXTERNAL`，管理器不会接管或停止它。

## 功能使用

### 启动、停止与控制台

- **全部启动**：按左侧编号依次启动；每服配置的世界全部就绪后，才启动下一服。自动启动默认关闭，可在「启动设置」开启。
- **停止 / 重启**：先逐世界保存，确认 `World saved` 后发送一次 `/off`；20 秒后若宿主残留，核对 PID、路径与创建时间，仅结束该单进程。保存失败会保留进程并提示。
- **全部停止**：先取消待启动队列，再逐服保存、停止。
- **发送指令**：选中左侧服务器，通过 `1 / 2 / 3` 选择世界，在对应控制台输入；「显示三个」可并排操作，不会广播指令。
- **关闭窗口**：只关闭界面。退出游戏服务器请使用停止按钮，不要直接结束 Host。

世界就绪检查需要 `config/config.json` 中的 `launcher.autoStartServers`，以及 `config/TShockAPI/config.json` 中有效的本机 REST 端口与令牌。三世界独立控制台需要配套 UTSL 的本地世界接口；接口未接入时显示宿主启动输出。

### 总插件库与沙箱

1. 停止目标服，打开「总插件库」并选择服务器。
2. 从该服收录核心和插件，或添加新的插件版本。
3. 选择核心版本、插件及版本，开启「独立运行沙箱」并保存。
4. 启动服务器；后台核验文件 SHA256 后准备新的 `run-0001` 等运行目录。

同名插件可保留多个版本，各服选择互不影响。收录新版本不会热载或替换正在运行的插件；切换版本需停止后重新选择。总库中的共享 DLL 不应手动覆盖。详见 [共享运行机制](docs/SHARED-RUNTIME.md)。

### 地图工坊

打开「地图工坊」→ 设置名称、尺寸、难度、种子与生成方式 → 点击「开始生成」。下方实时查看全景，可缩放、拖动及显示接缝标记。

- **连续母图**：先生成完整母图，再分割为西、中、东三个世界。
- **独立三世界**：分别生成常规世界，再处理接缝边界。
- **种子与环境**：在「种子特性合集」选择特殊种子，在「基础环境补全」查看并选择需要的内容。
- **输出结果**：从地图文件夹打开新批次；只使用包含 `COMPLETE.json` 的成功批次。生成不会自动覆盖运行服地图。边界传送需要另行配置对应插件。

## 程序目录

```text
Pigeon-UTSLManager/
├─ UTSl-Manager.exe          主程序
├─ Plugins/                 总插件库，可见目录只放 DLL
├─ Server/
│  ├─ World/                集中地图目录、地图生成批次
│  ├─ 0.<服务器名>/        每服配置、数据及原包内容
│  └─ 1.<服务器名>/
├─ Core/
│  ├─ Engines/              共享核心版本
│  └─ Runtime/              每服独立运行目录：run-0001…
├─ Data/                    名册、版本清单、控制台日志、操作记录
├─ Backups/                 备份与校验清单
└─ App/                     界面、Host 后台、地图生成引擎及许可
```

地图统一到 `Server/World` 需要完成显式迁移；旧包不会被静默搬动或清理。同一集中目录中的不同服务器地图必须使用不同文件名。目录迁移说明见 [可读目录布局](docs/readable-sandbox-layout.md)。

`UTSl-Manager.exe` 负责界面，`Pigeon.UTSLManager.Host.exe` 负责后台管理，每服 `UnifierTSL.exe` 负责实际游戏。三个世界属于同一 UTSL 进程；沙箱是运行目录隔离，不是针对恶意插件的权限隔离。

## 本地构建

**环境**：Windows 10 1809+ / Windows 11、.NET 10 SDK；执行开发脚本需 PowerShell 7。地图 worker 另需 .NET 9 运行时，地图流水线需 .NET 10 Windows Desktop 运行时。

```powershell
git clone --recurse-submodules https://github.com/CNS-Team/Pigeon-UTSLManager.git
cd Pigeon-UTSLManager
./scripts/Build.ps1
```

完整地图包还需准备符合 [MapStudio 依赖清单](external/MapStudio/docs/DEPENDENCIES.md) 的本地合法输入，再打包到**不存在的新目录**：

```powershell
./scripts/Package.ps1 -Destination <新程序目录> -MapDependencyRoot <地图依赖根目录>
```

运行包无需 PowerShell 或 SDK；需要相应 .NET 运行时。开发脚本只参与本地构建和验收，CI 只检查公开源码与构建，不发布私人服务端数据。

## 常见问题

| 现象 | 处理方式 |
| --- | --- |
| 启动队列等待或失败 | 核对端口、REST 配置、世界名称及服务器日志；HTTP 200 的「世界不存在」不算就绪 |
| 显示 `EXTERNAL` | 原进程由管理器外启动，先通过原方式正常停止，再从管理器启动 |
| 远程桌面显示异常 | 使用 `UTSl-Manager.exe --software-rendering`；检查 `Data/ui-startup.jsonl` |
| 插件更新未生效 | 确认目标服选择了新版本，并在停止后重新启动 |

反馈问题请附管理器版本、操作步骤和脱敏日志。服务器状态正常不等于玩家进服、准入与跨世界传送已通过测试。

---

源码采用 **GPL-3.0**。第三方归属见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
