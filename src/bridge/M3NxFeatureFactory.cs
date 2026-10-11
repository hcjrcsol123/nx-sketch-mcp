using System;
using System.Collections.Generic;
using System.Globalization;
using NXOpen;
using NXOpen.Features;

// Small NXOpen construction helpers used by the M3 operation adapter.  These
// helpers intentionally use only the creation patterns exercised by the
// P02/P04 probes; transaction marks, updates, and error recovery belong to the
// adapter that calls them.
public enum M3SketchPlane
{
    XY,
    XZ,
    YZ
}

public enum M3ExtrudeExtent
{
    Finite,
    ThroughAll
}

public enum M3ExtrudeDirection
{
    Positive,
    Negative
}

public enum M3BooleanMode
{
    Create,
    Unite,
    Subtract
}

public sealed class M3LineResult
{
    public Line Geometry;
    public SketchDimensionalConstraint[] DrivingDimensions;
}

public sealed class M3RectangleResult
{
    public Line[] Geometry;
    public SketchDimensionalConstraint[] DrivingDimensions;
}

public sealed class M3CircleResult
{
    public Arc Geometry;
    public SketchDimensionalConstraint[] DrivingDimensions;
}

public static class M3NxFeatureFactory
{
    private const double AxisTolerance = 1e-9;
    private const double PointTolerance = 1e-7;
    private const double Pi = Math.PI;

    private sealed class SketchFrame
    {
        internal Point3d Origin;
        internal Vector3d XAxis;
        internal Vector3d YAxis;
        internal Vector3d Normal;
    }

    /// <summary>Creates and activates an origin-based sketch on a principal plane.</summary>
    public static Sketch CreateSketch(Part part, M3SketchPlane plane)
    {
        return CreateSketch(part, plane, new Point3d(0.0, 0.0, 0.0));
    }

    /// <summary>Creates and activates a sketch on a principal plane at a world-space origin.</summary>
    public static Sketch CreateSketch(Part part, M3SketchPlane plane, Point3d origin)
    {
        RequirePart(part);
        RequireFinite(origin.X, "origin X");
        RequireFinite(origin.Y, "origin Y");
        RequireFinite(origin.Z, "origin Z");
        Vector3d u, v, normal;
        GetAxes(plane, out u, out v, out normal);

        Plane sketchPlane = part.Planes.CreatePlane(origin, normal, SmartObject.UpdateOption.WithinModeling);
        Direction axisReference = part.Directions.CreateDirection(origin, u, SmartObject.UpdateOption.WithinModeling);
        Point sketchOrigin = part.Points.CreatePoint(origin);

        SketchInPlaceBuilder builder = part.Sketches.CreateSketchInPlaceBuilder2(null);
        try
        {
            builder.PlaneReference = sketchPlane;
            builder.AxisReference = axisReference;
            builder.SketchOrigin = sketchOrigin;
            builder.PlaneOption = Sketch.PlaneOption.ExistingPlane;
            builder.OriginOption = OriginMethod.SpecifyPoint;
            Sketch sketch = (Sketch)builder.Commit();
            sketch.Activate(Sketch.ViewReorient.False);
            return sketch;
        }
        finally
        {
            builder.Destroy();
        }
    }

    /// <summary>Creates a sketch whose smart support plane is associatively coincident with one planar face.</summary>
    public static Sketch CreateSketchOnFace(Part part, Face face, Point3d origin,
        Vector3d xAxis, Vector3d normal, out Plane supportPlane)
    {
        RequirePart(part);
        if (face == null) throw new ArgumentNullException("face");
        RequireFinite(origin.X, "origin X");
        RequireFinite(origin.Y, "origin Y");
        RequireFinite(origin.Z, "origin Z");
        double normalLength = Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z);
        if (normalLength <= AxisTolerance) throw new ArgumentException("face normal must be nonzero");
        normal = new Vector3d(normal.X / normalLength, normal.Y / normalLength, normal.Z / normalLength);
        double projection = xAxis.X * normal.X + xAxis.Y * normal.Y + xAxis.Z * normal.Z;
        xAxis = new Vector3d(xAxis.X - projection * normal.X, xAxis.Y - projection * normal.Y, xAxis.Z - projection * normal.Z);
        double xLength = Math.Sqrt(xAxis.X * xAxis.X + xAxis.Y * xAxis.Y + xAxis.Z * xAxis.Z);
        if (xLength <= AxisTolerance) throw new ArgumentException("face reference axis must not be parallel to its normal");
        xAxis = new Vector3d(xAxis.X / xLength, xAxis.Y / xLength, xAxis.Z / xLength);

        supportPlane = part.Planes.CreatePlane(PlaneTypes.MethodType.Coincident,
            PlaneTypes.AlternateType.One, origin, normal, null, false, false, new NXObject[] { face });
        Direction axisReference = part.Directions.CreateDirection(origin, xAxis, SmartObject.UpdateOption.WithinModeling);
        Point sketchOrigin = part.Points.CreatePoint(origin);
        SketchInPlaceBuilder builder = part.Sketches.CreateSketchInPlaceBuilder2(null);
        try
        {
            builder.PlaneReference = supportPlane;
            builder.AxisReference = axisReference;
            builder.SketchOrigin = sketchOrigin;
            builder.PlaneOption = Sketch.PlaneOption.ExistingPlane;
            builder.OriginOption = OriginMethod.SpecifyPoint;
            Sketch sketch = (Sketch)builder.Commit();
            sketch.Activate(Sketch.ViewReorient.False);
            return sketch;
        }
        finally
        {
            builder.Destroy();
        }
    }

    /// <summary>
    /// Creates a line in sketch-local coordinates. Axis-aligned lines use one
    /// driving span dimension; diagonal lines use two driving projected spans.
    /// </summary>
    public static M3LineResult CreateLine(Part part, Sketch sketch,
        double x1, double y1, double x2, double y2, string expressionPrefix)
    {
        RequireSketch(part, sketch);
        RequireFinite(x1, "x1"); RequireFinite(y1, "y1");
        RequireFinite(x2, "x2"); RequireFinite(y2, "y2");
        SketchFrame frame = GetSketchFrame(sketch);
        double dx = x2 - x1;
        double dy = y2 - y1;
        bool horizontal = Math.Abs(dy) <= AxisTolerance && Math.Abs(dx) > AxisTolerance;
        bool vertical = Math.Abs(dx) <= AxisTolerance && Math.Abs(dy) > AxisTolerance;
        if (Math.Abs(dx) <= AxisTolerance && Math.Abs(dy) <= AxisTolerance)
            throw new ArgumentException("A line must have nonzero length.");

        if ((horizontal && dx < 0.0) || (vertical && dy < 0.0))
        {
            double swapX = x1; x1 = x2; x2 = swapX;
            double swapY = y1; y1 = y2; y2 = swapY;
        }

        Line line = part.Curves.CreateLine(ToModelPoint(frame, x1, y1), ToModelPoint(frame, x2, y2));
        sketch.AddGeometry(line, Sketch.InferConstraintsOption.InferNoConstraints);
        if (horizontal)
            sketch.CreateHorizontalConstraint(Constraint(line, Sketch.ConstraintPointType.None));
        else if (vertical)
            sketch.CreateVerticalConstraint(Constraint(line, Sketch.ConstraintPointType.None));

        List<SketchDimensionalConstraint> dimensions = new List<SketchDimensionalConstraint>();
        if (NearZero(x1) && NearZero(y1))
            sketch.CreateCoincidentConstraint(Constraint(line, Sketch.ConstraintPointType.StartVertex),
                new Sketch.ConstraintGeometry(sketch.OriginPoint, Sketch.ConstraintPointType.None, 0));
        else
        {
            dimensions.Add(CreateCoordinateDimension(part, sketch, line, Sketch.AssocType.StartPoint,
                true, x1, ToModelPoint(frame, x1, y1 - 1.0), expressionPrefix, "x"));
            dimensions.Add(CreateCoordinateDimension(part, sketch, line, Sketch.AssocType.StartPoint,
                false, y1, ToModelPoint(frame, x1 - 1.0, y1), expressionPrefix, "y"));
        }
        if (horizontal || vertical)
        {
            Sketch.ConstraintType dimensionType = horizontal
                ? Sketch.ConstraintType.HorizontalDim
                : Sketch.ConstraintType.VerticalDim;
            SketchDimensionalConstraint length = CreateSpanDimension(part, sketch, line,
                dimensionType, horizontal ? Math.Abs(dx) : Math.Abs(dy),
                ToModelPoint(frame, (x1 + x2) / 2.0,
                    (y1 + y2) / 2.0 + (horizontal ? -1.0 : 0.0)),
                expressionPrefix, "length");
            dimensions.Add(length);
        }
        else
        {
            double middleX = (x1 + x2) / 2.0;
            double middleY = (y1 + y2) / 2.0;
            dimensions.Add(CreateProjectedSpanDimension(part, sketch, line, true, dx,
                ToModelPoint(frame, middleX, middleY - 1.0), expressionPrefix, "length_x"));
            dimensions.Add(CreateProjectedSpanDimension(part, sketch, line, false, dy,
                ToModelPoint(frame, middleX - 1.0, middleY), expressionPrefix, "length_y"));
        }

        RequireFullyConstrained(sketch);
        return new M3LineResult
        {
            Geometry = line,
            DrivingDimensions = dimensions.ToArray()
        };
    }

    /// <summary>
    /// Creates a closed rectangle from its lower-left local coordinate and
    /// positive width/height, with horizontal/vertical, coincident, and two
    /// native driving size dimensions.
    /// </summary>
    public static M3RectangleResult CreateRectangle(Part part, Sketch sketch,
        double x, double y, double width, double height, string expressionPrefix)
    {
        RequireSketch(part, sketch);
        RequireFinite(x, "x"); RequireFinite(y, "y");
        RequirePositiveFinite(width, "width");
        RequirePositiveFinite(height, "height");
        SketchFrame frame = GetSketchFrame(sketch);

        double right = x + width;
        double top = y + height;
        RequireFinite(right, "x+width"); RequireFinite(top, "y+height");
        Line bottom = part.Curves.CreateLine(ToModelPoint(frame, x, y), ToModelPoint(frame, right, y));
        Line rightSide = part.Curves.CreateLine(ToModelPoint(frame, right, y), ToModelPoint(frame, right, top));
        Line topSide = part.Curves.CreateLine(ToModelPoint(frame, right, top), ToModelPoint(frame, x, top));
        Line left = part.Curves.CreateLine(ToModelPoint(frame, x, top), ToModelPoint(frame, x, y));

        sketch.AddGeometry(bottom, Sketch.InferConstraintsOption.InferNoConstraints);
        sketch.AddGeometry(rightSide, Sketch.InferConstraintsOption.InferNoConstraints);
        sketch.AddGeometry(topSide, Sketch.InferConstraintsOption.InferNoConstraints);
        sketch.AddGeometry(left, Sketch.InferConstraintsOption.InferNoConstraints);

        sketch.CreateCoincidentConstraint(Constraint(bottom, Sketch.ConstraintPointType.EndVertex),
            Constraint(rightSide, Sketch.ConstraintPointType.StartVertex));
        sketch.CreateCoincidentConstraint(Constraint(rightSide, Sketch.ConstraintPointType.EndVertex),
            Constraint(topSide, Sketch.ConstraintPointType.StartVertex));
        sketch.CreateCoincidentConstraint(Constraint(topSide, Sketch.ConstraintPointType.EndVertex),
            Constraint(left, Sketch.ConstraintPointType.StartVertex));
        sketch.CreateCoincidentConstraint(Constraint(left, Sketch.ConstraintPointType.EndVertex),
            Constraint(bottom, Sketch.ConstraintPointType.StartVertex));
        sketch.CreateHorizontalConstraint(Constraint(bottom, Sketch.ConstraintPointType.None));
        sketch.CreateHorizontalConstraint(Constraint(topSide, Sketch.ConstraintPointType.None));
        sketch.CreateVerticalConstraint(Constraint(rightSide, Sketch.ConstraintPointType.None));
        sketch.CreateVerticalConstraint(Constraint(left, Sketch.ConstraintPointType.None));

        List<SketchDimensionalConstraint> dimensions = new List<SketchDimensionalConstraint>();
        if (NearZero(x) && NearZero(y))
            sketch.CreateCoincidentConstraint(Constraint(bottom, Sketch.ConstraintPointType.StartVertex),
                new Sketch.ConstraintGeometry(sketch.OriginPoint, Sketch.ConstraintPointType.None, 0));
        else
        {
            dimensions.Add(CreateCoordinateDimension(part, sketch, bottom, Sketch.AssocType.StartPoint,
                true, x, ToModelPoint(frame, x + Math.Max(width * 0.2, 1.0), y - 1.0), expressionPrefix, "x"));
            dimensions.Add(CreateCoordinateDimension(part, sketch, bottom, Sketch.AssocType.StartPoint,
                false, y, ToModelPoint(frame, x - 1.0, y + Math.Max(height * 0.2, 1.0)), expressionPrefix, "y"));
        }
        SketchDimensionalConstraint length = CreateSpanDimension(part, sketch, bottom,
            Sketch.ConstraintType.HorizontalDim, width,
            ToModelPoint(frame, x + width / 2.0, y - Math.Max(height * 0.2, 1.0)),
            expressionPrefix, "width");
        SketchDimensionalConstraint heightDimension = CreateSpanDimension(part, sketch, left,
            Sketch.ConstraintType.VerticalDim, height,
            ToModelPoint(frame, x - Math.Max(width * 0.2, 1.0), y + height / 2.0),
            expressionPrefix, "height");
        dimensions.Add(length);
        dimensions.Add(heightDimension);

        RequireFullyConstrained(sketch);
        return new M3RectangleResult
        {
            Geometry = new Line[] { bottom, rightSide, topSide, left },
            DrivingDimensions = dimensions.ToArray()
        };
    }

    /// <summary>Creates a full circle (0 through 2π) with a native driving diameter dimension.</summary>
    public static M3CircleResult CreateCircle(Part part, Sketch sketch,
        double centerX, double centerY, double diameter, string expressionPrefix)
    {
        RequireSketch(part, sketch);
        RequireFinite(centerX, "centerX"); RequireFinite(centerY, "centerY");
        RequirePositiveFinite(diameter, "diameter");
        SketchFrame frame = GetSketchFrame(sketch);

        Point3d center = ToModelPoint(frame, centerX, centerY);
        Arc circle = part.Curves.CreateArc(center, frame.XAxis, frame.YAxis, diameter / 2.0, 0.0, 2.0 * Pi);
        sketch.AddGeometry(circle, Sketch.InferConstraintsOption.InferNoConstraints);
        List<SketchDimensionalConstraint> dimensions = new List<SketchDimensionalConstraint>();
        if (NearZero(centerX) && NearZero(centerY))
            sketch.CreateCoincidentConstraint(Constraint(circle, Sketch.ConstraintPointType.ArcCenter),
                new Sketch.ConstraintGeometry(sketch.OriginPoint, Sketch.ConstraintPointType.None, 0));
        else
        {
            dimensions.Add(CreateCoordinateDimension(part, sketch, circle, Sketch.AssocType.ArcCenter,
                true, centerX, ToModelPoint(frame, centerX + Math.Max(diameter, 1.0), centerY - 1.0),
                expressionPrefix, "x"));
            dimensions.Add(CreateCoordinateDimension(part, sketch, circle, Sketch.AssocType.ArcCenter,
                false, centerY, ToModelPoint(frame, centerX - 1.0, centerY + Math.Max(diameter, 1.0)),
                expressionPrefix, "y"));
        }
        SketchDimensionalConstraint dimension = sketch.CreateDiameterDimension(
            new Sketch.DimensionGeometry { Geometry = circle, AssocType = Sketch.AssocType.None, AssocValue = 0 },
            ToModelPoint(frame, centerX + diameter, centerY + diameter), null);
        SetDimensionValue(part, dimension, diameter, expressionPrefix, "diameter");
        dimensions.Add(dimension);

        RequireFullyConstrained(sketch);
        return new M3CircleResult { Geometry = circle, DrivingDimensions = dimensions.ToArray() };
    }

    /// <summary>
    /// Extrudes one closed profile found in the Sketch. For Unite/Subtract,
    /// targetBody is required and is passed explicitly to NX SetTargetBodies.
    /// sectionHelpX/Y are sketch-local section help coordinates.
    /// </summary>
    public static Feature CreateExtrude(Part part, Sketch sketch, double sectionHelpX, double sectionHelpY,
        M3ExtrudeExtent extent, double finiteDistance,
        M3ExtrudeDirection direction, M3BooleanMode booleanMode, Body targetBody)
    {
        RequirePart(part);
        if (sketch == null) throw new ArgumentNullException("sketch");
        RequireFinite(sectionHelpX, "sectionHelpX"); RequireFinite(sectionHelpY, "sectionHelpY");
        if (sketch.Feature == null) throw new ArgumentException("Sketch has no associated feature.", "sketch");
        Curve[] profileCurves = GetSingleClosedProfile(sketch);
        SketchFrame frame = GetSketchFrame(sketch);
        Point3d sectionHelpPoint = ToModelPoint(frame, sectionHelpX, sectionHelpY);

        if (extent == M3ExtrudeExtent.Finite) RequirePositiveFinite(finiteDistance, "finiteDistance");
        else if (extent != M3ExtrudeExtent.ThroughAll) throw new ArgumentOutOfRangeException("extent");
        if (direction != M3ExtrudeDirection.Positive && direction != M3ExtrudeDirection.Negative)
            throw new ArgumentOutOfRangeException("direction");
        if (booleanMode != M3BooleanMode.Create && booleanMode != M3BooleanMode.Unite && booleanMode != M3BooleanMode.Subtract)
            throw new ArgumentOutOfRangeException("booleanMode");

        if (booleanMode == M3BooleanMode.Create)
        {
            if (targetBody != null) throw new ArgumentException("Create mode does not accept a target body.", "targetBody");
        }
        else
        {
            RequireTargetBody(part, targetBody);
        }

        Section section = part.Sections.CreateSection(0.0095, 0.01, 0.5);
        CurveFeatureRule curveRule = part.ScRuleFactory.CreateRuleCurveFeature(new Feature[] { sketch.Feature });
        section.AddToSection(new SelectionIntentRule[] { curveRule }, profileCurves[0], null, null,
            sectionHelpPoint, Section.Mode.Create);

        double sign = direction == M3ExtrudeDirection.Positive ? 1.0 : -1.0;
        Vector3d extrudeVector = new Vector3d(frame.Normal.X * sign, frame.Normal.Y * sign, frame.Normal.Z * sign);
        Direction extrudeDirection = part.Directions.CreateDirection(
            new Point3d(0.0, 0.0, 0.0), extrudeVector, SmartObject.UpdateOption.WithinModeling);

        ExtrudeBuilder builder = part.Features.CreateExtrudeBuilder(null);
        try
        {
            builder.Section = section;
            builder.Direction = extrudeDirection;
            builder.Limits.StartExtend.Value.RightHandSide = "0";
            if (extent == M3ExtrudeExtent.ThroughAll)
                builder.Limits.EndExtend.TrimType = NXOpen.GeometricUtilities.Extend.ExtendType.ThroughAll;
            else
                builder.Limits.EndExtend.Value.RightHandSide = NumberText(finiteDistance);

            if (booleanMode == M3BooleanMode.Create)
                builder.BooleanOperation.Type = NXOpen.GeometricUtilities.BooleanOperation.BooleanType.Create;
            else
            {
                builder.BooleanOperation.Type = booleanMode == M3BooleanMode.Unite
                    ? NXOpen.GeometricUtilities.BooleanOperation.BooleanType.Unite
                    : NXOpen.GeometricUtilities.BooleanOperation.BooleanType.Subtract;
                builder.BooleanOperation.SetTargetBodies(new Body[] { targetBody });
            }

            return builder.CommitFeature();
        }
        finally
        {
            builder.Destroy();
        }
    }

    private static SketchDimensionalConstraint CreateSpanDimension(Part part, Sketch sketch, Line line,
        Sketch.ConstraintType type, double value, Point3d helpPoint, string prefix, string suffix)
    {
        Sketch.AssocType first = Sketch.AssocType.StartPoint;
        Sketch.AssocType second = Sketch.AssocType.EndPoint;
        SketchDimensionalConstraint dimension = sketch.CreateDimension(type,
            Dimension(line, first), Dimension(line, second), helpPoint, null,
            Sketch.DimensionOption.CreateAsDriving);
        SetDimensionValue(part, dimension, value, prefix, suffix);
        return dimension;
    }

    private static SketchDimensionalConstraint CreateProjectedSpanDimension(Part part, Sketch sketch,
        Line line, bool horizontal, double delta, Point3d helpPoint, string prefix, string suffix)
    {
        Sketch.ConstraintType type = horizontal ? Sketch.ConstraintType.HorizontalDim : Sketch.ConstraintType.VerticalDim;
        Sketch.DimensionGeometry start = Dimension(line, Sketch.AssocType.StartPoint);
        Sketch.DimensionGeometry end = Dimension(line, Sketch.AssocType.EndPoint);
        SketchDimensionalConstraint dimension = delta < 0.0
            ? sketch.CreateDimension(type, end, start, helpPoint, null, Sketch.DimensionOption.CreateAsDriving)
            : sketch.CreateDimension(type, start, end, helpPoint, null, Sketch.DimensionOption.CreateAsDriving);
        SetDimensionValue(part, dimension, Math.Abs(delta), prefix, suffix);
        return dimension;
    }

    private static SketchDimensionalConstraint CreateCoordinateDimension(Part part, Sketch sketch,
        NXObject geometry, Sketch.AssocType geometryAssociation, bool horizontal, double coordinate,
        Point3d helpPoint, string prefix, string suffix)
    {
        Sketch.DimensionGeometry origin = new Sketch.DimensionGeometry
        {
            Geometry = sketch.OriginPoint,
            AssocType = Sketch.AssocType.Midpoint,
            AssocValue = 0
        };
        Sketch.DimensionGeometry target = new Sketch.DimensionGeometry
        {
            Geometry = geometry,
            AssocType = geometryAssociation,
            AssocValue = 0
        };
        Sketch.ConstraintType type = horizontal ? Sketch.ConstraintType.HorizontalDim : Sketch.ConstraintType.VerticalDim;
        SketchDimensionalConstraint dimension = coordinate < 0.0
            ? sketch.CreateDimension(type, target, origin, helpPoint, null, Sketch.DimensionOption.CreateAsDriving)
            : sketch.CreateDimension(type, origin, target, helpPoint, null, Sketch.DimensionOption.CreateAsDriving);
        SetDimensionValue(part, dimension, Math.Abs(coordinate), prefix, suffix);
        return dimension;
    }

    private static void SetDimensionValue(Part part, SketchDimensionalConstraint dimension,
        double value, string prefix, string suffix)
    {
        if (dimension == null || dimension.AssociatedExpression == null)
            throw new InvalidOperationException("NX did not return an expression for a driving sketch dimension.");
        dimension.AssociatedExpression.RightHandSide = NumberText(value);
        if (!String.IsNullOrWhiteSpace(prefix))
            part.Expressions.Rename(dimension.AssociatedExpression, ExpressionName(prefix, suffix));
        if (dimension.DimensionState != SketchDimensionalConstraint.DimensionStateType.Driving)
            throw new InvalidOperationException("NX did not create the requested sketch dimension as Driving.");
    }

    private static string ExpressionName(string prefix, string suffix)
    {
        string clean = String.Empty;
        for (int i = 0; i < prefix.Length; i++)
        {
            char c = prefix[i];
            bool valid = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') || c == '_';
            clean += valid ? c : '_';
        }
        if (clean.Length == 0 || (clean[0] >= '0' && clean[0] <= '9')) clean = "m3_" + clean;
        return clean + "_" + suffix;
    }

    private static Sketch.ConstraintGeometry Constraint(NXObject geometry, Sketch.ConstraintPointType pointType)
    {
        return new Sketch.ConstraintGeometry(geometry, pointType, 0);
    }

    private static Sketch.DimensionGeometry Dimension(Line line, Sketch.AssocType assocType)
    {
        return new Sketch.DimensionGeometry { Geometry = line, AssocType = assocType, AssocValue = 0 };
    }

    private static void RequireSketch(Part part, Sketch sketch)
    {
        RequirePart(part);
        if (sketch == null) throw new ArgumentNullException("sketch");
    }

    private static void RequirePart(Part part)
    {
        if (part == null) throw new ArgumentNullException("part");
    }

    private static void RequireTargetBody(Part part, Body targetBody)
    {
        if (targetBody == null) throw new ArgumentNullException("targetBody", "Unite and Subtract require an explicit target body.");
        Body[] bodies = part.Bodies.ToArray();
        foreach (Body body in bodies)
            if (body != null && body.Tag.Equals(targetBody.Tag)) return;
        throw new ArgumentException("The target body does not belong to the input Part.", "targetBody");
    }

    private static void RequirePositiveFinite(double value, string name)
    {
        if (Double.IsNaN(value) || Double.IsInfinity(value) || value <= 0.0)
            throw new ArgumentOutOfRangeException(name, "Value must be finite and greater than zero.");
    }

    private static void RequireFinite(double value, string name)
    {
        if (Double.IsNaN(value) || Double.IsInfinity(value))
            throw new ArgumentOutOfRangeException(name, "Value must be finite.");
    }

    private static bool NearZero(double value)
    {
        return Math.Abs(value) <= AxisTolerance;
    }

    private static void RequireFullyConstrained(Sketch sketch)
    {
        sketch.CalculateStatus();
        int degreesOfFreedom;
        Sketch.Status status = sketch.GetStatus(out degreesOfFreedom);
        if (status != Sketch.Status.WellConstrained || degreesOfFreedom != 0)
            throw new InvalidOperationException("M3 primitive did not leave the Sketch fully constrained (status=" +
                status.ToString() + ", dof=" + degreesOfFreedom.ToString(CultureInfo.InvariantCulture) + ").");
    }

    private static SketchFrame GetSketchFrame(Sketch sketch)
    {
        Point3d origin = sketch.Origin;
        Matrix3x3 axes = sketch.Orientation.Element;
        RequireFinite(origin.X, "sketch origin X");
        RequireFinite(origin.Y, "sketch origin Y");
        RequireFinite(origin.Z, "sketch origin Z");
        return new SketchFrame
        {
            Origin = origin,
            XAxis = new Vector3d(axes.Xx, axes.Xy, axes.Xz),
            YAxis = new Vector3d(axes.Yx, axes.Yy, axes.Yz),
            Normal = new Vector3d(axes.Zx, axes.Zy, axes.Zz)
        };
    }

    private static Curve[] GetSingleClosedProfile(Sketch sketch)
    {
        NXObject[] geometry = sketch.GetAllGeometry();
        if (geometry == null || geometry.Length == 0)
            throw new ArgumentException("Sketch has no geometry to extrude.", "sketch");

        List<Curve> curves = new List<Curve>();
        List<Line> lines = new List<Line>();
        List<Arc> arcs = new List<Arc>();
        foreach (NXObject item in geometry)
        {
            if (item == null) throw new ArgumentException("Sketch geometry contains null.", "sketch");
            Curve curve = item as Curve;
            if (curve == null)
                throw new ArgumentException("Extrude supports only line and full-circle sketch profiles.", "sketch");
            curves.Add(curve);
            Line line = item as Line;
            Arc arc = item as Arc;
            if (line != null) lines.Add(line);
            else if (arc != null) arcs.Add(arc);
            else throw new ArgumentException("Extrude supports only line and full-circle sketch profiles.", "sketch");
        }

        if (arcs.Count == 1 && lines.Count == 0 && curves.Count == 1)
        {
            double sweep = arcs[0].EndAngle - arcs[0].StartAngle;
            if (Math.Abs(Math.Abs(sweep) - 2.0 * Pi) <= 1e-7) return curves.ToArray();
            throw new ArgumentException("Arc extrudes require one complete 0-to-2π circle.", "sketch");
        }
        if (arcs.Count != 0 || lines.Count < 3)
            throw new ArgumentException("Extrude requires one closed loop of lines or one full circle.", "sketch");

        RequireSingleLineLoop(lines);
        return curves.ToArray();
    }

    private static void RequireSingleLineLoop(List<Line> lines)
    {
        List<Point3d> vertices = new List<Point3d>();
        int[] starts = new int[lines.Count];
        int[] ends = new int[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            Point3d start = lines[i].StartPoint;
            Point3d end = lines[i].EndPoint;
            if (Distance(start, end) <= PointTolerance)
                throw new ArgumentException("Extrude profile contains a zero-length line.", "sketch");
            starts[i] = FindOrAddVertex(vertices, start);
            ends[i] = FindOrAddVertex(vertices, end);
        }

        int[] degrees = new int[vertices.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            degrees[starts[i]]++;
            degrees[ends[i]]++;
        }
        for (int i = 0; i < degrees.Length; i++)
            if (degrees[i] != 2)
                throw new ArgumentException("Extrude line profile is open or branches at a vertex.", "sketch");

        bool[] visited = new bool[vertices.Count];
        List<int> pending = new List<int>();
        pending.Add(0);
        visited[0] = true;
        for (int at = 0; at < pending.Count; at++)
        {
            int vertex = pending[at];
            for (int edge = 0; edge < lines.Count; edge++)
            {
                int adjacent = starts[edge] == vertex ? ends[edge] : (ends[edge] == vertex ? starts[edge] : -1);
                if (adjacent >= 0 && !visited[adjacent])
                {
                    visited[adjacent] = true;
                    pending.Add(adjacent);
                }
            }
        }
        for (int i = 0; i < visited.Length; i++)
            if (!visited[i]) throw new ArgumentException("Extrude supports exactly one connected profile loop.", "sketch");
    }

    private static int FindOrAddVertex(List<Point3d> vertices, Point3d point)
    {
        for (int i = 0; i < vertices.Count; i++)
            if (Distance(vertices[i], point) <= PointTolerance) return i;
        vertices.Add(point);
        return vertices.Count - 1;
    }

    private static double Distance(Point3d left, Point3d right)
    {
        double x = left.X - right.X;
        double y = left.Y - right.Y;
        double z = left.Z - right.Z;
        return Math.Sqrt(x * x + y * y + z * z);
    }

    private static Point3d ToModelPoint(SketchFrame frame, double x, double y)
    {
        RequireFinite(x, "local X");
        RequireFinite(y, "local Y");
        return new Point3d(frame.Origin.X + frame.XAxis.X * x + frame.YAxis.X * y,
            frame.Origin.Y + frame.XAxis.Y * x + frame.YAxis.Y * y,
            frame.Origin.Z + frame.XAxis.Z * x + frame.YAxis.Z * y);
    }

    private static void GetAxes(M3SketchPlane plane, out Vector3d u, out Vector3d v, out Vector3d normal)
    {
        switch (plane)
        {
            case M3SketchPlane.XY:
                u = new Vector3d(1.0, 0.0, 0.0);
                v = new Vector3d(0.0, 1.0, 0.0);
                normal = new Vector3d(0.0, 0.0, 1.0);
                return;
            case M3SketchPlane.XZ:
                u = new Vector3d(1.0, 0.0, 0.0);
                v = new Vector3d(0.0, 0.0, 1.0);
                normal = new Vector3d(0.0, -1.0, 0.0);
                return;
            case M3SketchPlane.YZ:
                u = new Vector3d(0.0, 1.0, 0.0);
                v = new Vector3d(0.0, 0.0, 1.0);
                normal = new Vector3d(1.0, 0.0, 0.0);
                return;
            default:
                throw new ArgumentOutOfRangeException("plane");
        }
    }

    private static string NumberText(double value)
    {
        if (Double.IsNaN(value) || Double.IsInfinity(value))
            throw new ArgumentOutOfRangeException("value", "NX expression values must be finite.");
        return value.ToString("R", CultureInfo.InvariantCulture);
    }
}
