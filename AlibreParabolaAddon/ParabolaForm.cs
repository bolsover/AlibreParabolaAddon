using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace AlibreParabolaAddon
{
    /// <summary>
    /// Modeless tool window: the parabola's two end points (filled in from the Alibre
    /// selection, or typed), the target sketch and the height; previews the equation
    /// and asks the command to create the spline. Leaving Z empty for both points makes
    /// a parabola in a 2D sketch; entering Z for both makes one in a 3D sketch.
    /// </summary>
    internal sealed class ParabolaForm : Form
    {
        private const string CoordinateFormat = "G10";

        private readonly ParabolaCommand _command;
        private readonly ComboBox _sketch = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        private readonly TextBox _x1 = CoordinateBox();
        private readonly TextBox _y1 = CoordinateBox();
        private readonly TextBox _z1 = CoordinateBox();
        private readonly TextBox _x2 = CoordinateBox();
        private readonly TextBox _y2 = CoordinateBox();
        private readonly TextBox _z2 = CoordinateBox();
        private readonly Label _status = new Label { AutoSize = true, ForeColor = SystemColors.GrayText };
        private readonly TextBox _height = new TextBox { Width = 120 };
        private readonly TextBox _equation = new TextBox
        {
            Multiline = true, ReadOnly = true, Height = 64, Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9f)
        };
        private readonly Button _create = new Button { Text = "Create", AutoSize = true };
        private readonly Button _swap = new Button { Text = "Swap points", AutoSize = true };
        private readonly Button _close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };

        /// <summary>Whether the sketch list currently holds 3D sketches.</summary>
        private bool _listIs3D;

        public ParabolaForm(ParabolaCommand command, string unit)
        {
            _command = command;

            Text = ParabolaAddOn.Title;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.CenterParent;
            TopMost = true;
            ShowInTaskbar = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(8);
            CancelButton = _close;

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, WrapContents = false
            };
            buttons.Controls.Add(_close);
            buttons.Controls.Add(_create);
            buttons.Controls.Add(_swap);

            var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 480));
            AddRow(grid, "Point 1:", PointRow(_x1, _y1, _z1, unit));
            AddRow(grid, "Point 2:", PointRow(_x2, _y2, _z2, unit));
            AddRow(grid, "", new Label
            {
                AutoSize = true, ForeColor = SystemColors.GrayText,
                Text = "Leave Z empty for a 2D sketch; enter Z for both points for a 3D sketch."
            });
            AddRow(grid, "Sketch:", _sketch);
            AddRow(grid, "", _status);
            AddRow(grid, "Height:", Row(_height, Caption(unit)));
            AddRow(grid, "Equation:", _equation);
            grid.Controls.Add(buttons, 0, grid.RowCount);
            grid.SetColumnSpan(buttons, 2);
            Controls.Add(grid);

            foreach (var box in new[] { _x1, _y1, _x2, _y2, _height })
                box.TextChanged += (s, e) => RefreshPreview();
            foreach (var box in new[] { _z1, _z2 })
                box.TextChanged += (s, e) => OnZChanged();
            _sketch.SelectedIndexChanged += (s, e) => RefreshPreview();
            // Pick up sketches added or renamed since the dialog opened.
            _sketch.DropDown += (s, e) => RefreshSketches(_sketch.SelectedItem as SketchTarget);
            _create.Click += (s, e) => OnCreate();
            _swap.Click += (s, e) => SwapPoints();
            _close.Click += (s, e) => Close();

            RefreshSketches(null);
            SetStatus("Select two sketch points or one sketch line, or type the points.");
        }

        private static TextBox CoordinateBox() => new TextBox { Width = 100 };

        private static Label Caption(string text) =>
            new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left };

        private static FlowLayoutPanel Row(params Control[] controls)
        {
            var row = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, WrapContents = false };
            row.Controls.AddRange(controls);
            return row;
        }

        private static FlowLayoutPanel PointRow(TextBox x, TextBox y, TextBox z, string unit) =>
            Row(Caption("X"), x, Caption("Y"), y, Caption("Z"), z, Caption(unit));

        private static void AddRow(TableLayoutPanel grid, string caption, Control control)
        {
            var row = grid.RowCount++;
            grid.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(3, 6, 3, 3) }, 0, row);
            grid.Controls.Add(control, 1, row);
        }

        /// <summary>
        /// Called by the command when two points are selected in a 2D sketch (display units,
        /// sketch coordinates). Clears Z, since the points are 2D.
        /// </summary>
        public void SetSelectedPoints(double x1, double y1, double x2, double y2, string sketchName, string status)
        {
            _x1.Text = Format(x1);
            _y1.Text = Format(y1);
            _x2.Text = Format(x2);
            _y2.Text = Format(y2);
            _z1.Text = _z2.Text = string.Empty;
            RefreshSketches(SketchTarget.Existing2D(sketchName));
            SetStatus(status);
        }

        /// <summary>
        /// Called by the command for any other selection. The point boxes are left alone,
        /// so typed values survive selection changes.
        /// </summary>
        public void SetStatus(string status)
        {
            _status.Text = status ?? string.Empty;
            RefreshPreview();
        }

        private static string Format(double value) => value.ToString(CoordinateFormat, CultureInfo.CurrentCulture);

        /// <summary>Entering or clearing Z switches the sketch list between 2D and 3D sketches.</summary>
        private void OnZChanged()
        {
            if (WantsThreeD() != _listIs3D) RefreshSketches(null);
            RefreshPreview();
        }

        /// <summary>3D as soon as either Z box has text, so the list switches while typing.</summary>
        private bool WantsThreeD() => _z1.Text.Trim().Length > 0 || _z2.Text.Trim().Length > 0;

        /// <summary>Reloads the sketch list for the current mode, selecting <paramref name="select"/> if present.</summary>
        private void RefreshSketches(SketchTarget select)
        {
            _listIs3D = WantsThreeD();
            var wanted = select != null && select.Is3D == _listIs3D ? select : null;
            wanted = wanted ?? _sketch.SelectedItem as SketchTarget;
            if (wanted != null && wanted.Is3D != _listIs3D) wanted = null;
            wanted = wanted ?? _command.DefaultSketchTarget(_listIs3D);

            _sketch.BeginUpdate();
            try
            {
                _sketch.Items.Clear();
                foreach (var target in _command.SketchTargets(_listIs3D)) _sketch.Items.Add(target);
                var index = _sketch.Items.IndexOf(wanted);
                _sketch.SelectedIndex = index >= 0 ? index : _sketch.Items.Count > 0 ? 0 : -1;
            }
            finally
            {
                _sketch.EndUpdate();
            }
        }

        private void SwapPoints()
        {
            Swap(_x1, _x2);
            Swap(_y1, _y2);
            Swap(_z1, _z2);
        }

        private static void Swap(TextBox a, TextBox b)
        {
            var text = a.Text;
            a.Text = b.Text;
            b.Text = text;
        }

        private static bool TryGetNumber(TextBox box, out double value) =>
            double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            && !double.IsNaN(value) && !double.IsInfinity(value);

        /// <summary>An empty box gives null; otherwise the box must hold a number.</summary>
        private static bool TryGetOptionalNumber(TextBox box, out double? value)
        {
            value = null;
            if (box.Text.Trim().Length == 0) return true;
            double number;
            if (!TryGetNumber(box, out number)) return false;
            value = number;
            return true;
        }

        private bool TryGetPoints(out double x1, out double y1, out double? z1,
            out double x2, out double y2, out double? z2)
        {
            // Non-short-circuit &: every out parameter must be assigned.
            return TryGetNumber(_x1, out x1) & TryGetNumber(_y1, out y1) & TryGetOptionalNumber(_z1, out z1)
                & TryGetNumber(_x2, out x2) & TryGetNumber(_y2, out y2) & TryGetOptionalNumber(_z2, out z2);
        }

        private bool TryGetHeight(out double height) => TryGetNumber(_height, out height) && height != 0;

        private void RefreshPreview()
        {
            double x1, y1, x2, y2, height;
            double? z1, z2;
            var pointsOk = TryGetPoints(out x1, out y1, out z1, out x2, out y2, out z2);
            var heightOk = TryGetHeight(out height);
            var sketchOk = _sketch.SelectedItem != null;
            _swap.Enabled = pointsOk;
            _create.Enabled = false;

            if (!pointsOk) { _equation.Text = "Enter numbers for X and Y of both points (Z optional)."; return; }
            if (!heightOk)
            {
                _equation.Text = z1.HasValue || z2.HasValue
                    ? "Enter a non-zero height (positive = up, +Z, from the chord)."
                    : "Enter a non-zero height (positive = left of Point 1 → Point 2).";
                return;
            }

            try
            {
                _equation.Text = _command.DescribeEquation(x1, y1, z1, x2, y2, z2, height);
                _create.Enabled = sketchOk;
                if (!sketchOk) _equation.Text += Environment.NewLine + "Choose the sketch to add the parabola to.";
            }
            catch (Exception e)
            {
                _equation.Text = e.Message;
            }
        }

        private void OnCreate()
        {
            double x1, y1, x2, y2, height;
            double? z1, z2;
            var target = _sketch.SelectedItem as SketchTarget;
            if (target == null || !TryGetPoints(out x1, out y1, out z1, out x2, out y2, out z2)
                || !TryGetHeight(out height)) return;
            try
            {
                var used = _command.CreateParabola(target, x1, y1, z1, x2, y2, z2, height);
                // A new sketch becomes the target, so further parabolas go into it.
                RefreshSketches(used);
                _status.Text = "Parabola created in " + used.Name + ".";
            }
            catch (Exception e)
            {
                MessageBox.Show(this, e.Message, ParabolaAddOn.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
