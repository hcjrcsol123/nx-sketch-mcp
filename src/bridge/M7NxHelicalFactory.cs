using System;
using System.Collections.Generic;
using System.Globalization;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;
using NxSketchMcp.M2;

// Typed NX2506 operations. No journals, NX session launch, or automatic body selection.
public sealed partial class M2OpenModelAdapter
{
    private const double M7ToleranceMm = 0.001;
    private const string M7BooleanAttribute = "M7_NATIVE_HELICAL_BOOLEAN";
    private const string M7HelperAttribute = "M7_NATIVE_HELICAL_HELPER";
    private const string M7SweepAttribute = "M7_NATIVE_HELICAL_SWEEP";
    private const string M7ThreadAttribute = "M7_NATIVE_EXTERNAL_THREAD";

    private sealed class M7PlanState
    {
        internal M3PlanState Common;
        internal M7ThreadRequest Thread;
        internal M7HelicalGrooveRequest Groove;
        internal Face Cylinder;
        internal Face StartFace;
        internal bool Reverse;
        internal Dictionary<string, object> ThreadCoverage;
        internal bool Consumed;
        internal Feature Sweep;
        internal Curve[] Guides;
        internal Line[] Section;
        internal Dictionary<string, object> DefinitionAfter;
    }

    public M3PlanSnapshot PrepareThread(M2ModelBinding binding, M7ThreadRequest request)
    {
        if (request == null) throw new M2ModelException("INVALID_REQUEST", "thread request is required");
        M7ValidateCommon(request.RevisionId, request.TargetBodyId, request.Axis, request.AxisOriginMm,
            request.StartMm, request.LengthMm, request.Handedness);
        if (request.Direction != "positive" && request.Direction != "negative")
            throw new M2ModelException("INVALID_REQUEST", "direction must be positive or negative");
        M7Positive(request.MajorDiameterMm, "major_diameter_mm");
        M7Positive(request.MinorDiameterMm, "minor_diameter_mm");
        M7Positive(request.PitchMm, "pitch_mm");
        if (request.MajorDiameterMm <= request.MinorDiameterMm || request.LengthMm / request.PitchMm > 500)
            throw new M2ModelException("INVALID_REQUEST", "thread requires major > minor and at most 500 turns");
        BindingState state = RequireState(binding);
        SnapshotResult snapshot = M7PrepareContext(binding, state, request.RevisionId);
        M7ThreadRequest clone = new M7ThreadRequest {
            RevisionId = request.RevisionId, TargetBodyId = request.TargetBodyId, Axis = request.Axis,
            AxisOriginMm = (double[])request.AxisOriginMm.Clone(), StartMm = request.StartMm,
            LengthMm = request.LengthMm, Handedness = request.Handedness, Direction = request.Direction,
            MajorDiameterMm = request.MajorDiameterMm, MinorDiameterMm = request.MinorDiameterMm, PitchMm = request.PitchMm
        };
        M7PlanState plan = new M7PlanState { Common = CreateM3PlanState(binding, state, "create_thread", snapshot), Thread = clone };
        plan.Common.TargetBody = FindBody(state.WorkPart, clone.TargetBodyId);
        plan.Common.TargetBodyId = clone.TargetBodyId;
        M7RequireSolid(plan.Common.TargetBody);
        M7SelectThreadFaces(plan);
        Dictionary<string, object> view = M7RequestRecord(plan);
        view.Add("cylindrical_face_id", RequiredJournalId(plan.Cylinder, "thread cylinder"));
        view.Add("start_face_id", RequiredJournalId(plan.StartFace, "thread start face"));
        view.Add("native_reverse_thread_direction", plan.Reverse);
        view.Add("axial_coverage", plan.ThreadCoverage);
        view.Add("native_mode", "Detailed external single-start 60-degree manual thread");
        return M7Snapshot(plan, view);
    }

    public M3PlanSnapshot PrepareHelicalGroove(M2ModelBinding binding, M7HelicalGrooveRequest request)
    {
        if (request == null) throw new M2ModelException("INVALID_REQUEST", "helical groove request is required");
        M7ValidateCommon(request.RevisionId, request.TargetBodyId, request.Axis, request.AxisOriginMm,
            request.StartMm, request.LengthMm, request.Handedness);
        M7Positive(request.LeadMm, "lead_mm"); M7Positive(request.RootDiameterMm, "root_diameter_mm");
        M7Positive(request.ReferenceDiameterMm, "reference_diameter_mm"); M7Positive(request.OuterDiameterMm, "outer_diameter_mm");
        M7Positive(request.SpaceWidthMm, "space_width_mm"); M7Finite(request.FlankAngleDeg, "flank_angle_deg");
        M7Finite(request.PhaseDeg, "phase_deg");
        if (!(request.RootDiameterMm < request.ReferenceDiameterMm && request.ReferenceDiameterMm < request.OuterDiameterMm) ||
            request.FlankAngleDeg <= 0 || request.FlankAngleDeg >= 45 || request.PhaseDeg < -360 || request.PhaseDeg > 360 ||
            request.LengthMm / request.LeadMm > 50)
            throw new M2ModelException("INVALID_REQUEST", "invalid groove diameters, angle, phase, or turn count");
        if (M7HalfWidth(request, request.RootDiameterMm / 2) <= 0 ||
            2 * M7HalfWidth(request, request.OuterDiameterMm / 2) >= request.LeadMm)
            throw new M2ModelException("INVALID_REQUEST", "groove must have positive root width and outer width below lead");
        BindingState state = RequireState(binding);
        SnapshotResult snapshot = M7PrepareContext(binding, state, request.RevisionId);
        M7HelicalGrooveRequest clone = new M7HelicalGrooveRequest {
            RevisionId = request.RevisionId, TargetBodyId = request.TargetBodyId, Axis = request.Axis,
            AxisOriginMm = (double[])request.AxisOriginMm.Clone(), StartMm = request.StartMm, LengthMm = request.LengthMm,
            Handedness = request.Handedness, LeadMm = request.LeadMm, RootDiameterMm = request.RootDiameterMm,
            ReferenceDiameterMm = request.ReferenceDiameterMm, OuterDiameterMm = request.OuterDiameterMm,
            SpaceWidthMm = request.SpaceWidthMm, FlankAngleDeg = request.FlankAngleDeg, PhaseDeg = request.PhaseDeg
        };
        M7PlanState plan = new M7PlanState { Common = CreateM3PlanState(binding, state, "create_helical_groove", snapshot), Groove = clone };
        plan.Common.TargetBody = FindBody(state.WorkPart, clone.TargetBodyId);
        plan.Common.TargetBodyId = clone.TargetBodyId;
        M7RequireSolid(plan.Common.TargetBody);
        double[] box = new double[6]; UFSession.GetUFSession().Modl.AskBoundingBox(plan.Common.TargetBody.Tag, box);
        int axis = M7Axis(clone.Axis);
        double width = M7HalfWidth(clone, clone.OuterDiameterMm / 2);
        if (clone.AxisOriginMm[axis] + clone.StartMm + width < box[axis] ||
            clone.AxisOriginMm[axis] + clone.StartMm + clone.LengthMm - width > box[axis + 3])
            throw new M2ModelException("GEOMETRY_OUT_OF_RANGE", "helical groove travel does not overlap target axial range");
        Dictionary<string, object> view = M7RequestRecord(plan);
        view.Add("guide_count", 3); view.Add("guide_samples_per_turn", 96);
        view.Add("spline_degree", 3); view.Add("section_vertex_count", 4);
        view.Add("tolerances", new double[] { 0.001, 0.05, 0.001 });
        view.Add("native_mode", "independent solid three-guide sweep followed by explicit target subtract");
        return M7Snapshot(plan, view);
    }

    private SnapshotResult M7PrepareContext(M2ModelBinding binding, BindingState state, string revision)
    {
        string reason;
        if (!IsBindingCurrent(binding, out reason)) throw new M2ModelException("STALE_BINDING", reason);
        RequireWritableSimplePart(state.WorkPart); RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
        if (state.Session.ActiveSketch != null) throw new M2ModelException("ACTIVE_SKETCH_UNSUPPORTED", "end the active sketch before preparing a helical operation");
        RefreshM4SupportTracksFromPart(state.WorkPart, state);
        if (state.M4SupportTracks.Count != 0)
            throw new M2ModelException("SUPPORT_DEPENDENCY_UNSUPPORTED", "helical operations cannot modify a part with protected M4 support associations");
        SnapshotResult captured = Capture(binding, state);
        if (revision != binding.RevisionId) throw new M2ModelException("STALE_REVISION", "revision changed before helical preparation");
        return captured;
    }

    private static M3PlanSnapshot M7Snapshot(M7PlanState plan, Dictionary<string, object> view)
    {
        view.Add("target_body_before", FindBodyMeasure(plan.Common.BodiesBefore, plan.Common.TargetBodyId).PublicRecord);
        view.Add("non_target_bodies_before", BodyMeasureRecords(plan.Common.BodiesBefore, plan.Common.TargetBodyId));
        return new M3PlanSnapshot { Kind = plan.Common.Kind, RevisionId = plan.Common.RevisionId,
            BodyId = plan.Common.TargetBodyId, PublicView = view, AdapterState = plan };
    }

    private static void M7ValidateCommon(string revision, string body, string axis, double[] origin,
        double start, double length, string handedness)
    {
        if (String.IsNullOrWhiteSpace(revision) || String.IsNullOrWhiteSpace(body))
            throw new M2ModelException("INVALID_REQUEST", "revision_id and target_body_id are required");
        M7Axis(axis);
        if (origin == null || origin.Length != 3) throw new M2ModelException("INVALID_REQUEST", "axis_origin_mm must contain three coordinates");
        foreach (double value in origin) M7Finite(value, "axis_origin_mm");
        M7Finite(start, "start_mm"); M7Positive(length, "length_mm");
        if (handedness != "right" && handedness != "left") throw new M2ModelException("INVALID_REQUEST", "handedness must be right or left");
    }

    private static int M7Axis(string axis)
    {
        if (axis == "X") return 0; if (axis == "Y") return 1; if (axis == "Z") return 2;
        throw new M2ModelException("INVALID_REQUEST", "axis must be X, Y, or Z");
    }
    private static void M7Finite(double value, string name)
    {
        if (Double.IsNaN(value) || Double.IsInfinity(value) || Math.Abs(value) > 1000000)
            throw new M2ModelException("INVALID_REQUEST", name + " must be finite and within +/-1000000");
    }
    private static void M7Positive(double value, string name)
    {
        M7Finite(value, name); if (value <= 0) throw new M2ModelException("INVALID_REQUEST", name + " must be positive");
    }
    private static void M7RequireSolid(Body body)
    {
        if (body == null || !body.IsSolidBody) throw new M2ModelException("TARGET_BODY_UNSUPPORTED", "target must be a solid body");
        int count; int[] types; Tag[] tags;
        UFSession.GetUFSession().Modl.AskBodyConsistency(body.Tag, out count, out types, out tags);
        if (count != 0) throw new M2ModelException("TARGET_BODY_INCONSISTENT", "native target body has consistency faults");
    }
    private static bool M7SnapshotBodiesConsistent(Dictionary<string, object> snapshot)
    {
        foreach (object item in SnapshotList(snapshot, "bodies"))
        {
            Dictionary<string, object> body = item as Dictionary<string, object>; object value;
            if (body == null || !body.TryGetValue("body_consistency_fault_count", out value) || Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0) return false;
        }
        return true;
    }

    private static void M7SelectThreadFaces(M7PlanState plan)
    {
        M7ThreadRequest r = plan.Thread; int axis = M7Axis(r.Axis);
        double start = r.AxisOriginMm[axis] + r.StartMm;
        double end = start + (r.Direction == "positive" ? r.LengthMm : -r.LengthMm);
        List<Face> cylinders = new List<Face>(); List<Face> starts = new List<Face>();
        double startNormal = 0;
        foreach (Face face in plan.Common.TargetBody.GetFaces())
        {
            int type, normal; double radius, radData;
            double[] p = new double[3], dir = new double[3], box = new double[6];
            UFSession.GetUFSession().Modl.AskFaceData(face.Tag, out type, p, dir, box, out radius, out radData, out normal);
            double magnitude = Length3(dir);
            if (magnitude < 1e-12 || Math.Abs(dir[axis] / magnitude) < 0.999999) continue;
            if (type == UFConstants.UF_MODL_CYLINDRICAL_FACE)
            {
                bool coaxial = true;
                for (int i = 0; i < 3; i++) if (i != axis && Math.Abs(p[i] - r.AxisOriginMm[i]) > M7ToleranceMm) coaxial = false;
                if (coaxial && Math.Abs(2 * radius - r.MajorDiameterMm) <= M7ToleranceMm &&
                    Math.Min(start, end) < box[axis + 3] + M7ToleranceMm && Math.Max(start, end) > box[axis] - M7ToleranceMm)
                    cylinders.Add(face);
            }
            else if (type == UFConstants.UF_MODL_PLANAR_FACE && Math.Abs(p[axis] - start) <= M7ToleranceMm)
            {
                starts.Add(face); startNormal = dir[axis] / magnitude;
            }
        }
        if (cylinders.Count != 1 || starts.Count != 1)
            throw new M2ModelException("AMBIGUOUS_THREAD_FACES", "thread requires exactly one matching coaxial cylinder and one planar start face");
        double[] bounds = M7ExactBox(plan.Common.TargetBody.Tag);
        if (Math.Min(start, end) < bounds[axis] - M7ToleranceMm || Math.Max(start, end) > bounds[axis + 3] + M7ToleranceMm)
            throw new M2ModelException("GEOMETRY_OUT_OF_RANGE", "thread extent lies outside the explicit target body");
        double[] cylinderBox = M7ExactBox(cylinders[0].Tag);
        bool cylinderCovers = Math.Min(start, end) >= cylinderBox[axis] - M7ToleranceMm &&
            Math.Max(start, end) <= cylinderBox[axis + 3] + M7ToleranceMm;
        Face leadCone = null;
        if (!cylinderCovers)
        {
            // Permit only a geometrically proven conical lead at the start cap.
            // The requested far end must be inside the diameter-matched cylinder;
            // crossing a different shaft diameter or a second shoulder is rejected.
            if (end < cylinderBox[axis] - M7ToleranceMm || end > cylinderBox[axis + 3] + M7ToleranceMm ||
                (start > cylinderBox[axis + 3] && r.Direction != "negative") ||
                (start < cylinderBox[axis] && r.Direction != "positive"))
                throw new M2ModelException("THREAD_COVERAGE_MISMATCH", "thread far end is outside the selected diameter-matched cylinder");
            double boundary = start > cylinderBox[axis + 3] ? cylinderBox[axis + 3] : cylinderBox[axis];
            List<Face> coneCandidates = new List<Face>();
            foreach (Face candidate in plan.Common.TargetBody.GetFaces())
            {
                if (candidate.SolidFaceType != Face.FaceType.Conical) continue;
                int type, normal; double radius, halfAngle;
                double[] p = new double[3], dir = new double[3], unusedBox = new double[6];
                UFSession.GetUFSession().Modl.AskFaceData(candidate.Tag, out type, p, dir, unusedBox, out radius, out halfAngle, out normal);
                double magnitude = Length3(dir); bool coaxial = magnitude > 1e-12 && Math.Abs(dir[axis] / magnitude) > .999999;
                for (int i = 0; i < 3; i++) if (i != axis && Math.Abs(p[i] - r.AxisOriginMm[i]) > M7ToleranceMm) coaxial = false;
                if (!coaxial || !M7FacesShareEdge(candidate, cylinders[0]) || !M7FacesShareEdge(candidate, starts[0])) continue;
                double[] coneBox = M7ExactBox(candidate.Tag);
                if (Math.Abs(coneBox[axis] - Math.Min(start, boundary)) > M7ToleranceMm ||
                    Math.Abs(coneBox[axis + 3] - Math.Max(start, boundary)) > M7ToleranceMm) continue;
                // Shared full circular edge establishes the cone's major-diameter
                // end at the selected cylinder, while the cap establishes its start.
                coneCandidates.Add(candidate);
            }
            if (coneCandidates.Count != 1)
                throw new M2ModelException("THREAD_COVERAGE_MISMATCH", "start gap requires one coaxial conical face connected directly to the start cap and selected cylinder");
            leadCone = coneCandidates[0];
        }
        plan.ThreadCoverage = new Dictionary<string, object> { { "start_coordinate_mm", start }, { "end_coordinate_mm", end },
            { "cylinder_axial_min_mm", cylinderBox[axis] }, { "cylinder_axial_max_mm", cylinderBox[axis + 3] },
            { "cylinder_covers_full_interval", cylinderCovers }, { "conical_start_lead_id", leadCone == null ? null : RequiredJournalId(leadCone, "thread lead cone") },
            { "conical_start_lead_native_geometry", leadCone == null ? null : M7FaceDefinition(leadCone) } };
        plan.Cylinder = cylinders[0]; plan.StartFace = starts[0];
        // Native default proceeds into the solid, opposite the planar face normal.
        plan.Reverse = (r.Direction == "positive" ? 1 : -1) * startNormal > 0;
    }
    private static bool M7FacesShareEdge(Face first, Face second)
    {
        HashSet<Tag> tags = new HashSet<Tag>(); foreach (Edge edge in first.GetEdges()) tags.Add(edge.Tag);
        foreach (Edge edge in second.GetEdges()) if (tags.Contains(edge.Tag)) return true;
        return false;
    }

    private static double[] M7ExactBox(Tag tag)
    {
        double[] corner = new double[3], distances = new double[3]; double[,] directions = new double[3, 3];
        UFSession.GetUFSession().Modl.AskBoundingBoxExact(tag, Tag.Null, corner, directions, distances);
        double[] box = new double[] { corner[0], corner[1], corner[2], corner[0], corner[1], corner[2] };
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double delta = directions[i, j] * distances[i];
                if (delta < 0) box[j] += delta; else box[j + 3] += delta;
            }
        return box;
    }

    private M3ExecutionResult ExecuteM7Plan(M2ModelBinding binding, M3PlanSnapshot prepared)
    {
        M7PlanState plan = (M7PlanState)prepared.AdapterState;
        M3PlanState c = plan.Common; BindingState state;
        if (plan.Consumed) return M3Execution("UNCHANGED", binding == null ? null : binding.RevisionId, null, "PLAN_ALREADY_USED", "helical plan is single-use");
        plan.Consumed = true;
        try
        {
            state = RequireState(binding);
            if (!Object.ReferenceEquals(state, c.BindingState) || prepared.Kind != c.Kind || prepared.RevisionId != c.RevisionId || prepared.BodyId != c.TargetBodyId)
                return M3Execution("UNCHANGED", binding.RevisionId, null, "INVALID_PLAN", "helical plan does not match retained binding, kind, revision and body");
            SnapshotResult current = M7PrepareContext(binding, state, c.RevisionId);
            Dictionary<string, object> evidence;
            if (CanonicalSemanticSnapshot(current.Snapshot) != c.SemanticBefore || CaptureFullGeometry(state.WorkPart) != c.GeometryBefore ||
                !BodyMeasuresEqual(c.BodiesBefore, CaptureM3BodyMeasures(state.WorkPart), out evidence))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "native geometry or model semantics changed after preparation");
            if (plan.Thread != null) M7SelectThreadFaces(plan);
        }
        catch (M2ModelException ex) { return M3Execution("UNCHANGED", binding == null ? null : binding.RevisionId, null, ex.Code, ex.Message); }
        catch (Exception ex) { return M3Execution("UNCHANGED", binding == null ? null : binding.RevisionId, null, "MODEL_READ_FAILED", ex.Message); }
        Session.UndoMarkId mark;
        try { mark = state.Session.SetUndoMark(Session.MarkVisibility.Invisible, "M7 native helical operation"); }
        catch (Exception ex) { return M3Execution("UNCHANGED", binding.RevisionId, null, "UPDATE_FAILED", ex.Message); }
        try
        {
            if (plan.Thread != null) M7CreateThread(state.WorkPart, plan); else M7CreateGroove(state.WorkPart, plan);
            int errors = state.Session.UpdateManager.DoUpdate(mark);
            if (errors != 0) throw new M2ModelException("UPDATE_FAILED", "helical update returned errors: " + errors);
            foreach (Feature feature in state.WorkPart.Features.GetFeatures())
                if (feature.GetFeatureErrorMessages().Length != 0) throw new M2ModelException("FEATURE_UPDATE_FAILED", "part has feature errors after helical update");
            c.FeatureId = RequiredJournalId(c.Feature, "helical feature");
            c.AffectedBodyId = RequiredJournalId(c.TargetBody, "target body");
            c.BodiesAfter = CaptureM3BodyMeasures(state.WorkPart);
            plan.DefinitionAfter = CaptureM7FeatureDefinition(state.WorkPart, c.Feature);
            SnapshotResult after = Capture(binding, state);
            c.SemanticAfter = CanonicalSemanticSnapshot(after.Snapshot); c.GeometryAfter = CaptureFullGeometry(state.WorkPart);
            c.HistoryAfter = HistoryProjection(after.Snapshot);
            Dictionary<string, object> result = M7RequestRecord(plan);
            result["feature_id"] = c.FeatureId; result["body_id"] = c.AffectedBodyId;
            result["native_definition"] = plan.DefinitionAfter; result["body_inventory"] = BodyMeasureRecords(c.BodiesAfter, null);
            result["revision_id"] = binding.RevisionId; result["update_error_count"] = errors;
            c.Execution = M3Execution("APPLIED", binding.RevisionId, result, null, null);
            M3VerificationResult verification = VerifyM7Plan(binding, prepared);
            if (!verification.Verified) throw new M2ModelException("PART_VERIFICATION_FAILED", "native helical result failed independent parameter, body or history verification");
            result["verification_checks"] = verification.Checks;
            return c.Execution;
        }
        catch (Exception ex)
        {
            c.Execution = RestoreM3State(binding, state, c, mark, ex);
            return c.Execution;
        }
        finally { TryDeleteUndoMark(state.Session, mark); }
    }

    private static void M7CreateThread(Part work, M7PlanState plan)
    {
        M7ThreadRequest r = plan.Thread; ThreadBuilder builder = work.Features.CreateThreadBuilder(null);
        try
        {
            builder.ThreadInput = ThreadBuilder.Input.Manual; builder.ThreadType = ThreadBuilder.Type.Detailed;
            builder.NumStarts = 1; builder.ThreadHandedness = r.Handedness == "right" ? ThreadBuilder.Handedness.RightHand : ThreadBuilder.Handedness.LeftHand;
            builder.ThreadLimit = ThreadBuilder.LimitOption.Value; builder.ReverseThreadDirection = plan.Reverse;
            builder.CylindricalFace.SetValue(plan.Cylinder, work.ModelingViews.WorkView,
                M7Point(r.Axis, r.AxisOriginMm, r.StartMm + (r.Direction == "positive" ? 1 : -1) * r.LengthMm / 2, r.MajorDiameterMm / 2, 0));
            builder.StartObject.SetValue(plan.StartFace, work.ModelingViews.WorkView,
                M7Point(r.Axis, r.AxisOriginMm, r.StartMm, (r.MajorDiameterMm + r.MinorDiameterMm) / 4, 0));
            if (builder.IsInternalThread) throw new M2ModelException("THREAD_MODE_MISMATCH", "native cylinder selection resulted in an internal thread");
            builder.ShaftPreference = ThreadBuilder.ShaftSizePreference.MajorDiameter;
            builder.ShaftDiameterExp.RightHandSide = NumberText(r.MajorDiameterMm);
            builder.MajorDiameterExp.RightHandSide = NumberText(r.MajorDiameterMm); builder.MinorDiameterExp.RightHandSide = NumberText(r.MinorDiameterMm);
            builder.PitchExp.RightHandSide = NumberText(r.PitchMm); builder.AngleExp.RightHandSide = "60";
            builder.ThreadLength.RightHandSide = NumberText(r.LengthMm);
            plan.Common.Feature = builder.CommitFeature();
            plan.Common.Feature.SetUserAttribute(M7ThreadAttribute, -1, "external-single-start-60-degree", Update.Option.Now);
            plan.Common.Feature.SetName("M7_external_single_start_thread");
        }
        finally { builder.Destroy(); }
    }

    private static Point3d M7Point(string axisName, double[] origin, double axial, double radius, double theta)
    {
        int axis = M7Axis(axisName), first = (axis + 1) % 3, second = (axis + 2) % 3;
        double[] p = (double[])origin.Clone(); p[axis] += axial; p[first] += radius * Math.Cos(theta); p[second] += radius * Math.Sin(theta);
        return new Point3d(p[0], p[1], p[2]);
    }
    private static double M7HalfWidth(M7HelicalGrooveRequest r, double radius)
    {
        return r.SpaceWidthMm / 2 + (radius - r.ReferenceDiameterMm / 2) * Math.Tan(r.FlankAngleDeg * Math.PI / 180);
    }
    private static double[,] M7Vertices(M7HelicalGrooveRequest r)
    {
        double root = r.RootDiameterMm / 2, outer = r.OuterDiameterMm / 2;
        return new double[,] { { r.StartMm - M7HalfWidth(r, root), root }, { r.StartMm + M7HalfWidth(r, root), root },
            { r.StartMm + M7HalfWidth(r, outer), outer }, { r.StartMm - M7HalfWidth(r, outer), outer } };
    }
    private static UFCurve.PtSlopeCrvatr[] M7GuidePoints(M7HelicalGrooveRequest r, double axial, double radius)
    {
        int count = (int)Math.Ceiling(r.LengthMm / r.LeadMm * 96) + 1;
        // Cubic interpolation requires at least four points, even for a fractional turn.
        count = Math.Max(4, count); UFCurve.PtSlopeCrvatr[] points = new UFCurve.PtSlopeCrvatr[count];
        for (int i = 0; i < count; i++)
        {
            double u = (double)i / (count - 1), theta = r.PhaseDeg * Math.PI / 180 + (r.Handedness == "right" ? 1 : -1) * 2 * Math.PI * r.LengthMm / r.LeadMm * u;
            Point3d p = M7Point(r.Axis, r.AxisOriginMm, axial + r.LengthMm * u, radius, theta);
            points[i] = new UFCurve.PtSlopeCrvatr { point = new double[] { p.X, p.Y, p.Z },
                slope_type = -1, slope = new double[3], crvatr_type = -1, crvatr = new double[3] };
        }
        return points;
    }
    private static StringList M7Strings(params Tag[][] groups)
    {
        StringList list = new StringList { num = groups.Length, _string = new int[groups.Length], dir = new int[groups.Length] };
        List<Tag> ids = new List<Tag>(); for (int i = 0; i < groups.Length; i++) { list._string[i] = groups[i].Length; list.dir[i] = 1; ids.AddRange(groups[i]); }
        list.id = ids.ToArray(); return list;
    }
    private static void M7CreateGroove(Part work, M7PlanState plan)
    {
        M7HelicalGrooveRequest r = plan.Groove; UFSession uf = UFSession.GetUFSession();
        double[,] vertices = M7Vertices(r); double phase = r.PhaseDeg * Math.PI / 180;
        plan.Section = new Line[4]; plan.Guides = new Curve[3];
        for (int i = 0; i < 4; i++)
        {
            int j = (i + 1) % 4;
            plan.Section[i] = work.Curves.CreateLine(M7Point(r.Axis, r.AxisOriginMm, vertices[i, 0], vertices[i, 1], phase),
                M7Point(r.Axis, r.AxisOriginMm, vertices[j, 0], vertices[j, 1], phase));
            plan.Section[i].SetUserAttribute(M7HelperAttribute, -1, "section", Update.Option.Now);
            plan.Section[i].Blank();
        }
        for (int i = 0; i < 3; i++)
        {
            UFCurve.PtSlopeCrvatr[] points = M7GuidePoints(r, vertices[i, 0], vertices[i, 1]);
            double[] parameters = new double[points.Length]; for (int j = 0; j < parameters.Length; j++) parameters[j] = (double)j / (parameters.Length - 1);
            Tag tag; uf.Curve.CreateSplineThruPts(3, 0, points.Length, points, parameters, 1, out tag);
            plan.Guides[i] = (Curve)NXOpen.Utilities.NXObjectManager.Get(tag);
            plan.Guides[i].SetUserAttribute(M7HelperAttribute, -1, "guide", Update.Option.Now); plan.Guides[i].Blank();
        }
        StringList guide = M7Strings(new Tag[] { plan.Guides[0].Tag }, new Tag[] { plan.Guides[1].Tag }, new Tag[] { plan.Guides[2].Tag });
        StringList section = M7Strings(new Tag[] { plan.Section[0].Tag, plan.Section[1].Tag, plan.Section[2].Tag, plan.Section[3].Tag });
        StringList spine = M7Strings();
        MethodList orientation = new MethodList { method = 4, inter = 0, id = Tag.Null, value = new double[3], s_curve = new StringList[] { M7Strings() } };
        MethodList scaling = new MethodList { method = 1, inter = 0, id = Tag.Null, value = new double[] { 1, 0, 0 }, s_curve = new StringList[] { M7Strings() } };
        int alignment = 1, interpolation = 2, bodyType = 1;
        Tag output; uf.Modl.CreateSweep(ref guide, ref section, ref spine, ref orientation, ref scaling, ref alignment, ref interpolation, ref bodyType,
            FeatureSigns.Nullsign, new double[] { 0.001, 0.05, 0.001 }, out output);
        TaggedObject obj = NXOpen.Utilities.NXObjectManager.Get(output); Body cutter = obj as Body;
        plan.Sweep = obj as Feature;
        if (cutter == null && plan.Sweep != null)
        {
            Body[] produced = plan.Sweep.GetBodies(); if (produced.Length == 1) cutter = produced[0];
        }
        if (cutter == null || cutter.Tag == plan.Common.TargetBody.Tag || !cutter.IsSolidBody)
            throw new M2ModelException("SWEEP_BODY_MISMATCH", "NULLSIGN sweep did not produce one independent solid cutter");
        if (plan.Sweep == null)
        {
            Tag[] features; uf.Modl.AskBodyFeats(cutter.Tag, out features);
            foreach (Tag tag in features)
            {
                Feature feature = NXOpen.Utilities.NXObjectManager.Get(tag) as Feature;
                if (feature != null && M7IsSweep(feature)) { if (plan.Sweep != null) throw new M2ModelException("AMBIGUOUS_SWEEP", "cutter has multiple native sweep features"); plan.Sweep = feature; }
            }
        }
        if (plan.Sweep == null) throw new M2ModelException("SWEEP_HISTORY_MISSING", "cutter has no native sweep feature");
        plan.Sweep.SetUserAttribute(M7SweepAttribute, -1, "three-guide-trapezoidal", Update.Option.Now);
        plan.Sweep.SetName("M7_three_guide_trapezoidal_cutter");
        Tag boolean; uf.Modl.SubtractBodiesWithRetainedOptions(plan.Common.TargetBody.Tag, cutter.Tag, false, false, out boolean);
        plan.Common.Feature = NXOpen.Utilities.NXObjectManager.Get(boolean) as Feature;
        if (plan.Common.Feature == null) throw new M2ModelException("BOOLEAN_HISTORY_MISSING", "explicit subtraction returned no native feature");
        plan.Common.Feature.SetUserAttribute(M7BooleanAttribute, -1, "three-guide-helical-groove", Update.Option.Now);
        plan.Common.Feature.SetName("M7_helical_groove_explicit_target_subtract");
    }

    private M3VerificationResult VerifyM7Plan(M2ModelBinding binding, M3PlanSnapshot prepared)
    {
        M7PlanState plan = (M7PlanState)prepared.AdapterState; M3PlanState c = plan.Common;
        if (c.Execution == null || c.Execution.State != "APPLIED") return M3VerificationFailure(binding == null ? null : binding.RevisionId, "helical verification requires an applied operation");
        BindingState state = RequireState(binding); string reason;
        if (!Object.ReferenceEquals(state, c.BindingState) || !IsBindingCurrent(binding, out reason)) return M3VerificationFailure(binding.RevisionId, "helical NX binding changed");
        SnapshotResult current = Capture(binding, state); List<M3BodyMeasure> bodies = CaptureM3BodyMeasures(state.WorkPart);
        Feature feature = FindFeature(state.WorkPart, c.FeatureId);
        Dictionary<string, object> definition = CaptureM7FeatureDefinition(state.WorkPart, feature);
        Dictionary<string, object> nativeEvidence; bool parameters = M7ParametersMatch(state.WorkPart, plan, out nativeEvidence);
        bool geometry = parameters && CaptureFullGeometry(state.WorkPart) == c.GeometryAfter;
        Dictionary<string, object> effect; bool bodyEffect = M7BodyEffect(plan, bodies, out effect);
        geometry = geometry && bodyEffect;
        Dictionary<string, object> bodyEvidence; bool bodiesMatch = BodyMeasuresEqual(c.BodiesAfter, bodies, out bodyEvidence) && bodyEffect;
        Dictionary<string, object> nonTargetEvidence;
        bool nonTargets = BodyMeasurementRecordsEqual(BodyMeasureRecords(c.BodiesBefore, c.TargetBodyId), BodyMeasureRecords(bodies, c.AffectedBodyId), out nonTargetEvidence);
        bool history = FeaturePresent(state.WorkPart, c.FeatureId) && SemanticValuesEqual(c.HistoryAfter, HistoryProjection(current.Snapshot), "history") &&
            c.SemanticAfter == CanonicalSemanticSnapshot(current.Snapshot) && feature.GetFeatureErrorMessages().Length == 0;
        Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal);
        nativeEvidence["constraint_basis"] = "native feature expressions, selected faces, sweep guides/section and boolean target association; no sketch constraints are created";
        checks.Add("geometry", VerificationCheck(geometry ? "VERIFIED" : "MISMATCH", new Dictionary<string, object> { { "native_definition", definition }, { "body_effect", effect }, { "native_parameters_match", parameters } }));
        checks.Add("constraints", VerificationCheck(parameters ? "VERIFIED" : "MISMATCH", nativeEvidence));
        checks.Add("history", VerificationCheck(history ? "VERIFIED" : "MISMATCH", new Dictionary<string, object> { { "feature_id", c.FeatureId }, { "before", c.HistoryBefore }, { "after", HistoryProjection(current.Snapshot) } }));
        bodyEvidence["body_effect"] = effect; checks.Add("bodies", VerificationCheck(bodiesMatch ? "VERIFIED" : "MISMATCH", bodyEvidence));
        checks.Add("non_target_bodies", VerificationCheck(nonTargets ? "VERIFIED" : "MISMATCH", nonTargetEvidence));
        bool passed = geometry && parameters && history && bodiesMatch && nonTargets;
        return new M3VerificationResult { Verified = passed, RevisionId = binding.RevisionId, Checks = checks,
            Result = new Dictionary<string, object> { { "kind", c.Kind }, { "feature_id", c.FeatureId }, { "body_id", c.AffectedBodyId }, { "native_definition", definition }, { "body_inventory", BodyMeasureRecords(bodies, null) } },
            ErrorCode = passed ? null : "PART_VERIFICATION_FAILED", ErrorMessage = passed ? null : "native helical parameters, associations, history or bodies differ" };
    }

    private static bool M7BodyEffect(M7PlanState plan, List<M3BodyMeasure> bodies, out Dictionary<string, object> evidence)
    {
        M3PlanState c = plan.Common; M3BodyMeasure before = FindBodyMeasure(c.BodiesBefore, c.TargetBodyId);
        M3BodyMeasure after = FindBodyMeasureByTag(bodies, c.AffectedBodyId, c.TargetBody);
        int faults = -1; int[] types; Tag[] tags;
        if (after != null) UFSession.GetUFSession().Modl.AskBodyConsistency(after.Body.Tag, out faults, out types, out tags);
        bool reduced = after != null && before.VolumeMm3.HasValue && after.VolumeMm3.HasValue &&
            before.VolumeMm3.Value - after.VolumeMm3.Value > Math.Max(1e-6, before.VolumeMm3.Value * 1e-9);
        bool passed = after != null && after.IsSolid && faults == 0 && reduced && bodies.Count == c.BodiesBefore.Count;
        evidence = new Dictionary<string, object> { { "before", before.PublicRecord }, { "after", after == null ? null : after.PublicRecord },
            { "volume_reduced", reduced }, { "body_consistency_fault_count", faults }, { "solid_body_count_unchanged", bodies.Count == c.BodiesBefore.Count } };
        return passed;
    }

    private static bool M7ParametersMatch(Part work, M7PlanState plan, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        if (plan.Thread != null)
        {
            M7ThreadRequest r = plan.Thread; NXOpen.Features.Thread thread = plan.Common.Feature as NXOpen.Features.Thread;
            if (thread == null) return false;
            ThreadBuilder builder = work.Features.CreateThreadBuilder(thread);
            try
            {
                Face cylinder = builder.CylindricalFace.Value as Face, start = builder.StartObject.Value as Face;
                bool faces = cylinder != null && start != null && cylinder.Tag == plan.Cylinder.Tag && start.Tag == plan.StartFace.Tag &&
                    cylinder.GetBody().Tag == plan.Common.TargetBody.Tag && start.GetBody().Tag == plan.Common.TargetBody.Tag;
                bool matches = faces && !builder.IsInternalThread && builder.ThreadInput == ThreadBuilder.Input.Manual &&
                    builder.ThreadType == ThreadBuilder.Type.Detailed && builder.NumStarts == 1 && builder.ThreadLimit == ThreadBuilder.LimitOption.Value &&
                    builder.ThreadHandedness == (r.Handedness == "right" ? ThreadBuilder.Handedness.RightHand : ThreadBuilder.Handedness.LeftHand) &&
                    builder.ReverseThreadDirection == plan.Reverse && builder.ShaftPreference == ThreadBuilder.ShaftSizePreference.MajorDiameter &&
                    M7Near(builder.MajorDiameterExp.Value, r.MajorDiameterMm) && M7Near(builder.MinorDiameterExp.Value, r.MinorDiameterMm) &&
                    M7Near(builder.ShaftDiameterExp.Value, r.MajorDiameterMm) && M7Near(builder.PitchExp.Value, r.PitchMm) &&
                    M7Near(builder.AngleExp.Value, 60) && M7Near(builder.ThreadLength.Value, r.LengthMm);
                evidence.Add("native_definition", M7ThreadDefinition(builder)); evidence.Add("selected_face_associations_match", faces);
                evidence.Add("prepared_axial_coverage", plan.ThreadCoverage);
                return matches;
            }
            finally { builder.Destroy(); }
        }
        M7HelicalGrooveRequest g = plan.Groove; UFSession uf = UFSession.GetUFSession();
        StringList guides, section, spine; MethodList orientation, scaling; int alignment, interpolation, bodyType;
        double[] tolerances = new double[3]; uf.Modl.AskSweep(plan.Sweep.Tag, out guides, out section, out spine, out orientation, out scaling,
            out alignment, out interpolation, out bodyType, tolerances);
        bool result = guides.num == 3 && guides.id != null && guides.id.Length == 3 && section.num == 1 && section.id != null && section.id.Length == 4 &&
            spine.num == 0 && bodyType == 1 && alignment == 1 && interpolation == 2 && orientation.method == 4 && scaling.method == 1 &&
            M7Near(scaling.value[0], 1) && M7Near(tolerances[0], .001) && M7Near(tolerances[1], .05) && M7Near(tolerances[2], .001);
        double[,] vertices = M7Vertices(g); List<object> curveEvidence = new List<object>();
        for (int i = 0; i < 3; i++)
        {
            bool associated = guides.id != null && guides.id.Length > i && guides.id[i] == plan.Guides[i].Tag;
            int degree, periodic, count; UFCurve.PtSlopeCrvatr[] points; double[] parameters;
            uf.Curve.AskSplineThruPts(plan.Guides[i].Tag, out degree, out periodic, out count, out points, out parameters);
            UFCurve.PtSlopeCrvatr[] expected = M7GuidePoints(g, vertices[i, 0], vertices[i, 1]);
            bool matches = associated && degree == 3 && periodic == 0 && count == expected.Length && points != null && points.Length == expected.Length;
            if (matches) for (int j = 0; j < count; j++)
                for (int k = 0; k < 3; k++) if (!M7Near(points[j].point[k], expected[j].point[k])) matches = false;
            result = result && matches; curveEvidence.Add(new Dictionary<string, object> { { "guide_id", RequiredJournalId(plan.Guides[i], "guide") }, { "interpolation_points_match_request", matches }, { "point_count", count } });
        }
        for (int i = 0; i < 4; i++)
        {
            int j = (i + 1) % 4; Point3d start = M7Point(g.Axis, g.AxisOriginMm, vertices[i, 0], vertices[i, 1], g.PhaseDeg * Math.PI / 180);
            Point3d end = M7Point(g.Axis, g.AxisOriginMm, vertices[j, 0], vertices[j, 1], g.PhaseDeg * Math.PI / 180);
            result = result && section.id.Length > i && section.id[i] == plan.Section[i].Tag && M7PointsNear(plan.Section[i].StartPoint, start) && M7PointsNear(plan.Section[i].EndPoint, end);
        }
        FeatureSigns sign; Tag target, tool; bool retainTarget, retainTool;
        uf.Modl.AskBooleanWithRetainedOptions(plan.Common.Feature.Tag, out sign, out target, out tool, out retainTarget, out retainTool);
        bool sweepParent = false; foreach (Feature parent in plan.Common.Feature.GetParents()) if (parent.Tag == plan.Sweep.Tag) sweepParent = true;
        result = result && sign == FeatureSigns.Negative && target == plan.Common.TargetBody.Tag && !retainTarget && !retainTool && sweepParent;
        evidence.Add("guide_geometry", curveEvidence); evidence.Add("sweep_parent_association", sweepParent);
        evidence.Add("explicit_boolean_target_match", target == plan.Common.TargetBody.Tag); evidence.Add("native_subtract_sign", sign.ToString());
        evidence.Add("sweep_definition", CaptureM7FeatureDefinition(work, plan.Sweep));
        return result;
    }
    private static bool M7Near(double first, double second) { return Math.Abs(first - second) <= 1e-6; }
    private static bool M7PointsNear(Point3d first, Point3d second) { return M7Near(first.X, second.X) && M7Near(first.Y, second.Y) && M7Near(first.Z, second.Z); }

    private static Dictionary<string, object> M7RequestRecord(M7PlanState plan)
    {
        Dictionary<string, object> record = new Dictionary<string, object> { { "kind", plan.Common.Kind }, { "revision_id", plan.Common.RevisionId }, { "target_body_id", plan.Common.TargetBodyId } };
        if (plan.Thread != null)
        {
            M7ThreadRequest r = plan.Thread;
            record.Add("axis", r.Axis); record.Add("axis_origin_mm", (double[])r.AxisOriginMm.Clone()); record.Add("start_mm", r.StartMm); record.Add("length_mm", r.LengthMm);
            record.Add("handedness", r.Handedness); record.Add("direction", r.Direction); record.Add("major_diameter_mm", r.MajorDiameterMm);
            record.Add("minor_diameter_mm", r.MinorDiameterMm); record.Add("pitch_mm", r.PitchMm);
        }
        else
        {
            M7HelicalGrooveRequest r = plan.Groove;
            record.Add("axis", r.Axis); record.Add("axis_origin_mm", (double[])r.AxisOriginMm.Clone()); record.Add("start_mm", r.StartMm); record.Add("length_mm", r.LengthMm);
            record.Add("handedness", r.Handedness); record.Add("lead_mm", r.LeadMm); record.Add("root_diameter_mm", r.RootDiameterMm);
            record.Add("reference_diameter_mm", r.ReferenceDiameterMm); record.Add("outer_diameter_mm", r.OuterDiameterMm);
            record.Add("space_width_mm", r.SpaceWidthMm); record.Add("flank_angle_deg", r.FlankAngleDeg); record.Add("phase_deg", r.PhaseDeg);
        }
        return record;
    }

    private static bool M7IsSweep(Feature feature)
    {
        string type = feature.FeatureType.ToUpperInvariant(); return type == "SWEPT" || type == "SWEEP" || type == "SKIN";
    }
    private static bool M7HasNativeDefinition(Feature feature)
    {
        return (feature is NXOpen.Features.Thread && feature.HasUserAttribute(M7ThreadAttribute, NXObject.AttributeType.String, -1)) ||
            (M7IsSweep(feature) && feature.HasUserAttribute(M7SweepAttribute, NXObject.AttributeType.String, -1)) ||
            feature.HasUserAttribute(M7BooleanAttribute, NXObject.AttributeType.String, -1);
    }
    private static List<object> CaptureM7NativeDefinitions(Part work)
    {
        List<Dictionary<string, object>> definitions = new List<Dictionary<string, object>>();
        foreach (Feature feature in work.Features.GetFeatures()) if (M7HasNativeDefinition(feature)) definitions.Add(CaptureM7FeatureDefinition(work, feature));
        // Include marked auxiliaries even when their sweep creation failed. Undo verification
        // must detect orphaned guides/sections as well as resulting native features.
        foreach (Curve curve in work.Curves.ToArray())
            if (curve.HasUserAttribute(M7HelperAttribute, NXObject.AttributeType.String, -1))
            {
                Dictionary<string, object> helper = M7CurveDefinition(curve);
                helper.Add("definition_kind", "helical_helper_curve");
                helper.Add("blanked", curve.IsBlanked);
                definitions.Add((Dictionary<string, object>)M7NormalizeNativeValue(helper));
            }
        SortRecords(definitions, "journal_id", "native helical feature");
        return ToObjectList(definitions);
    }
    private static Dictionary<string, object> CaptureM7FeatureDefinition(Part work, Feature feature)
    {
        Dictionary<string, object> record = new Dictionary<string, object> { { "journal_id", RequiredJournalId(feature, "native feature") }, { "feature_type", feature.FeatureType } };
        NXOpen.Features.Thread thread = feature as NXOpen.Features.Thread;
        if (thread != null)
        {
            ThreadBuilder builder = work.Features.CreateThreadBuilder(thread);
            try { record.Add("thread", M7ThreadDefinition(builder)); }
            finally { builder.Destroy(); }
        }
        else if (M7IsSweep(feature))
        {
            StringList guides, section, spine; MethodList orientation, scaling; int alignment, interpolation, bodyType; double[] tolerance = new double[3];
            UFSession.GetUFSession().Modl.AskSweep(feature.Tag, out guides, out section, out spine, out orientation, out scaling,
                out alignment, out interpolation, out bodyType, tolerance);
            record.Add("guides", M7StringDefinition(guides)); record.Add("section", M7StringDefinition(section)); record.Add("spine", M7StringDefinition(spine));
            record.Add("orientation", M7MethodDefinition(orientation)); record.Add("scaling", M7MethodDefinition(scaling));
            record.Add("alignment", alignment); record.Add("interpolation", interpolation); record.Add("body_type", bodyType); record.Add("tolerances", tolerance);
        }
        else
        {
            FeatureSigns sign; Tag target, tool; bool retainedTarget, retainedTool;
            UFSession.GetUFSession().Modl.AskBooleanWithRetainedOptions(feature.Tag, out sign, out target, out tool, out retainedTarget, out retainedTool);
            record.Add("boolean_sign", sign.ToString()); record.Add("target_body_id", M7TagJournalId(target)); record.Add("tool_body_id", M7TagJournalId(tool));
            record.Add("retain_target", retainedTarget); record.Add("retain_tool", retainedTool);
            record.Add("parent_ids", ObjectStrings(GetJournalIds(feature.GetParents(), "boolean parent")));
        }
        record.Add("feature_errors", feature.GetFeatureErrorMessages());
        return (Dictionary<string, object>)M7NormalizeNativeValue(record);
    }
    // Existing semantic comparison intentionally accepts List<object>, not raw CLR arrays.
    // Normalize every UF array so live revision and save/reopen comparison use values.
    private static object M7NormalizeNativeValue(object value)
    {
        Dictionary<string, object> map = value as Dictionary<string, object>;
        if (map != null)
        {
            Dictionary<string, object> normalized = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> item in map) normalized.Add(item.Key, M7NormalizeNativeValue(item.Value));
            return normalized;
        }
        System.Collections.IList list = value as System.Collections.IList;
        if (list != null)
        {
            List<object> normalized = new List<object>(); foreach (object item in list) normalized.Add(M7NormalizeNativeValue(item));
            return normalized;
        }
        return value;
    }
    private static Dictionary<string, object> M7ThreadDefinition(ThreadBuilder b)
    {
        Face cylinder = b.CylindricalFace.Value as Face, start = b.StartObject.Value as Face;
        return new Dictionary<string, object> { { "input", b.ThreadInput.ToString() }, { "mode", b.ThreadType.ToString() },
            { "internal", b.IsInternalThread }, { "starts", b.NumStarts }, { "handedness", b.ThreadHandedness.ToString() },
            { "reverse_direction", b.ReverseThreadDirection }, { "limit", b.ThreadLimit.ToString() }, { "shaft_preference", b.ShaftPreference.ToString() },
            { "major_diameter", M7ExpressionDefinition(b.MajorDiameterExp) }, { "minor_diameter", M7ExpressionDefinition(b.MinorDiameterExp) },
            { "shaft_diameter", M7ExpressionDefinition(b.ShaftDiameterExp) }, { "pitch", M7ExpressionDefinition(b.PitchExp) },
            { "angle", M7ExpressionDefinition(b.AngleExp) }, { "length", M7ExpressionDefinition(b.ThreadLength) },
            { "cylinder_face_id", cylinder == null ? null : RequiredJournalId(cylinder, "thread cylinder") },
            { "start_face_id", start == null ? null : RequiredJournalId(start, "thread start face") },
            { "cylinder_native_geometry", cylinder == null ? null : M7FaceDefinition(cylinder) },
            { "start_face_native_geometry", start == null ? null : M7FaceDefinition(start) } };
    }
    private static Dictionary<string, object> M7ExpressionDefinition(Expression expression)
    {
        return new Dictionary<string, object> { { "name", expression.Name }, { "rhs", expression.RightHandSide }, { "value", expression.Value } };
    }
    private static Dictionary<string, object> M7FaceDefinition(Face face)
    {
        int type, norm; double radius, radData; double[] point = new double[3], axis = new double[3], box = new double[6];
        UFSession.GetUFSession().Modl.AskFaceData(face.Tag, out type, point, axis, box, out radius, out radData, out norm);
        double magnitude = Length3(axis);
        if (magnitude < 1e-12) throw new M2ModelException("MODEL_READ_FAILED", "native analytic face has no axis or normal");
        for (int i = 0; i < 3; i++) axis[i] /= magnitude;
        // UF may return another representative point after SaveAs/reopen. The
        // support plane or axis line is invariant; a point on either is not.
        int sign = 1; for (int i = 0; i < 3; i++) if (Math.Abs(axis[i]) > 1e-12) { sign = axis[i] < 0 ? -1 : 1; break; }
        for (int i = 0; i < 3; i++) axis[i] *= sign;
        double projection = point[0] * axis[0] + point[1] * axis[1] + point[2] * axis[2];
        double[] perpendicular = new double[3]; for (int i = 0; i < 3; i++) perpendicular[i] = point[i] - projection * axis[i];
        Dictionary<string, object> record = new Dictionary<string, object> { { "type", type },
            { "canonical_axis", M7Rounded(axis, 9) }, { "exact_bbox_mm", M7Rounded(M7ExactBox(face.Tag), 6) } };
        if (type == UFConstants.UF_MODL_PLANAR_FACE)
        {
            record.Add("plane_offset_mm", Math.Round(projection, 6));
            record.Add("oriented_normal", M7Rounded(new double[] { axis[0] * sign, axis[1] * sign, axis[2] * sign }, 9));
        }
        else
        {
            record.Add("axis_perpendicular_origin_mm", M7Rounded(perpendicular, 6));
            if (type == UFConstants.UF_MODL_CYLINDRICAL_FACE) record.Add("radius_mm", Math.Round(radius, 6));
            if (type == UFConstants.UF_MODL_CONICAL_FACE) record.Add("half_angle_radians", Math.Round(radData, 9));
        }
        return record;
    }
    private static double[] M7Rounded(double[] values, int places)
    {
        double[] result = new double[values.Length]; for (int i = 0; i < values.Length; i++) result[i] = Math.Round(values[i], places);
        return result;
    }
    private static string M7TagJournalId(Tag tag)
    {
        if (tag == Tag.Null) return null;
        NXObject obj = NXOpen.Utilities.NXObjectManager.Get(tag) as NXObject;
        if (obj == null) throw new M2ModelException("MODEL_READ_FAILED", "native association has no NX object identity");
        return RequiredJournalId(obj, "native association");
    }
    private static Dictionary<string, object> M7MethodDefinition(MethodList method)
    {
        List<object> curves = new List<object>(); if (method.s_curve != null) foreach (StringList list in method.s_curve) curves.Add(M7StringDefinition(list));
        return new Dictionary<string, object> { { "method", method.method }, { "interpolation", method.inter }, { "object_id", M7TagJournalId(method.id) }, { "value", method.value }, { "curves", curves } };
    }
    private static Dictionary<string, object> M7StringDefinition(StringList strings)
    {
        List<Dictionary<string, object>> curves = new List<Dictionary<string, object>>(); List<object> orderedIds = new List<object>();
        if (strings.id != null) foreach (Tag tag in strings.id)
        {
            NXObject obj = NXOpen.Utilities.NXObjectManager.Get(tag) as NXObject;
            if (obj == null) throw new M2ModelException("MODEL_READ_FAILED", "sweep curve could not be resolved");
            string id = RequiredJournalId(obj, "sweep curve"); orderedIds.Add(id);
            curves.Add(M7CurveDefinition(obj));
        }
        SortRecords(curves, "journal_id", "native sweep curve");
        return new Dictionary<string, object> { { "string_count", strings.num }, { "segment_counts", strings._string }, { "directions", strings.dir }, { "ordered_curve_ids", orderedIds }, { "curve_definitions", ToObjectList(curves) } };
    }
    private static Dictionary<string, object> M7CurveDefinition(NXObject obj)
    {
        Dictionary<string, object> record = new Dictionary<string, object> { { "journal_id", RequiredJournalId(obj, "native curve") }, { "object_type", obj.GetType().Name } };
        Line line = obj as Line; Spline spline = obj as Spline;
        if (line != null) { record.Add("start", Point(line.StartPoint)); record.Add("end", Point(line.EndPoint)); }
        else if (spline != null)
        {
            UFCurve.Spline native; UFSession.GetUFSession().Curve.AskSplineData(spline.Tag, out native);
            List<object> poles = new List<object>();
            for (int i = 0; i < native.poles.GetLength(0); i++)
            {
                List<object> pole = new List<object>(); for (int j = 0; j < native.poles.GetLength(1); j++) pole.Add(native.poles[i, j]); poles.Add(pole);
            }
            record.Add("pole_count", native.num_poles); record.Add("order", native.order); record.Add("rational", native.is_rational);
            record.Add("knots", native.knots); record.Add("poles", poles); record.Add("start_parameter", native.start_param); record.Add("end_parameter", native.end_param);
        }
        else throw new M2ModelException("UNSUPPORTED_MODEL", "native sweep definition readback supports only line and spline curves");
        return record;
    }
}
