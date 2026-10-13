namespace MobilePackageGen.GUI
{
    partial class PreviewForm
    {
        private System.ComponentModel.IContainer components = null;
        private TextBox txtContent;
        private Button btnClose;
        private Button btnCopy;
        private Panel panelBottom;
        private Panel panelSearch;
        private TextBox txtSearch;
        private Button btnFindPrev;
        private Button btnFindNext;
        private Label lblSearchCount;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            txtContent = new TextBox();
            panelBottom = new Panel();
            btnCopy = new Button();
            btnClose = new Button();
            panelSearch = new Panel();
            lblSearchCount = new Label();
            btnFindPrev = new Button();
            btnFindNext = new Button();
            txtSearch = new TextBox();
            panelBottom.SuspendLayout();
            panelSearch.SuspendLayout();
            SuspendLayout();
            //
            // txtContent
            //
            txtContent.Dock = DockStyle.Fill;
            txtContent.Font = new Font("Consolas", 9F);
            txtContent.Location = new Point(0, 32);
            txtContent.Multiline = true;
            txtContent.Name = "txtContent";
            txtContent.ReadOnly = true;
            txtContent.ScrollBars = ScrollBars.Both;
            txtContent.Size = new Size(784, 454);
            txtContent.TabIndex = 0;
            txtContent.WordWrap = false;
            //
            // panelBottom
            //
            panelBottom.Controls.Add(btnCopy);
            panelBottom.Controls.Add(btnClose);
            panelBottom.Dock = DockStyle.Bottom;
            panelBottom.Location = new Point(0, 486);
            panelBottom.Name = "panelBottom";
            panelBottom.Size = new Size(784, 45);
            panelBottom.TabIndex = 1;
            //
            // btnCopy
            //
            btnCopy.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnCopy.Location = new Point(596, 10);
            btnCopy.Name = "btnCopy";
            btnCopy.Size = new Size(80, 25);
            btnCopy.TabIndex = 1;
            btnCopy.Text = "Copy";
            btnCopy.UseVisualStyleBackColor = true;
            btnCopy.Click += btnCopy_Click;
            //
            // btnClose
            //
            btnClose.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnClose.Location = new Point(682, 10);
            btnClose.Name = "btnClose";
            btnClose.Size = new Size(90, 25);
            btnClose.TabIndex = 0;
            btnClose.Text = "Close";
            btnClose.UseVisualStyleBackColor = true;
            btnClose.Click += btnClose_Click;
            //
            // panelSearch
            //
            panelSearch.Controls.Add(lblSearchCount);
            panelSearch.Controls.Add(btnFindPrev);
            panelSearch.Controls.Add(btnFindNext);
            panelSearch.Controls.Add(txtSearch);
            panelSearch.Dock = DockStyle.Top;
            panelSearch.Location = new Point(0, 0);
            panelSearch.Name = "panelSearch";
            panelSearch.Size = new Size(784, 32);
            panelSearch.TabIndex = 2;
            //
            // lblSearchCount
            //
            lblSearchCount.AutoSize = true;
            lblSearchCount.Location = new Point(350, 8);
            lblSearchCount.Name = "lblSearchCount";
            lblSearchCount.Size = new Size(0, 15);
            lblSearchCount.TabIndex = 3;
            //
            // btnFindPrev
            //
            btnFindPrev.Location = new Point(260, 4);
            btnFindPrev.Name = "btnFindPrev";
            btnFindPrev.Size = new Size(40, 24);
            btnFindPrev.TabIndex = 2;
            btnFindPrev.Text = "▲";
            btnFindPrev.UseVisualStyleBackColor = true;
            btnFindPrev.Click += btnFindPrev_Click;
            //
            // btnFindNext
            //
            btnFindNext.Location = new Point(304, 4);
            btnFindNext.Name = "btnFindNext";
            btnFindNext.Size = new Size(40, 24);
            btnFindNext.TabIndex = 1;
            btnFindNext.Text = "▼";
            btnFindNext.UseVisualStyleBackColor = true;
            btnFindNext.Click += btnFindNext_Click;
            //
            // txtSearch
            //
            txtSearch.Location = new Point(8, 5);
            txtSearch.Name = "txtSearch";
            txtSearch.PlaceholderText = "Search... (Ctrl+F)";
            txtSearch.Size = new Size(246, 23);
            txtSearch.TabIndex = 0;
            txtSearch.TextChanged += txtSearch_TextChanged;
            txtSearch.KeyDown += txtSearch_KeyDown;
            //
            // PreviewForm
            //
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(784, 531);
            Controls.Add(txtContent);
            Controls.Add(panelSearch);
            Controls.Add(panelBottom);
            KeyPreview = true;
            MinimizeBox = false;
            Name = "PreviewForm";
            StartPosition = FormStartPosition.CenterParent;
            KeyDown += PreviewForm_KeyDown;
            panelBottom.ResumeLayout(false);
            panelSearch.ResumeLayout(false);
            panelSearch.PerformLayout();
            ResumeLayout(false);
            PerformLayout();
        }
    }
}
