namespace MangosSuperUI_Extractor;

public partial class MainForm : Form
{
    private MpqManager? _mpqManager;
    private ExtractorEngine? _engine;
    private List<AssetCategory> _categories = new();
    private CancellationTokenSource? _cts;

    // Controls
    private TextBox txtDataPath = null!;
    private Button btnBrowse = null!;
    private Button btnScan = null!;
    private Label lblStatus = null!;
    private CheckedListBox chkCategories = null!;
    private Label lblCategoryInfo = null!;
    private TextBox txtOutputPath = null!;
    private Button btnBrowseOutput = null!;
    private Button btnExtractSelected = null!;
    private Button btnExtractAll = null!;
    private Button btnCancel = null!;
    private ProgressBar progressBar = null!;
    private RichTextBox txtLog = null!;

    public MainForm()
    {
        BuildUI();
    }

    private void BuildUI()
    {
        this.SuspendLayout();

        this.Text = "MangosSuperUI Extractor — WoW 1.12.1 MPQ Asset Extractor";
        this.Size = new Size(920, 750);
        this.MinimumSize = new Size(750, 600);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.Font = new Font("Segoe UI", 9F);
        this.BackColor = Color.FromArgb(30, 30, 30);
        this.ForeColor = Color.FromArgb(220, 220, 220);
        this.Icon = new Icon("favicon.ico");

        int y = 12, pad = 14, rEdge = 870;

        // ── Title ──
        AddLabel("MangosSuperUI Extractor", pad, y, 16F, FontStyle.Bold, Color.FromArgb(255, 180, 50));
        y += 32;
        AddLabel("Point at your WoW 1.12.1 Data folder → Scan → Extract", pad, y, 9F, FontStyle.Regular, Color.FromArgb(150, 150, 150));
        y += 28;

        // ── WoW Data folder ──
        AddLabel("WoW Client Data Folder:", pad, y);
        y += 20;

        txtDataPath = AddTextBox(pad, y, 670);
        txtDataPath.Text = GuessClientPath();

        btnBrowse = AddButton("Browse...", 695, y - 1, 80, OnBrowseData);
        btnScan = AddButton("Scan MPQs", 785, y - 1, 85, OnScan, Color.FromArgb(255, 180, 50), Color.FromArgb(30, 30, 30), true);
        y += 35;

        // ── Output folder ──
        AddLabel("Output Folder:", pad, y);
        y += 20;

        txtOutputPath = AddTextBox(pad, y, 670);
        txtOutputPath.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "MangosSuperUI_Extracted");

        btnBrowseOutput = AddButton("Browse...", 695, y - 1, 80, OnBrowseOutput);
        y += 38;

        // ── Categories ──
        AddLabel("Asset Categories:", pad, y, 9F, FontStyle.Bold);
        y += 20;

        chkCategories = new CheckedListBox
        {
            Location = new Point(pad, y),
            Size = new Size(430, 170),
            BackColor = Color.FromArgb(40, 40, 40),
            ForeColor = Color.FromArgb(220, 220, 220),
            BorderStyle = BorderStyle.FixedSingle,
            CheckOnClick = true,
            Font = new Font("Segoe UI", 9.5F)
        };
        chkCategories.SelectedIndexChanged += OnCategorySelected;
        this.Controls.Add(chkCategories);

        lblCategoryInfo = new Label
        {
            Location = new Point(455, y),
            Size = new Size(415, 170),
            BackColor = Color.FromArgb(38, 38, 38),
            ForeColor = Color.FromArgb(170, 170, 170),
            Padding = new Padding(10),
            Text = "Scan MPQs first to see available categories"
        };
        this.Controls.Add(lblCategoryInfo);
        y += 178;

        // ── Buttons ──
        btnExtractSelected = AddButton("Extract Selected", pad, y, 140, OnExtractSelected, Color.FromArgb(50, 120, 50), Color.White, true);
        btnExtractAll = AddButton("Extract All", 165, y, 110, OnExtractAll, Color.FromArgb(255, 180, 50), Color.FromArgb(30, 30, 30), true);
        btnCancel = AddButton("Cancel", 285, y, 80, OnCancel, Color.FromArgb(120, 40, 40), Color.White);
        btnExtractSelected.Enabled = false;
        btnExtractAll.Enabled = false;
        btnCancel.Enabled = false;

        lblStatus = new Label
        {
            Location = new Point(380, y + 7),
            AutoSize = true,
            ForeColor = Color.FromArgb(150, 150, 150),
            Text = "Ready"
        };
        this.Controls.Add(lblStatus);
        y += 40;

        // ── Progress ──
        progressBar = new ProgressBar
        {
            Location = new Point(pad, y),
            Size = new Size(rEdge - pad, 18),
            Style = ProgressBarStyle.Continuous
        };
        this.Controls.Add(progressBar);
        y += 26;

        // ── Log ──
        AddLabel("Log:", pad, y, 9F, FontStyle.Regular, Color.FromArgb(130, 130, 130));
        y += 18;

        txtLog = new RichTextBox
        {
            Location = new Point(pad, y),
            Size = new Size(rEdge - pad, 120),
            BackColor = Color.FromArgb(18, 18, 18),
            ForeColor = Color.FromArgb(0, 200, 100),
            Font = new Font("Cascadia Mono", 8.5F),
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            WordWrap = false
        };
        this.Controls.Add(txtLog);

        // ── Anchors for resize ──
        txtDataPath.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        btnBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnScan.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        txtOutputPath.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        btnBrowseOutput.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        lblCategoryInfo.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        progressBar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

        this.ResumeLayout(false);
    }

    // ── Event handlers ──

    private void OnBrowseData(object? s, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog { Description = "Select WoW 1.12.1 Data folder (contains .MPQ files)", UseDescriptionForTitle = true };
        if (dlg.ShowDialog() == DialogResult.OK) txtDataPath.Text = dlg.SelectedPath;
    }

    private void OnBrowseOutput(object? s, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog { Description = "Select output folder", UseDescriptionForTitle = true };
        if (dlg.ShowDialog() == DialogResult.OK) txtOutputPath.Text = dlg.SelectedPath;
    }

    private void OnScan(object? s, EventArgs e)
    {
        var dataPath = txtDataPath.Text.Trim();

        // Handle pointing at WoW root vs Data subfolder
        if (!Directory.Exists(dataPath))
        {
            ShowError("Folder not found.");
            return;
        }

        // If they pointed at the WoW root, look for Data subfolder
        if (!Directory.GetFiles(dataPath, "*.MPQ", SearchOption.TopDirectoryOnly).Any() &&
            !Directory.GetFiles(dataPath, "*.mpq", SearchOption.TopDirectoryOnly).Any())
        {
            var sub = Path.Combine(dataPath, "Data");
            if (Directory.Exists(sub))
            {
                dataPath = sub;
                txtDataPath.Text = dataPath;
            }
            else
            {
                ShowError("No .MPQ files found. Point at the WoW client's Data folder.");
                return;
            }
        }

        // Clean up previous
        _mpqManager?.Dispose();
        _mpqManager = new MpqManager();
        _mpqManager.Log += msg => Invoke(() => AppendLog(msg));

        lblStatus.Text = "Opening MPQ archives...";
        Application.DoEvents();

        try
        {
            var count = _mpqManager.OpenClientFolder(dataPath);
            if (count == 0)
            {
                ShowError("No MPQ archives could be opened.");
                return;
            }

            AppendLog($"Total files across all MPQs: {_mpqManager.TotalFileCount:N0}");

            // Create engine and load TRS
            _engine = new ExtractorEngine(_mpqManager);
            _engine.LogMessage += msg => Invoke(() => AppendLog(msg));
            _engine.LoadTrs();

            // Scan categories
            lblStatus.Text = "Scanning asset categories...";
            Application.DoEvents();

            _categories = _engine.ScanCategories();

            // Populate checklist
            chkCategories.Items.Clear();
            foreach (var cat in _categories)
            {
                chkCategories.Items.Add($"{cat.Name}  ({cat.FileCount:N0} files)", true);
            }

            btnExtractSelected.Enabled = _categories.Count > 0;
            btnExtractAll.Enabled = _categories.Count > 0;

            var total = _categories.Sum(c => c.FileCount);
            lblStatus.Text = $"Scan complete — {_categories.Count} categories, {total:N0} total files";
            AppendLog(lblStatus.Text);
        }
        catch (Exception ex)
        {
            ShowError($"Scan failed: {ex.Message}");
            AppendLog($"ERROR: {ex}");
        }
    }

    private void OnCategorySelected(object? s, EventArgs e)
    {
        var idx = chkCategories.SelectedIndex;
        if (idx < 0 || idx >= _categories.Count) return;

        var cat = _categories[idx];
        var info = $"{cat.Name}\n" +
                   $"{"".PadRight(40, '─')}\n" +
                   $"{cat.Description}\n\n" +
                   $"Files: {cat.FileCount:N0}\n" +
                   $"Output: {cat.OutputFolder}/\n" +
                   $"BLP→PNG: {(cat.Files.Any(f => f.ConvertBlpToPng) ? "Yes" : "No")}";

        if (cat.RequiresTrsRename)
            info += "\nmd5 rename: Yes (via md5translate.trs)";

        if (cat.Name == "Minimap Tiles" && _engine != null)
        {
            var maps = _engine.GetMinimapMapNames(cat);
            info += "\n\nMaps:\n";
            foreach (var (name, count) in maps.Take(20))
                info += $"  {name}: {count} tiles\n";
            if (maps.Count > 20)
                info += $"  ...and {maps.Count - 20} more";
        }

        lblCategoryInfo.Text = info;
    }

    private async void OnExtractSelected(object? s, EventArgs e) => await RunExtraction(false);
    private async void OnExtractAll(object? s, EventArgs e) => await RunExtraction(true);
    private void OnCancel(object? s, EventArgs e)
    {
        _cts?.Cancel();
        lblStatus.Text = "Cancelling...";
    }

    private async Task RunExtraction(bool all)
    {
        if (_engine == null) return;

        var outputPath = txtOutputPath.Text.Trim();
        if (string.IsNullOrEmpty(outputPath))
        {
            ShowError("Set an output folder first.");
            return;
        }

        var selected = new List<AssetCategory>();
        if (all)
        {
            selected = _categories.ToList();
        }
        else
        {
            for (int i = 0; i < chkCategories.Items.Count; i++)
                if (chkCategories.GetItemChecked(i) && i < _categories.Count)
                    selected.Add(_categories[i]);
        }

        if (selected.Count == 0) { ShowError("No categories selected."); return; }

        // UI state
        SetExtracting(true);
        _cts = new CancellationTokenSource();

        _engine.ProgressChanged += p => Invoke(() =>
        {
            if (p.Total > 0)
            {
                progressBar.Maximum = p.Total;
                progressBar.Value = Math.Min(p.Current, p.Total);
            }
            lblStatus.Text = p.Status;
        });

        int totalFiles = 0, totalErrors = 0;

        try
        {
            Directory.CreateDirectory(outputPath);

            foreach (var cat in selected)
            {
                AppendLog($"Extracting: {cat.Name} ({cat.FileCount:N0} files)...");
                await _engine.ExtractCategoryAsync(cat, outputPath, _cts.Token);
                totalFiles += cat.FileCount;
            }

            var msg = $"Extraction complete — {totalFiles:N0} files to {outputPath}";
            AppendLog("═══ " + msg + " ═══");
            lblStatus.Text = msg;
            MessageBox.Show(msg, "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Cancelled by user.");
            lblStatus.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            ShowError(ex.Message);
        }
        finally
        {
            SetExtracting(false);
        }
    }

    // ── Helpers ──

    private void SetExtracting(bool extracting)
    {
        btnExtractSelected.Enabled = !extracting;
        btnExtractAll.Enabled = !extracting;
        btnScan.Enabled = !extracting;
        btnCancel.Enabled = extracting;
    }

    private void AppendLog(string text)
    {
        if (txtLog.IsDisposed) return;
        txtLog.AppendText(text + "\n");
        txtLog.ScrollToCaret();
    }

    private void ShowError(string msg) =>
        MessageBox.Show(msg, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

    private string GuessClientPath()
    {
        // Common locations for 1.12.1 client
        var guesses = new[]
        {
            @"C:\WoW1.12\Data",
            @"C:\World of Warcraft 1.12\Data",
            @"C:\Games\WoW1.12\Data",
            @"D:\WoW1.12\Data",
            @"D:\Games\WoW1.12\Data",
        };
        foreach (var g in guesses)
            if (Directory.Exists(g)) return g;
        return @"C:\WoW1.12\Data";
    }

    // ── Control factory helpers (reduce boilerplate) ──

    private Label AddLabel(string text, int x, int y, float size = 9F, FontStyle style = FontStyle.Regular, Color? color = null)
    {
        var lbl = new Label
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            Font = new Font("Segoe UI", size, style),
            ForeColor = color ?? Color.FromArgb(200, 200, 200)
        };
        this.Controls.Add(lbl);
        return lbl;
    }

    private TextBox AddTextBox(int x, int y, int width)
    {
        var txt = new TextBox
        {
            Location = new Point(x, y),
            Size = new Size(width, 25),
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.FromArgb(220, 220, 220),
            BorderStyle = BorderStyle.FixedSingle
        };
        this.Controls.Add(txt);
        return txt;
    }

    private void InitializeComponent()
    {

    }

    private Button AddButton(string text, int x, int y, int width, EventHandler onClick,
        Color? bg = null, Color? fg = null, bool bold = false)
    {
        var btn = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(width, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = bg ?? Color.FromArgb(60, 60, 60),
            ForeColor = fg ?? Color.FromArgb(220, 220, 220),
            Font = bold ? new Font("Segoe UI", 9F, FontStyle.Bold) : new Font("Segoe UI", 9F)
        };
        btn.Click += onClick;
        this.Controls.Add(btn);
        return btn;
    }
}
