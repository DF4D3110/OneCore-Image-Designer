using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.Views.Tools
{
    public partial class WinSxSHashCalculatorWindow : Window
    {
        /// <summary>表示 manifest 内部没有 versionScope 属性。</summary>
        private const string NoScopeOption = "(无该属性)";

        public WinSxSHashCalculatorWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 读取架构输入。
        ///
        /// 注意：ComboBox 设了 IsEditable=True，用户手输时 SelectedItem 为 null，
        /// 原实现用 SelectedItem 会静默回退成 "x86" 而算出错误哈希 —— 故改用 Text。
        /// </summary>
        private string GetArchitecture()
        {
            string text = CmbArchitecture.Text?.Trim() ?? string.Empty;
            if (text.Length > 0) return text;

            return (CmbArchitecture.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "amd64";
        }

        /// <summary>读取 versionScope；选“(无该属性)”时返回 null（该字段不参与哈希）。</summary>
        private string? GetVersionScope()
        {
            string text = CmbVersionScope.Text?.Trim() ?? string.Empty;
            if (text.Length == 0)
                text = (CmbVersionScope.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;

            if (text.Length == 0 || text == NoScopeOption) return null;
            return text;
        }

        private void Calculate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string name = TxtName.Text.Trim();
                string publicKeyToken = TxtPublicKeyToken.Text.Trim();
                string architecture = GetArchitecture();
                string version = TxtVersion.Text.Trim();
                string culture = TxtCulture.Text.Trim();
                string? versionScope = GetVersionScope();

                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(publicKeyToken) || string.IsNullOrEmpty(version))
                {
                    MessageBox.Show("请填写包名、公钥令牌和版本。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // .inf 驱动类组件属已知边界，明确告知而不是给一个错误结果
                if (WinSxSHashCalculator.IsInfComponent(name))
                {
                    TxtWarning.Text =
                        "⚠ 该组件为 .inf 驱动类：其哈希无法用 X65599Variant 算法复现" +
                        "（实测 663/663 全部不命中）。下面的结果不可用于校验或改名。";
                }
                else
                {
                    TxtWarning.Text = string.Empty;
                }

                // 计算 hash
                string hash = WinSxSHashCalculator.CalculateWinSxSHash(
                    name, publicKeyToken, architecture, version, culture, versionScope);
                TxtHashResult.Text = hash;

                // 生成 WinSxS 文件夹名
                TxtFolderName.Text = WinSxSHashCalculator.GenerateWinSxFolderName(
                    name, publicKeyToken, architecture, version, culture, versionScope);

                // 生成 CBS 包名
                TxtCBSName.Text = WinSxSHashCalculator.GenerateCBSKeyform(
                    name, publicKeyToken, architecture, culture, version);

                // 展示实际参与哈希的字段，便于排查
                TxtFields.Text = DescribeFields(name, culture, version, publicKeyToken, architecture, versionScope);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"计算失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 按实际参与顺序列出字段。哈希对字段顺序敏感，展示出来便于核对输入。
        /// </summary>
        private static string DescribeFields(
            string name, string culture, string version, string pkt, string arch, string? versionScope)
        {
            var sb = new StringBuilder();
            sb.Append("name=\"").Append(name).Append('"');

            if (!WinSxSHashCalculator.IsNeutralCulture(culture))
                sb.Append(" → culture=\"").Append(culture).Append('"');
            else
                sb.Append(" → (culture 为中性，不参与)");

            sb.Append(" → version=\"").Append(version).Append('"');
            sb.Append(" → publicKeyToken=\"").Append(pkt).Append('"');
            sb.Append(" → processorArchitecture=\"").Append(arch).Append('"');

            if (!string.IsNullOrWhiteSpace(versionScope))
                sb.Append(" → versionScope=\"").Append(versionScope).Append('"');
            else
                sb.Append(" → (无 versionScope，不参与)");

            return sb.ToString();
        }

        private void Verify_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string folderName = TxtVerifyFolder.Text.Trim();
                if (string.IsNullOrEmpty(folderName))
                {
                    TxtVerifyResult.Text = "请输入要验证的文件夹名";
                    TxtVerifyResult.Foreground = Brushes.Orange;
                    return;
                }

                var info = WinSxSHashCalculator.ParseWinSxFolderName(folderName);
                if (info is null)
                {
                    TxtVerifyResult.Text = "❌ 无法解析文件夹名格式";
                    TxtVerifyResult.Foreground = Brushes.Red;
                    return;
                }

                if (info.IsInfComponent)
                {
                    TxtVerifyResult.Text =
                        $"⏭️ 跳过：{info.Name} 属 .inf 驱动类组件，\n" +
                        "其哈希无法用本算法复现，无法判定对错。";
                    TxtVerifyResult.Foreground = Brushes.Orange;
                    return;
                }

                bool isValid = WinSxSHashCalculator.VerifyWinSxFolderHash(folderName);
                if (isValid)
                {
                    TxtVerifyResult.Text = "✅ Hash 验证通过，文件夹名正确。";
                    TxtVerifyResult.Foreground = Brushes.Green;
                    return;
                }

                string calculated = WinSxSHashCalculator.CalculateWinSxSHash(
                    info.Name, info.PublicKeyToken, info.Architecture, info.Version,
                    info.Culture, WinSxSHashCalculator.DefaultVersionScope);

                TxtVerifyResult.Text =
                    $"❌ Hash 不一致\n" +
                    $"文件夹中的 Hash: {info.Hash}\n" +
                    $"计算得到的 Hash: {calculated}\n\n" +
                    "提示：不一致未必代表文件夹有误。文件夹名里的 name 段可能已被清理\n" +
                    "（如内部 “.NET Framework” → “.netframework”，实测 19% 的组件如此），\n" +
                    "且文件名不携带 versionScope。请改用上方输入 manifest 内部真实字段计算后比对。";
                TxtVerifyResult.Foreground = Brushes.Red;
            }
            catch (Exception ex)
            {
                TxtVerifyResult.Text = $"验证失败: {ex.Message}";
                TxtVerifyResult.Foreground = Brushes.Red;
            }
        }

        private void CopyFolder_Click(object sender, RoutedEventArgs e)
        {
            string text = TxtFolderName.Text;
            if (string.IsNullOrEmpty(text))
            {
                MessageBox.Show("请先点击“计算 Hash”。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
            {
                // 剪贴板被其他进程占用时不必中断流程
                MessageBox.Show($"复制到剪贴板失败：{ex.Message}", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
