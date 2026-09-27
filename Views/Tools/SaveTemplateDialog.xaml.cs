using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.Views.Tools
{
    /// <summary>
    /// 「另存为自定义模板」对话框：收集标识/显示名/适用设备/备注，并可选绑定一条官方校验规则
    /// （从 Assets\ValidationRules 实际存在的文件里选）。
    /// </summary>
    public partial class SaveTemplateDialog : Window
    {
        public string TemplateKey => TxtKey.Text.Trim();
        public string TemplateTitle => TxtTitle.Text.Trim();
        public string TemplateDeviceType => TxtDeviceType.Text.Trim();
        public string TemplateNotes => TxtNotes.Text.Trim();
        /// <summary>"时代/文件名"；未选则空串。</summary>
        public string RuleChoice => (CmbRule.SelectedItem as RuleItem)?.Value ?? string.Empty;

        record RuleItem(string Value, string Display) { public override string ToString() => Display; }

        public SaveTemplateDialog(Window owner)
        {
            InitializeComponent();
            var rules = DeviceLayoutRuleChecker.ListRules();
            var items = new System.Collections.Generic.List<RuleItem>
            {
                new(string.Empty, "（不绑定 — 只做结构自检）")
            };
            items.AddRange(rules.Select(r => new RuleItem($"{r.Era}/{r.Name}", $"{r.Era} · {r.Name}")));
            CmbRule.ItemsSource = items;
            CmbRule.SelectedIndex = 0;
        }

        void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtKey.Text) || string.IsNullOrWhiteSpace(TxtTitle.Text))
            {
                MessageBox.Show(this, "模板标识与显示名不能为空。", "缺少信息",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // 与内置重名时给出提示（保存逻辑会自动加 _custom，这里先让用户知情）
            if (DeviceLayoutTemplates.ListAll().Any(t => !t.Custom &&
                string.Equals(t.Key, TxtKey.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                TxtKey.Text += "_custom";
            }
            DialogResult = true;
        }

        void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
