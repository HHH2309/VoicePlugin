using System;
using System.Threading;
using System.Threading.Tasks;
using VoicePlugin.Config;

namespace VoicePlugin.Services
{
    public sealed class SpeechPipeline
    {
        private readonly TtsProviderManager _providers;
        private readonly IPreSpeechCuePlayer _cuePlayer;

        public SpeechPipeline(
            TtsProviderManager providers,
            IPreSpeechCuePlayer cuePlayer = null)
        {
            _providers = providers ?? throw new ArgumentNullException(nameof(providers));
            _cuePlayer = cuePlayer ?? new NoOpPreSpeechCuePlayer();
        }

        public Task<bool> PlayCueAsync(string mediaPath, CancellationToken cancellationToken)
        {
            return _cuePlayer.PlayAsync(mediaPath, cancellationToken);
        }

        public Task SpeakAsync(
            string text,
            PronunciationDictionary pronunciations,
            TtsRequest request,
            CancellationToken cancellationToken)
        {
            return _providers.SpeakAsync(
                pronunciations.Apply(text),
                request,
                cancellationToken);
        }

        /// <summary>
        /// 若请求的是“随机发音人”，解析为具体的发音人并返回新的请求；
        /// 否则原样返回。
        /// </summary>
        public async Task<TtsRequest> ResolveRandomVoiceAsync(
            TtsRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null
                || !string.Equals(
                    request.VoiceId,
                    VoiceConfigSnapshot.RandomVoiceId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return request;
            }

            var voiceId = await _providers.ResolveRandomVoiceAsync(
                request.Provider,
                cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(voiceId)) return request;

            return new TtsRequest(
                request.Provider,
                voiceId,
                request.Rate,
                request.Volume,
                request.EdgeTimeoutMilliseconds,
                request.FallbackToLocal);
        }

        public void Stop()
        {
            _cuePlayer.Stop();
            _providers.Stop();
        }
    }
}
