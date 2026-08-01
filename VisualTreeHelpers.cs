using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;

namespace VoicePlugin
{
    /// <summary>
    /// 视觉树与宿主辅助工具：深度优先查找子元素、禁用宿主 AutoFontSizeHelper。
    /// </summary>
    internal static class VisualTreeHelpers
    {
        /// <summary>深度优先查找第一个指定类型的后代元素。</summary>
        public static T FindFirstChild<T>(DependencyObject root)
            where T : DependencyObject
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var index = 0; index < count; index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                if (child is T match) return match;
                var nested = FindFirstChild<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }

        /// <summary>
        /// 通过反射禁用宿主 AutoFontSizeHelper 的 IsEnabled 附加属性
        /// （局部值覆盖样式设置器）。该 helper 只对英文自动缩字号、
        /// 对中文恢复原始字号，会导致中文标签被固定槽位裁切。
        /// 失败时静默忽略。
        /// </summary>
        public static void DisableAutoFontSizeHelper(FrameworkElement element)
        {
            try
            {
                var helperType = FindType("Ink_Canvas.Helpers.AutoFontSizeHelper");
                if (helperType == null) return;

                var isEnabledProperty = helperType
                    .GetField(
                        "IsEnabledProperty",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    ?.GetValue(null) as DependencyProperty;
                if (isEnabledProperty == null) return;

                element.SetValue(isEnabledProperty, false);
            }
            catch (Exception)
            {
                // 禁用失败时保持原行为。
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
