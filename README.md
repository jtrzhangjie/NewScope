# NewScope

[![Verify](https://github.com/jtrzhangjie/NewScope/actions/workflows/verify.yml/badge.svg)](https://github.com/jtrzhangjie/NewScope/actions/workflows/verify.yml)
[![Release](https://img.shields.io/github/v/release/jtrzhangjie/NewScope)](https://github.com/jtrzhangjie/NewScope/releases/latest)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-blue)
![.NET](https://img.shields.io/badge/.NET-10-purple)

**面向嵌入式调试的 J-Link 变量示波器。** 从 AXF/ELF 的 DWARF 调试信息读取变量，通过 J-Link 周期读取目标内存，在桌面上显示实时波形、统计数据，并导出 CSV。

适合查看电机控制、传感器、状态机、控制算法等固件中的全局变量变化。当前版本 **1.0.4**，采用 C#、.NET 10、Avalonia 和 MVVM 实现。

## 下载与使用方式

| 你的需求 | 入口 |
|---|---|
| 下载后安装使用 | **[下载最新 Windows x64 安装包](https://github.com/jtrzhangjie/NewScope/releases/latest/download/NewScope-Setup-win-x64.exe)** |
| 解压后直接运行 | **[下载最新免安装 ZIP](https://github.com/jtrzhangjie/NewScope/releases/latest/download/NewScope-Portable-win-x64.zip)** |
| 查看历史版本、更新说明和 SHA256 | [GitHub Releases](https://github.com/jtrzhangjie/NewScope/releases) |
| 获取全部源码、修改或自行构建 | `git clone https://github.com/jtrzhangjie/NewScope.git` |

安装包与免安装版均包含 .NET 运行时，**无需另外安装 .NET**。免安装 ZIP 请完整解压，运行其中的 `NewScope.exe`，并保留旁边的 `symbols/` 文件夹。配置保存在用户的 LocalAppData，不保存在程序目录。

仓库保存源码、文档和最终测试报告；可执行程序通过 Release 附件分发。Release 页面自动提供的 **Source code** 压缩包用于获取源码；直接使用程序请选择 `.exe` 安装包或 Portable ZIP。

## 主要功能

- **符号导入**：读取带 DWARF 调试信息的 AXF/ELF，展开常见结构体和小型标量数组，按名称、类型、地址搜索。
- **实时采集**：SWD 连接 Cortex-M，选择目标设备和通信速度；合并邻近地址读取，显示实际采样率。
- **多窗格波形**：最多 4 个上下窗格，支持时间缩放、拖拽平移、自动跟随、Y 轴缩放和适配。
- **WATCH**：当前值、最小值、最大值、平均值；可控制通道显示和所在窗格。
- **CSV 导出**：从一致性快照导出所有选中通道，带进度和取消，取消或失败时保留原目标文件。
- **工作区恢复**：恢复窗口、主题、语言、设备、采样设置、通道及窗格分配；同名不同地址的变量可分别保存。
- **稳定性设计**：J-Link DLL 使用独立 worker 进程；单实例运行；缓存预算；异常日志、minidump、UI watchdog 和诊断 ZIP。

## 运行环境

### 查看 Demo

- Windows 10/11 **x64**。
- Demo 不需要探针和目标板，也不需要安装 J-Link 软件。

### 连接真实目标板

1. SEGGER J-Link 探针及连接正确的目标板，目标板已供电。
2. 安装 [SEGGER J-Link Software](https://www.segger.com/downloads/jlink/)，提供 `JLink_x64.dll`。
3. 准备**与板上当前固件匹配**、包含 DWARF 调试信息的 AXF/ELF。

DLL 优先查找位置：

```text
D:\Program Files (x86)\SEGGER\JLink\JLink_x64.dll
C:\Program Files\SEGGER\JLink\JLink_x64.dll
C:\Program Files (x86)\SEGGER\JLink\JLink_x64.dll
```

随后尝试系统默认 DLL 搜索。设备默认为 `Cortex-M0`，可在顶部设备按钮中输入 J-Link 支持的设备名。SEGGER 软件和 DLL 不随本项目分发。

## 操作指南

### 1. 先用 Demo 熟悉界面

在程序目录打开 PowerShell：

```powershell
.\NewScope.exe --demo
```

程序加载随附示例变量并自动开始合成波形采集。示例符号地址仅供演示；实际硬件应导入自己的符号文件。

### 2. 导入固件符号

1. 使用 Keil/GCC 等工具链编译固件并保留调试信息。
2. 启动 NewScope，保持断开状态，点击左侧“导入”（`Ctrl+O`）。
3. 选择 `.axf`、`.elf` 或兼容的 `.out` 文件。
4. 展开变量分组，或按名称、类型、地址搜索，勾选需要采集的变量。

导入的是变量地址和类型信息，程序不执行固件烧录。重新编译、重新烧录后如变量地址变化，应重新导入对应文件。

### 3. 设置设备并连接

1. 在顶部选择设备名、SWD 速度和每通道请求采样率。
2. 选择窗格数量和新变量的目标窗格。
3. 点击“连接”。已有选中通道时，连接成功后自动开始采集。
4. 用“开始 / 暂停”或 `F5` 控制采样；需要更换设备、SWD 速度或符号时，先断开。

连接时会请求目标固件继续运行。**暂停采样只停止 NewScope 读取，不暂停 MCU。** `MAX` 是请求探针最高速度，实际速度由探针和目标协商；请求速率与实测速率可能不同。

### 4. 查看、缩放与管理波形

| 操作 | 效果 |
|---|---|
| 在绘图区滚动滚轮 | 缩放时间范围，支持 10 ms–1 h |
| 左键拖拽波形 | 平移当前时间视图，退出自动跟随 |
| 在 Y 轴区域滚动滚轮 | 手动缩放 Y 轴 |
| 双击 Y 轴 | 恢复自动 Y 轴范围 |
| “跟随” | 跟随最新数据 |
| “适配”或双击绘图区 | 适配当前保留的数据 |
| WATCH 中的窗格字母按钮 | 循环移动通道到其他窗格 |
| WATCH 显示勾选框 | 显示 / 隐藏曲线，仍继续采集和保留缓存 |
| 左侧取消变量勾选 | 从采集通道中移除该变量 |
| “清除” | 清空采样并重置时间基准 |

工具栏的时间窗和跟随状态作用于工作区，各窗格保有自己的视图和 Y 轴状态。重新连接会开启新会话并清空上一会话数据，需要保留的结果请先导出。

### 5. 导出 CSV

点击“导出 CSV”（`Ctrl+E`），选择目标文件。导出过程中可取消，并可继续观察采集。文件格式为 UTF-8：第一列 `time_s`，后续列为通道；缺失或非有限数值为空，同名变量的列名带地址以区分。

### 快捷键

| 快捷键 | 功能 |
|---|---|
| `F5` | 连接 / 开始 / 暂停采样 |
| `Ctrl+O` | 导入符号 |
| `Ctrl+E` | 导出 CSV |
| `Ctrl+D` | 导出诊断包 |
| `Ctrl+L` | 中英文切换 |
| `Esc` | 关闭错误提示 |

## 从源码构建与打包

开发环境：Windows x64、PowerShell 7、`global.json` 指定的 .NET 10.0.401 SDK 或同一功能带后续补丁。制作安装器还需要 NSIS 3.x。Python 仅用于可选的图标和符号辅助脚本，不是主程序构建依赖。

```powershell
git clone https://github.com/jtrzhangjie/NewScope.git
cd NewScope
dotnet restore NewScope.slnx
dotnet build NewScope.slnx -c Release --no-restore
dotnet test tests/NewScope.Tests/NewScope.Tests.csproj -c Release --no-restore
dotnet run --project src/NewScope/NewScope.csproj -c Release -- --demo
```

一键还原、构建、测试、发布、打包：

```powershell
pwsh -File tools/verify.ps1
```

最终产物：

```text
dist/
  NewScope-Setup-win-x64.exe          # 安装包，可直接双击安装
  NewScope-Portable-win-x64.zip       # 免安装版，完整解压后运行
  SHA256SUMS.txt                      # 下载文件校验值
  publish/
    NewScope.exe                     # 可直接运行的自包含程序
    symbols/stepperctrl.json          # 随附示例符号
  symbols/                           # 与本次程序匹配的 PDB 归档
```

没有 NSIS 时，使用 `pwsh -File tools/verify.ps1 -SkipPackage` 生成运行版和免安装 ZIP；`-SkipTests` 可跳过测试。`-NsisCompiler` 可指定 `makensis.exe` 路径。详细参数、版本检查和发布流程见 [构建与发布文档](docs/BUILD_AND_RELEASE.md)。

清理 `bin/obj` 等中间目录，同时保留 `dist/` 最终程序：

```powershell
pwsh -File tools/clean.ps1
```

## 版本标签与 Release

使用 `v主版本.次版本.修订版本`，例如 **`v1.0.4`**。标签标识这次发布的源码快照，Release 附件保存对应安装包、免安装 ZIP、校验值和调试符号。

`Release` 工作流会检查标签与项目版本一致，再构建、测试、打包并创建正式 Release。固定附件名让上方“下载最新安装包”链接自动跟随最新版，无需用户 clone 仓库。

维护者操作和重发约定见 [构建与发布文档](docs/BUILD_AND_RELEASE.md#6-版本标签与-github-release)。版本记录见 [CHANGELOG.md](CHANGELOG.md)。

## 测试与性能边界

- 当前回归测试 **103 项通过**，覆盖环形缓存、采集生命周期、导出保护、符号导入、配置和绘图算法等。
- 满历史测试覆盖 32/128/256/512 通道和 1/4 窗格绘图。
- 本机合成源 256 通道 × 2000 Hz 持续 300 秒，约 1.536 亿个通道样本，最终错误数为 0。
- 512 MiB 是**采样缓存预算**；CSV 快照会增加接近一份历史副本。极限实时场景进程私有内存约 2.7–3.1 GiB。
- 高通道全览仍有绘图延迟和偶发调度停顿。合成源平均 2000 Hz 不代表 J-Link 真机带宽，也不保证每个采样间隔恒定。

运行压力测试（会打开测试窗口，使用合成源，不保存测试工作区到用户配置）：

```powershell
pwsh -File tools/stress.ps1 -DurationSeconds 300
```

完整数据、环境和限制见 [压力测试报告](docs/STRESS_TEST_2026-10-26.md)，最终原始结果保存在 [docs/testing/2026-10-26](docs/testing/2026-10-26)。

## 日志、配置和故障排查

数据目录：`%LOCALAPPDATA%\NewScope`。

| 文件 / 目录 | 内容 |
|---|---|
| `settings.json` | 工作区、设备、主题、语言、窗口状态 |
| `symbols/` | 已导入的符号缓存 |
| `newscope.log`、`logs/` | 当前与历史日志，单份 5 MB 轮转，最多保留 10 份历史 |
| `crash.log`、`hang.log` | 崩溃与 UI 无响应摘要 |
| `dumps/` | minidump 与旁侧摘要；UI 无响应达到 10 秒时抓取现场 |
| `clean-exit.flag` | 正常退出标记 |

常见问题：

- **找不到 J-Link DLL**：确认安装 x64 J-Link 软件，检查 DLL 位置；Demo 不依赖 DLL。
- **导入后变量少或没有变量**：确认有 DWARF 调试信息，变量未被优化掉，且属于支持的 RAM 标量类型。
- **速率低于设定值**：减少实际采集通道、降低请求速率；隐藏曲线不会减少 J-Link 读取量。
- **掉线后采集停止**：检查供电和连线，点击“重试”或断开后重连；新连接会清空上一会话缓存。
- **闪退或卡住**：重新启动后按 `Ctrl+D` 导出诊断 ZIP，在 [Issues](https://github.com/jtrzhangjie/NewScope/issues) 提供复现步骤、时间、设备与显示器缩放信息。

诊断文件可能包含用户名、路径、符号名和目标内存片段，共享前请确认内容。

## 当前限制

- 主要验证 Keil/GCC 常见 DWARF 与 32 位、小端 Cortex-M 场景；IAR/Clang、压缩调试段、位域、复杂 C++ 类型和大数组覆盖有限。
- 尚无探针序列号选择界面，多探针同时连接需要额外处理。
- J-Link PLUS / Cortex-M0 的既有硬件测试中，探针最高约 15 MHz，目标实际协商约 10 MHz；高通道真机吞吐明显低于 Demo。
- 真实拔线、掉电、更长时间压力和多显示器切换仍需继续硬件回归。
- 当前安装包未做代码签名。

## 项目文档与目录

- [软件技术文档：架构、UML 类图、时序图、流程图](docs/TECHNICAL_DESIGN.md)
- [构建、打包与 GitHub Release 发布](docs/BUILD_AND_RELEASE.md)
- [压力测试报告](docs/STRESS_TEST_2026-10-26.md)
- [第三方组件说明](THIRD_PARTY_NOTICES.md)

```text
src/NewScope/              桌面程序、模型、采集、符号解析、绘图与诊断
tests/NewScope.Tests/      自动化回归测试与可移植 ELF 测试样本生成器
tools/NewScope.Stress/     可重复执行的大数据和真实窗口压力测试
tools/                    验证、打包、清理与辅助脚本
packaging/                NSIS 安装器与相关许可证
symbols/                  随附示例符号
docs/                     技术文档和最终测试记录
.github/workflows/        持续验证和标签发布工作流
```

项目自身暂未指定开源许可证；源码公开不自动附带额外授权。第三方组件按各自许可证使用，详见第三方说明。
