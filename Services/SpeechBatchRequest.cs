using System;
using System.Collections.Generic;
using System.Linq;
using VoicePlugin.Config;

namespace VoicePlugin.Services
{
    public sealed class SpeechBatchRequest
    {
        public SpeechBatchRequest(
            IEnumerable<string> texts,
            VoiceConfigSnapshot settings)
        {
            Texts = (texts ?? Array.Empty<string>())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text.Trim())
                .ToArray();
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public IReadOnlyList<string> Texts { get; }
        public VoiceConfigSnapshot Settings { get; }
        public int DelayMilliseconds => Settings.DelayMilliseconds;
        public string CuePath => Settings.CueEnabled ? Settings.CuePath : string.Empty;
        public int CueGapMilliseconds => Settings.CueGapMilliseconds;
        public string PostCuePath => Settings.PostCueEnabled
            ? Settings.PostCuePath
            : string.Empty;
        public int PostCueGapMilliseconds => Settings.PostCueGapMilliseconds;
    }
}
