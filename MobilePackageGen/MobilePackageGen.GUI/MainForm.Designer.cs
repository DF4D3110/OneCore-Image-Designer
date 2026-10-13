namespace MobilePackageGen.GUI
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;

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
            this.components = new System.ComponentModel.Container();
            this.toolStrip1 = new ToolStrip();
            this.btnOpen = new ToolStripButton();
            this.toolStripSeparator1 = new ToolStripSeparator();
            this.btnSelectAll = new ToolStripButton();
            this.btnDeselectAll = new ToolStripButton();
            this.toolStripSeparator2 = new ToolStripSeparator();
            this.cmbFilter = new ToolStripComboBox();
            this.txtSearch = new ToolStripTextBox();
            this.btnPreview = new ToolStripButton();
            this.statusStrip1 = new StatusStrip();
            this.lblStatus = new ToolStripStatusLabel();
            this.panelProgress = new Panel();
            this.progressBar = new ProgressBar();
            this.splitContainer1 = new SplitContainer();
            this.treePackages = new TreeView();
            this.panelDetails = new Panel();
            this.lblDetails = new Label();
            this.panelBottom = new Panel();
            this.chkUncompressed = new CheckBox();
            this.btnBuild = new Button();
            this.btnOutput = new Button();
            this.txtOutput = new TextBox();
            this.lblOutput = new Label();
            this.imageList1 = new ImageList(this.components);
            this.toolStrip1.SuspendLayout();
            this.statusStrip1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer1)).BeginInit();
            this.splitContainer1.Panel1.SuspendLayout();
            this.splitContainer1.Panel2.SuspendLayout();
            this.splitContainer1.SuspendLayout();
            this.panelDetails.SuspendLayout();
            this.panelBottom.SuspendLayout();
            this.SuspendLayout();

            this.toolStrip1.ImageScalingSize = new Size(20, 20);
            this.toolStrip1.Items.AddRange(new ToolStripItem[] {
            this.btnOpen,
            this.toolStripSeparator1,
            this.btnSelectAll,
            this.btnDeselectAll,
            this.toolStripSeparator2,
            this.cmbFilter,
            this.txtSearch,
            this.btnPreview});
            this.toolStrip1.Location = new Point(0, 0);
            this.toolStrip1.Name = "toolStrip1";
            this.toolStrip1.Size = new Size(1200, 27);
            this.toolStrip1.TabIndex = 0;
            this.toolStrip1.Text = "toolStrip1";

            this.btnOpen.DisplayStyle = ToolStripItemDisplayStyle.Text;
            this.btnOpen.Name = "btnOpen";
            this.btnOpen.Size = new Size(100, 24);
            this.btnOpen.Text = "Open Image...";
            this.btnOpen.Click += new EventHandler(this.btnOpen_Click);

            this.toolStripSeparator1.Name = "toolStripSeparator1";
            this.toolStripSeparator1.Size = new Size(6, 27);

            this.btnSelectAll.DisplayStyle = ToolStripItemDisplayStyle.Text;
            this.btnSelectAll.Name = "btnSelectAll";
            this.btnSelectAll.Size = new Size(70, 24);
            this.btnSelectAll.Text = "Select All";
            this.btnSelectAll.Click += new EventHandler(this.btnSelectAll_Click);

            this.btnDeselectAll.DisplayStyle = ToolStripItemDisplayStyle.Text;
            this.btnDeselectAll.Name = "btnDeselectAll";
            this.btnDeselectAll.Size = new Size(80, 24);
            this.btnDeselectAll.Text = "Deselect All";
            this.btnDeselectAll.Click += new EventHandler(this.btnDeselectAll_Click);

            this.toolStripSeparator2.Name = "toolStripSeparator2";
            this.toolStripSeparator2.Size = new Size(6, 27);

            this.cmbFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            this.cmbFilter.Items.AddRange(new object[] { "All", "CBS", "SPKG", "Driver" });
            this.cmbFilter.Name = "cmbFilter";
            this.cmbFilter.Size = new Size(100, 28);
            this.cmbFilter.SelectedIndex = 0;
            this.cmbFilter.SelectedIndexChanged += new EventHandler(this.cmbFilter_SelectedIndexChanged);

            this.txtSearch.Name = "txtSearch";
            this.txtSearch.Size = new Size(200, 27);
            this.txtSearch.TextChanged += new EventHandler(this.txtSearch_TextChanged);

            this.btnPreview.DisplayStyle = ToolStripItemDisplayStyle.Text;
            this.btnPreview.Name = "btnPreview";
            this.btnPreview.Size = new Size(60, 28);
            this.btnPreview.Text = "Preview";
            this.btnPreview.Click += new EventHandler(this.btnPreview_Click);

            this.statusStrip1.Items.AddRange(new ToolStripItem[] {
            this.lblStatus});
            this.statusStrip1.Location = new Point(0, 698);
            this.statusStrip1.Name = "statusStrip1";
            this.statusStrip1.Size = new Size(1200, 22);
            this.statusStrip1.TabIndex = 1;
            this.statusStrip1.Text = "statusStrip1";

            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new Size(1000, 17);
            this.lblStatus.Text = "Ready. Open an image file to enumerate packages.";

            this.panelProgress.Dock = DockStyle.Bottom;
            this.panelProgress.Location = new Point(0, 673);
            this.panelProgress.Name = "panelProgress";
            this.panelProgress.Padding = new Padding(8, 3, 8, 3);
            this.panelProgress.Size = new Size(1200, 25);
            this.panelProgress.TabIndex = 4;

            this.progressBar.Dock = DockStyle.Fill;
            this.progressBar.Location = new Point(8, 3);
            this.progressBar.Maximum = 100;
            this.progressBar.Name = "progressBar";
            this.progressBar.Size = new Size(1184, 19);
            this.progressBar.TabIndex = 0;

            this.splitContainer1.Dock = DockStyle.Fill;
            this.splitContainer1.Location = new Point(0, 27);
            this.splitContainer1.Name = "splitContainer1";

            this.splitContainer1.Panel1.Controls.Add(this.treePackages);
            this.splitContainer1.Panel1MinSize = 300;

            this.splitContainer1.Panel2.Controls.Add(this.panelDetails);
            this.splitContainer1.Size = new Size(1200, 576);
            this.splitContainer1.SplitterDistance = 400;
            this.splitContainer1.TabIndex = 2;

            this.treePackages.CheckBoxes = true;
            this.treePackages.Dock = DockStyle.Fill;
            this.treePackages.Location = new Point(0, 0);
            this.treePackages.Name = "treePackages";
            this.treePackages.Size = new Size(400, 576);
            this.treePackages.TabIndex = 0;
            this.treePackages.AfterCheck += new TreeViewEventHandler(this.treePackages_AfterCheck);
            this.treePackages.AfterSelect += new TreeViewEventHandler(this.treePackages_AfterSelect);

            this.panelDetails.Controls.Add(this.lblDetails);
            this.panelDetails.Dock = DockStyle.Fill;
            this.panelDetails.Location = new Point(0, 0);
            this.panelDetails.Name = "panelDetails";
            this.panelDetails.Padding = new Padding(10);
            this.panelDetails.Size = new Size(796, 576);
            this.panelDetails.TabIndex = 0;

            this.lblDetails.Dock = DockStyle.Fill;
            this.lblDetails.Font = new Font("Consolas", 9F);
            this.lblDetails.Location = new Point(10, 10);
            this.lblDetails.Name = "lblDetails";
            this.lblDetails.Size = new Size(776, 556);
            this.lblDetails.TabIndex = 0;
            this.lblDetails.Text = "Select a package to view details.";

            this.panelBottom.Controls.Add(this.chkUncompressed);
            this.panelBottom.Controls.Add(this.btnBuild);
            this.panelBottom.Controls.Add(this.btnOutput);
            this.panelBottom.Controls.Add(this.txtOutput);
            this.panelBottom.Controls.Add(this.lblOutput);
            this.panelBottom.Dock = DockStyle.Bottom;
            this.panelBottom.Location = new Point(0, 603);
            this.panelBottom.Name = "panelBottom";
            this.panelBottom.Padding = new Padding(10);
            this.panelBottom.Size = new Size(1200, 70);
            this.panelBottom.TabIndex = 3;

            this.btnBuild.Anchor = ((AnchorStyles)((AnchorStyles.Top | AnchorStyles.Right)));
            this.btnBuild.Location = new Point(1080, 25);
            this.btnBuild.Name = "btnBuild";
            this.btnBuild.Size = new Size(100, 30);
            this.btnBuild.TabIndex = 3;
            this.btnBuild.Text = "Build Selected";
            this.btnBuild.UseVisualStyleBackColor = true;
            this.btnBuild.Click += new EventHandler(this.btnBuild_Click);

            this.btnOutput.Anchor = ((AnchorStyles)((AnchorStyles.Top | AnchorStyles.Right)));
            this.btnOutput.Location = new Point(970, 25);
            this.btnOutput.Name = "btnOutput";
            this.btnOutput.Size = new Size(100, 30);
            this.btnOutput.TabIndex = 2;
            this.btnOutput.Text = "Browse...";
            this.btnOutput.UseVisualStyleBackColor = true;
            this.btnOutput.Click += new EventHandler(this.btnOutput_Click);

            this.txtOutput.Anchor = ((AnchorStyles)(((AnchorStyles.Top | AnchorStyles.Left) | AnchorStyles.Right)));
            this.txtOutput.Location = new Point(100, 28);
            this.txtOutput.Name = "txtOutput";
            this.txtOutput.Size = new Size(860, 23);
            this.txtOutput.TabIndex = 1;

            this.lblOutput.AutoSize = true;
            this.lblOutput.Location = new Point(10, 31);
            this.lblOutput.Name = "lblOutput";
            this.lblOutput.Size = new Size(84, 15);
            this.lblOutput.TabIndex = 0;
            this.lblOutput.Text = "Output Folder:";

            this.chkUncompressed.AutoSize = true;
            this.chkUncompressed.Location = new Point(100, 6);
            this.chkUncompressed.Name = "chkUncompressed";
            this.chkUncompressed.Size = new Size(230, 19);
            this.chkUncompressed.TabIndex = 4;
            this.chkUncompressed.Text = "Decompress manifests (plain XML)";

            this.AutoScaleDimensions = new SizeF(7F, 15F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new Size(1200, 720);
            this.Controls.Add(this.splitContainer1);
            this.Controls.Add(this.panelBottom);
            this.Controls.Add(this.panelProgress);
            this.Controls.Add(this.statusStrip1);
            this.Controls.Add(this.toolStrip1);
            this.Name = "MainForm";
            this.Text = "MobilePackageGen GUI - Package Extractor";
            this.toolStrip1.ResumeLayout(false);
            this.toolStrip1.PerformLayout();
            this.statusStrip1.ResumeLayout(false);
            this.statusStrip1.PerformLayout();
            this.splitContainer1.Panel1.ResumeLayout(false);
            this.splitContainer1.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer1)).EndInit();
            this.splitContainer1.ResumeLayout(false);
            this.panelDetails.ResumeLayout(false);
            this.panelBottom.ResumeLayout(false);
            this.panelBottom.PerformLayout();
            this.panelProgress.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        private ToolStrip toolStrip1;
        private ToolStripButton btnOpen;
        private ToolStripSeparator toolStripSeparator1;
        private ToolStripButton btnSelectAll;
        private ToolStripButton btnDeselectAll;
        private ToolStripSeparator toolStripSeparator2;
        private ToolStripComboBox cmbFilter;
        private ToolStripTextBox txtSearch;
        private ToolStripButton btnPreview;
        private StatusStrip statusStrip1;
        private ToolStripStatusLabel lblStatus;
        private Panel panelProgress;
        private ProgressBar progressBar;
        private SplitContainer splitContainer1;
        private TreeView treePackages;
        private Panel panelDetails;
        private Label lblDetails;
        private Panel panelBottom;
        private CheckBox chkUncompressed;
        private Button btnBuild;
        private Button btnOutput;
        private TextBox txtOutput;
        private Label lblOutput;
        private ImageList imageList1;
    }
}
