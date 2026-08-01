using System;
using System.Collections;
using System.Reflection;
using System.Threading.Tasks;
using VoicePlugin.Config;
using VoicePlugin.Services;

namespace VoicePlugin
{
    /// <summary>自动化引擎可选引擎（成员名即下拉显示文本）。</summary>
    public enum TtsEngineChoice
    {
        本地SAPI,
        本地Media,
        在线Edge
    }

    /// <summary>
    /// 自动化音色选择。
    /// 自动 = 引擎自动选择优先中文（相当于设置里留空）；
    /// 随机 = 每次播报随机选择（相当于设置里填 random）；
    /// 指定 = 使用“发音人ID”属性填写的发音人（留空则回落自动）。
    /// </summary>
    public enum TtsVoiceChoice
    {
        自动,
        随机,
        指定
    }

    /// <summary>
    /// 自动化语速分档（工作流编辑器的设置 UI 不支持滑条，
    /// 用分档下拉提供离散选择，范围 -10 ～ 10）。
    /// </summary>
    public enum RateChoice
    {
        极慢_10,
        较慢_5,
        正常_0,
        较快_5,
        极快_10
    }

    /// <summary>
    /// “切换 TTS 引擎/音色”行动的设置。
    /// 注：宿主工作流编辑器会渲染设置类的全部可写属性（无法做条件显隐），
    /// 因此“发音人ID”输入框始终显示，但仅在“音色=指定”时生效。
    /// </summary>
    public sealed class SwitchTtsActionSettings
    {
        public TtsEngineChoice 引擎 { get; set; } = TtsEngineChoice.本地SAPI;
        public TtsVoiceChoice 音色 { get; set; } = TtsVoiceChoice.自动;

        /// <summary>仅在“音色=指定”时生效；留空则回落自动（引擎自动优先中文）。</summary>
        public string 发音人ID_仅指定时生效留空回落自动 { get; set; } = string.Empty;
    }

    /// <summary>“当前TTS引擎”规则的设置（选择要匹配的引擎）。</summary>
    public sealed class EngineRuleSettings
    {
        public TtsEngineChoice 引擎 { get; set; } = TtsEngineChoice.本地SAPI;
    }

    /// <summary>“调整语速”行动的设置（分档下拉）。</summary>
    public sealed class SetRateActionSettings
    {
        public RateChoice 语速 { get; set; } = RateChoice.正常_0;
    }

    /// <summary>“调整播报模板”行动的设置（用 {name} 代表被抽中的姓名或学号）。</summary>
    public sealed class SetTemplateActionSettings
    {
        public string 播报模板 { get; set; } = "抽中了：{name}";
    }

    /// <summary>“开启/关闭自动播报”行动的设置。</summary>
    public sealed class SetEnabledActionSettings
    {
        public bool 开启自动播报 { get; set; } = true;
    }

    /// <summary>“TTS 播报中”规则的空设置（无设置项）。</summary>
    public sealed class SpeakingRuleSettings
    {
    }

    /// <summary>
    /// 自动化集成组件：向宿主自动化引擎注册 4 个行动（切换TTS引擎/音色、
    /// 调整语速、调整播报模板、开启/关闭自动播报）与 1 条规则（TTS播报中）。
    /// <para>
    /// 宿主自动化引擎（Ink_Canvas.WorkflowAutomation）的注册表是宿主程序集内的
    /// 静态字典（IActionService.Actions / IRulesetService.Rules），SDK 没有插件
    /// 接口，因此通过反射注册。工作流编辑器从这些字典读取列表，插件行动/规则
    /// 会自动出现在自动化设置页；设置 UI 由编辑器按设置类的公开可读写属性
    /// 自动生成（枚举→下拉、字符串→文本框、布尔→开关、整数→数字框），
    /// 无需宿主基类。
    /// </para>
    /// <para>
    /// 行动触发时通过应用回调修改插件配置并热生效；规则在求值时读取
    /// 播报状态（volatile 读，线程安全）。
    /// </para>
    /// </summary>
    internal sealed class AutomationActionComponent
    {
        public const string SwitchTtsActionId = "voice.switchtts";
        public const string SetRateActionId = "voice.setrate";
        public const string SetTemplateActionId = "voice.settemplate";
        public const string SetEnabledActionId = "voice.setenabled";
        public const string SpeakingRuleId = "voice.speaking";
        public const string EngineRuleId = "voice.engine";

        private const string ActionRegistryInfoTypeName =
            "Ink_Canvas.WorkflowAutomation.Models.ActionRegistryInfo";
        private const string ActionServiceTypeName =
            "Ink_Canvas.WorkflowAutomation.Abstractions.IActionService";
        private const string RuleRegistryInfoTypeName =
            "Ink_Canvas.WorkflowAutomation.Models.RuleRegistryInfo";
        private const string RulesetServiceTypeName =
            "Ink_Canvas.WorkflowAutomation.Abstractions.IRulesetService";

        private readonly Func<Action<VoiceConfig>, Task> _applyConfig;
        private readonly Func<bool> _isSpeaking;
        private readonly Func<VoiceConfigSnapshot> _getConfig;
        private readonly Action<string> _log;
        private readonly Action<string, Exception> _logError;

        public AutomationActionComponent(
            Func<Action<VoiceConfig>, Task> applyConfig,
            Func<bool> isSpeaking,
            Func<VoiceConfigSnapshot> getConfig,
            Action<string> log,
            Action<string, Exception> logError)
        {
            _applyConfig = applyConfig;
            _isSpeaking = isSpeaking;
            _getConfig = getConfig;
            _log = log;
            _logError = logError;
        }

        public void Install()
        {
            try
            {
                RegisterAction(
                    SwitchTtsActionId,
                    "切换TTS引擎/音色",
                    typeof(SwitchTtsActionSettings),
                    nameof(OnSwitchTtsTriggered));
                RegisterAction(
                    SetRateActionId,
                    "调整语速",
                    typeof(SetRateActionSettings),
                    nameof(OnSetRateTriggered));
                RegisterAction(
                    SetTemplateActionId,
                    "调整播报模板",
                    typeof(SetTemplateActionSettings),
                    nameof(OnSetTemplateTriggered));
                RegisterAction(
                    SetEnabledActionId,
                    "开启/关闭自动播报",
                    typeof(SetEnabledActionSettings),
                    nameof(OnSetEnabledTriggered));
                RegisterSpeakingRule();
                RegisterEngineRule();
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] failed to register the automation items.", ex);
            }
        }

        /// <summary>
        /// 插件卸载时从宿主注册表中移除本插件注册的行动与规则，
        /// 避免禁用插件后列表残留。
        /// </summary>
        public void Unregister()
        {
            try
            {
                var actions = GetStaticDictionary(ActionServiceTypeName, "Actions");
                if (actions != null)
                {
                    actions.Remove(SwitchTtsActionId);
                    actions.Remove(SetRateActionId);
                    actions.Remove(SetTemplateActionId);
                    actions.Remove(SetEnabledActionId);
                }

                var rules = GetStaticDictionary(RulesetServiceTypeName, "Rules");
                if (rules != null)
                {
                    rules.Remove(SpeakingRuleId);
                    rules.Remove(EngineRuleId);
                }
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] failed to unregister the automation items.", ex);
            }
        }

        /// <summary>
        /// 播报状态变化时通知宿主自动化引擎重新求值规则
        /// （让“TTS播报中”等规则实时反映状态）。内部切到 UI 线程调用。
        /// <para>
        /// 宿主的 <c>RulesetService.NotifyStatusChanged()</c> 是实例方法，
        /// 不能按静态方法调用（Invoke(null, …) 会抛 TargetException）。
        /// 实例经 <c>AutomationBootstrap.Service.RulesetService</c>（均为
        /// 公开静态/实例属性）解析后调用；取不到实例时静默降级——
        /// 宿主自身还有 5 秒兜底轮询，规则最终仍会求值。
        /// </para>
        /// </summary>
        public void NotifySpeakingStateChanged()
        {
            try
            {
                var ruleset = ResolveRulesetService();
                if (ruleset == null) return;

                var notifyMethod = ruleset.GetType().GetMethod(
                    "NotifyStatusChanged",
                    BindingFlags.Public | BindingFlags.Instance);
                if (notifyMethod == null) return;

                // 宿主规则求值可能触碰 UI，统一派发到 UI 线程。
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null) return;

                void Notify()
                {
                    try
                    {
                        notifyMethod.Invoke(ruleset, null);
                    }
                    catch (Exception ex)
                    {
                        _logError?.Invoke("[Voice] failed to notify the automation ruleset status.", ex);
                    }
                }

                if (dispatcher.CheckAccess())
                {
                    Notify();
                }
                else
                {
                    dispatcher.BeginInvoke(new Action(Notify));
                }
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] failed to notify the automation ruleset status.", ex);
            }
        }

        /// <summary>
        /// 解析宿主 RulesetService 实例：
        /// AutomationBootstrap.Service（静态）→ AutomationService.RulesetService（实例）。
        /// </summary>
        private static object ResolveRulesetService()
        {
            var bootstrapType = FindType("Ink_Canvas.WorkflowAutomation.AutomationBootstrap");
            if (bootstrapType == null) return null;

            var service = bootstrapType.GetProperty(
                "Service",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (service == null) return null;

            return service.GetType().GetProperty(
                "RulesetService",
                BindingFlags.Public | BindingFlags.Instance)?.GetValue(service);
        }

        private void RegisterAction(
            string id,
            string name,
            Type settingsType,
            string handlerMethodName)
        {
            var registryType = FindType(ActionRegistryInfoTypeName);
            if (registryType == null)
            {
                _log?.Invoke("[Voice] the host automation registry was not found; automation actions are unavailable.");
                return;
            }

            var actions = GetStaticDictionary(ActionServiceTypeName, "Actions");
            if (actions == null || actions.Contains(id)) return;

            var info = Activator.CreateInstance(
                registryType,
                new object[] { id, name, "CogOutline" });
            if (info == null) return;

            registryType.GetProperty("SettingsType")
                ?.SetValue(info, settingsType);

            var handleDelegateType = registryType.GetNestedType("HandleDelegate");
            var handlerMethod = GetType().GetMethod(
                handlerMethodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (handleDelegateType == null || handlerMethod == null) return;

            var handle = Delegate.CreateDelegate(handleDelegateType, this, handlerMethod);
            registryType.GetField("Handle")?.SetValue(info, handle);

            actions[id] = info;
            _log?.Invoke("[Voice] registered the automation action: " + name);
        }

        private void RegisterSpeakingRule()
        {
            RegisterRule(
                SpeakingRuleId,
                "TTS播报中",
                typeof(SpeakingRuleSettings),
                nameof(OnSpeakingRuleEvaluate));
        }

        private void RegisterEngineRule()
        {
            RegisterRule(
                EngineRuleId,
                "当前TTS引擎",
                typeof(EngineRuleSettings),
                nameof(OnEngineRuleEvaluate));
        }

        private void RegisterRule(
            string id,
            string name,
            Type settingsType,
            string handlerMethodName)
        {
            var registryType = FindType(RuleRegistryInfoTypeName);
            if (registryType == null) return;

            var rules = GetStaticDictionary(RulesetServiceTypeName, "Rules");
            if (rules == null || rules.Contains(id)) return;

            var info = Activator.CreateInstance(
                registryType,
                new object[] { id, name, "CogOutline" });
            if (info == null) return;

            registryType.GetProperty("SettingsType")
                ?.SetValue(info, settingsType);

            var handleDelegateType = registryType.GetNestedType("HandleDelegate");
            var handlerMethod = GetType().GetMethod(
                handlerMethodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (handleDelegateType == null || handlerMethod == null) return;

            var handle = Delegate.CreateDelegate(handleDelegateType, this, handlerMethod);
            registryType.GetField("Handle")?.SetValue(info, handle);

            rules[id] = info;
            _log?.Invoke("[Voice] registered the automation rule: " + name);
        }

        private void OnSwitchTtsTriggered(object settings, string guid)
        {
            if (settings is not SwitchTtsActionSettings s) return;

            var provider = s.引擎 switch
            {
                TtsEngineChoice.本地Media => TtsProviderKind.WinRt,
                TtsEngineChoice.在线Edge => TtsProviderKind.EdgeOnline,
                _ => TtsProviderKind.Sapi
            };
            // 音色选择：自动 → 留空（引擎自动优先中文）；随机 → random 标记；
            // 指定 → 使用填写的发音人 ID，留空则回落自动。
            var voiceId = s.音色 switch
            {
                TtsVoiceChoice.随机 => VoiceConfigSnapshot.RandomVoiceId,
                TtsVoiceChoice.指定 => s.发音人ID_仅指定时生效留空回落自动?.Trim() ?? string.Empty,
                _ => string.Empty
            };

            InvokeApply(config =>
            {
                config.Provider = provider.ToString();
                config.VoiceId = voiceId;
            });
        }

        private void OnSetRateTriggered(object settings, string guid)
        {
            if (settings is not SetRateActionSettings s) return;

            var rate = s.语速 switch
            {
                RateChoice.极慢_10 => -10,
                RateChoice.较慢_5 => -5,
                RateChoice.较快_5 => 5,
                RateChoice.极快_10 => 10,
                _ => 0
            };
            InvokeApply(config => config.Rate = rate);
        }

        private void OnSetTemplateTriggered(object settings, string guid)
        {
            if (settings is not SetTemplateActionSettings s) return;

            InvokeApply(config => config.SpeechTemplate = s.播报模板 ?? string.Empty);
        }

        private void OnSetEnabledTriggered(object settings, string guid)
        {
            if (settings is not SetEnabledActionSettings s) return;

            InvokeApply(config => config.Enabled = s.开启自动播报);
        }

        private bool OnSpeakingRuleEvaluate(object settings)
        {
            try
            {
                return _isSpeaking?.Invoke() ?? false;
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] the automation speaking rule failed.", ex);
                return false;
            }
        }

        private bool OnEngineRuleEvaluate(object settings)
        {
            try
            {
                if (settings is not EngineRuleSettings s) return false;
                if (_getConfig == null) return false;

                var provider = s.引擎 switch
                {
                    TtsEngineChoice.本地Media => TtsProviderKind.WinRt,
                    TtsEngineChoice.在线Edge => TtsProviderKind.EdgeOnline,
                    _ => TtsProviderKind.Sapi
                };
                return _getConfig()?.Provider == provider;
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] the automation engine rule failed.", ex);
                return false;
            }
        }

        private void InvokeApply(Action<VoiceConfig> mutate)
        {
            try
            {
                _ = _applyConfig(mutate);
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] an automation action failed to apply.", ex);
            }
        }

        private static IDictionary GetStaticDictionary(string typeName, string propertyName)
        {
            var type = FindType(typeName);
            if (type == null) return null;

            return type.GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IDictionary;
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName);
                    if (type != null) return type;
                }
                catch
                {
                }
            }
            return null;
        }
    }
}
