using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ink_Canvas.Plugins;
using VoicePlugin.Config;

namespace VoicePlugin.Services
{
    /// <summary>
    /// 播报内容预缓存执行器。
    /// <para>
    /// 文本来源：当前名单（无名单则为随机数 1-60），经与真实播报一致的
    /// 模板/单数字/读音展开后，用当前引擎/发音人/语速/音量逐条预合成进缓存。
    /// 互斥执行（新任务取消旧任务）；随机发音人模式下跳过（每次解析出不同
    /// 发音人，预缓存无法命中）；当前引擎不支持缓存（如 SAPI）时跳过。
    /// 任何失败条目静默跳过，播报时仍走实时合成回落，不影响正确性。
    /// </para>
    /// </summary>
    internal sealed class PrecacheService
    {
        private readonly TtsAudioCache _cache;
        private readonly TtsProviderManager _providers;
        private readonly Func<VoiceConfigSnapshot> _getConfig;
        private readonly VoiceTextFormatter _formatter;
        private readonly Action<string> _log;
        private readonly Action<string, Exception> _logError;
        private readonly Action<string, string, Ink_Canvas.Plugins.NotificationLevel> _notify;

        private readonly object _gate = new object();
        private readonly string _rosterFingerprintPath;
        private CancellationTokenSource _activeRun;
        private int _generation;

        public PrecacheService(
            TtsAudioCache cache,
            TtsProviderManager providers,
            Func<VoiceConfigSnapshot> getConfig,
            VoiceTextFormatter formatter,
            string rosterFingerprintPath,
            Action<string> log,
            Action<string, Exception> logError,
            Action<string, string, Ink_Canvas.Plugins.NotificationLevel> notify)
        {
            _cache = cache;
            _providers = providers;
            _getConfig = getConfig;
            _formatter = formatter;
            _rosterFingerprintPath = rosterFingerprintPath;
            _log = log;
            _logError = logError;
            _notify = notify;
        }

        /// <summary>启动预缓存（后台执行；正在运行则取消旧任务后重启）。</summary>
        public void StartPrecache()
        {
            CancellationTokenSource cancellation;
            int generation;
            lock (_gate)
            {
                cancellation = _activeRun;
                _activeRun = CancellationTokenSource.CreateLinkedTokenSource(
                    CancellationToken.None);
                generation = ++_generation;
                cancellation?.Cancel();
                cancellation?.Dispose();
            }

            _ = Task.Run(() => RunPrecacheAsync(_activeRun, generation));
        }

        /// <summary>
        /// 名单变更检测：当前启用名单（Names.txt/Replace.txt）指纹与上次不同
        /// 且开启了“切换名单后清除缓存”时，清空缓存并记录新指纹。
        /// 启动时与每次预缓存前调用。
        /// </summary>
        public void CheckRosterChange()
        {
            try
            {
                var config = _getConfig();
                if (config == null || !config.ClearCacheOnRosterChange) return;

                var fingerprint = PrecacheTextProvider.GetActiveRosterFingerprint();
                if (string.IsNullOrEmpty(fingerprint)) return;

                var stored = string.Empty;
                if (File.Exists(_rosterFingerprintPath))
                {
                    try
                    {
                        stored = File.ReadAllText(_rosterFingerprintPath)?.Trim() ?? string.Empty;
                    }
                    catch
                    {
                    }
                }

                if (string.Equals(stored, fingerprint, StringComparison.Ordinal))
                {
                    return;
                }

                _cache.Clear();
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_rosterFingerprintPath));
                    File.WriteAllText(_rosterFingerprintPath, fingerprint);
                }
                catch (Exception ex)
                {
                    _logError?.Invoke("[Voice] failed to record the roster fingerprint.", ex);
                }
                _log?.Invoke("[Voice] the active roster changed; the audio cache was cleared.");
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] roster change check failed.", ex);
            }
        }

        /// <summary>取消正在进行的预缓存（插件关闭时调用）。</summary>
        public void Stop()
        {
            lock (_gate)
            {
                var active = _activeRun;
                _activeRun = null;
                _generation++;
                try
                {
                    active?.Cancel();
                    active?.Dispose();
                }
                catch
                {
                }
            }
        }

        private async Task RunPrecacheAsync(
            CancellationTokenSource cancellation,
            int generation)
        {
            try
            {
                var config = _getConfig();
                if (config == null || !_cache.IsEnabled)
                {
                    return;
                }

                // 随机发音人模式下预缓存无效：每次播报解析出不同发音人，键无法命中。
                if (string.Equals(
                    config.VoiceId,
                    VoiceConfigSnapshot.RandomVoiceId,
                    StringComparison.OrdinalIgnoreCase))
                {
                    _log?.Invoke("[Voice] precache skipped because the voice is set to random.");
                    return;
                }

                // 当前引擎必须支持缓存（Edge/WinRT）；SAPI 等不支持时跳过。
                var provider = _providers.GetCacheableProvider(config.Provider);
                if (provider is not ITtsCacheableProvider cacheable)
                {
                    _log?.Invoke("[Voice] precache skipped because the current engine does not support audio caching.");
                    return;
                }

                // 切换名单后清除上次缓存（按“当前启用名单”指纹检测）。
                CheckRosterChange();

                var texts = PrecacheTextProvider.BuildPrecacheTexts(
                    config,
                    _formatter,
                    config.PrecacheScope,
                    config.PrecacheRosterGuid);
                if (texts.Count == 0)
                {
                    _log?.Invoke("[Voice] precache skipped because no speech texts were derived.");
                    return;
                }

                _log?.Invoke($"[Voice] precache started: {texts.Count} text(s) with {provider.DisplayName}.");

                var request = config.CreateTtsRequest();
                var cached = 0;
                var synthesized = 0;
                var failed = 0;
                foreach (var text in texts)
                {
                    if (cancellation.IsCancellationRequested || generation != _generation)
                    {
                        break;
                    }

                    var key = new TtsAudioCache.CacheKey(
                        provider.Kind,
                        request.VoiceId,
                        request.Rate,
                        request.Volume,
                        text);
                    if (_cache.TryGet(key, cacheable.CacheExtension, out _, countAsHit: false))
                    {
                        cached++;
                        continue;
                    }

                    try
                    {
                        await _cache.GetOrCreateAsync(
                            key,
                            cacheable.CacheExtension,
                            (path, token) => cacheable.SynthesizeAsync(
                                text,
                                request,
                                path,
                                token),
                            cancellation.Token,
                            countMiss: false).ConfigureAwait(false);
                        synthesized++;
                    }
                    catch (OperationCanceledException)
                        when (cancellation.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logError?.Invoke($"[Voice] precache failed for \"{text}\".", ex);
                    }
                }

                _log?.Invoke(
                    $"[Voice] precache finished: {synthesized} synthesized, {cached} already cached, {failed} failed.");

                if (generation == _generation && !cancellation.IsCancellationRequested)
                {
                    // 开关热生效：通知时以当前配置为准，运行中关闭则不弹提醒。
                    if (_getConfig()?.NotifyPrecacheCompleted ?? true)
                    {
                        _notify?.Invoke(
                            "语音播报",
                            $"播报内容预缓存完成：新缓存 {synthesized} 条，已有 {cached} 条"
                            + (failed > 0 ? $"，失败 {failed} 条（播报时将实时合成）" : string.Empty),
                            Ink_Canvas.Plugins.NotificationLevel.Info);
                    }
                }
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] precache run failed.", ex);
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_activeRun, cancellation))
                    {
                        _activeRun = null;
                    }
                }
                try
                {
                    cancellation.Dispose();
                }
                catch
                {
                }
            }
        }
    }
}
