using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ink_Canvas.Controls;
using Ink_Canvas.Plugins;
using Microsoft.Extensions.DependencyInjection;
using VoicePlugin.Config;
using VoicePlugin.Services;
using VoicePlugin.Views;

namespace VoicePlugin
{
    [PluginEntrance]
    public class VoicePlugin : PluginBase
    {
        private const string ConfigFileName = "voice_config.json";
        private const string LegacyConfigFileName = "voice_config.ini";
        private const string RollCallHistoryFileName = "RollCallHistory.json";
        private const int HistoryPollIntervalMilliseconds = 300;
        private const int StartupHistoryGracePeriodMilliseconds = 2000;

        private VoiceConfigSnapshot _config =
            new VoiceConfigSnapshot(new VoiceConfig());
        private VoiceConfigStore _configStore;
        private readonly VoiceTextFormatter _formatter = new VoiceTextFormatter();
        private readonly object _stateGate = new object();
        private WinRtPlaybackService _audioPlayback;
        private TtsProviderManager _providers;
        private VoiceQueue _voiceQueue;
        private TtsAudioCache _audioCache;
        private PrecacheService _precacheService;
        private HotkeyComponent _hotkeyComponent;
        private INotificationService _notificationService;
        private SettingsView _settingsView;
        private readonly object _settingsViewLock = new object();
        private TrayMenuComponent _trayMenuComponent;
        private MoreMenuComponent _moreMenuComponent;
        private AutomationActionComponent _automationActions;
        private CancellationTokenSource _activeSample;
        private object _eventServiceInstance;
        private EventInfo _randomPickCompletedEvent;
        private Delegate _randomPickCompletedHandler;
        private Timer _historyPollTimer;
        private string _rollCallHistoryPath;
        private List<string> _knownHistory = new List<string>();
        private DateTime _historyMonitoringStartsAtUtc;
        private int _historyPollInProgress;
        private volatile bool _isShuttingDown;

        public override void Initialize(IPluginHost host, IServiceCollection services)
        {
            base.Initialize(host);
            _isShuttingDown = false;

            try
            {
                _configStore = new VoiceConfigStore(
                    ConfigPath,
                    LegacyConfigPath,
                    Log,
                    LogError);
                var loaded = _configStore.Load(out var migrated);
                Volatile.Write(ref _config, loaded);

                // 播报音频缓存（Edge/WinRT 命中后直接播放缓存音频）。
                // 无论开关如何都创建实例：设置页热切换开关时必须有对象可操作，
                // 由 SetEnabled 门控；关闭时仅不启用，不做缓存读写。
                _audioCache = new TtsAudioCache(
                    Path.Combine(
                        PluginConfigFolder ?? string.Empty,
                        "AudioCache"),
                    loaded.AudioCacheSizeMb * 1024L * 1024L,
                    Log,
                    LogError);
                _audioCache.SetPolicy(
                    loaded.CacheLimitMode,
                    loaded.AudioCacheSizeMb * 1024L * 1024L,
                    loaded.AudioCacheRetentionDays);
                if (!loaded.EnableAudioCache)
                {
                    _audioCache.SetEnabled(false);
                }

                _audioPlayback = new WinRtPlaybackService();
                var sapi = new SapiTtsProvider(Log, LogError);
                var winRt = new WinRtTtsProvider(
                    _audioPlayback,
                    _audioCache,
                    Log,
                    LogError);
                // Edge 在线引擎仅在 net10 构建包含（WITH_EDGE_TTS，依赖
                // EdgeTTS.DotNet 无 net6 目标）；net6 构建不注册该引擎，
                // TtsProviderManager 对其自动显示“该引擎未加载。”，
                // 播报请求按既有容错逻辑自动降级到 WinRT/SAPI。
#if WITH_EDGE_TTS
                var edge = new EdgeOnlineTtsProvider(
                    _audioPlayback,
                    _audioCache,
                    Log,
                    LogError);
#endif
                _providers = new TtsProviderManager(
#if WITH_EDGE_TTS
                    new ITtsProvider[] { sapi, winRt, edge },
#else
                    new ITtsProvider[] { sapi, winRt },
#endif
                    Log,
                    LogError);

                var cuePlayer = new PreSpeechCuePlayer(_audioPlayback, Log, LogError);
                _voiceQueue = new VoiceQueue(_providers, LogError, cuePlayer);
                _voiceQueue.SpeakingStateChanged += OnSpeakingStateChanged;
                _notificationService = GetService<INotificationService>();

                // 全局热键“截断播报”：与宿主内置快捷键共用注册通道，
                // 并在宿主“快捷键设置”页注入配置项。
                _hotkeyComponent = new HotkeyComponent(
                    GetService<IHotkeyService>(),
                    ApplyConfigAsync,
                    StopSpeaking,
                    Log,
                    LogError);
                _hotkeyComponent.Apply(loaded);

                // 随机发音人模式下启动后台预热语音列表缓存，
                // 避免首次随机播报联网拉取（不阻塞启动，失败静默）。
                if (string.Equals(
                    loaded.VoiceId,
                    VoiceConfigSnapshot.RandomVoiceId,
                    StringComparison.OrdinalIgnoreCase))
                {
                    var prewarmProvider = loaded.Provider;
                    var prewarmCancellation = new CancellationTokenSource(
                        TimeSpan.FromSeconds(30));
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _providers.GetVoicesAsync(
                                prewarmProvider,
                                false,
                                prewarmCancellation.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            LogError("[Voice] voice list prewarm failed.", ex);
                        }
                        finally
                        {
                            prewarmCancellation.Dispose();
                        }
                    });
                }

                // 预缓存执行器：手动按钮与启动自动预缓存共用。
                // 与缓存对象一样始终创建；运行时会自查缓存开关（IsEnabled），
                // 关闭时直接跳过，因此无需按启动时的开关分叉。
                _precacheService = new PrecacheService(
                    _audioCache,
                    _providers,
                    GetConfig,
                    _formatter,
                    Path.Combine(
                        PluginConfigFolder ?? string.Empty,
                        ".roster_fingerprint.txt"),
                    Log,
                    LogError,
                    (title, message, level) =>
                        _notificationService?.Show(title, message, level));

                // 启动时检测名单变更（开启“切换名单后清除缓存”时清空旧缓存）。
                _precacheService.CheckRosterChange();

                if (loaded.PrecacheOnStartup)
                {
                    // 后台执行，不阻塞插件启动；缓存关闭时内部直接跳过。
                    _precacheService.StartPrecache();
                }

                // 浮动工具栏组件通过官方 SDK 注册（RegisterToolbarItem）。
                // 注：宿主 v1.7.19.9 的 SDK 无 RegisterBoardToolbarItem
                // （白板工具栏 API 为后续版本新增），白板控制栏的播报截断
                // 在旧宿主上不可用，仅注册浮动工具栏。
                RegisterToolbarItems(host);

                // “更多/工具”菜单组件：把“播报截断”注册进宿主菜单设置项，
                // 并在浮动工具栏“更多”菜单与白板“工具”菜单中渲染按钮。
                _moreMenuComponent = new MoreMenuComponent(
                    StopSpeaking,
                    Log,
                    LogError);
                _moreMenuComponent.Install();

                // 自动化集成：4 个行动（切换引擎/音色、语速、模板、开关）
                // + 2 条规则（TTS播报中、当前TTS引擎），反射注册进宿主自动化引擎。
                _automationActions = new AutomationActionComponent(
                    ApplyConfigAsync,
                    () => _voiceQueue?.IsSpeaking ?? false,
                    GetConfig,
                    Log,
                    LogError);
                _automationActions.Install();

                // 托盘右键菜单（直接注入宿主托盘 ContextMenu；
                // 宿主 ITrayService 因 Name 前缀带点号对任何 id 都会抛异常）。
                _trayMenuComponent = new TrayMenuComponent(
                    GetConfig,
                    ToggleEnabledAsync,
                    Log,
                    LogError);
                _trayMenuComponent.Install();

                // 注：宿主 v1.7.19.9 的 SDK 无 IPluginUriService，
                // icc://plugin/voice/* URI 路由在新版宿主才可用，旧宿主上不可用。
                if (migrated)
                {
                    // 观察写任务的结果，避免 SaveAfterDelayAsync 写失败时
                    // rethrow 产生未观察的任务异常（错误本身已记录到日志）。
                    _ = _configStore.ScheduleSaveAsync(loaded, 0)
                        .ContinueWith(
                            static task => _ = task.Exception,
                            CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted
                                | TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                }

                if (TrySubscribeToRandomPickEvent())
                {
                    Log("[Voice] initialized; subscribed to the host random-pick event.");
                    return;
                }

                StartHistoryMonitor();
                Log("[Voice] initialized; using roll-call history monitoring compatibility mode.");
            }
            catch (Exception ex)
            {
                LogError("[Voice] initialization failed; the plugin will remain inactive.", ex);
                DisposeVoiceResources();
            }
        }

        public override object GetSettingsView()
        {
            lock (_settingsViewLock)
            {
                if (_settingsView == null)
                {
                    _settingsView = new SettingsView(
                        GetConfig,
                        UpdateConfigAsync,
                        GetVoicesAsync,
                        GetProviderAvailability,
                        _formatter,
                        PlaySampleAsync,
                        () => _voiceQueue?.IsSpeaking ?? false,
                        () => _precacheService?.StartPrecache(),
                        () => _audioCache?.Clear(),
                        PrecacheTextProvider.GetRosters,
                        () => _audioCache?.GetStats());
                }

                return _settingsView;
            }
        }

        public override void Shutdown()
        {
            lock (_stateGate)
            {
                if (_isShuttingDown) return;
                _isShuttingDown = true;
            }

            var moreMenu = _moreMenuComponent;
            _moreMenuComponent = null;
            try
            {
                moreMenu?.Dispose();
            }
            catch (Exception ex)
            {
                LogError("[Voice] failed to dispose the more-menu component.", ex);
            }

            var tray = _trayMenuComponent;
            _trayMenuComponent = null;
            try
            {
                tray?.Dispose();
            }
            catch (Exception ex)
            {
                LogError("[Voice] failed to dispose the tray menu component.", ex);
            }

            var automation = _automationActions;
            _automationActions = null;
            try
            {
                automation?.Unregister();
            }
            catch (Exception ex)
            {
                LogError("[Voice] failed to unregister the automation items.", ex);
            }

            var precache = _precacheService;
            _precacheService = null;
            try
            {
                precache?.Stop();
            }
            catch (Exception ex)
            {
                LogError("[Voice] failed to stop the precache service.", ex);
            }

            var hotkey = _hotkeyComponent;
            _hotkeyComponent = null;
            try
            {
                hotkey?.Dispose();
            }
            catch (Exception ex)
            {
                LogError("[Voice] failed to dispose the hotkey component.", ex);
            }

            DisposeVoiceResources();
            lock (_settingsViewLock)
            {
                _settingsView = null;
            }
            Log("[Voice] shutdown complete.");
        }

        /// <summary>
        /// Stops the current cue/TTS operation and drops all queued announcements.
        /// This is intentionally separate from Shutdown so UI actions never dispose the worker.
        /// 这是所有“截断播报”按钮/菜单项的唯一入口，截断后按设置发送通知。
        /// </summary>
        public void StopSpeaking()
        {
            lock (_stateGate)
            {
                if (_isShuttingDown) return;
                _voiceQueue?.Clear();
            }

            NotifySpeechTruncated();
        }

        private static readonly TimeSpan NotifyDebounceWindow =
            TimeSpan.FromMilliseconds(1500);
        private DateTime _lastTruncateNotificationUtc = DateTime.MinValue;
        private readonly object _notifyGate = new object();

        private void NotifySpeechTruncated()
        {
            try
            {
                if (!GetConfig().NotifyOnStop) return;

                // 防抖：连点截断按钮时避免连续弹通知。
                lock (_notifyGate)
                {
                    var now = DateTime.UtcNow;
                    if (now - _lastTruncateNotificationUtc < NotifyDebounceWindow)
                    {
                        return;
                    }
                    _lastTruncateNotificationUtc = now;
                }

                var notification = _notificationService;
                if (notification == null) return;

                notification.Show(
                    "语音播报",
                    "已截断当前播报",
                    NotificationLevel.Info);
            }
            catch (Exception ex)
            {
                LogError("[Voice] failed to show the truncate notification.", ex);
            }
        }

        /// <summary>
        /// 播报状态变化（工作线程触发）：通知自动化引擎重新求值规则，
        /// 让“TTS播报中”等规则实时反映状态。
        /// </summary>
        private void OnSpeakingStateChanged(bool speaking)
        {
            _automationActions?.NotifySpeakingStateChanged();
        }

        private void RegisterToolbarItems(IPluginHost host)
        {
            try
            {
                host.RegisterToolbarItem(new PluginToolbarItemInfo
                {
                    Id = "voice.stop",
                    DisplayName = "播报截断",
                    Description = "单击立即截断（停止）当前正在播放的语音并清空待播队列",
                    IconGeometry = VoiceIconCatalog.StopIconGeometry,
                    ViewFactory = CreateToolbarButton,
                    ApplyOrientation = ApplyToolbarOrientation
                });
            }
            catch (Exception ex)
            {
                LogError("[Voice] failed to register toolbar controls.", ex);
            }
        }

        private ToolbarImageButton CreateToolbarButton()
        {
            var button = new ToolbarImageButton
            {
                Label = "播报截断",
                Tag = "VoicePlugin"
            };
            button.Icon.Geometry = Geometry.Parse(VoiceIconCatalog.StopIconGeometry);
            button.ToolTip = "单击：截断（停止）当前播报";
            button.ButtonMouseUp += (sender, args) => StopSpeaking();

            // “播报截断”四字以 10px 字号（约 40px 宽）在 44px 槽位内完整显示；
            // 模板标签启用了宿主 AutoFontSizeHelper（对中文恢复 13px 导致截断），
            // 需在 Loaded（视觉树就绪、helper 恢复字号之前）禁用并固定字号。
            button.LabelFontSize = 10;
            button.Loaded += (sender, args) =>
            {
                try
                {
                    var label = FindLabelTextBlock(button);
                    if (label == null) return;
                    VisualTreeHelpers.DisableAutoFontSizeHelper(label);
                    label.FontSize = 10;

                    // 图标与文字整体向下移动 3px：
                    // 模板内容（ButtonContent）默认贴顶，视觉上略偏上。
                    if (VisualTreeHelpers.FindFirstChild<Grid>(button) is Grid panel)
                    {
                        foreach (var child in panel.Children)
                        {
                            if (child is Grid content)
                            {
                                content.RenderTransform =
                                    new TranslateTransform(0, 3);
                                break;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // 布局微调失败不影响按钮功能。
                }
            };

            return button;
        }

        private static TextBlock FindLabelTextBlock(DependencyObject root)
        {
            return VisualTreeHelpers.FindFirstChild<TextBlock>(root);
        }

        private static void ApplyToolbarOrientation(
            FrameworkElement view,
            Orientation orientation)
        {
            if (view is ToolbarImageButton button)
            {
                button.ApplyOrientation(orientation == Orientation.Vertical);
            }
        }

        private async Task ToggleEnabledAsync()
        {
            try
            {
                await SetEnabledAsync(!GetConfig().Enabled).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogError("[Voice] failed to toggle the speech switch.", ex);
            }
        }

        /// <summary>
        /// 自动化行动的应用回调：对当前配置做一次变更并热生效、持久化。
        /// 引擎/开关/模板等运行时相关变化会清空当前播报队列。
        /// </summary>
        private Task ApplyConfigAsync(Action<VoiceConfig> mutate)
        {
            try
            {
                var current = GetConfig();
                var config = current.ToConfig();
                mutate(config);
                return UpdateConfigAsync(new VoiceConfigSnapshot(config));
            }
            catch (Exception ex)
            {
                LogError("[Voice] failed to apply an automation action.", ex);
                return Task.FromResult(false);
            }
        }

        private async Task<bool> SetEnabledAsync(bool enabled)
        {
            VoiceConfigSnapshot next;
            VoiceConfigStore store;
            lock (_stateGate)
            {
                if (_isShuttingDown) return false;

                var current = GetConfig();
                if (current.Enabled == enabled) return true;

                var config = current.ToConfig();
                config.Enabled = enabled;
                next = new VoiceConfigSnapshot(config);
                Volatile.Write(ref _config, next);
                if (!enabled)
                {
                    _voiceQueue?.Clear();
                }
                store = _configStore;
            }

            _trayMenuComponent?.UpdateState(enabled);
            _settingsView?.RefreshFromConfig();
            if (store == null) return false;

            try
            {
                await store.ScheduleSaveAsync(next).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                LogError("[Voice] failed to save the toolbar speech toggle.", ex);
                return false;
            }
        }

        /// <summary>
        /// 尝试直接订阅宿主的“抽选完成”事件。
        /// 注意：当前宿主版本（community-net10 源码 IEventService）并未提供
        /// RandomPickCompleted 事件，本路径总是返回 false，实际走历史文件
        /// 监控兼容模式；保留此分支仅为适配未来宿主版本新增事件的情况。
        /// </summary>
        private bool TrySubscribeToRandomPickEvent()
        {
            try
            {
                var eventService = GetService<IEventService>();
                if (eventService == null) return false;

                var eventInfo = eventService.GetType().GetEvent(
                    "RandomPickCompleted",
                    BindingFlags.Instance | BindingFlags.Public);
                if (eventInfo == null || eventInfo.EventHandlerType == null) return false;

                var handlerMethod = GetType().GetMethod(
                    nameof(OnRandomPickCompleted),
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var handler = Delegate.CreateDelegate(
                    eventInfo.EventHandlerType,
                    this,
                    handlerMethod,
                    false);
                if (handler == null) return false;

                eventInfo.AddEventHandler(eventService, handler);
                _eventServiceInstance = eventService;
                _randomPickCompletedEvent = eventInfo;
                _randomPickCompletedHandler = handler;
                return true;
            }
            catch (Exception ex)
            {
                LogError("[Voice] could not subscribe to the optional host random-pick event.", ex);
                return false;
            }
        }

        private void StartHistoryMonitor()
        {
            _rollCallHistoryPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Configs",
                RollCallHistoryFileName);
            _knownHistory = ReadHistory();
            _historyMonitoringStartsAtUtc = DateTime.UtcNow;
            _historyPollTimer = new Timer(
                OnHistoryPollTimer,
                null,
                HistoryPollIntervalMilliseconds,
                HistoryPollIntervalMilliseconds);
        }

        private void OnHistoryPollTimer(object state)
        {
            if (_isShuttingDown
                || Interlocked.Exchange(ref _historyPollInProgress, 1) != 0)
            {
                return;
            }

            try
            {
                if (!TryReadHistory(out var currentHistory)) return;

                if (DateTime.UtcNow - _historyMonitoringStartsAtUtc
                    < TimeSpan.FromMilliseconds(StartupHistoryGracePeriodMilliseconds))
                {
                    _knownHistory = currentHistory;
                    return;
                }

                var newResults = GetNewHistoryItems(_knownHistory, currentHistory);
                _knownHistory = currentHistory;
                if (newResults.Count > 0)
                {
                    OnRandomPickCompleted(newResults);
                }
            }
            catch (Exception ex)
            {
                LogError("[Voice] failed to read roll-call history.", ex);
            }
            finally
            {
                Volatile.Write(ref _historyPollInProgress, 0);
            }
        }

        private List<string> ReadHistory()
        {
            return TryReadHistory(out var history)
                ? history
                : new List<string>();
        }

        private bool TryReadHistory(out List<string> history)
        {
            history = null;
            try
            {
                if (string.IsNullOrWhiteSpace(_rollCallHistoryPath)
                    || !File.Exists(_rollCallHistoryPath))
                {
                    history = new List<string>();
                    return true;
                }

                var json = File.ReadAllText(_rollCallHistoryPath);
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty(
                    "History",
                    out var historyElement)
                    || historyElement.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }

                history = historyElement
                    .EnumerateArray()
                    .Select(element => element.ValueKind == JsonValueKind.String
                        ? element.GetString()
                        : null)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToList();
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static List<string> GetNewHistoryItems(
            List<string> previous,
            List<string> current)
        {
            if (current == null || current.Count == 0)
            {
                return new List<string>();
            }

            if (previous == null || previous.Count == 0)
            {
                return new List<string>(current);
            }

            var overlapLimit = Math.Min(previous.Count, current.Count);
            for (var overlap = overlapLimit; overlap > 0; overlap--)
            {
                var matches = true;
                for (var index = 0; index < overlap; index++)
                {
                    if (!string.Equals(
                        previous[previous.Count - overlap + index],
                        current[index],
                        StringComparison.Ordinal))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    return current.Skip(overlap).ToList();
                }
            }

            // 尾重叠完全失败：宿主只做“追加 + 从头部裁剪到 100 条”，
            // 正常轮询下不可能出现；说明历史文件被整体替换/重置
            // （恢复备份、损坏重建等）。此时旧文件的内容与当前文件
            // 没有承接关系，回退猜测"最近 10 条"会误播旧名单的名字，
            // 因此选择静默重新对齐基线（调用方会更新 _knownHistory）。
            return new List<string>();
        }

        private void OnRandomPickCompleted(List<string> results)
        {
            if (results == null || results.Count == 0) return;

            try
            {
                lock (_stateGate)
                {
                    var config = GetConfig();
                    if (_isShuttingDown || !config.Enabled) return;

                    var speechTexts = _formatter.FormatBatch(results, config);
                    if (speechTexts.Count == 0) return;

                    var queue = _voiceQueue;
                    if (queue == null)
                    {
                        LogError("[Voice] speech queue is unavailable; the result was not announced.");
                        return;
                    }

                    if (!queue.EnqueueLatest(new SpeechBatchRequest(
                        speechTexts,
                        config)))
                    {
                        Log("[Voice] latest random-pick announcement could not be queued.");
                        return;
                    }

                    Log($"[Voice] queued latest announcement for {speechTexts.Count} result(s).");
                }
            }
            catch (Exception ex)
            {
                LogError("[Voice] failed to handle a random-pick result.", ex);
            }
        }

        private string ConfigPath => Path.Combine(
            PluginConfigFolder ?? string.Empty,
            ConfigFileName);

        private string LegacyConfigPath => Path.Combine(
            PluginConfigFolder ?? string.Empty,
            LegacyConfigFileName);

        private VoiceConfigSnapshot GetConfig()
        {
            return Volatile.Read(ref _config);
        }

        private async Task<bool> UpdateConfigAsync(VoiceConfigSnapshot config)
        {
            if (config == null) return false;

            VoiceConfigStore store;
            VoiceConfigSnapshot previous = null;
            lock (_stateGate)
            {
                if (_isShuttingDown) return false;

                previous = Interlocked.Exchange(ref _config, config);
                try
                {
                    if (!config.Enabled || HasRuntimeSpeechChange(previous, config))
                    {
                        _voiceQueue?.Clear();
                    }
                }
                catch (Exception ex)
                {
                    LogError("[Voice] failed to apply hot-reloaded settings.", ex);
                }
                store = _configStore;
            }

            _trayMenuComponent?.UpdateState(config.Enabled);
            if (previous == null || previous.ShowTrayToggle != config.ShowTrayToggle)
            {
                _trayMenuComponent?.UpdateVisibility(config.ShowTrayToggle);
            }
            // 音频缓存设置热生效（开关/限制方式/容量/天数即时应用）。
            if (previous == null
                || previous.EnableAudioCache != config.EnableAudioCache
                || previous.CacheLimitMode != config.CacheLimitMode
                || previous.AudioCacheSizeMb != config.AudioCacheSizeMb
                || previous.AudioCacheRetentionDays != config.AudioCacheRetentionDays)
            {
                _audioCache?.SetEnabled(config.EnableAudioCache);
                _audioCache?.SetPolicy(
                    config.CacheLimitMode,
                    config.AudioCacheSizeMb * 1024L * 1024L,
                    config.AudioCacheRetentionDays);
                // 缓存由关转开：补一次名单变更检测，避免直接命中旧名单的缓存音频。
                if (config.EnableAudioCache
                    && (previous == null || !previous.EnableAudioCache))
                {
                    _precacheService?.CheckRosterChange();
                }
            }
            // 全局热键设置热生效（开关/组合键变化即时重注册）。
            if (previous == null
                || previous.EnableStopHotkey != config.EnableStopHotkey
                || previous.StopHotkeyModifiers != config.StopHotkeyModifiers
                || previous.StopHotkeyKey != config.StopHotkeyKey)
            {
                _hotkeyComponent?.Apply(config);
            }
            // 设置变化后顺带自愈“更多/工具”菜单按钮。
            _moreMenuComponent?.EnsureInjected();
            _settingsView?.RefreshFromConfig();

            try
            {
                if (store == null) return false;
                await store.ScheduleSaveAsync(config).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                LogError("[Voice] failed to persist hot-reloaded settings.", ex);
                return false;
            }
        }

        private Task<IReadOnlyList<TtsVoiceInfo>> GetVoicesAsync(
            TtsProviderKind provider,
            bool refreshOnline,
            CancellationToken cancellationToken)
        {
            var manager = _providers;
            return manager == null
                ? Task.FromResult<IReadOnlyList<TtsVoiceInfo>>(Array.Empty<TtsVoiceInfo>())
                : manager.GetVoicesAsync(provider, refreshOnline, cancellationToken);
        }

        private IReadOnlyList<ProviderAvailability> GetProviderAvailability()
        {
            return _providers?.ProviderAvailability
                ?? Array.Empty<ProviderAvailability>();
        }

        private static bool HasRuntimeSpeechChange(
            VoiceConfigSnapshot previous,
            VoiceConfigSnapshot current)
        {
            if (previous == null) return true;
            return previous.Provider != current.Provider
                || previous.Rate != current.Rate
                || previous.Volume != current.Volume
                || !string.Equals(previous.VoiceId, current.VoiceId, StringComparison.Ordinal)
                || previous.CueEnabled != current.CueEnabled
                || !string.Equals(previous.CuePath, current.CuePath, StringComparison.OrdinalIgnoreCase)
                || previous.PostCueEnabled != current.PostCueEnabled
                || !string.Equals(previous.PostCuePath, current.PostCuePath, StringComparison.OrdinalIgnoreCase)
                || previous.PostCueGapMilliseconds != current.PostCueGapMilliseconds
                || previous.EnablePronunciations != current.EnablePronunciations;
        }

        private static bool IsRandomVoiceId(string voiceId)
        {
            return string.Equals(
                voiceId,
                VoiceConfigSnapshot.RandomVoiceId,
                StringComparison.OrdinalIgnoreCase);
        }

        private void DisposeVoiceResources()
        {
            // 取消正在进行的示例试听（其播放链路与正式播报共用播放服务，
            // 关闭后不再需要它；取消由试听自身的 finally 完成清理与释放）。
            lock (_stateGate)
            {
                var sample = _activeSample;
                _activeSample = null;
                try
                {
                    sample?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            var timer = _historyPollTimer;
            _historyPollTimer = null;
            try
            {
                timer?.Dispose();
            }
            catch (Exception ex)
            {
                LogError("[Voice] failed to stop history monitoring.", ex);
            }

            if (_randomPickCompletedEvent != null
                && _randomPickCompletedHandler != null)
            {
                try
                {
                    _randomPickCompletedEvent.RemoveEventHandler(
                        _eventServiceInstance,
                        _randomPickCompletedHandler);
                }
                catch (Exception ex)
                {
                    LogError("[Voice] failed to unsubscribe from the host random-pick event.", ex);
                }
            }

            _eventServiceInstance = null;
            _randomPickCompletedEvent = null;
            _randomPickCompletedHandler = null;

            var queue = _voiceQueue;
            var providers = _providers;
            var playback = _audioPlayback;
            _voiceQueue = null;
            _providers = null;
            _audioPlayback = null;

            if (queue != null)
            {
                try
                {
                    queue.SpeakingStateChanged -= OnSpeakingStateChanged;
                }
                catch (Exception ex)
                {
                    LogError("[Voice] failed to unsubscribe from the speaking state event.", ex);
                }
            }

            try
            {
                queue?.Dispose();
            }
            catch (Exception ex)
            {
                LogError("[Voice] failed to dispose the speech queue.", ex);
            }

            try
            {
                providers?.Dispose();
            }
            catch (Exception ex)
            {
                LogError("[Voice] failed to dispose the TTS providers.", ex);
            }

            try
            {
                playback?.Dispose();
            }
            catch (Exception ex)
            {
                LogError("[Voice] failed to dispose audio playback.", ex);
            }

            var store = _configStore;
            _configStore = null;
            if (store != null)
            {
                try
                {
                    store.FlushAsync(TimeSpan.FromSeconds(2))
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception ex)
                {
                    LogError("[Voice] failed to flush configuration during shutdown.", ex);
                }
                store.Dispose();
            }
        }

        /// <summary>
        /// 使用当前设置的引擎与音效试听一段示例文本。
        /// 走独立的播放链路（不经过播报队列），因此不会顶掉正在进行的正式播报；
        /// 若此时正好有正式播报在朗读则直接跳过本次试听。
        /// 再次点击试听会停止上一次试听并重新开始。
        /// </summary>
        private async Task PlaySampleAsync(
            VoiceConfigSnapshot snapshot,
            string sampleText)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(sampleText)) return;

            TtsProviderManager providers;
            WinRtPlaybackService playback;
            CancellationTokenSource cancellation;
            lock (_stateGate)
            {
                if (_isShuttingDown) return;

                providers = _providers;
                playback = _audioPlayback;
                if (providers == null || playback == null) return;

                // 正式播报进行中时不打断它。
                if (_voiceQueue?.IsSpeaking == true)
                {
                    Log("[Voice] sample playback skipped because an announcement is speaking.");
                    return;
                }

                var previous = _activeSample;
                _activeSample = CancellationTokenSource.CreateLinkedTokenSource(
                    CancellationToken.None);
                cancellation = _activeSample;
                try
                {
                    previous?.Cancel();
                    previous?.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            try
            {
                var cuePlayer = new PreSpeechCuePlayer(playback, Log, LogError);
                var pronunciations = new PronunciationDictionary(
                    snapshot.EnablePronunciations
                        ? snapshot.Pronunciations
                        : null);
                var ttsRequest = snapshot.CreateTtsRequest();
                if (IsRandomVoiceId(ttsRequest.VoiceId))
                {
                    // “随机发音人”：为本次试听解析一个具体的发音人。
                    var randomVoiceId = await providers
                        .ResolveRandomVoiceAsync(
                            ttsRequest.Provider,
                            cancellation.Token)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(randomVoiceId))
                    {
                        ttsRequest = new TtsRequest(
                            ttsRequest.Provider,
                            randomVoiceId,
                            ttsRequest.Rate,
                            ttsRequest.Volume,
                            ttsRequest.EdgeTimeoutMilliseconds,
                            ttsRequest.FallbackToLocal);
                    }
                }

                if (snapshot.CueEnabled
                    && !string.IsNullOrWhiteSpace(snapshot.CuePath))
                {
                    await cuePlayer.PlayAsync(
                        snapshot.CuePath,
                        cancellation.Token).ConfigureAwait(false);
                }

                if (cancellation.Token.IsCancellationRequested) return;

                await providers.SpeakAsync(
                    pronunciations.Apply(sampleText),
                    ttsRequest,
                    cancellation.Token).ConfigureAwait(false);

                if (cancellation.Token.IsCancellationRequested) return;

                // 朗读完毕后等待“播报后间隔”，再播放后置音（若已配置）。
                if (snapshot.PostCueEnabled
                    && !string.IsNullOrWhiteSpace(snapshot.PostCuePath))
                {
                    if (snapshot.PostCueGapMilliseconds > 0)
                    {
                        await Task.Delay(
                            snapshot.PostCueGapMilliseconds,
                            cancellation.Token).ConfigureAwait(false);
                    }
                    if (cancellation.Token.IsCancellationRequested) return;

                    await cuePlayer.PlayAsync(
                        snapshot.PostCuePath,
                        cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
                when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                LogError("[Voice] sample playback failed.", ex);
            }
            finally
            {
                lock (_stateGate)
                {
                    if (ReferenceEquals(_activeSample, cancellation))
                    {
                        _activeSample = null;
                    }
                }
                cancellation.Dispose();
            }
        }
    }
}
