using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.Views.Tools
{
    public partial class FirmwareParserWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private ObservableCollection<FirmwareTreeNode> _rootNodes = new();
        private UefiFirmwareParser.ParseResult? _result;
        private ElfParser.ElfFile? _elfResult;
        private string _firmwarePath = "";
        private bool _isElfFile;

        private bool _hasResult;
        public bool HasResult
        {
            get => _hasResult;
            set { _hasResult = value; OnPropertyChanged(nameof(HasResult)); }
        }

        private int _volumeCount;
        public int VolumeCount
        {
            get => _volumeCount;
            set { _volumeCount = value; OnPropertyChanged(nameof(VolumeCount)); }
        }

        private int _fileCount;
        public int FileCount
        {
            get => _fileCount;
            set { _fileCount = value; OnPropertyChanged(nameof(FileCount)); }
        }

        private int _sectionCount;
        public int SectionCount
        {
            get => _sectionCount;
            set { _sectionCount = value; OnPropertyChanged(nameof(SectionCount)); }
        }

        public FirmwareParserWindow()
        {
            InitializeComponent();
            DataContext = this;
            TreeFirmware.ItemsSource = _rootNodes;
        }

        public FirmwareParserWindow(string firmwarePath) : this()
        {
            LoadFirmware(firmwarePath);
        }

        private void LoadFirmware_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择固件文件",
                Filter = "固件文件|*.bin;*.rom;*.fd;*.fv;*.efi;*.img|所有文件|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                LoadFirmware(dialog.FileName);
            }
        }

        public void LoadFirmware(string firmwarePath)
        {
            try
            {
                _firmwarePath = firmwarePath;
                TxtFilePath.Text = firmwarePath;
                TxtStatus.Text = "正在解析固件...";
                Cursor = System.Windows.Input.Cursors.Wait;

                byte[] data = File.ReadAllBytes(firmwarePath);

                // 检测是否为 ELF 文件
                if (ElfParser.IsElfFile(data))
                {
                    _isElfFile = true;
                    _result = null;
                    _elfResult = ElfParser.Parse(data);
                    LoadElfResult();
                }
                else
                {
                    _isElfFile = false;
                    _elfResult = null;
                    _result = UefiFirmwareParser.Parse(data);
                    LoadUefiResult();
                }

                Cursor = null;
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"解析失败: {ex.Message}";
                Cursor = null;
                MessageBox.Show($"解析失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadUefiResult()
        {
            if (_result == null) return;

            // 更新统计
            HasResult = _result.Volumes.Count > 0;
            VolumeCount = _result.Volumes.Count;
            FileCount = _result.TotalFiles;
            SectionCount = _result.TotalSections;

            if (!string.IsNullOrEmpty(_result.BuildId))
            {
                TxtBuildId.Text = $"Build ID: {_result.BuildId}";
            }
            else
            {
                TxtBuildId.Text = "";
            }

            // 构建树
            _rootNodes.Clear();
            foreach (var volume in _result.Volumes)
            {
                var volumeNode = new FirmwareTreeNode
                {
                    Icon = "📦",
                    Name = volume.Name,
                    TypeName = $"FV, {Math.Round(volume.Size / 1024.0 / 1024.0, 2)} MB",
                    Detail = BuildVolumeDetail(volume)
                };

                foreach (var file in volume.Files)
                {
                    var fileNode = new FirmwareTreeNode
                    {
                        Icon = file.Type == 0x07 ? "⚙️" : file.Type == 0x09 ? "📱" : "📄",
                        Name = file.Name ?? file.Guid.ToString().Substring(0, 8),
                        TypeName = file.TypeName,
                        Detail = BuildFileDetail(file)
                    };

                    foreach (var section in file.Sections)
                    {
                        var sectionNode = new FirmwareTreeNode
                        {
                            Icon = section.Type == 0x10 ? "🔧" : section.Type == 0x15 ? "🏷️" : "📝",
                            Name = section.Name ?? section.TypeName,
                            TypeName = $"{section.TypeName}, {section.Size} bytes",
                            Detail = BuildSectionDetail(section)
                        };
                        fileNode.Children.Add(sectionNode);
                    }

                    volumeNode.Children.Add(fileNode);
                }

                _rootNodes.Add(volumeNode);
            }

            TxtStatus.Text = $"解析完成 (UEFI)，找到 {_result.Volumes.Count} 个 FV, {_result.TotalFiles} 个文件";

            if (_result.Errors.Count > 0)
            {
                TxtStatus.Text += $" ({_result.Errors.Count} 个警告)";
            }
        }

        private void LoadElfResult()
        {
            if (_elfResult == null) return;

            HasResult = _elfResult.IsValid;
            VolumeCount = _elfResult.ProgramHeaders.Count;
            FileCount = _elfResult.SectionHeaders.Count;
            SectionCount = _elfResult.Symbols.Count;

            TxtBuildId.Text = $"{_elfResult.Header.ClassName} / {_elfResult.Header.MachineName} / {_elfResult.Header.TypeName}";

            // 构建树
            _rootNodes.Clear();

            // ELF 头节点
            var headerNode = new FirmwareTreeNode
            {
                Icon = "🎯",
                Name = "ELF Header",
                TypeName = $"{_elfResult.Header.ClassName}, {_elfResult.Header.MachineName}",
                Detail = BuildElfHeaderDetail(_elfResult.Header)
            };
            _rootNodes.Add(headerNode);

            // 程序头节点
            if (_elfResult.ProgramHeaders.Count > 0)
            {
                var phNode = new FirmwareTreeNode
                {
                    Icon = "📋",
                    Name = $"Program Headers ({_elfResult.ProgramHeaders.Count})",
                    TypeName = "Segments",
                    Detail = "程序头（段）列表"
                };

                for (int i = 0; i < _elfResult.ProgramHeaders.Count; i++)
                {
                    var ph = _elfResult.ProgramHeaders[i];
                    var node = new FirmwareTreeNode
                    {
                        Icon = "📄",
                        Name = $"[{i}] {ph.TypeName}",
                        TypeName = $"{ph.FlagsName}, {ph.FileSize:N0} bytes",
                        Detail = BuildElfProgramHeaderDetail(ph)
                    };
                    phNode.Children.Add(node);
                }

                _rootNodes.Add(phNode);
            }

            // 节头节点
            if (_elfResult.SectionHeaders.Count > 0)
            {
                var shNode = new FirmwareTreeNode
                {
                    Icon = "📁",
                    Name = $"Section Headers ({_elfResult.SectionHeaders.Count})",
                    TypeName = "Sections",
                    Detail = "节头列表"
                };

                for (int i = 0; i < _elfResult.SectionHeaders.Count; i++)
                {
                    var sh = _elfResult.SectionHeaders[i];
                    var node = new FirmwareTreeNode
                    {
                        Icon = "📝",
                        Name = $"[{i}] {sh.Name}",
                        TypeName = $"{sh.TypeName}, {sh.Size:N0} bytes",
                        Detail = BuildElfSectionHeaderDetail(sh)
                    };
                    shNode.Children.Add(node);
                }

                _rootNodes.Add(shNode);
            }

            // 符号表节点
            if (_elfResult.Symbols.Count > 0)
            {
                var symNode = new FirmwareTreeNode
                {
                    Icon = "🔣",
                    Name = $"Symbols ({_elfResult.Symbols.Count})",
                    TypeName = "Symbol Table",
                    Detail = "符号表（仅显示有名称的符号）"
                };

                int shown = 0;
                foreach (var sym in _elfResult.Symbols)
                {
                    if (!string.IsNullOrEmpty(sym.Name) && shown < 500) // 限制显示数量
                    {
                        var node = new FirmwareTreeNode
                        {
                            Icon = sym.Type == 2 ? "⚡" : "📊",
                            Name = sym.Name,
                            TypeName = $"{sym.TypeName}/{sym.BindName}, {sym.Size:N0} bytes",
                            Detail = BuildElfSymbolDetail(sym)
                        };
                        symNode.Children.Add(node);
                        shown++;
                    }
                }

                _rootNodes.Add(symNode);
            }

            TxtStatus.Text = $"解析完成 (ELF)，{_elfResult.Header.ClassName}, {_elfResult.ProgramHeaders.Count} 段, {_elfResult.SectionHeaders.Count} 节, {_elfResult.Symbols.Count} 符号";

            if (_elfResult.Errors.Count > 0)
            {
                TxtStatus.Text += $" ({_elfResult.Errors.Count} 个警告)";
            }
        }

        private void TreeFirmware_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is FirmwareTreeNode node)
            {
                TxtDetailTitle.Text = $"{node.Icon} {node.Name}";
                TxtDetailContent.Text = node.Detail;
            }
        }

        private void ExportReport_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null && _elfResult == null) return;

            var dialog = new SaveFileDialog
            {
                Title = "导出固件解析报告",
                FileName = $"firmware_report_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                Filter = "文本文件|*.txt"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    string report;
                    if (_isElfFile && _elfResult != null)
                    {
                        report = ElfParser.GenerateReport(_elfResult);
                    }
                    else if (_result != null)
                    {
                        report = UefiFirmwareParser.GenerateReport(_result);
                    }
                    else
                    {
                        return;
                    }

                    File.WriteAllText(dialog.FileName, report, Encoding.UTF8);
                    MessageBox.Show($"报告已导出到:\n{dialog.FileName}", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private string BuildVolumeDetail(UefiFirmwareParser.FirmwareVolume volume)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"类型: Firmware Volume (FV)");
            sb.AppendLine($"偏移: 0x{volume.Offset:X}");
            sb.AppendLine($"大小: {volume.Size:N0} 字节 ({Math.Round(volume.Size / 1024.0 / 1024.0, 2)} MB)");
            sb.AppendLine($"Magic: {volume.Magic}");
            sb.AppendLine($"文件数: {volume.Files.Count}");
            sb.AppendLine();
            sb.AppendLine("文件列表:");
            foreach (var file in volume.Files)
            {
                string name = file.Name ?? file.Guid.ToString();
                sb.AppendLine($"  - {name} ({file.TypeName})");
            }
            return sb.ToString();
        }

        private string BuildFileDetail(UefiFirmwareParser.FirmwareFile file)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"类型: {file.TypeName} (0x{file.Type:X2})");
            sb.AppendLine($"GUID: {file.Guid}");
            sb.AppendLine($"偏移: 0x{file.Offset:X}");
            sb.AppendLine($"大小: {file.Size:N0} 字节");
            sb.AppendLine($"校验和: {(file.ChecksumValid ? "有效" : "无效/忽略")}");
            sb.AppendLine($"Section 数: {file.Sections.Count}");
            if (file.Sections.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Sections:");
                foreach (var section in file.Sections)
                {
                    string name = section.Name ?? section.TypeName;
                    string compressed = section.IsCompressed ? $" [压缩: {section.CompressionType}]" : "";
                    sb.AppendLine($"  - {name} ({section.TypeName}, {section.Size} bytes){compressed}");
                }
            }
            return sb.ToString();
        }

        private string BuildSectionDetail(UefiFirmwareParser.FirmwareSection section)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"类型: {section.TypeName} (0x{section.Type:X2})");
            sb.AppendLine($"偏移: 0x{section.Offset:X}");
            sb.AppendLine($"大小: {section.Size:N0} 字节");
            if (!string.IsNullOrEmpty(section.Name))
            {
                sb.AppendLine($"名称: {section.Name}");
            }
            if (section.IsCompressed)
            {
                sb.AppendLine($"压缩类型: {section.CompressionType}");
            }
            if (section.Data != null && section.Data.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine("数据 (前64字节):");
                int displayLen = Math.Min(section.Data.Length, 64);
                for (int i = 0; i < displayLen; i += 16)
                {
                    int lineLen = Math.Min(16, displayLen - i);
                    sb.Append("  ");
                    for (int j = 0; j < lineLen; j++)
                    {
                        sb.Append($"{section.Data[i + j]:X2} ");
                    }
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        #region ELF 详情构建

        private string BuildElfHeaderDetail(ElfParser.ElfHeader header)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== ELF Header ===");
            sb.AppendLine($"格式: {header.ClassName} ({header.DataName})");
            sb.AppendLine($"类型: {header.TypeName} (0x{header.Type:X4})");
            sb.AppendLine($"架构: {header.MachineName} (0x{header.Machine:X4})");
            sb.AppendLine($"版本: {header.Version2}");
            sb.AppendLine($"入口点: 0x{header.EntryPoint:X}");
            sb.AppendLine($"程序头偏移: 0x{header.ProgramHeaderOffset:X}");
            sb.AppendLine($"节头偏移: 0x{header.SectionHeaderOffset:X}");
            sb.AppendLine($"标志: 0x{header.Flags:X8}");
            sb.AppendLine($"ELF头大小: {header.HeaderSize}");
            sb.AppendLine($"程序头: {header.ProgramHeaderCount} 个, 每个 {header.ProgramHeaderEntrySize} 字节");
            sb.AppendLine($"节头: {header.SectionHeaderCount} 个, 每个 {header.SectionHeaderEntrySize} 字节");
            sb.AppendLine($"节名字符串表索引: {header.SectionNameStringTableIndex}");
            sb.AppendLine($"OS/ABI: 0x{header.OsAbi:X2} (版本 {header.AbiVersion})");
            return sb.ToString();
        }

        private string BuildElfProgramHeaderDetail(ElfParser.ProgramHeader ph)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Program Header ===");
            sb.AppendLine($"类型: {ph.TypeName} (0x{ph.Type:X8})");
            sb.AppendLine($"标志: {ph.FlagsName} (0x{ph.Flags:X8})");
            sb.AppendLine($"文件偏移: 0x{ph.Offset:X}");
            sb.AppendLine($"虚拟地址: 0x{ph.VirtualAddress:X}");
            sb.AppendLine($"物理地址: 0x{ph.PhysicalAddress:X}");
            sb.AppendLine($"文件大小: {ph.FileSize:N0} 字节");
            sb.AppendLine($"内存大小: {ph.MemorySize:N0} 字节");
            sb.AppendLine($"对齐: 0x{ph.Alignment:X}");
            return sb.ToString();
        }

        private string BuildElfSectionHeaderDetail(ElfParser.SectionHeader sh)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Section Header ===");
            sb.AppendLine($"名称: {sh.Name}");
            sb.AppendLine($"类型: {sh.TypeName} (0x{sh.Type:X8})");
            sb.AppendLine($"标志: {sh.FlagsName} (0x{sh.Flags:X16})");
            sb.AppendLine($"地址: 0x{sh.Address:X}");
            sb.AppendLine($"偏移: 0x{sh.Offset:X}");
            sb.AppendLine($"大小: {sh.Size:N0} 字节");
            sb.AppendLine($"链接: {sh.Link}");
            sb.AppendLine($"信息: {sh.Info}");
            sb.AppendLine($"对齐: 0x{sh.Alignment:X}");
            sb.AppendLine($"入口大小: {sh.EntrySize:N0} 字节");
            return sb.ToString();
        }

        private string BuildElfSymbolDetail(ElfParser.Symbol sym)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Symbol ===");
            sb.AppendLine($"名称: {sym.Name}");
            sb.AppendLine($"值: 0x{sym.Value:X}");
            sb.AppendLine($"大小: {sym.Size:N0} 字节");
            sb.AppendLine($"类型: {sym.TypeName} ({sym.Type})");
            sb.AppendLine($"绑定: {sym.BindName} ({sym.Bind})");
            sb.AppendLine($"其他: 0x{sym.Other:X2}");
            sb.AppendLine($"节索引: {sym.SectionIndex}");
            return sb.ToString();
        }

        #endregion

        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// 固件树节点
    /// </summary>
    public class FirmwareTreeNode
    {
        public string Icon { get; set; } = "";
        public string Name { get; set; } = "";
        public string TypeName { get; set; } = "";
        public string Detail { get; set; } = "";
        public ObservableCollection<FirmwareTreeNode> Children { get; } = new();
    }
}
