using System;
using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Ink_Canvas.Controls;
using VoicePlugin.Services;

namespace VoicePlugin
{
    /// <summary>
    /// “更多/工具”菜单组件：把“播报截断”注册为宿主菜单设置页可管理的菜单项，
    /// 并负责在浮动工具栏“更多”菜单与白板“工具”菜单中渲染对应按钮。
    /// <para>
    /// 宿主菜单项由 <c>ToolsMenuRegistry.AllItems</c>（静态列表）+ 配置文件
    /// （Configs/ToolsMenuConfigs/floatingbar.json、board.json）驱动，没有插件
    /// 注册接口，因此通过反射：① 把 <c>ToolsMenuItemInfo</c> 追加进 AllItems
    /// （工具栏菜单设置页/白板菜单设置页即可列出并可增删），并给宿主
    /// Strings.KeyDict 补一个键，使菜单项显示名回退为“播报截断”而不是
    /// “#key:播报截断”；② 宿主 ApplyMenuLayout 重建菜单面板时会清空注入的
    /// 按钮，因此监听两个弹窗的 Opened 事件，在每次打开时按配置重新注入/移除。
    /// 菜单项默认不加入任何菜单配置，由用户在菜单设置页自行添加。
    /// </para>
    /// <para>
    /// 线程模型：插件 Initialize 由宿主在后台线程调用，因此所有 UI 操作
    /// （注册/注入/移除）一律经 Application.Current.Dispatcher 派发到 UI 线程。
    /// 宿主结构变化时自动降级为不可用并记录日志。
    /// </para>
    /// </summary>
    internal sealed class MoreMenuComponent : IDisposable
    {
        private const string MenuItemId = "voiceStop";
        private const string MenuItemName = "播报截断";
        private const string ComponentTag = "VoicePlugin.MoreMenu";
        private const string RegistryTypeName = "Ink_Canvas.Controls.Toolbar.ToolsMenuRegistry";
        private const string MenuItemInfoTypeName = "Ink_Canvas.Controls.Toolbar.ToolsMenuItemInfo";
        private const string StringsTypeName = "Ink_Canvas.Properties.Strings";

        private const string ToolbarPopupFieldName = "BorderTools";
        private const string BoardPopupFieldName = "BoardBorderToolsPopup";
        private const string ToolbarPopupContentFieldName = "MainToolsPopupContent";
        private const string BoardPopupContentFieldName = "BoardToolsPopupContent";
        private const string MenuPanelFieldName = "MenuPanel";

        private readonly Action _stopSpeaking;
        private readonly Action<string> _log;
        private readonly Action<string, Exception> _logError;

        private readonly object _gate = new object();
        private object _mainWindow;
        private Popup _toolbarPopup;
        private Popup _boardPopup;
        private bool _disposed;

        public MoreMenuComponent(
            Action stopSpeaking,
            Action<string> log,
            Action<string, Exception> logError)
        {
            _stopSpeaking = stopSpeaking;
            _log = log;
            _logError = logError;
        }

        public void Install()
        {
            DispatchToUiThread(() =>
            {
                try
                {
                    if (_disposed) return;

                    _mainWindow = Application.Current?.MainWindow;
                    if (_mainWindow == null) return;

                    if (_mainWindow is FrameworkElement windowElement
                        && !windowElement.IsLoaded)
                    {
                        windowElement.Loaded += OnWindowLoaded;
                        return;
                    }

                    InstallCore();
                }
                catch (Exception ex)
                {
                    _logError?.Invoke("[Voice] failed to install the more-menu component.", ex);
                }
            });
        }

        /// <summary>自愈入口：菜单被宿主重建时重新注入。可从任意线程调用。</summary>
        public void EnsureInjected()
        {
            lock (_gate)
            {
                if (_disposed) return;
            }

            DispatchToUiThread(InjectAll);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }

            // 从宿主注册表移除菜单项与字符串键补丁，避免禁用插件后列表残留。
            UnregisterRegistryEntries();

            DispatchToUiThread(() =>
            {
                try
                {
                    if (_toolbarPopup != null) _toolbarPopup.Opened -= OnPopupOpened;
                    if (_boardPopup != null) _boardPopup.Opened -= OnPopupOpened;

                    RemoveAll();
                }
                catch (Exception ex)
                {
                    _logError?.Invoke("[Voice] failed to remove the more-menu buttons.", ex);
                }
            });
        }

        /// <summary>移除 ToolsMenuRegistry.AllItems 中的菜单项与 Strings.KeyDict 补丁键。</summary>
        private void UnregisterRegistryEntries()
        {
            try
            {
                var registryType = FindType(RegistryTypeName);
                if (registryType != null)
                {
                    var allItems = registryType.GetField(
                        "AllItems",
                        BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IList;
                    if (allItems != null)
                    {
                        for (var index = allItems.Count - 1; index >= 0; index--)
                        {
                            var id = allItems[index]?.GetType()
                                .GetProperty("Id")?.GetValue(allItems[index]) as string;
                            if (string.Equals(id, MenuItemId, StringComparison.OrdinalIgnoreCase))
                            {
                                allItems.RemoveAt(index);
                                break;
                            }
                        }
                    }
                }

                var stringsType = FindType(StringsTypeName);
                if (stringsType != null)
                {
                    var keyDict = stringsType.GetField(
                        "KeyDict",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                        ?.GetValue(null) as IDictionary;
                    keyDict?.Remove(MenuItemName);
                }
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] failed to unregister the menu registry entries.", ex);
            }
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement window)
            {
                window.Loaded -= OnWindowLoaded;
            }
            InstallCore();
        }

        private void InstallCore()
        {
            if (_disposed) return;

            try
            {
                RegisterMenuItemInRegistry();
                HookPopups();
                InjectAll();
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] failed to install the more-menu component.", ex);
            }
        }

        private void DispatchToUiThread(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            if (dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.BeginInvoke(action);
            }
        }

        /// <summary>把菜单项注册进 ToolsMenuRegistry.AllItems（菜单设置页可列出）。</summary>
        private void RegisterMenuItemInRegistry()
        {
            var registryType = FindType(RegistryTypeName);
            if (registryType == null)
            {
                _log?.Invoke("[Voice] the host ToolsMenuRegistry was not found; the menu item registration is skipped.");
                return;
            }

            var allItems = registryType.GetField(
                "AllItems",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IList;
            if (allItems == null) return;

            foreach (var item in allItems)
            {
                var id = item?.GetType().GetProperty("Id")?.GetValue(item) as string;
                if (string.Equals(id, MenuItemId, StringComparison.OrdinalIgnoreCase))
                {
                    return; // 已注册。
                }
            }

            var infoType = FindType(MenuItemInfoTypeName);
            if (infoType == null) return;

            var info = Activator.CreateInstance(infoType);
            infoType.GetProperty("Id")?.SetValue(info, MenuItemId);
            infoType.GetProperty("LocalizationKey")?.SetValue(info, MenuItemName);
            infoType.GetProperty("Description")?.SetValue(info, "单击截断（停止）当前播报");
            infoType.GetProperty("IconGeometry")?.SetValue(info, VoiceIconCatalog.StopIconGeometry);

            allItems.Add(info);

            // 给宿主 Strings.KeyDict 补一个键：使菜单项的显示名解析
            // （Strings.GetString 返回 null 时 DisplayName 回退为
            // LocalizationKey 本身），而不是显示 "#key:播报截断"。
            PatchStringsKeyDict();

            _log?.Invoke("[Voice] registered the truncate menu item into ToolsMenuRegistry.");
        }

        private void PatchStringsKeyDict()
        {
            try
            {
                var stringsType = FindType(StringsTypeName);
                if (stringsType == null) return;

                var keyDict = stringsType.GetField(
                    "KeyDict",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    ?.GetValue(null) as IDictionary;
                if (keyDict == null || keyDict.Contains(MenuItemName)) return;

                // 映射到 (FloatingBarStrings, 播报截断)：资源查找必然返回 null，
                // 于是 DisplayName 的 ?? LocalizationKey 回退到“播报截断”。
                keyDict.Add(
                    MenuItemName,
                    ValueTuple.Create("FloatingBarStrings", MenuItemName));
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] failed to patch the host string dictionary; the menu item name may show a key prefix.", ex);
            }
        }

        /// <summary>监听两个菜单弹窗的打开事件：宿主重建菜单面板后按配置重新注入。</summary>
        private void HookPopups()
        {
            var type = _mainWindow.GetType();
            _toolbarPopup = GetPopupField(type, ToolbarPopupFieldName);
            _boardPopup = GetPopupField(type, BoardPopupFieldName);

            if (_toolbarPopup != null) _toolbarPopup.Opened += OnPopupOpened;
            if (_boardPopup != null) _boardPopup.Opened += OnPopupOpened;

            if (_toolbarPopup == null || _boardPopup == null)
            {
                _log?.Invoke("[Voice] some host menu popups were not found; the more-menu self-heal is limited.");
            }
        }

        private Popup GetPopupField(Type mainWindowType, string fieldName)
        {
            var field = mainWindowType.GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(_mainWindow) as Popup;
        }

        private void OnPopupOpened(object sender, EventArgs e)
        {
            InjectAll();
        }

        private void InjectAll()
        {
            lock (_gate)
            {
                if (_disposed || _mainWindow == null) return;
            }

            try
            {
                var type = _mainWindow.GetType();
                InjectMenu(type, ToolbarPopupContentFieldName, "LoadFloatingBarConfig", "FloatingBarItems");
                InjectMenu(type, BoardPopupContentFieldName, "LoadBoardConfig", "BoardItems");
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] failed to inject the more-menu truncate button.", ex);
            }
        }

        private void InjectMenu(
            Type mainWindowType,
            string popupContentFieldName,
            string loadMethod,
            string itemsProperty)
        {
            var popupField = mainWindowType.GetField(
                popupContentFieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var popupContent = popupField?.GetValue(_mainWindow);
            if (popupContent == null) return;

            var menuPanel = ResolveMenuPanel(popupContent);
            if (menuPanel == null) return;

            var enabled = IsInConfig(loadMethod, itemsProperty);

            // 移除现有的注入按钮（可能在行内或直接挂在面板上，配置关闭时）。
            RemoveInjectedButton(menuPanel);

            if (!enabled) return;

            var button = new ToolMenuButton
            {
                Label = MenuItemName,
                IconGeometry = VoiceIconCatalog.StopIconGeometry,
                // 直接挂进纵向 MenuPanel 时默认 Stretch 会被拉伸到整行宽度
                // （3 个按钮位），改为按自身内容宽（48px）居中排布。
                HorizontalAlignment = HorizontalAlignment.Center,
                Tag = ComponentTag
            };
            button.ToolTip = "单击：截断（停止）当前播报";
            button.ButtonMouseUp += (sender, args) =>
            {
                try
                {
                    _stopSpeaking();
                }
                catch (Exception ex)
                {
                    _logError?.Invoke("[Voice] the more-menu truncate action failed.", ex);
                }
            };

            // 缩小组件图标（模板 30px）与标签字号（模板 14px），
            // 与菜单里其它按钮相比更紧凑；标签需禁用宿主 AutoFontSizeHelper，
            // 否则中文会被恢复为 14px 并可能被 48px 面板裁切。
            button.Loaded += (sender, args) =>
            {
                try
                {
                    if (VisualTreeHelpers.FindFirstChild<Image>(button) is Image image)
                    {
                        image.Width = 22;
                        image.Height = 22;
                        // 图标微微上移：顶部边距 9→12（相对整体下移时的 18 上移 6）。
                        image.Margin = new Thickness(13, 12, 13, 0);
                    }
                    if (VisualTreeHelpers.FindFirstChild<Label>(button) is Label label)
                    {
                        VisualTreeHelpers.DisableAutoFontSizeHelper(label);
                        label.FontSize = 11;
                        // 文字微微下移：加 6px 顶部边距，让图标与文字分布更均衡。
                        label.Margin = new Thickness(0, 6, 0, 0);
                    }
                }
                catch (Exception)
                {
                    // 尺寸调整失败不影响按钮功能。
                }
            };

            // 追加到最后一个未满（<3 个按钮）的行，行满或没有行时新建一行，
            // 与宿主菜单按钮接续排布而不是独占一行。
            var lastRow = FindLastRow(menuPanel);
            if (lastRow != null && CountChildren(lastRow) < 3)
            {
                if (!TryAppend(GetChildrenList(lastRow), button))
                {
                    _log?.Invoke($"[Voice] the menu row of [{popupContentFieldName}] is not writable; the more-menu button was not injected.");
                }
            }
            else
            {
                var row = CreateMenuRow();
                var rowChildren = GetChildrenList(row);
                if (rowChildren == null
                    || !TryAppend(rowChildren, button)
                    || !TryAppend(GetChildrenList(menuPanel), row as FrameworkElement))
                {
                    _log?.Invoke($"[Voice] the menu panel of [{popupContentFieldName}] is not writable; the more-menu button was not injected.");
                }
            }
        }

        private bool IsInConfig(string loadMethod, string itemsProperty)
        {
            var registryType = FindType(RegistryTypeName);
            if (registryType == null) return false;

            var layout = registryType.GetMethod(loadMethod)?.Invoke(null, null);
            if (layout == null) return false;

            var items = layout.GetType().GetProperty(itemsProperty)?.GetValue(layout) as IList;
            if (items == null) return false;

            foreach (var id in items)
            {
                if (string.Equals(id as string, MenuItemId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private void RemoveAll()
        {
            lock (_gate)
            {
                if (!_disposed || _mainWindow == null) return;
            }

            try
            {
                var type = _mainWindow.GetType();
                RemoveFromMenu(type, ToolbarPopupContentFieldName);
                RemoveFromMenu(type, BoardPopupContentFieldName);
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] failed to remove the more-menu truncate buttons.", ex);
            }
            finally
            {
                lock (_gate)
                {
                    _mainWindow = null;
                }
            }
        }

        private void RemoveFromMenu(Type mainWindowType, string popupContentFieldName)
        {
            var popupField = mainWindowType.GetField(
                popupContentFieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var popupContent = popupField?.GetValue(_mainWindow);
            if (popupContent == null) return;

            var menuPanel = ResolveMenuPanel(popupContent);
            if (menuPanel == null) return;

            RemoveInjectedButton(menuPanel);
        }

        /// <summary>递归移除注入的按钮（可能在行内或直接挂在面板上）。</summary>
        private static void RemoveInjectedButton(object container)
        {
            var children = GetChildrenList(container);
            if (children == null) return;

            for (var index = children.Count - 1; index >= 0; index--)
            {
                var child = children[index];
                if (child is FrameworkElement element
                    && Equals(element.Tag, ComponentTag))
                {
                    children.RemoveAt(index);
                    continue;
                }
                if (child is DependencyObject nested)
                {
                    RemoveInjectedButton(nested);
                }
            }
        }

        /// <summary>查找菜单面板里最后一个横向行容器（宿主行布局为每行最多 3 个按钮）。</summary>
        private static FrameworkElement FindLastRow(object menuPanel)
        {
            var children = GetChildrenList(menuPanel);
            if (children == null) return null;

            for (var index = children.Count - 1; index >= 0; index--)
            {
                if (children[index] is FrameworkElement element
                    && IsHorizontalRow(element))
                {
                    return element;
                }
            }
            return null;
        }

        private static bool IsHorizontalRow(FrameworkElement element)
        {
            if (element is StackPanel stackPanel)
            {
                return stackPanel.Orientation == Orientation.Horizontal;
            }

            try
            {
                var orientation = element.GetType().GetProperty(
                    "Orientation",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(element);
                return orientation is Orientation value
                    && value == Orientation.Horizontal;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static int CountChildren(object container)
        {
            return GetChildrenList(container)?.Count ?? 0;
        }

        /// <summary>创建与宿主一致的横向行容器（iNKORE SimpleStackPanel，3px 间距）。</summary>
        private static object CreateMenuRow()
        {
            var rowType = FindType("iNKORE.UI.WPF.Controls.SimpleStackPanel");
            if (rowType != null)
            {
                try
                {
                    var row = Activator.CreateInstance(rowType);
                    rowType.GetProperty("Orientation")?.SetValue(row, Orientation.Horizontal);
                    rowType.GetProperty("Spacing")?.SetValue(row, 3.0);
                    return row;
                }
                catch (Exception)
                {
                }
            }

            // 回退：普通横向 StackPanel。
            return new StackPanel { Orientation = Orientation.Horizontal };
        }

        private static object ResolveMenuPanel(object popupContent)
        {
            var panelField = popupContent.GetType().GetField(
                MenuPanelFieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return panelField?.GetValue(popupContent);
        }

        private static IList GetChildrenList(object container)
        {
            if (container == null) return null;
            if (container is Panel panel) return panel.Children;

            var childrenProperty = container.GetType().GetProperty(
                "Children",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return childrenProperty?.GetValue(container) as IList;
        }

        private static bool TryAppend(IList children, FrameworkElement child)
        {
            try
            {
                children.Add(child);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
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
