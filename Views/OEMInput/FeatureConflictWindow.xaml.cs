using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using OneCore_Image_Designer.Models.FM;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.Views.OEMInput
{
    /// <summary>
    /// Feature 互斥约束冲突选择窗口（替代旧版 Microsoft.VisualBasic 的序号输入框）。
    /// 单选"保留哪个"，支持"全部取消"（ZeroOrOne 允许一个都不选）。
    /// </summary>
    public partial class FeatureConflictWindow : Window
    {
        public class Candidate
        {
            public string Id { get; set; } = string.Empty;
            public string Category { get; set; } = string.Empty;
        }

        /// <summary>用户确认要保留的 ID；NoneSelected=true 表示选择"全部取消"。</summary>
        public string? KeptId { get; private set; }
        public bool Confirmed { get; private set; }
        public bool UncheckAllRequested { get; private set; }

        private readonly List<RadioButton> _radios = new();

        public FeatureConflictWindow(FeatureGroupInfo group, IEnumerable<FeatureInfo> candidates)
        {
            InitializeComponent();

            string hint = group.ConstraintType switch
            {
                FeatureGroupConstraint.OneAndOnlyOne => "必须且只能保留一个：",
                FeatureGroupConstraint.ZeroOrOne => "最多保留一个（可全部取消）：",
                FeatureGroupConstraint.OneOrMore => "这是「至少选一个」的组，请确认要保留的项：",
                _ => "请选择要保留的 Feature："
            };

            TitleText.Text = $"Feature 组 \"{group.Name}\" 触发互斥约束（{group.Constraint}，来自 {group.SourceFM}）";
            DetailText.Text = hint;

            var list = candidates.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var rb = new RadioButton
                {
                    GroupName = "Keep",
                    Margin = new Thickness(0, 3, 0, 3),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Content = $"{list[i].ID}（{list[i].Category}）",
                    Tag = list[i].ID,
                    IsChecked = i == 0
                };
                CandidateList.Items.Add(rb);
                _radios.Add(rb);
            }

            UncheckAllButton.Visibility =
                group.ConstraintType == FeatureGroupConstraint.ZeroOrOne
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Keep_Click(object sender, RoutedEventArgs e)
        {
            var sel = _radios.FirstOrDefault(r => r.IsChecked == true);
            if (sel == null) { MessageBox.Show(this, "请先选择一个要保留的 Feature。"); return; }
            KeptId = sel.Tag as string;
            Confirmed = true;
            DialogResult = true;
        }

        private void UncheckAll_Click(object sender, RoutedEventArgs e)
        {
            UncheckAllRequested = true;
            Confirmed = true;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            DialogResult = false;
        }
    }
}
