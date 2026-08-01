using System;
using System.Collections.Generic;

namespace VoicePlugin.Services
{
    public interface IPronunciationDictionary
    {
        string Apply(string text);
    }

    public sealed class PronunciationDictionary : IPronunciationDictionary
    {
        private readonly IReadOnlyList<PronunciationRule> _mappings;

        public PronunciationDictionary(IReadOnlyList<PronunciationRule> mappings = null)
        {
            _mappings = mappings ?? Array.Empty<PronunciationRule>();
        }

        public string Apply(string text)
        {
            var transformed = text ?? string.Empty;
            foreach (var mapping in _mappings)
            {
                if (mapping == null || string.IsNullOrEmpty(mapping.Original)) continue;
                transformed = transformed.Replace(
                    mapping.Original,
                    mapping.Replacement ?? string.Empty,
                    StringComparison.Ordinal);
            }

            return transformed;
        }
    }
}
