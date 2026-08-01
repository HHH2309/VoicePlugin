using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VoicePlugin.Services
{
    public sealed class SapiTtsProvider : ITtsProvider
    {
        private readonly SapiTtsEngine _engine;
        private IReadOnlyList<TtsVoiceInfo> _lastVoices = Array.Empty<TtsVoiceInfo>();

        public SapiTtsProvider(
            Action<string> log = null,
            Action<string, Exception> logError = null)
        {
            _engine = new SapiTtsEngine(log, logError);
        }

        public TtsProviderKind Kind => TtsProviderKind.Sapi;
        public string DisplayName => "Windows SAPI（本地）";

        /// <summary>
        /// 仅在引擎初始化成功后可用（ProgID 缺失/COM 激活失败时恒为 false），
        /// 让设置页与播报链路如实反映 SAPI 可用性。
        /// 初始化窗口（毫秒级）内的瞬时不可用由 GetVoicesAsync 的轮询吸收。
        /// </summary>
        public bool IsAvailable => _engine.IsReady;

        public async Task<IReadOnlyList<TtsVoiceInfo>> GetVoicesAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var voices = _engine.AvailableVoices;
                if (voices.Count > 0 || _engine.IsReady)
                {
                    _lastVoices = voices;
                    return voices;
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            return _lastVoices;
        }

        public Task SpeakAsync(
            string text,
            TtsRequest request,
            CancellationToken cancellationToken)
        {
            _engine.SetRate(request.Rate);
            _engine.SetVolume(request.Volume);
            _engine.SetVoice(request.Provider == Kind ? request.VoiceId : string.Empty);
            return _engine.SpeakAsync(text, cancellationToken);
        }

        public void Stop()
        {
            _engine.Stop();
        }

        public void Dispose()
        {
            _engine.Dispose();
        }
    }
}
