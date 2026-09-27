using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace OneCore_Image_Designer.Helpers
{
    public sealed class AppTabContentTemplateSelector : DataTemplateSelector
    {
        private static readonly DataTemplate MissingTemplate = CreateMissingTemplate();

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (item == null) return MissingTemplate;
            var key = new DataTemplateKey(item.GetType());
            if (container is FrameworkElement element &&
                element.TryFindResource(key) is DataTemplate template)
            {
                Services.AppTrace.Log($"Tab content template resolved: {item.GetType().FullName}");
                return template;
            }

            Services.AppTrace.Log($"Tab content template missing: {item.GetType().FullName}");
            return CreateMissingTemplate(item.GetType().FullName);
        }

        private static DataTemplate CreateMissingTemplate(string? typeName = null)
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetValue(TextBlock.TextProperty, typeName == null
                ? "页面内容模板不可用。请检查标签页内容绑定。"
                : $"没有为 {typeName} 注册页面视图。");
            text.SetValue(TextBlock.ForegroundProperty, Brushes.Firebrick);
            text.SetValue(TextBlock.FontSizeProperty, 18d);
            text.SetValue(TextBlock.MarginProperty, new Thickness(28));
            text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);

            var template = new DataTemplate { VisualTree = text };
            return template;
        }
    }
}
