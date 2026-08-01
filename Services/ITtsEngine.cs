using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VoicePlugin.Services
{
    public interface ITtsEngine : IDisposable
    {
        bool IsReady { get; }
        string EngineName { get; }
        IReadOnlyList<TtsVoiceInfo> AvailableVoices { get; }

        Task SpeakAsync(string text, CancellationToken cancellationToken);
        void Stop();
        void SetRate(int rate);
        void SetVolume(int volume);
        void SetVoice(string voiceId);
    }
}
