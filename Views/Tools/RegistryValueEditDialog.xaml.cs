using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace OneCore_Image_Designer.Views.Tools
{
    /// <summary>
    /// 注册表值编辑对话框
    /// </summary>
    public partial class RegistryValueEditDialog : Window
    {
        public string ValueName { get; private set; }
        public object? ValueData { get; private set; }
        public RegistryValueKind ValueKind { get; private set; }

        private TextBox _txtString = null!;
        private TextBox _txtDword = null!;
        private TextBox _txtQword = null!;
        private TextBox _txtMultiString = null!;
        private TextBox _txtBinary = null!;
        private RadioButton _rbHex = null!;
        private RadioButton _rbDecimal = null!;

        public RegistryValueEditDialog(string valueName, object? valueData, RegistryValueKind valueKind)
        {
            ValueName = valueName;
            ValueData = valueData;
            ValueKind = valueKind;

            Title = "编辑注册表值";
            Width = 500;
            Height = 350;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = System.Windows.Media.Brushes.WhiteSmoke;
            ResizeMode = ResizeMode.CanResize;

            BuildUI();
            LoadValue();
        }

        private void BuildUI()
        {
            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 名称
            var spName = new StackPanel();
            spName.Children.Add(new TextBlock { Text = "值名称:", FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            var txtName = new TextBox { Text = ValueName, IsReadOnly = true, FontSize = 11, Background = System.Windows.Media.Brushes.WhiteSmoke };
            spName.Children.Add(txtName);
            Grid.SetRow(spName, 0);
            grid.Children.Add(spName);

            // 类型
            var spType = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
            spType.Children.Add(new TextBlock { Text = "值类型:", FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            spType.Children.Add(new TextBlock { Text = ValueKind.ToString(), FontSize = 11, Foreground = System.Windows.Media.Brushes.DarkBlue });
            Grid.SetRow(spType, 1);
            grid.Children.Add(spType);

            // 数据编辑区
            var borderData = new Border
            {
                Background = System.Windows.Media.Brushes.White,
                BorderBrush = System.Windows.Media.Brushes.LightGray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8)
            };
            Grid.SetRow(borderData, 2);

            var spData = new StackPanel();

            // 字符串编辑
            _txtString = new TextBox { FontSize = 12, AcceptsReturn = false, Height = 28, VerticalContentAlignment = VerticalAlignment.Center };
            spData.Children.Add(_txtString);

            // DWORD 编辑（含进制选择）
            var spDword = new StackPanel { Visibility = Visibility.Collapsed };
            var spRadio = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            _rbHex = new RadioButton { Content = "十六进制", IsChecked = true, FontSize = 11, Margin = new Thickness(0, 0, 12, 0) };
            _rbDecimal = new RadioButton { Content = "十进制", FontSize = 11 };
            _rbHex.Checked += (s, e) => UpdateDwordDisplay();
            _rbDecimal.Checked += (s, e) => UpdateDwordDisplay();
            spRadio.Children.Add(_rbHex);
            spRadio.Children.Add(_rbDecimal);
            spDword.Children.Add(spRadio);
            _txtDword = new TextBox { FontSize = 12, Height = 28, VerticalContentAlignment = VerticalAlignment.Center };
            spDword.Children.Add(_txtDword);
            spData.Children.Add(spDword);

            // QWORD 编辑
            _txtQword = new TextBox { FontSize = 12, Height = 28, Visibility = Visibility.Collapsed, VerticalContentAlignment = VerticalAlignment.Center };
            spData.Children.Add(_txtQword);

            // 多字符串编辑
            _txtMultiString = new TextBox { FontSize = 11, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Visibility = Visibility.Collapsed, Height = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            spData.Children.Add(_txtMultiString);

            // 二进制编辑
            _txtBinary = new TextBox { FontSize = 11, FontFamily = new System.Windows.Media.FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Height = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            spData.Children.Add(_txtBinary);

            borderData.Child = spData;
            grid.Children.Add(borderData);

            // 按钮
            var spButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var btnCancel = new Button { Content = "取消", Width = 80, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            btnCancel.Click += (s, e) => { DialogResult = false; Close(); };
            var btnOk = new Button { Content = "确定", Width = 80, Height = 28, Background = System.Windows.Media.Brushes.SteelBlue, Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0) };
            btnOk.Click += BtnOk_Click;
            spButtons.Children.Add(btnCancel);
            spButtons.Children.Add(btnOk);
            Grid.SetRow(spButtons, 3);
            grid.Children.Add(spButtons);

            Content = grid;
        }

        private void LoadValue()
        {
            // 隐藏所有编辑框
            _txtString.Visibility = Visibility.Collapsed;
            _txtDword.Parent!.SetValue(VisibilityProperty, Visibility.Collapsed);
            _txtQword.Visibility = Visibility.Collapsed;
            _txtMultiString.Visibility = Visibility.Collapsed;
            _txtBinary.Visibility = Visibility.Collapsed;

            switch (ValueKind)
            {
                case RegistryValueKind.String:
                case RegistryValueKind.ExpandString:
                    _txtString.Visibility = Visibility.Visible;
                    _txtString.Text = ValueData?.ToString() ?? "";
                    break;

                case RegistryValueKind.DWord:
                    _txtDword.Parent!.SetValue(VisibilityProperty, Visibility.Visible);
                    uint dwordVal = ValueData != null ? Convert.ToUInt32(ValueData) : 0;
                    _txtDword.Tag = dwordVal;
                    UpdateDwordDisplay();
                    break;

                case RegistryValueKind.QWord:
                    _txtQword.Visibility = Visibility.Visible;
                    ulong qwordVal = ValueData != null ? Convert.ToUInt64(ValueData) : 0;
                    _txtQword.Text = $"0x{qwordVal:X16}";
                    break;

                case RegistryValueKind.MultiString:
                    _txtMultiString.Visibility = Visibility.Visible;
                    string[]? multiStr = ValueData as string[];
                    _txtMultiString.Text = multiStr != null ? string.Join("\n", multiStr) : "";
                    break;

                case RegistryValueKind.Binary:
                    _txtBinary.Visibility = Visibility.Visible;
                    byte[]? binary = ValueData as byte[];
                    if (binary != null)
                    {
                        _txtBinary.Text = BitConverter.ToString(binary).Replace("-", " ");
                    }
                    break;

                default:
                    _txtString.Visibility = Visibility.Visible;
                    _txtString.Text = ValueData?.ToString() ?? "";
                    _txtString.IsReadOnly = true;
                    break;
            }
        }

        private void UpdateDwordDisplay()
        {
            if (_txtDword.Tag is uint dwordVal)
            {
                if (_rbHex.IsChecked == true)
                    _txtDword.Text = $"0x{dwordVal:X8}";
                else
                    _txtDword.Text = dwordVal.ToString();
            }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                switch (ValueKind)
                {
                    case RegistryValueKind.String:
                    case RegistryValueKind.ExpandString:
                        ValueData = _txtString.Text;
                        break;

                    case RegistryValueKind.DWord:
                        string dwordText = _txtDword.Text.Trim();
                        uint dwordResult;
                        if (dwordText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                            dwordResult = Convert.ToUInt32(dwordText.Substring(2), 16);
                        else
                            dwordResult = uint.Parse(dwordText);
                        ValueData = dwordResult;
                        break;

                    case RegistryValueKind.QWord:
                        string qwordText = _txtQword.Text.Trim();
                        ulong qwordResult;
                        if (qwordText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                            qwordResult = Convert.ToUInt64(qwordText.Substring(2), 16);
                        else
                            qwordResult = ulong.Parse(qwordText);
                        ValueData = qwordResult;
                        break;

                    case RegistryValueKind.MultiString:
                        string[] multiLines = _txtMultiString.Text.Split(new[] { '\n' }, StringSplitOptions.None);
                        for (int i = 0; i < multiLines.Length; i++)
                            multiLines[i] = multiLines[i].TrimEnd('\r');
                        ValueData = multiLines;
                        break;

                    case RegistryValueKind.Binary:
                        string hexText = _txtBinary.Text.Replace(" ", "").Replace("-", "");
                        if (hexText.Length % 2 != 0)
                            throw new ArgumentException("十六进制数据长度必须为偶数");
                        byte[] bytes = new byte[hexText.Length / 2];
                        for (int i = 0; i < bytes.Length; i++)
                            bytes[i] = Convert.ToByte(hexText.Substring(i * 2, 2), 16);
                        ValueData = bytes;
                        break;
                }

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"输入格式错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
