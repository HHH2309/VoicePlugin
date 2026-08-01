using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using VoicePlugin.Config;

namespace VoicePlugin.Services
{
    /// <summary>
    /// Executes only the latest speech batch so host callbacks never wait for TTS.
    /// </summary>
    public sealed class VoiceQueue : IDisposable
    {
        private readonly SpeechPipeline _pipeline;
        private readonly Action<string, Exception> _logError;
        private readonly BlockingCollection<QueuedBatch> _queue
            = new BlockingCollection<QueuedBatch>();
        private readonly CancellationTokenSource _shutdown
            = new CancellationTokenSource();
        private readonly object _gate = new object();
        private readonly Task _workerTask;
        private CancellationTokenSource _activeRequest;
        private long _generation;
        private int _isSpeaking;
        private bool _disposed;

        /// <summary>是否正在播报（从工作线程实时更新，读侧无需加锁）。</summary>
        public bool IsSpeaking => Volatile.Read(ref _isSpeaking) != 0;

        /// <summary>播报开始/结束通知；事件可能在工作线程触发，调用方自行编排线程。</summary>
        public event Action<bool> SpeakingStateChanged;

        public VoiceQueue(
            TtsProviderManager providers,
            Action<string, Exception> logError = null,
            IPreSpeechCuePlayer cuePlayer = null)
        {
            _pipeline = new SpeechPipeline(providers, cuePlayer);
            _logError = logError;
            _workerTask = Task.Run(() => ProcessQueueAsync(_shutdown.Token));
        }

        public bool EnqueueLatest(SpeechBatchRequest request)
        {
            if (request == null || request.Texts.Count == 0) return false;

            lock (_gate)
            {
                if (_disposed || _queue.IsAddingCompleted) return false;

                CancelActiveLocked();
                DrainPendingLocked();
                StopPipelineLocked();

                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _shutdown.Token);
                var item = new QueuedBatch(
                    request,
                    ++_generation,
                    cancellation);
                _activeRequest = cancellation;

                try
                {
                    _queue.Add(item);
                    return true;
                }
                catch (InvalidOperationException)
                {
                    if (ReferenceEquals(_activeRequest, cancellation))
                    {
                        _activeRequest = null;
                    }
                    cancellation.Dispose();
                    return false;
                }
                catch (Exception ex)
                {
                    if (ReferenceEquals(_activeRequest, cancellation))
                    {
                        _activeRequest = null;
                    }
                    cancellation.Dispose();
                    ReportError("[Voice] failed to enqueue the latest speech batch.", ex);
                    return false;
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                if (_disposed) return;
                ++_generation;
                CancelActiveLocked();
                DrainPendingLocked();
                StopPipelineLocked();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                ++_generation;
                CancelActiveLocked();
                DrainPendingLocked();
                StopPipelineLocked();
                _shutdown.Cancel();
                _queue.CompleteAdding();
            }

            try
            {
                _workerTask.Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                ReportError("[Voice] speech worker did not finish cleanly.", ex);
            }

            _queue.Dispose();
            _shutdown.Dispose();
        }

        private async Task ProcessQueueAsync(CancellationToken shutdownToken)
        {
            try
            {
                foreach (var item in _queue.GetConsumingEnumerable(shutdownToken))
                {
                    try
                    {
                        if (!IsCurrent(item)) continue;

                        // 批次开始（含延迟等待）即视为“正在播报”，
                        // 供工具栏/白板按钮切换为播报中图标。
                        SetSpeaking(true);

                        if (item.Request.DelayMilliseconds > 0)
                        {
                            await Task.Delay(
                                item.Request.DelayMilliseconds,
                                item.Cancellation.Token).ConfigureAwait(false);
                        }

                        if (!IsCurrent(item)) continue;

                        var cuePlayed = await _pipeline.PlayCueAsync(
                            item.Request.CuePath,
                            item.Cancellation.Token).ConfigureAwait(false);

                        if (!IsCurrent(item)) continue;

                        // 只有前奏音实际播放成功才等待间隔，
                        // 避免“没响但停顿”的假象。
                        if (cuePlayed && item.Request.CueGapMilliseconds > 0)
                        {
                            await Task.Delay(
                                item.Request.CueGapMilliseconds,
                                item.Cancellation.Token).ConfigureAwait(false);
                        }

                        var pronunciations = new PronunciationDictionary(
                            item.Request.Settings.EnablePronunciations
                                ? item.Request.Settings.Pronunciations
                                : null);
                        var ttsRequest = item.Request.Settings.CreateTtsRequest();
                        if (string.Equals(
                            ttsRequest.VoiceId,
                            VoiceConfigSnapshot.RandomVoiceId,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            // “随机发音人”：为本次批次解析一个具体的发音人。
                            ttsRequest = await _pipeline
                                .ResolveRandomVoiceAsync(
                                    ttsRequest,
                                    item.Cancellation.Token)
                                .ConfigureAwait(false);
                        }
                        foreach (var text in item.Request.Texts)
                        {
                            if (!IsCurrent(item)) break;
                            await _pipeline.SpeakAsync(
                                text,
                                pronunciations,
                                ttsRequest,
                                item.Cancellation.Token).ConfigureAwait(false);
                        }

                        // 全部朗读完毕后等待“播报后间隔”，再播放后置音；
                        // 中途被停止（IsCurrent 失败）时不播放。
                        if (IsCurrent(item)
                            && !string.IsNullOrWhiteSpace(
                                item.Request.PostCuePath))
                        {
                            if (item.Request.PostCueGapMilliseconds > 0)
                            {
                                await Task.Delay(
                                    item.Request.PostCueGapMilliseconds,
                                    item.Cancellation.Token).ConfigureAwait(false);
                            }

                            if (IsCurrent(item))
                            {
                                await _pipeline.PlayCueAsync(
                                    item.Request.PostCuePath,
                                    item.Cancellation.Token).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                        when (item.Cancellation.IsCancellationRequested
                            || shutdownToken.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        ReportError("[Voice] speech batch failed.", ex);
                    }
                    finally
                    {
                        SetSpeaking(false);
                        lock (_gate)
                        {
                            if (ReferenceEquals(
                                _activeRequest,
                                item.Cancellation))
                            {
                                _activeRequest = null;
                            }
                        }
                        item.Cancellation.Dispose();
                    }
                }
            }
            catch (OperationCanceledException)
                when (shutdownToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                ReportError("[Voice] speech worker stopped unexpectedly.", ex);
            }
        }

        private bool IsCurrent(QueuedBatch item)
        {
            lock (_gate)
            {
                return !_disposed
                    && !item.Cancellation.IsCancellationRequested
                    && item.Generation == _generation
                    && ReferenceEquals(_activeRequest, item.Cancellation);
            }
        }

        private void CancelActiveLocked()
        {
            var active = _activeRequest;
            _activeRequest = null;
            if (active == null) return;

            try
            {
                active.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void DrainPendingLocked()
        {
            while (_queue.TryTake(out var pending))
            {
                try
                {
                    pending.Cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                pending.Cancellation.Dispose();
            }
        }

        private void StopPipelineLocked()
        {
            try
            {
                _pipeline.Stop();
            }
            catch (Exception ex)
            {
                ReportError("[Voice] failed to stop active speech.", ex);
            }
        }

        private void SetSpeaking(bool speaking)
        {
            var value = speaking ? 1 : 0;
            if (Interlocked.Exchange(ref _isSpeaking, value) == value) return;

            try
            {
                SpeakingStateChanged?.Invoke(speaking);
            }
            catch (Exception ex)
            {
                // 通知失败不影响播报流程。
                ReportError("[Voice] speaking state notification failed.", ex);
            }
        }

        private void ReportError(string message, Exception ex)
        {
            _logError?.Invoke(message, ex);
        }

        private sealed class QueuedBatch
        {
            public QueuedBatch(
                SpeechBatchRequest request,
                long generation,
                CancellationTokenSource cancellation)
            {
                Request = request;
                Generation = generation;
                Cancellation = cancellation;
            }

            public SpeechBatchRequest Request { get; }
            public long Generation { get; }
            public CancellationTokenSource Cancellation { get; }
        }
    }
}
