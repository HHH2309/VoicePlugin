using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VoicePlugin.Services
{
    public interface IPreSpeechCuePlayer
    {
        /// <summary>
        /// 播放前奏音；返回 true 表示实际播放并成功完成，
        /// false 表示被跳过或播放失败（播报仍会继续）。
        /// </summary>
        Task<bool> PlayAsync(string mediaPath, CancellationToken cancellationToken);
        void Stop();
    }

    public sealed class PreSpeechCuePlayer : IPreSpeechCuePlayer
    {
        private readonly IAudioPlaybackService _playback;
        private readonly Action<string> _log;
        private readonly Action<string, Exception> _logError;

        public PreSpeechCuePlayer(
            IAudioPlaybackService playback,
            Action<string> log = null,
            Action<string, Exception> logError = null)
        {
            _playback = playback ?? throw new ArgumentNullException(nameof(playback));
            _log = log;
            _logError = logError;
        }

        public async Task<bool> PlayAsync(
            string mediaPath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(mediaPath)) return false;

            var extension = Path.GetExtension(mediaPath);
            if (!string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".flac", StringComparison.OrdinalIgnoreCase))
            {
                _log?.Invoke("[Voice] cue skipped because only WAV, MP3, and FLAC are supported.");
                return false;
            }

            if (!File.Exists(mediaPath))
            {
                _log?.Invoke("[Voice] configured cue file does not exist; speech will continue without it.");
                return false;
            }

            try
            {
                await _playback.PlayFileAsync(mediaPath, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logError?.Invoke(
                    "[Voice] cue playback failed; speech will continue without it.",
                    ex);
                return false;
            }
        }

        public void Stop()
        {
            _playback.Stop();
        }
    }

    internal sealed class NoOpPreSpeechCuePlayer : IPreSpeechCuePlayer
    {
        public Task<bool> PlayAsync(string mediaPath, CancellationToken cancellationToken)
        {
            return cancellationToken.IsCancellationRequested
                ? Task.FromCanceled<bool>(cancellationToken)
                : Task.FromResult(false);
        }

        public void Stop()
        {
        }
    }
}
