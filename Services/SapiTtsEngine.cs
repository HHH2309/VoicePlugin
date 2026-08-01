using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace VoicePlugin.Services
{
    /// <summary>
    /// Windows SAPI COM engine isolated to a dedicated STA worker thread.
    /// </summary>
    public sealed class SapiTtsEngine : ITtsEngine
    {
        private const int SpeakAsyncFlag = 1;
        private const int PurgeBeforeSpeakFlag = 2;

        private readonly BlockingCollection<WorkItem> _workItems = new BlockingCollection<WorkItem>();
        private readonly Thread _workerThread;
        private readonly Action<string> _log;
        private readonly Action<string, Exception> _logError;
        private readonly object _lifecycleLock = new object();
        private object _voice;
        private Type _voiceType;
        private bool _disposed;
        private int _stopGeneration;
        private int _rate;
        private int _volume = 100;
        private string _voiceId = string.Empty;
        private IReadOnlyList<TtsVoiceInfo> _availableVoices = Array.Empty<TtsVoiceInfo>();

        // 就绪状态由工作线程写入、被外部线程读取（IsAvailable/轮询），
        // 用 volatile 保证可见性（与 AvailableVoices 的 Volatile.Read 一致）。
        private volatile bool _isReady;

        public bool IsReady => _isReady;
        public string EngineName => "Windows SAPI (COM)";
        public IReadOnlyList<TtsVoiceInfo> AvailableVoices => Volatile.Read(ref _availableVoices);

        public SapiTtsEngine(Action<string> log = null, Action<string, Exception> logError = null)
        {
            _log = log;
            _logError = logError;
            _workerThread = new Thread(WorkerMain)
            {
                IsBackground = true,
                Name = "VoicePlugin.SapiTts"
            };
            _workerThread.SetApartmentState(ApartmentState.STA);
            _workerThread.Start();
        }

        public Task SpeakAsync(string text, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lifecycleLock)
            {
                if (_disposed || _workItems.IsAddingCompleted)
                {
                    completion.TrySetException(new ObjectDisposedException(nameof(SapiTtsEngine)));
                    return completion.Task;
                }

                var stopGeneration = _stopGeneration;
                if (!TryQueueLocked(new WorkItem(
                    WorkItemKind.Speak,
                    text,
                    0,
                    completion,
                    cancellationToken,
                    stopGeneration)))
                {
                    completion.TrySetException(new InvalidOperationException("The speech engine is unavailable."));
                }
            }

            return completion.Task;
        }

        public void Stop()
        {
            lock (_lifecycleLock)
            {
                if (_disposed || _workItems.IsAddingCompleted) return;
                _stopGeneration++;
                TryQueueLocked(new WorkItem(
                    WorkItemKind.Stop,
                    null,
                    0,
                    null,
                    CancellationToken.None,
                    0));
            }
        }

        public void SetRate(int rate)
        {
            var clamped = Math.Clamp(rate, -10, 10);
            lock (_lifecycleLock)
            {
                if (_disposed || _workItems.IsAddingCompleted) return;
                _rate = clamped;
                TryQueueLocked(new WorkItem(
                    WorkItemKind.SetRate,
                    null,
                    clamped,
                    null,
                    CancellationToken.None,
                    0));
            }
        }

        public void SetVolume(int volume)
        {
            var clamped = Math.Clamp(volume, 0, 100);
            lock (_lifecycleLock)
            {
                if (_disposed || _workItems.IsAddingCompleted) return;
                _volume = clamped;
                TryQueueLocked(new WorkItem(
                    WorkItemKind.SetVolume,
                    null,
                    clamped,
                    null,
                    CancellationToken.None,
                    0));
            }
        }

        public void SetVoice(string voiceId)
        {
            var normalized = voiceId?.Trim() ?? string.Empty;
            lock (_lifecycleLock)
            {
                if (_disposed || _workItems.IsAddingCompleted) return;
                _voiceId = normalized;
                TryQueueLocked(new WorkItem(
                    WorkItemKind.SetVoice,
                    normalized,
                    0,
                    null,
                    CancellationToken.None,
                    0));
            }
        }

        public void Dispose()
        {
            lock (_lifecycleLock)
            {
                if (_disposed) return;
                _stopGeneration++;
                TryQueueLocked(new WorkItem(
                    WorkItemKind.Stop,
                    null,
                    0,
                    null,
                    CancellationToken.None,
                    0));
                _disposed = true;
                _workItems.CompleteAdding();
            }

            if (!_workerThread.Join(TimeSpan.FromSeconds(3)))
            {
                ReportError("[Voice] SAPI worker did not stop before shutdown timeout; resources remain owned by the worker.", null);
                return;
            }

            _workItems.Dispose();
        }

        private bool TryQueueLocked(WorkItem item)
        {
            try
            {
                _workItems.Add(item);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private void WorkerMain()
        {
            try
            {
                InitializeOnWorker();
            }
            catch (Exception ex)
            {
                _isReady = false;
                ReportError("[Voice] SAPI initialization failed.", ex);
            }

            try
            {
                foreach (var item in _workItems.GetConsumingEnumerable())
                {
                    if (!IsReady && item.Kind == WorkItemKind.Speak)
                    {
                        item.Completion?.TrySetException(
                            new InvalidOperationException("Windows SAPI is unavailable."));
                        continue;
                    }
                    ProcessWorkItem(item);
                }
            }
            catch (Exception ex)
            {
                ReportError("[Voice] SAPI worker failed.", ex);
            }
            finally
            {
                ReleaseVoice();
            }
        }

        private void InitializeOnWorker()
        {
            _voiceType = Type.GetTypeFromProgID("SAPI.SpVoice")
                ?? Type.GetTypeFromProgID("Speech.SpVoice");
            if (_voiceType == null)
            {
                throw new InvalidOperationException("Windows SAPI voice component is not installed.");
            }

            _voice = Activator.CreateInstance(_voiceType);
            if (_voice == null)
            {
                throw new InvalidOperationException("Windows SAPI voice instance could not be created.");
            }

            ApplyProperty("Volume", _volume);
            ApplyProperty("Rate", _rate);
            RefreshAvailableVoices();
            SelectVoice(_voiceId);
            _isReady = true;
            _log?.Invoke("[Voice] SAPI initialized on a dedicated STA thread.");
        }

        private void ProcessWorkItem(WorkItem item)
        {
            try
            {
                switch (item.Kind)
                {
                    case WorkItemKind.Speak:
                        Speak(item);
                        break;
                    case WorkItemKind.Stop:
                        StopOnWorker();
                        break;
                    case WorkItemKind.SetRate:
                        ApplyProperty("Rate", item.Value);
                        break;
                    case WorkItemKind.SetVolume:
                        ApplyProperty("Volume", item.Value);
                        break;
                    case WorkItemKind.SetVoice:
                        SelectVoice(item.Text);
                        break;
                }
            }
            catch (Exception ex)
            {
                item.Completion?.TrySetException(ex);
                ReportError("[Voice] SAPI operation failed.", ex);
            }
        }

        private void Speak(WorkItem item)
        {
            if (!IsReady || _voice == null)
            {
                throw new InvalidOperationException("Windows SAPI is unavailable.");
            }

            if (item.CancellationToken.IsCancellationRequested)
            {
                item.Completion?.TrySetCanceled(item.CancellationToken);
                return;
            }

            if (item.StopGeneration != Volatile.Read(ref _stopGeneration))
            {
                item.Completion?.TrySetResult(null);
                return;
            }

            var stopGeneration = item.StopGeneration;
            InvokeOnVoice("Speak", item.Text, SpeakAsyncFlag);
            while (!item.CancellationToken.IsCancellationRequested
                && stopGeneration == Volatile.Read(ref _stopGeneration)
                && !WaitUntilDone(50))
            {
            }

            if (item.CancellationToken.IsCancellationRequested || stopGeneration != Volatile.Read(ref _stopGeneration))
            {
                StopOnWorker();
                if (item.CancellationToken.IsCancellationRequested)
                {
                    item.Completion?.TrySetCanceled(item.CancellationToken);
                }
                else
                {
                    item.Completion?.TrySetResult(null);
                }
                return;
            }

            item.Completion?.TrySetResult(null);
        }

        private bool WaitUntilDone(int timeoutMilliseconds)
        {
            var result = _voiceType.InvokeMember(
                "WaitUntilDone",
                BindingFlags.InvokeMethod,
                null,
                _voice,
                new object[] { timeoutMilliseconds });
            return Convert.ToBoolean(result);
        }

        private void StopOnWorker()
        {
            if (_voice == null) return;
            InvokeOnVoice("Speak", string.Empty, PurgeBeforeSpeakFlag);
        }

        private void ApplyProperty(string propertyName, int value)
        {
            if (_voice == null) return;
            _voiceType.InvokeMember(propertyName, BindingFlags.SetProperty, null, _voice, new object[] { value });
        }

        private void RefreshAvailableVoices()
        {
            var discovered = new List<TtsVoiceInfo>();
            object voices = null;
            try
            {
                voices = _voiceType.InvokeMember("GetVoices", BindingFlags.InvokeMethod, null, _voice, null);
                if (voices == null)
                {
                    Volatile.Write(ref _availableVoices, Array.Empty<TtsVoiceInfo>());
                    return;
                }

                var voicesType = voices.GetType();
                var count = Convert.ToInt32(voicesType.InvokeMember("Count", BindingFlags.GetProperty, null, voices, null));
                for (var index = 0; index < count; index++)
                {
                    object token = null;
                    try
                    {
                        token = voicesType.InvokeMember("Item", BindingFlags.InvokeMethod, null, voices, new object[] { index });
                        if (token == null) continue;

                        var tokenType = token.GetType();
                        var id = tokenType.InvokeMember("Id", BindingFlags.GetProperty, null, token, null) as string;
                        var description = tokenType.InvokeMember("GetDescription", BindingFlags.InvokeMethod, null, token, null) as string;
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            discovered.Add(new TtsVoiceInfo(id, description));
                        }
                    }
                    finally
                    {
                        ReleaseComObject(token);
                    }
                }
            }
            catch (Exception ex)
            {
                ReportError("[Voice] could not enumerate installed SAPI voices.", ex);
            }
            finally
            {
                ReleaseComObject(voices);
                Volatile.Write(ref _availableVoices, discovered.AsReadOnly());
            }
        }

        private void SelectVoice(string voiceId)
        {
            object voices = null;
            object fallbackToken = null;
            object selectedToken = null;
            try
            {
                voices = _voiceType.InvokeMember("GetVoices", BindingFlags.InvokeMethod, null, _voice, null);
                if (voices == null) return;

                var requestedId = voiceId?.Trim() ?? string.Empty;
                var voicesType = voices.GetType();
                var count = Convert.ToInt32(voicesType.InvokeMember("Count", BindingFlags.GetProperty, null, voices, null));
                for (var index = 0; index < count; index++)
                {
                    object token = null;
                    try
                    {
                        token = voicesType.InvokeMember("Item", BindingFlags.InvokeMethod, null, voices, new object[] { index });
                        if (token == null) continue;

                        var tokenType = token.GetType();
                        var id = tokenType.InvokeMember("Id", BindingFlags.GetProperty, null, token, null) as string;
                        var description = tokenType.InvokeMember("GetDescription", BindingFlags.InvokeMethod, null, token, null) as string;

                        if (!string.IsNullOrEmpty(requestedId)
                            && string.Equals(id, requestedId, StringComparison.OrdinalIgnoreCase))
                        {
                            selectedToken = token;
                            token = null;
                            break;
                        }

                        if (string.IsNullOrEmpty(requestedId)
                            && fallbackToken == null
                            && IsChineseVoice(description))
                        {
                            fallbackToken = token;
                            token = null;
                        }
                    }
                    finally
                    {
                        ReleaseComObject(token);
                    }
                }

                if (selectedToken != null)
                {
                    _voiceType.InvokeMember("Voice", BindingFlags.SetProperty, null, _voice, new[] { selectedToken });
                    return;
                }

                if (string.IsNullOrEmpty(requestedId) && fallbackToken != null)
                {
                    _voiceType.InvokeMember("Voice", BindingFlags.SetProperty, null, _voice, new[] { fallbackToken });
                    return;
                }

                if (!string.IsNullOrEmpty(requestedId))
                {
                    _log?.Invoke("[Voice] configured SAPI voice is unavailable; using automatic selection.");
                    SelectVoice(string.Empty);
                }
            }
            catch (Exception ex)
            {
                ReportError("[Voice] could not select the configured SAPI voice; using the current voice.", ex);
            }
            finally
            {
                ReleaseComObject(selectedToken);
                ReleaseComObject(fallbackToken);
                ReleaseComObject(voices);
            }
        }

        private static bool IsChineseVoice(string description)
        {
            return !string.IsNullOrEmpty(description)
                && (description.Contains("Chinese", StringComparison.OrdinalIgnoreCase)
                    || description.Contains("中文", StringComparison.Ordinal));
        }

        private static void ReleaseComObject(object value)
        {
            if (value == null || !Marshal.IsComObject(value)) return;

            try
            {
                Marshal.FinalReleaseComObject(value);
            }
            catch
            {
                // The owning SAPI voice will release any remaining references on shutdown.
            }
        }

        private void InvokeOnVoice(string memberName, params object[] arguments)
        {
            _voiceType?.InvokeMember(memberName, BindingFlags.InvokeMethod, null, _voice, arguments);
        }

        private void ReleaseVoice()
        {
            _isReady = false;
            try
            {
                if (_voice != null && Marshal.IsComObject(_voice))
                {
                    Marshal.FinalReleaseComObject(_voice);
                }
            }
            catch (Exception ex)
            {
                ReportError("[Voice] failed to release the SAPI COM object.", ex);
            }
            finally
            {
                _voice = null;
                _voiceType = null;
            }
        }

        private void ReportError(string message, Exception ex)
        {
            _logError?.Invoke(message, ex);
        }

        private enum WorkItemKind
        {
            Speak,
            Stop,
            SetRate,
            SetVolume,
            SetVoice
        }

        private sealed class WorkItem
        {
            public WorkItem(WorkItemKind kind, string text, int value, TaskCompletionSource<object> completion, CancellationToken cancellationToken, int stopGeneration)
            {
                Kind = kind;
                Text = text;
                Value = value;
                Completion = completion;
                CancellationToken = cancellationToken;
                StopGeneration = stopGeneration;
            }

            public WorkItemKind Kind { get; }
            public string Text { get; }
            public int Value { get; }
            public TaskCompletionSource<object> Completion { get; }
            public CancellationToken CancellationToken { get; }
            public int StopGeneration { get; }
        }
    }
}
