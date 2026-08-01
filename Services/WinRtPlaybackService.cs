using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace VoicePlugin.Services
{
    /// <summary>
    /// 基于 Windows.Media.Playback.MediaPlayer 的音频播放服务。
    /// 使用 WinRT 原生播放器而非第三方库，避免宿主对外部程序集
    /// 的授权限制导致音频播放功能整体不可用。
    /// </summary>
    public sealed class WinRtPlaybackService : IAudioPlaybackService
    {
        private const int IdleWaitTimeoutMilliseconds = 120000;

        private readonly object _gate = new object();
        private MediaPlayer _activePlayer;
        private TaskCompletionSource<object> _activeCompletion;
        private bool _disposed;

        public async Task PlayFileAsync(
            string path,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            cancellationToken.ThrowIfCancellationRequested();

            var waited = 0;
            while (true)
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    if (_activePlayer == null) break;
                }

                if (waited >= IdleWaitTimeoutMilliseconds)
                {
                    throw new TimeoutException(
                        "The previous audio playback did not finish in time.");
                }

                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                waited += 10;
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The audio file was not found.", path);
            }

            MediaPlayer player = null;
            MediaSource source = null;
            var completion = new TaskCompletionSource<object>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            void OnMediaEnded(MediaPlayer sender, object args)
            {
                completion.TrySetResult(null);
            }

            void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
            {
                completion.TrySetException(new InvalidOperationException(
                    "Audio playback failed: "
                    + (args.ErrorMessage ?? "unknown media error")));
            }

            try
            {
                source = MediaSource.CreateFromUri(new Uri(path));
                player = new MediaPlayer
                {
                    AutoPlay = false,
                    IsLoopingEnabled = false,
                    Volume = 1.0
                };
                player.Source = source;
                player.MediaEnded += OnMediaEnded;
                player.MediaFailed += OnMediaFailed;

                using var registration = cancellationToken.Register(() =>
                {
                    try
                    {
                        player.Pause();
                    }
                    catch
                    {
                    }
                    completion.TrySetCanceled();
                });

                lock (_gate)
                {
                    ThrowIfDisposed();
                    _activePlayer = player;
                    _activeCompletion = completion;
                }

                player.Play();
                await completion.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_activePlayer, player))
                    {
                        _activePlayer = null;
                        _activeCompletion = null;
                    }
                }

                if (player != null)
                {
                    player.MediaEnded -= OnMediaEnded;
                    player.MediaFailed -= OnMediaFailed;
                }

                try
                {
                    player?.Dispose();
                }
                catch
                {
                }

                try
                {
                    source?.Dispose();
                }
                catch
                {
                }
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (_disposed) return;

                var player = _activePlayer;
                var completion = _activeCompletion;
                try
                {
                    player?.Pause();
                }
                catch
                {
                }

                // 与旧实现的 Stop 语义保持一致：正在等待播放完成的
                // 调用方需要立即得到完成信号，而不是悬挂到下次取消。
                completion?.TrySetResult(null);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;

                try
                {
                    _activePlayer?.Pause();
                }
                catch
                {
                }

                _activeCompletion?.TrySetResult(null);
                _activePlayer?.Dispose();
                _activePlayer = null;
                _activeCompletion = null;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(WinRtPlaybackService));
            }
        }
    }
}
