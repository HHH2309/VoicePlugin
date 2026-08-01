using System;
using System.Threading;
using System.Threading.Tasks;

namespace VoicePlugin.Services
{
    public interface IAudioPlaybackService : IDisposable
    {
        Task PlayFileAsync(string path, CancellationToken cancellationToken);
        void Stop();
    }
}
