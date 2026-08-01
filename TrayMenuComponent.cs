using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VoicePlugin.Config;

namespace VoicePlugin
{
    /// <summary>
    /// 托盘图标右键菜单组件：在托盘菜单（宿主 App 资源的 TaskbarTrayIcon 的
    /// ContextMenu）中注入“开启/关闭自动播报”菜单项。
    /// <para>
    /// 宿主 SDK 的 <see cref="Ink_Canvas.Plugins.ITrayService"/> 实现会把菜单项
    /// Name 设为 "PluginTray." + id——前缀本身含点号，而 WPF 的 Name 属性
    /// 拒绝任何含点号的字符串，因此该 API 对任意插件 id 都会抛异常
    /// （宿主日志：…不是属性"Name"的有效值）。本组件绕开该 API，直接向
    /// 托盘 ContextMenu 注入标准 <see cref="MenuItem"/>（纯插件侧实现）。
    /// </para>
    /// <para>
    /// 所有访问托盘 UI 的操作都派发到 UI 线程；注入内容带 Tag 标记去重；
    /// 可通过 <see cref="UpdateVisibility"/> 按设置项控制菜单项的显隐；
    /// <see cref="Dispose"/> 时移除。
    /// </para>
    /// </summary>
    internal sealed class TrayMenuComponent : IDisposable
    {
        private const string ComponentTag = "VoicePlugin.TrayMenu";
        private const string RestartItemName = "RestartAppTrayIconMenuItem";

        // Material "volume_up"（24x24），缩放为 16x16 菜单图标。
        private const string SpeakerIconGeometry =
            "M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05 "
            + "c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 "
            + "5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77S18.01 "
            + "4.14 14 3.23z";

        private readonly Func<VoiceConfigSnapshot> _getConfig;
        private readonly Func<Task> _toggleEnabled;
        private readonly Action<string> _log;
        private readonly Action<string, Exception> _logError;

        private ContextMenu _trayMenu;
        private MenuItem _toggleItem;
        // 注入重试定时器：宿主托盘图标资源（TaskbarTrayIcon）可能晚于
        // 插件初始化才挂到 Application.Resources，首次注入失败时每秒重试，
        // 注入成功或开关关闭后停止。（仅 UI 线程访问。）
        private DispatcherTimer _injectionTimer;
        private bool _disposed;

        public TrayMenuComponent(
            Func<VoiceConfigSnapshot> getConfig,
            Func<Task> toggleEnabled,
            Action<string> log,
            Action<string, Exception> logError)
        {
            _getConfig = getConfig;
            _toggleEnabled = toggleEnabled;
            _log = log;
            _logError = logError;
        }

        public void Install()
        {
            DispatchToUiThread(() =>
            {
                if (_disposed) return;
                UpdateVisibility(_getConfig().ShowTrayToggle);
                EnsureInjectionLoop();
            });
        }

        /// <summary>按设置项控制托盘菜单项的显隐。可从任意线程调用。</summary>
        public void UpdateVisibility(bool show)
        {
            if (_disposed) return;

            DispatchToUiThread(() =>
            {
                if (_disposed) return;
                try
                {
                    if (show)
                    {
                        EnsureInjected();
                        EnsureInjectionLoop();
                    }
                    else
                    {
                        RemoveInjected();
                        StopInjectionLoop();
                    }
                }
                catch (Exception ex)
                {
                    _logError?.Invoke("[Voice] failed to update the tray menu visibility.", ex);
                }
            });
        }

        /// <summary>按播报开关状态刷新菜单项的勾选状态（两态）。可从任意线程调用。</summary>
        public void UpdateState(bool enabled)
        {
            if (_disposed || _toggleItem == null) return;

            DispatchToUiThread(() =>
            {
                if (_disposed || _toggleItem == null) return;
                try
                {
                    _toggleItem.IsChecked = enabled;
                }
                catch (Exception ex)
                {
                    _logError?.Invoke("[Voice] failed to update the tray toggle item.", ex);
                }
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            DispatchToUiThread(() =>
            {
                try
                {
                    StopInjectionLoop();
                    RemoveInjected();
                }
                catch (Exception ex)
                {
                    _logError?.Invoke("[Voice] failed to remove the tray menu items.", ex);
                }
            });
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

        private void EnsureInjected()
        {
            if (_toggleItem != null && IsStillAttached()) return;

            var trayMenu = ResolveTrayMenu();
            if (trayMenu == null) return;

            // 去重：已有注入时直接复用。
            foreach (var item in trayMenu.Items)
            {
                if (item is MenuItem existing
                    && Equals(existing.Tag, ComponentTag))
                {
                    _trayMenu = trayMenu;
                    _toggleItem = existing;
                    return;
                }
            }

            // 与宿主的插入位置保持一致：插在“重启程序”菜单项之前。
            var insertIndex = trayMenu.Items.Count;
            for (var index = 0; index < trayMenu.Items.Count; index++)
            {
                if (trayMenu.Items[index] is MenuItem restart
                    && string.Equals(
                        restart.Name,
                        RestartItemName,
                        StringComparison.Ordinal))
                {
                    insertIndex = index;
                    break;
                }
            }

            var enabled = _getConfig().Enabled;
            var toggleItem = new MenuItem
            {
                Header = "自动播报",
                Icon = CreateIcon(),
                IsCheckable = true,
                IsChecked = enabled,
                ToolTip = "单击开启/关闭自动播报",
                Tag = ComponentTag
            };
            toggleItem.Click += (sender, args) =>
                InvokeSafely(() => _ = _toggleEnabled());

            trayMenu.Items.Insert(insertIndex, toggleItem);

            _trayMenu = trayMenu;
            _toggleItem = toggleItem;
            _log?.Invoke("[Voice] tray toggle item injected.");
            StopInjectionLoop();
        }

        /// <summary>启动注入重试循环（仅 UI 线程；已注入或已存在则不动）。</summary>
        private void EnsureInjectionLoop()
        {
            if (_disposed || _injectionTimer != null || IsInjected()) return;

            _injectionTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _injectionTimer.Tick += (sender, args) => CheckInjection();
            _injectionTimer.Start();
        }

        private void StopInjectionLoop()
        {
            var timer = _injectionTimer;
            _injectionTimer = null;
            if (timer == null) return;
            try
            {
                timer.Stop();
            }
            catch
            {
            }
        }

        private void CheckInjection()
        {
            if (_disposed)
            {
                StopInjectionLoop();
                return;
            }

            if (IsInjected())
            {
                StopInjectionLoop();
                return;
            }

            // 开关被关闭（托盘菜单项不显示）时无需再注入。
            if (!_getConfig().ShowTrayToggle)
            {
                StopInjectionLoop();
                return;
            }

            EnsureInjected();
            if (IsInjected())
            {
                StopInjectionLoop();
            }
        }

        private bool IsInjected()
        {
            return _toggleItem != null && IsStillAttached();
        }

        private void RemoveInjected()
        {
            if (_trayMenu == null && _toggleItem == null) return;

            if (_trayMenu != null)
            {
                for (var index = _trayMenu.Items.Count - 1; index >= 0; index--)
                {
                    if (_trayMenu.Items[index] is MenuItem item
                        && Equals(item.Tag, ComponentTag))
                    {
                        _trayMenu.Items.RemoveAt(index);
                    }
                }
            }

            _trayMenu = null;
            _toggleItem = null;
        }

        private ContextMenu ResolveTrayMenu()
        {
            if (_trayMenu != null) return _trayMenu;

            var trayIcon = Application.Current?.Resources["TaskbarTrayIcon"];
            if (trayIcon == null)
            {
                _log?.Invoke("[Voice] the host tray icon resource was not found; the tray menu is unavailable.");
                return null;
            }

            var trayMenu = trayIcon.GetType()
                .GetProperty("ContextMenu")
                ?.GetValue(trayIcon) as ContextMenu;
            if (trayMenu == null)
            {
                _log?.Invoke("[Voice] the host tray context menu was not found; the tray menu is unavailable.");
                return null;
            }

            _trayMenu = trayMenu;
            return trayMenu;
        }

        private bool IsStillAttached()
        {
            if (_toggleItem == null || _trayMenu == null) return false;

            for (var index = 0; index < _trayMenu.Items.Count; index++)
            {
                if (ReferenceEquals(_trayMenu.Items[index], _toggleItem))
                {
                    return true;
                }
            }
            return false;
        }

        private static Image CreateIcon()
        {
            // 与宿主托盘菜单图标一致：24x24 图形裁剪 + 深色 #27272a，
            // 图片缩小到 12x12（小于 14px 菜单文字），避免显得过大。
            var icon = new GeometryDrawing
            {
                Geometry = Geometry.Parse(SpeakerIconGeometry),
                Brush = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x2a))
            };
            var drawingGroup = new DrawingGroup
            {
                ClipGeometry = new RectangleGeometry(new Rect(0, 0, 24, 24))
            };
            drawingGroup.Children.Add(icon);

            return new Image
            {
                Source = new DrawingImage(drawingGroup),
                Width = 12,
                Height = 12,
                Margin = new Thickness(-1)
            };
        }

        private void InvokeSafely(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logError?.Invoke("[Voice] a tray menu action failed.", ex);
            }
        }
    }
}
