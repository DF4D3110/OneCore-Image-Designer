using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace OneCore_Image_Designer.Converters
{
    /// <summary>
    /// Boolean 到 Visibility 的转换器
    /// </summary>
    public class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Visibility visibility)
            {
                return visibility == Visibility.Visible;
            }
            return false;
        }
    }

    /// <summary>
    /// 反向 Boolean 到 Visibility：true→Collapsed，false→Visible（用于"无数据"占位）。
    /// </summary>
    public class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && !b ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility v && v == Visibility.Collapsed;
    }

    /// <summary>
    /// Feature Category 到颜色的转换器
    /// </summary>
    public class CategoryColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var category = value?.ToString() ?? string.Empty;
            return category switch
            {
                "Microsoft" => new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC)),
                "OEM" => new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)),
                _ => new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99))
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 分区名称到颜色的转换器
    /// </summary>
    public class PartitionColorConverter : IValueConverter
    {
        private static readonly Color[] Colors = new[]
        {
            Color.FromRgb(0x00, 0x7A, 0xCC),
            Color.FromRgb(0x4C, 0xAF, 0x50),
            Color.FromRgb(0xFF, 0x98, 0x00),
            Color.FromRgb(0x9C, 0x27, 0xB0),
            Color.FromRgb(0xF4, 0x43, 0x36),
            Color.FromRgb(0x00, 0xBC, 0xD4),
            Color.FromRgb(0x79, 0x55, 0x48),
            Color.FromRgb(0x60, 0x7D, 0x8B),
        };

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var name = value?.ToString() ?? string.Empty;
            var hash = name.GetHashCode();
            var index = Math.Abs(hash) % Colors.Length;
            return new SolidColorBrush(Colors[index]);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
