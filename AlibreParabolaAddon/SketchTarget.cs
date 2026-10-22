namespace AlibreParabolaAddon
{
    /// <summary>The primary design planes a new 2D sketch can be placed on.</summary>
    internal enum PrimaryPlane
    {
        XY,
        YZ,
        ZX
    }

    /// <summary>
    /// Where the parabola goes: an existing 2D or 3D sketch (by name), or a new
    /// sketch to be created (a 2D one on a primary plane, or a 3D one).
    /// </summary>
    internal sealed class SketchTarget
    {
        private SketchTarget(string name, bool is3D, bool isNew, PrimaryPlane plane)
        {
            Name = name;
            Is3D = is3D;
            IsNew = isNew;
            Plane = plane;
        }

        public static SketchTarget Existing2D(string name) => new SketchTarget(name, false, false, PrimaryPlane.XY);

        public static SketchTarget Existing3D(string name) => new SketchTarget(name, true, false, PrimaryPlane.XY);

        public static SketchTarget New2D(PrimaryPlane plane) => new SketchTarget(null, false, true, plane);

        public static SketchTarget New3D() => new SketchTarget(null, true, true, PrimaryPlane.XY);

        /// <summary>The sketch name; null for a sketch still to be created.</summary>
        public string Name { get; }

        public bool Is3D { get; }
        public bool IsNew { get; }

        /// <summary>The plane for a new 2D sketch.</summary>
        public PrimaryPlane Plane { get; }

        public override string ToString()
        {
            if (!IsNew) return Name;
            return Is3D ? "<New 3D sketch>" : "<New sketch on " + Plane + "-Plane>";
        }

        public override bool Equals(object obj)
        {
            var other = obj as SketchTarget;
            return other != null && other.Is3D == Is3D && other.IsNew == IsNew
                   && (IsNew ? other.Plane == Plane : other.Name == Name);
        }

        public override int GetHashCode() => ToString().GetHashCode();
    }
}
