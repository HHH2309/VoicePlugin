using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ink_Canvas.Plugins;
using VoicePlugin.Config;

namespace VoicePlugin
{
    /// <summary>
    /// URI 路由组件：通过宿主 <see cref="IPluginUriService"/> 注册
    /// <c>icc://plugin/voice/</c> 深链接。
    /// <para>
    /// 动作类：
    /// <list type="bullet">
    /// <item><c>icc://plugin/voice/speak?text=…</c> —— 直接播报文本（经播报队列，
    /// 遵守打断抢占、发音词典与“自动播报”开关；播报延迟按当前配置生效）。</item>
    /// <item><c>icc://plugin/voice/stop</c> —— 截断（停止）当前播报并清空待播队列。</item>
    /// <item><c>icc://plugin/voice/sfx?path=…</c> —— 独立播放一个音效文件
    /// （不经播报队列；点按“播报截断”会一并停止它）。</item>
    /// </list>
    /// 配置类：
    /// <list type="bullet">
    /// <item><c>icc://plugin/voice/settings?key=value&amp;…</c> —— 按白名单热修改配置
    /// （自动保存并热生效；数值由快照归一化钳制）。</item>
    /// <item><c>icc://plugin/voice/toggle-mute</c> —— 快捷开关“自动播报”。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 宿主保证处理器在 UI 线程调用；插件卸载时宿主自动注销处理器，无需手动注销。
    /// 处理器返回 false 时宿主记录“未处理”日志。
    /// </para>
    /// </summary>
    internal sealed class UriRouteComponent
    {
        private readonly IPluginUriService _uriService;
        private readonly Func<VoiceConfigSnapshot> _getConfig;
        private readonly Func<Action<VoiceConfig>, Task> _applyConfig;
        private readonly Action _stopSpeaking;
        private readonly Action<string> _enqueueSpeechText;
        private readonly Func<string, Task> _playSfx;
        private readonly Action<string> _log;
        private readonly Action<string, Exception> _logError;

        public UriRouteComponent(
            IPluginUriService uriService,
            Func<VoiceConfigSnapshot> getConfig,
            Func<Action<VoiceConfig>, Task> applyConfig,
            Action stopSpeaking,
            Action<string> enqueueSpeechText,
            Func<string, Task> playSfx,
            Action<string> log,
            Action<string, Exception> logError)
        {
            _uriService = uriService ?? throw new ArgumentNullException(nameof(uriService));
            _getConfig = getConfig ?? throw new ArgumentNullException(nameof(getConfig));
            _applyConfig = applyConfig ?? throw new ArgumentNullException(nameof(applyConfig));
            _stopSpeaking = stopSpeaking ?? throw new ArgumentNullException(nameof(stopSpeaking));
            _enqueueSpeechText = enqueueSpeechText
                ?? throw new ArgumentNullException(nameof(enqueueSpeechText));
            _playSfx = playSfx ?? throw new ArgumentNullException(nameof(playSfx));
            _log = log;
            _logError = logError;
        }

        public void Install()
        {
            if (_uriService == null) return;
            try
            {
                _uriService.RegisterHandler("speak", OnSpeak);
                _uriService.RegisterHandler("stop", OnStop);
                _uriService.RegisterHandler("sfx", OnSfx);
                _uriService.RegisterHandler("settings", OnSettings);
                _uriService.RegisterHandler("toggle-mute", OnToggleMute);
                _log?.Invoke("[Voice] registered icc://plugin/voice/ URI routes.");
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] failed to register URI routes.", ex);
            }
        }

        private bool OnSpeak(PluginUriRequest request)
        {
            if (request.Query == null
                || !request.Query.TryGetValue("text", out var text)
                || string.IsNullOrWhiteSpace(text))
            {
                _log?.Invoke("[Voice] URI speak 缺少 text 参数。");
                return false;
            }

            _enqueueSpeechText(text);
            return true;
        }

        private bool OnStop(PluginUriRequest request)
        {
            _stopSpeaking();
            return true;
        }

        private bool OnSfx(PluginUriRequest request)
        {
            if (request.Query == null
                || !request.Query.TryGetValue("path", out var path)
                || string.IsNullOrWhiteSpace(path))
            {
                _log?.Invoke("[Voice] URI sfx 缺少 path 参数。");
                return false;
            }

            _ = _playSfx(path);
            return true;
        }

        private bool OnSettings(PluginUriRequest request)
        {
            if (request.Query == null || request.Query.Count == 0)
            {
                _log?.Invoke("[Voice] URI settings 未提供任何配置项。");
                return false;
            }

            // ApplyConfigAsync 的 mutate 回调同步执行，返回前 applied 已确定。
            var applied = 0;
            _applyConfig(config =>
            {
                applied = ApplyQueryToConfig(config, request.Query);
            });
            return applied > 0;
        }

        private bool OnToggleMute(PluginUriRequest request)
        {
            _applyConfig(config => config.Enabled = !_getConfig().Enabled);
            return true;
        }

        private int ApplyQueryToConfig(
            VoiceConfig config,
            IReadOnlyDictionary<string, string> query)
        {
            var applied = 0;
            foreach (var pair in query)
            {
                if (TryApplySetting(config, pair.Key, pair.Value))
                {
                    applied++;
                }
                else
                {
                    _log?.Invoke(
                        $"[Voice] URI settings 忽略未知或非法的配置项: {pair.Key}={pair.Value}");
                }
            }
            return applied;
        }

        /// <summary>配置白名单：未知键返回 false；数值/开关交给快照构造时的钳制兜底。</summary>
        private bool TryApplySetting(VoiceConfig config, string key, string value)
        {
            switch (key.ToLowerInvariant())
            {
                case "enabled":
                    return TrySetBool(value, enabled => config.Enabled = enabled);
                case "provider":
                    config.Provider = value;
                    return true;
                case "voiceid":
                    config.VoiceId = value;
                    return true;
                case "rate":
                    return TrySetInt(value, rate => config.Rate = rate);
                case "volume":
                    return TrySetInt(value, volume => config.Volume = volume);
                case "template":
                    config.SpeechTemplate = value;
                    return true;
                case "delay":
                    return TrySetInt(value, delay => config.DelayMilliseconds = delay);
                case "spacedigits":
                    return TrySetBool(value, digits => config.SpaceNumericDigits = digits);
                case "pronunciations":
                    return TrySetBool(value, enabled => config.EnablePronunciations = enabled);
                case "cueenabled":
                    return TrySetBool(value, enabled => config.Cue.Enabled = enabled);
                case "cuepath":
                    config.Cue.Path = value;
                    return true;
                case "cuegap":
                    return TrySetInt(value, gap => config.Cue.GapMilliseconds = gap);
                case "postcueenabled":
                    return TrySetBool(value, enabled => config.PostCue.Enabled = enabled);
                case "postcuepath":
                    config.PostCue.Path = value;
                    return true;
                case "postcuegap":
                    return TrySetInt(value, gap => config.PostCue.GapMilliseconds = gap);
                case "edgetimeout":
                    return TrySetInt(value, timeout => config.Edge.TimeoutMilliseconds = timeout);
                case "edgefallback":
                    return TrySetBool(value, fallback => config.Edge.FallbackToLocal = fallback);
                default:
                    return false;
            }
        }

        private static bool TrySetBool(string value, Action<bool> setter)
        {
            if (bool.TryParse(value, out var boolean))
            {
                setter(boolean);
                return true;
            }
            if (string.Equals(value, "1", StringComparison.Ordinal))
            {
                setter(true);
                return true;
            }
            if (string.Equals(value, "0", StringComparison.Ordinal))
            {
                setter(false);
                return true;
            }
            return false;
        }

        private static bool TrySetInt(string value, Action<int> setter)
        {
            if (int.TryParse(value, out var number))
            {
                setter(number);
                return true;
            }
            return false;
        }
    }
}
