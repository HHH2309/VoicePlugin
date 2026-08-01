using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.SpeechSynthesis;

namespace VoicePlugin.Services
{
    /// <summary>
    /// Windows.Media speech synthesis. Microsoft documents this API as requiring
    /// package identity for desktop apps, so activation is lazy and failures are
    /// isolated from plugin startup.
    /// </summary>
    public sealed class WinRtTtsProvider : ITtsProvider, ITtsCacheableProvider
    {
        private readonly IAudioPlaybackService _playback;
        private readonly TtsAudioCache _cache;
        private readonly Action<string> _log;
        private readonly Action<string, Exception> _logError;
        private readonly Lazy<bool> _availability;
        private readonly object _gate = new object();
        private CancellationTokenSource _active;
        private bool _disposed;

        public WinRtTtsProvider(
            IAudioPlaybackService playback,
            TtsAudioCache cache = null,
            Action<string> log = null,
            Action<string, Exception> logError = null)
        {
            _playback = playback ?? throw new ArgumentNullException(nameof(playback));
            _cache = cache;
            _log = log;
            _logError = logError;
            _availability = new Lazy<bool>(ProbeAvailability, true);
        }

        public string CacheExtension => ".wav";

        public TtsProviderKind Kind => TtsProviderKind.WinRt;
        public string DisplayName => "Windows Media（本地）";
        public bool IsAvailable => !_disposed && _availability.Value;

        public Task<IReadOnlyList<TtsVoiceInfo>> GetVoicesAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAvailable)
            {
                return Task.FromResult<IReadOnlyList<TtsVoiceInfo>>(
                    Array.Empty<TtsVoiceInfo>());
            }

            return Task.Run<IReadOnlyList<TtsVoiceInfo>>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return SpeechSynthesizer.AllVoices
                    .OrderByDescending(voice =>
                        voice.Language?.StartsWith(
                            "zh",
                            StringComparison.OrdinalIgnoreCase) == true)
                    .ThenBy(voice => voice.Language)
                    .ThenBy(voice => voice.DisplayName)
                    .Select(voice => new TtsVoiceInfo(
                        voice.Id,
                        $"{voice.DisplayName} · {voice.Language}",
                        Kind,
                        voice.Language,
                        false))
                    .ToArray();
            }, cancellationToken);
        }

        public async Task SpeakAsync(
            string text,
            TtsRequest request,
            CancellationToken cancellationToken)
        {
            if (!IsAvailable)
            {
                throw new NotSupportedException(
                    "Windows Media TTS is unavailable. The host may not have the package identity required by this Windows API.");
            }

            CancellationTokenSource operation;
            lock (_gate)
            {
                ThrowIfDisposed();
                CancelActiveLocked();
                operation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                _active = operation;
            }

            var deleteAfterPlay = false;
            string audioPath = null;
            try
            {
                if (_cache?.IsEnabled == true && !string.IsNullOrWhiteSpace(text))
                {
                    // 缓存路径：命中直接播缓存文件；未命中合成后写入缓存。
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
                        "VoicePlugin-WinRT-" + Guid.NewGuid().ToString("N") + CacheExtension);
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
        public async Task SynthesizeAsync(
            string text,
            TtsRequest request,
            string outputPath,
            CancellationToken cancellationToken)
        {
            if (!IsAvailable)
            {
                throw new NotSupportedException(
                    "Windows Media TTS is unavailable. The host may not have the package identity required by this Windows API.");
            }

            using var synthesizer = new SpeechSynthesizer();
            SelectVoice(synthesizer, request);
            ApplyOptions(synthesizer, request);

            using var speechStream = await synthesizer
                .SynthesizeTextToStreamAsync(text)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            using (var input = speechStream.AsStreamForRead())
            using (var output = new FileStream(
                outputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, cancellationToken)
                    .ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
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

        private bool ProbeAvailability()
        {
            try
            {
                using var synthesizer = new SpeechSynthesizer();
                _ = SpeechSynthesizer.AllVoices.Count;
                _log?.Invoke("[Voice] Windows Media TTS is available.");
                return true;
            }
            catch (Exception ex)
            {
                _logError?.Invoke(
                    "[Voice] Windows Media TTS is unavailable; this normally means the unpackaged host lacks the required package identity.",
                    ex);
                return false;
            }
        }

        private static void SelectVoice(
            SpeechSynthesizer synthesizer,
            TtsRequest request)
        {
            var voices = SpeechSynthesizer.AllVoices;
            VoiceInformation selected = null;
            if (request.Provider == TtsProviderKind.WinRt
                && !string.IsNullOrWhiteSpace(request.VoiceId))
            {
                selected = voices.FirstOrDefault(voice => string.Equals(
                    voice.Id,
                    request.VoiceId,
                    StringComparison.OrdinalIgnoreCase));
            }

            selected ??= voices.FirstOrDefault(voice =>
                voice.Language?.StartsWith(
                    "zh",
                    StringComparison.OrdinalIgnoreCase) == true);
            selected ??= SpeechSynthesizer.DefaultVoice;
            if (selected != null)
            {
                synthesizer.Voice = selected;
            }
        }

        private static void ApplyOptions(
            SpeechSynthesizer synthesizer,
            TtsRequest request)
        {
            synthesizer.Options.AudioVolume = request.Volume / 100d;
            synthesizer.Options.SpeakingRate = Math.Clamp(
                Math.Pow(2d, request.Rate / 10d),
                0.5d,
                2d);
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
                throw new ObjectDisposedException(nameof(WinRtTtsProvider));
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
