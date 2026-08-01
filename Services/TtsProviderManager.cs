using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VoicePlugin.Services
{
    public sealed class TtsProviderManager
    {
        private static readonly TimeSpan VoiceCacheLifetime =
            TimeSpan.FromMinutes(10);

        private readonly IReadOnlyDictionary<TtsProviderKind, ITtsProvider> _providers;
        private readonly Action<string> _log;
        private readonly Action<string, Exception> _logError;
        private readonly object _gate = new object();
        private readonly Dictionary<TtsProviderKind, CachedVoices> _voiceCache
            = new Dictionary<TtsProviderKind, CachedVoices>();
        private CancellationTokenSource _active;
        private bool _disposed;

        public TtsProviderManager(
            IEnumerable<ITtsProvider> providers,
            Action<string> log = null,
            Action<string, Exception> logError = null)
        {
            if (providers == null) throw new ArgumentNullException(nameof(providers));
            _providers = providers.ToDictionary(provider => provider.Kind);
            _log = log;
            _logError = logError;
        }

        /// <summary>返回支持音频缓存（<see cref="ITtsCacheableProvider"/>）的引擎实现；不支持返回 null。</summary>
        public ITtsProvider GetCacheableProvider(TtsProviderKind kind)
        {
            return _providers.TryGetValue(kind, out var provider)
                && provider is ITtsCacheableProvider
                    ? provider
                    : null;
        }

        public IReadOnlyList<ProviderAvailability> ProviderAvailability =>
            Enum.GetValues<TtsProviderKind>()
                .Select(provider =>
                {
                    if (_providers.TryGetValue(provider, out var implementation))
                    {
                        return new ProviderAvailability(
                            provider,
                            implementation.DisplayName,
                            implementation.IsAvailable,
                            provider == TtsProviderKind.WinRt && !implementation.IsAvailable
                                ? "当前宿主没有 WinRT 语音所需的应用包身份。"
                                : provider == TtsProviderKind.WinRt
                                    ? "使用 Windows.Media.SpeechSynthesis 本地语音。"
                                    : provider == TtsProviderKind.Sapi && !implementation.IsAvailable
                                        ? "系统 SAPI 组件不可用。"
                                        : string.Empty);
                    }

                    return new ProviderAvailability(
                        provider,
                        GetDisplayName(provider),
                        false,
                        "该引擎未加载。");
                })
                .ToArray();

        public async Task<IReadOnlyList<TtsVoiceInfo>> GetVoicesAsync(
            TtsProviderKind provider,
            bool refreshOnline,
            CancellationToken cancellationToken)
        {
            if (!_providers.TryGetValue(provider, out var implementation))
            {
                return Array.Empty<TtsVoiceInfo>();
            }

            // Sapi 引擎初始化是异步的（专用线程），其 GetVoicesAsync 内部自带
            // 就绪轮询（最多约 1 秒），因此不在此提前拦截，让轮询吸收初始化
            // 窗口；其余引擎的可用性探测代价高（WinRt 惰性探测）或恒可用
            // （Edge），不可用时直接返回空。
            if (provider != TtsProviderKind.Sapi && !implementation.IsAvailable)
            {
                return Array.Empty<TtsVoiceInfo>();
            }

            try
            {
                var voices = await implementation.GetVoicesAsync(cancellationToken)
                    .ConfigureAwait(false);

                // 成功拉取后顺手写入“随机发音人”共享缓存：
                // 设置页查看发音人列表即完成预热，避免随机播报首次联网拉取。
                if (voices != null)
                {
                    lock (_gate)
                    {
                        if (!_disposed)
                        {
                            _voiceCache[provider] = new CachedVoices(
                                voices,
                                DateTimeOffset.UtcNow);
                        }
                    }
                }
                return voices;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logError?.Invoke($"[Voice] failed to enumerate {implementation.DisplayName} voices.", ex);
                return Array.Empty<TtsVoiceInfo>();
            }
        }

        /// <summary>
        /// 为“随机发音人”解析一个具体的发音人 id。
        /// 优先从中文发音人中随机挑选；没有任何可用发音人时返回空串
        /// （由各提供器自行选择默认发音人）。
        /// </summary>
        public async Task<string> ResolveRandomVoiceAsync(
            TtsProviderKind provider,
            CancellationToken cancellationToken)
        {
            try
            {
                var voices = await GetCachedVoicesAsync(
                    provider,
                    cancellationToken).ConfigureAwait(false);
                var candidates = voices
                    .Where(voice => voice != null
                        && !string.IsNullOrWhiteSpace(voice.Id))
                    .ToArray();
                if (candidates.Length == 0) return string.Empty;

                var chinese = candidates
                    .Where(voice => voice.Language?.StartsWith(
                        "zh",
                        StringComparison.OrdinalIgnoreCase) == true)
                    .ToArray();
                var pool = chinese.Length > 0 ? chinese : candidates;
                return pool[Random.Shared.Next(pool.Length)].Id;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] failed to resolve a random voice.", ex);
                return string.Empty;
            }
        }

        private async Task<IReadOnlyList<TtsVoiceInfo>> GetCachedVoicesAsync(
            TtsProviderKind provider,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (!_disposed
                    && _voiceCache.TryGetValue(provider, out var cached)
                    && DateTimeOffset.UtcNow - cached.Timestamp
                        < VoiceCacheLifetime)
                {
                    return cached.Voices;
                }
            }

            var voices = await GetVoicesAsync(
                provider,
                false,
                cancellationToken).ConfigureAwait(false)
                ?? Array.Empty<TtsVoiceInfo>();

            lock (_gate)
            {
                if (!_disposed)
                {
                    _voiceCache[provider] = new CachedVoices(
                        voices,
                        DateTimeOffset.UtcNow);
                }
            }
            return voices;
        }

        public async Task SpeakAsync(
            string text,
            TtsRequest request,
            CancellationToken cancellationToken)
        {
            CancellationTokenSource operation;
            lock (_gate)
            {
                ThrowIfDisposed();
                CancelActiveLocked();
                operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _active = operation;
            }

            try
            {
                if (request.Provider == TtsProviderKind.EdgeOnline)
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                            operation.Token);
                        timeout.CancelAfter(request.EdgeTimeoutMilliseconds);
                        await SpeakWithProviderAsync(
                            TtsProviderKind.EdgeOnline,
                            text,
                            request,
                            timeout.Token).ConfigureAwait(false);
                        return;
                    }
                    catch (OperationCanceledException)
                        when (!operation.IsCancellationRequested)
                    {
                        _log?.Invoke("[Voice] Edge online TTS timed out; trying a local engine.");
                    }
                    catch (OperationCanceledException)
                        when (operation.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logError?.Invoke("[Voice] Edge online TTS failed; trying a local engine.", ex);
                    }

                    if (!request.FallbackToLocal)
                    {
                        throw new InvalidOperationException(
                            "Edge online TTS failed and local fallback is disabled.");
                    }
                }

                if (request.Provider == TtsProviderKind.WinRt)
                {
                    try
                    {
                        await SpeakWithProviderAsync(
                            TtsProviderKind.WinRt,
                            text,
                            request,
                            operation.Token).ConfigureAwait(false);
                        return;
                    }
                    catch (OperationCanceledException) when (operation.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logError?.Invoke("[Voice] Windows Media TTS failed; falling back to SAPI.", ex);
                        if (!request.FallbackToLocal)
                        {
                            throw new InvalidOperationException(
                                "Windows Media TTS failed and local fallback is disabled.");
                        }
                    }
                }

                if (request.Provider == TtsProviderKind.EdgeOnline)
                {
                    try
                    {
                        await SpeakWithProviderAsync(
                            TtsProviderKind.WinRt,
                            text,
                            request,
                            operation.Token).ConfigureAwait(false);
                        return;
                    }
                    catch (OperationCanceledException) when (operation.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logError?.Invoke("[Voice] local Windows Media fallback is unavailable; trying SAPI.", ex);
                    }
                }

                await SpeakWithProviderAsync(
                    TtsProviderKind.Sapi,
                    text,
                    request,
                    operation.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_active, operation)) _active = null;
                }
                operation.Dispose();
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (_disposed) return;
                CancelActiveLocked();
                foreach (var provider in _providers.Values)
                {
                    try
                    {
                        provider.Stop();
                    }
                    catch (Exception ex)
                    {
                        _logError?.Invoke("[Voice] failed to stop a TTS provider.", ex);
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                CancelActiveLocked();
            }

            foreach (var provider in _providers.Values)
            {
                try
                {
                    provider.Dispose();
                }
                catch (Exception ex)
                {
                    _logError?.Invoke("[Voice] failed to dispose a TTS provider.", ex);
                }
            }
        }

        private async Task SpeakWithProviderAsync(
            TtsProviderKind provider,
            string text,
            TtsRequest request,
            CancellationToken cancellationToken)
        {
            if (!_providers.TryGetValue(provider, out var implementation)
                || !implementation.IsAvailable)
            {
                throw new NotSupportedException($"{GetDisplayName(provider)} is unavailable.");
            }

            var effectiveRequest = request.Provider == provider
                ? request
                : new TtsRequest(
                    provider,
                    string.Empty,
                    request.Rate,
                    request.Volume,
                    request.EdgeTimeoutMilliseconds,
                    request.FallbackToLocal);
            await implementation.SpeakAsync(text, effectiveRequest, cancellationToken)
                .ConfigureAwait(false);
        }

        private void CancelActiveLocked()
        {
            var active = _active;
            _active = null;
            if (active == null) return;
            try
            {
                active.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(TtsProviderManager));
            }
        }

        private static string GetDisplayName(TtsProviderKind provider)
        {
            return provider switch
            {
                TtsProviderKind.Sapi => "Windows SAPI（本地）",
                TtsProviderKind.WinRt => "Windows Media（本地）",
                TtsProviderKind.EdgeOnline => "Edge 在线语音（非官方）",
                _ => provider.ToString()
            };
        }

        private sealed class CachedVoices
        {
            public CachedVoices(
                IReadOnlyList<TtsVoiceInfo> voices,
                DateTimeOffset timestamp)
            {
                Voices = voices;
                Timestamp = timestamp;
            }

            public IReadOnlyList<TtsVoiceInfo> Voices { get; }
            public DateTimeOffset Timestamp { get; }
        }
    }
}
