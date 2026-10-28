using System;
using System.Drawing;
using System.Windows.Forms;

namespace CalcStudioEPM.Forms
{
    /// <summary>
    /// Asked before every pull: what to update — the calculations (Calculation Manager export
    /// into the scripts folder), the dimensions, members and substitution / user variables used
    /// by validation and autocomplete (quick), or both; the IPM configuration (Auto Predict),
    /// calendars and events, when the connection offers them (never ticked to start with) — and
    /// whether to clear the "*.local.NNN.*" copies kept from earlier pulls (only with the
    /// calculations or IPM).
    /// </summary>
    public class PullOptionsForm : PluginForm
    {
        private readonly CheckBox _chkCalculations;
        private readonly CheckBox _chkMetadata;
        private readonly CheckBox _chkIpm;
        private readonly CheckBox _chkClearCopies;
        private readonly Button   _btnPull;
        private readonly int      _localCopies;

        public bool UpdateCalculations => _chkCalculations.Checked;
        /// <summary>Substitution and user variables, and — when the connection downloads them — dimensions and members.</summary>
        public bool UpdateMetadata => _chkMetadata.Checked;
        /// <summary>Auto Predict jobs, calendars and events, as a package of their own.</summary>
        public bool UpdateIpm => _chkIpm.Enabled && _chkIpm.Checked;
        /// <summary>Don't copy the earlier "keep both" copies back after the pull.</summary>
        public bool ClearLocalCopies => _chkClearCopies.Enabled && _chkClearCopies.Checked;

        /// <param name="withDimensions">The connection downloads dimensions and members.</param>
        /// <param name="dimensionsPulled">When the dimensions were last downloaded; null = never.</param>
        /// <param name="localCopies">How many "*.local.NNN.*" files the scripts folder holds.</param>
        /// <param name="withIpm">The connection offers IPM (Configure EPM Connection).</param>
        public PullOptionsForm(string connection, bool withDimensions, DateTime? dimensionsPulled, int localCopies, bool withIpm = false)
        {
            _localCopies    = localCopies;
            Text            = "Pull Calculations — " + connection;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            ShowIcon        = false;
            StartPosition   = FormStartPosition.CenterParent;
            AutoSize        = true;
            AutoSizeMode    = AutoSizeMode.GrowAndShrink;

            var stack = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16, 14, 16, 12),
            };

            stack.Controls.Add(new Label
            {
                Text = "What to update from EPM Cloud:", AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(0, 0, 0, 10),
            });

            _chkCalculations = Box("Update all calculations", true);
            stack.Controls.Add(_chkCalculations);
            stack.Controls.Add(Note("Exports Calculation Manager and writes the rules and templates to the scripts folder again."));

            _chkMetadata = Box(withDimensions ? "Update dimensions, members and substitution variables"
                                              : "Update substitution and user variables", true);
            stack.Controls.Add(_chkMetadata);
            stack.Controls.Add(Note(withDimensions
                ? "Also the user variables. Used to check and autocomplete member names; a quick download. " +
                  (dimensionsPulled == null ? "Dimensions were never downloaded for this connection."
                                            : $"Dimensions last updated {dimensionsPulled.Value:g}.")
                : "Used by validation and autocomplete; a quick download. (This connection doesn't download " +
                  "dimensions and members — see Configure EPM Connection.)"));

            _chkIpm = Box("Update IPM configuration, calendar and events", false);
            _chkIpm.Enabled = withIpm;
            stack.Controls.Add(_chkIpm);
            stack.Controls.Add(Note(withIpm
                ? "Auto Predict jobs, calendars and events, exported as a package of their own and written to the " +
                  "scripts folder's IPM folder for editing (members by name when the dimensions are downloaded)."
                : "This connection doesn't download IPM — see Configure EPM Connection."));

            _chkClearCopies = Box("Clear local copies" + (localCopies > 0 ? $" ({localCopies} file{(localCopies == 1 ? "" : "s")})" : ""), false);
            stack.Controls.Add(_chkClearCopies);
            stack.Controls.Add(Note(localCopies > 0
                ? "The *.local.NNN.* files kept beside EPM's version by earlier pulls (\"keep both\"). Ticked, they are not " +
                  "copied back after this pull; a copy stays in the connection's .epm\\backup folder."
                : "There are no *.local.NNN.* files from earlier pulls."));

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft, Size = new Size(440, 30), Margin = Padding.Empty,
            };
            var cancel = new Button { Text = "Cancel", Size = new Size(80, 26), DialogResult = DialogResult.Cancel, Margin = Padding.Empty };
            _btnPull   = new Button { Text = "Pull",   Size = new Size(80, 26), DialogResult = DialogResult.OK, Margin = new Padding(0, 0, 8, 0) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(_btnPull);
            stack.Controls.Add(buttons);

            _chkCalculations.CheckedChanged += (s, e) => UpdateEnabled();
            _chkMetadata.CheckedChanged     += (s, e) => UpdateEnabled();
            _chkIpm.CheckedChanged          += (s, e) => UpdateEnabled();
            UpdateEnabled();

            Controls.Add(stack);
            AcceptButton = _btnPull;
            CancelButton = cancel;
        }

        /// <summary>Clearing copies only applies with the calculations or IPM; Pull needs something to update.</summary>
        private void UpdateEnabled()
        {
            _chkClearCopies.Enabled = (_chkCalculations.Checked || UpdateIpm) && _localCopies > 0;
            _btnPull.Enabled = _chkCalculations.Checked || _chkMetadata.Checked || UpdateIpm;
        }

        private static CheckBox Box(string text, bool on) =>
            new CheckBox { Text = text, AutoSize = true, Checked = on, Margin = new Padding(0, 0, 0, 4) };

        private static Label Note(string text) => new Label
        {
            Text = text, AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = SystemColors.GrayText,
            Margin = new Padding(18, 0, 0, 14),
        };
    }
}
