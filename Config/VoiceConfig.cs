using System.Collections.Generic;

namespace VoicePlugin.Config
{
    /// <summary>播报音频缓存的限制方式。</summary>
    public enum AudioCacheLimitMode
    {
        /// <summary>按容量上限（LRU 淘汰）。</summary>
        Size,
        /// <summary>按保留天数（超期删除）。</summary>
        Date,
        /// <summary>不限制。</summary>
        Unlimited
    }

    /// <summary>预缓存的内容范围。</summary>
    public enum PrecacheScopeMode
    {
        /// <summary>当前启用名单（Names.txt；无名单则随机数 1-60）。</summary>
        Current,
        /// <summary>软件内全部名单的并集。</summary>
        All,
        /// <summary>指定名单（PrecacheRosterGuid）。</summary>
        Roster
    }

    /// <summary>
    /// Voice 插件配置。
    /// </summary>
    public class VoiceConfig
    {
        public int SchemaVersion { get; set; } = 2;
        public bool Enabled { get; set; } = true;
        public string Provider { get; set; } = "Sapi";
        public string VoiceId { get; set; } = string.Empty;
        public int Rate { get; set; }
        public int Volume { get; set; } = 100;
        public string SpeechTemplate { get; set; } = VoiceTextTemplate.DefaultTemplate;
        public int DelayMilliseconds { get; set; }
        public bool SpaceNumericDigits { get; set; }
        /// <summary>自定义读音替换（整体默认关闭；规则列表默认含一条彩蛋规则）。</summary>
        public bool EnablePronunciations { get; set; }
        /// <summary>是否在系统托盘右键菜单中显示“开启/关闭自动播报”开关。</summary>
        public bool ShowTrayToggle { get; set; } = true;
        /// <summary>截断播报时是否显示应用内通知。</summary>
        public bool NotifyOnStop { get; set; } = true;
        /// <summary>是否启用播报音频缓存（Edge/WinRT 引擎命中后直接播放缓存音频）。</summary>
        public bool EnableAudioCache { get; set; } = true;
        /// <summary>缓存限制方式：按容量 / 按日期 / 不限制。</summary>
        public AudioCacheLimitMode CacheLimitMode { get; set; } = AudioCacheLimitMode.Size;
        /// <summary>播报音频缓存容量上限（MB，按容量模式）。</summary>
        public int AudioCacheSizeMb { get; set; } = 50;
        /// <summary>缓存保留天数（按日期模式，1-30）。</summary>
        public int AudioCacheRetentionDays { get; set; } = 7;
        /// <summary>预缓存内容范围：当前启用名单 / 全部名单 / 指定名单。</summary>
        public PrecacheScopeMode PrecacheScope { get; set; } = PrecacheScopeMode.Current;
        /// <summary>预缓存指定名单（PrecacheScope=Roster 时的名单 Guid）。</summary>
        public string PrecacheRosterGuid { get; set; } = string.Empty;
        /// <summary>软件启动后是否后台自动预缓存播报内容。</summary>
        public bool PrecacheOnStartup { get; set; } = true;
        /// <summary>切换名单后是否清除上次的缓存音频。</summary>
        public bool ClearCacheOnRosterChange { get; set; } = true;
        /// <summary>预缓存完成时是否显示应用内通知。</summary>
        public bool NotifyPrecacheCompleted { get; set; } = true;
        /// <summary>是否显示缓存分析（命中率/体积等统计）。</summary>
        public bool EnableCacheStats { get; set; }
        /// <summary>是否启用全局热键“截断播报”。</summary>
        public bool EnableStopHotkey { get; set; } = true;
        /// <summary>全局热键修饰键（ModifierKeys 的数值）。</summary>
        public uint StopHotkeyModifiers { get; set; } = 3; // Ctrl + Alt
        /// <summary>全局热键主键（虚拟键码）。</summary>
        public uint StopHotkeyKey { get; set; } = 0x53; // S
        public CueConfig Cue { get; set; } = new CueConfig();
        public PostCueConfig PostCue { get; set; } = new PostCueConfig();
        public EdgeConfig Edge { get; set; } = new EdgeConfig();
        public List<PronunciationRuleConfig> Pronunciations { get; set; }
            = new List<PronunciationRuleConfig>
            {
                new PronunciationRuleConfig
                {
                    Original = "cjk",
                    Replacement = "cjk为什么一直在响"
                }
            };

        // Legacy INI-only properties retained for migration.
        public bool EnablePrefix { get; set; } = true;
        public string PrefixText { get; set; } = "抽中了：";
        public bool EnableSuffix { get; set; }
        public string SuffixText { get; set; } = string.Empty;
    }

    public sealed class CueConfig
    {
        public bool Enabled { get; set; }
        public string Path { get; set; } = string.Empty;
        public int GapMilliseconds { get; set; }
    }

    /// <summary>播报后置音：语音朗读完毕后播放。</summary>
    public sealed class PostCueConfig
    {
        public bool Enabled { get; set; }
        public string Path { get; set; } = string.Empty;
        /// <summary>朗读结束到后置音开始之间的间隔（毫秒）。</summary>
        public int GapMilliseconds { get; set; }
    }

    public sealed class EdgeConfig
    {
        public int TimeoutMilliseconds { get; set; } = 10000;
        public bool FallbackToLocal { get; set; } = true;
    }

    public sealed class PronunciationRuleConfig
    {
        public string Original { get; set; } = string.Empty;
        public string Replacement { get; set; } = string.Empty;
    }
}
