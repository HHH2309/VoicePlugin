using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoicePlugin.Config;

namespace VoicePlugin.Services
{
    /// <summary>
    /// 一个可预缓存的名单方案（来自宿主 Configs/Settings.json 的 RandSettings.NameRosters）。
    /// </summary>
    public sealed class VoiceRosterInfo
    {
        public string Guid { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string NamesContent { get; set; } = string.Empty;
        public string ReplaceContent { get; set; } = string.Empty;
    }

    /// <summary>
    /// 预缓存文本来源枚举：复刻宿主“随机抽选”的输入空间。
    /// <para>
    /// 宿主逻辑（已查证）：当前名单存于宿主目录 Names.txt（每行一个名字）+
    /// Replace.txt（"原名--&gt;替换名"精确整行替换）；无名单/空则随机数 1-60。
    /// 全部名单/指定名单从宿主 Configs/Settings.json 的 RandSettings.NameRosters
    /// 读取（每条含 namesContent/replaceContent）。产出与真实播报一致的最终
    /// 文本集合（模板展开 + 单数字朗读 + 自定义读音替换）。
    /// </para>
    /// </summary>
    internal static class PrecacheTextProvider
    {
        /// <summary>宿主随机数的固定范围（与宿主 NewStyleRollCallWindow 的 Next(1, 61) 一致）。</summary>
        public const int RandomNumberMax = 60;

        public static string NamesFilePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Names.txt");

        public static string ReplaceFilePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Replace.txt");

        private static string HostSettingsFilePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Configs", "Settings.json");

        /// <summary>枚举宿主 Settings.json 中的全部名单方案；读取失败返回空列表。</summary>
        public static IReadOnlyList<VoiceRosterInfo> GetRosters()
        {
            try
            {
                if (!File.Exists(HostSettingsFilePath)) return Array.Empty<VoiceRosterInfo>();

                var json = File.ReadAllText(HostSettingsFilePath);
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("randSettings", out var rand)
                    || !rand.TryGetProperty("nameRosters", out var rosters)
                    || rosters.ValueKind != JsonValueKind.Array)
                {
                    return Array.Empty<VoiceRosterInfo>();
                }

                var result = new List<VoiceRosterInfo>();
                foreach (var element in rosters.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object) continue;
                    var roster = new VoiceRosterInfo
                    {
                        Guid = GetString(element, "guid"),
                        Name = GetString(element, "name"),
                        NamesContent = GetString(element, "namesContent"),
                        ReplaceContent = GetString(element, "replaceContent")
                    };
                    if (!string.IsNullOrWhiteSpace(roster.Guid)
                        || !string.IsNullOrWhiteSpace(roster.Name))
                    {
                        result.Add(roster);
                    }
                }
                return result;
            }
            catch (Exception)
            {
                return Array.Empty<VoiceRosterInfo>();
            }
        }

        /// <summary>读取当前名单（应用 Replace 规则、去空、去重），无名单时返回空。</summary>
        public static IReadOnlyList<string> GetCandidateNames()
        {
            try
            {
                if (!File.Exists(NamesFilePath)) return Array.Empty<string>();

                var replaces = ReadReplaceRules(File.Exists(ReplaceFilePath)
                    ? File.ReadAllLines(ReplaceFilePath)
                    : Array.Empty<string>());
                return ApplyRules(File.ReadAllLines(NamesFilePath), replaces);
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// 按预缓存范围构建待预缓存的最终播报文本集合
        /// （与真实播报路径一致的模板/单数字/读音展开）。
        /// </summary>
        public static IReadOnlyList<string> BuildPrecacheTexts(
            VoiceConfigSnapshot config,
            VoiceTextFormatter formatter,
            PrecacheScopeMode scope,
            string rosterGuid)
        {
            if (config == null || formatter == null) return Array.Empty<string>();

            var candidates = ResolveCandidates(scope, rosterGuid);
            if (candidates.Count == 0)
            {
                // 无名单：随机数 1-60。
                candidates = Enumerable.Range(1, RandomNumberMax)
                    .Select(number => number.ToString())
                    .ToList();
            }

            var pronunciations = new PronunciationDictionary(
                config.EnablePronunciations ? config.Pronunciations : null);
            return formatter.FormatBatch(candidates, config)
                .Select(text => pronunciations.Apply(text))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>当前启用名单内容的指纹（Names.txt + Replace.txt 全文哈希），用于名单变更检测。</summary>
        public static string GetActiveRosterFingerprint()
        {
            try
            {
                var names = File.Exists(NamesFilePath)
                    ? File.ReadAllText(NamesFilePath)
                    : string.Empty;
                var replaces = File.Exists(ReplaceFilePath)
                    ? File.ReadAllText(ReplaceFilePath)
                    : string.Empty;
                using var sha = SHA256.Create();
                var bytes = sha.ComputeHash(
                    Encoding.UTF8.GetBytes(names + "" + replaces));
                var builder = new StringBuilder(32);
                for (var index = 0; index < 16; index++)
                {
                    builder.Append(bytes[index].ToString("x2"));
                }
                return builder.ToString();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static List<string> ResolveCandidates(
            PrecacheScopeMode scope,
            string rosterGuid)
        {
            switch (scope)
            {
                case PrecacheScopeMode.All:
                    // 全部名单并集。
                    var all = new List<string>();
                    foreach (var roster in GetRosters())
                    {
                        var replaces = ReadReplaceRules(SplitLines(roster.ReplaceContent));
                        all.AddRange(ApplyRules(SplitLines(roster.NamesContent), replaces));
                    }
                    return all.Distinct(StringComparer.Ordinal).ToList();

                case PrecacheScopeMode.Roster:
                    // 指定名单。
                    var target = GetRosters().FirstOrDefault(roster =>
                        string.Equals(roster.Guid, rosterGuid, StringComparison.OrdinalIgnoreCase));
                    if (target == null) return new List<string>();
                    var targetReplaces = ReadReplaceRules(SplitLines(target.ReplaceContent));
                    return ApplyRules(SplitLines(target.NamesContent), targetReplaces)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

                default:
                    // 当前启用名单（Names.txt / Replace.txt）。
                    return GetCandidateNames().ToList();
            }
        }

        private static List<string> ApplyRules(
            IEnumerable<string> lines,
            List<(string From, string To)> replaces)
        {
            var names = new List<string>();
            foreach (var raw in lines)
            {
                var name = raw?.Trim() ?? string.Empty;
                foreach (var (from, to) in replaces)
                {
                    if (string.Equals(name, from, StringComparison.Ordinal))
                    {
                        name = to;
                        break;
                    }
                }
                if (name.Length > 0) names.Add(name);
            }
            return names;
        }

        private static List<(string From, string To)> ReadReplaceRules(IEnumerable<string> lines)
        {
            var replaces = new List<(string From, string To)>();
            foreach (var raw in lines)
            {
                var line = raw?.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                var marker = line.IndexOf("-->", StringComparison.Ordinal);
                if (marker <= 0) continue; // 不含 --> 的行按宿主约定忽略
                var from = line.Substring(0, marker).Trim();
                var to = line.Substring(marker + 3).Trim();
                if (from.Length > 0) replaces.Add((from, to));
            }
            return replaces;
        }

        private static string[] SplitLines(string content)
        {
            if (string.IsNullOrEmpty(content)) return Array.Empty<string>();
            return content.Replace("\r\n", "\n").Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        private static string GetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : string.Empty;
        }
    }
}
