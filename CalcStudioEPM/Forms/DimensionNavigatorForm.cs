using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using CalcStudioEPM.Config;
using CalcStudioEPM.Epm;

namespace CalcStudioEPM.Forms
{
    /// <summary>
    /// Browse the dimensions and members downloaded on Pull: pick a cube and a dimension,
    /// walk the hierarchy (loaded as nodes are expanded, so large dimensions stay fast),
    /// search names, aliases and member IDs, see a member's parent, path, children, shared
    /// positions and every field Planning sent for it (ID, storage …), and insert it at the
    /// cursor. Modeless: it stays open next to the script.
    /// </summary>
    public class DimensionNavigatorForm : PluginForm
    {
        private const string AllCubes = "(all cubes)";
        private const string AllDimensions = "(all dimensions)";
        private const int MaxResults = 1000;

        private readonly Action<string> _insert;
        private ConnectionConfig _cfg;
        private EpmMetadata _md;

        private ComboBox _cboCube, _cboDimension;
        private TextBox  _txtSearch, _txtDetails;
        private CheckBox _chkAliases, _chkQuotes, _chkDense;
        private TreeView _tree;
        private ListView _results;
        private Label    _lblStatus;
        private Button   _btnInsert, _btnCopy;
        private readonly Timer _searchDelay = new Timer { Interval = 300 };
        private bool _loading;
        private SplitContainer _split, _right;

        /// <param name="insert">Puts text at the cursor of the active document.</param>
        public DimensionNavigatorForm(ConnectionConfig cfg, Action<string> insert)
        {
            _insert = insert;
            Text          = "Dimension Navigator";
            StartPosition = FormStartPosition.CenterParent;
            ShowIcon      = false;
            MaximizeBox   = true;
            ClientSize    = new Size(900, 600);
            AllowResize("DimensionNavigator", minimum96: new Size(640, 420));
            BuildLayout();
            _searchDelay.Tick += (s, e) => { _searchDelay.Stop(); Search(); };
            ShowConnection(cfg);
        }

        // ------------------------------------------------------------------ //
        //  Layout
        // ------------------------------------------------------------------ //

        private void BuildLayout()
        {
            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 8, 8, 0), WrapContents = false,
            };
            Label L(string t) => new Label { Text = t, AutoSize = true, Margin = new Padding(0, 6, 4, 0) };
            _cboCube = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, Margin = new Padding(0, 2, 12, 0) };
            _cboDimension = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, Margin = new Padding(0, 2, 12, 0) };
            _txtSearch = new TextBox { Width = 220, Margin = new Padding(0, 3, 12, 0) };
            _chkAliases = new CheckBox { Text = "Show aliases", AutoSize = true, Checked = true, Margin = new Padding(0, 5, 0, 0) };
            // Dense / sparse as Planning reports it (read only); for dimensions downloaded before that
            // was kept, the user sets it here. FIX( autocomplete uses it.
            _chkDense = new CheckBox { Text = "Dense", AutoSize = true, Visible = false, Margin = new Padding(0, 5, 12, 0) };
            top.Controls.AddRange(new Control[] { L("Cube:"), _cboCube, L("Dimension:"), _cboDimension, _chkDense, L("Search:"), _txtSearch, _chkAliases });
            _chkDense.CheckedChanged += (s, e) =>
            {
                if (_loading || !_chkDense.Visible) return;
                try { CubeDensity.SetDense(_cfg.LocalFolderPath, _md, (string)_cboCube.SelectedItem, (string)_cboDimension.SelectedItem, _chkDense.Checked); }
                catch (Exception ex) { _lblStatus.Text = "Could not save dense / sparse: " + ex.Message; }
                ShowDensity();
            };

            _cboCube.SelectedIndexChanged      += (s, e) => { if (!_loading) { FillDimensions(); FillTree(); Search(); ShowDensity(); } };
            _cboDimension.SelectedIndexChanged += (s, e) => { if (!_loading) { FillTree(); Search(); ShowDensity(); } };
            _txtSearch.TextChanged += (s, e) => { _searchDelay.Stop(); _searchDelay.Start(); };
            _txtSearch.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { _searchDelay.Stop(); Search(); e.SuppressKeyPress = true; } };
            _chkAliases.CheckedChanged += (s, e) => { FillTree(); Search(); };

            // Tree | (results / details)
            _split = new SplitContainer { Dock = DockStyle.Fill };
            var split = _split;
            _tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true };
            _tree.BeforeExpand += (s, e) => LoadChildren(e.Node);
            _tree.AfterSelect  += (s, e) => ShowDetails(e.Node?.Tag as NodeRef);
            _tree.NodeMouseDoubleClick += (s, e) => { if (e.Node?.Tag is NodeRef r && r.Node != null) InsertSelected(); };
            split.Panel1.Controls.Add(_tree);

            _right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            var right = _right;
            _results = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
            _results.Columns.Add("Member", 150);
            _results.Columns.Add("Alias", 130);
            _results.Columns.Add("Dimension", 90);
            _results.Columns.Add("Parent", 110);
            _results.SelectedIndexChanged += (s, e) =>
            {
                if (_results.SelectedItems.Count > 0 && _results.SelectedItems[0].Tag is NodeRef r) { ShowDetails(r); Reveal(r); }
            };
            _results.ItemActivate += (s, e) => InsertSelected();
            right.Panel1.Controls.Add(_results);

            _txtDetails = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9.5f), BackColor = SystemColors.Window,
            };
            right.Panel2.Controls.Add(_txtDetails);
            split.Panel2.Controls.Add(right);

            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 44, ColumnCount = 5, Padding = new Padding(8, 6, 8, 6) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (int i = 0; i < 4; i++) bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _lblStatus = new Label { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText, AutoEllipsis = true };
            _chkQuotes = new CheckBox { Text = "Insert with \"quotes\"", AutoSize = true, Checked = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 12, 0) };
            _btnInsert = new Button { Text = "Insert", Size = new Size(80, 28), Enabled = false, Margin = new Padding(0, 0, 8, 0) };
            _btnCopy   = new Button { Text = "Copy",   Size = new Size(80, 28), Enabled = false, Margin = new Padding(0, 0, 8, 0) };
            var close  = new Button { Text = "Close",  Size = new Size(80, 28), Margin = Padding.Empty };
            _btnInsert.Click += (s, e) => InsertSelected();
            _btnCopy.Click   += (s, e) => { string n = SelectedName(); if (n != null) Clipboard.SetText(n); };
            close.Click      += (s, e) => Close();
            bottom.Controls.Add(_lblStatus, 0, 0);
            bottom.Controls.Add(_chkQuotes, 1, 0);
            bottom.Controls.Add(_btnInsert, 2, 0);
            bottom.Controls.Add(_btnCopy, 3, 0);
            bottom.Controls.Add(close, 4, 0);

            Controls.Add(split);
            Controls.Add(top);
            Controls.Add(bottom);
            CancelButton = close;
        }

        // ------------------------------------------------------------------ //
        //  Data
        // ------------------------------------------------------------------ //

        private sealed class NodeRef
        {
            public EpmMetadata.DimensionInfo Dim;
            public EpmMetadata.MemberNode    Node;   // null = the dimension itself
        }

        /// <summary>Shows another connection's dimensions (when the window is reused).</summary>
        /// <summary>Local folder of the connection shown.</summary>
        public string ConnectionFolder => _cfg?.LocalFolderPath;

        public void ShowConnection(ConnectionConfig cfg)
        {
            _cfg = cfg;
            _md  = EpmMetadata.Load(cfg?.LocalFolderPath);
            string conn = string.IsNullOrWhiteSpace(cfg?.Name) ? "EPM Cloud" : cfg.Name;
            Text = "Dimension Navigator — " + conn;

            _loading = true;
            _cboCube.Items.Clear();
            _cboCube.Items.Add(AllCubes);
            if (_md.HasDimensions) foreach (string c in _md.Dims.Cubes.Keys.OrderBy(c => c)) _cboCube.Items.Add(c);
            _cboCube.SelectedIndex = 0;
            _loading = false;
            FillDimensions();
            FillTree();
            Search();
            ShowDensity();

            if (!_md.HasDimensions)
                _lblStatus.Text = "No dimensions downloaded for this connection. Tick \"Download dimensions and members on Pull\" " +
                                  "in the Connections window, then Pull with \"update dimensions and members\" ticked.";
            else
            {
                int members = _md.Dims.ByName.Values.Sum(d => d.Members.Count);
                bool flat = _md.Dims.ByName.Values.Any(d => !d.HasHierarchy && d.Members.Count > 0);
                _lblStatus.Text = $"{_md.Dims.ByName.Count} dimensions, {members:N0} members, downloaded {_md.Dims.PulledAt:g}" +
                                  (flat ? " — pull dimensions again to see the hierarchy" : "");
            }
        }

        private IEnumerable<EpmMetadata.DimensionInfo> DimensionsInScope()
        {
            if (!_md.HasDimensions) return Enumerable.Empty<EpmMetadata.DimensionInfo>();
            string cube = _cboCube.SelectedItem as string;
            IEnumerable<string> names = cube == null || cube == AllCubes
                ? (IEnumerable<string>)_md.Dims.ByName.Keys
                : (_md.Dims.Cubes.TryGetValue(cube, out var list) ? list : new List<string>());
            string dim = _cboDimension.SelectedItem as string;
            if (dim != null && dim != AllDimensions) names = names.Where(n => string.Equals(n, dim, StringComparison.OrdinalIgnoreCase));
            // A cube shows its own members (Planning returns only those valid for it); all cubes, the union
            string inCube = cube == null || cube == AllCubes ? null : cube;
            return names.Select(n => _md.Dims.Get(inCube, n)).Where(d => d != null).OrderBy(d => d.Name);
        }

        /// <summary>The Dense box: shown for one dimension (not an attribute one) of one cube.</summary>
        private void ShowDensity()
        {
            string cube = _cboCube.SelectedItem as string, dim = _cboDimension.SelectedItem as string;
            EpmMetadata.DimensionInfo info = cube == null || cube == AllCubes || dim == null || dim == AllDimensions ? null : _md.Dims.Get(cube, dim);
            bool show = info != null && !CubeDensity.IsAttribute(info);
            _loading = true;
            _chkDense.Visible = show;
            if (show)
            {
                bool fromEpm = CubeDensity.FromEpm(_md, cube, dim);
                _chkDense.Text = $"Dense in {cube}" + (fromEpm ? " (from EPM)" : CubeDensity.IsChosen(_cfg?.LocalFolderPath, cube) ? "" : " (default)");
                _chkDense.Checked = CubeDensity.IsDense(_cfg?.LocalFolderPath, _md, cube, dim);
                _chkDense.Enabled = !fromEpm;    // what Planning reports can't be overridden here
            }
            _loading = false;
        }

        private void FillDimensions()
        {
            _loading = true;
            string keep = _cboDimension.SelectedItem as string;
            _cboDimension.Items.Clear();
            _cboDimension.Items.Add(AllDimensions);
            string cube = _cboCube.SelectedItem as string;
            if (_md.HasDimensions)
            {
                IEnumerable<string> dims = cube == null || cube == AllCubes
                    ? (IEnumerable<string>)_md.Dims.ByName.Keys
                    : (_md.Dims.Cubes.TryGetValue(cube, out var l) ? l : new List<string>());
                foreach (string d in dims.OrderBy(x => x)) _cboDimension.Items.Add(d);
            }
            int idx = keep == null ? 0 : _cboDimension.Items.IndexOf(keep);
            _cboDimension.SelectedIndex = idx < 0 ? 0 : idx;
            _loading = false;
        }

        // ------------------------------------------------------------------ //
        //  Tree (children loaded on expand)
        // ------------------------------------------------------------------ //

        private const string Placeholder = "…";

        private void FillTree()
        {
            _tree.BeginUpdate();
            try
            {
                _tree.Nodes.Clear();
                var dims = DimensionsInScope().ToList();
                foreach (var d in dims)
                {
                    var node = new TreeNode($"{d.Name}  ({d.DimType}, {d.Members.Count:N0} members)")
                    {
                        Tag = new NodeRef { Dim = d }, NodeFont = new Font(_tree.Font, FontStyle.Bold),
                    };
                    bool hasKids = d.HasHierarchy ? d.ChildrenOf(d.Name).Count > 0 : d.Members.Count > 0;
                    if (hasKids) node.Nodes.Add(Placeholder);
                    _tree.Nodes.Add(node);
                }
                if (dims.Count == 1 && _tree.Nodes.Count == 1) _tree.Nodes[0].Expand();
            }
            finally
            {
                _tree.EndUpdate();
            }
            ShowDetails(null);   // the previous selection belonged to the old tree (another cube's copy)
        }

        private void LoadChildren(TreeNode parent)
        {
            if (parent.Nodes.Count != 1 || parent.Nodes[0].Text != Placeholder) return;
            var r = (NodeRef)parent.Tag;
            _tree.BeginUpdate();
            try
            {
                parent.Nodes.Clear();
                if (!r.Dim.HasHierarchy)
                {
                    // Older download without parents: a flat, sorted list (search works too)
                    foreach (string m in r.Dim.Members.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(5000))
                        parent.Nodes.Add(new TreeNode(m) { Tag = new NodeRef { Dim = r.Dim, Node = new EpmMetadata.MemberNode { Name = m, Parent = r.Dim.Name } } });
                    return;
                }
                foreach (EpmMetadata.MemberNode child in r.Dim.ChildrenOf(r.Node?.Name ?? r.Dim.Name))
                {
                    var node = new TreeNode(Label(r.Dim, child)) { Tag = new NodeRef { Dim = r.Dim, Node = child } };
                    if (IsShared(r.Dim, child)) node.ForeColor = SystemColors.GrayText;
                    else if (r.Dim.ChildrenOf(child.Name).Count > 0) node.Nodes.Add(Placeholder);
                    parent.Nodes.Add(node);
                }
            }
            finally
            {
                _tree.EndUpdate();
            }
        }

        private string Label(EpmMetadata.DimensionInfo dim, EpmMetadata.MemberNode n) =>
            n.Name + (_chkAliases.Checked && !string.IsNullOrEmpty(n.Alias) ? "  —  " + n.Alias : "") +
            (IsShared(dim, n) ? "  (shared)" : "");

        /// <summary>A shared member: a later position of a member already placed earlier in the outline.</summary>
        private static bool IsShared(EpmMetadata.DimensionInfo dim, EpmMetadata.MemberNode n)
        {
            var positions = dim.PositionsOf(n.Name);
            return positions.Count > 1 && !ReferenceEquals(positions[0], n);
        }

        /// <summary>Expands the tree down to a member and selects it.</summary>
        private void Reveal(NodeRef r)
        {
            if (r?.Node == null || !r.Dim.HasHierarchy) return;
            TreeNode root = _tree.Nodes.Cast<TreeNode>().FirstOrDefault(n => ((NodeRef)n.Tag).Dim == r.Dim);
            if (root == null) return;
            List<string> path = r.Dim.PathOf(r.Node);
            TreeNode cur = root;
            for (int i = 1; i < path.Count && cur != null; i++)
            {
                cur.Expand();
                string want = path[i];
                bool last = i == path.Count - 1;
                cur = cur.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Tag is NodeRef nr && nr.Node != null &&
                          string.Equals(nr.Node.Name, want, StringComparison.OrdinalIgnoreCase) &&
                          (!last || ReferenceEquals(nr.Node, r.Node)))
                      ?? cur.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Tag is NodeRef nr && nr.Node != null &&
                          string.Equals(nr.Node.Name, want, StringComparison.OrdinalIgnoreCase));
            }
            if (cur != null)
            {
                _tree.SelectedNode = cur;
                cur.EnsureVisible();
            }
        }

        // ------------------------------------------------------------------ //
        //  Search
        // ------------------------------------------------------------------ //

        private void Search()
        {
            string q = _txtSearch.Text.Trim();
            _results.BeginUpdate();
            try
            {
                _results.Items.Clear();
                if (q.Length == 0) return;
                int found = 0;
                foreach (var d in DimensionsInScope())
                {
                    IEnumerable<EpmMetadata.MemberNode> pool = d.HasHierarchy
                        ? d.Nodes.Where(n => !IsShared(d, n))
                        : d.Members.Select(m => new EpmMetadata.MemberNode { Name = m, Parent = d.Name });
                    foreach (var n in pool)
                    {
                        bool hit = n.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   (n.Alias != null && n.Alias.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                                   string.Equals(n.Id, q, StringComparison.OrdinalIgnoreCase);   // a pasted member ID
                        if (!hit) continue;
                        if (++found > MaxResults) break;
                        _results.Items.Add(new ListViewItem(new[] { n.Name, n.Alias ?? "", d.Name, n.Parent ?? "" })
                        {
                            Tag = new NodeRef { Dim = d, Node = n },
                        });
                    }
                    if (found > MaxResults) break;
                }
                _lblStatus.Text = found == 0 ? $"No member, alias or member ID matches \"{q}\"."
                                : found > MaxResults ? $"More than {MaxResults} matches — showing the first {MaxResults}. Type more to narrow it."
                                : $"{found} match{(found == 1 ? "" : "es")} for \"{q}\".";
            }
            finally
            {
                _results.EndUpdate();
            }
        }

        // ------------------------------------------------------------------ //
        //  Details, insert, copy
        // ------------------------------------------------------------------ //

        private NodeRef _selected;

        private void ShowDetails(NodeRef r)
        {
            _selected = r;
            _btnInsert.Enabled = _btnCopy.Enabled = r != null;
            if (r == null) { _txtDetails.Text = ""; return; }

            var sb = new StringBuilder();
            var cubes = _md.Dims.Cubes.Where(c => c.Value.Contains(r.Dim.Name, StringComparer.OrdinalIgnoreCase)).Select(c => c.Key).OrderBy(c => c);
            if (r.Node == null)
            {
                sb.AppendLine($"Dimension:  {r.Dim.Name}");
                sb.AppendLine($"Type:       {r.Dim.DimType}");
                sb.AppendLine($"Members:    {r.Dim.Members.Count:N0}");
                sb.AppendLine($"Top level:  {string.Join(", ", r.Dim.ChildrenOf(r.Dim.Name).Select(n => n.Name).Take(20))}");
                sb.AppendLine($"In cubes:   {string.Join(", ", cubes)}");
            }
            else
            {
                var n = r.Node;
                var kids = r.Dim.HasHierarchy ? r.Dim.ChildrenOf(n.Name) : new List<EpmMetadata.MemberNode>();
                var positions = r.Dim.HasHierarchy ? r.Dim.PositionsOf(n.Name) : new List<EpmMetadata.MemberNode>();
                sb.AppendLine($"Member:     {n.Name}");
                if (!string.IsNullOrEmpty(n.Alias)) sb.AppendLine($"Alias:      {n.Alias}");
                sb.AppendLine($"Dimension:  {r.Dim.Name} ({r.Dim.DimType})");
                if (r.Dim.HasHierarchy)
                {
                    sb.AppendLine($"Parent:     {n.Parent}");
                    sb.AppendLine($"Path:       {string.Join(" › ", r.Dim.PathOf(n))}");
                    sb.AppendLine($"Children:   {kids.Count}{(kids.Count == 0 ? "  (level 0)" : "")}");
                    if (kids.Count > 0)
                        sb.AppendLine($"            {string.Join(", ", kids.Select(k => k.Name).Take(15))}{(kids.Count > 15 ? ", …" : "")}");
                    if (positions.Count > 1)
                        sb.AppendLine($"Shared:     under {positions.Count} parents — {string.Join(", ", positions.Select(p => p.Parent))}");
                }
                sb.AppendLine($"In cubes:   {string.Join(", ", cubes)}");
            }
            AppendFields(sb, r.Node == null ? r.Dim.Fields : r.Node.Fields);
            _txtDetails.Text = sb.ToString();
        }

        /// <summary>Every field Planning sent for the member or dimension, as received.</summary>
        private void AppendFields(StringBuilder sb, Dictionary<string, string> fields)
        {
            sb.AppendLine();
            if (fields == null || fields.Count == 0)
            {
                if (_md.HasDimensions) sb.AppendLine("From EPM:   pull dimensions and members again to see the member's ID and properties");
                return;
            }
            sb.AppendLine("From EPM:");
            int width = Math.Min(28, fields.Keys.Max(k => k.Length));
            foreach (var kv in fields)
                sb.AppendLine($"  {kv.Key.PadRight(width)}  {kv.Value.Replace("\r", "").Replace("\n", "\r\n  " + new string(' ', width + 2))}");
        }

        private string SelectedName() => _selected == null ? null : (_selected.Node?.Name ?? _selected.Dim.Name);

        private void InsertSelected()
        {
            string name = SelectedName();
            if (name == null) return;
            _insert?.Invoke(_chkQuotes.Checked ? "\"" + name + "\"" : name);
        }

        // ------------------------------------------------------------------ //
        //  Modeless inside Notepad++: register so Tab / Enter reach the dialog
        // ------------------------------------------------------------------ //

        private const uint NPPM_MODELESSDIALOG = 2024 + 12;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SendMessage(NppPluginNET.PluginBase.nppData._nppHandle, NPPM_MODELESSDIALOG, IntPtr.Zero, Handle);      // MODELESSDIALOGADD
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            SendMessage(NppPluginNET.PluginBase.nppData._nppHandle, NPPM_MODELESSDIALOG, (IntPtr)1, Handle);        // MODELESSDIALOGREMOVE
            base.OnHandleDestroyed(e);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);   // scaled and sized: now the splitters can be placed
            try
            {
                _split.Panel1MinSize    = Scaled(200);
                _split.Panel2MinSize    = Scaled(220);
                _split.SplitterDistance = Math.Max(_split.Panel1MinSize, _split.Width * 45 / 100);
                _right.SplitterDistance = Math.Max(_right.Panel1MinSize, _right.Height * 55 / 100);
            }
            catch (InvalidOperationException) { /* window too small for the minimums: keep defaults */ }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _searchDelay.Dispose();
            base.OnFormClosed(e);
        }

        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    }
}
