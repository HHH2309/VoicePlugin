namespace VoicePlugin.Services
{
    public sealed class ProviderAvailability
    {
        public ProviderAvailability(
            TtsProviderKind provider,
            string displayName,
            bool isAvailable,
            string detail)
        {
            Provider = provider;
            DisplayName = displayName ?? provider.ToString();
            IsAvailable = isAvailable;
            Detail = detail ?? string.Empty;
        }

        public TtsProviderKind Provider { get; }
        public string DisplayName { get; }
        public bool IsAvailable { get; }
        public string Detail { get; }
    }
}
