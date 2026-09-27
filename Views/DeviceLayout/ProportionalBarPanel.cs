using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace OneCore_Image_Designer.Views.DeviceLayout
{
    /// <summary>
    /// 比例色块面板：按附加属性 Ratio 在“最终 arrange 宽度”内分配每块宽度。
    /// 测量永远只报告子元素的自然期望宽度，绝不把 ActualWidth 反馈回子宽度绑定，
    /// 因此不存在“子变宽→父变宽→子再变宽”的自激布局循环（旧版卡死根因）。
    /// </summary>
    public class ProportionalBarPanel : Panel
    {
        public static readonly DependencyProperty RatioProperty =
            DependencyProperty.RegisterAttached("Ratio", typeof(double), typeof(ProportionalBarPanel),
                new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsArrange));

        public static void SetRatio(DependencyObject o, double v) => o.SetValue(RatioProperty, v);
        public static double GetRatio(DependencyObject o) => (double)o.GetValue(RatioProperty);

        const double MinChildWidth = 26;

        protected override Size MeasureOverride(Size availableSize)
        {
            double w = 0, h = 0;
            double cw = double.IsPositiveInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width;
            foreach (UIElement c in InternalChildren)
            {
                c.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                w += c.DesiredSize.Width;
                h = Math.Max(h, c.DesiredSize.Height);
            }
            // 返回自然总宽：外层 ScrollViewer/StackPanel 会把 arrange 宽度拉伸到可用宽度，
            // arrange 里再按比例分配 —— DesiredSize 与分配宽度无关，无反馈。
            return new Size(Math.Max(w, cw == double.PositiveInfinity ? w : 0), h);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            int n = InternalChildren.Count;
            if (n == 0) return finalSize;

            double total = finalSize.Width;
            if (total <= 0) total = DesiredSize.Width;

            // 收集有效比例：无 Ratio（NaN）时按子自然宽度做比例
            var ratios = new double[n];
            double sum = 0;
            bool anyRatio = false;
            for (int i = 0; i < n; i++)
            {
                double r = GetRatio(InternalChildren[i]);
                if (!double.IsNaN(r) && r > 0) { anyRatio = true; }
                else r = InternalChildren[i].DesiredSize.Width;
                ratios[i] = r;
                sum += r;
            }
            if (sum <= 0 || !anyRatio && sum <= 0) sum = 1;

            double x = 0;
            for (int i = 0; i < n; i++)
            {
                double w = Math.Max(MinChildWidth, total * ratios[i] / sum);
                InternalChildren[i].Arrange(new Rect(x, 0, w, finalSize.Height));
                x += w + 2;   // 块间留 2px 缝，避免完全贴合
            }
            return finalSize;
        }
    }
}
