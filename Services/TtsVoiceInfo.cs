using System;

namespace VoicePlugin.Services
{
    public sealed class TtsVoiceInfo : IEquatable<TtsVoiceInfo>
    {
        public TtsVoiceInfo(
            string id,
            string displayName,
            TtsProviderKind provider = TtsProviderKind.Sapi,
            string language = "",
            bool isOnline = false)
        {
            Id = id ?? string.Empty;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Id : displayName;
            Provider = provider;
            Language = language ?? string.Empty;
            IsOnline = isOnline;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public TtsProviderKind Provider { get; }
        public string Language { get; }
        public bool IsOnline { get; }

        public bool Equals(TtsVoiceInfo other)
        {
            return other != null
                && Provider == other.Provider
                && string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as TtsVoiceInfo);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(
                Provider,
                StringComparer.OrdinalIgnoreCase.GetHashCode(Id));
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }
}
