using System;
using System.Collections.Generic;
using System.Linq;
using VoicePlugin.Config;

namespace VoicePlugin.Services
{
    public sealed class VoiceTextFormatter
    {
        private const int MaximumSpeechLength = 200;

        public IReadOnlyList<string> FormatBatch(
            IEnumerable<string> results,
            VoiceConfigSnapshot config)
        {
            if (results == null || config == null)
            {
                return Array.Empty<string>();
            }

            return results
                .Select(result => FormatOne(result, config))
                .Where(result => !string.IsNullOrWhiteSpace(result))
                .ToArray();
        }

        public string FormatOne(string result, VoiceConfigSnapshot config)
        {
            if (config == null || string.IsNullOrWhiteSpace(result))
            {
                return string.Empty;
            }

            var name = result.Trim();
            if (config.SpaceNumericDigits && IsAsciiDigits(name))
            {
                name = string.Join(" ", name.ToCharArray());
            }

            var rendered = config.SpeechTemplate
                .Replace(VoiceTextTemplate.Placeholder, name, StringComparison.Ordinal)
                .Trim();

            return rendered.Length <= MaximumSpeechLength
                ? rendered
                : rendered.Substring(0, MaximumSpeechLength - 3) + "...";
        }

        private static bool IsAsciiDigits(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;

            foreach (var character in value)
            {
                if (character < '0' || character > '9') return false;
            }

            return true;
        }
    }
}
