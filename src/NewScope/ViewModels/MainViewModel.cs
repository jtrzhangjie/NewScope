using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NewScope.Models;
using NewScope.Services;

namespace NewScope.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private static readonly IBrush IdleBrush = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IBrush LiveBrush = new SolidColorBrush(Color.Parse("#4DD0E1"));
    private static readonly IBrush RunningBrush = new SolidColorBrush(Color.Parse("#7ED957"));
    private static readonly IBrush DemoBrush = new SolidColorBrush(Color.Parse("#FFCA28"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#FF5C5C"));
    private static readonly int[] CommonJLinkSpeeds =
    [
        1000,
        2000,
        4000,
        5000,
        10000,
        15000,
        20000,
        25000,
        30000,
        40000,
        50000
    ];

    private readonly bool _demo;
    private readonly AppSettings _settings;
    private readonly Func<bool> _saveSettings;
    private readonly Func<SymbolCatalog> _loadCatalog;
    private readonly Func<string, int, IValueSource> _createSource;
    private readonly Dictionary<string, ChannelViewModel> _channelsByIdentity = [];
    private readonly Dictionary<string, SymbolItemViewModel> _itemsByIdentity = [];
    private readonly AcquisitionController _acquisition = new();
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private IValueSource? _source;
    private CancellationTokenSource? _exportCts;
    private Task? _exportTask;
    private readonly Stopwatch _statsClock = Stopwatch.StartNew();
    private TimeSpan _lastStatsUpdate;
    private TimeSpan _lastHiddenStatsUpdate;
    private int _paletteIndex;
    private bool _shuttingDown;
    private string _statusKey = "StatusReady";
    private object[] _statusArguments = [];
    private string? _connectionKey = "NoProbe";
    private object[] _connectionArguments = [];
    private int _variableCount;
    private bool _hasSymbolFile;
    private uint _ramStart;
    private uint _ramSize;

    public MainViewModel() : this(false)
    {
    }

    public MainViewModel(bool demo) : this(
        demo, AppSettingsStore.Current, () => SymbolCatalogLoader.Load(),
        (device, speed) => demo ? new DemoValueSource() : new JLinkValueSource(device, speed),
        AppSettingsStore.Save)
    {
    }

    internal MainViewModel(
        bool demo,
        AppSettings settings,
        Func<SymbolCatalog> loadCatalog,
        Func<string, int, IValueSource> createSource,
        Func<bool>? saveSettings = null)
    {
        _demo = demo;
        _settings = settings;
        _saveSettings = saveSettings ?? (() => true);
        _loadCatalog = loadCatalog;
        _createSource = createSource;
        Localization.LanguageChanged += OnLanguageChanged;
        SampleRates =
        [
            new RateOption(10),
            new RateOption(20),
            new RateOption(50),
            new RateOption(100),
            new RateOption(200),
            new RateOption(500),
            new RateOption(1000),
            new RateOption(2000)
        ];
        WindowOptions =
        [
            new WindowOption(1),
            new WindowOption(2),
            new WindowOption(5),
            new WindowOption(10),
            new WindowOption(20),
            new WindowOption(30),
            new WindowOption(60)
        ];
        JLinkSpeedOptions = CreateSpeedOptions(50_000);

        _selectedSampleRate = SampleRates.First(option => option.Hz == 100);
        _acquisition.SampleRateHz = _selectedSampleRate.Hz;
        _selectedWindow = WindowOptions.First(option => Math.Abs(option.Seconds - 5) < 0.01);
        _selectedSpeed = JLinkSpeedOptions.First(option => option.KHz == 5000);
        _selectedPaneCount = PaneCountOptions[0];
        _selectedPane = PaneOptions[0];
        _isLightTheme = string.Equals(_settings.Theme, "Light", StringComparison.OrdinalIgnoreCase);
        RebuildVisiblePaneOptions();
        ApplyWorkspaceSettings();

        LoadSymbols();
        SelectDefaultChannels();
        RefreshFilter();
        UpdateStatusVisuals();
    }

    public ObservableCollection<SymbolItemViewModel> AllVariables { get; } = [];
    public ObservableCollection<VariableNode> FilteredVariables { get; } = [];
    public ObservableCollection<ChannelViewModel> Channels { get; } = [];
    public ObservableCollection<ChannelViewModel> PaneAChannels { get; } = [];
    public ObservableCollection<ChannelViewModel> PaneBChannels { get; } = [];
    public ObservableCollection<ChannelViewModel> PaneCChannels { get; } = [];
    public ObservableCollection<ChannelViewModel> PaneDChannels { get; } = [];
    public IReadOnlyList<RateOption> SampleRates { get; }
    public IReadOnlyList<WindowOption> WindowOptions { get; }
    private ObservableCollection<SpeedOption> _jLinkSpeedOptions = [];
    public ObservableCollection<SpeedOption> JLinkSpeedOptions
    {
        get => _jLinkSpeedOptions;
        private set
        {
            if (ReferenceEquals(_jLinkSpeedOptions, value))
            {
                return;
            }

            _jLinkSpeedOptions = value;
            OnPropertyChanged();
        }
    }
    public IReadOnlyList<string> JLinkDeviceOptions { get; } =
        ["Cortex-M0", "Cortex-M0+", "Cortex-M3", "Cortex-M4", "Cortex-M7", "Cortex-M33"];
    public IReadOnlyList<PaneOption> PaneOptions { get; } = [new(0, "A"), new(1, "B"), new(2, "C"), new(3, "D")];
    public ObservableCollection<PaneOption> VisiblePaneOptions { get; } = [];
    public IReadOnlyList<PaneCountOption> PaneCountOptions { get; } = [new(1), new(2), new(3), new(4)];

    public bool IsDemo => _demo;
    public string VersionLabel =>
        $"v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";

    public int SpeedKHz => SelectedSpeed?.KHz ?? 5000;
    public bool ShowTwoPanes => PaneCount > 1;
    public bool ShowPaneC => PaneCount >= 3;
    public bool ShowPaneD => PaneCount >= 4;
    public double WindowSeconds => SelectedWindow?.Seconds ?? 5;
    public int SampleRateHz => SelectedSampleRate?.Hz ?? 100;
    public bool CanConnect => !IsConnected;
    public bool CanDisconnect => IsConnected;
    public bool CanToggleRun => IsConnected;
    public string RunButtonText => Localization.Get(IsRunning ? "RunPause" : "RunStart");
    public string LanguageLabel => Localization.Get("LanguageLabel");
    public IEnumerable<ChannelViewModel> PrimaryChannels => PaneCount <= 1 ? Channels : PaneAChannels;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _showSelectedOnly;

    [ObservableProperty]
    private RateOption? _selectedSampleRate;

    [ObservableProperty]
    private WindowOption? _selectedWindow;

    [ObservableProperty]
    private bool _autoFollow = true;

    [ObservableProperty]
    private int _paneCount = 1;

    [ObservableProperty]
    private PaneCountOption _selectedPaneCount = null!;

    [ObservableProperty]
    private SpeedOption _selectedSpeed = null!;

    [ObservableProperty]
    private PaneOption _selectedPane = null!;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = Localization.Get("StatusReady");

    [ObservableProperty]
    private string _connectionDetail = Localization.Get("NoProbe");

    [ObservableProperty]
    private string _jLinkDevice = "Cortex-M0";

    [ObservableProperty]
    private string _symbolFileText = Localization.Get("NoSymbolFile");

    [ObservableProperty]
    private string _symbolCountText = Localization.Format("VariablesCount", 0);

    [ObservableProperty]
    private string _selectedCountText = Localization.ChannelCount(0);

    [ObservableProperty]
    private string _sampleCountText = "0";

    [ObservableProperty]
    private string _elapsedText = "00:00.0";

    [ObservableProperty]
    private string _actualRateText = "0 Hz";

    [ObservableProperty]
    private string _connectionBadge = Localization.Get("BadgeOffline");

    [ObservableProperty]
    private IBrush _statusBrush = IdleBrush;

    [ObservableProperty]
    private bool _isImportingSymbols;

    [ObservableProperty]
    private bool _isLightTheme;

    [ObservableProperty]
    private bool _memoryGuardActive;

    [ObservableProperty]
    private string _memoryGuardText = "";

    public bool CanImportSymbols => !IsConnected && !IsImportingSymbols;
    public string ThemeLabel => Localization.Get(IsLightTheme ? "LightTheme" : "DarkTheme");

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _connectionRetryAvailable;

    [ObservableProperty]
    private bool _isExporting;

    [ObservableProperty]
    private double _exportProgress;

    [ObservableProperty]
    private string _exportStatusText = "";

    [ObservableProperty]
    private string _targetSummary = "Cortex-M0 · SWD 5 MHz";

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(CanToggleRun));
        OnPropertyChanged(nameof(CanImportSymbols));
    }

    partial void OnIsImportingSymbolsChanged(bool value)
    {
        OnPropertyChanged(nameof(CanImportSymbols));
    }

    partial void OnIsLightThemeChanged(bool value)
    {
        OnPropertyChanged(nameof(ThemeLabel));
        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = value ? ThemeVariant.Light : ThemeVariant.Dark;
        }

        _settings.Theme = value ? "Light" : "Dark";
        _saveSettings();
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(RunButtonText));
    }

    partial void OnSearchTextChanged(string value) => RefreshFilter();

    partial void OnShowSelectedOnlyChanged(bool value) => RefreshFilter();

    partial void OnSelectedWindowChanged(WindowOption? value)
    {
        OnPropertyChanged(nameof(WindowSeconds));
    }

    partial void OnPaneCountChanged(int value)
    {
        var count = Math.Clamp(value, 1, 4);
        if (count != value)
        {
            PaneCount = count;
            return;
        }

        foreach (var channel in Channels.Where(channel => channel.PaneIndex >= count).ToArray())
        {
            MoveChannelToPane(channel, 0);
        }

        var selectedCount = PaneCountOptions.FirstOrDefault(option => option.Count == count);
        if (selectedCount is not null && !ReferenceEquals(SelectedPaneCount, selectedCount))
        {
            SelectedPaneCount = selectedCount;
        }

        RebuildVisiblePaneOptions();
        OnPropertyChanged(nameof(ShowTwoPanes));
        OnPropertyChanged(nameof(ShowPaneC));
        OnPropertyChanged(nameof(ShowPaneD));
        OnPropertyChanged(nameof(PrimaryChannels));
    }

    partial void OnSelectedPaneCountChanged(PaneCountOption value)
    {
        if (value is not null && PaneCount != value.Count)
        {
            PaneCount = value.Count;
        }
    }

    partial void OnSelectedSampleRateChanged(RateOption? value)
    {
        OnPropertyChanged(nameof(SampleRateHz));
        _acquisition.SampleRateHz = value?.Hz ?? 100;
        ApplyBufferLimits();
    }

    partial void OnSelectedSpeedChanged(SpeedOption value)
    {
        OnPropertyChanged(nameof(SpeedKHz));
        UpdateTargetSummary();
    }

    partial void OnJLinkDeviceChanged(string value) => UpdateTargetSummary();

    [RelayCommand]
    private async Task ConnectAsync()
    {
        await _connectionGate.WaitAsync();
        IValueSource? source = null;
        try
        {
            if (IsConnected || _shuttingDown || IsImportingSymbols)
            {
                return;
            }

            ClearError();
            ValidateTargetAddresses();
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    source = _createSource(EffectiveJLinkDevice, SpeedKHz);

                    SetStatus(_demo ? "StatusStartDemo" : "StatusOpeningJLink");
                    await Task.Run(source.Open);
                    break;
                }
                catch (Exception) when (!_demo && attempt == 0)
                {
                    if (source is not null)
                    {
                        await Task.Run(source.Dispose);
                    }
                    source = null;
                    SetStatus("StatusRetryingConnection");
                    AppLog.Warn("J-Link open failed; retrying once after 500 ms");
                    await Task.Delay(500);
                }
            }

            if (_shuttingDown)
            {
                return;
            }

            _source = source;
            IsConnected = true;
            if (!_demo)
            {
                ApplyProbeSpeedLimit(source.ActualSpeedKHz, source.MaxSpeedKHz);
            }

            ConnectionRetryAvailable = false;
            ClearData();
            _paletteIndex = _channelsByIdentity.Count;
            _connectionKey = null;
            ConnectionDetail = source.Description;
            SetStatus(_demo ? "StatusDemoReady" : "StatusConnected");
            AppLog.Info($"Connected: {source.Description}");
            UpdateStatusVisuals();

            if (Channels.Count > 0)
            {
                StartAcquisition();
            }
            else
            {
                SetStatus("StatusSelectVariable");
            }
        }
        catch (Exception ex)
        {
            _source = null;
            IsConnected = false;
            ConnectionRetryAvailable = true;
            SetConnectionDetail("ConnectionFailed");
            SetError(ex.Message, ex);
            UpdateStatusVisuals();
        }
        finally
        {
            try
            {
                if (source is not null && !ReferenceEquals(source, _source))
                {
                    await Task.Run(source.Dispose);
                }
            }
            finally
            {
                _connectionGate.Release();
            }
        }
    }

    [RelayCommand]
    private async Task RetryConnectionAsync()
    {
        if (IsConnected)
        {
            await DisconnectAsync();
        }

        await ConnectAsync();
    }

    [RelayCommand]
    private async Task ToggleRunAsync()
    {
        if (!IsConnected)
        {
            await ConnectAsync();
            if (IsConnected && !IsRunning)
            {
                StartAcquisition();
            }

            return;
        }

        if (IsRunning)
        {
            await PauseAsync();
        }
        else
        {
            StartAcquisition();
        }
    }

    [RelayCommand]
    private async Task PauseAsync()
    {
        await _acquisition.StopAsync();
        IsRunning = false;
        SetStatus(IsConnected ? "StatusPaused" : "StatusDisconnected");
        UpdateStatusVisuals();
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await _connectionGate.WaitAsync();
        try
        {
            await PauseAsync();
            var source = _source;
            _source = null;
            IsConnected = false;
            SetConnectionDetail(source is null ? "NoProbe" : "ReleasingJLink");
            SetStatus("StatusDisconnecting");
            UpdateStatusVisuals();

            if (source is not null)
            {
                await Task.Run(source.Dispose);
            }

            if (!_demo)
            {
                ResetSpeedOptions();
            }

            SetConnectionDetail(_demo ? "DemoStopped" : "JLinkReleased");
            SetStatus("StatusDisconnected");
            AppLog.Info("Disconnected");
            UpdateStatusVisuals();
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    [RelayCommand]
    private void DismissError() => ClearError();

    [RelayCommand]
    private void ClearData()
    {
        _acquisition.Reset(startClock: IsConnected);
        foreach (var channel in Channels)
        {
            channel.UpdateStats();
        }

        SampleCountText = "0";
        ElapsedText = "00:00.0";
        ActualRateText = "0 Hz";
        SetStatus(IsConnected ? "StatusDataCleared" : "StatusReady");
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        foreach (var item in Flatten(FilteredVariables).ToArray())
        {
            SelectItem(item);
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var item in AllVariables.Where(item => item.IsSelected).ToArray())
        {
            DeselectItem(item);
        }
    }

    public void UpdateLiveStats()
    {
        var now = _statsClock.Elapsed;
        var visibleInterval = Channels.Count > 128
            ? TimeSpan.FromMilliseconds(500)
            : TimeSpan.FromMilliseconds(250);
        if (now - _lastStatsUpdate >= visibleInterval)
        {
            _lastStatsUpdate = now;
            foreach (var channel in Channels)
            {
                if (channel.IsVisible)
                {
                    channel.UpdateStats();
                }
            }
        }

        if (now - _lastHiddenStatsUpdate >= TimeSpan.FromSeconds(2))
        {
            _lastHiddenStatsUpdate = now;
            foreach (var channel in Channels)
            {
                if (!channel.IsVisible)
                {
                    channel.UpdateStats();
                }
            }
        }

        var metrics = _acquisition.GetMetrics();
        SampleCountText = metrics.FrameCount.ToString("N0", CultureInfo.CurrentCulture);
        var elapsed = metrics.Elapsed;
        ElapsedText = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);

        if (metrics.ActualRate > 0)
        {
            ActualRateText = $"{metrics.ActualRate:0.#} Hz";
        }

        SelectedCountText = Localization.ChannelCount(Channels.Count);

        if (HasError)
        {
            ConnectionBadge = Localization.Get("BadgeError");
            StatusBrush = ErrorBrush;
        }
        else if (IsConnected)
        {
            ConnectionBadge = Localization.Get(IsDemo ? "BadgeDemo" : IsRunning ? "BadgeLive" : "BadgePaused");
            StatusBrush = IsDemo ? DemoBrush : IsRunning ? RunningBrush : LiveBrush;
        }
        else
        {
            ConnectionBadge = Localization.Get("BadgeOffline");
            StatusBrush = IdleBrush;
        }
    }

    public bool HasSamples => Channels.Any(channel => channel.Buffer.Count > 0);

    public string BuildCsv() => CsvExporter.BuildSnapshot(_acquisition.CaptureSnapshot());

    public void WriteCsv(TextWriter writer) => CsvExporter.WriteSnapshot(writer, _acquisition.CaptureSnapshot());

    public Task ExportCsvAsync(string path, string displayName)
    {
        if (IsExporting || _shuttingDown)
        {
            return Task.CompletedTask;
        }

        IsExporting = true;
        ExportProgress = 0;
        ExportStatusText = Localization.Format("Exporting", 0);
        var cts = new CancellationTokenSource();
        _exportCts = cts;
        _exportTask = ExportCsvCoreAsync(path, displayName, cts);
        return _exportTask;
    }

    private async Task ExportCsvCoreAsync(string path, string displayName, CancellationTokenSource cts)
    {
        var token = cts.Token;
        var progress = new Progress<double>(value =>
        {
            if (ReferenceEquals(_exportCts, cts))
            {
                ExportProgress = value * 100;
                ExportStatusText = Localization.Format("Exporting", value * 100);
            }
        });

        try
        {
            var snapshot = await Task.Run(() => _acquisition.CaptureSnapshot(token), token);
            await CsvExporter.ExportSnapshotAsync(path, snapshot, progress, token);

            ExportProgress = 100;
            SetStatus("Exported", displayName);
            AppLog.Info($"Exported CSV: {displayName}");
        }
        catch (OperationCanceledException)
        {
            ExportStatusText = Localization.Get("ExportCancelled");
            SetStatus("ExportCancelled");
        }
        finally
        {
            IsExporting = false;
            _exportCts = null;
            cts.Dispose();
        }
    }

    [RelayCommand]
    private void CancelExport() => _exportCts?.Cancel();

    public async Task ShutdownAsync()
    {
        _exportCts?.Cancel();
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        if (_exportTask is { } exportTask)
        {
            try
            {
                await exportTask;
            }
            catch (Exception exception)
            {
                AppLog.Error("Export failed during shutdown", exception);
            }
        }

        SaveWorkspace();
        await DisconnectAsync();
        await _acquisition.DisposeAsync();
        Localization.LanguageChanged -= OnLanguageChanged;
        DisposeVariableTree();
    }

    private void StartAcquisition()
    {
        if (_shuttingDown || !IsConnected || _source is null || _acquisition.IsRunning)
        {
            return;
        }

        if (Channels.Count == 0)
        {
            SetStatus("StatusSelectVariable");
            return;
        }

        if (!_acquisition.Start(_source))
        {
            return;
        }

        ClearError();
        IsRunning = true;
        SetStatus(_demo ? "StatusDemoRunning" : "StatusLiveSampling");
        UpdateStatusVisuals();
        _ = ObserveAcquisitionAsync(_acquisition.Completion, _acquisition.RunId);
    }

    private async Task ObserveAcquisitionAsync(Task completion, long runId)
    {
        await completion;
        if (_shuttingDown || runId != _acquisition.RunId)
        {
            return;
        }

        IsRunning = false;
        if (_acquisition.LastError is { } exception)
        {
            HandleAcquisitionError(exception);
        }
    }

    private void HandleAcquisitionError(Exception exception)
    {
        SetError($"Acquisition stopped: {exception.Message}", exception);
        ConnectionRetryAvailable = true;
        IsRunning = false;
        SetStatus("StatusAcquisitionError");
        UpdateStatusVisuals();
    }

    public async Task<bool> ImportSymbolsAsync(string path)
    {
        await _connectionGate.WaitAsync();
        try
        {
            if (_shuttingDown)
            {
                return false;
            }

            if (IsConnected)
            {
                SetError("Disconnect from the target before importing a symbol file.");
                return false;
            }

            ClearError();
            IsImportingSymbols = true;
            SetStatus("StatusReadingSymbols");
            var catalog = await Task.Run(() => ElfSymbolImporter.Import(path));
            var savedPath = await Task.Run(() => AppSettingsStore.SaveSymbolCatalog(catalog, path));
            _settings.SymbolCatalogPath = savedPath;
            _saveSettings();

            ClearChannels();
            AllVariables.Clear();
            _itemsByIdentity.Clear();
            _paletteIndex = 0;
            AddSymbols(catalog);

            SelectDefaultChannels();
            RefreshFilter();
            SaveWorkspace();
            SetStatus("StatusImportedVariables", catalog.VariableCount);
            AppLog.Info($"Imported symbols: {path} ({catalog.VariableCount:N0} variables)");
            return true;
        }
        catch (Exception ex)
        {
            SetError($"Symbol import failed: {ex.Message}", ex);
            SetStatus("StatusSymbolImportFailed");
            return false;
        }
        finally
        {
            IsImportingSymbols = false;
            _connectionGate.Release();
        }
    }

    private void LoadSymbols()
    {
        try
        {
            var catalog = _loadCatalog();
            AddSymbols(catalog);
        }
        catch (Exception ex)
        {
            SetError($"Symbols: {ex.Message}");
            _hasSymbolFile = false;
            SymbolFileText = Localization.Get("NoSymbolFile");
            UpdateSymbolCountText();
        }
    }

    private void AddSymbols(SymbolCatalog catalog)
    {
        _hasSymbolFile = true;
        _variableCount = catalog.VariableCount;
        _ramStart = catalog.RamStart;
        _ramSize = catalog.RamSize;
        SymbolFileText = Path.GetFileName(catalog.Elf);
        UpdateSymbolCountText();
        foreach (var variable in catalog.Variables)
        {
            var item = new SymbolItemViewModel(variable, OnVariableSelectionChanged);
            AllVariables.Add(item);
            _itemsByIdentity[variable.Identity] = item;
        }
    }

    private void ClearChannels()
    {
        Channels.Clear();
        PaneAChannels.Clear();
        PaneBChannels.Clear();
        PaneCChannels.Clear();
        PaneDChannels.Clear();
        _channelsByIdentity.Clear();
        _acquisition.SetChannels([]);

        SelectedCountText = Localization.ChannelCount(0);
        ApplyBufferLimits();
    }

    private void SelectDefaultChannels(bool restoreWorkspace = true)
    {
        if (restoreWorkspace && RestoreSavedChannels())
        {
            return;
        }

        string[] preferred =
        [
            "steppCtrl.TargetSpeed",
            "steppCtrl.CurrSpeed",
            "CurrLoop.CurrIsFb",
            "IqLite.measured_current",
            "Commut.Angle",
            "ADSamp.Vdc"
        ];

        foreach (var name in preferred)
        {
            if (_channelsByIdentity.Count >= 6)
            {
                break;
            }

            if (AllVariables.FirstOrDefault(item => item.Name == name) is { } item)
            {
                SelectItem(item);
            }
        }

        if (Channels.Count > 0)
        {
            return;
        }

        foreach (var item in AllVariables.Where(item => item.Symbol.Size >= 2).Take(4))
        {
            SelectItem(item);
        }
    }

    private bool RestoreSavedChannels()
    {
        var saved = _settings.Channels;
        if (saved.Count == 0)
        {
            return false;
        }

        var restored = 0;
        foreach (var entry in saved)
        {
            var item = entry.Address is { } address
                ? _itemsByIdentity.GetValueOrDefault($"{entry.Name}@{address:X8}")
                : AllVariables.FirstOrDefault(candidate => candidate.Name == entry.Name);
            if (item is null)
            {
                // Firmware rebuilds can move a uniquely named variable. Only
                // fall back by name when there is no ambiguous static symbol.
                var matches = AllVariables.Where(candidate => candidate.Name == entry.Name).Take(2).ToArray();
                item = matches.Length == 1 ? matches[0] : null;
            }
            if (item is null)
            {
                continue;
            }

            SelectItem(item);
            if (_channelsByIdentity.TryGetValue(item.Symbol.Identity, out var channel))
            {
                MoveChannelToPane(channel, entry.PaneIndex);
                channel.IsVisible = entry.Visible;
                restored++;
            }
        }

        return restored > 0;
    }

    private void ApplyWorkspaceSettings()
    {
        var settings = _settings;
        PaneCount = Math.Clamp(settings.PaneCount, 1, 4);
        SelectedPaneCount = PaneCountOptions.First(option => option.Count == PaneCount);
        SelectedPane = PaneOptions[Math.Clamp(settings.SelectedPane, 0, PaneCount - 1)];
        SelectedSampleRate = SampleRates.FirstOrDefault(option => option.Hz == settings.SampleRateHz) ?? SampleRates[3];
        if (settings.SpeedKHz > 0 &&
            JLinkSpeedOptions.All(option => option.KHz != settings.SpeedKHz))
        {
            JLinkSpeedOptions = CreateSpeedOptions(50_000, settings.SpeedKHz);
        }

        SelectedSpeed =
            JLinkSpeedOptions.FirstOrDefault(option => option.KHz == settings.SpeedKHz) ??
            JLinkSpeedOptions.First(option => option.KHz == 5000);
        JLinkDevice = settings.JLinkDevice;
        SelectedWindow = WindowOptions.FirstOrDefault(option => Math.Abs(option.Seconds - settings.WindowSeconds) < 0.01)
                         ?? WindowOptions[2];
        AutoFollow = settings.AutoFollow;
    }

    public void SaveWorkspace()
    {
        var settings = _settings;
        settings.SampleRateHz = SampleRateHz;
        settings.SpeedKHz = SpeedKHz;
        settings.JLinkDevice = EffectiveJLinkDevice;
        settings.WindowSeconds = WindowSeconds;
        settings.PaneCount = PaneCount;
        settings.SelectedPane = SelectedPane?.Index ?? 0;
        settings.AutoFollow = AutoFollow;
        settings.Language = Localization.Language;
        settings.Channels = Channels
            .Select(channel => new ChannelWorkspaceEntry
            {
                Name = channel.Name,
                Address = channel.Symbol.Address,
                PaneIndex = channel.PaneIndex,
                Visible = channel.IsVisible
            })
            .ToList();
        _saveSettings();
    }

    private void OnVariableSelectionChanged(SymbolItemViewModel item, bool selected)
    {
        if (selected)
        {
            SelectItem(item);
        }
        else
        {
            DeselectItem(item);
        }

        RefreshTreeSelectionVisuals();
    }

    private void ToggleGroupSelection(VariableNode group)
    {
        var items = Flatten([group]).ToArray();
        if (items.Length == 0)
        {
            return;
        }

        var select = !items.All(item => item.IsSelected);
        foreach (var item in items)
        {
            if (select)
            {
                SelectItem(item);
            }
            else
            {
                DeselectItem(item);
            }
        }

        RefreshTreeSelectionVisuals();
    }

    private void RefreshTreeSelectionVisuals()
    {
        foreach (var root in FilteredVariables)
        {
            root.RefreshSelectionVisuals();
        }
    }

    private void SelectItem(SymbolItemViewModel item)
    {
        var identity = item.Symbol.Identity;
        if (_channelsByIdentity.TryGetValue(identity, out var existing))
        {
            item.SetSelectedSilently(true, existing.ColorBrush);
            return;
        }

        var paneIndex = Math.Clamp(SelectedPane?.Index ?? 0, 0, PaneCount - 1);
        var capacity = SampleMemoryBudget.GetCapacity(Channels.Count + 1, SampleRateHz);
        var channel = new ChannelViewModel(item.Symbol, _paletteIndex++, MoveChannelToPane, capacity)
        {
            PaneIndex = paneIndex
        };
        _channelsByIdentity[identity] = channel;
        Channels.Add(channel);
        PaneCollection(paneIndex).Add(channel);
        item.SetSelectedSilently(true, channel.ColorBrush);
        _acquisition.SetChannels(Channels.ToArray());

        SelectedCountText = Localization.ChannelCount(Channels.Count);
        ApplyBufferLimits();
        if (ShowSelectedOnly)
        {
            RefreshFilter();
        }

        if (IsConnected && !IsRunning)
        {
            SetStatus("StatusReadyToSample");
        }
    }

    private void DeselectItem(SymbolItemViewModel item)
    {
        if (_channelsByIdentity.Remove(item.Symbol.Identity, out var channel))
        {
            Channels.Remove(channel);
            PaneAChannels.Remove(channel);
            PaneBChannels.Remove(channel);
            PaneCChannels.Remove(channel);
            PaneDChannels.Remove(channel);
            _acquisition.SetChannels(Channels.ToArray());
        }

        item.SetSelectedSilently(false);
        SelectedCountText = Localization.ChannelCount(Channels.Count);
        ApplyBufferLimits();
        if (ShowSelectedOnly)
        {
            RefreshFilter();
        }

        if (Channels.Count == 0 && IsRunning)
        {
            SetStatus("StatusSelectVariable");
        }
    }

    private void MoveChannelToPane(ChannelViewModel channel)
    {
        var target = PaneCount <= 1 ? 0 : (channel.PaneIndex + 1) % PaneCount;
        MoveChannelToPane(channel, target);
    }

    private void MoveChannelToPane(ChannelViewModel channel, int target)
    {
        target = Math.Clamp(target, 0, Math.Max(0, PaneCount - 1));
        PaneAChannels.Remove(channel);
        PaneBChannels.Remove(channel);
        PaneCChannels.Remove(channel);
        PaneDChannels.Remove(channel);
        channel.PaneIndex = target;
        PaneCollection(target).Add(channel);
    }

    private ObservableCollection<ChannelViewModel> PaneCollection(int index) => index switch
    {
        1 => PaneBChannels,
        2 => PaneCChannels,
        3 => PaneDChannels,
        _ => PaneAChannels
    };

    private void RebuildVisiblePaneOptions()
    {
        var selected = SelectedPane?.Index ?? 0;
        VisiblePaneOptions.Clear();
        for (var i = 0; i < PaneCount; i++)
        {
            VisiblePaneOptions.Add(PaneOptions[i]);
        }

        SelectedPane = selected < PaneCount ? PaneOptions[selected] : PaneOptions[0];
    }

    private void RefreshFilter()
    {
        var terms = SearchText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var visible = AllVariables.Where(item =>
            (!ShowSelectedOnly || item.IsSelected) &&
            (terms.Length == 0 || terms.All(term =>
                item.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase))));

        DisposeVariableTree();
        foreach (var root in BuildVariableTree(visible, terms.Length > 0, ToggleGroupSelection))
        {
            FilteredVariables.Add(root);
        }
    }

    private void DisposeVariableTree()
    {
        foreach (var root in FilteredVariables)
        {
            root.Dispose();
        }

        FilteredVariables.Clear();
    }

    private static List<VariableNode> BuildVariableTree(
        IEnumerable<SymbolItemViewModel> items,
        bool expandAll,
        Action<VariableNode>? toggleGroupSelection = null)
    {
        var roots = new List<VariableNode>();
        var groups = new Dictionary<string, VariableNode>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            var parts = item.Name.Split('.');
            VariableNode? parent = null;
            var path = "";

            for (var i = 0; i < parts.Length - 1; i++)
            {
                path = path.Length == 0 ? parts[i] : $"{path}.{parts[i]}";
                if (!groups.TryGetValue(path, out var group))
                {
                    group = VariableNode.CreateGroup(parts[i], path, toggleGroupSelection);
                    groups[path] = group;
                    if (parent is null)
                    {
                        roots.Add(group);
                    }
                    else
                    {
                        parent.Children.Add(group);
                    }
                }

                parent = group;
            }

            var leaf = VariableNode.CreateLeaf(parts[^1], item.Name, item);
            if (parent is null)
            {
                roots.Add(leaf);
            }
            else
            {
                parent.Children.Add(leaf);
            }
        }

        PrepareTree(roots, expandAll);
        return roots;
    }

    private static void PrepareTree(IList<VariableNode> nodes, bool expandAll)
    {
        var sorted = nodes
            .OrderBy(node => node.IsGroup ? 0 : 1)
            .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        nodes.Clear();
        foreach (var node in sorted)
        {
            nodes.Add(node);
        }

        foreach (var node in nodes)
        {
            if (node.IsGroup)
            {
                PrepareTree(node.Children, expandAll);
                node.IsExpanded = expandAll || node.HasSelectedLeaf();
            }

            node.UpdateLeafCount();
        }
    }

    private static IEnumerable<SymbolItemViewModel> Flatten(IEnumerable<VariableNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Item is { } item)
            {
                yield return item;
                continue;
            }

            foreach (var child in Flatten(node.Children))
            {
                yield return child;
            }
        }
    }

    private void UpdateStatusVisuals()
    {
        if (HasError)
        {
            StatusBrush = ErrorBrush;
            ConnectionBadge = Localization.Get("BadgeError");
        }
        else if (IsConnected)
        {
            StatusBrush = IsDemo ? DemoBrush : IsRunning ? RunningBrush : LiveBrush;
            ConnectionBadge = Localization.Get(IsDemo ? "BadgeDemo" : IsRunning ? "BadgeLive" : "BadgePaused");
        }
        else
        {
            StatusBrush = IdleBrush;
            ConnectionBadge = Localization.Get("BadgeOffline");
        }

        UpdateTargetSummary();
    }

    private void UpdateTargetSummary() =>
        TargetSummary = $"{EffectiveJLinkDevice} · SWD {SpeedKHz / 1000.0:0.#} MHz";

    private int _lastMemoryCapacity = -1;

    private void ApplyBufferLimits()
    {
        var channelCount = Channels.Count;
        var capacity = SampleMemoryBudget.GetCapacity(channelCount, SampleRateHz);
        foreach (var channel in Channels)
        {
            channel.Buffer.SetMaxCapacity(capacity);
        }

        var constrained = SampleMemoryBudget.IsConstrained(channelCount, SampleRateHz);
        MemoryGuardActive = constrained;
        MemoryGuardText = constrained
            ? Localization.Format(
                "MemoryGuardText",
                SampleMemoryBudget.MaxBufferBytes / 1024 / 1024,
                capacity / (double)Math.Max(1, SampleRateHz))
            : "";

        if (constrained && capacity != _lastMemoryCapacity)
        {
            AppLog.Warn(
                $"Sample memory guard active: {channelCount} channels at {SampleRateHz} Hz, " +
                $"capacity={capacity:N0} samples/channel " +
                $"({capacity / (double)Math.Max(1, SampleRateHz):0.#} s), " +
                $"budget={SampleMemoryBudget.MaxBufferBytes / 1024 / 1024} MB.");
        }

        _lastMemoryCapacity = capacity;
    }

    private string EffectiveJLinkDevice =>
        string.IsNullOrWhiteSpace(JLinkDevice) ? "Cortex-M0" : JLinkDevice.Trim();

    private static ObservableCollection<SpeedOption> CreateSpeedOptions(
        int maxSpeedKHz,
        int actualSpeedKHz = 0)
    {
        var values = CommonJLinkSpeeds
            .Where(speedKHz => speedKHz < maxSpeedKHz)
            .ToList();
        if (actualSpeedKHz > 0 && actualSpeedKHz < maxSpeedKHz && !values.Contains(actualSpeedKHz))
        {
            values.Add(actualSpeedKHz);
        }

        values.Sort();
        var options = new ObservableCollection<SpeedOption>(
            values.Select(speedKHz => new SpeedOption(speedKHz)));
        options.Add(new SpeedOption(maxSpeedKHz, isMax: true));
        return options;
    }

    private void ApplyProbeSpeedLimit(int actualSpeedKHz, int maxSpeedKHz)
    {
        if (maxSpeedKHz <= 0)
        {
            return;
        }

        var preferredSpeedKHz = actualSpeedKHz > 0
            ? Math.Min(actualSpeedKHz, maxSpeedKHz)
            : Math.Min(SpeedKHz, maxSpeedKHz);
        JLinkSpeedOptions = CreateSpeedOptions(maxSpeedKHz, preferredSpeedKHz);
        SelectedSpeed =
            JLinkSpeedOptions.FirstOrDefault(option => option.KHz == preferredSpeedKHz) ??
            JLinkSpeedOptions.First(option => option.IsMax);
    }

    private void ResetSpeedOptions()
    {
        var preferredSpeedKHz = SpeedKHz;
        JLinkSpeedOptions = CreateSpeedOptions(50_000, preferredSpeedKHz);
        SelectedSpeed =
            JLinkSpeedOptions.FirstOrDefault(option => option.KHz == preferredSpeedKHz) ??
            JLinkSpeedOptions.First(option => option.IsMax);
    }

    private void SetStatus(string key, params object[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        StatusText = Localization.Format(key, arguments);
    }

    private void SetConnectionDetail(string key, params object[] arguments)
    {
        _connectionKey = key;
        _connectionArguments = arguments;
        ConnectionDetail = Localization.Format(key, arguments);
    }

    private void UpdateSymbolCountText() =>
        SymbolCountText = Localization.Format("VariablesCount", _variableCount);

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(RunButtonText));
        OnPropertyChanged(nameof(ThemeLabel));
        OnPropertyChanged(nameof(LanguageLabel));
        StatusText = Localization.Format(_statusKey, _statusArguments);
        if (_connectionKey is not null)
        {
            ConnectionDetail = Localization.Format(_connectionKey, _connectionArguments);
        }
        else if (_demo && IsConnected)
        {
            ConnectionDetail = Localization.Get("DemoDescription");
        }

        SelectedCountText = Localization.ChannelCount(Channels.Count);
        UpdateSymbolCountText();
        if (!_hasSymbolFile)
        {
            SymbolFileText = Localization.Get("NoSymbolFile");
        }

        RefreshTreeSelectionVisuals();
        UpdateStatusVisuals();
        UpdateLiveStats();
        ApplyBufferLimits();
    }

    [RelayCommand]
    private void ToggleLanguage() =>
        Localization.SetLanguage(Localization.IsEnglish ? "zh" : "en");

    private void SetError(string message, Exception? exception = null)
    {
        AppLog.Error(message, exception);
        ErrorMessage = message;
        HasError = true;
        SetStatus("StatusError");
        StatusBrush = ErrorBrush;
    }

    private void ClearError()
    {
        ErrorMessage = null;
        HasError = false;
        ConnectionRetryAvailable = false;
    }

    private void ValidateTargetAddresses()
    {
        if (_demo || _ramSize == 0 || Channels.Count == 0)
        {
            return;
        }

        var ramEnd = (ulong)_ramStart + _ramSize;
        var invalid = Channels
            .Where(channel =>
                channel.Symbol.Address < _ramStart ||
                (ulong)channel.Symbol.Address + (uint)channel.Symbol.Size > ramEnd)
            .Select(channel => channel.Name)
            .ToArray();

        if (invalid.Length > 0)
        {
            throw new InvalidOperationException(Localization.Format(
                "ErrorAddressOutsideRam",
                $"0x{_ramStart:X8}",
                $"0x{ramEnd - 1:X8}",
                string.Join(", ", invalid)));
        }
    }

}
