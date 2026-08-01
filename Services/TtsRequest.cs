using System;

namespace VoicePlugin.Services
{
    public sealed class TtsRequest
    {
        public TtsRequest(
            TtsProviderKind provider,
            string voiceId,
            int rate,
            int volume,
            int edgeTimeoutMilliseconds,
            bool fallbackToLocal)
        {
            Provider = provider;
            VoiceId = voiceId?.Trim() ?? string.Empty;
            Rate = Math.Clamp(rate, -10, 10);
            Volume = Math.Clamp(volume, 0, 100);
            EdgeTimeoutMilliseconds = Math.Clamp(edgeTimeoutMilliseconds, 1000, 30000);
            FallbackToLocal = fallbackToLocal;
        }

        public TtsProviderKind Provider { get; }
        public string VoiceId { get; }
        public int Rate { get; }
        public int Volume { get; }
        public int EdgeTimeoutMilliseconds { get; }
        public bool FallbackToLocal { get; }
    }
}
