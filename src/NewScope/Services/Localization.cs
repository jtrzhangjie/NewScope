using System.Globalization;
using Avalonia;

namespace NewScope.Services;

public static class Localization
{
    private static readonly Dictionary<string, string> Chinese = new()
    {
        ["AppTitle"] = "NewScope — J-Link 变量示波器",
        ["AppSubtitle"] = "J-LINK 变量示波器",
        ["Connect"] = "连接",
        ["Disconnect"] = "断开",
        ["RunStart"] = "开始",
        ["RunPause"] = "暂停",
        ["RatePerChannel"] = "采样 / 通道",
        ["RateTooltip"] = "每个通道每秒请求的采样数。实际采样率显示在右下角，受 SWD 速度、变量数量和目标内存影响。",
        ["RateComboTooltip"] = "每通道请求采样率；ACTUAL 为实测值。",
        ["Window"] = "时间窗",
        ["Follow"] = "跟随",
        ["Fit"] = "适配",
        ["Clear"] = "清除",
        ["ExportCsv"] = "导出 CSV",
        ["Diagnostics"] = "诊断",
        ["DiagnosticsTooltip"] = "导出日志、配置、显示器和最近崩溃转储",
        ["DiagnosticsDialogTitle"] = "保存 NewScope 诊断包",
        ["DiagnosticsFileType"] = "ZIP 压缩包",
        ["DiagnosticsSaved"] = "诊断包已保存：{0}",
        ["DiagnosticsFailed"] = "诊断包导出失败：{0}",
        ["Retry"] = "重试",
        ["RetryConnectionTooltip"] = "重新尝试连接目标",
        ["StatusRetryingConnection"] = "J-Link 无响应，正在重试…",
        ["ErrorAddressOutsideRam"] = "所选变量不在符号文件的 RAM 范围（{0}–{1}）内：{2}",
        ["Panes"] = "窗格",
        ["PaneCountTooltip"] = "上下堆叠的波形窗格数量",
        ["Target"] = "目标",
        ["TargetPaneTooltip"] = "新变量加入此窗格",
        ["SwdTooltip"] = "J-Link SWD 时钟；连接时按探针能力自动校准，重新连接后生效",
        ["JLinkDeviceLabel"] = "J-Link 设备",
        ["JLinkDeviceHint"] = "使用 J-Link 支持的设备名，例如 Cortex-M0。修改后重新连接生效。",
        ["JLinkDeviceTooltip"] = "查看或修改 J-Link 设备名",
        ["MemoryGuardText"] = "内存上限 {0} MB · 保留 {1:0} s",
        ["MemoryGuardTooltip"] = "通道数和采样率较高时，NewScope 自动限制每通道缓存，以保护内存。",
        ["ThemeTooltip"] = "切换亮色 / 暗色主题",
        ["LightTheme"] = "亮色",
        ["DarkTheme"] = "暗色",
        ["LanguageLabel"] = "EN",
        ["LanguageTooltip"] = "切换到英文界面",
        ["Dismiss"] = "关闭",
        ["Variables"] = "变量",
        ["Import"] = "导入",
        ["ImportTooltip"] = "导入 AXF/ELF DWARF 符号；请先断开连接。",
        ["SearchPlaceholder"] = "搜索名称、类型或地址",
        ["SelectedOnly"] = "仅显示已选",
        ["All"] = "全选",
        ["None"] = "清空",
        ["SelectAllTooltip"] = "选择当前可见的全部变量",
        ["GroupSelectTooltip"] = "选择或清除此组内的全部变量",
        ["NoSymbolFile"] = "未加载符号文件",
        ["VariablesCount"] = "{0:N0} 个变量",
        ["ChannelsCountOne"] = "{0} 个通道",
        ["ChannelsCountMany"] = "{0} 个通道",
        ["Waveform"] = "波形",
        ["RelativeTime"] = "相对时间",
        ["WaveHint"] = "滚轮缩放时间 · 拖动平移 · 滚轮缩放 Y 轴 · 双击 Y 轴恢复自动",
        ["VisibleSpanTooltip"] = "可见时间范围",
        ["YModeTooltip"] = "滚轮在 Y 轴区缩放；双击 Y 轴恢复自动范围",
        ["YAuto"] = "Y 自动",
        ["YManual"] = "Y 手动",
        ["PaneA"] = "窗格 A",
        ["PaneB"] = "窗格 B",
        ["PaneC"] = "窗格 C",
        ["PaneD"] = "窗格 D",
        ["PlotEmpty"] = "从左侧选择变量开始绘图",
        ["PlotWaiting"] = "等待采样…",
        ["PlotNoSamples"] = "当前视图内无采样点",
        ["TimeAxis"] = "时间 (s)",
        ["ValueAxis"] = "数值",
        ["Watch"] = "监视",
        ["WatchOn"] = "显示",
        ["WatchSignal"] = "信号",
        ["WatchCurrent"] = "当前",
        ["WatchMin"] = "最小",
        ["WatchMax"] = "最大",
        ["WatchAvg"] = "平均",
        ["WatchPaneTooltip"] = "窗格",
        ["ShowHideTooltip"] = "显示或隐藏此通道",
        ["MovePaneTooltip"] = "将此通道移到其他窗格",
        ["WatchHint"] = "悬停波形查看采样值",
        ["LiveMemory"] = "J-LINK · 实时内存",
        ["Samples"] = "采样",
        ["TimeShort"] = "T",
        ["Rate"] = "速率",
        ["NoProbe"] = "未连接探针",
        ["ConnectionFailed"] = "连接失败",
        ["ReleasingJLink"] = "正在释放 J-Link…",
        ["DemoStopped"] = "演示已停止",
        ["JLinkReleased"] = "J-Link 已释放",
        ["StatusReady"] = "就绪",
        ["StatusStartDemo"] = "正在启动演示源…",
        ["StatusOpeningJLink"] = "正在打开 J-Link…",
        ["StatusDemoReady"] = "演示源已就绪",
        ["StatusConnected"] = "已连接 · 目标运行中",
        ["StatusSelectVariable"] = "请至少选择一个变量",
        ["StatusPaused"] = "已暂停",
        ["StatusDisconnected"] = "已断开",
        ["StatusDisconnecting"] = "正在断开…",
        ["StatusDataCleared"] = "数据已清除",
        ["StatusError"] = "错误",
        ["StatusAcquisitionError"] = "采集错误",
        ["StatusReadingSymbols"] = "正在读取调试符号…",
        ["StatusSymbolImportFailed"] = "符号导入失败",
        ["StatusImportedVariables"] = "已导入 {0:N0} 个变量",
        ["StatusReadyToSample"] = "可以开始采样",
        ["StatusDemoRunning"] = "演示运行中",
        ["StatusLiveSampling"] = "实时采样中",
        ["ErrorAcquisitionStopped"] = "采集已停止：{0}",
        ["ErrorDisconnectBeforeImport"] = "导入符号前请先断开目标连接。",
        ["ErrorValueSourceCount"] = "值源返回了意外的采样数量。",
        ["ErrorSymbols"] = "符号：{0}",
        ["ErrorImportFailed"] = "符号导入失败：{0}",
        ["ErrorNoSamplesToExport"] = "没有可导出的采样",
        ["Exported"] = "已导出 {0}",
        ["Exporting"] = "正在导出… {0:0}%",
        ["ExportCancelled"] = "导出已取消",
        ["Cancel"] = "取消",
        ["ErrorExportFailed"] = "导出失败：{0}",
        ["BadgeOffline"] = "离线",
        ["BadgeDemo"] = "演示",
        ["BadgeLive"] = "实时",
        ["BadgePaused"] = "暂停",
        ["BadgeError"] = "错误",
        ["ImportDialogTitle"] = "导入 AXF / ELF 调试符号",
        ["ExportDialogTitle"] = "导出 NewScope 采样",
        ["CsvFileType"] = "CSV 文件",
        ["DemoDescription"] = "演示 · 合成信号"
    };

    private static readonly Dictionary<string, string> English = new()
    {
        ["AppTitle"] = "NewScope — J-Link Variable Scope",
        ["AppSubtitle"] = "J-LINK VARIABLE SCOPE",
        ["Connect"] = "Connect",
        ["Disconnect"] = "Disconnect",
        ["RunStart"] = "Start",
        ["RunPause"] = "Pause",
        ["RatePerChannel"] = "RATE / CH",
        ["RateTooltip"] = "Requested samples per second for each channel. The real rate is shown at the bottom right and depends on SWD speed, symbol count and target memory.",
        ["RateComboTooltip"] = "Requested per-channel sample rate. ACTUAL is the measured rate.",
        ["Window"] = "WINDOW",
        ["Follow"] = "Follow",
        ["Fit"] = "Fit",
        ["Clear"] = "Clear",
        ["ExportCsv"] = "Export CSV",
        ["Diagnostics"] = "Diag",
        ["DiagnosticsTooltip"] = "Export logs, settings, display details and the latest crash dump",
        ["DiagnosticsDialogTitle"] = "Save NewScope diagnostics",
        ["DiagnosticsFileType"] = "ZIP archive",
        ["DiagnosticsSaved"] = "Diagnostics saved: {0}",
        ["DiagnosticsFailed"] = "Diagnostics export failed: {0}",
        ["Retry"] = "Retry",
        ["RetryConnectionTooltip"] = "Try connecting to the target again",
        ["StatusRetryingConnection"] = "J-Link did not respond; retrying…",
        ["ErrorAddressOutsideRam"] = "Selected variables are outside the symbol file RAM range ({0}–{1}): {2}",
        ["Panes"] = "PANES",
        ["PaneCountTooltip"] = "Number of stacked charts",
        ["Target"] = "TARGET",
        ["TargetPaneTooltip"] = "New variables are assigned to this pane",
        ["SwdTooltip"] = "J-Link SWD clock; calibrated to probe capability on connect, reconnect to apply",
        ["JLinkDeviceLabel"] = "J-LINK DEVICE",
        ["JLinkDeviceHint"] = "Use a device name supported by J-Link, for example Cortex-M0. Reconnect to apply changes.",
        ["JLinkDeviceTooltip"] = "View or change the J-Link device name",
        ["MemoryGuardText"] = "MEMORY CAP {0} MB · {1:0} s RETAINED",
        ["MemoryGuardTooltip"] = "When channel count and sample rate are high, NewScope limits each channel buffer to protect memory.",
        ["ThemeTooltip"] = "Toggle light / dark theme",
        ["LightTheme"] = "Light",
        ["DarkTheme"] = "Dark",
        ["LanguageLabel"] = "中",
        ["LanguageTooltip"] = "Switch to Chinese",
        ["Dismiss"] = "Dismiss",
        ["Variables"] = "VARIABLES",
        ["Import"] = "Import",
        ["ImportTooltip"] = "Import AXF/ELF DWARF symbols. Disconnect first.",
        ["SearchPlaceholder"] = "Search name, type or address",
        ["SelectedOnly"] = "Selected only",
        ["All"] = "All",
        ["None"] = "None",
        ["SelectAllTooltip"] = "Select all visible variables",
        ["NoSymbolFile"] = "No symbol file",
        ["VariablesCount"] = "{0:N0} variables",
        ["ChannelsCountOne"] = "{0} channel",
        ["ChannelsCountMany"] = "{0} channels",
        ["Waveform"] = "WAVEFORM",
        ["RelativeTime"] = "RELATIVE TIME",
        ["WaveHint"] = "Scroll to zoom · Drag to pan · Wheel over Y axis to scale · Double-click Y axis to auto",
        ["VisibleSpanTooltip"] = "Visible time span",
        ["YModeTooltip"] = "Wheel over the Y axis to scale manually; double-click it to return to auto",
        ["YAuto"] = "Y AUTO",
        ["YManual"] = "Y MANUAL",
        ["PaneA"] = "PANE A",
        ["PaneB"] = "PANE B",
        ["PaneC"] = "PANE C",
        ["PaneD"] = "PANE D",
        ["PlotEmpty"] = "Select variables from the left to start plotting",
        ["PlotWaiting"] = "Waiting for samples...",
        ["PlotNoSamples"] = "No samples in view",
        ["TimeAxis"] = "TIME (s)",
        ["ValueAxis"] = "VALUE",
        ["Watch"] = "WATCH",
        ["WatchOn"] = "ON",
        ["WatchSignal"] = "SIGNAL",
        ["WatchCurrent"] = "CUR",
        ["WatchMin"] = "MIN",
        ["WatchMax"] = "MAX",
        ["WatchAvg"] = "AVG",
        ["WatchPaneTooltip"] = "Pane",
        ["ShowHideTooltip"] = "Show or hide this channel",
        ["MovePaneTooltip"] = "Move this channel to the other pane",
        ["WatchHint"] = "Hover the plot to inspect samples",
        ["LiveMemory"] = "J-LINK · LIVE MEMORY",
        ["Samples"] = "SAMPLES",
        ["TimeShort"] = "T",
        ["Rate"] = "RATE",
        ["NoProbe"] = "No probe attached",
        ["ConnectionFailed"] = "Connection failed",
        ["ReleasingJLink"] = "Releasing J-Link...",
        ["DemoStopped"] = "Demo stopped",
        ["JLinkReleased"] = "J-Link released",
        ["StatusReady"] = "Ready",
        ["StatusStartDemo"] = "Starting demo source...",
        ["StatusOpeningJLink"] = "Opening J-Link...",
        ["StatusDemoReady"] = "Demo source ready",
        ["StatusConnected"] = "Connected · target running",
        ["StatusSelectVariable"] = "Select at least one variable",
        ["StatusPaused"] = "Paused",
        ["StatusDisconnected"] = "Disconnected",
        ["StatusDisconnecting"] = "Disconnecting...",
        ["StatusDataCleared"] = "Data cleared",
        ["StatusError"] = "Error",
        ["StatusAcquisitionError"] = "Acquisition error",
        ["StatusReadingSymbols"] = "Reading debug symbols...",
        ["StatusSymbolImportFailed"] = "Symbol import failed",
        ["StatusImportedVariables"] = "Imported {0:N0} variables",
        ["StatusReadyToSample"] = "Ready to sample",
        ["StatusDemoRunning"] = "Demo running",
        ["StatusLiveSampling"] = "Live sampling",
        ["ErrorAcquisitionStopped"] = "Acquisition stopped: {0}",
        ["ErrorDisconnectBeforeImport"] = "Disconnect from the target before importing a symbol file.",
        ["ErrorValueSourceCount"] = "Value source returned an unexpected number of samples.",
        ["ErrorSymbols"] = "Symbols: {0}",
        ["ErrorImportFailed"] = "Symbol import failed: {0}",
        ["ErrorNoSamplesToExport"] = "No samples to export",
        ["Exported"] = "Exported {0}",
        ["Exporting"] = "Exporting… {0:0}%",
        ["ExportCancelled"] = "Export cancelled",
        ["Cancel"] = "Cancel",
        ["ErrorExportFailed"] = "Export failed: {0}",
        ["BadgeOffline"] = "OFFLINE",
        ["BadgeDemo"] = "DEMO",
        ["BadgeLive"] = "LIVE",
        ["BadgePaused"] = "PAUSED",
        ["BadgeError"] = "ERROR",
        ["ImportDialogTitle"] = "Import AXF / ELF debug symbols",
        ["ExportDialogTitle"] = "Export NewScope samples",
        ["CsvFileType"] = "CSV file",
        ["DemoDescription"] = "DEMO · synthesized signals"
    };

    private static string _language = "zh";

    public static event EventHandler? LanguageChanged;

    public static string Language => _language;
    public static bool IsEnglish => _language == "en";

    public static void Initialize()
    {
        SetLanguage(AppSettingsStore.Current.Language, persist: false);
    }

    public static void SetLanguage(string? language, bool persist = true)
    {
        _language = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
        ApplyResources();
        if (persist)
        {
            AppSettingsStore.Current.Language = _language;
            AppSettingsStore.Save();
        }

        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string Get(string key)
    {
        var catalog = IsEnglish ? English : Chinese;
        return catalog.TryGetValue(key, out var value) ? value : key;
    }

    public static string Format(string key, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

    public static string ChannelCount(int count) =>
        Format(count == 1 ? "ChannelsCountOne" : "ChannelsCountMany", count);

    private static void ApplyResources()
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        foreach (var (key, value) in IsEnglish ? English : Chinese)
        {
            application.Resources[key] = value;
        }
    }
}
