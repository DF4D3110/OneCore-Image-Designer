using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.Views.OEMInput
{
    /// <summary>
    /// 跨 FM 重名 Feature 冲突：按"冲突簇"弹窗（一簇=一组互相共享重名 Feature 的 FM），
    /// 用户选"仅保留所选 FM"会把该簇里其他 FM 取消勾选；选"保留全部"按官方先到先得合并。
    /// </summary>
    public partial class FmDuplicateWindow : Window
    {
        public bool KeepAllRequested { get; private set; }
        public bool Skipped { get; private set; } = true;
        public DiscoveredFmItem? Winner { get; private set; }

        private readonly List<RadioButton> _radios = new();

        public FmDuplicateWindow(List<string> sharedFeatureIds, List<DiscoveredFmItem> candidates)
        {
            InitializeComponent();

            string sample = string.Join("、", sharedFeatureIds.Take(3));
            if (sharedFeatureIds.Count > 3) sample += $" 等 {sharedFeatureIds.Count} 个";
            TitleText.Text = $"{candidates.Count} 个 FM 共享重名 Feature：{sample}";
            DetailText.Text = "官方按“先到先得”合并重复 FeatureID。选择保留哪个 FM，或保留全部交给官方规则合并。";

            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                string sub = c.FromCab ? $"CAB: {System.IO.Path.GetFileName(c.ContainingCab)}" : $"XML: {c.XmlPath}";
                var rb = new RadioButton
                {
                    GroupName = "Winner",
                    Margin = new Thickness(0, 4, 0, 4),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    IsChecked = i == 0,
                    Tag = c,
                    Content = $"{c.DisplayName}  ·  {sub}",
                };
                CandidateList.Items.Add(rb);
                _radios.Add(rb);
            }
        }

        private void KeepAll_Click(object sender, RoutedEventArgs e)
        {
            KeepAllRequested = true; Skipped = false;
            DialogResult = true;
        }

        private void KeepOne_Click(object sender, RoutedEventArgs e)
        {
            var sel = _radios.FirstOrDefault(r => r.IsChecked == true);
            if (sel == null) { MessageBox.Show(this, "请先选择一个要保留的 FM。"); return; }
            Winner = sel.Tag as DiscoveredFmItem;
            Skipped = false;
            DialogResult = true;
        }

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            Skipped = true;
            DialogResult = false;
        }
    }
}
