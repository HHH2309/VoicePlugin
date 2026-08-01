using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VoicePlugin.Services
{
    public interface ITtsProvider : IDisposable
    {
        TtsProviderKind Kind { get; }
        string DisplayName { get; }
        bool IsAvailable { get; }

        Task<IReadOnlyList<TtsVoiceInfo>> GetVoicesAsync(
            CancellationToken cancellationToken);

        Task SpeakAsync(
            string text,
            TtsRequest request,
            CancellationToken cancellationToken);

        void Stop();
    }
}
