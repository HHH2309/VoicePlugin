using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EdgeTTS.DotNet;

namespace VoicePlugin.Services
{
    public sealed class EdgeOnlineTtsProvider : ITtsProvider, ITtsCacheableProvider
    {
        private const string DefaultVoice = "zh-CN-XiaoxiaoNeural";
        private readonly IAudioPlaybackService _playback;
        private readonly TtsAudioCache _cache;
        private readonly object _gate = new object();
        private CancellationTokenSource _active;
        private bool _disposed;

        public EdgeOnlineTtsProvider(
            IAudioPlaybackService playback,
            TtsAudioCache cache = null,
            Action<string> log = null,
            Action<string, Exception> logError = null)
        {
            _playback = playback ?? throw new ArgumentNullException(nameof(playback));
            _cache = cache;
        }

        public string CacheExtension => ".mp3";

        public TtsProviderKind Kind => TtsProviderKind.EdgeOnline;
        public string DisplayName => "Edge 在线语音（非官方）";
        public bool IsAvailable => !_disposed;

        public async Task<IReadOnlyList<TtsVoiceInfo>> GetVoicesAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var voices = await Voices.ListVoicesAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var mapped = voices
                .Where(voice => voice != null && !string.IsNullOrWhiteSpace(voice.ShortName))
                .OrderByDescending(voice =>
                    voice.Locale?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true)
                .ThenBy(voice => voice.Locale)
                .ThenBy(voice => voice.ShortName)
                .Select(voice => new TtsVoiceInfo(
                    voice.ShortName,
                    $"{voice.ShortName} · {voice.Locale}",
                    Kind,
                    voice.Locale,
                    true))
                .ToArray();
            return mapped;
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

            var deleteAfterPlay = false;
            string audioPath = null;
            try
            {
                if (_cache?.IsEnabled == true && !string.IsNullOrWhiteSpace(text))
                {
                    // 缓存路径：命中直接播缓存文件；未命中合成后写入缓存（提交失败时
                    // 返回缓存目录内的 .tmp 路径，播放后由下方清理）。
                    audioPath = await _cache.GetOrCreateAsync(
                        new TtsAudioCache.CacheKey(
                            Kind,
                            request.VoiceId,
                            request.Rate,
                            request.Volume,
                            text),
                        CacheExtension,
                        (path, token) => SynthesizeAsync(text, request, path, token),
                        operation.Token).ConfigureAwait(false);
                    deleteAfterPlay = audioPath.EndsWith(
                        TtsAudioCache.TempSuffix,
                        StringComparison.Ordinal);
                }
                else
                {
                    // 无缓存：合成到系统临时目录（原行为）。
                    audioPath = Path.Combine(
                        Path.GetTempPath(),
                        "VoicePlugin-" + Guid.NewGuid().ToString("N") + CacheExtension);
                    deleteAfterPlay = true;
                    await SynthesizeAsync(text, request, audioPath, operation.Token)
                        .ConfigureAwait(false);
                }

                operation.Token.ThrowIfCancellationRequested();
                await _playback.PlayFileAsync(audioPath, operation.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_active, operation)) _active = null;
                }
                operation.Dispose();
                if (deleteAfterPlay)
                {
                    TryDelete(audioPath);
                }
            }
        }

        /// <summary>合成到指定文件（不播放、不清理），供缓存与预缓存复用。</summary>
        public Task SynthesizeAsync(
            string text,
            TtsRequest request,
            string outputPath,
            CancellationToken cancellationToken)
        {
            var voice = request.Provider == Kind && !string.IsNullOrWhiteSpace(request.VoiceId)
                ? request.VoiceId
                : DefaultVoice;
            var rate = FormatPercentage(request.Rate * 5);
            var volume = FormatPercentage(request.Volume - 100);
            var communication = new Communicate(
                text,
                voice: voice,
                rate: rate,
                volume: volume,
                pitch: "+0Hz");
            return communication.SaveAsync(outputPath, cancellationToken);
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (_disposed) return;
                CancelActiveLocked();
                _playback.Stop();
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
        }

        private static string FormatPercentage(int percentage)
        {
            // EdgeTTS.DotNet 的参数校验要求符号前缀（如 "+0%"）；
            // 没有符号的 "0%" 会被直接判定为非法参数。
            return percentage >= 0
                ? "+" + percentage + "%"
                : percentage + "%";
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
                throw new ObjectDisposedException(nameof(EdgeOnlineTtsProvider));
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
