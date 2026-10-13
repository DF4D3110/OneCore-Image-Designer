using MobilePackageGen;

namespace MobilePackageGen.GUI
{
    public partial class MainForm : Form
    {
        private List<PackageInfo> allPackages = new();
        private List<FMFileInfo> allFMFiles = new();
        private IEnumerable<IDisk>? loadedDisks;
        private UpdateHistory.UpdateHistory? updateHistory;
        private ToolStripDropDownButton languageBtn = new();
        private ToolStripDropDownButton helpBtn = new();

        public MainForm()
        {
            Lang.Init();
            InitializeComponent();
            SetupMenu();
            ApplyLanguage();
            txtOutput.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "MobilePackageGen_Output");
            treePackages.NodeMouseDoubleClick += treePackages_NodeMouseDoubleClick;
        }

        private void SetupMenu()
        {
            languageBtn = new ToolStripDropDownButton();
            helpBtn = new ToolStripDropDownButton();
            languageBtn.DisplayStyle = ToolStripItemDisplayStyle.Text;
            helpBtn.DisplayStyle = ToolStripItemDisplayStyle.Text;

            foreach (var (code, name) in Lang.SupportedLanguages)
            {
                var item = new ToolStripMenuItem(name);
                item.Tag = code;
                item.Click += LangItem_Click;
                languageBtn.DropDownItems.Add(item);
            }

            var usageItem = new ToolStripMenuItem();
            usageItem.Click += (s, e) => ShowHelp();
            helpBtn.DropDownItems.Add(usageItem);

            var aboutItem = new ToolStripMenuItem();
            aboutItem.Click += (s, e) => ShowAbout();
            helpBtn.DropDownItems.Add(aboutItem);

            toolStrip1.Items.Add(new ToolStripSeparator());
            toolStrip1.Items.Add(languageBtn);
            toolStrip1.Items.Add(helpBtn);
        }

        private void LangItem_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem item && item.Tag is string code)
            {
                Lang.SetLanguage(code);
                ApplyLanguage();
            }
        }

        private void ApplyLanguage()
        {
            this.Text = Lang.GetString("FormTitle");
            btnOpen.Text = Lang.GetString("BtnOpen");
            btnSelectAll.Text = Lang.GetString("BtnSelectAll");
            btnDeselectAll.Text = Lang.GetString("BtnDeselectAll");
            lblOutput.Text = Lang.GetString("LblOutput");
            btnOutput.Text = Lang.GetString("BtnBrowse");
            btnBuild.Text = Lang.GetString("BtnBuild");
            languageBtn.Text = Lang.GetString("MenuLanguage");
            helpBtn.Text = Lang.GetString("MenuHelp");
            if (helpBtn.DropDownItems.Count > 0)
                helpBtn.DropDownItems[0].Text = Lang.GetString("MenuUsage");
            if (helpBtn.DropDownItems.Count > 1)
                helpBtn.DropDownItems[1].Text = Lang.GetString("MenuAbout");
            lblDetails.Text = Lang.GetString("DetailsDefault");
            lblStatus.Text = Lang.GetString("StatusReady");

            cmbFilter.BeginUpdate();
            cmbFilter.Items.Clear();
            cmbFilter.Items.Add(Lang.GetString("FilterAll"));
            cmbFilter.Items.Add("CBS");
            cmbFilter.Items.Add("SPKG");
            cmbFilter.Items.Add("Driver");
            cmbFilter.Items.Add("FM");
            cmbFilter.SelectedIndex = 0;
            cmbFilter.EndUpdate();

            if (allPackages.Count > 0)
            {
                PopulateTree();
                UpdateSelectionCount();
            }
        }

        private void ShowHelp()
        {
            MessageBox.Show(Lang.GetString("HelpContent"), Lang.GetString("HelpTitle"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowAbout()
        {
            using var dlg = new Form
            {
                Text = Lang.GetString("AboutTitle"),
                Size = new System.Drawing.Size(480, 360),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                Font = new System.Drawing.Font("Microsoft YaHei UI", 9F)
            };
            var title = new Label { Text = "MobilePackageGen GUI", Font = new System.Drawing.Font("Microsoft YaHei UI", 14F, System.Drawing.FontStyle.Bold), AutoSize = true, Location = new System.Drawing.Point(20, 15) };
            var ver = new Label { Text = "OneCore Image Designer Preview", AutoSize = true, Location = new System.Drawing.Point(22, 45) };
            var desc = new Label { Text = "MobilePackageGen 图形界面\n从 WIM 镜像提取 CBS 功能包\n支持包选择、XML预览、CAB结构预览\n支持文件搜索和批量提取", AutoSize = false, Size = new System.Drawing.Size(420, 100), Location = new System.Drawing.Point(22, 80) };
            var info = new Label { Text = "组织: WinStory 2026\nhttps://wiki.win-story.cn\n编译者: DF4D3110", AutoSize = false, Size = new System.Drawing.Size(420, 80), Location = new System.Drawing.Point(22, 195) };
            var ok = new Button { Text = "确定", Size = new System.Drawing.Size(80, 28), Location = new System.Drawing.Point(180, 290), DialogResult = DialogResult.OK };
            dlg.Controls.AddRange(new Control[] { title, ver, desc, info, ok });
            dlg.AcceptButton = ok;
            dlg.ShowDialog(this);
        }

        private void btnOpen_Click(object? sender, EventArgs e)
        {
            using var dlg = new OpenFileDialog();
            dlg.Filter = Lang.GetString("ImageFilter");
            dlg.Title = Lang.GetString("OpenImageTitle");
            dlg.Multiselect = true;   // storage-pool images are often split across multiple VHDs
            if (dlg.ShowDialog() != DialogResult.OK) return;

            string[] paths = dlg.FileNames;
            if (paths.Length == 0) return;

            LoadImage(paths);
        }

        private void LoadImage(string[] paths)
        {
            string outputPath = txtOutput.Text;
            var ui = this;

            SetProgress(Lang.GetString("LoadingImage"), 10);

            Task.Run(() =>
            {
                try
                {
                    var disks = DiskLoader.LoadDisks(paths);
                    if (disks == null || !disks.Any())
                    {
                        ui.Invoke(() =>
                        {
                            MessageBox.Show(Lang.GetString("LoadFailed"), Lang.GetString("BuildErrorTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                            SetProgress(Lang.GetString("LoadFailed"), 0);
                        });
                        return;
                    }

                    ui.Invoke(() => SetProgress(Lang.GetString("EnumeratingPackages"), 30));

                    var uh = BuildMetadataHandler.GetUpdateHistory(disks);
                    ui.Invoke(() => SetProgress("Enumerating CBS/SPKG/Drivers...", 40));
                    var pkgs = PackageEnumerator.EnumerateAll(disks, outputPath);
                    ui.Invoke(() => SetProgress("Enumerating FM files...", 75));
                    var fms = EnumerateFMFiles(disks);

                    ui.Invoke(() =>
                    {
                        loadedDisks = disks;
                        updateHistory = uh;
                        allPackages = pkgs;
                        allFMFiles = fms;
                        SetProgress("Populating tree...", 85);
                        PopulateTree();
                        SetProgress(string.Format(Lang.GetString("PackagesFound"),
                            paths.Length == 1 ? Path.GetFileName(paths[0]) : $"{paths.Length} images",
                            allPackages.Count + allFMFiles.Count,
                            allPackages.Count(p => p.Type == PackageType.CBS),
                            allPackages.Count(p => p.Type == PackageType.SPKG),
                            allPackages.Count(p => p.Type == PackageType.Driver)) + $"  FM: {allFMFiles.Count}  Appx: {allPackages.Count(p => p.Type == PackageType.Appx)}", 100);
                    });
                }
                catch (Exception ex)
                {
                    var detail = ex.Message;
                    var inner = ex.InnerException;
                    int depth = 0;
                    while (inner != null && depth < 5)
                    {
                        detail += $"\n\n[Inner {depth + 1}] {inner.GetType().Name}: {inner.Message}";
                        inner = inner.InnerException;
                        depth++;
                    }
                    detail += $"\n\nStackTrace:\n{ex.StackTrace}";
                    ui.Invoke(() =>
                    {
                        MessageBox.Show($"{Lang.GetString("ErrorLoading")}\n{detail}", Lang.GetString("BuildErrorTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                        SetProgress("Error: " + ex.Message, 0);
                    });
                }
            });
        }

        private void SetProgress(string status, int value)
        {
            lblStatus.Text = status;
            progressBar.Value = Math.Max(0, Math.Min(100, value));
            Application.DoEvents();
        }

        private List<FMFileInfo> EnumerateFMFiles(IEnumerable<IDisk> disks)
        {
            var result = new List<FMFileInfo>();
            var fmDirs = new[] { "Microsoft", "OEM" };
            var basePath = @"Windows\ImageUpdate\FeatureManifest";

            foreach (var disk in disks)
            {
                foreach (var partition in disk.Partitions)
                {
                    var fs = partition.FileSystem;
                    if (fs == null) continue;

                    foreach (var vendor in fmDirs)
                    {
                        var dirPath = Path.Combine(basePath, vendor);
                        try
                        {
                            if (!fs.DirectoryExists(dirPath)) continue;
                            var files = fs.GetFiles(dirPath, "*.xml", SearchOption.TopDirectoryOnly);
                            foreach (var file in files)
                            {
                                var name = Path.GetFileName(file);
                                var fm = new FMFileInfo
                                {
                                    Name = name,
                                    SourcePath = file,
                                    Vendor = vendor,
                                    PartitionName = partition.Name ?? "Unknown",
                                    DestinationPath = Path.Combine("FMFiles", vendor, name)
                                };
                                try { fm.Size = fs.GetFileInfo(file).Length; } catch { }
                                result.Add(fm);
                            }
                        }
                        catch { }
                    }
                }
            }
            return result;
        }

        private void PopulateTree()
        {
            treePackages.BeginUpdate();
            treePackages.Nodes.Clear();

            var filtered = GetFilteredPackages();

            var byType = filtered.GroupBy(p => p.Type);
            foreach (var group in byType)
            {
                var typeNode = treePackages.Nodes.Add(group.Key.ToString(), $"{group.Key} ({group.Count()})");
                typeNode.Tag = "type";

                var byPartition = group.GroupBy(p => p.PartitionName);
                foreach (var partGroup in byPartition)
                {
                    var partNode = typeNode.Nodes.Add(partGroup.Key ?? "Unknown", $"{partGroup.Key} ({partGroup.Count()})");
                    partNode.Tag = "partition";

                    foreach (var pkg in partGroup.OrderBy(p => p.Name))
                    {
                        var pkgNode = partNode.Nodes.Add(pkg.Name);
                        pkgNode.Tag = pkg;
                        pkgNode.Checked = pkg.Selected;
                    }
                }
            }

            if (allFMFiles.Count > 0 && (cmbFilter.SelectedIndex == 0 || cmbFilter.SelectedItem?.ToString() == "FM"))
            {
                var fmByVendor = allFMFiles.GroupBy(f => f.Vendor);
                foreach (var vg in fmByVendor)
                {
                    var fmNode = treePackages.Nodes.Add($"FM_{vg.Key}", $"FM Files ({vg.Key}) ({vg.Count()})");
                    fmNode.Tag = "fmtype";
                    foreach (var fm in vg.OrderBy(f => f.Name))
                    {
                        var fmFileNode = fmNode.Nodes.Add(fm.Name);
                        fmFileNode.Tag = fm;
                        fmFileNode.Checked = fm.Selected;
                    }
                }
            }

            treePackages.ExpandAll();
            treePackages.EndUpdate();
        }

        private List<PackageInfo> GetFilteredPackages()
        {
            var result = allPackages.AsEnumerable();

            if (cmbFilter.SelectedIndex > 0)
            {
                var filterText = cmbFilter.SelectedItem!.ToString()!;
                if (filterText == "FM")
                {
                    return new List<PackageInfo>();
                }
                if (Enum.TryParse<PackageType>(filterText, out var filterType))
                {
                    result = result.Where(p => p.Type == filterType);
                }
            }

            if (!string.IsNullOrWhiteSpace(txtSearch.Text))
            {
                var search = txtSearch.Text.ToLowerInvariant();
                result = result.Where(p =>
                    p.Name.ToLowerInvariant().Contains(search) ||
                    p.PartitionName.ToLowerInvariant().Contains(search) ||
                    p.Architecture.ToLowerInvariant().Contains(search));
            }

            return result.ToList();
        }

        private void treePackages_AfterCheck(object? sender, TreeViewEventArgs e)
        {
            if (e.Node.Tag is PackageInfo pkg)
            {
                pkg.Selected = e.Node.Checked;
            }
            else if (e.Node.Tag is FMFileInfo fm)
            {
                fm.Selected = e.Node.Checked;
            }
            else if (e.Node.Tag is string tag && (tag == "type" || tag == "partition" || tag == "fmtype"))
            {
                foreach (TreeNode child in e.Node.Nodes)
                {
                    child.Checked = e.Node.Checked;
                }
            }
            UpdateSelectionCount();
        }

        private void treePackages_AfterSelect(object? sender, TreeViewEventArgs e)
        {
            if (e.Node.Tag is PackageInfo pkg)
            {
                lblDetails.Text = $"{Lang.GetString("Package")}: {pkg.Name}\n\n" +
                    $"{Lang.GetString("Type")}: {pkg.TypeDisplay}\n" +
                    $"{Lang.GetString("Partition")}: {pkg.PartitionName}\n" +
                    $"{Lang.GetString("Architecture")}: {pkg.Architecture}\n" +
                    $"{Lang.GetString("Version")}: {pkg.Version}\n" +
                    $"{Lang.GetString("PublicKeyToken")}: {pkg.PublicKeyToken}\n" +
                    $"{Lang.GetString("Language")}: {pkg.Language}\n" +
                    $"{Lang.GetString("OutputPath")}: {pkg.CabFilePath}\n" +
                    $"{Lang.GetString("Source")}: {pkg.SourceManifest}\n" +
                    $"\n{Lang.GetString("Description")}: {pkg.Description}";
            }
            else if (e.Node.Tag is FMFileInfo fm)
            {
                lblDetails.Text = $"FM File: {fm.Name}\n\n" +
                    $"Vendor: {fm.Vendor}\n" +
                    $"Partition: {fm.PartitionName}\n" +
                    $"Size: {fm.Size:N0} bytes\n" +
                    $"Source: {fm.SourcePath}\n" +
                    $"Destination: {fm.DestinationPath}";
            }
        }

        private void UpdateSelectionCount()
        {
            int selected = allPackages.Count(p => p.Selected) + allFMFiles.Count(f => f.Selected);
            int total = allPackages.Count + allFMFiles.Count;
            lblStatus.Text = string.Format(Lang.GetString("PackagesSelected"), total, selected);
        }

        private void btnSelectAll_Click(object? sender, EventArgs e)
        {
            SetAllCheckState(true);
        }

        private void btnDeselectAll_Click(object? sender, EventArgs e)
        {
            SetAllCheckState(false);
        }

        private void SetAllCheckState(bool check)
        {
            treePackages.BeginUpdate();
            foreach (TreeNode node in treePackages.Nodes)
            {
                SetNodeCheckState(node, check);
            }
            treePackages.EndUpdate();
            foreach (var pkg in allPackages) pkg.Selected = check;
            foreach (var fm in allFMFiles) fm.Selected = check;
            UpdateSelectionCount();
        }

        private void SetNodeCheckState(TreeNode node, bool check)
        {
            node.Checked = check;
            foreach (TreeNode child in node.Nodes)
            {
                SetNodeCheckState(child, check);
            }
        }

        private void cmbFilter_SelectedIndexChanged(object? sender, EventArgs e)
        {
            PopulateTree();
        }

        private void txtSearch_TextChanged(object? sender, EventArgs e)
        {
            PopulateTree();
        }

        private void btnOutput_Click(object? sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog();
            dlg.Description = Lang.GetString("OutputFolderTitle");
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                txtOutput.Text = dlg.SelectedPath;
            }
        }

        private void btnBuild_Click(object? sender, EventArgs e)
        {
            if (loadedDisks == null)
            {
                MessageBox.Show(Lang.GetString("NoImageLoaded"), Lang.GetString("WarningTitle"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var selected = allPackages.Where(p => p.Selected).ToList();
            var selectedFM = allFMFiles.Where(f => f.Selected).ToList();
            if (selected.Count == 0 && selectedFM.Count == 0)
            {
                MessageBox.Show(Lang.GetString("NoPackagesSelected"), Lang.GetString("WarningTitle"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!Directory.Exists(txtOutput.Text))
            {
                Directory.CreateDirectory(txtOutput.Text);
            }

            var result = MessageBox.Show(
                string.Format(Lang.GetString("ConfirmBuildMsg"), selected.Count + selectedFM.Count,
                    selected.Count(p => p.Type == PackageType.CBS),
                    selected.Count(p => p.Type == PackageType.SPKG),
                    selected.Count(p => p.Type == PackageType.Driver),
                    txtOutput.Text) + (selectedFM.Count > 0 ? $"\nFM Files: {selectedFM.Count}" : "") +
                    $"\nAppx: {selected.Count(p => p.Type == PackageType.Appx)}",
                Lang.GetString("ConfirmBuildTitle"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (result != DialogResult.Yes) return;

            BuildPackages(selected);
        }

        private void BuildPackages(List<PackageInfo> selected)
        {
            var ui = this;
            var disks = loadedDisks;
            string outputPath = txtOutput.Text;
            bool uncompressed = chkUncompressed.Checked;

            btnBuild.Enabled = false;
            btnOpen.Enabled = false;
            SetProgress(Lang.GetString("BuildingCBS"), 0);

            // Route a copy of all logs into the build output directory.
            Logging.SetLogFile(Path.Combine(outputPath, "build.log"));

            var cbsNames = new HashSet<string>(selected.Where(p => p.Type == PackageType.CBS).Select(p => Path.GetFileName(p.CabFileName)));
            var spkgNames = new HashSet<string>(selected.Where(p => p.Type == PackageType.SPKG).Select(p => Path.GetFileName(p.CabFileName)));
            var driverNames = new HashSet<string>(selected.Where(p => p.Type == PackageType.Driver).Select(p => Path.GetFileName(p.CabFileName)));
            var appxNames = new HashSet<string>(selected.Where(p => p.Type == PackageType.Appx)
                .Select(p => p.Name.Split('_')[0] + "_" + p.Name.Split('_')[^1]));

            Task.Run(() =>
            {
                try
                {
                    ui.Invoke(() => SetProgress(Lang.GetString("BuildingCBS"), 10));
                    if (cbsNames.Count > 0)
                    {
                        CBSBuilder.UseUncompressedManifests = uncompressed;
                        CBSBuilder.BuildCBS(disks!, outputPath, updateHistory, cbsNames);
                    }

                    ui.Invoke(() => SetProgress(Lang.GetString("BuildingSPKG"), 40));
                    if (spkgNames.Count > 0)
                        SPKGBuilder.BuildSPKG(disks!, outputPath, updateHistory, spkgNames);

                    ui.Invoke(() => SetProgress(Lang.GetString("BuildingDrivers"), 70));
                    if (driverNames.Count > 0)
                        DriverBuilder.BuildDrivers(disks!, outputPath, updateHistory, driverNames);

                    if (appxNames.Count > 0)
                    {
                        ui.Invoke(() => SetProgress("Extracting Appx Packages...", 78));
                        int appIdx = 0;
                        foreach (var disk in disks!)
                        {
                            foreach (var partition in disk.Partitions)
                            {
                                var fs = partition.FileSystem;
                                if (fs == null) continue;
                                try
                                {
                                    if (fs.DirectoryExists("AppData") && fs.DirectoryExists("WindowsApps"))
                                    {
                                        AppBuilder.ExtractAppLicenses(fs, outputPath, new List<AppxPackage>(), appxNames);
                                        AppBuilder.ExtractAppPackages(fs, outputPath, appxNames.Count, ref appIdx, new List<AppxPackage>(), appxNames);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logging.Log($"App extraction error: {ex.Message}", LoggingLevel.Error);
                                }
                            }
                        }
                    }

                    var selectedFM = allFMFiles.Where(f => f.Selected).ToList();
                    if (selectedFM.Count > 0)
                    {
                        ui.Invoke(() => SetProgress("Extracting FM Files...", 85));
                        ExtractFMFiles(selectedFM, outputPath);
                    }

                    ui.Invoke(() =>
                    {
                        SetProgress(string.Format(Lang.GetString("PackagesFound"), "", selected.Count + selectedFM.Count, 0, 0, 0), 100);
                        MessageBox.Show(string.Format(Lang.GetString("BuildCompleteMsg"), selected.Count + selectedFM.Count, outputPath),
                            Lang.GetString("BuildCompleteTitle"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    });
                }
                catch (Exception ex)
                {
                    ui.Invoke(() =>
                    {
                        SetProgress("Build error: " + ex.Message, 0);
                        MessageBox.Show($"Build error: {ex.Message}", Lang.GetString("BuildErrorTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                    });
                }
                finally
                {
                    ui.Invoke(() =>
                    {
                        btnBuild.Enabled = true;
                        btnOpen.Enabled = true;
                    });
                }
            });
        }

        private void ExtractFMFiles(List<FMFileInfo> fmFiles, string outputPath)
        {
            foreach (var fm in fmFiles)
            {
                try
                {
                    foreach (var disk in loadedDisks!)
                    {
                        bool found = false;
                        foreach (var partition in disk.Partitions)
                        {
                            var fs = partition.FileSystem;
                            if (fs == null) continue;
                            try
                            {
                                if (fs.FileExists(fm.SourcePath))
                                {
                                    var destPath = Path.Combine(outputPath, fm.DestinationPath);
                                    var destDir = Path.GetDirectoryName(destPath);
                                    if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                                        Directory.CreateDirectory(destDir);

                                    using var srcStream = fs.OpenFile(fm.SourcePath, FileMode.Open, FileAccess.Read);
                                    using var destStream = File.Create(destPath);
                                    srcStream.CopyTo(destStream);
                                    found = true;
                                    break;
                                }
                            }
                            catch { }
                        }
                        if (found) break;
                    }
                }
                catch { }
            }
        }
        private void btnPreview_Click(object? sender, EventArgs e)
        {
            PreviewSelectedNode();
        }

        private void treePackages_NodeMouseDoubleClick(object? sender, TreeNodeMouseClickEventArgs e)
        {
            PreviewSelectedNode();
        }

        private void PreviewSelectedNode()
        {
            if (treePackages.SelectedNode == null) return;
            var tag = treePackages.SelectedNode.Tag;

            if (tag is FMFileInfo fm)
            {
                PreviewFMFile(fm);
            }
            else if (tag is PackageInfo pkg)
            {
                PreviewPackageManifest(pkg);
            }
        }

        private void PreviewFMFile(FMFileInfo fm)
        {
            try
            {
                foreach (var disk in loadedDisks!)
                {
                    foreach (var partition in disk.Partitions)
                    {
                        var fs = partition.FileSystem;
                        if (fs == null) continue;
                        try
                        {
                            if (fs.FileExists(fm.SourcePath))
                            {
                                using var stream = fs.OpenFile(fm.SourcePath, FileMode.Open, FileAccess.Read);
                                using var reader = new StreamReader(stream);
                                var content = reader.ReadToEnd();
                                var formatted = FormatXml(content);
                                using var dlg = new PreviewForm($"FM: {fm.Name}", formatted);
                                dlg.ShowDialog(this);
                                return;
                            }
                        }
                        catch { }
                    }
                }
                MessageBox.Show("FM file not found in image.", "Preview", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Preview error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void PreviewPackageManifest(PackageInfo pkg)
        {
            try
            {
                var manifestPath = pkg.SourceManifest;
                if (string.IsNullOrEmpty(manifestPath))
                {
                    MessageBox.Show("No manifest source path available.", "Preview", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                foreach (var disk in loadedDisks!)
                {
                    foreach (var partition in disk.Partitions)
                    {
                        var fs = partition.FileSystem;
                        if (fs == null) continue;
                        try
                        {
                            if (fs.FileExists(manifestPath))
                            {
                                using var stream = fs.OpenFile(manifestPath, FileMode.Open, FileAccess.Read);
                                using var reader = new StreamReader(stream);
                                var content = reader.ReadToEnd();
                                var fileList = ExtractFileListFromManifest(content);
                                var preview = BuildPackagePreview(pkg, manifestPath, fileList);
                                using var dlg = new PreviewForm($"Package: {pkg.Name}", preview);
                                dlg.ShowDialog(this);
                                return;
                            }
                        }
                        catch { }
                    }
                }
                MessageBox.Show("Manifest not found in image.", "Preview", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Preview error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private string FormatXml(string xml)
        {
            try
            {
                var doc = new System.Xml.XmlDocument();
                doc.LoadXml(xml);
                using var sw = new StringWriter();
                using var writer = new System.Xml.XmlTextWriter(sw) { Formatting = System.Xml.Formatting.Indented, Indentation = 2 };
                doc.WriteTo(writer);
                writer.Flush();
                return sw.ToString();
            }
            catch
            {
                return xml;
            }
        }

        private string BuildPackagePreview(PackageInfo pkg, string manifestPath, List<string> fileList)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Package: {pkg.Name}");
            sb.AppendLine($"Type: {pkg.TypeDisplay}");
            sb.AppendLine($"Architecture: {pkg.Architecture}");
            sb.AppendLine($"Version: {pkg.Version}");
            sb.AppendLine($"Manifest: {manifestPath}");
            sb.AppendLine(new string('=', 70));
            sb.AppendLine();
            sb.AppendLine($"Total files: {fileList.Count}");
            sb.AppendLine();

            var executables = fileList.Where(f => HasExtension(f, ".exe", ".dll", ".sys", ".drv")).ToList();
            var manifests = fileList.Where(f => HasExtension(f, ".manifest", ".mum")).ToList();
            var catalogs = fileList.Where(f => HasExtension(f, ".cat")).ToList();
            var others = fileList.Where(f => !executables.Contains(f) && !manifests.Contains(f) && !catalogs.Contains(f)).ToList();

            if (executables.Count > 0)
            {
                sb.AppendLine($"--- Executables / Drivers ({executables.Count}) ---");
                foreach (var f in executables)
                    sb.AppendLine($"  {ExpandRuntimeMacro(f)}");
                sb.AppendLine();
            }

            if (manifests.Count > 0)
            {
                sb.AppendLine($"--- Manifests ({manifests.Count}) ---");
                foreach (var f in manifests)
                    sb.AppendLine($"  {Path.GetFileName(f)}");
                sb.AppendLine();
            }

            if (catalogs.Count > 0)
            {
                sb.AppendLine($"--- Catalogs ({catalogs.Count}) ---");
                foreach (var f in catalogs)
                    sb.AppendLine($"  {ExpandRuntimeMacro(f)}");
                sb.AppendLine();
            }

            if (others.Count > 0)
            {
                sb.AppendLine($"--- Other ({others.Count}) ---");
                foreach (var f in others)
                    sb.AppendLine($"  {ExpandRuntimeMacro(f)}");
                sb.AppendLine();
            }

            sb.AppendLine(new string('=', 70));
            sb.AppendLine("Raw paths (with macros):");
            sb.AppendLine();
            foreach (var f in fileList.Select((f, i) => new { f, i }))
                sb.AppendLine($"  {f.i + 1,4}. {f.f}");

            return sb.ToString();
        }

        private bool HasExtension(string path, params string[] exts)
        {
            var ext = Path.GetExtension(path);
            return exts.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
        }

        private string ExpandRuntimeMacro(string path)
        {
            return path.Replace("$(runtime.bootdrive)", "")
                       .Replace("$(runtime.systemroot)", "Windows")
                       .Replace("$(runtime.fonts)", @"Windows\Fonts")
                       .Replace("$(runtime.inf)", @"Windows\INF")
                       .Replace("$(runtime.system)", @"Windows\System")
                       .Replace("$(runtime.system32)", @"Windows\System32")
                       .Replace("$(runtime.wbem)", @"Windows\System32\wbem")
                       .Replace("$(runtime.drivers)", @"Windows\System32\drivers")
                       .Replace("$(runtime.programfiles)", "Program Files")
                       .Replace("$(runtime.programdata)", "ProgramData")
                       .Replace("$(runtime.startmenu)", @"ProgramData\Microsoft\Windows\Start Menu")
                       .TrimStart('\\');
        }

        private List<string> ExtractFileListFromManifest(string manifestXml)
        {
            var files = new List<string>();
            try
            {
                var doc = new System.Xml.XmlDocument();
                doc.LoadXml(manifestXml);

                foreach (System.Xml.XmlElement elem in doc.GetElementsByTagName("File"))
                {
                    var name = elem.GetAttribute("Name");
                    if (!string.IsNullOrEmpty(name) && !files.Contains(name))
                        files.Add(name);
                }

                if (files.Count == 0)
                {
                    var allElements = doc.SelectNodes("//*[@Name]");
                    if (allElements != null)
                    {
                        foreach (System.Xml.XmlElement elem in allElements)
                        {
                            var localName = elem.LocalName;
                            if (localName.Equals("File", StringComparison.OrdinalIgnoreCase) ||
                                localName.Equals("PayloadFile", StringComparison.OrdinalIgnoreCase))
                            {
                                var name = elem.GetAttribute("Name");
                                if (!string.IsNullOrEmpty(name) && !files.Contains(name))
                                    files.Add(name);
                            }
                        }
                    }
                }

                if (files.Count == 0)
                {
                    var matches = System.Text.RegularExpressions.Regex.Matches(manifestXml, @"Name\s*=\s*""([^""]+\.(?:dll|exe|sys|inf|cat|mui|manifest|xml|json|txt|bin|dat|wim|ppkg))""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    foreach (System.Text.RegularExpressions.Match m in matches)
                    {
                        var name = m.Groups[1].Value;
                        if (!files.Contains(name))
                            files.Add(name);
                    }
                }
            }
            catch { }
            return files.OrderBy(f => f).ToList();
        }
    }
}
