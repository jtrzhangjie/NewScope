# NewScope 软件技术文档

> 对应版本：1.0.4。图采用 Mermaid，GitHub 可直接渲染。图中只列核心成员，省略 MVVM 自动生成的属性、命令和部分辅助类。

## 1. 目标与业务边界

NewScope 将嵌入式固件的“变量名称和类型”关联到“运行时地址”，再通过 J-Link 读取数值，以波形和统计方式帮助观察算法行为。

主要业务包括：导入符号、选择变量和窗格、连接目标、连续采样、暂停/恢复、缩放与平移、导出 CSV、恢复工作区和收集诊断数据。

当前为 Windows x64 桌面程序，重点覆盖 32 位小端 Cortex-M 的常见 RAM 标量变量。它不承担固件编译、下载烧录或完整调试器功能。连接会请求目标继续运行，暂停采样不会暂停目标 MCU。

## 2. 总体架构与进程边界

```mermaid
flowchart LR
    User["用户"]
    Elf["AXF / ELF<br/>DWARF 调试信息"]
    Settings["LocalAppData<br/>设置与符号缓存"]
    Csv["CSV 文件"]
    Diag["日志 / dump / 诊断 ZIP"]
    subgraph MainProcess["主进程：NewScope.exe"]
        View["MainWindow / WaveformPlot"]
        VM["MainViewModel<br/>命令与工作区协调"]
        Importer["ElfSymbolImporter"]
        AC["AcquisitionController<br/>采集任务与帧提交"]
        Buffer["ChannelBuffer<br/>环形历史与分块摘要"]
        Source["IValueSource"]
        Client["JLinkWorkerSession<br/>超时与进程管理"]
        Demo["DemoValueSource"]
        Export["CsvExporter / AtomicFile"]
        Monitor["AppLog / DiagnosticsService / UiWatchdog"]
    end
    subgraph WorkerProcess["worker 进程：同一 exe 的 --jlink-worker 模式"]
        Worker["JLinkWorker<br/>命令分发"]
        Session["JLinkSession / JLinkNative"]
        DLL["外部 JLink_x64.dll"]
    end
    Target["J-Link 探针 / SWD / MCU RAM"]
    User --> View
    View <--> VM
    Elf --> Importer --> VM
    VM <--> Settings
    VM --> AC --> Source
    AC --> Buffer --> View
    Source --> Demo
    Source --> Client
    Client <-->|"stdin / stdout 二进制协议"| Worker
    Worker --> Session --> DLL --> Target
    VM --> Export --> Csv
    Buffer --> Export
    View --> Monitor --> Diag
```

### 2.1 分层职责

| 层 | 主要对象 | 职责 |
|---|---|---|
| 视图 | `MainWindow`、`WaveformPlot` | 文件选择、窗口事件、输入交互、绘图、UI 定时刷新 |
| 协调 | `MainViewModel`、`ChannelViewModel`、`VariableNode` | 命令、状态、通道选择、窗格管理、绑定属性 |
| 数据与采集 | `SampleChannel`、`ChannelBuffer`、`AcquisitionController` | 帧写入、缓存、时间基准、生命周期、快照 |
| 设备 | `IValueSource`、`JLinkValueSource`、`IJLinkSession` | 数值来源、读块规划、协议客户端、原生访问 |
| 支撑 | `ElfSymbolImporter`、`CsvExporter`、`AppSettingsStore` 等 | 导入、导出、持久化、日志与诊断 |

采集和 CSV 服务依赖 `ISampleChannel`，不依赖具体的 UI ViewModel。`ChannelViewModel` 通过内部 `SampleChannel` 暴露同一份符号和缓存。

## 3. UML 类图

### 3.1 符号、通道和缓存

```mermaid
classDiagram
    class SymbolCatalog {
        +string Format
        +string Elf
        +uint RamStart
        +uint RamSize
        +int VariableCount
        +List~SymbolVariable~ Variables
    }
    class SymbolVariable {
        +string Name
        +uint Address
        +int Size
        +string Type
        +bool Signed
        +string Kind
        +string Identity
    }
    class ISampleChannel {
        <<interface>>
        +SymbolVariable Symbol
        +ChannelBuffer Buffer
        +string Name
    }
    class SampleChannel {
        +SymbolVariable Symbol
        +ChannelBuffer Buffer
    }
    class ChannelViewModel {
        +SampleChannel Data
        +bool IsVisible
        +int PaneIndex
        +UpdateStats()
        +MovePane()
    }
    class ChannelBuffer {
        -double[] _times
        -double[] _values
        -RangeSummary[] _summaries
        +int Count
        +int MaxCapacity
        +Add(time, value)
        +Clear()
        +SetMaxCapacity(capacity)
        +GetStats() ChannelStats
        +CopyVisible(start, end, destination, budget) int
        +TryGetVisibleRange(start, end, min, max) bool
        +TryGetNearest(time, start, end, point) bool
        +ToArray() SamplePoint[]
    }
    class SamplePoint {
        <<struct>>
        +double Time
        +double Value
        +bool IsValid
    }
    class ChannelStats {
        <<struct>>
        +double Min
        +double Max
        +double Average
        +double Last
        +int Count
    }
    class ChannelSnapshot {
        +string Name
        +SamplePoint[] Samples
    }
    SymbolCatalog "1" *-- "0..*" SymbolVariable
    ISampleChannel <|.. SampleChannel
    ISampleChannel <|.. ChannelViewModel
    ChannelViewModel "1" *-- "1" SampleChannel
    SampleChannel --> SymbolVariable
    SampleChannel "1" *-- "1" ChannelBuffer
    ChannelBuffer ..> SamplePoint : 返回逻辑样本
    ChannelBuffer ..> ChannelStats : 计算统计
    ChannelSnapshot "1" *-- "0..*" SamplePoint
```

关键约定：

- `Identity = Name + "@" + Address`，同名不同地址的变量独立存在。
- `ChannelBuffer` 的时间有序，查找使用二分；底层为并行 `double[]`，不为每个采样分配对象。
- 无效数值可存在于历史中，但不参与有限值的范围/平均计算。统计 `Count` 是保留的样本数。
- 快照使用独立数组，后续采集覆盖环形缓存不会改变导出数据。

### 3.2 采集与 J-Link 访问

```mermaid
classDiagram
    class MainViewModel {
        -AcquisitionController _acquisition
        -SemaphoreSlim _connectionGate
        -IValueSource _source
        +ConnectAsync()
        +DisconnectAsync()
        +PauseAsync()
        +ImportSymbolsAsync(path)
        +ExportCsvAsync(path, name)
        +ShutdownAsync()
    }
    class AcquisitionController {
        -object _gate
        -long _dataVersion
        -CancellationTokenSource _cts
        -Task _completion
        +int SampleRateHz
        +bool IsRunning
        +Exception LastError
        +SetChannels(channels)
        +Reset(startClock)
        +Start(source) bool
        +StopAsync()
        +CaptureSnapshot() ChannelSnapshot[]
        +GetMetrics() AcquisitionMetrics
    }
    class IValueSource {
        <<interface>>
        +string Description
        +int ActualSpeedKHz
        +int MaxSpeedKHz
        +Open()
        +Close()
        +Read(channels) double[]
        +Dispose()
    }
    class DemoValueSource {
        +Read(channels) double[]
    }
    class JLinkValueSource {
        -ReadBlock[] _blocks
        -IJLinkSession _session
        +int RecoveryCount
        +Read(channels) double[]
    }
    class IJLinkSession {
        <<interface>>
        +Open(device, speed, resume)
        +ReadMemory(address, buffer, length)
        +Close()
        +Dispose()
    }
    class JLinkWorkerSession {
        -Process _process
        -int _workerGeneration
        -PendingRequest _pending
        +Open(device, speed, resume)
        +ReadMemory(address, buffer, length)
        +Close()
    }
    class JLinkWorker {
        <<static>>
        +Run(args) int
    }
    class JLinkSession {
        +Open(device, speed, resume)
        +ReadMemory(address, buffer, length)
        +Close()
    }
    class JLinkNative {
        <<static>>
        +TryOpen(error) bool
        +ReadMemory(address, buffer, length) int
        +SetSpeed(speed)
        +Go()
    }
    MainViewModel "1" *-- "1" AcquisitionController
    MainViewModel --> IValueSource : 创建并释放
    AcquisitionController --> IValueSource : 单个后台读取任务
    AcquisitionController --> ISampleChannel
    IValueSource <|.. DemoValueSource
    IValueSource <|.. JLinkValueSource
    JLinkValueSource "1" *-- "1" IJLinkSession
    IJLinkSession <|.. JLinkWorkerSession
    IJLinkSession <|.. JLinkSession
    JLinkWorkerSession ..> JLinkWorker : 进程间协议
    JLinkWorker "1" *-- "1" JLinkSession
    JLinkSession --> JLinkNative
```

`JLinkWorkerSession` 在主进程实现 `IJLinkSession`；worker 内的 `JLinkSession` 直接访问 DLL。接口相同，运行位置不同。

## 4. 启动与工作区恢复

```mermaid
flowchart TD
    A["Program.Main(args)"] --> B{"worker 参数？"}
    B -->|是| C["JLinkWorker.Run<br/>读取命令并监控父进程"]
    B -->|否| D{"取得单实例锁？"}
    D -->|否| E["激活已有窗口或提示已运行<br/>当前进程退出"]
    D -->|是| F["初始化日志、异常和原生崩溃处理"]
    F --> G["启动 Avalonia<br/>读取设置、主题与语言"]
    G --> H["创建 MainViewModel"]
    H --> I["加载用户符号缓存<br/>不存在则使用随附示例"]
    I --> J["恢复通道、可见性、窗格<br/>无可恢复通道时选择默认变量"]
    J --> K["创建窗口并恢复位置 / 最大化"]
    K --> L{"--demo？"}
    L -->|是| M["打开 Demo 源并开始合成采集"]
    L -->|否| N["等待用户导入或连接"]
    M --> O["UI 定时刷新与 watchdog 心跳"]
    N --> O
```

`AppSettingsStore` 对加载值进行规范化。工作区保存通道名称和可空地址；旧配置没有地址时按名称恢复，新配置优先按名称+地址恢复。固件重编译导致地址变化时，仅在名称唯一的情况下回退匹配，避免混淆多个同名静态变量。

## 5. 符号导入业务

```mermaid
sequenceDiagram
    actor U as 用户
    participant V as MainWindow
    participant VM as MainViewModel
    participant ELF as ElfSymbolImporter
    participant Store as AppSettingsStore
    participant AF as AtomicFile
    U->>V: 选择 AXF/ELF
    V->>VM: ImportSymbolsAsync(path)
    VM->>VM: 等待连接操作互斥门
    alt 已连接或正在关闭
        VM-->>V: 拒绝导入，保留原工作区
    else 可导入
        VM->>ELF: 后台解析文件
        ELF->>ELF: 校验 ELF 节表，获取可写已分配范围
        ELF->>ELF: 解析 DWARF 单元、缩写表、类型与位置
        ELF->>ELF: 展开支持的叶子变量，校验完整地址范围
        ELF-->>VM: SymbolCatalog
        VM->>Store: SaveSymbolCatalog(catalog, path)
        Store->>AF: 同目录临时文件、刷盘、替换
        AF-->>Store: 完整符号缓存文件
        VM->>Store: 更新 SymbolCatalogPath 并保存设置
        VM->>VM: 替换通道与变量集合、恢复选择
        VM->>VM: 释放旧树节点订阅，重建筛选树
        VM-->>V: 导入结果 / 错误
    end
```

解析约束：

- 保留常见整数、浮点、布尔和枚举叶子，大小为 1/2/4/8 字节。
- 跳过无法求得绝对地址的位置、指针、位域和不支持的复杂类型。
- 小型标量数组最多展开 16 个元素；字符/字节字符串数组等按当前实现过滤。
- 按 `(Name, Address)` 去重，相同名称但不同地址不会静默丢弃。
- 变量的整个值必须落入某个可写分配范围；不是仅检查起始地址。
- 导入是解析，不会烧录固件；工具链优化可能使变量无可采集位置。

源码入口：`Services/ElfSymbolImporter.cs`，内部包含 ELF 读取、DWARF 读取和符号收集器。

## 6. 连接、读块规划与异常恢复

### 6.1 连接时序

```mermaid
sequenceDiagram
    actor U as 用户
    participant VM as MainViewModel
    participant Source as JLinkValueSource
    participant Client as JLinkWorkerSession
    participant Worker as JLinkWorker进程
    participant Native as JLinkSession和DLL
    participant AC as AcquisitionController
    U->>VM: 连接
    VM->>VM: 连接互斥门、地址检查
    VM->>Source: 后台 Open()
    Source->>Client: Open(device, speed, resume=true)
    Client->>Worker: 启动同一 exe 的 worker 模式
    Client->>Worker: Open 命令
    Worker->>Native: 打开探针、选择 SWD 和设备、连接
    Native->>Native: 获取速度并请求固件运行
    Native-->>Worker: 设备描述与实际/最大速度
    Worker-->>Client: 状态、描述、速度数据
    Client-->>Source: 已连接
    Source-->>VM: Open 完成
    VM->>AC: Reset() 清空旧会话并重置时钟
    alt 已选择通道
        VM->>AC: Start(source)
    else 无通道
        VM-->>U: 等待选择变量
    end
```

连接入口允许一次额外重试，间隔 500 ms。连接、断开、导入共享协调门，防止 F5 与连接按钮同时创建两个源。关闭过程中完成的打开操作会释放源，不再启动采集。

### 6.2 内存读块

`JLinkValueSource` 在通道数组引用或数量变化时重建计划：

1. 按目标地址排序。
2. 将相邻变量合并到读块，块长最多 512 字节，允许最多 64 字节间隙。
3. 每个块一次读取，随后按偏移解码各变量。
4. 解码使用小端规则，并转换为 `double`；8 字节整数超过 `double` 精确整数范围时会有精度限制。

读取失败时，源关闭会话并尝试重新打开，默认最多 2 次恢复尝试；恢复成功后重建读计划并再次读取。最终失败则交给采集控制器停止任务并保存错误。

### 6.3 worker 协议和超时

| 命令 | 编号 | 请求字段 | 正常响应载荷 |
|---|---:|---|---|
| Open | 1 | 设备字符串、速度 Int32、恢复运行 Boolean | 实际速度和最大速度两个 Int32 |
| Read | 2 | 地址 UInt32、长度 Int32 | 指定长度的内存字节 |
| Close | 3 | 无 | 空 |
| Ping | 4 | 无 | 空 |

响应统一为：`状态 Int32 + 消息字符串 + 载荷长度 Int32 + 载荷字节`，由 .NET `BinaryReader/BinaryWriter` 编解码；载荷上限 4 MiB。

- Open：20 秒；Read：4 秒；Close：2 秒。
- 同一会话串行发送请求，后台响应线程完成对应的 `TaskCompletionSource`。
- worker generation 防止已淘汰进程的响应完成新请求。
- 超时/通信异常停止 worker，必要时终止进程树；后续恢复创建新进程。
- worker 每秒检查父进程，父进程退出后 worker 自行退出。

4 秒是单次 Read 响应超时，不是包含恢复打开和进程清理在内的整个操作上限。

## 7. 采样帧、清空与状态生命周期

### 7.1 采样流程与同步点

```mermaid
flowchart TD
    A["启动一个后台采集任务<br/>捕获本次 CTS 与源"] --> B{"取消请求？"}
    B -->|是| Z["保存错误结果，清理 CTS<br/>完成本次任务"]
    B -->|否| C["数据门内取得<br/>通道数组、dataVersion、采样时间"]
    C --> D{"有通道？"}
    D -->|否| E["可取消等待 40 ms"] --> B
    D -->|是| F["门外调用 source.Read"]
    F --> G{"读取成功且值数量正确？"}
    G -->|否| Z
    G -->|是| H["重新进入数据门"]
    H --> I{"版本相同且未取消？"}
    I -->|是| J["同一时间戳写入各通道<br/>更新帧数与速率"]
    I -->|否| K["丢弃过期帧"]
    J --> L["释放数据门，按请求周期安排下次读取"]
    K --> L
    L --> B
```

```mermaid
sequenceDiagram
    participant Sampler as 采集任务
    participant AC as AcquisitionController
    participant Source as IValueSource
    participant UI as UI线程
    Sampler->>AC: 门内取得版本 V 和时间 T
    Sampler->>Source: 门外 Read(channels)
    UI->>AC: Reset()
    AC->>AC: dataVersion 增加、清空缓存、重置时钟
    AC-->>UI: 重置完成
    Source-->>Sampler: 旧读取结果
    Sampler->>AC: 尝试提交版本 V
    AC->>AC: 发现版本变化，丢弃旧帧
    Sampler->>AC: 重新获取版本和时间
```

核心不变量：

- 读探针不持有数据门，因此原生读取等待不会阻止清空或获取已完成帧的快照。
- 同一帧的所有通道写入、重置、快照在同一数据门下互斥。
- 重连同时重置缓存和时钟；清空和通道变更增加版本号，阻止过期读取写入新状态。
- `StopAsync` 取消并等待任务，不提前释放还在使用的 CTS 或源。
- 任务结束清理状态后可以重新 `Start`；UI 只处理当前 runId 的完成通知。
- `GetMetrics` 是非阻塞的显示用遥测，避免 UI 等待大快照，不替代帧快照的一致性保障。

### 7.2 业务状态

```mermaid
stateDiagram-v2
    state "断开" as Offline
    state "连接中" as Connecting
    state "已连接，未采集" as Ready
    state "采集中" as Running
    state "采集出错，任务已结束" as Faulted
    state "断开处理中" as Disconnecting
    state "关闭处理中" as Closing
    [*] --> Offline
    Offline --> Connecting: 连接 / F5
    Connecting --> Offline: 打开失败
    Connecting --> Ready: 无通道
    Connecting --> Running: 已有通道
    Ready --> Running: 开始
    Running --> Ready: 暂停
    Running --> Faulted: 不可恢复读取错误
    Faulted --> Running: 再次开始并成功读取
    Ready --> Disconnecting: 断开
    Running --> Disconnecting: 断开
    Faulted --> Disconnecting: 重试 / 断开
    Disconnecting --> Offline: 释放源完成
    Offline --> Closing: 关闭窗口
    Ready --> Closing: 关闭窗口
    Running --> Closing: 关闭窗口
    Faulted --> Closing: 关闭窗口
    Closing --> [*]: 取消导出、停止采集、释放资源
```

“采集错误”状态仍可能保留连接对象，不能仅用 `IsConnected` 判断采集任务是否存在。

## 8. 缓存与波形业务

### 8.1 数据结构与预算

- 环形数组按需增长，最大容量由采样率、60 秒目标历史及通道数计算；单通道默认容量上限 200,000 点。
- 样本预算 512 MiB，估算中包含每点两个 `double` 及分块摘要摊销；这是缓存预算，不包含整个 UI、运行时、原生绘图和导出副本。
- 每 64 个物理槽维护有限值数量、和、最小/最大值及其索引、最后有限值。更新仅重算相应小块，范围查询组合整块和边缘数据。
- `ToArray` 复制环形缓冲的两个连续物理区段，保持逻辑时间顺序，避免逐点取模。
- 可见区间用二分定位边界；最近点直接查找原始采样，不使用已降采样的近似点。

### 8.2 绘图流程

```mermaid
flowchart TD
    A["UI 定时刷新或用户输入"] --> B["读取可见通道、检查可绘制尺寸"]
    B --> C["自动跟随最新时间<br/>或使用手动时间视图"]
    C --> D{"手动 Y 轴？"}
    D -->|是| E["使用手动 Y 范围"]
    D -->|否| F["分块摘要查询可见极值<br/>AxisRangePolicy 生成范围"]
    E --> G["按时间桶复制 min/max 代表点"]
    F --> G
    G --> H["过滤非有限点，断开无效段<br/>映射到有限屏幕坐标"]
    H --> I["屏幕空间线性简化<br/>最大 0.25 逻辑像素误差"]
    I --> J["逐通道构建独立不可变路径"]
    J --> K["绘制网格、曲线、刻度与图例"]
    K --> L{"鼠标在绘图区？"}
    L -->|是| M["二分取得原始最近点<br/>绘制十字光标及数值"]
    L -->|否| N["完成"]
    M --> N
```

`CopyVisible` 在点数预算足够时保留端点及各时间桶最小/最大值，避免固定步长漏掉尖峰。屏幕简化只影响提交给绘图后端的几何，不修改原始缓存、统计、CSV 或光标读数。

`AxisRangePolicy` 对新极值立即扩展，持续较窄的范围达到约 2 秒后再收缩，减小自动 Y 轴抖动。极端有限数值使用有限端点和归一化坐标计算，避免溢出为原生绘图中的非有限坐标。

资源与交互约束：

- 单窗格点预算基于宽度和 64,000 点帧预算计算；高通道下减少每通道点数。
- 常规 UI 定时器 16 ms；通道数大于 32 时图表刷新间隔至少 66 ms，大于 128 时至少 200 ms。实际帧耗时仍受数据、窗口尺寸和机器影响。
- 可见通道统计每 250/500 ms 更新；隐藏通道统计每 2 秒更新，隐藏不停止采样。
- 时间滚轮视图范围 0.01–3600 秒；零垂直增量不缩放；丢失鼠标捕获时退出拖拽。
- 文字测量缓存有固定容量，暂存数组复用；图表离开视觉树时解除语言和集合订阅。
- **路径发布给渲染线程后不得重新打开或修改。** 采用每帧新建的不可变路径，避免 UI 线程与原生渲染线程同时访问可变对象。

## 9. CSV 导出与文件一致性

```mermaid
sequenceDiagram
    actor U as 用户
    participant VM as MainViewModel
    participant AC as AcquisitionController
    participant Buf as 各通道缓存
    participant CSV as CsvExporter
    participant AF as AtomicFile
    U->>VM: 选择 CSV 路径
    VM->>AC: 后台 CaptureSnapshot(token)
    AC->>AC: 进入数据门，暂停帧提交
    loop 每个通道
        AC->>Buf: ToArray()
        Buf-->>AC: 独立样本数组
    end
    AC-->>VM: 退出数据门并返回快照
    Note over AC,Buf: 后续采样可以继续覆盖环形缓存
    VM->>CSV: ExportSnapshotAsync(path, snapshot)
    CSV->>AF: 在目标目录创建唯一临时文件
    CSV->>CSV: 合并全部通道时间线，写 UTF-8 行
    CSV-->>VM: 异步进度
    alt 用户取消或写入失败
        VM->>CSV: 取消令牌
        CSV->>AF: 关闭并删除临时文件
        Note over AF,U: 原目标文件保持不变
    else 全部完成
        CSV->>AF: 刷盘并再次检查取消
        AF->>AF: 关闭文件后原子替换目标
        CSV-->>VM: 导出成功
    end
```

CSV 采用时间线并集，避免只选最长通道而漏掉其他通道的有效行。数值用区域无关 `G17`，时间用 6 位小数；缺值、NaN 和 Infinity 输出空单元格。重复通道名称用地址补充列名。进度和取消按批次检查，快照会临时增加接近一份历史数据的内存。

`AtomicFile` 也用于设置和导入符号缓存：同目录唯一临时文件、完整序列化/写入、刷盘、关闭后替换；失败清理临时文件。设置保存失败返回 `false` 并记录日志。

## 10. 诊断、退出与配置

| 组件 | 机制 |
|---|---|
| `AppLog` | 带时间、线程、进程和 session；5 MB 轮转；保留历史 |
| `DiagnosticsService` | 托管/原生异常摘要、Windows minidump、诊断 ZIP |
| `UiWatchdog` | 独立线程检查 UI 心跳，10 秒静默触发 hang 现场，恢复后重新允许报告 |
| `WindowPlacementPolicy` | 按屏幕工作区夹取已保存窗口位置 |
| `AppSettingsStore` | LocalAppData 设置、符号缓存与原子保存 |

关闭顺序：标记关闭 → 取消并等待导出 → 保存工作区 → 等待正在进行的连接/断开操作 → 停止采集 → 释放源和 worker → 解除订阅 → 允许窗口关闭。正常结束写入 clean-exit 标记。

诊断 ZIP 收集当前/历史日志、配置、系统与窗口信息、dump 目录最近文件。日志和 dump 可能含用户路径、符号和内存片段，问题反馈应由提供者确认可共享内容。

## 11. 验证和交付

- 回归测试：`tests/NewScope.Tests`，ELF 样本由测试代码生成，无本机固件路径依赖。
- 压力工具：`tools/NewScope.Stress`，独立执行满缓存绘图和真实窗口采集；反射调整测试时钟仅用于预填充场景，不是生产接口。
- 最终压测：[报告](STRESS_TEST_2026-10-26.md) 与 [原始数据](testing/2026-10-26)。
- 构建/安装/标签发布：[构建与发布](BUILD_AND_RELEASE.md)。
- 源码在 Git，安装包与免安装版在 GitHub Release；标签指向对应提交，SHA256 用于核对下载产物。

已验证负载不代表硬实时保证或所有硬件组合。J-Link 真实吞吐、掉电/拔线、多显示器、低内存及更长时间运行仍需按实际部署环境验证。
