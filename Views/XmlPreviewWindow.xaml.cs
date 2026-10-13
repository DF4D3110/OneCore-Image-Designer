using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace OneCore_Image_Designer.Views
{
    /// <summary>
    /// XML/文本 预览窗口：FlowDocument 渲染，标签/属性/值/注释/文本分色。
    /// 纯 UI 组件，内容获取（磁盘文件或镜像内文件）由调用方负责。
    /// </summary>
    public partial class XmlPreviewWindow : Window
    {
        private string _plain = "";

        public XmlPreviewWindow(string title, string content, string note, bool asXml)
        {
            InitializeComponent();
            TxtTitle.Text = title;
            TxtNote.Text = note;
            _plain = content;
            DocView.Document = asXml ? BuildXmlDoc(content) : BuildPlainDoc(content);
            DocView.Visibility = Visibility.Visible;
        }

        static readonly Brush TagBrush = Frozen(0x871B5F);   // 标签：品红（VS 风格）
        static readonly Brush AttrBrush = Frozen(0xE50000);  // 属性名：红
        static readonly Brush ValBrush = Frozen(0x0451A5);   // 属性值：蓝
        static readonly Brush CommentBrush = Frozen(0x008000);
        static readonly Brush TextBrush = Frozen(0x111111);
        static readonly Brush DeclBrush = Frozen(0x808080);

        static SolidColorBrush Frozen(int rgb)
        {
            var b = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            b.Freeze();
            return b;
        }

        static Paragraph Para(string text, Brush brush, double indent = 0)
            => new(new Run(text) { Foreground = brush, FontFamily = new FontFamily("Consolas"), FontSize = 12.5 })
            { Margin = new Thickness(indent * 12, 0, 0, 0) };

        static FlowDocument BuildPlainDoc(string content)
        {
            var doc = new FlowDocument { FontFamily = new FontFamily("Consolas"), PagePadding = new Thickness(8) };
            foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
                doc.Blocks.Add(Para(line, TextBrush));
            return doc;
        }

        static FlowDocument BuildXmlDoc(string content)
        {
            var doc = new FlowDocument { FontFamily = new FontFamily("Consolas"), PagePadding = new Thickness(8) };
            foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
            {
                int indent = 0;
                while (indent < line.Length && line[indent] == ' ') indent++;
                string t = line.TrimStart();
                if (t.Length == 0) { doc.Blocks.Add(Para("", TextBrush, indent / 2.0)); continue; }
                if (t.StartsWith("<?")) { doc.Blocks.Add(Para(line, DeclBrush, indent / 2.0)); continue; }
                if (t.StartsWith("<!--")) { doc.Blocks.Add(Para(line, CommentBrush, indent / 2.0)); continue; }

                var p = new Paragraph { Margin = new Thickness(indent / 2.0 * 12, 0, 0, 0) };
                int i = 0;
                while (i < t.Length)
                {
                    int lt = t.IndexOf('<', i);
                    if (lt < 0) { p.Inlines.Add(new Run(t[i..]) { Foreground = TextBrush }); break; }
                    if (lt > i) p.Inlines.Add(new Run(t[i..lt]) { Foreground = TextBrush });
                    int gt = t.IndexOf('>', lt);
                    if (gt < 0) { p.Inlines.Add(new Run(t[lt..]) { Foreground = TagBrush }); break; }
                    ColorizeTag(p, t.Substring(lt, gt - lt + 1));
                    i = gt + 1;
                }
                doc.Blocks.Add(p);
            }
            return doc;
        }

        static void ColorizeTag(Paragraph p, string tag)
        {
            // <name  a="b" .../>  粗体标签名 + 红属性 + 蓝值
            int j = 0;
            while (j < tag.Length && tag[j] != ' ' && j < tag.Length - 1) j++;
            p.Inlines.Add(new Run(tag[..j]) { Foreground = TagBrush, FontWeight = FontWeights.Bold });
            var rest = tag[j..];
            int k = 0;
            while (k < rest.Length)
            {
                int eq = rest.IndexOf('=', k);
                if (eq < 0) { p.Inlines.Add(new Run(rest[k..]) { Foreground = TagBrush }); break; }
                int q1 = rest.IndexOf('"', eq);
                if (q1 < 0) { p.Inlines.Add(new Run(rest[k..]) { Foreground = TagBrush }); break; }
                int q2 = rest.IndexOf('"', q1 + 1);
                if (q2 < 0) q2 = rest.Length - 1;
                p.Inlines.Add(new Run(rest[k..(q1 + 1)]) { Foreground = AttrBrush });
                p.Inlines.Add(new Run(rest.Substring(q1 + 1, q2 - q1)) { Foreground = ValBrush });
                k = q2 + 1;
            }
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(_plain); } catch { }
        }
    }
}
