using System;
using System.Globalization;
using AlibreX;

namespace AlibreParabolaAddon
{
    /// <summary>
    /// A parabolic arc from P1 to P2 whose apex sits a signed height H off the
    /// chord midpoint, along a unit "height direction" n perpendicular to the chord.
    /// In a 2D sketch n is the left normal of P1→P2, so positive H puts the apex to
    /// the left. In 3D n is the upward (+Z) direction square to the chord, so the
    /// parabola lies in the vertical plane through P1 and P2 (+X if the chord is vertical).
    /// All values are in one length unit (the caller's choice; normally model units).
    /// </summary>
    internal sealed class ParabolaGeometry
    {
        private const double Epsilon = 1e-9;

        private readonly double[] _p1, _p2, _mid;
        private readonly double[] _u;   // unit chord direction
        private readonly double[] _n;   // unit height direction

        private ParabolaGeometry(double[] p1, double[] p2, double height, bool is3D)
        {
            _p1 = p1;
            _p2 = p2;
            Height = height;
            Is3D = is3D;

            var d = Sub(p2, p1);
            Length = Norm(d);
            if (Length < Epsilon) throw new ArgumentException("The two points are coincident.");
            if (Math.Abs(height) < Epsilon) throw new ArgumentException("The height must not be zero.");

            _u = Scale(d, 1 / Length);
            _mid = Scale(Add(p1, p2), 0.5);
            _n = is3D ? UpSquareTo(_u) : new[] { -_u[1], _u[0], 0 };
        }

        /// <summary>A parabola in a 2D sketch.</summary>
        public static ParabolaGeometry InPlane(double x1, double y1, double x2, double y2, double height) =>
            new ParabolaGeometry(new[] { x1, y1, 0.0 }, new[] { x2, y2, 0.0 }, height, false);

        /// <summary>A parabola in a 3D sketch.</summary>
        public static ParabolaGeometry InSpace(double x1, double y1, double z1, double x2, double y2, double z2, double height) =>
            new ParabolaGeometry(new[] { x1, y1, z1 }, new[] { x2, y2, z2 }, height, true);

        /// <summary>+Z with its component along the chord removed; +X when the chord is vertical.</summary>
        private static double[] UpSquareTo(double[] u)
        {
            foreach (var axis in new[] { new double[] { 0, 0, 1 }, new double[] { 1, 0, 0 } })
            {
                var v = Sub(axis, Scale(u, Dot(axis, u)));
                var length = Norm(v);
                if (length > 1e-6) return Scale(v, 1 / length);
            }
            throw new InvalidOperationException("No height direction square to the chord.");
        }

        public bool Is3D { get; }
        public double Height { get; }
        public double Length { get; }
        public double MidX => _mid[0];
        public double MidY => _mid[1];
        public double ApexX => _mid[0] + Height * _n[0];
        public double ApexY => _mid[1] + Height * _n[1];
        public double ApexZ => _mid[2] + Height * _n[2];

        /// <summary>Coordinates per point in the arrays passed to Alibre: 2 or 3.</summary>
        public int Dimension => Is3D ? 3 : 2;

        /// <summary>Chord angle from the sketch X axis, in radians (2D).</summary>
        public double AngleRad => Math.Atan2(_u[1], _u[0]);

        /// <summary>a in y′ = a·x′² + H (chord-local frame, origin at the chord midpoint).</summary>
        public double Coefficient => -4 * Height / (Length * Length);

        /// <summary>True when the chord is parallel to the sketch X axis (2D).</summary>
        public bool IsChordHorizontal => !Is3D && Math.Abs(_u[1]) < Epsilon;

        /// <summary>
        /// The arc is exactly a quadratic Bézier P1, C, P2 with C = M + 2H·n.
        /// A knot is inserted at 0.5 so there are more control points than the order.
        /// </summary>
        public const int Order = 3;
        public const int ControlPointCount = 4;

        public double[] BsplineControlPoints()
        {
            var c = Add(_mid, Scale(_n, 2 * Height));
            return Flatten(_p1, Scale(Add(_p1, c), 0.5), Scale(Add(c, _p2), 0.5), _p2);
        }

        public double[] Knots() => new[] { 0, 0, 0, 0.5, 1, 1, 1 };

        public double[] Weights() => new double[] { 1, 1, 1, 1 };

        /// <summary>n points evenly spaced along the chord, as interleaved X, Y (and Z in 3D).</summary>
        public double[] Sample(int n)
        {
            if (n < 3) n = 3;
            var points = new double[n][];
            for (var i = 0; i < n; i++)
            {
                var s = -Length / 2 + Length * i / (n - 1);   // local x′
                var h = Coefficient * s * s + Height;         // local y′
                points[i] = Add(_mid, Add(Scale(_u, s), Scale(_n, h)));
            }
            return Flatten(points);
        }

        /// <summary>Human-readable equation, with lengths converted by <paramref name="toDisplay"/>.</summary>
        public string EquationText(Func<double, double> toDisplay, string unit)
        {
            var len = toDisplay(Length);
            var h = toDisplay(Height);
            // a scales as 1/length, so convert it via a unit length.
            var a = Coefficient / toDisplay(1.0);

            if (Is3D)
            {
                return string.Format(CultureInfo.CurrentCulture,
                    "y′ = {0:G6}·x′² + {1:G6}   [{2}]\r\nx′ along chord from midpoint ({3:G6}, {4:G6}, {5:G6}); y′ along ({6:G4}, {7:G4}, {8:G4})\r\nVertex ({9:G6}, {10:G6}, {11:G6}), span {12:G6}",
                    a, h, unit, toDisplay(_mid[0]), toDisplay(_mid[1]), toDisplay(_mid[2]), _n[0], _n[1], _n[2],
                    toDisplay(ApexX), toDisplay(ApexY), toDisplay(ApexZ), len);
            }

            if (IsChordHorizontal)
            {
                // In sketch coordinates the sign follows the apex side, not H
                // (a right-to-left chord puts positive H below the chord).
                var aGlobal = -4 * (ApexY - MidY) / (Length * Length) / toDisplay(1.0);
                return string.Format(CultureInfo.CurrentCulture,
                    "y = {0:G6}·(x − {1:G6})² + {2:G6}   [{3}]\r\nVertex ({1:G6}, {2:G6}), span {4:G6}",
                    aGlobal, toDisplay(MidX), toDisplay(ApexY), unit, len);
            }

            return string.Format(CultureInfo.CurrentCulture,
                "y′ = {0:G6}·x′² + {1:G6}   [{2}]\r\nx′ along chord from midpoint ({3:G6}, {4:G6}) at {5:G6}°\r\nVertex ({6:G6}, {7:G6}), span {8:G6}",
                a, h, unit, toDisplay(MidX), toDisplay(MidY), AngleRad * 180 / Math.PI,
                toDisplay(ApexX), toDisplay(ApexY), len);
        }

        private double[] Flatten(params double[][] points)
        {
            var dimension = Dimension;
            var result = new double[points.Length * dimension];
            for (var i = 0; i < points.Length; i++)
                Array.Copy(points[i], 0, result, i * dimension, dimension);
            return result;
        }

        private static double[] Add(double[] a, double[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };

        private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };

        private static double[] Scale(double[] a, double k) => new[] { a[0] * k, a[1] * k, a[2] * k };

        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        private static double Norm(double[] a) => Math.Sqrt(Dot(a, a));
    }

    /// <summary>Length unit conversions between Alibre's ADUnits.</summary>
    internal static class Units
    {
        public static double ToMm(ADUnits units)
        {
            switch (units)
            {
                case ADUnits.AD_MILLIMETERS: return 1;
                case ADUnits.AD_CENTIMETERS: return 10;
                case ADUnits.AD_METERS: return 1000;
                case ADUnits.AD_INCHES: return 25.4;
                case ADUnits.AD_FEET: return 304.8;
                case ADUnits.AD_FEET_INCHES: return 25.4;   // shown as decimal inches
                default: return 10;                         // model default is cm
            }
        }

        public static double Convert(double value, ADUnits from, ADUnits to) => value * ToMm(from) / ToMm(to);

        public static string Suffix(ADUnits units)
        {
            switch (units)
            {
                case ADUnits.AD_MILLIMETERS: return "mm";
                case ADUnits.AD_CENTIMETERS: return "cm";
                case ADUnits.AD_METERS: return "m";
                case ADUnits.AD_INCHES:
                case ADUnits.AD_FEET_INCHES: return "in";
                case ADUnits.AD_FEET: return "ft";
                default: return "cm";
            }
        }
    }
}
