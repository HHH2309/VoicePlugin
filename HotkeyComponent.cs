using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ink_Canvas.Plugins;
using VoicePlugin.Config;

namespace VoicePlugin
{
    /// <summary>
    /// 全局热键“截断播报”组件。
    /// <para>
    /// 与软件内置快捷键**集成在同一处**：通过宿主官方 <see cref="IHotkeyService"/>
    /// 注册（共用 GlobalHotkeyManager 的冲突处理与模式开关），并动态向宿主的
    /// “快捷键设置”页注入一个 <c>HotkeyItem</c>（与撤销/清空等内置项并列），
    /// 在该页直接捕获/修改组合键——变更持久化到插件配置并即时重注册。
    /// 插件设置页只保留“启用全局热键”开关；开关关闭时立即注销热键，
    /// 并从宿主快捷键页移除注入项（重新开启后自动重新注入）。
    /// </para>
    /// </summary>
    internal sealed class HotkeyComponent : IDisposable
    {
        private const string HotkeyId = "VoiceStop";
        private const string InjectedTag = "VoicePlugin.HotkeyItem";

        private const string SettingsWindowTypeName =
            "Ink_Canvas.Windows.SettingsViews.SettingsWindow";
        private const string HotkeyPageTypeName =
            "Ink_Canvas.Windows.SettingsViews.Pages.HotkeyPage";
        private const string HotkeyItemTypeName = "Ink_Canvas.Windows.HotkeyItem";

        private readonly IHotkeyService _hotkeys;
        private readonly Func<Action<VoiceConfig>, Task> _applyConfig;
        private readonly Action _stopSpeaking;
        private readonly Action<string> _log;
        private readonly Action<string, Exception> _logError;

        private DispatcherTimer _injectionTimer;
        private bool _registered;
        private bool _enabled;
        private uint _modifiers;
        private uint _key;
        private object _injectedPage;   // 已注入的页面实例（弱引用语义：页面重建后重新注入）
        private object _injectedItem;
        private object _injectedContainer; // 注入项所在的宿主容器（移除时使用）
        private bool _disposed;

        public HotkeyComponent(
            IHotkeyService hotkeys,
            Func<Action<VoiceConfig>, Task> applyConfig,
            Action stopSpeaking,
            Action<string> log,
            Action<string, Exception> logError)
        {
            _hotkeys = hotkeys;
            _applyConfig = applyConfig;
            _stopSpeaking = stopSpeaking;
            _log = log;
            _logError = logError;
        }

        /// <summary>按配置应用热键（开关/组合键变化即时重注册）。</summary>
        public void Apply(VoiceConfigSnapshot config)
        {
            if (config == null) return;

            _enabled = config.EnableStopHotkey;
            _modifiers = config.StopHotkeyModifiers;
            _key = config.StopHotkeyKey;

            if (_registered)
            {
                try
                {
                    _hotkeys.Unregister(HotkeyId);
                }
                catch (Exception ex)
                {
                    _logError?.Invoke("[Voice] failed to unregister the stop hotkey.", ex);
                }
                _registered = false;
            }

            if (!_enabled)
            {
                // 开关关闭：若“快捷键设置”页仍打开，把注入项从宿主页移除。
                RemoveInjectedItemOnUiThread();
                return;
            }
            if (_hotkeys == null) return;

            try
            {
                _registered = _hotkeys.Register(
                    HotkeyId,
                    _modifiers,
                    _key,
                    () =>
                    {
                        try
                        {
                            _stopSpeaking();
                        }
                        catch (Exception ex)
                        {
                            _logError?.Invoke("[Voice] the stop hotkey action failed.", ex);
                        }
                    });
                if (_registered)
                {
                    _log?.Invoke("[Voice] the global stop hotkey is active.");
                }
                else
                {
                    _log?.Invoke("[Voice] the global stop hotkey could not be registered (possibly in use or disabled).");
                }
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] failed to register the stop hotkey.", ex);
            }

            // 快捷键页已打开时同步显示。
            EnsureInjectionTimer();
        }

        public void Dispose()
        {
            _disposed = true;
            if (_injectionTimer != null)
            {
                try
                {
                    _injectionTimer.Stop();
                }
                catch
                {
                }
                _injectionTimer = null;
            }
            if (_registered)
            {
                _registered = false;
                try
                {
                    _hotkeys?.Unregister(HotkeyId);
                }
                catch (Exception ex)
                {
                    _logError?.Invoke("[Voice] failed to unregister the stop hotkey on shutdown.", ex);
                }
            }
            // 若设置窗口仍打开，把注入项从宿主快捷键页移除。
            RemoveInjectedItemOnUiThread();
            _injectedItem = null;
            _injectedPage = null;
            _injectedContainer = null;
        }

        private void EnsureInjectionTimer()
        {
            if (_injectionTimer != null || _disposed) return;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            void Create()
            {
                if (_injectionTimer != null || _disposed) return;
                _injectionTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _injectionTimer.Tick += (sender, args) => CheckInjectHostPage();
                _injectionTimer.Start();
            }

            if (dispatcher.CheckAccess())
            {
                Create();
            }
            else
            {
                dispatcher.BeginInvoke(new Action(Create));
            }
        }

        /// <summary>
        /// 周期检测“快捷键设置”页：未注入则注入 HotkeyItem，
        /// 已注入则按当前配置同步显示。
        /// </summary>
        private void CheckInjectHostPage()
        {
            if (_disposed) return;
            if (Application.Current == null) return;

            try
            {
                foreach (Window window in Application.Current.Windows)
                {
                    if (window.GetType().FullName != SettingsWindowTypeName) continue;

                    var page = FindDescendant(window, HotkeyPageTypeName);
                    if (page == null) break; // 设置窗口开着但还没到快捷键页

                    if (!ReferenceEquals(_injectedPage, page))
                    {
                        _injectedPage = null;
                        _injectedItem = null;
                        _injectedContainer = null;
                        if (_enabled)
                        {
                            TryInjectItem(page);
                        }
                        return;
                    }

                    // 已注入：开关开启→同步显示；开关关闭→移除注入项。
                    if (_enabled)
                    {
                        SyncInjectedItem();
                    }
                    else
                    {
                        RemoveInjectedItemOnUiThread();
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] hotkey page injection check failed.", ex);
            }
        }

        private void TryInjectItem(FrameworkElement page)
        {
            try
            {
                var itemType = FindType(HotkeyItemTypeName);
                if (itemType == null) return;

                var container = FindItemsContainer(page);
                if (container == null) return;

                var item = Activator.CreateInstance(itemType);
                if (item == null) return;

                itemType.GetProperty("Title")?.SetValue(item, "截断播报（语音播报插件）");
                itemType.GetProperty("Description")?.SetValue(
                    item,
                    "在任意窗口按下组合键立即截断当前播报");
                itemType.GetProperty("HotkeyName")?.SetValue(item, HotkeyId);

                // 显示当前绑定。
                var setCurrent = itemType.GetMethod("SetCurrentHotkey");
                if (setCurrent != null)
                {
                    var key = KeyInterop.KeyFromVirtualKey((int)_key);
                    var modifiers = (ModifierKeys)_modifiers;
                    setCurrent.Invoke(item, new object[] { key, modifiers });
                }

                // 变更事件：持久化到插件配置并重注册。
                var changedEvent = itemType.GetEvent("HotkeyChanged");
                if (changedEvent != null && changedEvent.EventHandlerType != null)
                {
                    var handlerMethod = GetType().GetMethod(
                        nameof(OnHostHotkeyChanged),
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    if (handlerMethod != null)
                    {
                        var handler = Delegate.CreateDelegate(
                            changedEvent.EventHandlerType,
                            this,
                            handlerMethod);
                        changedEvent.AddEventHandler(item, handler);
                    }
                }

                (item as FrameworkElement).Tag = InjectedTag;

                // 紧跟原有设置项：插到“保存/恢复默认”按钮行之前，
                // 而非追加到页面最底部（按钮行是嵌套面板，需在其外层
                // 面板的直接子项中定位包含“恢复默认”按钮的那个）。
                var containerChildren = GetChildrenList(container);
                var insertIndex = FindInsertIndex(containerChildren);
                containerChildren?.Insert(insertIndex, item);

                _injectedPage = page;
                _injectedItem = item;
                _injectedContainer = container;
                _log?.Invoke("[Voice] injected the stop hotkey item into the host hotkey page.");
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] failed to inject the hotkey item into the host page.", ex);
            }
        }

        private void SyncInjectedItem()
        {
            if (_injectedItem == null) return;

            try
            {
                var setCurrent = _injectedItem.GetType().GetMethod("SetCurrentHotkey");
                setCurrent?.Invoke(
                    _injectedItem,
                    new object[]
                    {
                        KeyInterop.KeyFromVirtualKey((int)_key),
                        (ModifierKeys)_modifiers
                    });
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] failed to sync the injected hotkey item.", ex);
            }
        }

        /// <summary>
        /// 把注入项从宿主“快捷键设置”页移除（UI 线程执行，必要时派发）。
        /// 捕获引用后派发，避免 Apply/Dispose 清空字段后异步回调落空。
        /// </summary>
        private void RemoveInjectedItemOnUiThread()
        {
            if (_injectedItem == null) return;

            var item = _injectedItem;
            var container = _injectedContainer;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                RemoveInjectedItemCore(item, container);
                return;
            }
            dispatcher.BeginInvoke(new Action(() => RemoveInjectedItemCore(item, container)));
        }

        private void RemoveInjectedItemCore(object item, object container)
        {
            try
            {
                // 页面可能已重建：记录的容器失效时回退到元素的逻辑父级。
                var target = container;
                if (target == null && item is FrameworkElement element)
                {
                    target = element.Parent;
                }
                var containerChildren = target == null ? null : GetChildrenList(target);
                containerChildren?.Remove(item);
                _log?.Invoke("[Voice] removed the stop hotkey item from the host hotkey page.");
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] failed to remove the injected hotkey item.", ex);
            }
            finally
            {
                if (ReferenceEquals(_injectedItem, item))
                {
                    _injectedItem = null;
                    _injectedPage = null;
                    _injectedContainer = null;
                }
            }
        }

        private void OnHostHotkeyChanged(object sender, EventArgs e)
        {
            try
            {
                // 参数类型为宿主 HotkeyChangedEventArgs，经反射读取。
                var key = (Key)(e?.GetType().GetProperty("Key")?.GetValue(e) ?? Key.None);
                var modifiers = (ModifierKeys)(e?.GetType()
                    .GetProperty("Modifiers")?.GetValue(e) ?? ModifierKeys.None);

                if (key == Key.None)
                {
                    return;
                }

                _modifiers = (uint)modifiers;
                _key = (uint)KeyInterop.VirtualKeyFromKey(key);

                // 持久化到插件配置并重注册（ApplyConfigAsync 内部走 UpdateConfigAsync）。
                if (_applyConfig != null)
                {
                    _ = _applyConfig(config =>
                    {
                        config.EnableStopHotkey = true;
                        config.StopHotkeyModifiers = _modifiers;
                        config.StopHotkeyKey = _key;
                    });
                }
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] failed to apply the host hotkey change.", ex);
            }
        }

        /// <summary>
        /// 计算注入项的插入位置：
        /// 优先插在“保存/恢复默认”按钮行之前（紧跟原有设置项）；
        /// 找不到按钮行时回退到最后一个内置 HotkeyItem 之后；
        /// 兜底追加到末尾。
        /// </summary>
        private static int FindInsertIndex(System.Collections.IList children)
        {
            if (children == null) return 0;
            var count = children.Count;

            // 按钮行：包含“恢复默认”（BtnResetToDefault）按钮的嵌套面板，
            // 位于主容器直接子项中，插入到它之前。
            for (var index = 0; index < count; index++)
            {
                if (children[index] is DependencyObject child
                    && ContainsNamedDescendant(child, "BtnResetToDefault"))
                {
                    return index;
                }
            }

            // 回退：最后一个内置 HotkeyItem 之后。
            var hotkeyItemType = FindType(HotkeyItemTypeName);
            if (hotkeyItemType != null)
            {
                for (var index = count - 1; index >= 0; index--)
                {
                    if (children[index] is DependencyObject child
                        && hotkeyItemType.IsAssignableFrom(child.GetType()))
                    {
                        return index + 1;
                    }
                }
            }

            return count;
        }

        private static bool ContainsNamedDescendant(
            DependencyObject root,
            string name)
        {
            if (root is FrameworkElement element
                && string.Equals(element.Name, name, StringComparison.Ordinal))
            {
                return true;
            }
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var index = 0; index < count; index++)
            {
                if (ContainsNamedDescendant(
                    VisualTreeHelper.GetChild(root, index),
                    name))
                {
                    return true;
                }
            }
            return false;
        }

        private static object FindItemsContainer(FrameworkElement page)
        {
            // 外层面板：包含 HotkeyItem 子项的纵向面板（MaxWidth=1000 的 SimpleStackPanel）。
            var panelType = FindType("iNKORE.UI.WPF.Controls.SimpleStackPanel");
            var hotkeyItemType = FindType(HotkeyItemTypeName);
            return FindDescendant(page, node =>
                node != null
                && (panelType == null || panelType.IsAssignableFrom(node.GetType()))
                && HasChildOfType(node, hotkeyItemType));
        }

        private static bool HasChildOfType(DependencyObject parent, Type type)
        {
            if (type == null) return false;
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var index = 0; index < count; index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (type.IsAssignableFrom(child.GetType())) return true;
                if (HasChildOfType(child, type)) return true;
            }
            return false;
        }

        private static FrameworkElement FindDescendant(
            DependencyObject root,
            string typeFullName)
        {
            if (root == null) return null;
            if (root.GetType().FullName == typeFullName)
            {
                return root as FrameworkElement;
            }
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var index = 0; index < count; index++)
            {
                var found = FindDescendant(VisualTreeHelper.GetChild(root, index), typeFullName);
                if (found != null) return found;
            }
            return null;
        }

        private static object FindDescendant(
            DependencyObject root,
            Func<DependencyObject, bool> predicate)
        {
            if (root == null) return null;
            if (predicate(root)) return root;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var index = 0; index < count; index++)
            {
                var found = FindDescendant(VisualTreeHelper.GetChild(root, index), predicate);
                if (found != null) return found;
            }
            return null;
        }

        private static System.Collections.IList GetChildrenList(object container)
        {
            if (container is System.Windows.Controls.Panel panel) return panel.Children;
            var childrenProperty = container?.GetType().GetProperty(
                "Children",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return childrenProperty?.GetValue(container) as System.Collections.IList;
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
