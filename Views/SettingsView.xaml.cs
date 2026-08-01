using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using VoicePlugin.Config;
using VoicePlugin.Services;

namespace VoicePlugin.Views
{
    public partial class SettingsView : UserControl
    {
        private readonly Func<VoiceConfigSnapshot> _getConfig;
        private readonly Func<VoiceConfigSnapshot, Task<bool>> _saveCallback;
        private readonly Func<TtsProviderKind, bool, CancellationToken,
            Task<IReadOnlyList<TtsVoiceInfo>>> _getVoicesAsync;
        private readonly Func<IReadOnlyList<ProviderAvailability>> _getProviders;
        private readonly VoiceTextFormatter _formatter;
        private readonly Func<VoiceConfigSnapshot, string, Task> _playSampleCallback;
        private readonly Func<bool> _isSpeaking;
        private readonly Action _precacheNow;
        private readonly Action _clearCache;
        private readonly Func<IReadOnlyList<VoiceRosterInfo>> _getRosters;
        private readonly Func<TtsAudioCache.CacheStats?> _getCacheStats;
        private DispatcherTimer _speakingTimer;
        private CancellationTokenSource _voiceRefresh;
        private bool _loading;
        private bool _voiceSelectionConfirmed;

        private static readonly IReadOnlyList<CacheLimitOption> CacheLimitOptions =
            new CacheLimitOption[]
            {
                new CacheLimitOption(AudioCacheLimitMode.Size, "按容量（MB 上限，自动淘汰最旧）"),
                new CacheLimitOption(AudioCacheLimitMode.Date, "按日期（仅保留最近 N 天）"),
                new CacheLimitOption(AudioCacheLimitMode.Unlimited, "不限制")
            };

        public SettingsView(
            Func<VoiceConfigSnapshot> getConfig,
            Func<VoiceConfigSnapshot, Task<bool>> saveCallback,
            Func<TtsProviderKind, bool, CancellationToken,
                Task<IReadOnlyList<TtsVoiceInfo>>> getVoicesAsync,
            Func<IReadOnlyList<ProviderAvailability>> getProviders,
            VoiceTextFormatter formatter,
            Func<VoiceConfigSnapshot, string, Task> playSampleCallback,
            Func<bool> isSpeaking,
            Action precacheNow,
            Action clearCache,
            Func<IReadOnlyList<VoiceRosterInfo>> getRosters,
            Func<TtsAudioCache.CacheStats?> getCacheStats)
        {
            InitializeComponent();
            _getConfig = getConfig ?? throw new ArgumentNullException(nameof(getConfig));
            _saveCallback = saveCallback ?? throw new ArgumentNullException(nameof(saveCallback));
            _getVoicesAsync = getVoicesAsync ?? throw new ArgumentNullException(nameof(getVoicesAsync));
            _getProviders = getProviders ?? throw new ArgumentNullException(nameof(getProviders));
            _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
            _playSampleCallback = playSampleCallback
                ?? throw new ArgumentNullException(nameof(playSampleCallback));
            _isSpeaking = isSpeaking ?? throw new ArgumentNullException(nameof(isSpeaking));
            _precacheNow = precacheNow ?? throw new ArgumentNullException(nameof(precacheNow));
            _clearCache = clearCache ?? throw new ArgumentNullException(nameof(clearCache));
            _getRosters = getRosters ?? throw new ArgumentNullException(nameof(getRosters));
            _getCacheStats = getCacheStats ?? throw new ArgumentNullException(nameof(getCacheStats));
            // 注意：定时器在 Loaded（UI 线程）时才创建——宿主可能在后台线程
            // 调用 GetSettingsView，构造时创建的 DispatcherTimer 会挂在
            // 无消息循环的线程上永不触发。
            PronunciationRules = new ObservableCollection<PronunciationRuleRow>();
            DataContext = this;
            Loaded += SettingsView_Loaded;
            Unloaded += SettingsView_Unloaded;
            LoadSettings();
        }

        public ObservableCollection<PronunciationRuleRow> PronunciationRules { get; }

        private void LoadSettings()
        {
            var config = _getConfig();
            _loading = true;
            EnableRandomPickTtsToggle.IsChecked = config.Enabled;
            ShowTrayToggleToggle.IsChecked = config.ShowTrayToggle;
            NotifyOnStopToggle.IsChecked = config.NotifyOnStop;
            EnableAudioCacheToggle.IsChecked = config.EnableAudioCache;
            CacheLimitModeComboBox.ItemsSource = CacheLimitOptions;
            CacheLimitModeComboBox.SelectedItem = CacheLimitOptions.FirstOrDefault(
                option => option.Mode == config.CacheLimitMode)
                ?? CacheLimitOptions[0];
            AudioCacheSizeSlider.Value = config.AudioCacheSizeMb;
            AudioCacheRetentionSlider.Value = config.AudioCacheRetentionDays;
            PopulatePrecacheScopeCombo(config);
            ClearCacheOnRosterChangeToggle.IsChecked = config.ClearCacheOnRosterChange;
            PrecacheOnStartupToggle.IsChecked = config.PrecacheOnStartup;
            NotifyPrecacheCompletedToggle.IsChecked = config.NotifyPrecacheCompleted;
            EnableCacheStatsToggle.IsChecked = config.EnableCacheStats;
            EnableStopHotkeyToggle.IsChecked = config.EnableStopHotkey;
            ProviderComboBox.ItemsSource = BuildProviderChoices();
            ProviderComboBox.SelectedValue = config.Provider;
            RateSlider.Value = config.Rate;
            VolumeSlider.Value = config.Volume;
            SpeechTemplateTextBox.Text = config.SpeechTemplate;
            DelaySlider.Value = config.DelayMilliseconds;
            SpaceNumericDigitsToggle.IsChecked = config.SpaceNumericDigits;
            EnablePronunciationsToggle.IsChecked = config.EnablePronunciations;
            CueEnabledToggle.IsChecked = config.CueEnabled;
            CuePathTextBox.Text = config.CuePath;
            CueGapSlider.Value = config.CueGapMilliseconds;
            PostCueEnabledToggle.IsChecked = config.PostCueEnabled;
            PostCuePathTextBox.Text = config.PostCuePath;
            PostCueGapSlider.Value = config.PostCueGapMilliseconds;
            EdgeTimeoutSlider.Value = config.EdgeTimeoutMilliseconds;
            EdgeFallbackToggle.IsChecked = config.EdgeFallbackToLocal;

            PronunciationRules.Clear();
            foreach (var rule in config.Pronunciations)
            {
                var row = new PronunciationRuleRow
                {
                    Original = rule.Original,
                    Replacement = rule.Replacement
                };
                SubscribeRow(row);
                PronunciationRules.Add(row);
            }

            _loading = false;
            UpdateControlStates();
            UpdatePreview();
        }

        /// <summary>
        /// 外部（托盘菜单、自动化行动等）修改配置后，把设置页控件与最新配置对齐。
        /// 带 _loading 保护，不会触发反向保存；引擎/发音人变化时刷新语音列表
        /// （并兜底加入“已配置的发音人”，避免外部设置的发音人显示丢失）。
        /// 可从任意线程调用。
        /// </summary>
        internal void RefreshFromConfig()
        {
            var dispatcher = Dispatcher;
            if (dispatcher == null || !dispatcher.CheckAccess())
            {
                dispatcher?.BeginInvoke(new Action(RefreshFromConfig));
                return;
            }

            if (!IsLoaded) return;
            var config = _getConfig();

            // 全部控件与引擎/发音人同步都在 _loading 保护内，
            // 抑制 SelectionChanged 引发的重复刷新与多余保存。
            _loading = true;
            try
            {
                EnableRandomPickTtsToggle.IsChecked = config.Enabled;
                ShowTrayToggleToggle.IsChecked = config.ShowTrayToggle;
                NotifyOnStopToggle.IsChecked = config.NotifyOnStop;
                EnableAudioCacheToggle.IsChecked = config.EnableAudioCache;
                CacheLimitModeComboBox.SelectedItem = CacheLimitOptions.FirstOrDefault(
                    option => option.Mode == config.CacheLimitMode)
                    ?? CacheLimitOptions[0];
                AudioCacheSizeSlider.Value = config.AudioCacheSizeMb;
                AudioCacheRetentionSlider.Value = config.AudioCacheRetentionDays;
                PopulatePrecacheScopeCombo(config);
                ClearCacheOnRosterChangeToggle.IsChecked = config.ClearCacheOnRosterChange;
                PrecacheOnStartupToggle.IsChecked = config.PrecacheOnStartup;
                NotifyPrecacheCompletedToggle.IsChecked = config.NotifyPrecacheCompleted;
                EnableCacheStatsToggle.IsChecked = config.EnableCacheStats;
                EnableStopHotkeyToggle.IsChecked = config.EnableStopHotkey;
                SpaceNumericDigitsToggle.IsChecked = config.SpaceNumericDigits;
                EnablePronunciationsToggle.IsChecked = config.EnablePronunciations;
                CueEnabledToggle.IsChecked = config.CueEnabled;
                CuePathTextBox.Text = config.CuePath;
                CueGapSlider.Value = config.CueGapMilliseconds;
                PostCueEnabledToggle.IsChecked = config.PostCueEnabled;
                PostCuePathTextBox.Text = config.PostCuePath;
                PostCueGapSlider.Value = config.PostCueGapMilliseconds;
                RateSlider.Value = config.Rate;
                VolumeSlider.Value = config.Volume;
                DelaySlider.Value = config.DelayMilliseconds;
                SpeechTemplateTextBox.Text = config.SpeechTemplate;
                EdgeTimeoutSlider.Value = config.EdgeTimeoutMilliseconds;
                EdgeFallbackToggle.IsChecked = config.EdgeFallbackToLocal;

                // 引擎变化：切换选项并刷新语音列表（_loading 内，走手动刷新路径）。
                var providerChanged = GetSelectedProvider() != config.Provider;
                if (providerChanged)
                {
                    _voiceSelectionConfirmed = false;
                    ProviderComboBox.SelectedValue = config.Provider;
                    _ = RefreshVoicesAsync(config.Provider, config.VoiceId, false);
                }
                else if (!_voiceSelectionConfirmed
                    && !string.Equals(
                        VoiceComboBox.SelectedValue as string,
                        config.VoiceId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    // 引擎未变但发音人可能是外部改的：当前列表不含该发音人时刷新列表，
                    // 否则直接选中（避免下次保存把外部设置的发音人冲掉）。
                    var configuredVoiceInList = VoiceComboBox.Items.Cast<TtsVoiceInfo>()
                        .Any(voice => string.Equals(
                            voice.Id,
                            config.VoiceId,
                            StringComparison.OrdinalIgnoreCase));
                    if (configuredVoiceInList)
                    {
                        VoiceComboBox.SelectedValue = config.VoiceId;
                    }
                    else
                    {
                        _ = RefreshVoicesAsync(config.Provider, config.VoiceId, false);
                    }
                }
            }
            finally
            {
                _loading = false;
            }

            UpdateControlStates();
            UpdatePreview();
        }

        private async void SettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            var config = _getConfig();
            await RefreshVoicesAsync(config.Provider, config.VoiceId, false);
            UpdateControlStates();
            UpdatePreview();

            if (_speakingTimer == null)
            {
                // Loaded 一定在 UI 线程触发，此时创建定时器才可靠。
                _speakingTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(500)
                };
                _speakingTimer.Tick += SpeakingTimer_Tick;
            }
            _speakingTimer.Start();
        }

        private void SettingsView_Unloaded(object sender, RoutedEventArgs e)
        {
            _speakingTimer.Stop();
            CancelVoiceRefresh();
        }

        private void SpeakingTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                // 正式播报进行中时禁用试听按钮，避免示例顶掉真实播报。
                PlaySampleButton.IsEnabled = !_isSpeaking();
            }
            catch (Exception)
            {
                // 轮询失败时保持按钮可用，不阻塞设置页。
                PlaySampleButton.IsEnabled = true;
            }
        }

        private IReadOnlyList<ProviderChoice> BuildProviderChoices()
        {
            var availability = _getProviders();
            var byKind = availability.ToDictionary(item => item.Provider);
            return Enum.GetValues<TtsProviderKind>()
                .Select(provider =>
                {
                    byKind.TryGetValue(provider, out var item);
                    return new ProviderChoice(
                        provider,
                        item?.DisplayName ?? GetProviderName(provider),
                        item?.IsAvailable ?? provider != TtsProviderKind.WinRt,
                        item?.Detail ?? string.Empty);
                })
                .ToArray();
        }

        private async Task RefreshVoicesAsync(
            TtsProviderKind provider,
            string configuredVoiceId,
            bool forceRefresh)
        {
            CancelVoiceRefresh();
            _voiceRefresh = new CancellationTokenSource();
            var cancellationToken = _voiceRefresh.Token;
            var selectedId = _voiceSelectionConfirmed
                ? VoiceComboBox.SelectedValue as string ?? string.Empty
                : configuredVoiceId ?? string.Empty;

            VoiceComboBox.IsEnabled = false;
            RefreshVoicesButton.IsEnabled = false;
            VoiceHintText.Text = provider == TtsProviderKind.EdgeOnline
                ? "正在获取在线语音列表…"
                : "正在读取本地语音列表…";

            try
            {
                var installed = await _getVoicesAsync(
                    provider,
                    forceRefresh,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                var voices = new List<TtsVoiceInfo>
                {
                    new TtsVoiceInfo(
                        string.Empty,
                        provider == TtsProviderKind.EdgeOnline
                            ? "自动（优先中文在线语音）"
                            : "自动（优先中文）",
                        provider,
                        string.Empty,
                        provider == TtsProviderKind.EdgeOnline),
                    new TtsVoiceInfo(
                        VoiceConfigSnapshot.RandomVoiceId,
                        "随机（每次播报随机选择发音人）",
                        provider,
                        string.Empty,
                        false)
                };
                if (installed != null)
                {
                    voices.AddRange(installed
                        .Where(voice => voice != null
                            && !string.IsNullOrWhiteSpace(voice.Id))
                        .GroupBy(voice => voice.Id, StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.First()));
                }

                if (!string.IsNullOrWhiteSpace(selectedId)
                    && voices.All(voice => !string.Equals(
                        voice.Id,
                        selectedId,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    voices.Add(new TtsVoiceInfo(
                        selectedId,
                        "已配置的发音人（当前不可用）",
                        provider));
                }

                _loading = true;
                VoiceComboBox.ItemsSource = voices;
                VoiceComboBox.SelectedItem = voices.FirstOrDefault(voice =>
                        string.Equals(
                            voice.Id,
                            selectedId,
                            StringComparison.OrdinalIgnoreCase))
                    ?? voices[0];
                _loading = false;

                var realVoiceCount = voices.Count(voice =>
                    !string.IsNullOrWhiteSpace(voice.Id)
                    && !string.Equals(
                        voice.Id,
                        VoiceConfigSnapshot.RandomVoiceId,
                        StringComparison.OrdinalIgnoreCase));
                VoiceHintText.Text = realVoiceCount > 0
                    ? $"检测到 {realVoiceCount} 个可选发音人。"
                    : provider == TtsProviderKind.WinRt
                        ? "当前宿主没有 Windows Media TTS 所需的应用包身份。"
                        : "暂未检测到可选发音人，将使用自动选择。";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                VoiceHintText.Text = "语音列表加载失败：" + ex.Message;
            }
            finally
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    VoiceComboBox.IsEnabled = true;
                    RefreshVoicesButton.IsEnabled = true;
                }
            }
        }

        private async void ProviderComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (_loading || !IsLoaded) return;
            _voiceSelectionConfirmed = false;
            UpdateControlStates();
            UpdatePreview();
            var provider = GetSelectedProvider();
            await RefreshVoicesAsync(provider, string.Empty, true);
            ApplyAndScheduleSave();
        }

        private async void RefreshVoicesButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            await RefreshVoicesAsync(
                GetSelectedProvider(),
                VoiceComboBox.SelectedValue as string ?? string.Empty,
                true);
        }

        private void OnSettingChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || !IsLoaded) return;

            if (ReferenceEquals(sender, VoiceComboBox))
            {
                _voiceSelectionConfirmed = true;
            }

            UpdateControlStates();
            UpdatePreview();
            ApplyAndScheduleSave();
        }

        private void UpdateControlStates()
        {
            var enabled = EnableRandomPickTtsToggle.IsChecked ?? false;
            if (SettingsContentPanel != null)
            {
                foreach (var child in SettingsContentPanel.Children.OfType<UIElement>())
                {
                    child.IsEnabled = enabled;
                }
            }
            var provider = GetSelectedProvider();
            var isEdge = provider == TtsProviderKind.EdgeOnline;
            var isWinRt = provider == TtsProviderKind.WinRt;
            EdgeWarningPanel.Visibility = isEdge
                ? Visibility.Visible
                : Visibility.Collapsed;
            EdgeOptionsPanel.Visibility = isEdge
                ? Visibility.Visible
                : Visibility.Collapsed;
            FallbackOptionsPanel.Visibility = isEdge || isWinRt
                ? Visibility.Visible
                : Visibility.Collapsed;

            var providerChoice = ProviderComboBox.SelectedItem as ProviderChoice;
            ProviderHintText.Text = providerChoice == null
                ? string.Empty
                : providerChoice.IsAvailable
                    ? providerChoice.Detail
                    : string.IsNullOrWhiteSpace(providerChoice.Detail)
                        ? "当前环境中无法使用此引擎；播报时将回退到 SAPI。"
                        : providerChoice.Detail;

            CueOptionsPanel.IsEnabled = CueEnabledToggle.IsChecked ?? false;
            PostCueOptionsPanel.IsEnabled = PostCueEnabledToggle.IsChecked ?? false;
            AudioCacheOptionsPanel.IsEnabled = EnableAudioCacheToggle.IsChecked ?? false;

            var cacheEnabled = EnableAudioCacheToggle.IsChecked ?? false;
            var limitMode = (CacheLimitModeComboBox.SelectedItem as CacheLimitOption)?.Mode
                ?? AudioCacheLimitMode.Size;
            CacheSizeLabel.IsEnabled = cacheEnabled && limitMode == AudioCacheLimitMode.Size;
            AudioCacheSizeSlider.IsEnabled = cacheEnabled && limitMode == AudioCacheLimitMode.Size;
            CacheRetentionLabel.IsEnabled = cacheEnabled && limitMode == AudioCacheLimitMode.Date;
            AudioCacheRetentionSlider.IsEnabled = cacheEnabled && limitMode == AudioCacheLimitMode.Date;


            var statsEnabled = EnableCacheStatsToggle.IsChecked ?? false;
            CacheStatsPanel.Visibility = statsEnabled
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (statsEnabled)
            {
                RefreshCacheStats();
            }

            var pronunciationsEnabled = EnablePronunciationsToggle.IsChecked ?? false;
            AddPronunciationButton.IsEnabled = pronunciationsEnabled;
            PronunciationList.IsEnabled = pronunciationsEnabled;
            EmptyPronunciationHint.Visibility = PronunciationRules.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void UpdatePreview()
        {
            var config = BuildSnapshot();
            var previews = BuildPreviews(config);
            var dictionary = new PronunciationDictionary(
                config.EnablePronunciations
                    ? config.Pronunciations
                    : null);
            PreviewText.Text = previews.Count == 0
                ? "（模板没有可播报内容）"
                : string.Join(
                    Environment.NewLine,
                    previews.Select(dictionary.Apply));
        }

        private IReadOnlyList<string> BuildPreviews(VoiceConfigSnapshot config)
        {
            return _formatter.FormatBatch(
                new[] { "张三", "2024" },
                config);
        }

        private void ApplyAndScheduleSave()
        {
            // 设置自动保存（宿主负责持久化与错误日志），这里只负责触发。
            _ = ApplyAndSaveAsync(BuildSnapshot());
        }

        private async Task ApplyAndSaveAsync(VoiceConfigSnapshot snapshot)
        {
            try
            {
                await _saveCallback(snapshot).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 保存失败已由宿主记录到插件日志，设置页无需额外提示。
            }
        }

        /// <summary>
        /// 填充“预缓存内容”下拉：当前启用名单 / 全部名单 / 软件内各名单方案。
        /// </summary>
        private void PopulatePrecacheScopeCombo(VoiceConfigSnapshot config)
        {
            var options = new List<PrecacheScopeOption>
            {
                new PrecacheScopeOption("当前启用名单", PrecacheScopeMode.Current, string.Empty),
                new PrecacheScopeOption("全部名单", PrecacheScopeMode.All, string.Empty)
            };
            foreach (var roster in _getRosters() ?? Array.Empty<VoiceRosterInfo>())
            {
                if (string.IsNullOrWhiteSpace(roster.Name)) continue;
                options.Add(new PrecacheScopeOption(
                    roster.Name,
                    PrecacheScopeMode.Roster,
                    roster.Guid ?? string.Empty));
            }

            PrecacheScopeComboBox.ItemsSource = options;
            PrecacheScopeComboBox.SelectedItem = options.FirstOrDefault(option =>
                option.Mode == config.PrecacheScope
                && (option.Mode != PrecacheScopeMode.Roster
                    || string.Equals(option.Guid, config.PrecacheRosterGuid, StringComparison.OrdinalIgnoreCase)))
                ?? options[0];
        }

        private async void ResetSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var defaults = new VoiceConfigSnapshot(new VoiceConfig());
                await _saveCallback(defaults);
                LoadSettings();

                // 发音人列表按默认引擎/发音人重建，避免下一次保存回写旧值。
                var config = _getConfig();
                await RefreshVoicesAsync(config.Provider, config.VoiceId, false);
            }
            catch (Exception)
            {
                // 恢复失败由插件记录日志。
            }
        }

        private void PrecacheNowButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _precacheNow();
            }
            catch (Exception)
            {
                // 预缓存启动失败由插件记录日志。
            }
        }

        private void ClearCacheButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _clearCache();
                RefreshCacheStats();
            }
            catch (Exception)
            {
                // 清空缓存失败由插件记录日志。
            }
        }

        private void RefreshCacheStatsButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshCacheStats();
        }

        private void RefreshCacheStats()
        {
            try
            {
                var stats = _getCacheStats();
                if (stats == null)
                {
                    CacheHitRateText.Text = "--";
                    CacheSizeText.Text = "--";
                    CacheHitMissText.Text = "--";
                    CacheEntryText.Text = "--";
                    return;
                }

                var value = stats.Value;
                CacheHitRateText.Text = (value.HitRate * 100).ToString("0.0") + " %";
                CacheSizeText.Text = FormatBytes(value.TotalBytes);
                CacheHitMissText.Text = $"{value.Hits} / {value.Misses}";
                CacheEntryText.Text = $"{value.EntryCount} 条 / {FormatBytes(value.BytesServed)}";
            }
            catch (Exception)
            {
                // 统计读取失败时保持现有显示。
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
        }

        private async void PlaySampleButton_Click(object sender, RoutedEventArgs e)
        {
            var snapshot = BuildSnapshot();
            var sampleText = BuildSampleText(snapshot);
            if (string.IsNullOrWhiteSpace(sampleText)) return;

            try
            {
                await _playSampleCallback(snapshot, sampleText).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 试听失败已由插件记录日志。
            }
        }

        private string BuildSampleText(VoiceConfigSnapshot snapshot)
        {
            var previews = BuildPreviews(snapshot);
            if (previews.Count == 0) return string.Empty;

            return new PronunciationDictionary(
                snapshot.EnablePronunciations
                    ? snapshot.Pronunciations
                    : null).Apply(previews[0]);
        }

        private void BrowseCueButton_Click(object sender, RoutedEventArgs e)
        {
            BrowseAudioFile(CuePathTextBox, "选择播报前奏音");
        }

        private void BrowsePostCueButton_Click(object sender, RoutedEventArgs e)
        {
            BrowseAudioFile(PostCuePathTextBox, "选择播报后置音");
        }

        private static void BrowseAudioFile(TextBox target, string title)
        {
            var dialog = new OpenFileDialog
            {
                Title = title,
                Filter = "支持的音频|*.wav;*.mp3;*.flac|WAV 音频|*.wav|MP3 音频|*.mp3|FLAC 音频|*.flac|所有文件|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog() == true)
            {
                target.Text = dialog.FileName;
            }
        }

        private void AddPronunciationButton_Click(object sender, RoutedEventArgs e)
        {
            if (PronunciationRules.Count >= 100) return;
            var row = new PronunciationRuleRow();
            SubscribeRow(row);
            PronunciationRules.Add(row);
            PronunciationList.SelectedItem = row;
            UpdateControlStates();
            ApplyAndScheduleSave();
        }

        private void RemovePronunciationButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PronunciationRuleRow row)
            {
                return;
            }
            UnsubscribeRow(row);
            PronunciationRules.Remove(row);
            UpdateControlStates();
            UpdatePreview();
            ApplyAndScheduleSave();
        }

        private void MovePronunciationUp_Click(object sender, RoutedEventArgs e)
        {
            MoveRule(sender as FrameworkElement, -1);
        }

        private void MovePronunciationDown_Click(object sender, RoutedEventArgs e)
        {
            MoveRule(sender as FrameworkElement, 1);
        }

        private void MoveRule(FrameworkElement sender, int offset)
        {
            if (sender?.DataContext is not PronunciationRuleRow row) return;
            var index = PronunciationRules.IndexOf(row);
            var destination = index + offset;
            if (index < 0 || destination < 0 || destination >= PronunciationRules.Count) return;
            PronunciationRules.Move(index, destination);
            PronunciationList.SelectedItem = row;
            UpdatePreview();
            ApplyAndScheduleSave();
        }

        private void PronunciationTextBox_TextChanged(
            object sender,
            TextChangedEventArgs e)
        {
            if (_loading || !IsLoaded) return;
            UpdatePreview();
            ApplyAndScheduleSave();
        }

        #region 读音规则拖拽排序

        private Point _dragStartPoint;
        private PronunciationRuleRow _dragCandidate;
        private bool _dragArmed;

        private void PronunciationList_PreviewMouseLeftButtonDown(
            object sender, MouseButtonEventArgs e)
        {
            _dragCandidate = null;
            _dragArmed = false;

            // 从按下位置向上找到所在的列表项，作为拖拽候选。
            var source = e.OriginalSource as DependencyObject;
            while (source != null && source is not ListBoxItem)
            {
                source = VisualTreeHelper.GetParent(source);
            }
            if (source is ListBoxItem container
                && container.DataContext is PronunciationRuleRow row)
            {
                _dragCandidate = row;
                _dragStartPoint = e.GetPosition(PronunciationList);
                _dragArmed = true;
            }
        }

        private void PronunciationList_PreviewMouseMove(
            object sender, MouseEventArgs e)
        {
            if (!_dragArmed || _dragCandidate == null
                || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            var position = e.GetPosition(PronunciationList);
            if (Math.Abs(position.X - _dragStartPoint.X)
                    < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(position.Y - _dragStartPoint.Y)
                    < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _dragArmed = false;
            DragDrop.DoDragDrop(
                PronunciationList,
                new DataObject(_dragCandidate),
                DragDropEffects.Move);
        }

        private void PronunciationList_PreviewMouseLeftButtonUp(
            object sender, MouseButtonEventArgs e)
        {
            _dragArmed = false;
            _dragCandidate = null;
        }

        private void PronunciationList_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(typeof(PronunciationRuleRow))
                    is not PronunciationRuleRow dragged)
            {
                return;
            }

            var fromIndex = PronunciationRules.IndexOf(dragged);
            if (fromIndex < 0) return;

            var targetIndex = GetDropIndex(e.GetPosition(PronunciationList));
            if (targetIndex > fromIndex) targetIndex--;
            if (targetIndex == fromIndex) return;

            PronunciationRules.Move(fromIndex, targetIndex);
            PronunciationList.SelectedItem = dragged;
            UpdatePreview();
            ApplyAndScheduleSave();
        }

        /// <summary>按落点纵坐标换算目标插入位置（落在项的上半区即插入其前）。</summary>
        private int GetDropIndex(Point position)
        {
            var index = 0;
            foreach (var item in PronunciationList.Items)
            {
                if (PronunciationList.ItemContainerGenerator
                        .ContainerFromItem(item) is ListBoxItem container)
                {
                    var top = container.TransformToAncestor(PronunciationList)
                        .Transform(new Point(0, 0)).Y;
                    if (position.Y < top + container.ActualHeight / 2)
                    {
                        return index;
                    }
                }
                index++;
            }
            return index;
        }

        #endregion

        private void SubscribeRow(PronunciationRuleRow row)
        {
            row.PropertyChanged += Rule_PropertyChanged;
        }

        private void UnsubscribeRow(PronunciationRuleRow row)
        {
            row.PropertyChanged -= Rule_PropertyChanged;
        }

        private void Rule_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_loading || !IsLoaded) return;
            UpdatePreview();
        }

        private VoiceConfigSnapshot BuildSnapshot()
        {
            return new VoiceConfigSnapshot(new VoiceConfig
            {
                SchemaVersion = VoiceConfigSnapshot.CurrentSchemaVersion,
                Enabled = EnableRandomPickTtsToggle.IsChecked ?? false,
                ShowTrayToggle = ShowTrayToggleToggle.IsChecked ?? true,
                NotifyOnStop = NotifyOnStopToggle.IsChecked ?? true,
                Provider = GetSelectedProvider().ToString(),
                Rate = (int)Math.Round(RateSlider.Value),
                Volume = (int)Math.Round(VolumeSlider.Value),
                VoiceId = VoiceComboBox.SelectedValue as string ?? string.Empty,
                SpeechTemplate = SpeechTemplateTextBox.Text ?? string.Empty,
                DelayMilliseconds = (int)Math.Round(DelaySlider.Value),
                SpaceNumericDigits = SpaceNumericDigitsToggle.IsChecked ?? false,
                EnablePronunciations = EnablePronunciationsToggle.IsChecked ?? false,
                Cue = new CueConfig
                {
                    Enabled = CueEnabledToggle.IsChecked ?? false,
                    Path = CuePathTextBox.Text ?? string.Empty,
                    GapMilliseconds = (int)Math.Round(CueGapSlider.Value)
                },
                PostCue = new PostCueConfig
                {
                    Enabled = PostCueEnabledToggle.IsChecked ?? false,
                    Path = PostCuePathTextBox.Text ?? string.Empty,
                    GapMilliseconds = (int)Math.Round(PostCueGapSlider.Value)
                },
                EnableAudioCache = EnableAudioCacheToggle.IsChecked ?? true,
                CacheLimitMode = (CacheLimitModeComboBox.SelectedItem as CacheLimitOption)?.Mode
                    ?? AudioCacheLimitMode.Size,
                AudioCacheSizeMb = (int)Math.Round(AudioCacheSizeSlider.Value),
                AudioCacheRetentionDays = (int)Math.Round(AudioCacheRetentionSlider.Value),
                PrecacheScope = (PrecacheScopeComboBox.SelectedItem as PrecacheScopeOption)?.Mode
                    ?? PrecacheScopeMode.Current,
                PrecacheRosterGuid = (PrecacheScopeComboBox.SelectedItem as PrecacheScopeOption)?.Guid
                    ?? string.Empty,
                ClearCacheOnRosterChange = ClearCacheOnRosterChangeToggle.IsChecked ?? true,
                PrecacheOnStartup = PrecacheOnStartupToggle.IsChecked ?? true,
                NotifyPrecacheCompleted = NotifyPrecacheCompletedToggle.IsChecked ?? true,
                EnableCacheStats = EnableCacheStatsToggle.IsChecked ?? false,
                EnableStopHotkey = EnableStopHotkeyToggle.IsChecked ?? true,
                Edge = new EdgeConfig
                {
                    TimeoutMilliseconds = (int)Math.Round(EdgeTimeoutSlider.Value),
                    FallbackToLocal = EdgeFallbackToggle.IsChecked ?? true
                },
                Pronunciations = PronunciationRules.Select(row =>
                    new PronunciationRuleConfig
                    {
                        Original = row.Original,
                        Replacement = row.Replacement
                    }).ToList()
            });
        }

        private TtsProviderKind GetSelectedProvider()
        {
            if (ProviderComboBox.SelectedValue is TtsProviderKind provider)
            {
                return provider;
            }
            return _getConfig().Provider;
        }

        private void CancelVoiceRefresh()
        {
            var cancellation = _voiceRefresh;
            _voiceRefresh = null;
            if (cancellation == null) return;
            cancellation.Cancel();
            cancellation.Dispose();
        }

        private static string GetProviderName(TtsProviderKind provider)
        {
            return provider switch
            {
                TtsProviderKind.Sapi => "Windows SAPI（本地）",
                TtsProviderKind.WinRt => "Windows Media（本地）",
                TtsProviderKind.EdgeOnline => "Edge 在线语音（非官方）",
                _ => provider.ToString()
            };
        }

        private sealed record CacheLimitOption(AudioCacheLimitMode Mode, string Label);

        private sealed record PrecacheScopeOption(
            string Label,
            PrecacheScopeMode Mode,
            string Guid);

        private sealed class ProviderChoice
        {
            public ProviderChoice(
                TtsProviderKind provider,
                string displayName,
                bool isAvailable,
                string detail)
            {
                Provider = provider;
                DisplayName = isAvailable
                    ? displayName
                    : displayName + " · 当前不可用";
                IsAvailable = isAvailable;
                Detail = detail ?? string.Empty;
            }

            public TtsProviderKind Provider { get; }
            public string DisplayName { get; }
            public bool IsAvailable { get; }
            public string Detail { get; }
        }
    }
}
