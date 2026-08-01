namespace VoicePlugin.Services
{
    public sealed class PronunciationRule
    {
        public PronunciationRule(string original, string replacement)
        {
            Original = original ?? string.Empty;
            Replacement = replacement ?? string.Empty;
        }

        public string Original { get; }
        public string Replacement { get; }
    }
}
