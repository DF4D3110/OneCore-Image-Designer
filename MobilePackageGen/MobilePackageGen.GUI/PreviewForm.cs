namespace MobilePackageGen.GUI
{
    public partial class PreviewForm : Form
    {
        private List<int> matchPositions = new();
        private int currentMatchIndex = -1;

        public PreviewForm(string title, string content)
        {
            InitializeComponent();
            this.Text = title;
            txtContent.Text = content;
            txtContent.SelectionStart = 0;
            txtContent.SelectionLength = 0;
        }

        private void btnClose_Click(object? sender, EventArgs e)
        {
            this.Close();
        }

        private void btnCopy_Click(object? sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(txtContent.Text))
            {
                Clipboard.SetText(txtContent.Text);
                MessageBox.Show("Copied to clipboard.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void txtSearch_TextChanged(object? sender, EventArgs e)
        {
            PerformSearch();
        }

        private void txtSearch_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                if (e.Shift)
                    FindPrev();
                else
                    FindNext();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                txtSearch.Clear();
            }
        }

        private void btnFindNext_Click(object? sender, EventArgs e)
        {
            FindNext();
        }

        private void btnFindPrev_Click(object? sender, EventArgs e)
        {
            FindPrev();
        }

        private void PreviewForm_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.F)
            {
                e.SuppressKeyPress = true;
                txtSearch.Focus();
                txtSearch.SelectAll();
            }
        }

        private void PerformSearch()
        {
            matchPositions.Clear();
            currentMatchIndex = -1;

            var query = txtSearch.Text;
            if (string.IsNullOrEmpty(query))
            {
                lblSearchCount.Text = "";
                return;
            }

            var content = txtContent.Text;
            int pos = 0;
            while ((pos = content.IndexOf(query, pos, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                matchPositions.Add(pos);
                pos += query.Length;
            }

            lblSearchCount.Text = matchPositions.Count > 0
                ? $"{matchPositions.Count} match(es)"
                : "No matches";

            if (matchPositions.Count > 0)
            {
                currentMatchIndex = 0;
                HighlightMatch();
            }
        }

        private void FindNext()
        {
            if (matchPositions.Count == 0) return;
            currentMatchIndex = (currentMatchIndex + 1) % matchPositions.Count;
            HighlightMatch();
        }

        private void FindPrev()
        {
            if (matchPositions.Count == 0) return;
            currentMatchIndex = (currentMatchIndex - 1 + matchPositions.Count) % matchPositions.Count;
            HighlightMatch();
        }

        private void HighlightMatch()
        {
            if (currentMatchIndex < 0 || currentMatchIndex >= matchPositions.Count) return;
            var pos = matchPositions[currentMatchIndex];
            var len = txtSearch.Text.Length;
            txtContent.Focus();
            txtContent.Select(pos, len);
            txtContent.ScrollToCaret();
            lblSearchCount.Text = $"{currentMatchIndex + 1}/{matchPositions.Count}";
        }
    }
}
