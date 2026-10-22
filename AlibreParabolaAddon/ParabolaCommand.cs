using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AlibreAddOn;
using AlibreX;
using Timer = System.Windows.Forms.Timer;

namespace AlibreParabolaAddon
{
    /// <summary>
    /// Live command: tracks the two sketch points the user selects, and adds an
    /// exact parabolic B-spline through them to their sketch on request.
    /// </summary>
    public class ParabolaCommand : IAlibreAddOnCommand
    {
        /// <summary>CustomError.AD_E_ACTIVE_SKETCHSESSION_EXISTS: the sketch is already open for editing.</summary>
        private const int ActiveSketchSessionExists = -2147220963;

        private const int InterpolationSamples = 21;

        private readonly IADRoot _root;
        private readonly string _sessionIdentifier;
        private readonly IADSession _session;
        private readonly IntPtr _parentWindow;
        private readonly Action<ParabolaCommand> _onTerminated;
        private readonly ADUnits _modelUnits;
        private readonly ADUnits _displayUnits;

        private IADAddOnCommandSite _site;
        private ParabolaForm _form;
        private bool _terminated;
        private string _selectionKey;
        private readonly Timer _poll = new Timer { Interval = 300 };

        public ParabolaCommand(IADRoot root, IADSession session, IntPtr parentWindow, Action<ParabolaCommand> onTerminated)
        {
            _root = root;
            _session = session;
            _sessionIdentifier = session.Identifier;
            _parentWindow = parentWindow;
            _onTerminated = onTerminated;
            _poll.Tick += (s, e) => OnPoll();

            var properties = (session as IADDesignSession)?.DesignProperties;
            _modelUnits = properties?.ModelUnits ?? ADUnits.AD_CENTIMETERS;
            _displayUnits = properties?.LengthDisplayUnits ?? _modelUnits;
        }

        public IADAddOnCommandSite CommandSite
        {
            get { return _site; }
            // Keep this a plain assignment: Alibre sets the site while the command is still
            // starting, and side effects here can stop OnComplete from ever being called.
            set { _site = value; }
        }

        /// <summary>True once OnComplete has shown the dialog, until the dialog is closed.</summary>
        public bool IsShowing => _form != null && !_terminated;

        /// <summary>
        /// Called once Alibre has successfully started the command (not when it ends):
        /// show the dialog and let the user pick sketch figures.
        /// </summary>
        public void OnComplete()
        {
            if (_terminated || _form != null) return;
            try
            {
                _form = new ParabolaForm(this, Units.Suffix(_displayUnits));
                _form.FormClosed += (s, e) =>
                {
                    _form = null;
                    Terminate();
                };
                _form.Show(new WindowHandle(_parentWindow));
            }
            catch (Exception e)
            {
                MessageBox.Show(e.ToString(), ParabolaAddOn.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                Terminate();
                return;
            }

            // Only once the command is running, as in Alibre's SelectionAddOnSample:
            // allow individual sketch figures (not just whole sketches) to be picked.
            EnableSketchFigureSelection();
            // The user may already have the points selected.
            OnSelectionChange();
            // OnSelectionChange stops once Alibre ends the command (for example when the
            // user opens the sketch for editing), so keep following the selection.
            _poll.Start();
        }

        /// <summary>Brings the dialog to the front (used when the menu item is clicked again).</summary>
        public void Activate() => _form?.Activate();

        public void OnSelectionChange() => RefreshSelection(true);

        private void RefreshSelection(bool force)
        {
            if (_form == null || _terminated) return;
            try
            {
                UpdateSelection(force);
            }
            catch (Exception e)
            {
                // Show the failure: an exception passed back to Alibre is swallowed silently.
                _selectionKey = null;
                _form.SetStatus("Could not read the selection: " + e.Message);
            }
        }

        private void UpdateSelection(bool force)
        {
            List<string> others;
            var points = SelectedSketchPoints(out others);

            // The poll runs several times a second: only touch the dialog when the selection changes.
            var key = string.Join("|", points.ConvertAll(p => p.Key)) + "#" + string.Join("|", others);
            if (!force && key == _selectionKey) return;
            _selectionKey = key;

            // Anything but a usable pair only updates the status: points the user has typed stay put.
            if (points.Count != 2)
            {
                var status = string.Format(CultureInfo.CurrentCulture,
                    "Select two sketch points or one sketch line, or type the points ({0} points selected).", points.Count);
                // Name anything else that is selected, so an unexpected target type is visible.
                if (others.Count > 0) status += " Also selected: " + string.Join(", ", others);
                _form.SetStatus(status);
                return;
            }

            if (!string.Equals(points[0].SketchName, points[1].SketchName, StringComparison.Ordinal))
            {
                _form.SetStatus("The two points must be in the same sketch.");
                return;
            }

            _form.SetSelectedPoints(ToDisplay(points[0].X), ToDisplay(points[0].Y),
                ToDisplay(points[1].X), ToDisplay(points[1].Y),
                points[0].SketchName, "From selection in " + points[0].SketchName + ". You can edit the values.");
        }

        /// <summary>
        /// Equation of the parabola for the given end points and height (display units).
        /// With z1 and z2 the parabola is 3D; without them it lies in a 2D sketch.
        /// </summary>
        internal string DescribeEquation(double x1, double y1, double? z1, double x2, double y2, double? z2,
            double heightDisplay) =>
            Geometry(x1, y1, z1, x2, y2, z2, heightDisplay).EquationText(ToDisplay, Units.Suffix(_displayUnits));

        /// <summary>
        /// Adds the parabola to the target sketch, creating that sketch first if it is a new one.
        /// Coordinates and height in display units. Returns the sketch actually used, so further
        /// parabolas can go into the same one.
        /// </summary>
        internal SketchTarget CreateParabola(SketchTarget target, double x1, double y1, double? z1,
            double x2, double y2, double? z2, double heightDisplay)
        {
            var geometry = Geometry(x1, y1, z1, x2, y2, z2, heightDisplay);
            if (geometry.Is3D != target.Is3D)
                throw new InvalidOperationException(geometry.Is3D
                    ? "Points with Z values need a 3D sketch."
                    : "Points without Z values need a 2D sketch.");

            _site?.StartChangingModel();
            try
            {
                return geometry.Is3D ? AddTo3DSketch(target, geometry) : AddTo2DSketch(target, geometry);
            }
            finally
            {
                _site?.StopChangingModel();
                _site?.UpdateCanvas();
            }
        }

        private ParabolaGeometry Geometry(double x1, double y1, double? z1, double x2, double y2, double? z2,
            double heightDisplay)
        {
            if (z1.HasValue != z2.HasValue)
                throw new ArgumentException("Enter Z for both points, or leave both empty for a 2D sketch.");
            return z1.HasValue
                ? ParabolaGeometry.InSpace(ToModel(x1), ToModel(y1), ToModel(z1.Value),
                    ToModel(x2), ToModel(y2), ToModel(z2.Value), ToModel(heightDisplay))
                : ParabolaGeometry.InPlane(ToModel(x1), ToModel(y1), ToModel(x2), ToModel(y2), ToModel(heightDisplay));
        }

        private SketchTarget AddTo2DSketch(SketchTarget target, ParabolaGeometry geometry)
        {
            var sketch = target.IsNew ? NewSketch(target.Plane) : FindSketch(target.Name);
            if (sketch == null) throw new InvalidOperationException("Sketch '" + target.Name + "' no longer exists.");

            var began = BeginChange(sketch.BeginChange);
            try
            {
                var figures = sketch.Figures;
                AddSpline(geometry,
                    (control, knots, weights) => figures.AddBspline(ParabolaGeometry.Order,
                        ParabolaGeometry.ControlPointCount, ref control, ref knots, ref weights),
                    samples => figures.AddBsplineByInterpolation(ref samples));
            }
            finally
            {
                if (began) sketch.EndChange();
            }

            return SketchTarget.Existing2D(sketch.Name);
        }

        private SketchTarget AddTo3DSketch(SketchTarget target, ParabolaGeometry geometry)
        {
            var sketch = target.IsNew ? New3DSketch() : Find3DSketch(target.Name);
            if (sketch == null) throw new InvalidOperationException("3D sketch '" + target.Name + "' no longer exists.");

            var began = BeginChange(sketch.BeginChange);
            try
            {
                var figures = sketch.Figures;
                AddSpline(geometry,
                    (control, knots, weights) => figures.AddBspline(ParabolaGeometry.Order,
                        ParabolaGeometry.ControlPointCount, ref control, ref knots, ref weights),
                    samples => figures.AddBsplineByInterpolation(ref samples));
            }
            finally
            {
                if (began) sketch.EndChange();
            }

            return SketchTarget.Existing3D(sketch.Name);
        }

        /// <summary>
        /// The sketches a parabola can go into, for the dialog's list: the existing 2D (or 3D)
        /// sketches, then the choice of creating a new one.
        /// </summary>
        internal List<SketchTarget> SketchTargets(bool threeD)
        {
            var targets = new List<SketchTarget>();
            try
            {
                if (threeD)
                    foreach (var sketch in Sketches3D()) targets.Add(SketchTarget.Existing3D(sketch.Name));
                else
                    foreach (var sketch in Sketches()) targets.Add(SketchTarget.Existing2D(sketch.Name));
            }
            catch (COMException)
            {
            }

            if (threeD) targets.Add(SketchTarget.New3D());
            else
                foreach (PrimaryPlane plane in Enum.GetValues(typeof(PrimaryPlane)))
                    targets.Add(SketchTarget.New2D(plane));
            return targets;
        }

        /// <summary>
        /// The sketch to offer first: the one open for editing, else the only one there is,
        /// else a new one (so a parabola can be made in a part with no sketches yet).
        /// </summary>
        internal SketchTarget DefaultSketchTarget(bool threeD)
        {
            var existing = new List<SketchTarget>();
            try
            {
                if (threeD)
                {
                    foreach (var sketch in Sketches3D())
                    {
                        if (sketch.IsActive) return SketchTarget.Existing3D(sketch.Name);
                        existing.Add(SketchTarget.Existing3D(sketch.Name));
                    }
                }
                else
                {
                    foreach (var sketch in Sketches())
                    {
                        if (sketch.IsActive) return SketchTarget.Existing2D(sketch.Name);
                        existing.Add(SketchTarget.Existing2D(sketch.Name));
                    }
                }
            }
            catch (COMException)
            {
            }

            if (existing.Count == 1) return existing[0];
            return threeD ? SketchTarget.New3D() : SketchTarget.New2D(PrimaryPlane.XY);
        }

        private IADSketch FindSketch(string name)
        {
            foreach (var sketch in Sketches())
                if (string.Equals(sketch.Name, name, StringComparison.Ordinal)) return sketch;
            return null;
        }

        private IAD3DSketch Find3DSketch(string name)
        {
            foreach (var sketch in Sketches3D())
                if (string.Equals(sketch.Name, name, StringComparison.Ordinal)) return sketch;
            return null;
        }

        private IEnumerable<IADSketch> Sketches()
        {
            var sketches = (Session as IADPartSession)?.Sketches;
            if (sketches == null) yield break;
            foreach (IADSketch sketch in sketches) yield return sketch;
        }

        private IEnumerable<IAD3DSketch> Sketches3D()
        {
            var sketches = (Session as IADPartSession)?.Sketches3D;
            if (sketches == null) yield break;
            foreach (IAD3DSketch sketch in sketches) yield return sketch;
        }

        /// <summary>A new 2D sketch on one of the part's primary design planes; Alibre names it.</summary>
        private IADSketch NewSketch(PrimaryPlane plane)
        {
            var part = Session as IADPartSession;
            if (part == null) throw new InvalidOperationException("Sketches can only be created in a part.");

            // AddSketch discards unsaved changes to a sketch that is open for editing.
            foreach (var sketch in Sketches())
                if (sketch.IsActive)
                    throw new InvalidOperationException("Close the sketch '" + sketch.Name +
                        "' first, or choose it as the target: creating a new sketch would discard its unsaved changes.");

            return part.Sketches.AddSketch(null, FindPrimaryPlane(part, plane), string.Empty);
        }

        /// <summary>The built-in XY, YZ or ZX plane, recognised by its normal as in the API documentation.</summary>
        private static IADDesignPlane FindPrimaryPlane(IADPartSession part, PrimaryPlane plane)
        {
            foreach (IADDesignPlane candidate in ((IADDesignSession) part).DesignPlanes)
            {
                if (candidate.PlaneType != ADDesignGeometryType.AD_PRIMARY_PLANE) continue;
                var normal = candidate.Normal;
                var found = Math.Abs(normal.Z) > 0.5 ? PrimaryPlane.XY
                    : Math.Abs(normal.Y) > 0.5 ? PrimaryPlane.ZX
                    : PrimaryPlane.YZ;
                if (found == plane) return candidate;
            }

            throw new InvalidOperationException("Could not find the " + plane + "-Plane.");
        }

        private IAD3DSketch New3DSketch()
        {
            var part = Session as IADPartSession;
            if (part == null) throw new InvalidOperationException("3D sketches can only be created in a part.");

            var names = new HashSet<string>();
            foreach (var sketch in Sketches3D()) names.Add(sketch.Name);
            var name = "Parabola 3D Sketch";
            for (var i = 2; names.Contains(name); i++) name = "Parabola 3D Sketch " + i;
            return part.Sketches3D.Add3DSketch(name);
        }

        /// <summary>
        /// Opens a sketch for editing. Returns false when it is already open in the GUI,
        /// in which case figures go straight in and EndChange is not ours to call.
        /// </summary>
        private static bool BeginChange(Action beginChange)
        {
            try
            {
                beginChange();
                return true;
            }
            catch (COMException e) when (e.ErrorCode == ActiveSketchSessionExists)
            {
                return false;
            }
        }

        private delegate object AddBspline(Array control, Array knots, Array weights);

        /// <summary>
        /// Adds the parabola through the given 2D or 3D figure collection's methods: exactly,
        /// as a quadratic B-spline, falling back to a spline interpolated through sample points.
        /// </summary>
        private static void AddSpline(ParabolaGeometry geometry, AddBspline addBspline, Func<Array, object> addByInterpolation)
        {
            try
            {
                if (addBspline(geometry.BsplineControlPoints(), geometry.Knots(), geometry.Weights()) != null)
                    return;
            }
            catch (COMException)
            {
                // Fall through to interpolation.
            }

            if (addByInterpolation(geometry.Sample(InterpolationSamples)) == null)
                throw new InvalidOperationException("Alibre did not create the spline.");
        }

        private void EnableSketchFigureSelection()
        {
            try
            {
                _site?.EnableSketchFigureSelection(true);
            }
            catch (Exception e)
            {
                _form?.SetStatus("Could not enable sketch figure selection: " + e.Message);
            }
        }

        /// <summary>
        /// The live session, looked up again by identifier as Alibre's
        /// SelectionAddOnSample does, rather than a reference kept from start-up.
        /// </summary>
        private IADSession Session
        {
            get
            {
                try
                {
                    return _root.Sessions.Item(_sessionIdentifier) ?? _session;
                }
                catch (COMException)
                {
                    return _session;
                }
            }
        }

        private List<PickedPoint> SelectedSketchPoints(out List<string> others)
        {
            var points = new List<PickedPoint>();
            others = new List<string>();
            IObjectCollector selected;
            try
            {
                selected = Session?.SelectedObjects;
            }
            catch (COMException)
            {
                return points;
            }

            if (selected == null) return points;

            // Index (0-based) rather than foreach, as the Alibre sample and the sibling
            // add-ons do. Selection order decides Point 1 / Point 2.
            var count = selected.Count;
            for (var i = 0; i < count; i++)
            {
                var item = selected.Item(i);
                if (item == null) continue;
                // The collector holds IADTargetProxy wrappers around the selected figures.
                var target = (item as IADTargetProxy)?.Target ?? item;

                // A line stands for its two end points, Start then End.
                var line = ToSketchLine(target);
                if (line != null)
                {
                    points.AddRange(EndPoints(line));
                    continue;
                }

                string description;
                var point = ToSketchPoint(target, out description);
                if (point != null) points.Add(PickedPoint.From(point));
                else others.Add(description);
            }

            return points;
        }

        /// <summary>
        /// The selected object as a sketch line, or null. Like points, a line may arrive
        /// only as an IADSketchFigure (FigureType AD_LINE), so fall back to fetching it by ID.
        /// </summary>
        private static IADSketchLine ToSketchLine(object target)
        {
            var direct = target as IADSketchLine;
            if (direct != null) return direct;

            var figure = target as IADSketchFigure;
            if (figure == null || figure.FigureType != ADGeometryType.AD_LINE) return null;
            return figure.Sketch?.Figures?.GetFigureByID(figure.ID) as IADSketchLine;
        }

        /// <summary>
        /// The line's end points, Start then End. These are transient objects, not figures
        /// of the sketch: Alibre releases them almost at once and cannot answer ID or Sketch
        /// for them. So read only X and Y, straight away, and take the ID and sketch from the line.
        /// </summary>
        private static PickedPoint[] EndPoints(IADSketchLine line)
        {
            var id = line.ID;
            var sketch = line.Sketch;
            try
            {
                return EndPoints(line, id, sketch);
            }
            catch (COMException)
            {
                // The selected line itself may be a stale wrapper; ask the sketch for it afresh.
                var fresh = sketch?.Figures?.GetFigureByID(id) as IADSketchLine;
                if (fresh == null) throw;
                return EndPoints(fresh, id, sketch);
            }
        }

        private static PickedPoint[] EndPoints(IADSketchLine line, string id, IADSketch sketch)
        {
            var start = line.Start;
            var startX = start.X;
            var startY = start.Y;
            var end = line.End;
            var endX = end.X;
            var endY = end.Y;
            return new[]
            {
                new PickedPoint(startX, startY, id + ":start", sketch),
                new PickedPoint(endX, endY, id + ":end", sketch)
            };
        }

        /// <summary>
        /// A selected sketch figure arrives as an IADSketchFigure; a point is one whose
        /// FigureType is AD_POINT. If the object does not answer IADSketchPoint directly,
        /// fetch the same figure from its sketch by ID.
        /// </summary>
        private static IADSketchPoint ToSketchPoint(object target, out string description)
        {
            description = null;
            var direct = target as IADSketchPoint;
            if (direct != null) return direct;

            var figure = target as IADSketchFigure;
            if (figure == null)
            {
                description = target is IADSketch ? "whole sketch" : "non-sketch object";
                return null;
            }

            var type = figure.FigureType;
            if (type != ADGeometryType.AD_POINT)
            {
                description = type.ToString().Replace("AD_", string.Empty).Replace('_', ' ').ToLowerInvariant();
                return null;
            }

            var point = figure.Sketch?.Figures?.GetFigureByID(figure.ID) as IADSketchPoint;
            if (point == null) description = "point (not readable as IADSketchPoint)";
            return point;
        }

        private double ToDisplay(double model) => Units.Convert(model, _modelUnits, _displayUnits);

        private double ToModel(double display) => Units.Convert(display, _displayUnits, _modelUnits);

        /// <summary>The dialog has closed: end the Alibre command too, if it is still running.</summary>
        private void Terminate()
        {
            if (_terminated) return;
            _terminated = true;
            _poll.Stop();
            _poll.Dispose();

            var site = _site;
            _site = null;
            try
            {
                site?.Terminate();
            }
            catch (COMException)
            {
            }

            var form = _form;
            _form = null;
            if (form != null && !form.IsDisposed) form.Close();
            _onTerminated?.Invoke(this);
        }

        /// <summary>
        /// Alibre ends the command whenever another one starts, including opening a sketch
        /// for editing. Keep the dialog open; the poll timer keeps following the selection.
        /// </summary>
        public void OnTerminate() => _site = null;

        /// <summary>True while the document this dialog belongs to is still open.</summary>
        private bool SessionIsOpen()
        {
            foreach (IADSession session in _root.Sessions)
                if (session.Identifier == _sessionIdentifier) return true;
            return false;
        }

        private void OnPoll()
        {
            if (_terminated || _form == null) return;
            try
            {
                if (!SessionIsOpen())
                {
                    Terminate();
                    return;
                }
            }
            catch (COMException)
            {
                return;
            }

            RefreshSelection(false);
        }

        public bool IsTwoWayToggle() => false;

        public bool AddTab() => false;

        public string TabName => string.Empty;

        public Array Extents => null;

        public void OnShowUI(long hWnd)
        {
        }

        public void OnRender(int hDC, int clipRectX, int clipRectY, int clipRectWidth, int clipRectHeight)
        {
        }

        public void On3DRender()
        {
        }

        // Pass all input through so Alibre's own selection keeps working.
        public bool OnClick(int screenX, int screenY, ADDONMouseButtons buttons) => false;

        public bool OnDoubleClick(int screenX, int screenY) => false;

        public bool OnMouseDown(int screenX, int screenY, ADDONMouseButtons buttons) => false;

        public bool OnMouseMove(int screenX, int screenY, ADDONMouseButtons buttons) => false;

        public bool OnMouseUp(int screenX, int screenY, ADDONMouseButtons buttons) => false;

        public bool OnMouseWheel(double delta) => false;

        public bool OnKeyDown(int keycode) => false;

        public bool OnKeyUp(int keycode) => false;

        public bool OnEscape() => false;

        /// <summary>
        /// A selected point, copied out of Alibre when it is read: the COM objects behind
        /// a selection (line end points especially) do not stay valid for later queries.
        /// </summary>
        private sealed class PickedPoint
        {
            public PickedPoint(double x, double y, string key, IADSketch sketch)
            {
                X = x;
                Y = y;
                Key = key;
                SketchName = sketch?.Name;
            }

            public static PickedPoint From(IADSketchPoint point) =>
                new PickedPoint(point.X, point.Y, point.ID, point.Sketch);

            public double X { get; }
            public double Y { get; }

            /// <summary>Identifies the point for change detection.</summary>
            public string Key { get; }

            public string SketchName { get; }
        }

        private sealed class WindowHandle : IWin32Window
        {
            public WindowHandle(IntPtr handle)
            {
                Handle = handle;
            }

            public IntPtr Handle { get; }
        }
    }
}