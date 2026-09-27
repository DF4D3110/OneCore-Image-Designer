using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace OneCore_Image_Designer.Views.FFUBrowser
{
    /// <summary>
    /// 十六进制查看器控件，支持大文件虚拟化加载、格式识别、字符串提取
    /// </summary>
    public partial class HexViewerControl : UserControl
    {
        private Stream? _stream;
        private long _length;
        private const int BytesPerLine = 16;
        private List<StringEntry>? _strings;
        private List<StringEntry>? _allStrings;
        private Services.FirmwareParser.FirmwareInfo? _firmwareInfo;
        private bool _stringsPanelVisible = false;
        private bool _structurePanelVisible = false;

        public HexViewerControl()
        {
            InitializeComponent();
            HexListBox.SelectionChanged += HexListBox_SelectionChanged;
        }

        /// <summary>
        /// 加载流进行十六进制显示
        /// </summary>
        public void LoadStream(Stream stream, long length, string displayName = "")
        {
            _stream = stream;
            _length = length;
            _strings = null;

            // 安全限制：超过16MB的分区只加载前16MB，避免超大分区卡顿
            const long MaxDisplaySize = 16 * 1024 * 1024; // 16MB
            long displayLength = Math.Min(length, MaxDisplaySize);
            bool truncated = length > MaxDisplaySize;

            // 计算总行数
            long totalLines = (displayLength + BytesPerLine - 1) / BytesPerLine;

            // 使用虚拟化集合
            var collection = new VirtualizingHexCollection(stream, displayLength);
            HexListBox.ItemsSource = collection;

            // 格式识别
            string formatInfo = DetectFormat(stream, length);

            // 固件结构解析
            _firmwareInfo = Services.FirmwareParser.Parse(stream, length);
            if (_firmwareInfo != null && !string.IsNullOrEmpty(_firmwareInfo.Summary))
            {
                formatInfo = _firmwareInfo.Summary;
            }

            // 更新信息
            string sizeInfo = truncated
                ? $"{FormatSize(length)} (仅显示前 {FormatSize(MaxDisplaySize)})"
                : FormatSize(length);
            InfoText.Text = $"{displayName} | 大小: {sizeInfo} | 共 {totalLines:N0} 行";
            StatusText.Text = $"就绪 | 每行 {BytesPerLine} 字节 | 虚拟化加载 | {formatInfo}";

            // 显示格式信息
            if (_firmwareInfo != null && !string.IsNullOrEmpty(_firmwareInfo.Format) && _firmwareInfo.Format != "未知" && _firmwareInfo.Format != "原始二进制 (raw)")
            {
                FormatText.Text = _firmwareInfo.Summary;
                FormatInfoBar.Visibility = Visibility.Visible;
            }
            else if (!string.IsNullOrEmpty(formatInfo) && formatInfo != "未知格式")
            {
                FormatText.Text = formatInfo;
                FormatInfoBar.Visibility = Visibility.Visible;
            }
            else
            {
                FormatInfoBar.Visibility = Visibility.Collapsed;
            }

            // 隐藏面板
            _stringsPanelVisible = false;
            _structurePanelVisible = false;
            StringsPanel.Visibility = Visibility.Collapsed;
            StructurePanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 清除显示
        /// </summary>
        public void Clear()
        {
            _stream = null;
            _length = 0;
            _strings = null;
            HexListBox.ItemsSource = null;
            InfoText.Text = "";
            StatusText.Text = "";
            FormatInfoBar.Visibility = Visibility.Collapsed;
            StringsPanel.Visibility = Visibility.Collapsed;
            StringsListBox.ItemsSource = null;
        }

        /// <summary>
        /// 格式识别
        /// </summary>
        private string DetectFormat(Stream stream, long length)
        {
            try
            {
                if (length < 4) return "未知格式";

                byte[] header = new byte[Math.Min(64, length)];
                stream.Seek(0, SeekOrigin.Begin);
                int read = stream.Read(header, 0, header.Length);
                if (read < 4) return "未知格式";

                // ELF (0x7F 'E' 'L' 'F')
                if (header[0] == 0x7F && header[1] == 0x45 && header[2] == 0x4C && header[3] == 0x46)
                {
                    string bitness = header[4] == 1 ? "32-bit" : header[4] == 2 ? "64-bit" : "未知";
                    string endian = header[5] == 1 ? "Little-Endian" : header[5] == 2 ? "Big-Endian" : "未知";
                    string machine = "未知";
                    if (read >= 20)
                    {
                        ushort mach = (ushort)(header[18] | (header[19] << 8));
                        machine = mach switch
                        {
                            0x28 => "ARM",
                            0xB7 => "AArch64",
                            0x03 => "x86",
                            0x3E => "x86-64",
                            _ => $"0x{mach:X4}"
                        };
                    }
                    return $"ELF {bitness} {endian} | 架构: {machine} | 版本: {header[6]}";
                }

                // MZ / PE (DOS头 'M' 'Z')
                if (header[0] == 0x4D && header[1] == 0x5A)
                {
                    // 检查PE签名偏移
                    if (read >= 0x40 + 4)
                    {
                        int peOffset = BitConverter.ToInt32(header, 0x3C);
                        if (peOffset > 0 && peOffset + 4 < length)
                        {
                            byte[] peSig = new byte[4];
                            stream.Seek(peOffset, SeekOrigin.Begin);
                            stream.Read(peSig, 0, 4);
                            if (peSig[0] == 0x50 && peSig[1] == 0x45 && peSig[2] == 0 && peSig[3] == 0)
                            {
                                return $"PE/COFF 可执行文件 (UEFI驱动/应用) | PE偏移: 0x{peOffset:X}";
                            }
                        }
                    }
                    return "MZ DOS 可执行文件";
                }

                // UEFI固件卷 (FV签名 '_' 'F' 'V' 'H' 即 0x5F465648)
                // 注意：FV签名通常在偏移0，但有些在8字节对齐位置
                if (header[0] == 0x5F && header[1] == 0x46 && header[2] == 0x56 && header[3] == 0x48)
                {
                    return "UEFI 固件卷 (Firmware Volume) | 包含PEI/DXE模块";
                }

                // 搜索FV签名（前1KB内）
                long searchLen = Math.Min(1024, length);
                byte[] searchBuf = new byte[searchLen];
                stream.Seek(0, SeekOrigin.Begin);
                stream.Read(searchBuf, 0, (int)searchLen);
                for (int i = 0; i < searchLen - 4; i += 8)
                {
                    if (searchBuf[i] == 0x5F && searchBuf[i + 1] == 0x46 &&
                        searchBuf[i + 2] == 0x56 && searchBuf[i + 3] == 0x48)
                    {
                        return $"UEFI 固件卷 (FV) | FV签名偏移: 0x{i:X}";
                    }
                }

                // 高通设备程序加载器 (QCSBL) 签名
                if (header[0] == 0x44 && header[1] == 0x51 && header[2] == 0x43 && header[3] == 0x53)
                {
                    return "高通 QCSBL 引导加载器";
                }

                // 全零或高熵数据
                int zeroCount = 0;
                for (int i = 0; i < Math.Min(256, read); i++)
                {
                    if (header[i] == 0) zeroCount++;
                }
                if (zeroCount > read * 0.9)
                {
                    return "空分区 (大部分为零)";
                }

                return "原始二进制数据 (raw)";
            }
            catch
            {
                return "未知格式";
            }
        }

        /// <summary>
        /// 提取所有可打印字符串（ASCII + UTF-16 LE）
        /// </summary>
        private List<StringEntry> ExtractStrings(int minLength = 4)
        {
            var result = new List<StringEntry>();
            if (_stream == null || _length == 0) return result;

            try
            {
                // 限制扫描前16MB，避免大分区卡顿
                long scanLen = Math.Min(_length, 16 * 1024 * 1024);
                byte[] buffer = new byte[scanLen];
                _stream.Seek(0, SeekOrigin.Begin);
                int read = _stream.Read(buffer, 0, (int)scanLen);

                // 1. 提取ASCII字符串
                var asciiSb = new StringBuilder();
                long asciiStart = 0;

                for (long i = 0; i < read; i++)
                {
                    byte b = buffer[i];
                    if (b >= 0x20 && b <= 0x7E)
                    {
                        if (asciiSb.Length == 0) asciiStart = i;
                        asciiSb.Append((char)b);
                    }
                    else
                    {
                        if (asciiSb.Length >= minLength)
                        {
                            result.Add(new StringEntry
                            {
                                Offset = asciiStart,
                                Text = asciiSb.ToString(),
                                Length = asciiSb.Length,
                                Type = "ASCII"
                            });
                        }
                        asciiSb.Clear();
                    }
                }
                if (asciiSb.Length >= minLength)
                {
                    result.Add(new StringEntry
                    {
                        Offset = asciiStart,
                        Text = asciiSb.ToString(),
                        Length = asciiSb.Length,
                        Type = "ASCII"
                    });
                }

                // 2. 提取UTF-16 LE字符串（字符+0x00模式）
                var utf16Sb = new StringBuilder();
                long utf16Start = 0;

                for (long i = 0; i < read - 1; i += 2)
                {
                    byte low = buffer[i];
                    byte high = buffer[i + 1];

                    // UTF-16 LE: 可打印字符 (high == 0 且 low 可打印) 或 中文等 (high > 0)
                    bool isPrintable = (high == 0 && low >= 0x20 && low <= 0x7E)
                                       || (high >= 0x4E && high <= 0x9F)  // CJK统一汉字
                                       || (high >= 0x30 && high <= 0x30 && low >= 0x40)  // CJK符号
                                       || (high == 0xFF && low >= 0x00);  // 全角字符

                    if (isPrintable)
                    {
                        if (utf16Sb.Length == 0) utf16Start = i;
                        char c = (char)(low | (high << 8));
                        utf16Sb.Append(c);
                    }
                    else
                    {
                        if (utf16Sb.Length >= minLength)
                        {
                            result.Add(new StringEntry
                            {
                                Offset = utf16Start,
                                Text = utf16Sb.ToString(),
                                Length = utf16Sb.Length,
                                Type = "UTF-16"
                            });
                        }
                        utf16Sb.Clear();
                    }
                }
                if (utf16Sb.Length >= minLength)
                {
                    result.Add(new StringEntry
                    {
                        Offset = utf16Start,
                        Text = utf16Sb.ToString(),
                        Length = utf16Sb.Length,
                        Type = "UTF-16"
                    });
                }

                // 按偏移排序
                result.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            }
            catch { }

            return result;
        }

        private void BtnStructure_Click(object sender, RoutedEventArgs e)
        {
            _structurePanelVisible = !_structurePanelVisible;
            StructurePanel.Visibility = _structurePanelVisible ? Visibility.Visible : Visibility.Collapsed;

            if (_structurePanelVisible)
            {
                // 关闭字符串面板
                _stringsPanelVisible = false;
                StringsPanel.Visibility = Visibility.Collapsed;
                BuildStructurePanel();
            }
        }

        private void BuildStructurePanel()
        {
            StructureContent.Children.Clear();

            if (_firmwareInfo == null)
            {
                AddStructureText("未解析到固件结构", "#888");
                return;
            }

            // 基本信息
            AddStructureHeader("基本信息");
            AddStructureRow("格式", _firmwareInfo.Format);
            if (!string.IsNullOrEmpty(_firmwareInfo.Architecture))
                AddStructureRow("架构", _firmwareInfo.Architecture);
            if (!string.IsNullOrEmpty(_firmwareInfo.EntryPoint))
                AddStructureRow("入口点", _firmwareInfo.EntryPoint);
            if (_firmwareInfo.CodeSize > 0)
                AddStructureRow("代码大小", FormatSize(_firmwareInfo.CodeSize));
            if (_firmwareInfo.DataSize > 0)
                AddStructureRow("数据大小", FormatSize(_firmwareInfo.DataSize));

            // 附加信息
            if (_firmwareInfo.AdditionalInfo.Count > 0)
            {
                AddStructureHeader("详细参数");
                foreach (var kv in _firmwareInfo.AdditionalInfo)
                {
                    AddStructureRow(kv.Key, kv.Value);
                }
            }

            // 节表/节区
            if (_firmwareInfo.Sections.Count > 0)
            {
                AddStructureHeader($"节表 ({_firmwareInfo.Sections.Count} 个)");
                foreach (var sec in _firmwareInfo.Sections)
                {
                    string info = $"{sec.Name} | VA:0x{sec.VirtualAddress:X} | 大小:{FormatSize(sec.RawSize)}";
                    if (!string.IsNullOrEmpty(sec.Flags))
                        info += $" | [{sec.Flags}]";
                    AddStructureText(info, "#9CDCFE", sec.RawOffset);
                }
            }

            // UEFI模块列表
            if (_firmwareInfo.Modules.Count > 0)
            {
                AddStructureHeader($"UEFI模块 ({_firmwareInfo.Modules.Count} 个)");
                foreach (var mod in _firmwareInfo.Modules)
                {
                    string info = $"{mod.Name} | 类型:{mod.Type} | 大小:{FormatSize(mod.Size)}";
                    AddStructureText(info, "#CE9178", mod.Offset);
                }
            }

            if (_firmwareInfo.Sections.Count == 0 && _firmwareInfo.Modules.Count == 0
                && _firmwareInfo.AdditionalInfo.Count == 0 && string.IsNullOrEmpty(_firmwareInfo.Architecture))
            {
                AddStructureText("该格式无详细结构信息", "#888");
            }
        }

        private void AddStructureHeader(string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4E, 0xC9, 0xB0)),
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 2)
            };
            StructureContent.Children.Add(tb);
        }

        private void AddStructureRow(string key, string value)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            var keyTb = new TextBlock
            {
                Text = key + ": ",
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0xDC, 0xFE)),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 10,
                Width = 100
            };
            var valTb = new TextBlock
            {
                Text = value,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD4, 0xD4, 0xD4)),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 10,
                TextWrapping = System.Windows.TextWrapping.Wrap
            };
            panel.Children.Add(keyTb);
            panel.Children.Add(valTb);
            StructureContent.Children.Add(panel);
        }

        private void AddStructureText(string text, string colorHex, long gotoOffset = -1)
        {
            var tb = new TextBlock
            {
                Text = text,
                Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex)),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 10,
                Margin = new Thickness(0, 1, 0, 1),
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Cursor = gotoOffset >= 0 ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow
            };
            if (gotoOffset >= 0)
            {
                long offset = gotoOffset;
                tb.MouseLeftButtonUp += (s, e) => GotoOffset(offset);
                tb.ToolTip = $"点击跳转到偏移 0x{offset:X}";
            }
            StructureContent.Children.Add(tb);
        }

        private void BtnStrings_Click(object sender, RoutedEventArgs e)
        {
            _stringsPanelVisible = !_stringsPanelVisible;
            StringsPanel.Visibility = _stringsPanelVisible ? Visibility.Visible : Visibility.Collapsed;

            if (_stringsPanelVisible)
            {
                // 关闭结构面板
                _structurePanelVisible = false;
                StructurePanel.Visibility = Visibility.Collapsed;

                if (_strings == null)
                {
                    StatusText.Text = "正在提取字符串...";
                    // 后台提取
                    var bw = new System.ComponentModel.BackgroundWorker();
                    bw.DoWork += (s, ev) =>
                    {
                        _strings = ExtractStrings(4);
                    };
                    bw.RunWorkerCompleted += (s, ev) =>
                    {
                        _allStrings = _strings;
                        StringsListBox.ItemsSource = _strings;
                        StringsCount.Text = $"{_strings?.Count ?? 0} 个";
                        StatusText.Text = $"提取完成: {_strings?.Count ?? 0} 个字符串 (仅扫描前16MB)";
                    };
                    bw.RunWorkerAsync();
                }
            }
        }

        private void StringsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_allStrings == null) return;

            string keyword = StringsSearchBox.Text.Trim();
            if (string.IsNullOrEmpty(keyword))
            {
                _strings = _allStrings;
                StringsCount.Text = $"{_strings.Count} 个";
            }
            else
            {
                _strings = _allStrings
                    .Where(s => s.Text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                StringsCount.Text = $"{_strings.Count} / {_allStrings.Count} 个";
            }
            StringsListBox.ItemsSource = _strings;
        }

        private void StringsSearchClear_Click(object sender, RoutedEventArgs e)
        {
            StringsSearchBox.Text = "";
        }

        private void DataInterpret_Click(object sender, RoutedEventArgs e)
        {
            if (HexListBox.SelectedItem is not HexLine line || _stream == null)
            {
                MessageBox.Show("请先选中一行十六进制数据。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                // 读取当前位置的64字节用于解释
                _stream.Seek(line.OffsetLong, SeekOrigin.Begin);
                byte[] data = new byte[Math.Min(64, _length - line.OffsetLong)];
                int read = _stream.Read(data, 0, data.Length);
                if (read <= 0) return;

                var sb = new StringBuilder();
                sb.AppendLine($"=== 数据解释 @ 0x{line.OffsetLong:X} ===");
                sb.AppendLine($"原始字节 (前16): {BitConverter.ToString(data, 0, Math.Min(16, read)).Replace("-", " ")}");
                sb.AppendLine();

                // 8位
                if (read >= 1)
                {
                    sb.AppendLine($"--- 8位 ---");
                    sb.AppendLine($"  uint8:  {data[0]} (0x{data[0]:X2})");
                    sb.AppendLine($"  int8:   {(sbyte)data[0]}");
                    sb.AppendLine();
                }

                // 16位
                if (read >= 2)
                {
                    sb.AppendLine($"--- 16位 ---");
                    sb.AppendLine($"  uint16 LE: {BitConverter.ToUInt16(data, 0)} (0x{BitConverter.ToUInt16(data, 0):X4})");
                    sb.AppendLine($"  int16  LE: {BitConverter.ToInt16(data, 0)}");
                    Array.Reverse(data, 0, 2);
                    sb.AppendLine($"  uint16 BE: {BitConverter.ToUInt16(data, 0)} (0x{BitConverter.ToUInt16(data, 0):X4})");
                    sb.AppendLine($"  int16  BE: {BitConverter.ToInt16(data, 0)}");
                    Array.Reverse(data, 0, 2); // 恢复
                    sb.AppendLine();
                }

                // 32位
                if (read >= 4)
                {
                    sb.AppendLine($"--- 32位 ---");
                    sb.AppendLine($"  uint32 LE: {BitConverter.ToUInt32(data, 0)} (0x{BitConverter.ToUInt32(data, 0):X8})");
                    sb.AppendLine($"  int32  LE: {BitConverter.ToInt32(data, 0)}");
                    sb.AppendLine($"  float  LE: {BitConverter.ToSingle(data, 0)}");
                    Array.Reverse(data, 0, 4);
                    sb.AppendLine($"  uint32 BE: {BitConverter.ToUInt32(data, 0)} (0x{BitConverter.ToUInt32(data, 0):X8})");
                    sb.AppendLine($"  int32  BE: {BitConverter.ToInt32(data, 0)}");
                    sb.AppendLine($"  float  BE: {BitConverter.ToSingle(data, 0)}");
                    Array.Reverse(data, 0, 4); // 恢复
                    sb.AppendLine();
                }

                // 64位
                if (read >= 8)
                {
                    sb.AppendLine($"--- 64位 ---");
                    sb.AppendLine($"  uint64 LE: {BitConverter.ToUInt64(data, 0)} (0x{BitConverter.ToUInt64(data, 0):X16})");
                    sb.AppendLine($"  int64  LE: {BitConverter.ToInt64(data, 0)}");
                    sb.AppendLine($"  double LE: {BitConverter.ToDouble(data, 0)}");
                    Array.Reverse(data, 0, 8);
                    sb.AppendLine($"  uint64 BE: {BitConverter.ToUInt64(data, 0)} (0x{BitConverter.ToUInt64(data, 0):X16})");
                    sb.AppendLine($"  int64  BE: {BitConverter.ToInt64(data, 0)}");
                    sb.AppendLine($"  double BE: {BitConverter.ToDouble(data, 0)}");
                    Array.Reverse(data, 0, 8); // 恢复
                    sb.AppendLine();
                }

                // 字符串
                sb.AppendLine($"--- 字符串 ---");
                // ASCII
                var asciiStr = new StringBuilder();
                for (int i = 0; i < Math.Min(32, read); i++)
                {
                    if (data[i] >= 0x20 && data[i] <= 0x7E)
                        asciiStr.Append((char)data[i]);
                    else
                        asciiStr.Append('.');
                }
                sb.AppendLine($"  ASCII (前32): \"{asciiStr}\"");

                // UTF-16 LE
                try
                {
                    var utf16 = Encoding.Unicode.GetString(data, 0, Math.Min(32, read / 2 * 2));
                    var cleanUtf16 = new string(utf16.TakeWhile(c => c != '\0').ToArray());
                    if (!string.IsNullOrEmpty(cleanUtf16))
                        sb.AppendLine($"  UTF-16 LE:    \"{cleanUtf16}\"");
                }
                catch { }

                MessageBox.Show(sb.ToString(), $"数据解释 @ 0x{line.OffsetLong:X}", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"数据解释失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CopyOffset_Click(object sender, RoutedEventArgs e)
        {
            if (HexListBox.SelectedItem is HexLine line)
            {
                Clipboard.SetText($"0x{line.OffsetLong:X}");
            }
        }

        private void CopyHex_Click(object sender, RoutedEventArgs e)
        {
            if (HexListBox.SelectedItem is HexLine line)
            {
                Clipboard.SetText(line.Hex);
            }
        }

        private void BtnGoto_Click(object sender, RoutedEventArgs e)
        {
            // 简单的跳转对话框
            var inputDialog = new Window
            {
                Title = "跳转偏移",
                Width = 300,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                Background = System.Windows.Media.Brushes.White
            };

            var panel = new StackPanel { Margin = new Thickness(10) };
            var label = new TextBlock { Text = "输入十六进制偏移 (如 0x1000):", Margin = new Thickness(0, 0, 0, 5) };
            var textBox = new TextBox { Name = "OffsetInput", Margin = new Thickness(0, 0, 0, 10) };
            var okButton = new Button { Content = "跳转", Width = 80, HorizontalAlignment = HorizontalAlignment.Right };

            panel.Children.Add(label);
            panel.Children.Add(textBox);
            panel.Children.Add(okButton);
            inputDialog.Content = panel;

            okButton.Click += (s, ev) =>
            {
                try
                {
                    string input = textBox.Text.Trim();
                    if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        input = input.Substring(2);
                    long offset = Convert.ToInt64(input, 16);
                    GotoOffset(offset);
                    inputDialog.Close();
                }
                catch
                {
                    MessageBox.Show("无效的偏移地址", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            inputDialog.ShowDialog();
        }

        /// <summary>
        /// 跳转到指定偏移
        /// </summary>
        public void GotoOffset(long offset)
        {
            if (HexListBox.ItemsSource is not VirtualizingHexCollection collection) return;

            int lineIndex = (int)(offset / BytesPerLine);
            if (lineIndex < 0 || lineIndex >= collection.Count) return;

            HexListBox.ScrollIntoView(collection[lineIndex]);
            HexListBox.SelectedIndex = lineIndex;
            StatusText.Text = $"跳转到偏移: 0x{offset:X} (行 {lineIndex:N0})";
        }

        private void StringsListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (StringsListBox.SelectedItem is StringEntry entry)
            {
                GotoOffset(entry.Offset);
            }
        }

        private void HexListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HexListBox.SelectedItem is HexLine line)
            {
                StatusText.Text = $"选中偏移: 0x{line.Offset} | {line.Hex}";
            }
        }

        private static string FormatSize(long size)
        {
            return size switch
            {
                > 1073741824 => $"{size / 1073741824.0:F2} GB",
                > 1048576 => $"{size / 1048576.0:F2} MB",
                > 1024 => $"{size / 1024.0:F2} KB",
                _ => $"{size} B"
            };
        }
    }

    /// <summary>
    /// 字符串条目
    /// </summary>
    public class StringEntry
    {
        public long Offset { get; set; }
        public string Text { get; set; } = "";
        public int Length { get; set; }
        public string Type { get; set; } = "ASCII";
        public string OffsetDisplay => $"0x{Offset:X} ({Length} 字符, {Type})";
    }

    /// <summary>
    /// 十六进制数据行
    /// </summary>
    public class HexLine
    {
        public string Offset { get; set; } = "";
        public long OffsetLong { get; set; }
        public string Hex { get; set; } = "";
        public string Ascii { get; set; } = "";
    }

    /// <summary>
    /// 虚拟化十六进制数据集合，按需读取数据。同时实现泛型和非泛型IList，确保WPF虚拟化生效。
    /// </summary>
    public class VirtualizingHexCollection : IList<HexLine>, IList
    {
        private readonly Stream _stream;
        private readonly long _length;
        private const int BytesPerLine = 16;
        private readonly byte[] _readBuffer = new byte[BytesPerLine];

        public VirtualizingHexCollection(Stream stream, long length)
        {
            _stream = stream;
            _length = length;
        }

        public int Count => (int)((_length + BytesPerLine - 1) / BytesPerLine);

        public bool IsReadOnly => true;
        public bool IsFixedSize => true;
        public object SyncRoot => this;
        public bool IsSynchronized => false;

        public HexLine this[int index]
        {
            get
            {
                if (index < 0 || index >= Count)
                    throw new ArgumentOutOfRangeException(nameof(index));

                long offset = (long)index * BytesPerLine;
                return ReadLine(offset);
            }
            set => throw new NotSupportedException();
        }

        object? IList.this[int index]
        {
            get => this[index];
            set => throw new NotSupportedException();
        }

        private HexLine ReadLine(long offset)
        {
            int bytesToRead = (int)Math.Min(BytesPerLine, _length - offset);

            _stream.Seek(offset, SeekOrigin.Begin);
            int read = _stream.Read(_readBuffer, 0, bytesToRead);

            var hexSb = new StringBuilder();
            var asciiSb = new StringBuilder();

            for (int i = 0; i < BytesPerLine; i++)
            {
                if (i < read)
                {
                    byte b = _readBuffer[i];
                    hexSb.Append(b.ToString("X2"));
                    hexSb.Append(' ');

                    if (b >= 32 && b <= 126)
                        asciiSb.Append((char)b);
                    else
                        asciiSb.Append('.');
                }
                else
                {
                    hexSb.Append("   ");
                    asciiSb.Append(' ');
                }

                if (i == 7)
                {
                    hexSb.Append(' ');
                }
            }

            return new HexLine
            {
                Offset = offset.ToString("X8"),
                OffsetLong = offset,
                Hex = hexSb.ToString().TrimEnd(),
                Ascii = asciiSb.ToString()
            };
        }

        public IEnumerator<HexLine> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
                yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public int IndexOf(HexLine item) => -1;
        public void Insert(int index, HexLine item) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
        public void Add(HexLine item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(HexLine item) => false;
        public void CopyTo(HexLine[] array, int arrayIndex) => throw new NotSupportedException();
        public bool Remove(HexLine item) => throw new NotSupportedException();

        // 非泛型IList实现
        public int Add(object? value) => throw new NotSupportedException();
        public bool Contains(object? value) => false;
        public int IndexOf(object? value) => -1;
        public void Insert(int index, object? value) => throw new NotSupportedException();
        public void Remove(object? value) => throw new NotSupportedException();
        public void CopyTo(Array array, int index) => throw new NotSupportedException();
    }
}
