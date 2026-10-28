using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using CalcStudioEPM.Config;
using CalcStudioEPM.Epm;

namespace CalcStudioEPM.Forms
{
    /// <summary>
    /// Dialog for managing all EPM Cloud connection configurations.
    ///
    /// The form holds an in-memory list of connections that mirrors the central
    /// <c>connections.json</c> store.  Every [Save Connection] click upserts the
    /// current fields directly via <see cref="ConfigManager.UpsertConfig"/>, so
    /// nothing is lost if the form is closed without a final "OK".
    ///
    /// Buttons:
    ///   [New]        — blank slate for a brand-new connection
    ///   [Duplicate]  — like New, pre-filled from the selected connection
    ///   [Import…]    — from an export file, or from an existing project folder
    ///                  (pre-fills fields from the legacy config.dat when present)
    ///   [Export…]    — writes connections to a file; saved passwords only with a key
    ///   [Remove]     — removes the selected connection from the store (no files deleted)
    ///   [Test]       — tests the current fields without saving
    ///   [Save]       — upserts the current fields and marks that connection active
    ///   [Close]      — dismisses the dialog
    /// </summary>
    public class ConfigForm : PluginForm
    {
        // ------------------------------------------------------------------ //
        //  Controls
        // ------------------------------------------------------------------ //

        private ComboBox _cboConnections;
        private Button   _btnNew;
        private Button   _btnDuplicate;
        private Button   _btnImport;
        private Button   _btnExport;
        private Button   _btnRemove;

        private TextBox  _txtName;
        private TextBox  _txtServiceUrl;
        private TextBox  _txtApplication;
        private TextBox  _txtUsername;
        private TextBox  _txtPassword;
        private CheckBox _chkSavePassword;
        private TextBox  _txtLocalFolder;
        private ComboBox _cboDeployMethod;
        private CheckBox _chkPullMembers;
        private CheckBox _chkReadOnly;
        private CheckBox _chkPullIpm;
        private Button   _btnBrowse;

        private Label    _lblStatus;
        private Button   _btnTest;
        private Button   _btnSave;
        private Button   _btnClose;

        // ------------------------------------------------------------------ //
        //  State
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Suppresses <see cref="OnConnectionSelected"/> during batch ComboBox
        /// operations (Clear / Add) to avoid spurious field-population calls.
        /// </summary>
        private bool _suppressSelectionChange;

        /// <summary>Folder of the saved connection being edited; null for a new one. Its own
        /// folder doesn't conflict with itself, and changing it moves the connection.</summary>
        private string _editingFolder;

        // ------------------------------------------------------------------ //
        //  Constructor
        // ------------------------------------------------------------------ //

        public ConfigForm()
        {
            InitializeComponents();
            LoadConnections(selectFolderPath: ConfigManager.GetActive()?.LocalFolderPath);
        }

        // ------------------------------------------------------------------ //
        //  Layout
        // ------------------------------------------------------------------ //

        private void InitializeComponents()
        {
            // Pixel sizes below are for 100 % scaling; PluginForm scales them with the font.
            Text            = "CalcStudio — Connections";
            MaximizeBox     = false;
            StartPosition   = FormStartPosition.CenterParent;
            Size            = new Size(760, 544);
            AllowResize("Connections");

            var layout = new TableLayoutPanel
            {
                Dock        = DockStyle.Fill,
                ColumnCount = 2,
                RowCount    = 14,
                Padding     = new Padding(10),
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150f));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,  100f));
            // Rows
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f)); // 0  connection selector
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10f)); // 1  separator spacer
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f)); // 2  Name
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f)); // 3  Service URL
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f)); // 4  Application
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f)); // 5  Username
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f)); // 6  Password
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f)); // 7  Local Folder
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f)); // 8  Default Deploy Method
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f)); // 9  Validation metadata
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f)); // 10 Read only
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f)); // 11 IPM
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // 12 Status (takes extra height)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f)); // 13 Buttons

            // ── Row 0: connection selector ──────────────────────────────── //
            var connPanel = new FlowLayoutPanel
            {
                Dock          = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents  = false,
                Padding       = new Padding(0),
                Margin        = new Padding(0),
            };
            var connLabel = new Label
            {
                Text      = "Connection:",
                AutoSize  = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin    = new Padding(0, 7, 4, 0),
            };
            _cboConnections = new ComboBox
            {
                DropDownStyle    = ComboBoxStyle.DropDownList,
                FormattingEnabled = true,
                Width            = 200,
                Margin           = new Padding(0, 4, 6, 0),
            };
            _cboConnections.Format += FormatConnectionItem;
            _cboConnections.SelectedIndexChanged += OnConnectionSelected;

            _btnNew       = MakeSmallButton("New",       52, OnNewClick);
            _btnDuplicate = MakeSmallButton("Duplicate", 74, OnDuplicateClick);
            _btnImport    = MakeSmallButton("Import…",   70, OnImportClick);
            _btnExport    = MakeSmallButton("Export…",   70, OnExportClick);
            _btnRemove    = MakeSmallButton("Remove",    65, OnRemoveClick);
            _btnRemove.Enabled = _btnDuplicate.Enabled = false;

            connPanel.Controls.AddRange(new Control[]
                { connLabel, _cboConnections, _btnNew, _btnDuplicate, _btnImport, _btnExport, _btnRemove });
            layout.SetColumnSpan(connPanel, 2);
            layout.Controls.Add(connPanel, 0, 0);

            // ── Row 1: thin separator ───────────────────────────────────── //
            var sep = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0) };
            sep.Paint += (s, e) => e.Graphics.DrawLine(SystemPens.ControlDark,
                0, sep.Height - 1, sep.Width, sep.Height - 1);
            layout.SetColumnSpan(sep, 2);
            layout.Controls.Add(sep, 0, 1);

            // ── Row 2: Name ─────────────────────────────────────────────── //
            layout.Controls.Add(MakeLabel("Name:"), 0, 2);
            _txtName = new TextBox { Dock = DockStyle.Fill };
            SetCueBanner(_txtName, "e.g. Vision Production");
            layout.Controls.Add(_txtName, 1, 2);

            // ── Row 3: Service URL ──────────────────────────────────────── //
            layout.Controls.Add(MakeLabel("Service URL:"), 0, 3);
            _txtServiceUrl = new TextBox { Dock = DockStyle.Fill };
            SetCueBanner(_txtServiceUrl, "https://planning-xxx.pbcs.us2.oraclecloud.com");
            layout.Controls.Add(_txtServiceUrl, 1, 3);

            // ── Row 4: Application ──────────────────────────────────────── //
            layout.Controls.Add(MakeLabel("Application:"), 0, 4);
            _txtApplication = new TextBox { Dock = DockStyle.Fill };
            SetCueBanner(_txtApplication, "Vision");
            layout.Controls.Add(_txtApplication, 1, 4);

            // ── Row 5: Username ─────────────────────────────────────────── //
            layout.Controls.Add(MakeLabel("Username:"), 0, 5);
            _txtUsername = new TextBox { Dock = DockStyle.Fill };
            layout.Controls.Add(_txtUsername, 1, 5);

            // ── Row 6: Password ─────────────────────────────────────────── //
            layout.Controls.Add(MakeLabel("Password:"), 0, 6);
            // Password box only enabled when the password is saved on the connection;
            // otherwise the user is asked for it when an action needs it.
            var passwordPanel = FieldWithSide();
            _txtPassword = new TextBox
            {
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(0, 0, 8, 0),
                UseSystemPasswordChar = true,
            };
            _chkSavePassword = new CheckBox
            {
                Text     = "Save password",
                Anchor   = AnchorStyles.Left,
                AutoSize = true,
                Margin   = Padding.Empty,
                Checked  = true,
            };
            _chkSavePassword.CheckedChanged += (s, e) => UpdatePasswordEnabled();
            passwordPanel.Controls.Add(_txtPassword, 0, 0);
            passwordPanel.Controls.Add(_chkSavePassword, 1, 0);
            layout.Controls.Add(passwordPanel, 1, 6);

            // ── Row 7: Local Folder ─────────────────────────────────────── //
            layout.Controls.Add(MakeLabel("Local Folder:"), 0, 7);
            var folderPanel = FieldWithSide();
            _txtLocalFolder = new TextBox
            {
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(0, 0, 6, 0),
            };
            _btnBrowse = new Button
            {
                Text   = "Browse…",
                Anchor = AnchorStyles.Left,
                Size   = new Size(80, 24),
                Margin = Padding.Empty,
            };
            _btnBrowse.Click += OnBrowseClick;
            folderPanel.Controls.Add(_txtLocalFolder, 0, 0);
            folderPanel.Controls.Add(_btnBrowse, 1, 0);
            layout.Controls.Add(folderPanel, 1, 7);

            // ── Row 8: Default Deploy Method ────────────────────────────── //
            // Item order matches the DeployMethod enum (index = value).
            layout.Controls.Add(MakeLabel("Default Deploy Method:"), 0, 8);
            _cboDeployMethod = new ComboBox
            {
                Dock          = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            _cboDeployMethod.Items.AddRange(new object[]
            {
                DeployMethodNames.Long(DeployMethod.CmOnly),
                DeployMethodNames.Long(DeployMethod.CmAndHp),
            });
            _cboDeployMethod.SelectedIndex = (int)DeployMethod.CmAndHp;
            layout.Controls.Add(_cboDeployMethod, 1, 8);

            // ── Row 9: Validation metadata ──────────────────────────────── //
            layout.Controls.Add(MakeLabel("Validation:"), 0, 9);
            _chkPullMembers = new CheckBox
            {
                Text     = "Download dimensions and members on Pull to check member names",
                AutoSize = true,
                Anchor   = AnchorStyles.Left,
            };
            layout.Controls.Add(_chkPullMembers, 1, 9);

            // ── Row 10: Read only ───────────────────────────────────────── //
            layout.Controls.Add(MakeLabel("Read only:"), 0, 10);
            _chkReadOnly = new CheckBox
            {
                Text     = "Pulled scripts are read-only files, and Deploy is disabled (Pull and Execute still work)",
                AutoSize = true,
                Anchor   = AnchorStyles.Left,
            };
            layout.Controls.Add(_chkReadOnly, 1, 10);

            // ── Row 11: IPM ─────────────────────────────────────────────── //
            layout.Controls.Add(MakeLabel("IPM:"), 0, 11);
            _chkPullIpm = new CheckBox
            {
                Text     = "Also download IPM configuration (Auto Predict), calendars and events (offered on Pull)",
                AutoSize = true,
                Anchor   = AnchorStyles.Left,
            };
            layout.Controls.Add(_chkPullIpm, 1, 11);

            // ── Row 12: Status ──────────────────────────────────────────── //
            _lblStatus = new Label
            {
                Dock         = DockStyle.Fill,
                ForeColor    = Color.Gray,
                TextAlign    = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
            };
            layout.SetColumnSpan(_lblStatus, 2);
            layout.Controls.Add(_lblStatus, 0, 12);

            // ── Row 13: Buttons ──────────────────────────────────────────── //
            var btnPanel = new FlowLayoutPanel
            {
                Dock          = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                Padding       = new Padding(0),
            };
            _btnClose = new Button
            {
                Text         = "Close",
                DialogResult = DialogResult.Cancel,
                Width        = 75,
            };
            _btnSave = new Button { Text = "Save Connection", Width = 115 };
            _btnTest = new Button { Text = "Test Connection", Width = 115 };
            _btnSave.Click += OnSaveClick;
            _btnTest.Click += OnTestClick;
            btnPanel.Controls.AddRange(new Control[] { _btnClose, _btnSave, _btnTest });
            layout.SetColumnSpan(btnPanel, 2);
            layout.Controls.Add(btnPanel, 0, 13);

            Controls.Add(layout);
            CancelButton = _btnClose;
        }

        // ------------------------------------------------------------------ //
        //  Helpers — control factories
        // ------------------------------------------------------------------ //

        /// <summary>A field row: a stretching box on the left, a fixed control on the right.</summary>
        private static TableLayoutPanel FieldWithSide()
        {
            var p = new TableLayoutPanel
            {
                Dock        = DockStyle.Fill,
                ColumnCount = 2,
                RowCount    = 1,
                Margin      = new Padding(3, 3, 3, 0),
                Padding     = Padding.Empty,
            };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            p.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            return p;
        }

        private static Label MakeLabel(string text) =>
            new Label
            {
                Text      = text,
                Dock      = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
            };

        private static Button MakeSmallButton(string text, int width, EventHandler handler)
        {
            var btn = new Button
            {
                Text   = text,
                Width  = width,
                Height = 26,
                Margin = new Padding(0, 4, 2, 0),
            };
            btn.Click += handler;
            return btn;
        }

        // ------------------------------------------------------------------ //
        //  ComboBox display helper
        // ------------------------------------------------------------------ //

        private static void FormatConnectionItem(object sender, ListControlConvertEventArgs e)
        {
            if (e.ListItem is ConnectionConfig c)
                e.Value = string.IsNullOrWhiteSpace(c.Name)
                    ? ("(unnamed) " + (c.LocalFolderPath ?? string.Empty))
                    : c.Name;
        }

        // ------------------------------------------------------------------ //
        //  Connection list management
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Rebuilds the ComboBox from the store and optionally pre-selects an entry
        /// by its <paramref name="selectFolderPath"/>.  Falls back to index 0, or
        /// <see cref="EnterNewMode"/> when no connections exist.
        /// </summary>
        private void LoadConnections(string selectFolderPath = null)
        {
            _suppressSelectionChange = true;
            _cboConnections.Items.Clear();
            foreach (var c in ConfigManager.GetAll())
                _cboConnections.Items.Add(c);
            _suppressSelectionChange = false;
            _btnExport.Enabled = _cboConnections.Items.Count > 0;

            if (_cboConnections.Items.Count == 0)
            {
                EnterNewMode();
                return;
            }

            // Try to honour the requested selection
            if (!string.IsNullOrEmpty(selectFolderPath))
            {
                for (int i = 0; i < _cboConnections.Items.Count; i++)
                {
                    var c = (ConnectionConfig)_cboConnections.Items[i];
                    if (string.Equals(c.LocalFolderPath, selectFolderPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _cboConnections.SelectedIndex = i;
                        return;
                    }
                }
            }

            _cboConnections.SelectedIndex = 0;
        }

        private void EnterNewMode()
        {
            _suppressSelectionChange = true;
            _cboConnections.SelectedIndex = -1;
            _suppressSelectionChange = false;

            PopulateFields(null);
            _editingFolder = null;
            _btnRemove.Enabled = _btnDuplicate.Enabled = false;
            SetStatus(Color.Gray, string.Empty);
        }

        private void PopulateFields(ConnectionConfig cfg)
        {
            if (cfg == null)
            {
                _txtName.Text        = string.Empty;
                _txtServiceUrl.Text  = string.Empty;
                _txtApplication.Text = string.Empty;
                _txtUsername.Text    = string.Empty;
                _txtPassword.Text    = string.Empty;
                _chkSavePassword.Checked = true;
                _txtLocalFolder.Text = string.Empty;
                // New connections: deploy to Planning too, and check member names
                _cboDeployMethod.SelectedIndex = (int)DeployMethod.CmAndHp;
                _chkPullMembers.Checked = true;
                _chkReadOnly.Checked = false;
                _chkPullIpm.Checked = false;
                return;
            }

            _txtName.Text        = cfg.Name            ?? string.Empty;
            _txtServiceUrl.Text  = cfg.ServiceBaseUrl  ?? string.Empty;
            _txtApplication.Text = cfg.Application     ?? string.Empty;
            _txtUsername.Text    = cfg.Username         ?? string.Empty;
            _txtLocalFolder.Text = cfg.LocalFolderPath ?? string.Empty;
            _cboDeployMethod.SelectedIndex = (int)cfg.DefaultDeployMethod;
            _chkPullMembers.Checked = cfg.PullDimensionMembers;
            _chkReadOnly.Checked = cfg.ReadOnly;
            _chkPullIpm.Checked = cfg.PullIpm;

            _chkSavePassword.Checked = cfg.SavePassword;
            _txtPassword.Text = string.Empty;
            if (cfg.SavePassword && !string.IsNullOrEmpty(cfg.EncryptedPassword))
            {
                try { _txtPassword.Text = ConfigManager.DecryptPassword(cfg.EncryptedPassword); }
                catch { /* leave blank — user must re-enter */ }
            }
        }

        /// <summary>The password box only applies when the password is saved.</summary>
        private void UpdatePasswordEnabled()
        {
            _txtPassword.Enabled = _chkSavePassword.Checked;
        }

        private void SetStatus(Color color, string text)
        {
            _lblStatus.ForeColor = color;
            _lblStatus.Text      = text;
        }

        // ------------------------------------------------------------------ //
        //  Event handlers — connection toolbar
        // ------------------------------------------------------------------ //

        private void OnConnectionSelected(object sender, EventArgs e)
        {
            if (_suppressSelectionChange) return;

            var cfg = _cboConnections.SelectedItem as ConnectionConfig;
            PopulateFields(cfg);
            _editingFolder = cfg?.LocalFolderPath;
            _btnRemove.Enabled = _btnDuplicate.Enabled = (cfg != null);
            SetStatus(Color.Gray, string.Empty);
        }

        private void OnNewClick(object sender, EventArgs e)
        {
            EnterNewMode();
            _txtName.Focus();
        }

        /// <summary>Like New, pre-filled from the selected connection — except its local
        /// folder, which can't be shared with (or nested in) another connection's.</summary>
        private void OnDuplicateClick(object sender, EventArgs e)
        {
            var source = _cboConnections.SelectedItem as ConnectionConfig;
            if (source == null) return;
            EnterNewMode();
            PopulateFields(source);
            _txtName.Text        = (source.Name ?? string.Empty) + " (copy)";
            _txtLocalFolder.Text = string.Empty;
            SetStatus(Color.DarkOrange,
                "Copy of " + (string.IsNullOrWhiteSpace(source.Name) ? "the connection" : $"'{source.Name}'") +
                ": choose its own local folder (not inside or around another connection's), then click Save.");
            _txtName.Focus();
            _txtName.SelectAll();
        }

        private void OnImportClick(object sender, EventArgs e)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("From an exported connections file…", null, (s, a) => ImportFromFile());
            menu.Items.Add("From a project folder…",            null, (s, a) => ImportFromFolder());
            menu.Closed += (s, a) => BeginInvoke(new Action(menu.Dispose));
            menu.Show(_btnImport, new Point(0, _btnImport.Height));
        }

        private void ImportFromFile()
        {
            string path;
            using (var dlg = new OpenFileDialog
            {
                Title  = "Import connections",
                Filter = "CalcStudio connections (*.json)|*.json|All files (*.*)|*.*",
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                path = dlg.FileName;
            }

            ConnectionTransfer.ExportFile file;
            try { file = ConnectionTransfer.Read(path); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (file.Connections.Count == 0)
            {
                MessageBox.Show(this, "The file has no connections.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new ImportConnectionsForm(file, path))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                LoadConnections(dlg.ImportedFolder);
                SetStatus(Color.DarkGreen, $"Imported {dlg.ImportedCount} connection(s).");
            }
        }

        private void OnExportClick(object sender, EventArgs e)
        {
            var all = ConfigManager.GetAll();
            if (all.Count == 0) return;
            using (var dlg = new ExportConnectionsForm(all, _cboConnections.SelectedItem as ConnectionConfig))
                dlg.ShowDialog(this);
        }

        private void ImportFromFolder()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description         = "Select the project folder to import";
                dlg.ShowNewFolderButton = false;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string folder = dlg.SelectedPath;

                // If this folder is already in our list, just select it
                for (int i = 0; i < _cboConnections.Items.Count; i++)
                {
                    var existing = (ConnectionConfig)_cboConnections.Items[i];
                    if (string.Equals(existing.LocalFolderPath, folder,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _cboConnections.SelectedIndex = i;
                        SetStatus(Color.Gray,
                            "This folder is already configured — fields loaded from the saved connection.");
                        return;
                    }
                }

                // Try to read a legacy config.dat from the folder
                var imported = ConfigManager.TryImportFromFolder(folder);
                if (imported != null)
                {
                    // Pre-fill fields; user can edit before clicking Save
                    PopulateFields(imported);
                    _suppressSelectionChange = true;
                    _cboConnections.SelectedIndex = -1;
                    _suppressSelectionChange = false;
                    _editingFolder = null;
                    _btnRemove.Enabled = _btnDuplicate.Enabled = false;
                    SetStatus(Color.DarkGreen,
                        "Existing configuration imported from the selected folder. Review and click Save.");
                }
                else
                {
                    // No config.dat — create a blank entry for this folder
                    PopulateFields(null);
                    _txtLocalFolder.Text = folder;
                    if (string.IsNullOrWhiteSpace(_txtName.Text))
                        _txtName.Text = Path.GetFileName(folder.TrimEnd('\\', '/'));
                    _suppressSelectionChange = true;
                    _cboConnections.SelectedIndex = -1;
                    _suppressSelectionChange = false;
                    _editingFolder = null;
                    _btnRemove.Enabled = _btnDuplicate.Enabled = false;
                    SetStatus(Color.DarkOrange,
                        "No existing configuration found. Fill in the fields and click Save.");
                }
            }
        }

        private void OnRemoveClick(object sender, EventArgs e)
        {
            var cfg = _cboConnections.SelectedItem as ConnectionConfig;
            if (cfg == null) return;

            string displayName = string.IsNullOrWhiteSpace(cfg.Name)
                ? cfg.LocalFolderPath
                : $"'{cfg.Name}'";

            var answer = MessageBox.Show(
                $"Remove connection {displayName}?\n\n" +
                "This only removes it from the plugin's connection list — no files on disk are deleted.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;

            ConfigManager.DeleteConfig(cfg.LocalFolderPath);

            // Rebuild the list without the removed entry
            LoadConnections(
                ConfigManager.GetActive()?.LocalFolderPath);

            SetStatus(Color.Gray, $"Connection {displayName} removed.");
        }

        // ------------------------------------------------------------------ //
        //  Event handlers — browse / save / test
        // ------------------------------------------------------------------ //

        private void OnBrowseClick(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description         = "Select the local working folder for CalcStudio files";
                dlg.ShowNewFolderButton = true;
                if (!string.IsNullOrWhiteSpace(_txtLocalFolder.Text))
                    dlg.SelectedPath = _txtLocalFolder.Text;

                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _txtLocalFolder.Text = dlg.SelectedPath;
                string conflict = ConfigManager.FolderConflict(dlg.SelectedPath, _editingFolder);
                SetStatus(conflict == null ? Color.Gray : Color.Red, conflict ?? string.Empty);
            }
        }

        private void OnSaveClick(object sender, EventArgs e)
        {
            if (!ValidateFields()) return;

            var cfg = BuildConfig();
            string conflict = ConfigManager.FolderConflict(cfg.LocalFolderPath, _editingFolder);
            if (conflict != null)
            {
                SetStatus(Color.Red, conflict);
                MessageBox.Show(this, conflict + "\n\nChoose another local folder.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_editingFolder != null && !ConfigManager.SameFolder(_editingFolder, cfg.LocalFolderPath) &&
                MessageBox.Show(this,
                    $"Move this connection from\n{_editingFolder}\nto\n{cfg.LocalFolderPath}?\n\n" +
                    "Files in the old folder are not moved. (To keep both, use Duplicate instead.)",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            ConnectionConfig before = _editingFolder == null ? null
                : ConfigManager.GetAll().Find(c => ConfigManager.SameFolder(c.LocalFolderPath, _editingFolder));
            try
            {
                ConfigManager.UpsertConfig(cfg, replacingFolder: _editingFolder);
                string filesNote = "";
                if (cfg.ReadOnly != (before?.ReadOnly ?? false))
                {
                    int n = ReadOnlyScripts.Apply(ReadOnlyScripts.PulledDir(cfg), cfg.ReadOnly);
                    if (n > 0) filesNote = cfg.ReadOnly ? $" {n} pulled files marked read-only." : $" {n} pulled files made editable again.";
                }

                // Re-load the list and select the just-saved connection
                LoadConnections(cfg.LocalFolderPath);
                _btnRemove.Enabled = true;

                SetStatus(Color.DarkGreen, "Connection saved and set as active." + filesNote);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to save: " + ex.Message,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void OnTestClick(object sender, EventArgs e)
        {
            if (!ValidateFields(requireFolder: false)) return;

            _btnTest.Enabled = false;
            SetStatus(Color.Gray, "Testing connection…");

            var testCfg = BuildConfig();
            // Not saving the password: ask for it (this test only, or this session)
            if (!PasswordForm.Ensure(this, testCfg, allowSave: false))
            {
                _btnTest.Enabled = true;
                SetStatus(Color.Gray, string.Empty);
                return;
            }
            try
            {
                await Task.Run(async () =>
                {
                    using (var client = new EpmClient(testCfg))
                        await client.TestConnectionAsync().ConfigureAwait(false);
                });

                SetStatus(Color.DarkGreen, "Connection successful!");
            }
            catch (EpmException ex)
            {
                SetStatus(Color.Red, "Failed: " + ex.Message);
                MessageBox.Show(ex.Message, "Connection Failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                SetStatus(Color.Red, "Error: " + ex.Message);
                MessageBox.Show(ex.Message, "Connection Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _btnTest.Enabled = true;
                Credentials.EndAction();   // "this action only" = this test
            }
        }

        // ------------------------------------------------------------------ //
        //  Validation and config construction
        // ------------------------------------------------------------------ //

        private bool ValidateFields(bool requireFolder = true)
        {
            if (string.IsNullOrWhiteSpace(_txtName.Text))
            {
                MessageBox.Show("Name is required.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            string url = _txtServiceUrl.Text.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                MessageBox.Show("Service URL is required.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri parsed) ||
                !string.Equals(parsed.Scheme, Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    "Service URL must be a valid HTTPS address.\n\nExample:\n" +
                    "https://planning-xxx.pbcs.us2.oraclecloud.com",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (string.IsNullOrWhiteSpace(_txtApplication.Text))
            {
                MessageBox.Show("Application is required.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (string.IsNullOrWhiteSpace(_txtUsername.Text))
            {
                MessageBox.Show("Username is required.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (_chkSavePassword.Checked && string.IsNullOrWhiteSpace(_txtPassword.Text))
            {
                MessageBox.Show("Password is required.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (requireFolder && string.IsNullOrWhiteSpace(_txtLocalFolder.Text))
            {
                MessageBox.Show("Local Folder is required.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            return true;
        }

        private ConnectionConfig BuildConfig()
        {
            return new ConnectionConfig
            {
                Name              = _txtName.Text.Trim(),
                ServiceBaseUrl    = _txtServiceUrl.Text.Trim().TrimEnd('/'),
                Application       = _txtApplication.Text.Trim(),
                Username          = _txtUsername.Text.Trim(),
                SavePassword      = _chkSavePassword.Checked,
                EncryptedPassword = _chkSavePassword.Checked
                    ? ConfigManager.EncryptPassword(_txtPassword.Text)
                    : string.Empty,
                LocalFolderPath   = _txtLocalFolder.Text.Trim(),
                DefaultDeployMethod = (DeployMethod)Math.Max(0, _cboDeployMethod.SelectedIndex),
                PullDimensionMembers = _chkPullMembers.Checked,
                ReadOnly             = _chkReadOnly.Checked,
                PullIpm              = _chkPullIpm.Checked,
            };
        }

        // ------------------------------------------------------------------ //
        //  Win32 cue-banner (placeholder text) helper for .NET 4.8
        // ------------------------------------------------------------------ //

        private const uint EM_SETCUEBANNER = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

        private static void SetCueBanner(TextBox tb, string hint) =>
            SendMessage(tb.Handle, EM_SETCUEBANNER, new IntPtr(1), hint);
    }
}
