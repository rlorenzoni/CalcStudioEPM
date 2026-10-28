using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CalcStudioEPM.Forms
{
    /// <summary>
    /// Asks for the two parameters of an Auto Predict job before running it: forceRun (true /
    /// false) and paginatedDim (a dimension of the job's cube). Each is pre-filled with the
    /// answer last given for this job.
    /// </summary>
    public class AutoPredictRunForm : PluginForm
    {
        private readonly ComboBox _force;
        private readonly ComboBox _dimension;

        public bool   ForceRun           => _force.SelectedIndex == 0;
        public string PaginatedDimension => _dimension.Text.Trim();

        /// <param name="dimensions">The cube's dimensions; empty when they weren't downloaded (then typed).</param>
        public AutoPredictRunForm(string jobName, List<string> dimensions, string lastForceRun, string lastDimension)
        {
            Text            = $"Run Auto Predict — {jobName}";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            ShowIcon        = false;
            StartPosition   = FormStartPosition.CenterParent;

            const int width = 520, rowH = 52, top = 36;
            Controls.Add(new Label
            {
                Text = "Enter the job's parameters, then click Execute.", Location = new Point(12, 12), AutoSize = true,
            });

            void Row(int i, string label, string hint, Control box)
            {
                int y = top + i * rowH;
                Controls.Add(new Label
                {
                    Text = label, Location = new Point(12, y + 2), Size = new Size(200, 18),
                    Font = new Font(Font, FontStyle.Bold), AutoEllipsis = true,
                });
                Controls.Add(new Label
                {
                    Text = hint, Location = new Point(12, y + 20), Size = new Size(200, 18),
                    ForeColor = SystemColors.GrayText, AutoEllipsis = true,
                });
                box.Location = new Point(220, y + 10);
                box.Width    = width - 220 - 28;
                Controls.Add(box);
            }

            _force = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            _force.Items.AddRange(new object[] { "true", "false" });
            _force.SelectedIndex = string.Equals(lastForceRun, "true", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            Row(0, "Force run", "forceRun · true or false", _force);

            bool listed = dimensions != null && dimensions.Count > 0;
            _dimension = new ComboBox { DropDownStyle = listed ? ComboBoxStyle.DropDownList : ComboBoxStyle.DropDown };
            if (listed)
            {
                _dimension.Items.AddRange(dimensions.Cast<object>().ToArray());
                int at = dimensions.FindIndex(d => string.Equals(d, lastDimension, StringComparison.OrdinalIgnoreCase));
                if (at >= 0) _dimension.SelectedIndex = at;
            }
            else _dimension.Text = lastDimension ?? "";
            Row(1, "Paginated dimension", listed ? "paginatedDim · a dimension of the cube" : "paginatedDim · type a dimension name", _dimension);

            int buttonsY = top + 2 * rowH + 10;
            var execute = new Button { Text = "Execute", Location = new Point(width - 184, buttonsY), Size = new Size(80, 26) };
            var cancel  = new Button { Text = "Cancel",  Location = new Point(width - 96,  buttonsY), Size = new Size(80, 26),
                                       DialogResult = DialogResult.Cancel };
            execute.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            void Enable() => execute.Enabled = PaginatedDimension.Length > 0;
            _dimension.SelectedIndexChanged += (s, e) => Enable();
            _dimension.TextChanged          += (s, e) => Enable();
            Enable();
            Controls.Add(execute);
            Controls.Add(cancel);
            AcceptButton = execute;
            CancelButton = cancel;
            ClientSize = new Size(width, buttonsY + 26 + 12);
        }
    }
}
