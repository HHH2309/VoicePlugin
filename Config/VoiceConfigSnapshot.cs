using System;
using System.Collections.Generic;
using System.Linq;
using VoicePlugin.Services;

namespace VoicePlugin.Config
{
    public sealed class VoiceConfigSnapshot
    {
        /// <summary>“随机发音人”的 VoiceId 标记值。</summary>
        public const string RandomVoiceId = "random";

        /// <summary>当前配置 schema 版本（低于此版本的旧配置会在保存时重写）。</summary>
        public const int CurrentSchemaVersion = 2;

        public VoiceConfigSnapshot(VoiceConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            SchemaVersion = CurrentSchemaVersion;
            Enabled = config.Enabled;
            Provider = ParseProvider(config.Provider);
            VoiceId = config.VoiceId?.Trim() ?? string.Empty;
            Rate = Math.Clamp(config.Rate, -10, 10);
            Volume = Math.Clamp(config.Volume, 0, 100);
            SpeechTemplate = VoiceTextTemplate.Normalize(config.SpeechTemplate);
            DelayMilliseconds = Math.Clamp(config.DelayMilliseconds, 0, 2000);
            SpaceNumericDigits = config.SpaceNumericDigits;
            EnablePronunciations = config.EnablePronunciations;
            ShowTrayToggle = config.ShowTrayToggle;
            NotifyOnStop = config.NotifyOnStop;
            EnableAudioCache = config.EnableAudioCache;
            CacheLimitMode = config.CacheLimitMode;
            AudioCacheSizeMb = Math.Clamp(config.AudioCacheSizeMb, 20, 200);
            AudioCacheRetentionDays = Math.Clamp(config.AudioCacheRetentionDays, 1, 30);
            PrecacheScope = config.PrecacheScope;
            PrecacheRosterGuid = config.PrecacheRosterGuid?.Trim() ?? string.Empty;
            PrecacheOnStartup = config.PrecacheOnStartup;
            ClearCacheOnRosterChange = config.ClearCacheOnRosterChange;
            NotifyPrecacheCompleted = config.NotifyPrecacheCompleted;
            EnableCacheStats = config.EnableCacheStats;
            EnableStopHotkey = config.EnableStopHotkey;
            StopHotkeyModifiers = config.StopHotkeyModifiers;
            StopHotkeyKey = config.StopHotkeyKey;

            var cue = config.Cue ?? new CueConfig();
            CueEnabled = cue.Enabled;
            CuePath = cue.Path?.Trim() ?? string.Empty;
            CueGapMilliseconds = Math.Clamp(cue.GapMilliseconds, 0, 5000);

            var postCue = config.PostCue ?? new PostCueConfig();
            PostCueEnabled = postCue.Enabled;
            PostCuePath = postCue.Path?.Trim() ?? string.Empty;
            PostCueGapMilliseconds = Math.Clamp(postCue.GapMilliseconds, 0, 5000);

            var edge = config.Edge ?? new EdgeConfig();
            EdgeTimeoutMilliseconds = Math.Clamp(
                edge.TimeoutMilliseconds,
                1000,
                30000);
            EdgeFallbackToLocal = edge.FallbackToLocal;

            Pronunciations = NormalizePronunciations(config.Pronunciations);
        }

        public int SchemaVersion { get; }
        public bool Enabled { get; }
        public TtsProviderKind Provider { get; }
        public string VoiceId { get; }
        public int Rate { get; }
        public int Volume { get; }
        public string SpeechTemplate { get; }
        public int DelayMilliseconds { get; }
        public bool SpaceNumericDigits { get; }
        public bool EnablePronunciations { get; }
        public bool ShowTrayToggle { get; }
        public bool NotifyOnStop { get; }
        public bool EnableAudioCache { get; }
        public AudioCacheLimitMode CacheLimitMode { get; }
        public int AudioCacheSizeMb { get; }
        public int AudioCacheRetentionDays { get; }
        public PrecacheScopeMode PrecacheScope { get; }
        public string PrecacheRosterGuid { get; }
        public bool PrecacheOnStartup { get; }
        public bool ClearCacheOnRosterChange { get; }
        public bool NotifyPrecacheCompleted { get; }
        public bool EnableCacheStats { get; }
        public bool EnableStopHotkey { get; }
        public uint StopHotkeyModifiers { get; }
        public uint StopHotkeyKey { get; }
        public bool CueEnabled { get; }
        public string CuePath { get; }
        public int CueGapMilliseconds { get; }
        public bool PostCueEnabled { get; }
        public string PostCuePath { get; }
        public int PostCueGapMilliseconds { get; }
        public int EdgeTimeoutMilliseconds { get; }
        public bool EdgeFallbackToLocal { get; }
        public IReadOnlyList<PronunciationRule> Pronunciations { get; }

        public TtsRequest CreateTtsRequest()
        {
            return new TtsRequest(
                Provider,
                VoiceId,
                Rate,
                Volume,
                EdgeTimeoutMilliseconds,
                EdgeFallbackToLocal);
        }

        public VoiceConfig ToConfig()
        {
            return new VoiceConfig
            {
                SchemaVersion = SchemaVersion,
                Enabled = Enabled,
                Provider = Provider.ToString(),
                VoiceId = VoiceId,
                Rate = Rate,
                Volume = Volume,
                SpeechTemplate = SpeechTemplate,
                DelayMilliseconds = DelayMilliseconds,
                SpaceNumericDigits = SpaceNumericDigits,
                EnablePronunciations = EnablePronunciations,
                ShowTrayToggle = ShowTrayToggle,
                NotifyOnStop = NotifyOnStop,
                EnableAudioCache = EnableAudioCache,
                CacheLimitMode = CacheLimitMode,
                AudioCacheSizeMb = AudioCacheSizeMb,
                AudioCacheRetentionDays = AudioCacheRetentionDays,
                PrecacheScope = PrecacheScope,
                PrecacheRosterGuid = PrecacheRosterGuid,
                PrecacheOnStartup = PrecacheOnStartup,
                ClearCacheOnRosterChange = ClearCacheOnRosterChange,
                NotifyPrecacheCompleted = NotifyPrecacheCompleted,
                EnableCacheStats = EnableCacheStats,
                EnableStopHotkey = EnableStopHotkey,
                StopHotkeyModifiers = StopHotkeyModifiers,
                StopHotkeyKey = StopHotkeyKey,
                Cue = new CueConfig
                {
                    Enabled = CueEnabled,
                    Path = CuePath,
                    GapMilliseconds = CueGapMilliseconds
                },
                PostCue = new PostCueConfig
                {
                    Enabled = PostCueEnabled,
                    Path = PostCuePath,
                    GapMilliseconds = PostCueGapMilliseconds
                },
                Edge = new EdgeConfig
                {
                    TimeoutMilliseconds = EdgeTimeoutMilliseconds,
                    FallbackToLocal = EdgeFallbackToLocal
                },
                Pronunciations = Pronunciations.Select(rule =>
                    new PronunciationRuleConfig
                    {
                        Original = rule.Original,
                        Replacement = rule.Replacement
                    }).ToList(),
                EnablePrefix = false,
                PrefixText = string.Empty,
                EnableSuffix = false,
                SuffixText = string.Empty
            };
        }

        private static TtsProviderKind ParseProvider(string value)
        {
            return Enum.TryParse<TtsProviderKind>(value, true, out var provider)
                ? provider
                : TtsProviderKind.Sapi;
        }

        private static IReadOnlyList<PronunciationRule> NormalizePronunciations(
            IEnumerable<PronunciationRuleConfig> rules)
        {
            if (rules == null) return Array.Empty<PronunciationRule>();

            return rules
                .Where(rule => rule != null)
                .Select(rule => new PronunciationRule(
                    Limit(rule.Original?.Trim(), 80),
                    Limit(rule.Replacement?.Trim(), 120)))
                .Where(rule => !string.IsNullOrWhiteSpace(rule.Original))
                .Take(100)
                .ToArray();
        }

        private static string Limit(string value, int maximumLength)
        {
            var normalized = value ?? string.Empty;
            return normalized.Length <= maximumLength
                ? normalized
                : normalized.Substring(0, maximumLength);
        }
    }

    internal static class VoiceTextTemplate
    {
        public const string Placeholder = "{name}";
        public const string DefaultTemplate = "抽中了：{name}";

        public static string Normalize(string template)
        {
            var normalized = template?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized) || HasInvalidPlaceholder(normalized))
            {
                return Placeholder;
            }

            return normalized.Contains(Placeholder, StringComparison.Ordinal)
                ? normalized
                : normalized + " " + Placeholder;
        }

        private static bool HasInvalidPlaceholder(string template)
        {
            for (var index = 0; index < template.Length; index++)
            {
                if (template[index] != '{')
                {
                    if (template[index] == '}') return true;
                    continue;
                }

                var close = template.IndexOf('}', index + 1);
                if (close < 0) return true;

                if (!string.Equals(
                    template.Substring(index, close - index + 1),
                    Placeholder,
                    StringComparison.Ordinal))
                {
                    return true;
                }

                index = close;
            }

            return false;
        }
    }
}
