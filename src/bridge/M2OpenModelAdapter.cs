using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;
using NxSketchMcp.M2;

// NXOpen-backed adapter. M2Bridge calls these methods only after takeover and
// only on the WinForms dispatcher thread. Parameter edits are opt-in and limited
// to a revalidated Driving sketch dimension with a supported geometry readback.
public sealed partial class M2OpenModelAdapter : IM2ModelAdapter, IM2OperationAdapter, IM2M27ModelAdapter, IM3OperationAdapter, IM4FaceQueryAdapter, IM7HelicalOperationAdapter
{
    private const double M4FaceNormalDotTolerance = 0.999;
    private const double M4BoundaryChordToleranceMm = 0.001;
    private const double M4DomainClearanceMm = 0.002;
    private const string M4SelectorAttribute = "M4_SUPPORT_FACE_SELECTOR";
    private const string M4BodyAttribute = "M4_SUPPORT_BODY_ID";
    private const string M4DomainAttribute = "M4_SUPPORT_DOMAIN_VALIDATED";
    private const string M4BoundaryMethodAttribute = "M4_SUPPORT_BOUNDARY_METHOD";
    private const string M4CutFeatureAttribute = "M4_SUPPORT_OWN_CUT_FEATURE_IDS";

    private sealed class M4FaceSelector
    {
        internal int Axis;
        internal int Sign;
        internal string AxisName;
        internal string PositionRule;
        internal double? CoordinateMm;
        internal string Canonical;
    }

    private sealed class M4FaceLoop
    {
        internal List<M4Point2> Points;
        internal string EdgeType;
    }

    private sealed class M4Profile
    {
        internal string Kind;
        internal List<M4Point2> Points;
        internal List<string> GeometryIds = new List<string>();
        internal M4Point2 Center;
        internal double Radius;
        internal bool Closed;
    }

    private sealed class M4LineSegment
    {
        internal M4Point2 First;
        internal M4Point2 Last;
        internal string Id;
    }

    private struct M4Point2
    {
        internal double X;
        internal double Y;
        internal M4Point2(double x, double y) { X = x; Y = y; }
    }

    private sealed class M4FaceCandidate
    {
        internal Face Face;
        internal Body Body;
        internal string BodyId;
        internal string FaceId;
        internal double PlaneCoordinate;
        internal double PlaneOffset;
        internal double[] Normal;
        internal double[] Origin;
        internal double[] XAxis;
        internal double[] YAxis;
        internal double[] Bbox;
        internal double[] Centroid;
        internal double Area;
        internal List<M4FaceLoop> Loops;
        internal List<string> BoundaryTypes;
        internal List<M4Profile> SupportProfiles;
        internal bool BoundarySupported;
        internal string BoundaryReason;
        internal Dictionary<string, string> FeatureJournalIdsByTag;
        internal List<object> ExcludedCutFeatures;
        internal List<object> EdgeFeatureProvenance = new List<object>();
        internal List<object> LoopProvenance = new List<object>();
    }

    private sealed class M4SupportTrack
    {
        internal string SketchId;
        internal string BodyId;
        internal string Selector;
        internal string FaceId;
        internal string FeatureId;
        internal Plane Plane;
        internal bool DomainValidated;
        internal bool Native;
        internal string GeometryKind;
        internal double[] SupportOrigin;
        internal List<string> CutFeatureIds = new List<string>();
    }

    private sealed class BindingState
    {
        internal Session Session;
        internal Part WorkPart;
        internal string PartTag;
        internal string PartPath;
        internal string CanonicalPath;
        internal Dictionary<string, object> SupportSummary;
        internal string NxBuild;
        internal string NxBuildSource;
        internal string CanonicalSnapshot;
        internal List<object> CanonicalBodyMeasurements;
        internal int RevisionNumber;
        internal Dictionary<string, object> ReadEffects;
        internal readonly Dictionary<string, M4SupportTrack> M4SupportTracks =
            new Dictionary<string, M4SupportTrack>(StringComparer.Ordinal);
    }

    private sealed class SnapshotResult
    {
        internal Dictionary<string, object> Snapshot;
        internal Dictionary<string, object> ReadEffects;
        internal bool SemanticChanged;
    }

    private sealed class ParameterPlanState
    {
        internal BindingState Binding;
        internal Sketch Sketch;
        internal SketchDimensionalConstraint Dimension;
        internal Expression Expression;
        internal Unit Unit;
        internal string RevisionId;
        internal string SemanticSnapshot;
        internal string FullGeometrySnapshot;
        internal List<object> HistorySnapshot;
        internal List<object> AssociationSnapshot;
        internal string SketchId;
        internal string ConstraintId;
        internal string SketchFeatureId;
        internal string ExpressionName;
        internal string ExpressionRightHandSide;
        internal string UnitSymbol;
        internal string DimensionType;
        internal string GeometrySignature;
        internal string GeometryKind;
        internal List<string> GeometryRefs;
        internal double CurrentExpressionValue;
        internal double GeometryValue;
        internal double RequestedValue;
        internal string RequestedValueText;
        internal string RequestedUnit;
        internal Dictionary<string, object> GeometryBefore;
        internal List<object> M4SupportAssociationsBefore;
        internal bool HasM4SupportAssociations;
    }

    private sealed class ParameterTarget
    {
        internal Sketch Sketch;
        internal SketchDimensionalConstraint Dimension;
        internal Expression Expression;
        internal Unit Unit;
        internal string SketchId;
        internal string ConstraintId;
        internal string ExpressionRightHandSide;
        internal string UnitSymbol;
        internal string DimensionType;
        internal string DimensionState;
        internal string GeometrySignature;
        internal string GeometryKind;
        internal List<string> GeometryRefs;
        internal double ExpressionValue;
        internal double GeometryValue;
        internal Dictionary<string, object> GeometryReadback;
    }

    private sealed class M3DimensionState
    {
        internal SketchDimensionalConstraint Dimension;
        internal string GeometryId;
        internal string Role;
        internal double ExpectedValue;
    }

    private sealed class M3PrimitiveState
    {
        internal string RequestId;
        internal string Type;
        internal Curve[] Curves;
        internal List<M3DimensionState> Dimensions;
        internal double[] LocalPoints;
        internal double Width;
        internal double Height;
        internal double Diameter;
    }

    private sealed class M3BodyMeasure
    {
        internal Body Body;
        internal string Id;
        internal bool IsSolid;
        internal double? VolumeMm3;
        internal double[] BboxMm;
        internal Dictionary<string, object> PublicRecord;
    }

    private sealed class M3PlanState
    {
        internal BindingState BindingState;
        internal string Kind;
        internal string RevisionId;
        internal string SemanticBefore;
        internal string GeometryBefore;
        internal List<object> HistoryBefore;
        internal Dictionary<string, object> SketchRecordBefore;
        internal List<M3BodyMeasure> BodiesBefore;
        internal M3SketchRequest SketchRequest;
        internal M3ExtrudeRequest ExtrudeRequest;
        internal Sketch Sketch;
        internal Feature Feature;
        internal Body TargetBody;
        internal string SketchId;
        internal string FeatureId;
        internal string TargetBodyId;
        internal string AffectedBodyId;
        internal double SectionHelpX;
        internal double SectionHelpY;
        internal List<M3PrimitiveState> Primitives;
        internal List<M3BodyMeasure> BodiesAfter;
        internal string SemanticAfter;
        internal string GeometryAfter;
        internal List<object> HistoryAfter;
        internal List<string> ConstraintIds;
        internal List<object> DrivingDimensions;
        internal Dictionary<string, object> ExtrudeDefinitionEvidence;
        internal M3ExecutionResult Execution;
        internal Sketch ActiveSketchBefore;
        internal M4FaceCandidate SupportFace;
        internal Plane SupportPlane;
        internal double[] SupportFrameOrigin;
        internal double[] SupportFrameX;
        internal double[] SupportFrameY;
        internal double[] SupportFrameNormal;
        internal bool SupportDomainValidated;
        internal Dictionary<string, object> SupportAssociationEvidence;
        internal Dictionary<string, object> ExecutionFailureEvidence;
    }

    public M2ModelBinding Takeover(string expectedPartPath, string artifactScope, string sessionEpoch,
        string loadInstanceId, int bindingEpoch)
    {
        bool currentWorkPartScope = String.Equals(artifactScope, "current_work_part", StringComparison.Ordinal);
        if (!currentWorkPartScope && !String.Equals(artifactScope, "saved_independent_copy", StringComparison.Ordinal))
            throw new M2ModelException("INVALID_REQUEST", "artifact_scope must be saved_independent_copy or current_work_part");
        if (String.IsNullOrWhiteSpace(expectedPartPath) && !currentWorkPartScope)
            throw new M2ModelException("INVALID_REQUEST", "expected_part_path is required for saved_independent_copy");

        try
        {
            UI ui = UI.GetUI();
            if (ui.AskLockStatus() != UI.Status.Unlock || !ui.CanOpenPart())
                throw new M2ModelException("NX_BUSY", "NX UI is busy; no model read was started");

            Session session = Session.GetSession();
            if (session == null) throw new M2ModelException("MODEL_READ_FAILED", "NX Session is unavailable");
            if (session.IsInTaskEnvironment)
                throw new M2ModelException("NX_BUSY", "NX task environment is active; no model read was started");

            Part workPart = session.Parts.Work;
            if (workPart == null) throw new M2ModelException("NO_WORK_PART", "NX has no active Work Part");

            string partPath = workPart.FullPath ?? String.Empty;
            string canonicalPath = CanonicalPartIdentityPath(partPath);
            if (!PartIdentityPathMatches(partPath, expectedPartPath))
                throw new M2ModelException("SESSION_MISMATCH", "active Work Part path does not match expected_part_path");

            bool modifiedBefore = workPart.IsModified;
            if (!currentWorkPartScope && modifiedBefore)
                throw new M2ModelException("UNSUPPORTED_ARTIFACT_SCOPE", "active Work Part is modified; takeover requires a saved, clean independent copy");
            if (workPart.ComponentAssembly != null && workPart.ComponentAssembly.RootComponent != null)
                throw new M2ModelException("ASSEMBLY_UNSUPPORTED", "M5 supports a standalone native part; assembly Work Parts are not supported");
            bool ugmanagerActive;
            UFSession.GetUFSession().UF.IsUgmanagerActive(out ugmanagerActive);
            if (ugmanagerActive || partPath.StartsWith("@DB", StringComparison.OrdinalIgnoreCase))
                throw new M2ModelException("TEAMCENTER_UNSUPPORTED", "Teamcenter managed parts are outside the M5 independent-part scope");
            Dictionary<string, object> supportSummary = BuildArtifactSupportSummary(workPart, artifactScope);
            string rejection = Convert.ToString(supportSummary["rejection_code"], CultureInfo.InvariantCulture);
            if (!String.IsNullOrEmpty(rejection))
                throw new M2ModelException(rejection,
                    Convert.ToString(supportSummary["rejection_reason"], CultureInfo.InvariantCulture));
            if (!currentWorkPartScope && !File.Exists(partPath))
                throw new M2ModelException("UNSUPPORTED_ARTIFACT_SCOPE", "saved_independent_copy requires an existing native .prt file");
            if (!currentWorkPartScope && !String.Equals(Path.GetExtension(partPath), ".prt", StringComparison.OrdinalIgnoreCase))
                throw new M2ModelException("UNSUPPORTED_ARTIFACT_SCOPE", "the active Work Part is not a native .prt part");

            string partTag = workPart.Tag.ToString();
            if (String.IsNullOrEmpty(partTag))
                throw new M2ModelException("UNSUPPORTED_MODEL", "active Work Part has no stable NX Tag");

            BindingState state = new BindingState();
            state.Session = session;
            state.WorkPart = workPart;
            state.PartTag = partTag;
            state.PartPath = partPath;
            state.CanonicalPath = canonicalPath;
            state.SupportSummary = supportSummary;
            state.NxBuild = GetNxBuild(session);
            state.NxBuildSource = String.IsNullOrEmpty(state.NxBuild)
                ? "NX_FULL_VERSION unavailable"
                : "Session.GetEnvironmentVariableValue(NX_FULL_VERSION)";
            state.RevisionNumber = 0;

            M2ModelBinding binding = new M2ModelBinding();
            binding.SessionEpoch = sessionEpoch;
            binding.LoadInstanceId = loadInstanceId;
            binding.BindingEpoch = bindingEpoch;
            binding.ArtifactScope = artifactScope;
            binding.ArtifactKind = "standalone_part";
            binding.ArtifactAssessment = supportSummary;
            binding.IsReadOnly = workPart.IsReadOnly;
            binding.PartPath = partPath;
            binding.PartTag = partTag;
            binding.NxBuild = state.NxBuild;
            binding.NxBuildSource = state.NxBuildSource;
            binding.AdapterState = state;

            SnapshotResult captured = Capture(binding, state);
            bool modifiedAfterTakeoverRead = workPart.IsModified;
            captured.ReadEffects["is_modified_before_read"] = modifiedBefore;
            captured.ReadEffects["is_modified_after_read"] = modifiedAfterTakeoverRead;
            captured.ReadEffects["dirty_changed_by_read"] = !modifiedBefore && modifiedAfterTakeoverRead;
            captured.Snapshot["support_summary"] = supportSummary;
            binding.RevisionId = state.RevisionNumber.ToString(CultureInfo.InvariantCulture);
            binding.CanonicalSnapshot = state.CanonicalSnapshot;
            binding.Identity = BuildIdentity(binding, workPart, captured.ReadEffects);
            return binding;
        }
        catch (M2ModelException) { throw; }
        catch (Exception ex)
        {
            throw new M2ModelException("MODEL_READ_FAILED", "NX takeover inspection failed (" + ex.GetType().Name + ")");
        }
    }

    public bool IsBindingCurrent(M2ModelBinding binding, out string reason)
    {
        reason = null;
        BindingState state = binding == null ? null : binding.AdapterState as BindingState;
        if (state == null)
        {
            reason = "model binding has no NX adapter state";
            return false;
        }
        try
        {
            Session currentSession = Session.GetSession();
            if (!Object.ReferenceEquals(currentSession, state.Session))
            {
                reason = "NX Session identity changed after takeover";
                return false;
            }
            Part currentPart = currentSession.Parts.Work;
            if (currentPart == null)
            {
                reason = "NX Work Part was closed after takeover";
                return false;
            }
            if (!String.Equals(currentPart.Tag.ToString(), state.PartTag, StringComparison.Ordinal))
            {
                reason = "NX Work Part Tag changed after takeover";
                return false;
            }
            if (!String.Equals(CanonicalPartIdentityPath(currentPart.FullPath), state.CanonicalPath, StringComparison.OrdinalIgnoreCase))
            {
                reason = "NX Work Part path changed after takeover";
                return false;
            }
            if (binding.BindingEpoch <= 0 || String.IsNullOrEmpty(binding.SessionEpoch) || String.IsNullOrEmpty(binding.LoadInstanceId))
            {
                reason = "NX binding epoch or session identity is invalid";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = "NX identity could not be verified (" + ex.GetType().Name + ")";
            return false;
        }
    }

    public Dictionary<string, object> GetSession(M2ModelBinding binding)
    {
        BindingState state = RequireState(binding);
        SnapshotResult captured = Capture(binding, state);
        Part part = state.WorkPart;
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("nx_build", state.NxBuild ?? "UNKNOWN");
        result.Add("nx_build_source", state.NxBuildSource);
        result.Add("nxopen_assembly_version", typeof(Session).Assembly.GetName().Version.ToString());
        result.Add("nxopen_file_version", GetNxOpenFileVersion());
        result.Add("release_number", state.Session.ReleaseNumber.ToString(CultureInfo.InvariantCulture));
        result.Add("full_release_number", state.Session.FullReleaseNumber.ToString(CultureInfo.InvariantCulture));
        result.Add("application_name", state.Session.ApplicationName ?? String.Empty);
        result.Add("ugii_version", Environment.GetEnvironmentVariable("UGII_VERSION") ?? String.Empty);
        result.Add("process_id", Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
        result.Add("session_epoch", binding.SessionEpoch);
        result.Add("load_instance_id", binding.LoadInstanceId);
        result.Add("binding_epoch", binding.BindingEpoch);
        result.Add("part_path", state.PartPath);
        result.Add("part_tag", state.PartTag);
        result.Add("part_units", part.PartUnits.ToString());
        result.Add("part_load_state", part.PartLoadState.ToString());
        result.Add("is_read_only", IsPartReadOnly(part));
        result.Add("artifact_scope", binding.ArtifactScope);
        result.Add("artifact_kind", binding.ArtifactKind);
        result.Add("support_summary", Clone(binding.ArtifactAssessment));
        result.Add("is_modified_before_read", captured.ReadEffects["is_modified_before_read"]);
        result.Add("is_modified_after_read", captured.ReadEffects["is_modified_after_read"]);
        result.Add("dirty_changed_by_read", captured.ReadEffects["dirty_changed_by_read"]);
        result.Add("read_may_mark_modified", true);
        result.Add("revision_id", binding.RevisionId);
        result.Add("semantic_changed_since_previous_read", captured.SemanticChanged);
        result.Add("support", SupportStatus("SUPPORTED", null));
        return result;
    }

    public Dictionary<string, object> InspectPart(M2ModelBinding binding)
    {
        BindingState state = RequireState(binding);
        SnapshotResult captured = Capture(binding, state);
        Dictionary<string, object> result = Clone(captured.Snapshot);
        result["read_effects"] = captured.ReadEffects;
        result["revision_id"] = binding.RevisionId;
        result["semantic_changed_since_previous_read"] = captured.SemanticChanged;
        return result;
    }

    public Dictionary<string, object> InspectSketch(M2ModelBinding binding, string sketchId)
    {
        if (String.IsNullOrWhiteSpace(sketchId))
            throw new M2ModelException("INVALID_REQUEST", "sketch_id is required");
        BindingState state = RequireState(binding);
        SnapshotResult captured = Capture(binding, state);
        List<object> sketches = captured.Snapshot["sketches"] as List<object>;
        if (sketches == null) throw new M2ModelException("MODEL_READ_FAILED", "sketch inventory is unavailable");

        Dictionary<string, object> found = null;
        foreach (object item in sketches)
        {
            Dictionary<string, object> sketch = item as Dictionary<string, object>;
            if (sketch == null) throw new M2ModelException("MODEL_READ_FAILED", "sketch inventory contains an invalid record");
            if (String.Equals(Convert.ToString(sketch["journal_id"], CultureInfo.InvariantCulture), sketchId, StringComparison.Ordinal))
            {
                found = sketch;
                break;
            }
        }
        if (found == null) throw new M2ModelException("SKETCH_NOT_FOUND", "sketch_id does not identify a Sketch in the bound Work Part");

        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("sketch", found);
        result.Add("part_path", state.PartPath);
        result.Add("part_tag", state.PartTag);
        result.Add("part_units", state.WorkPart.PartUnits.ToString());
        result.Add("read_effects", captured.ReadEffects);
        result.Add("revision_id", binding.RevisionId);
        result.Add("semantic_changed_since_previous_read", captured.SemanticChanged);
        object supportAssociation;
        if (found.TryGetValue("support_association", out supportAssociation))
            result.Add("support_association", supportAssociation);
        object supportFrame;
        if (found.TryGetValue("support_frame", out supportFrame))
            result.Add("support_frame", supportFrame);
        object profileGeometry;
        if (found.TryGetValue("geometry", out profileGeometry))
            result.Add("profile_geometry", profileGeometry);
        return result;
    }

    public Dictionary<string, object> QueryFaces(M2ModelBinding binding, string revisionId,
        string selectorText, string bodyId)
    {
        if (String.IsNullOrWhiteSpace(selectorText))
            throw new M2ModelException("INVALID_REQUEST", "selector is required");
        BindingState state = RequireState(binding);
        RequireWritableSimplePart(state.WorkPart);
        RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
        SnapshotResult captured = Capture(binding, state);
        if (!String.Equals(revisionId, binding.RevisionId, StringComparison.Ordinal))
            throw new M2ModelException("STALE_REVISION", "revision_id changed while querying planar faces");

        M4FaceSelector selector;
        if (!TryParseM4FaceSelector(selectorText, out selector))
            return M4UnsupportedSelectorResult(selectorText, binding.RevisionId);

        List<M4FaceCandidate> candidates = ResolveM4Faces(state.WorkPart, selector, bodyId,
            null, null, null, null);
        if (selector.PositionRule == "outermost" && candidates.Count > 0)
        {
            double extreme = candidates[0].PlaneCoordinate;
            for (int i = 1; i < candidates.Count; i++)
                extreme = selector.Sign > 0 ? Math.Max(extreme, candidates[i].PlaneCoordinate) : Math.Min(extreme, candidates[i].PlaneCoordinate);
            candidates.RemoveAll(delegate(M4FaceCandidate item)
            {
                return Math.Abs(item.PlaneCoordinate - extreme) > 1e-6;
            });
        }
        else if (selector.PositionRule == "at" && candidates.Count > 0)
        {
            double expected = selector.CoordinateMm.Value;
            candidates.RemoveAll(delegate(M4FaceCandidate item)
            {
                return Math.Abs(item.PlaneCoordinate - expected) > 1e-5;
            });
        }
        SortM4FaceCandidates(candidates);

        string status = candidates.Count == 1 ? "unique" : (candidates.Count > 1 ? "ambiguous" : "none");
        List<object> publicCandidates = new List<object>();
        for (int i = 0; i < candidates.Count; i++)
        {
            Dictionary<string, object> item = M4FaceCandidateRecord(candidates[i]);
            item["candidate_id"] = "candidate-" + (i + 1).ToString("D3", CultureInfo.InvariantCulture);
            publicCandidates.Add(item);
        }
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("selection_status", status);
        result.Add("selector_normalized", M4SelectorRecord(selector));
        result.Add("candidate_count", candidates.Count);
        result.Add("candidates", publicCandidates);
        result.Add("clarification_required", candidates.Count != 1);
        result.Add("revision_id", binding.RevisionId);
        result.Add("supported_selector_examples", M4SupportedSelectorExamples());
        return result;
    }

    public M2ParameterPlanSnapshot PrepareParameterEdit(M2ModelBinding binding, M2ParameterEditRequest request)
    {
        if (request == null) throw new M2ModelException("INVALID_REQUEST", "parameter edit request is required");
        if (String.IsNullOrWhiteSpace(request.RevisionId) || String.IsNullOrWhiteSpace(request.SketchId) ||
            String.IsNullOrWhiteSpace(request.ConstraintId) || String.IsNullOrWhiteSpace(request.ValueText) ||
            String.IsNullOrWhiteSpace(request.Unit))
            throw new M2ModelException("INVALID_REQUEST", "revision_id, sketch_id, constraint_id, value_text and unit are required");

        double requestedValue = ParsePositiveMillimeters(request.ValueText, "value_text");
        BindingState state = RequireState(binding);
        RequireWritableSimplePart(state.WorkPart);
        string bindingReason;
        if (!IsBindingCurrent(binding, out bindingReason))
            throw new M2ModelException("STALE_BINDING", bindingReason ?? "NX model binding is no longer current");
        if (!String.Equals(request.RevisionId, binding.RevisionId, StringComparison.Ordinal))
            throw new M2ModelException("STALE_REVISION", "revision_id does not match the current captured model revision");

        SnapshotResult preparedCapture;
        try { preparedCapture = Capture(binding, state); }
        catch (M2ModelException) { throw; }
        catch (Exception ex)
        {
            throw new M2ModelException("PARAMETER_PREPARE_FAILED", "could not refresh the model revision (" + ex.GetType().Name + ")");
        }
        if (!String.Equals(request.RevisionId, binding.RevisionId, StringComparison.Ordinal))
            throw new M2ModelException("STALE_REVISION", "model semantics changed while preparing the parameter edit");
        RefreshM4SupportTracksFromPart(state.WorkPart, state);
        RequireM4SubtractContourEditSupported(state.WorkPart, state, request.SketchId);

        ParameterTarget target;
        string fullGeometrySnapshot;
        try
        {
            RequireMillimeterPartAndUnit(state.WorkPart, request.Unit, null);
            target = ResolveParameterTarget(state.WorkPart, request.SketchId, request.ConstraintId);
            if (target.Sketch.Feature == null || target.Sketch.Feature.IsOccurrence ||
                target.Sketch.Feature.OwningPart == null || target.Sketch.Feature.OwningPart.Tag != state.WorkPart.Tag)
                throw new M2ModelException("SKETCH_HISTORY_UNAVAILABLE", "the selected Sketch has no native feature history; only supported operations that append new features are available");
            fullGeometrySnapshot = CaptureFullGeometry(state.WorkPart);
        }
        catch (M2ModelException) { throw; }
        catch (Exception ex)
        {
            throw new M2ModelException("PARAMETER_PREPARE_FAILED", "selected dimension could not be inspected safely (" + ex.GetType().Name + ")");
        }
        if (!String.Equals(target.UnitSymbol, request.Unit, StringComparison.Ordinal))
            throw new M2ModelException("UNIT_MISMATCH", "requested unit must exactly match the associated expression unit symbol");
        if (!NearlyEqual(target.ExpressionValue, target.GeometryValue))
            throw new M2ModelException("UNSUPPORTED_PARAMETER", "associated expression value does not match the measured target geometry");

        ParameterPlanState planState = new ParameterPlanState();
        planState.Binding = state;
        planState.SemanticSnapshot = CanonicalSemanticSnapshot(preparedCapture.Snapshot);
        planState.FullGeometrySnapshot = fullGeometrySnapshot;
        planState.HistorySnapshot = HistoryProjection(preparedCapture.Snapshot);
        planState.AssociationSnapshot = AssociationProjection(preparedCapture.Snapshot);
        planState.Sketch = target.Sketch;
        planState.Dimension = target.Dimension;
        planState.Expression = target.Expression;
        planState.Unit = target.Unit;
        planState.RevisionId = binding.RevisionId;
        planState.SketchId = target.SketchId;
        planState.ConstraintId = target.ConstraintId;
        planState.SketchFeatureId = target.Sketch.Feature == null
            ? null : RequiredJournalId(target.Sketch.Feature, "sketch feature");
        planState.ExpressionName = target.Expression.Name;
        planState.ExpressionRightHandSide = target.ExpressionRightHandSide;
        planState.UnitSymbol = target.UnitSymbol;
        planState.DimensionType = target.DimensionType;
        planState.GeometrySignature = target.GeometrySignature;
        planState.GeometryKind = target.GeometryKind;
        planState.GeometryRefs = CopyStrings(target.GeometryRefs);
        planState.CurrentExpressionValue = target.ExpressionValue;
        planState.GeometryValue = target.GeometryValue;
        planState.RequestedValue = requestedValue;
        planState.RequestedValueText = request.ValueText.Trim();
        planState.RequestedUnit = request.Unit;
        planState.GeometryBefore = Clone(target.GeometryReadback);
        planState.M4SupportAssociationsBefore = M4SupportSnapshot(state);
        planState.HasM4SupportAssociations = planState.M4SupportAssociationsBefore.Count > 0;

        M2ParameterPlanSnapshot snapshot = new M2ParameterPlanSnapshot();
        snapshot.RevisionId = binding.RevisionId;
        snapshot.SketchId = target.SketchId;
        snapshot.ConstraintId = target.ConstraintId;
        snapshot.DimensionType = target.DimensionType;
        snapshot.DimensionState = target.DimensionState;
        snapshot.SketchFeatureId = planState.SketchFeatureId;
        snapshot.ExpressionName = target.Expression.Name;
        snapshot.ExpressionRightHandSide = target.ExpressionRightHandSide;
        snapshot.CurrentValue = NumberText(target.ExpressionValue);
        snapshot.Unit = target.UnitSymbol;
        snapshot.GeometryRefs = CopyStrings(target.GeometryRefs);
        snapshot.AdapterState = planState;
        return snapshot;
    }

    public M2ParameterExecutionResult ExecuteParameterEdit(M2ModelBinding binding,
        M2ParameterPlanSnapshot prepared, string requestedValue, string requestedUnit)
    {
        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("update_error_count", -1);
        evidence.Add("rollback_update_error_count", -1);
        evidence.Add("readback_verified", false);
        evidence.Add("rollback_readback_verified", false);
        evidence.Add("write_attempted", false);
        evidence.Add("after_rhs", null);
        evidence.Add("after_value", null);
        evidence.Add("after_unit", null);
        evidence.Add("geometry_after", null);

        ParameterPlanState plan = prepared == null ? null : prepared.AdapterState as ParameterPlanState;
        if (plan == null)
            return ParameterResult("UNCHANGED", binding == null ? null : binding.RevisionId, evidence,
                "INVALID_PLAN", "prepared parameter edit has no NX adapter state");

        AddBeforeEvidence(evidence, plan);
        BindingState state = null;
        try { state = RequireState(binding); }
        catch (M2ModelException ex)
        {
            return ParameterResult("UNCHANGED", binding == null ? null : binding.RevisionId, evidence, ex.Code, ex.Message);
        }
        if (!Object.ReferenceEquals(state, plan.Binding))
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "STALE_BINDING", "prepared edit belongs to a different NX model binding");
        try { RequireWritableSimplePart(state.WorkPart); }
        catch (M2ModelException ex)
        {
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, ex.Code, ex.Message);
        }
        if (!PlanFieldsMatch(prepared, plan))
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "INVALID_PLAN", "prepared plan fields do not match its retained NX state");

        double parsedRequested;
        try { parsedRequested = ParsePositiveMillimeters(requestedValue, "requested_value"); }
        catch (M2ModelException ex)
        {
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, ex.Code, ex.Message);
        }
        if (!NearlyEqual(parsedRequested, plan.RequestedValue) ||
            !String.Equals(requestedValue == null ? null : requestedValue.Trim(), plan.RequestedValueText, StringComparison.Ordinal) ||
            !String.Equals(requestedUnit, plan.RequestedUnit, StringComparison.Ordinal))
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "INVALID_REQUEST", "execution value and unit must match the prepared request");

        string bindingReason;
        if (!IsBindingCurrent(binding, out bindingReason))
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "STALE_BINDING", bindingReason ?? "NX model binding is no longer current");
        if (!String.Equals(binding.RevisionId, plan.RevisionId, StringComparison.Ordinal))
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "STALE_REVISION", "model revision changed after parameter preparation");

        SnapshotResult currentCapture;
        try { currentCapture = Capture(binding, state); }
        catch (Exception ex)
        {
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "MODEL_READ_FAILED", "could not refresh the model revision before editing (" + ex.GetType().Name + ")");
        }
        if (!String.Equals(binding.RevisionId, plan.RevisionId, StringComparison.Ordinal))
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "STALE_REVISION", "model semantics changed after parameter preparation");
        try
        {
            if (!String.Equals(CanonicalSemanticSnapshot(currentCapture.Snapshot), plan.SemanticSnapshot, StringComparison.Ordinal) ||
                !String.Equals(CaptureFullGeometry(state.WorkPart), plan.FullGeometrySnapshot, StringComparison.Ordinal))
                return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "STALE_GEOMETRY", "part geometry or semantic state changed after parameter preparation");
        }
        catch (Exception ex)
        {
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "MODEL_READ_FAILED", "could not verify complete geometry before editing (" + ex.GetType().Name + ")");
        }

        ParameterTarget current;
        try
        {
            RequireWritableSimplePart(state.WorkPart);
            RequireMillimeterPartAndUnit(state.WorkPart, requestedUnit, plan.UnitSymbol);
            current = ResolveParameterTarget(state.WorkPart, plan.SketchId, plan.ConstraintId);
            RequireSameTarget(plan, current);
        }
        catch (M2ModelException ex)
        {
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "PARAMETER_READ_FAILED", "selected dimension could not be revalidated (" + ex.GetType().Name + ")");
        }

        if (!String.Equals(current.ExpressionRightHandSide, plan.ExpressionRightHandSide, StringComparison.Ordinal) ||
            !NearlyEqual(current.ExpressionValue, plan.CurrentExpressionValue) ||
            !NearlyEqual(current.GeometryValue, plan.GeometryValue))
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "STALE_PARAMETER", "selected expression or measured geometry changed after preparation");

        evidence["association_verified_before"] = true;
        evidence["unit_verified_before"] = true;
        if (NearlyEqual(parsedRequested, current.GeometryValue) && NearlyEqual(parsedRequested, current.ExpressionValue))
        {
            SetFinalReadbackEvidence(evidence, current);
            evidence["readback_verified"] = true;
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, null, null);
        }

        Session.UndoMarkId mark;
        try { mark = state.Session.SetUndoMark(Session.MarkVisibility.Invisible, "M2 selected sketch dimension edit"); }
        catch (Exception ex)
        {
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "UPDATE_FAILED", "could not create an NX update mark (" + ex.GetType().Name + ")");
        }

        int updateErrors = -1;
        bool updateCallReturned = false;
        string operationError = null;
        ParameterTarget after = null;
        bool m4SupportsValid = true;
        List<object> m4SupportEvidence = new List<object>();
        try
        {
            evidence["write_attempted"] = true;
            state.WorkPart.Expressions.EditExpressionWithUnits(current.Expression, current.Unit, plan.RequestedValueText);
            updateErrors = state.Session.UpdateManager.DoUpdate(mark);
            updateCallReturned = true;
            evidence["update_error_count"] = updateErrors;
            after = ResolveParameterTarget(state.WorkPart, plan.SketchId, plan.ConstraintId);
            RequireSameTarget(plan, after);
            m4SupportsValid = ValidateM4SupportTracks(state, out m4SupportEvidence, null);
            evidence["support_associations_before"] = plan.M4SupportAssociationsBefore ?? new List<object>();
            evidence["support_associations_after"] = m4SupportEvidence;
            evidence["support_associations_verified"] = m4SupportsValid;
            if (!m4SupportsValid) operationError = "M4_FACE_ASSOCIATION_INVALID: an attached Sketch lost its unique native support face or face-domain contract";
        }
        catch (Exception ex)
        {
            operationError = "parameter edit or verification failed (" + ex.GetType().Name + ")";
        }

        bool geometryChanged = after != null && !NearlyEqual(after.GeometryValue, plan.GeometryValue);
        bool appliedReadback = updateCallReturned && updateErrors == 0 && after != null &&
            geometryChanged && NearlyEqual(after.ExpressionValue, parsedRequested) &&
            NearlyEqual(after.GeometryValue, parsedRequested) &&
            String.Equals(after.UnitSymbol, plan.UnitSymbol, StringComparison.Ordinal) &&
            IsNumericRhsEqual(after.ExpressionRightHandSide, parsedRequested) && m4SupportsValid;
        evidence["geometry_changed"] = geometryChanged;
        evidence["readback_verified"] = appliedReadback;
        if (after != null)
        {
            evidence["attempt_after_rhs"] = after.ExpressionRightHandSide;
            evidence["attempt_after_value"] = NumberText(after.ExpressionValue);
            evidence["attempt_after_unit"] = after.UnitSymbol;
            evidence["geometry_after_attempt"] = after.GeometryReadback;
            evidence["association_verified_after"] = true;
            evidence["unit_verified_after"] = String.Equals(after.UnitSymbol, plan.UnitSymbol, StringComparison.Ordinal);
        }

        if (appliedReadback)
        {
            SetFinalReadbackEvidence(evidence, after);
            try
            {
                Capture(binding, state);
                TryDeleteUndoMark(state.Session, mark);
                return ParameterResult("APPLIED", binding.RevisionId, evidence, null, null);
            }
            catch (Exception ex)
            {
                TryDeleteUndoMark(state.Session, mark);
                evidence["revision_refresh_error"] = ex.GetType().Name;
                return ParameterResult("UNKNOWN", binding.RevisionId, evidence, "POST_EDIT_READ_FAILED", "edit readback succeeded but the semantic revision could not be refreshed");
            }
        }

        if (operationError == null)
            operationError = updateCallReturned && updateErrors != 0
                ? "NX update reported errors"
                : geometryChanged ? "expression and geometry readback did not match the requested value" : "selected geometry did not move to the requested value";
        evidence["operation_error"] = operationError;
        string restorationState = TryRestoreParameter(binding, state, plan, mark, evidence);
        TryDeleteUndoMark(state.Session, mark);
        if (String.Equals(restorationState, "UNCHANGED", StringComparison.Ordinal))
        {
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "EDIT_FAILED_UNCHANGED", operationError);
        }
        if (String.Equals(restorationState, "ROLLED_BACK", StringComparison.Ordinal))
        {
            evidence["rollback_readback_verified"] = true;
            return ParameterResult("ROLLED_BACK", binding.RevisionId, evidence, "EDIT_NOT_VERIFIED", operationError);
        }
        return ParameterResult("UNKNOWN", binding.RevisionId, evidence, "ROLLBACK_UNVERIFIED", "NX edit failed and restoration could not be verified");
    }

    public M2PartVerificationResult VerifyParameterEdit(M2ModelBinding binding,
        M2ParameterPlanSnapshot prepared)
    {
        if (prepared == null) throw new M2ModelException("INVALID_PLAN", "prepared plan is required for verification");
        BindingState state = RequireState(binding);
        ParameterPlanState plan = prepared.AdapterState as ParameterPlanState;
        if (plan == null) throw new M2ModelException("INVALID_PLAN", "prepared plan has no retained NX parameter target");
        if (!PlanFieldsMatch(prepared, plan))
            throw new M2ModelException("INVALID_PLAN", "prepared plan fields do not match the retained verification state");
        string bindingReason;
        if (!IsBindingCurrent(binding, out bindingReason))
            throw new M2ModelException("STALE_BINDING", bindingReason ?? "NX model binding is no longer current");

        SnapshotResult captured = Capture(binding, state);
        ParameterTarget actual;
        try { actual = ResolveParameterTarget(state.WorkPart, plan.SketchId, plan.ConstraintId); }
        catch (M2ModelException ex)
        {
            M2PartVerificationResult rejected = VerificationFailure(binding.RevisionId, ex.Code + ": " + ex.Message);
            rejected.ErrorCode = "PART_VERIFICATION_FAILED";
            rejected.ErrorMessage = "The selected Driving expression or geometry relationship could not be resolved from the live NX model.";
            return rejected;
        }

        bool expressionMatches = String.Equals(actual.Expression.Name, plan.ExpressionName, StringComparison.Ordinal) &&
            String.Equals(actual.UnitSymbol, plan.UnitSymbol, StringComparison.Ordinal) &&
            NearlyEqual(actual.ExpressionValue, plan.RequestedValue) &&
            IsNumericRhsEqual(actual.ExpressionRightHandSide, plan.RequestedValue);
        bool geometryMatches = StringListsEqual(actual.GeometryRefs, plan.GeometryRefs) &&
            NearlyEqual(actual.GeometryValue, plan.RequestedValue);
        Dictionary<string, object> historyEvidence;
        bool selectedFeatureMatches = SelectedHistoryMatches(state.WorkPart, actual.Sketch,
            prepared.SketchFeatureId, out historyEvidence);
        List<object> currentHistory = HistoryProjection(captured.Snapshot);
        historyEvidence.Add("before", plan.HistorySnapshot);
        historyEvidence.Add("after", currentHistory);
        bool historyMatches = selectedFeatureMatches &&
            SemanticValuesEqual(plan.HistorySnapshot, currentHistory, "history");

        List<object> currentAssociations = AssociationProjection(captured.Snapshot);
        bool targetSignatureMatches = true;
        string targetSignatureReason = null;
        try { RequireSameTarget(plan, actual); }
        catch (M2ModelException ex)
        {
            targetSignatureMatches = false;
            targetSignatureReason = ex.Code + ": " + ex.Message;
        }
        bool associationMatches = targetSignatureMatches &&
            SemanticValuesEqual(plan.AssociationSnapshot, currentAssociations, "associations");
        List<object> supportAssociationsNow;
        bool supportAssociationsMatch = ValidateM4SupportTracks(state, out supportAssociationsNow, null);

        Dictionary<string, object> expressionEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        expressionEvidence.Add("sketch_id", plan.SketchId);
        expressionEvidence.Add("constraint_id", plan.ConstraintId);
        expressionEvidence.Add("expression_name", actual.Expression.Name);
        expressionEvidence.Add("expected_value", NumberText(plan.RequestedValue));
        expressionEvidence.Add("actual_rhs", actual.ExpressionRightHandSide);
        expressionEvidence.Add("actual_value", NumberText(actual.ExpressionValue));
        expressionEvidence.Add("unit", actual.UnitSymbol);

        Dictionary<string, object> geometryEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        geometryEvidence.Add("sketch_id", plan.SketchId);
        geometryEvidence.Add("constraint_id", plan.ConstraintId);
        geometryEvidence.Add("geometry_refs", ObjectStrings(actual.GeometryRefs));
        geometryEvidence.Add("expected_value", NumberText(plan.RequestedValue));
        geometryEvidence.Add("actual_value", NumberText(actual.GeometryValue));
        geometryEvidence.Add("geometry", actual.GeometryReadback);

        Dictionary<string, object> associationEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        associationEvidence.Add("sketch_id", plan.SketchId);
        associationEvidence.Add("constraint_id", plan.ConstraintId);
        associationEvidence.Add("dimension_state", actual.DimensionState);
        associationEvidence.Add("dimension_type", actual.DimensionType);
        associationEvidence.Add("expression_name", actual.Expression.Name);
        associationEvidence.Add("geometry_refs", ObjectStrings(actual.GeometryRefs));
        associationEvidence.Add("geometry_kind", actual.GeometryKind);
        associationEvidence.Add("geometry_signature", actual.GeometrySignature);
        associationEvidence.Add("unit", actual.UnitSymbol);
        associationEvidence.Add("target_signature_verified", targetSignatureMatches);
        associationEvidence.Add("target_signature_reason", targetSignatureReason);
        associationEvidence.Add("before", plan.AssociationSnapshot);
        associationEvidence.Add("after", currentAssociations);

        Dictionary<string, object> supportEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        supportEvidence.Add("before", plan.M4SupportAssociationsBefore ?? new List<object>());
        supportEvidence.Add("after", supportAssociationsNow);
        supportEvidence.Add("native_unique_domain_verified", supportAssociationsMatch);

        Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal);
        checks.Add("expression", VerificationCheck(expressionMatches ? "VERIFIED" : "MISMATCH", expressionEvidence));
        checks.Add("geometry", VerificationCheck(geometryMatches ? "VERIFIED" : "MISMATCH", geometryEvidence));
        checks.Add("history", VerificationCheck(historyMatches ? "VERIFIED" : "MISMATCH", historyEvidence));
        checks.Add("associations", VerificationCheck(associationMatches ? "VERIFIED" : "MISMATCH", associationEvidence));
        if (plan.HasM4SupportAssociations)
            checks.Add("support_associations", VerificationCheck(supportAssociationsMatch ? "VERIFIED" : "MISMATCH", supportEvidence));
        bool verified = expressionMatches && geometryMatches && historyMatches && associationMatches &&
            (!plan.HasM4SupportAssociations || supportAssociationsMatch);
        return new M2PartVerificationResult
        {
            Verified = verified,
            RevisionId = binding.RevisionId,
            Checks = checks,
            ErrorCode = verified ? null : "PART_VERIFICATION_FAILED",
            ErrorMessage = verified ? null : "The live NX expression, geometry, sketch history, or dimensional association differs from the successful execute plan."
        };
    }

    public M3PlanSnapshot PrepareSketch(M2ModelBinding binding, M3SketchRequest request)
    {
        if (request == null || request.Geometries == null || request.Geometries.Count == 0)
            throw new M2ModelException("INVALID_REQUEST", "M3 sketch requires at least one geometry");
        BindingState state = RequireState(binding);
        RequireWritableSimplePart(state.WorkPart);
        RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
        if (state.Session.ActiveSketch != null)
            throw new M2ModelException("NX_BUSY", "a Sketch is already active in NX");
        bool faceMode = !String.IsNullOrWhiteSpace(request.SupportFaceSelector);
        if (faceMode)
        {
            if (request.Plane != null || request.OriginMm != null)
                throw new M2ModelException("INVALID_REQUEST", "face sketch mode cannot include plane or origin_mm");
            if (request.FaceOriginOffsetMm == null || request.FaceOriginOffsetMm.Length != 2)
                throw new M2ModelException("INVALID_REQUEST", "face_origin_offset_mm must contain two local coordinates");
            foreach (double value in request.FaceOriginOffsetMm) EnsureFinite(value, "face_origin_offset_mm");
        }
        else
        {
            if (request.Plane != "XY" && request.Plane != "XZ" && request.Plane != "YZ")
                throw new M2ModelException("INVALID_REQUEST", "plane must be XY, XZ, or YZ");
            if (request.OriginMm == null || request.OriginMm.Length != 3)
                throw new M2ModelException("INVALID_REQUEST", "origin_mm must contain three world coordinates");
            foreach (double value in request.OriginMm) EnsureFinite(value, "origin_mm");
        }
        HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (M3SketchGeometryRequest geometry in request.Geometries)
        {
            if (geometry == null || String.IsNullOrEmpty(geometry.Id) || !ids.Add(geometry.Id))
                throw new M2ModelException("INVALID_REQUEST", "geometry records require unique non-empty ids");
            if (geometry.Type == "line")
            {
                if (geometry.StartMm == null || geometry.EndMm == null || geometry.StartMm.Length != 2 || geometry.EndMm.Length != 2)
                    throw new M2ModelException("INVALID_REQUEST", "line geometry requires 2D start_mm and end_mm");
                foreach (double value in geometry.StartMm) EnsureFinite(value, "line coordinate");
                foreach (double value in geometry.EndMm) EnsureFinite(value, "line coordinate");
                double dx = geometry.EndMm[0] - geometry.StartMm[0];
                double dy = geometry.EndMm[1] - geometry.StartMm[1];
                if (Math.Abs(dx) <= 1e-9 && Math.Abs(dy) <= 1e-9)
                    throw new M2ModelException("INVALID_REQUEST", "line start_mm and end_mm must differ");
            }
            else if (geometry.Type == "rectangle")
            {
                if (geometry.CornerMm == null || geometry.CornerMm.Length != 2 || geometry.WidthMm <= 0 || geometry.HeightMm <= 0)
                    throw new M2ModelException("INVALID_REQUEST", "rectangle requires a 2D corner and positive width_mm/height_mm");
                EnsureFinite(geometry.CornerMm[0], "rectangle corner x");
                EnsureFinite(geometry.CornerMm[1], "rectangle corner y");
                EnsureFinite(geometry.WidthMm, "rectangle width");
                EnsureFinite(geometry.HeightMm, "rectangle height");
            }
            else if (geometry.Type == "circle")
            {
                if (geometry.CenterMm == null || geometry.CenterMm.Length != 2 || geometry.DiameterMm <= 0)
                    throw new M2ModelException("INVALID_REQUEST", "circle requires a 2D center and positive diameter_mm");
                EnsureFinite(geometry.CenterMm[0], "circle center x");
                EnsureFinite(geometry.CenterMm[1], "circle center y");
                EnsureFinite(geometry.DiameterMm, "circle diameter");
            }
            else throw new M2ModelException("UNSUPPORTED_GEOMETRY", "M3 sketch supports line, rectangle, and full circle");
        }

        SnapshotResult capture = Capture(binding, state);
        if (!String.Equals(request.RevisionId, binding.RevisionId, StringComparison.Ordinal))
            throw new M2ModelException("STALE_REVISION", "revision_id does not match the current part revision");
        M3PlanState planState = CreateM3PlanState(binding, state, "create_sketch", capture);
        planState.SketchRequest = CloneM3SketchRequest(request);
        if (faceMode)
        {
            planState.SupportFace = ResolveM4UniqueFace(state.WorkPart, request.SupportFaceSelector, request.SupportBodyId);
            planState.SupportDomainValidated = ValidateM4SketchProfile(planState.SupportFace,
                request.Geometries, request.FaceOriginOffsetMm, out planState.SupportAssociationEvidence);
            planState.SupportFrameOrigin = M4OffsetFrameOrigin(planState.SupportFace, request.FaceOriginOffsetMm);
            planState.SupportFrameX = (double[])planState.SupportFace.XAxis.Clone();
            planState.SupportFrameY = (double[])planState.SupportFace.YAxis.Clone();
            planState.SupportFrameNormal = (double[])planState.SupportFace.Normal.Clone();
        }
        Dictionary<string, object> publicView = new Dictionary<string, object>(StringComparer.Ordinal);
        publicView.Add("kind", "create_sketch");
        publicView.Add("revision_id", binding.RevisionId);
        if (faceMode)
        {
            publicView.Add("support_face_selector", request.SupportFaceSelector);
            publicView.Add("support_body_id", planState.SupportFace.BodyId);
            publicView.Add("face_origin_offset_mm", NumberList(request.FaceOriginOffsetMm));
            publicView.Add("support_face", M4FaceCandidateRecord(planState.SupportFace));
            publicView.Add("support_frame", M4FrameRecord(planState.SupportFrameOrigin,
                planState.SupportFrameX, planState.SupportFrameY, planState.SupportFrameNormal));
            publicView.Add("support_domain", planState.SupportAssociationEvidence);
        }
        else
        {
            publicView.Add("plane", request.Plane);
            publicView.Add("origin_mm", NumberList(request.OriginMm));
        }
        publicView.Add("geometries", SketchGeometryPublicRecords(request.Geometries));
        publicView.Add("driving_dimensions", ExpectedDrivingDimensionRecords(request.Geometries));
        return new M3PlanSnapshot
        {
            Kind = "create_sketch",
            RevisionId = binding.RevisionId,
            PublicView = publicView,
            AdapterState = planState
        };
    }

    public M3PlanSnapshot PrepareExtrude(M2ModelBinding binding, M3ExtrudeRequest request)
    {
        if (request == null || String.IsNullOrWhiteSpace(request.SketchId))
            throw new M2ModelException("INVALID_REQUEST", "M3 Extrude requires sketch_id");
        BindingState state = RequireState(binding);
        RequireWritableSimplePart(state.WorkPart);
        RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
        if (state.Session.ActiveSketch != null)
            throw new M2ModelException("NX_BUSY", "an active Sketch must be ended before preparing an Extrude");
        ValidateExtrudeRequest(request);
        SnapshotResult capture = Capture(binding, state);
        if (!String.Equals(request.RevisionId, binding.RevisionId, StringComparison.Ordinal))
            throw new M2ModelException("STALE_REVISION", "revision_id does not match the current part revision");
        Sketch sketch = FindSketch(state.WorkPart, request.SketchId);
        double helpX, helpY;
        ValidateSingleExtrudeProfile(sketch, out helpX, out helpY);
        Body target = null;
        string targetId = null;
        if (request.Mode != "create")
        {
            target = FindBody(state.WorkPart, request.TargetBodyId);
            if (!target.IsSolidBody)
                throw new M2ModelException("UNSUPPORTED_BODY", "boolean Extrude target must identify one solid body");
            targetId = RequiredJournalId(target, "target body");
        }

        M3PlanState planState = CreateM3PlanState(binding, state, "create_extrude", capture);
        planState.ExtrudeRequest = CloneM3ExtrudeRequest(request);
        planState.Sketch = sketch;
        planState.SketchId = RequiredJournalId(sketch, "sketch");
        planState.SketchRecordBefore = Clone(FindSketchRecord(capture.Snapshot, planState.SketchId));
        planState.TargetBody = target;
        planState.TargetBodyId = targetId;
        planState.SectionHelpX = helpX;
        planState.SectionHelpY = helpY;
        Dictionary<string, object> publicView = new Dictionary<string, object>(StringComparer.Ordinal);
        publicView.Add("kind", "create_extrude");
        publicView.Add("revision_id", binding.RevisionId);
        publicView.Add("sketch_id", planState.SketchId);
        publicView.Add("mode", request.Mode);
        publicView.Add("direction", request.Direction);
        publicView.Add("extent", request.Extent);
        publicView.Add("depth_mm", request.Extent == "finite" ? (object)request.DepthMm : null);
        publicView.Add("target_body_id", targetId);
        publicView.Add("target_body_before", targetId == null ? null : FindBodyMeasure(planState.BodiesBefore, targetId).PublicRecord);
        publicView.Add("non_target_bodies_before", BodyMeasureRecords(planState.BodiesBefore, targetId));
        return new M3PlanSnapshot
        {
            Kind = "create_extrude",
            RevisionId = binding.RevisionId,
            SketchId = planState.SketchId,
            PublicView = publicView,
            AdapterState = planState
        };
    }

    public M3ExecutionResult ExecutePlan(M2ModelBinding binding, M3PlanSnapshot prepared)
    {
        if (prepared != null && prepared.AdapterState is M7PlanState) return ExecuteM7Plan(binding, prepared);
        M3PlanState plan = prepared == null ? null : prepared.AdapterState as M3PlanState;
        if (plan == null || (prepared.Kind != "create_sketch" && prepared.Kind != "create_extrude"))
            return M3Execution("UNCHANGED", binding == null ? null : binding.RevisionId, null,
                "INVALID_PLAN", "M3 plan does not contain retained NX adapter state");
        BindingState state;
        try { state = RequireState(binding); }
        catch (M2ModelException ex) { return M3Execution("UNCHANGED", binding == null ? null : binding.RevisionId, null, ex.Code, ex.Message); }
        if (!Object.ReferenceEquals(state, plan.BindingState) || !String.Equals(plan.RevisionId, prepared.RevisionId, StringComparison.Ordinal))
            return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "M3 plan belongs to a different binding or revision");
        string bindingReason;
        if (!IsBindingCurrent(binding, out bindingReason))
            return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_BINDING", bindingReason);
        try { RequireWritableSimplePart(state.WorkPart); }
        catch (M2ModelException ex)
        {
            return M3Execution("UNCHANGED", binding.RevisionId, null, ex.Code, ex.Message);
        }

        try
        {
            SnapshotResult current = Capture(binding, state);
            if (!String.Equals(CanonicalSemanticSnapshot(current.Snapshot), plan.SemanticBefore, StringComparison.Ordinal) ||
                !String.Equals(CaptureFullGeometry(state.WorkPart), plan.GeometryBefore, StringComparison.Ordinal))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "part geometry or semantic state changed after M3 plan preparation");
            Dictionary<string, object> bodyPreconditionEvidence;
            if (!BodyMeasuresEqual(plan.BodiesBefore, CaptureM3BodyMeasures(state.WorkPart), out bodyPreconditionEvidence))
                return M3Execution("UNCHANGED", binding.RevisionId, bodyPreconditionEvidence, "STALE_GEOMETRY", "body volume or bounding box changed after M3 plan preparation");
        }
        catch (Exception ex)
        {
            return M3Execution("UNCHANGED", binding.RevisionId, null, "MODEL_READ_FAILED", "could not refresh model state before M3 execution (" + ex.GetType().Name + ")");
        }

        Session.UndoMarkId mark;
        try { mark = state.Session.SetUndoMark(Session.MarkVisibility.Invisible, "M3 native sketch/extrude operation"); }
        catch (Exception ex)
        {
            return M3Execution("UNCHANGED", binding.RevisionId, null, "UPDATE_FAILED", "could not create an NX update mark (" + ex.GetType().Name + ")");
        }

        try
        {
            RequireWritableSimplePart(state.WorkPart);
            RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
            if (plan.Kind == "create_sketch")
                ExecuteM3Sketch(binding, state, plan, mark);
            else
                ExecuteM3Extrude(binding, state, plan, mark);
            return plan.Execution;
        }
        catch (Exception ex)
        {
            M3ExecutionResult rolled = RestoreM3State(binding, state, plan, mark, ex);
            plan.Execution = rolled;
            return rolled;
        }
        finally { TryDeleteUndoMark(state.Session, mark); }
    }

    public M3VerificationResult VerifyPlan(M2ModelBinding binding, M3PlanSnapshot prepared)
    {
        if (prepared != null && prepared.AdapterState is M7PlanState) return VerifyM7Plan(binding, prepared);
        M3PlanState plan = prepared == null ? null : prepared.AdapterState as M3PlanState;
        if (plan == null || plan.Execution == null || plan.Execution.State != "APPLIED")
            return M3VerificationFailure(binding == null ? null : binding.RevisionId, "M3 verification requires a successfully applied plan");
        BindingState state = RequireState(binding);
        string bindingReason;
        if (!Object.ReferenceEquals(state, plan.BindingState) || !IsBindingCurrent(binding, out bindingReason))
            return M3VerificationFailure(binding == null ? null : binding.RevisionId, "NX binding changed before M3 verification");

        SnapshotResult captured = Capture(binding, state);
        string geometryNow = CaptureFullGeometry(state.WorkPart);
        List<object> historyNow = HistoryProjection(captured.Snapshot);
        List<M3BodyMeasure> bodiesNow = CaptureM3BodyMeasures(state.WorkPart);
        bool geometryMatches;
        bool constraintsMatch;
        Dictionary<string, object> geometryEvidence;
        Dictionary<string, object> constraintEvidence;
        Dictionary<string, object> historyEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        bool historyMatches = String.Equals(plan.SemanticAfter, CanonicalSemanticSnapshot(captured.Snapshot), StringComparison.Ordinal) &&
            String.Equals(plan.GeometryAfter, geometryNow, StringComparison.Ordinal) &&
            SemanticValuesEqual(plan.HistoryAfter, historyNow, "history");
        historyEvidence.Add("expected_feature_id", plan.FeatureId);
        historyEvidence.Add("expected_sketch_id", plan.SketchId);
        historyEvidence.Add("before", plan.HistoryBefore);
        historyEvidence.Add("after_execute", plan.HistoryAfter);
        historyEvidence.Add("after_verify", historyNow);
        historyEvidence.Add("semantic_matches_execute", String.Equals(plan.SemanticAfter, CanonicalSemanticSnapshot(captured.Snapshot), StringComparison.Ordinal));
        historyEvidence.Add("geometry_snapshot_matches_execute", String.Equals(plan.GeometryAfter, geometryNow, StringComparison.Ordinal));

        Dictionary<string, object> bodyEvidence;
        bool bodiesMatch = BodyMeasurementRecordsEqual(BodyMeasureRecords(plan.BodiesAfter, null), BodyMeasureRecords(bodiesNow, null), out bodyEvidence);
        Dictionary<string, object> nonTargetEvidence;
        bool nonTargetsMatch;
        if (plan.Kind == "create_extrude")
            nonTargetsMatch = BodyMeasurementRecordsEqual(BodyMeasureRecords(plan.BodiesBefore, plan.TargetBodyId),
                BodyMeasureRecords(bodiesNow, plan.AffectedBodyId), out nonTargetEvidence);
        else
            nonTargetsMatch = BodyMeasurementRecordsEqual(BodyMeasureRecords(plan.BodiesBefore, null),
                BodyMeasureRecords(bodiesNow, null), out nonTargetEvidence);

        if (plan.Kind == "create_sketch")
        {
            Sketch sketch = FindSketch(state.WorkPart, plan.SketchId);
            geometryMatches = SketchGeometryMatches(plan, sketch, out geometryEvidence);
            constraintsMatch = SketchConstraintsMatch(plan, sketch, out constraintEvidence);
        }
        else
        {
            Sketch sketch = FindSketch(state.WorkPart, plan.SketchId);
            bool sketchGeometryMatches = SketchGeometryMatches(plan, sketch, out geometryEvidence);
            bool sketchConstraintsMatch = SketchConstraintsMatch(plan, sketch, out constraintEvidence);
            bool extrudeHistoryMatches = FeaturePresent(state.WorkPart, plan.FeatureId);
            Dictionary<string, object> effectEvidence;
            geometryEvidence.Add("sketch_geometry_matches", sketchGeometryMatches);
            geometryEvidence.Add("extrude_feature_id", plan.FeatureId);
            geometryEvidence.Add("extrude_feature_present", extrudeHistoryMatches);
            constraintsMatch = sketchConstraintsMatch;
            historyMatches = historyMatches && extrudeHistoryMatches;
            M3BodyMeasure affected = FindBodyMeasureByTag(bodiesNow, plan.AffectedBodyId, plan.TargetBody);
            bool bodyEffectMatches = false;
            if (affected != null) bodyEffectMatches = M3BodyEffectMatches(plan, affected, bodiesNow, out effectEvidence);
            else effectEvidence = new Dictionary<string, object>(StringComparer.Ordinal) { { "status", "MISMATCH" }, { "reason", "affected body is absent" } };
            Dictionary<string, object> extrudeDefinitionEvidence;
            bool extrudeDefinitionMatches = M3ExtrudeDefinitionMatches(plan, FindFeature(state.WorkPart, plan.FeatureId), out extrudeDefinitionEvidence);
            object sourceSketchParentValue;
            bool sourceSketchParent = extrudeDefinitionEvidence.TryGetValue("source_sketch_parent", out sourceSketchParentValue) && sourceSketchParentValue is bool && (bool)sourceSketchParentValue;
            geometryMatches = sketchGeometryMatches && bodyEffectMatches && extrudeDefinitionMatches;
            historyMatches = historyMatches && sourceSketchParent;
            geometryEvidence.Add("body_effect", effectEvidence);
            geometryEvidence.Add("extrude_definition", extrudeDefinitionEvidence);
            bodyEvidence["affected_body"] = affected == null ? null : affected.PublicRecord;
        }

        Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal);
        checks.Add("geometry", VerificationCheck(geometryMatches ? "VERIFIED" : "MISMATCH", geometryEvidence));
        checks.Add("constraints", VerificationCheck(constraintsMatch ? "VERIFIED" : "MISMATCH", constraintEvidence));
        checks.Add("history", VerificationCheck(historyMatches ? "VERIFIED" : "MISMATCH", historyEvidence));
        checks.Add("bodies", VerificationCheck(bodiesMatch ? "VERIFIED" : "MISMATCH", bodyEvidence));
        checks.Add("non_target_bodies", VerificationCheck(nonTargetsMatch ? "VERIFIED" : "MISMATCH", nonTargetEvidence));
        bool supportMatches = true;
        List<object> supportRecords = new List<object>();
        if (state.M4SupportTracks.ContainsKey(plan.SketchId))
        {
            RefreshM4SupportTracksFromPart(state.WorkPart, state);
            supportMatches = ValidateM4SupportTracks(state, out supportRecords, null);
            Dictionary<string, object> supportEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
            supportEvidence.Add("associations", supportRecords);
            supportEvidence.Add("native_unique_face_and_domain_verified", supportMatches);
            checks.Add("support_association", VerificationCheck(supportMatches ? "VERIFIED" : "MISMATCH", supportEvidence));
        }

        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("kind", plan.Kind);
        result.Add("sketch_id", plan.SketchId);
        result.Add("feature_id", plan.FeatureId);
        result.Add("body_id", plan.AffectedBodyId);
        result.Add("body_inventory", BodyMeasureRecords(bodiesNow, null));
        result.Add("revision_id", binding.RevisionId);
        if (supportRecords.Count > 0) result.Add("support_associations", supportRecords);
        bool verified = geometryMatches && constraintsMatch && historyMatches && bodiesMatch && nonTargetsMatch && supportMatches;
        return new M3VerificationResult
        {
            Verified = verified,
            RevisionId = binding.RevisionId,
            Checks = checks,
            Result = result,
            ErrorCode = verified ? null : "PART_VERIFICATION_FAILED",
            ErrorMessage = verified ? null : "Live M3 geometry, dimensions, feature history, body measurements, or non-target bodies differ from the executed result."
        };
    }

    private void ExecuteM3Sketch(M2ModelBinding binding, BindingState state, M3PlanState plan, Session.UndoMarkId mark)
    {
        M3SketchRequest request = plan.SketchRequest;
        bool faceMode = !String.IsNullOrWhiteSpace(request.SupportFaceSelector);
        Sketch sketch;
        if (faceMode)
        {
            M4FaceCandidate currentFace = ResolveM4UniqueFace(state.WorkPart,
                request.SupportFaceSelector, request.SupportBodyId);
            if (!String.Equals(currentFace.FaceId, plan.SupportFace.FaceId, StringComparison.Ordinal) ||
                !String.Equals(currentFace.BodyId, plan.SupportFace.BodyId, StringComparison.Ordinal))
                throw new M2ModelException("STALE_REVISION", "selected support face changed after the immutable M4 plan was prepared");
            Dictionary<string, object> domainEvidence;
            if (!ValidateM4SketchProfile(currentFace, request.Geometries,
                request.FaceOriginOffsetMm, out domainEvidence))
                throw new M2ModelException("M4_PROFILE_OUTSIDE_FACE", "the requested sketch profile is outside the selected face domain");
            plan.SupportFace = currentFace;
            plan.SupportAssociationEvidence = domainEvidence;
            plan.SupportFrameOrigin = M4OffsetFrameOrigin(currentFace, request.FaceOriginOffsetMm);
            Plane supportPlane;
            sketch = M3NxFeatureFactory.CreateSketchOnFace(state.WorkPart, currentFace.Face,
                ToPoint3d(plan.SupportFrameOrigin), ToVector3d(currentFace.XAxis),
                ToVector3d(currentFace.Normal), out supportPlane);
            plan.SupportPlane = supportPlane;
        }
        else
        {
            M3SketchPlane plane = ParseSketchPlane(request.Plane);
            Point3d origin = new Point3d(request.OriginMm[0], request.OriginMm[1], request.OriginMm[2]);
            sketch = M3NxFeatureFactory.CreateSketch(state.WorkPart, plane, origin);
        }
        plan.Sketch = sketch;
        plan.SketchId = RequiredJournalId(sketch, "M3 sketch");
        plan.Feature = sketch.Feature;
        plan.FeatureId = RequiredJournalId(plan.Feature, "M3 sketch feature");
        string prefixRoot = "m3_" + Guid.NewGuid().ToString("N");
        foreach (M3SketchGeometryRequest geometry in request.Geometries)
        {
            string prefix = prefixRoot + "_" + ExpressionSuffix(geometry.Id);
            M3PrimitiveState primitive = new M3PrimitiveState();
            primitive.RequestId = geometry.Id;
            primitive.Type = geometry.Type;
            primitive.Dimensions = new List<M3DimensionState>();
            if (geometry.Type == "line")
            {
                M3LineResult created = M3NxFeatureFactory.CreateLine(state.WorkPart, sketch,
                    geometry.StartMm[0], geometry.StartMm[1], geometry.EndMm[0], geometry.EndMm[1], prefix);
                primitive.Curves = new Curve[] { created.Geometry };
                primitive.LocalPoints = new double[] { geometry.StartMm[0], geometry.StartMm[1], geometry.EndMm[0], geometry.EndMm[1] };
                AddM3Dimensions(primitive, created.DrivingDimensions, prefix);
            }
            else if (geometry.Type == "rectangle")
            {
                M3RectangleResult created = M3NxFeatureFactory.CreateRectangle(state.WorkPart, sketch,
                    geometry.CornerMm[0], geometry.CornerMm[1], geometry.WidthMm, geometry.HeightMm, prefix);
                primitive.Curves = new Curve[created.Geometry.Length];
                for (int i = 0; i < created.Geometry.Length; i++) primitive.Curves[i] = created.Geometry[i];
                primitive.LocalPoints = new double[] { geometry.CornerMm[0], geometry.CornerMm[1] };
                primitive.Width = geometry.WidthMm;
                primitive.Height = geometry.HeightMm;
                AddM3Dimensions(primitive, created.DrivingDimensions, prefix);
            }
            else if (geometry.Type == "circle")
            {
                M3CircleResult created = M3NxFeatureFactory.CreateCircle(state.WorkPart, sketch,
                    geometry.CenterMm[0], geometry.CenterMm[1], geometry.DiameterMm, prefix);
                primitive.Curves = new Curve[] { created.Geometry };
                primitive.LocalPoints = new double[] { geometry.CenterMm[0], geometry.CenterMm[1] };
                primitive.Diameter = geometry.DiameterMm;
                AddM3Dimensions(primitive, created.DrivingDimensions, prefix);
            }
            else throw new M2ModelException("UNSUPPORTED_GEOMETRY", "M3 sketch supports line, rectangle, and full circle");
            plan.Primitives.Add(primitive);
        }

        int updateErrors = state.Session.UpdateManager.DoUpdate(mark);
        if (updateErrors != 0) throw new M2ModelException("M3_UPDATE_FAILED", "NX update reported errors after native sketch creation");
        if (!FeaturePresent(state.WorkPart, plan.FeatureId))
            throw new M2ModelException("M3_SKETCH_NOT_CREATED", "native Sketch feature was not present after NX update");
        Dictionary<string, object> geometryEvidence;
        if (!SketchGeometryMatches(plan, sketch, out geometryEvidence))
            throw new M2ModelException("M3_SKETCH_READBACK_FAILED", "native Sketch geometry did not match the requested dimensions and position");
        List<object> constraints = ReadConstraintRecords(sketch);
        int dof;
        sketch.CalculateStatus();
        Sketch.Status createdStatus = sketch.GetStatus(out dof);
        if (createdStatus != Sketch.Status.WellConstrained || dof != 0)
            throw new M2ModelException("M3_SKETCH_UNDERCONSTRAINED", "native Sketch is not fully constrained");
        if (faceMode)
        {
            Dictionary<string, object> nativeEvidence;
            if (!ReadM4NativeSupport(state.WorkPart, sketch, plan.SupportPlane,
                plan.SupportFace.BodyId, out nativeEvidence))
                throw new M2ModelException("M4_FACE_ASSOCIATION_INVALID", "NX did not retain a native Coincident Plane reference to the uniquely selected Face");
            plan.SupportAssociationEvidence["native_readback"] = nativeEvidence;
            plan.SupportAssociationEvidence["native"] = true;
            plan.SupportAssociationEvidence["geometry_kind"] = "Face";
            plan.SupportAssociationEvidence["selector_unique"] = true;
            plan.SupportAssociationEvidence["in_face_domain"] = true;
            plan.SupportAssociationEvidence["current_face_matches"] = true;
            PersistM4SupportMetadata(sketch, request.SupportFaceSelector,
                plan.SupportFace.BodyId, true);
            M4SupportTrack supportTrack = new M4SupportTrack();
            supportTrack.SketchId = plan.SketchId;
            supportTrack.BodyId = plan.SupportFace.BodyId;
            supportTrack.Selector = request.SupportFaceSelector;
            supportTrack.FaceId = plan.SupportFace.Face.Tag.ToString();
            supportTrack.FeatureId = plan.FeatureId;
            supportTrack.Plane = plan.SupportPlane;
            supportTrack.DomainValidated = true;
            supportTrack.Native = true;
            supportTrack.GeometryKind = "Face";
            supportTrack.SupportOrigin = (double[])plan.SupportFrameOrigin.Clone();
            state.M4SupportTracks[plan.SketchId] = supportTrack;
        }
        Dictionary<string, object> bodyChangeEvidence;
        if (!BodyMeasuresEqual(plan.BodiesBefore, CaptureM3BodyMeasures(state.WorkPart), out bodyChangeEvidence))
            throw new M2ModelException("M3_SKETCH_CREATED_BODY", "native Sketch unexpectedly changed body measurements");

        sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
        int deactivateUpdateErrors = state.Session.UpdateManager.DoUpdate(mark);
        if (deactivateUpdateErrors != 0)
            throw new M2ModelException("M3_UPDATE_FAILED", "NX update reported errors after leaving the new Sketch edit environment");
        updateErrors += deactivateUpdateErrors;
        SnapshotResult after = Capture(binding, state);
        if (!FeaturePresent(state.WorkPart, plan.FeatureId))
            throw new M2ModelException("M3_SKETCH_NOT_CREATED", "Sketch feature disappeared after deactivation");

        plan.BodiesAfter = CaptureM3BodyMeasures(state.WorkPart);
        plan.SemanticAfter = CanonicalSemanticSnapshot(after.Snapshot);
        plan.GeometryAfter = CaptureFullGeometry(state.WorkPart);
        plan.HistoryAfter = HistoryProjection(after.Snapshot);
        List<object> constraintIds = ConstraintIds(constraints);
        plan.ConstraintIds = StringObjectList(constraintIds);
        plan.DrivingDimensions = ReadM3DrivingDimensions(plan.Primitives);
        Dictionary<string, object> sketchRecord = BuildSketchDetail(state.WorkPart, sketch, state.WorkPart.PartUnits.ToString());
        resultForSketch(plan, state, sketchRecord, constraints, constraintIds, geometryEvidence, updateErrors);
    }

    private void resultForSketch(M3PlanState plan, BindingState state, Dictionary<string, object> sketchRecord,
        List<object> constraints, List<object> constraintIds, Dictionary<string, object> geometryEvidence, int updateErrors)
    {
        int dof;
        Sketch.Status sketchStatus = plan.Sketch.GetStatus(out dof);
        if (sketchStatus != Sketch.Status.WellConstrained || dof != 0)
            throw new M2ModelException("M3_SKETCH_UNDERCONSTRAINED", "native Sketch did not remain fully constrained after update");
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("sketch_id", plan.SketchId);
        result.Add("feature_id", plan.FeatureId);
        result.Add("constraint_ids", constraintIds);
        result.Add("driving_dimensions", plan.DrivingDimensions);
        result.Add("constraints", constraints);
        result.Add("sketch", sketchRecord);
        result.Add("sketch_status", sketchStatus.ToString());
        result.Add("degrees_of_freedom", dof);
        result.Add("update_error_count", updateErrors);
        result.Add("geometry_readback", geometryEvidence);
        result.Add("body_inventory", BodyMeasureRecords(plan.BodiesAfter, null));
        if (plan.SupportFace != null)
        {
            Dictionary<string, object> association = plan.SupportAssociationEvidence == null
                ? new Dictionary<string, object>(StringComparer.Ordinal)
                : Clone(plan.SupportAssociationEvidence);
            association["native"] = true;
            association["geometry_kind"] = "Face";
            association["selector_unique"] = true;
            association["in_face_domain"] = true;
            association["current_face_matches"] = true;
            result.Add("support_association", association);
            result.Add("support_frame", M4FrameRecord(plan.SupportFrameOrigin,
                plan.SupportFrameX, plan.SupportFrameY, plan.SupportFrameNormal));
        }
        result.Add("revision_id", plan.BindingState.RevisionNumber.ToString(CultureInfo.InvariantCulture));
        plan.Execution = M3Execution("APPLIED", result["revision_id"] as string, result, null, null);
    }

    private void ExecuteM3Extrude(M2ModelBinding binding, BindingState state, M3PlanState plan, Session.UndoMarkId mark)
    {
        RefreshM4SupportTracksFromPart(state.WorkPart, state);
        M3ExtrudeRequest request = plan.ExtrudeRequest;
        M3BooleanMode mode = request.Mode == "create" ? M3BooleanMode.Create :
            (request.Mode == "unite" ? M3BooleanMode.Unite : M3BooleanMode.Subtract);
        M3ExtrudeExtent extent = request.Extent == "through_all" ? M3ExtrudeExtent.ThroughAll : M3ExtrudeExtent.Finite;
        M3ExtrudeDirection direction = request.Direction == "positive" ? M3ExtrudeDirection.Positive : M3ExtrudeDirection.Negative;
        Feature feature = M3NxFeatureFactory.CreateExtrude(state.WorkPart, plan.Sketch,
            plan.SectionHelpX, plan.SectionHelpY, extent, request.DepthMm, direction, mode, plan.TargetBody);
        plan.Feature = feature;
        plan.FeatureId = RequiredJournalId(feature, "M3 Extrude feature");
        int updateErrors = state.Session.UpdateManager.DoUpdate(mark);
        if (updateErrors != 0) throw new M2ModelException("M3_UPDATE_FAILED", "NX update reported errors after Extrude creation");
        M4SupportTrack m4Support;
        HashSet<string> refreshSourceSupport = new HashSet<string>(StringComparer.Ordinal);
        if (state.M4SupportTracks.TryGetValue(plan.SketchId, out m4Support))
        {
            if (String.Equals(request.Mode, "subtract", StringComparison.Ordinal) &&
                !m4Support.CutFeatureIds.Contains(plan.FeatureId)) m4Support.CutFeatureIds.Add(plan.FeatureId);
            PersistM4CutFeatureIds(plan.Sketch, m4Support.CutFeatureIds);
            refreshSourceSupport.Add(plan.SketchId);
        }
        if (state.M4SupportTracks.Count > 0)
        {
            List<object> supportRecords;
            if (!ValidateM4SupportTracks(state, out supportRecords,
                refreshSourceSupport.Count == 0 ? null : refreshSourceSupport))
            {
                plan.ExecutionFailureEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
                plan.ExecutionFailureEvidence.Add("support_associations", supportRecords);
                plan.ExecutionFailureEvidence.Add("refreshed_source_sketch_ids",
                    refreshSourceSupport.Count == 0 ? new List<object>() : new List<object>(refreshSourceSupport));
                plan.ExecutionFailureEvidence.Add("extrude_feature_id", plan.FeatureId);
                throw new M2ModelException("M4_FACE_ASSOCIATION_INVALID", "appended Extrude no longer has a unique native Face support association");
            }
            foreach (object value in supportRecords)
            {
                Dictionary<string, object> item = value as Dictionary<string, object>;
                if (item != null && String.Equals(Convert.ToString(item["sketch_id"], CultureInfo.InvariantCulture), plan.SketchId, StringComparison.Ordinal))
                { plan.SupportAssociationEvidence = item; break; }
            }
        }
        SnapshotResult after = Capture(binding, state);
        plan.BodiesAfter = CaptureM3BodyMeasures(state.WorkPart);
        M3BodyMeasure affected = ResolveAffectedBody(plan, plan.BodiesAfter, feature);
        Dictionary<string, object> extrudeDefinitionEvidence;
        if (!M3ExtrudeDefinitionMatches(plan, feature, out extrudeDefinitionEvidence))
            throw new M2ModelException("M3_EXTRUDE_READBACK_FAILED", "live NX Extrude direction, extent, boolean type, target, or sketch dependency differs from the request");
        plan.ExtrudeDefinitionEvidence = extrudeDefinitionEvidence;
        Dictionary<string, object> effectEvidence;
        if (!M3BodyEffectMatches(plan, affected, plan.BodiesAfter, out effectEvidence))
            throw new M2ModelException("M3_BOOLEAN_NO_EFFECT", "NX Extrude did not create a new solid or change the explicit target body volume in the requested direction");
        plan.AffectedBodyId = affected.Id;
        plan.SemanticAfter = CanonicalSemanticSnapshot(after.Snapshot);
        plan.GeometryAfter = CaptureFullGeometry(state.WorkPart);
        plan.HistoryAfter = HistoryProjection(after.Snapshot);
        if (!FeaturePresent(state.WorkPart, plan.FeatureId))
            throw new M2ModelException("M3_FEATURE_NOT_CREATED", "native Extrude feature was not present after NX update");

        Dictionary<string, object> sketchGeometryEvidence;
        if (!SketchGeometryMatches(plan, plan.Sketch, out sketchGeometryEvidence))
        {
            plan.ExecutionFailureEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
            plan.ExecutionFailureEvidence.Add("source_sketch_geometry", sketchGeometryEvidence);
            throw new M2ModelException("M3_SKETCH_CHANGED", "Extrude changed the source Sketch geometry");
        }
        Dictionary<string, object> targetBefore = plan.TargetBodyId == null ? null : FindBodyMeasure(plan.BodiesBefore, plan.TargetBodyId).PublicRecord;
        Dictionary<string, object> targetAfter = affected.PublicRecord;
        List<object> nonTargetBefore = BodyMeasureRecords(plan.BodiesBefore, plan.TargetBodyId);
        List<object> nonTargetAfter = BodyMeasureRecords(plan.BodiesAfter, plan.AffectedBodyId);
        plan.ConstraintIds = new List<string>();
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("sketch_id", plan.SketchId);
        result.Add("feature_id", plan.FeatureId);
        result.Add("body_id", plan.AffectedBodyId);
        result.Add("mode", request.Mode);
        result.Add("direction", request.Direction);
        result.Add("extent", request.Extent);
        result.Add("depth_mm", request.Extent == "finite" ? (object)request.DepthMm : null);
        result.Add("target_body_id", plan.TargetBodyId);
        result.Add("target_body_before", targetBefore);
        result.Add("target_body_after", targetAfter);
        result.Add("non_target_bodies_before", nonTargetBefore);
        result.Add("non_target_bodies_after", nonTargetAfter);
        result.Add("body_inventory", BodyMeasureRecords(plan.BodiesAfter, null));
        result.Add("volume_mm3", affected.VolumeMm3.HasValue ? (object)affected.VolumeMm3.Value : null);
        result.Add("bbox_mm", affected.BboxMm == null ? null : NumberList(affected.BboxMm));
        result.Add("body_effect", effectEvidence);
        result.Add("extrude_definition", extrudeDefinitionEvidence);
        if (plan.SupportAssociationEvidence != null)
        {
            Dictionary<string, object> association = Clone(plan.SupportAssociationEvidence);
            result.Add("support_association", association);
            result.Add("support_frame", association["support_frame"]);
        }
        result.Add("update_error_count", updateErrors);
        result.Add("revision_id", binding.RevisionId);
        plan.Execution = M3Execution("APPLIED", binding.RevisionId, result, null, null);
    }

    private static M3SketchPlane ParseSketchPlane(string plane)
    {
        if (plane == "XY") return M3SketchPlane.XY;
        if (plane == "XZ") return M3SketchPlane.XZ;
        if (plane == "YZ") return M3SketchPlane.YZ;
        throw new M2ModelException("INVALID_REQUEST", "plane must be XY, XZ, or YZ");
    }

    private static string ExpressionSuffix(string value)
    {
        StringBuilder result = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            result.Append(((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') || c == '_') ? c : '_');
        }
        if (result.Length == 0 || (result[0] >= '0' && result[0] <= '9')) result.Insert(0, "g_");
        return result.ToString();
    }

    private static void AddM3Dimensions(M3PrimitiveState primitive,
        SketchDimensionalConstraint[] dimensions, string prefix)
    {
        if (dimensions == null) throw new M2ModelException("M3_DIMENSION_FAILED", "NX returned no Driving dimension list");
        string marker = prefix + "_";
        foreach (SketchDimensionalConstraint dimension in dimensions)
        {
            if (dimension == null || dimension.AssociatedExpression == null ||
                dimension.DimensionState != SketchDimensionalConstraint.DimensionStateType.Driving)
                throw new M2ModelException("M3_DIMENSION_FAILED", "NX returned a sketch dimension that is not Driving");
            string name = dimension.AssociatedExpression.Name ?? String.Empty;
            if (!name.StartsWith(marker, StringComparison.Ordinal))
                throw new M2ModelException("M3_DIMENSION_FAILED", "NX Driving expression name did not match its M3 primitive");
            string role = name.Substring(marker.Length);
            M3DimensionState record = new M3DimensionState();
            record.Dimension = dimension;
            record.GeometryId = primitive.RequestId;
            record.Role = role;
            record.ExpectedValue = dimension.AssociatedExpression.Value;
            EnsureFinite(record.ExpectedValue, "M3 dimension value");
            primitive.Dimensions.Add(record);
        }
    }

    private static List<object> ReadConstraintRecords(Sketch sketch)
    {
        return BuildSketchConstraints(sketch);
    }

    private static List<object> ConstraintIds(List<object> constraints)
    {
        List<object> ids = new List<object>();
        foreach (object value in constraints)
        {
            Dictionary<string, object> record = value as Dictionary<string, object>;
            if (record == null || !(record["journal_id"] is string))
                throw new M2ModelException("MODEL_READ_FAILED", "sketch constraint record has no journal_id");
            ids.Add(record["journal_id"]);
        }
        return ids;
    }

    private static List<string> StringObjectList(List<object> values)
    {
        List<string> result = new List<string>();
        foreach (object value in values)
        {
            string text = value as string;
            if (text == null) throw new M2ModelException("MODEL_READ_FAILED", "constraint id is not a string");
            result.Add(text);
        }
        return result;
    }

    private static List<object> ReadM3DrivingDimensions(List<M3PrimitiveState> primitives)
    {
        List<object> records = new List<object>();
        foreach (M3PrimitiveState primitive in primitives)
        {
            foreach (M3DimensionState state in primitive.Dimensions)
            {
                SketchDimensionalConstraint dimension = state.Dimension;
                Expression expression = dimension.AssociatedExpression;
                Sketch.DimensionGeometry[] operands = dimension.GetDimensionGeometry();
                if (operands == null) throw new M2ModelException("MODEL_READ_FAILED", "M3 dimension geometry returned null");
                List<string> geometryRefs = new List<string>();
                foreach (Sketch.DimensionGeometry operand in operands) geometryRefs.Add(DimensionReference(operand));
                geometryRefs.Sort(StringComparer.Ordinal);
                Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
                record.Add("constraint_id", RequiredJournalId(dimension, "M3 Driving constraint"));
                record.Add("geometry_id", state.GeometryId);
                record.Add("role", state.Role);
                record.Add("constraint_type", dimension.ConstraintType.ToString());
                record.Add("value", expression.Value);
                record.Add("unit", expression.Units == null ? null : expression.Units.Symbol);
                record.Add("expression_name", expression.Name);
                record.Add("dimension_state", dimension.DimensionState.ToString());
                record.Add("is_driving", dimension.DimensionState == SketchDimensionalConstraint.DimensionStateType.Driving);
                record.Add("geometry_refs", ObjectStrings(geometryRefs));
                records.Add(record);
            }
        }
        return records;
    }

    private static Dictionary<string, object> FindSketchRecord(Dictionary<string, object> snapshot, string sketchId)
    {
        List<object> sketches = SnapshotList(snapshot, "sketches");
        Dictionary<string, object> found = null;
        int matches = 0;
        foreach (object value in sketches)
        {
            Dictionary<string, object> sketch = value as Dictionary<string, object>;
            if (sketch != null && String.Equals(Convert.ToString(sketch["journal_id"], CultureInfo.InvariantCulture), sketchId, StringComparison.Ordinal))
            {
                found = sketch;
                matches++;
            }
        }
        if (matches != 1 || found == null)
            throw new M2ModelException(matches == 0 ? "SKETCH_NOT_FOUND" : "AMBIGUOUS_SKETCH", "Sketch record is not unique in the current Work Part snapshot");
        return found;
    }

    private static bool FeaturePresent(Part part, string featureId)
    {
        if (part == null || String.IsNullOrWhiteSpace(featureId)) return false;
        Feature[] features = part.Features.GetFeatures();
        if (features == null) throw new M2ModelException("MODEL_READ_FAILED", "feature history returned null");
        int matches = 0;
        foreach (Feature feature in features)
            if (feature != null && String.Equals(RequiredJournalId(feature, "feature"), featureId, StringComparison.Ordinal)) matches++;
        return matches == 1;
    }

    private static Feature FindFeature(Part part, string featureId)
    {
        if (part == null || String.IsNullOrWhiteSpace(featureId)) throw new M2ModelException("FEATURE_NOT_FOUND", "feature_id is required");
        Feature[] features = part.Features.GetFeatures();
        if (features == null) throw new M2ModelException("MODEL_READ_FAILED", "feature history returned null");
        Feature found = null;
        int matches = 0;
        foreach (Feature feature in features)
        {
            if (feature != null && String.Equals(RequiredJournalId(feature, "feature"), featureId, StringComparison.Ordinal))
            {
                found = feature;
                matches++;
            }
        }
        if (matches != 1 || found == null)
            throw new M2ModelException(matches == 0 ? "FEATURE_NOT_FOUND" : "AMBIGUOUS_FEATURE", "feature_id must identify exactly one feature");
        return found;
    }

    private static bool M3ExtrudeDefinitionMatches(M3PlanState plan, Feature feature,
        out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        if (plan == null || plan.ExtrudeRequest == null || feature == null)
        {
            evidence.Add("status", "MISMATCH");
            evidence.Add("reason", "retained Extrude request or feature is unavailable");
            return false;
        }
        ExtrudeBuilder builder = feature.OwningPart.Features.CreateExtrudeBuilder(feature);
        try
        {
            M3ExtrudeRequest request = plan.ExtrudeRequest;
            Direction direction = builder.Direction;
            if (direction == null) throw new M2ModelException("M3_EXTRUDE_READBACK_FAILED", "NX returned no Extrude direction");
            Vector3d actualDirection = direction.Vector;
            Matrix3x3 axes = plan.Sketch.Orientation.Element;
            double sign = request.Direction == "positive" ? 1.0 : -1.0;
            Vector3d expectedDirection = new Vector3d(axes.Zx * sign, axes.Zy * sign, axes.Zz * sign);
            bool directionMatches = NearlyEqual(actualDirection.X, expectedDirection.X) &&
                NearlyEqual(actualDirection.Y, expectedDirection.Y) && NearlyEqual(actualDirection.Z, expectedDirection.Z);

            NXOpen.GeometricUtilities.Extend start = builder.Limits.StartExtend;
            NXOpen.GeometricUtilities.Extend end = builder.Limits.EndExtend;
            double startValue = start == null || start.Value == null ? Double.NaN : start.Value.Value;
            bool startMatches = start != null && start.TrimType == NXOpen.GeometricUtilities.Extend.ExtendType.Value && NearlyEqual(startValue, 0.0);
            double endValue = end == null || end.Value == null ? Double.NaN : end.Value.Value;
            bool extentMatches = request.Extent == "finite"
                ? end != null && end.TrimType == NXOpen.GeometricUtilities.Extend.ExtendType.Value && NearlyEqual(endValue, request.DepthMm)
                : end != null && end.TrimType == NXOpen.GeometricUtilities.Extend.ExtendType.ThroughAll;

            string expectedBoolean = request.Mode == "create" ? "Create" : (request.Mode == "unite" ? "Unite" : "Subtract");
            string actualBoolean = builder.BooleanOperation == null ? null : builder.BooleanOperation.Type.ToString();
            bool booleanMatches = String.Equals(expectedBoolean, actualBoolean, StringComparison.Ordinal);
            Body[] targetBodies = builder.BooleanOperation.GetTargetBodies();
            bool targetsMatch;
            List<object> targetIds = new List<object>();
            if (targetBodies != null)
            {
                foreach (Body target in targetBodies)
                {
                    if (target == null) throw new M2ModelException("M3_EXTRUDE_READBACK_FAILED", "NX returned a null boolean target");
                    targetIds.Add(RequiredJournalId(target, "Extrude target body"));
                }
            }
            if (request.Mode == "create") targetsMatch = targetIds.Count == 0;
            else targetsMatch = targetBodies != null && targetBodies.Length == 1 &&
                targetBodies[0].Tag.Equals(plan.TargetBody.Tag) &&
                String.Equals(RequiredJournalId(targetBodies[0], "Extrude target body"), plan.TargetBodyId, StringComparison.Ordinal);

            Feature[] parents = feature.GetParents();
            if (parents == null) throw new M2ModelException("MODEL_READ_FAILED", "Extrude parent feature list returned null");
            List<object> parentIds = new List<object>();
            bool sketchParent = false;
            foreach (Feature parent in parents)
            {
                if (parent == null) throw new M2ModelException("MODEL_READ_FAILED", "Extrude parent feature list contains null");
                string parentId = RequiredJournalId(parent, "Extrude parent feature");
                parentIds.Add(parentId);
                if (plan.Sketch != null && plan.Sketch.Feature != null && parent.Tag.Equals(plan.Sketch.Feature.Tag)) sketchParent = true;
            }
            bool matched = directionMatches && startMatches && extentMatches && booleanMatches && targetsMatch && sketchParent;
            evidence.Add("status", matched ? "VERIFIED" : "MISMATCH");
            evidence.Add("feature_id", RequiredJournalId(feature, "Extrude feature"));
            evidence.Add("expected_direction", new List<object> { expectedDirection.X, expectedDirection.Y, expectedDirection.Z });
            evidence.Add("actual_direction", new List<object> { actualDirection.X, actualDirection.Y, actualDirection.Z });
            evidence.Add("direction_matches", directionMatches);
            evidence.Add("start_trim_type", start == null ? null : start.TrimType.ToString());
            evidence.Add("start_value_mm", startValue);
            evidence.Add("start_matches", startMatches);
            evidence.Add("extent", request.Extent);
            evidence.Add("end_trim_type", end == null ? null : end.TrimType.ToString());
            evidence.Add("expected_depth_mm", request.Extent == "finite" ? (object)request.DepthMm : null);
            evidence.Add("actual_depth_mm", request.Extent == "finite" ? (object)endValue : null);
            evidence.Add("extent_matches", extentMatches);
            evidence.Add("expected_boolean_type", expectedBoolean);
            evidence.Add("actual_boolean_type", actualBoolean);
            evidence.Add("boolean_matches", booleanMatches);
            evidence.Add("target_body_ids", targetIds);
            evidence.Add("expected_target_body_id", plan.TargetBodyId);
            evidence.Add("targets_match", targetsMatch);
            evidence.Add("source_sketch_feature_id", plan.Sketch == null || plan.Sketch.Feature == null ? null : RequiredJournalId(plan.Sketch.Feature, "source sketch feature"));
            evidence.Add("parent_feature_ids", parentIds);
            evidence.Add("source_sketch_parent", sketchParent);
            return matched;
        }
        finally { builder.Destroy(); }
    }

    private static List<object> FeatureIds(Dictionary<string, object> snapshot)
    {
        List<object> features = SnapshotList(snapshot, "features");
        List<string> ids = new List<string>();
        foreach (object value in features)
        {
            Dictionary<string, object> feature = value as Dictionary<string, object>;
            if (feature == null || !(feature["journal_id"] is string))
                throw new M2ModelException("MODEL_READ_FAILED", "feature history contains a record without journal_id");
            ids.Add((string)feature["journal_id"]);
        }
        ids.Sort(StringComparer.Ordinal);
        return ObjectStrings(ids);
    }

    private bool SketchGeometryMatches(M3PlanState plan, Sketch sketch, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        if (sketch == null)
        {
            evidence.Add("status", "MISMATCH");
            evidence.Add("reason", "Sketch is unavailable");
            return false;
        }
        if (plan.Kind == "create_extrude")
        {
            Dictionary<string, object> current = BuildSketchDetail(sketch.OwningPart as Part, sketch, sketch.OwningPart.PartUnits.ToString());
            Dictionary<string, object> expectedProjection = M3SourceSketchProjection(plan.SketchRecordBefore);
            Dictionary<string, object> actualProjection = M3SourceSketchProjection(current);
            bool same = plan.SketchRecordBefore != null &&
                ProjectionComplete(expectedProjection) && ProjectionComplete(actualProjection) &&
                SemanticValuesEqual(expectedProjection, actualProjection, "source_sketch");
            evidence.Add("status", same ? "VERIFIED" : "MISMATCH");
            evidence.Add("expected_source_sketch_projection", expectedProjection);
            evidence.Add("actual_source_sketch_projection", actualProjection);
            return same;
        }

        bool matches = true;
        List<object> primitiveEvidence = new List<object>();
        NXObject[] geometry = sketch.GetAllGeometry();
        if (geometry == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch.GetAllGeometry returned null during M3 verification");
        Dictionary<string, NXObject> objects = new Dictionary<string, NXObject>(StringComparer.Ordinal);
        foreach (NXObject item in geometry)
            if (item != null) objects[RequiredJournalId(item, "M3 sketch geometry")] = item;
        foreach (M3PrimitiveState primitive in plan.Primitives)
        {
            Dictionary<string, object> itemEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
            itemEvidence.Add("geometry_id", primitive.RequestId);
            itemEvidence.Add("type", primitive.Type);
            bool itemMatches = true;
            if (primitive.Type == "line")
            {
                Line expected = primitive.Curves[0] as Line;
                Line actual;
                string id = RequiredJournalId(expected, "M3 line");
                NXObject found;
                if (!objects.TryGetValue(id, out found) || (actual = found as Line) == null)
                    itemMatches = false;
                else
                {
                    double[] p = primitive.LocalPoints;
                    Point3d expectedA = SketchPoint(sketch, p[0], p[1]);
                    Point3d expectedB = SketchPoint(sketch, p[2], p[3]);
                    itemMatches = EndpointsMatch(actual.StartPoint, actual.EndPoint, expectedA, expectedB);
                    itemEvidence.Add("geometry_journal_id", id);
                    itemEvidence.Add("start", Point(actual.StartPoint));
                    itemEvidence.Add("end", Point(actual.EndPoint));
                    itemEvidence.Add("expected_start", Point(expectedA));
                    itemEvidence.Add("expected_end", Point(expectedB));
                }
            }
            else if (primitive.Type == "rectangle")
            {
                double[] p = primitive.LocalPoints;
                double x = p[0], y = p[1], width = primitive.Width, height = primitive.Height;
                Point3d[] corners = new Point3d[] {
                    SketchPoint(sketch, x, y), SketchPoint(sketch, x + width, y),
                    SketchPoint(sketch, x + width, y + height), SketchPoint(sketch, x, y + height)
                };
                HashSet<string> actualIds = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < primitive.Curves.Length; i++)
                {
                    Line expectedLine = primitive.Curves[i] as Line;
                    string id = RequiredJournalId(expectedLine, "M3 rectangle line");
                    NXObject found;
                    Line actualLine = objects.TryGetValue(id, out found) ? found as Line : null;
                    if (actualLine == null) { itemMatches = false; break; }
                    actualIds.Add(id);
                    int edge = -1;
                    for (int j = 0; j < 4; j++)
                        if (EndpointsMatch(actualLine.StartPoint, actualLine.EndPoint, corners[j], corners[(j + 1) % 4])) { edge = j; break; }
                    if (edge < 0) itemMatches = false;
                }
                itemEvidence.Add("expected_corners", new List<object> { Point(corners[0]), Point(corners[1]), Point(corners[2]), Point(corners[3]) });
                itemEvidence.Add("line_ids", ObjectStrings(new List<string>(actualIds)));
            }
            else
            {
                Arc expected = primitive.Curves[0] as Arc;
                string id = RequiredJournalId(expected, "M3 circle");
                NXObject found;
                Arc actual = objects.TryGetValue(id, out found) ? found as Arc : null;
                double[] p = primitive.LocalPoints;
                Point3d expectedCenter = SketchPoint(sketch, p[0], p[1]);
                if (actual == null) itemMatches = false;
                else
                {
                    double sweep = Math.Abs(actual.EndAngle - actual.StartAngle);
                    itemMatches = PointNearlyEqual(actual.CenterPoint, expectedCenter) &&
                        NearlyEqual(actual.Radius * 2.0, primitive.Diameter) && NearlyEqual(sweep, 2.0 * Math.PI);
                    itemEvidence.Add("geometry_journal_id", id);
                    itemEvidence.Add("center", Point(actual.CenterPoint));
                    itemEvidence.Add("expected_center", Point(expectedCenter));
                    itemEvidence.Add("radius_mm", actual.Radius);
                    itemEvidence.Add("diameter_mm", actual.Radius * 2.0);
                    itemEvidence.Add("expected_diameter_mm", primitive.Diameter);
                    itemEvidence.Add("sweep_radians", sweep);
                }
            }
            itemEvidence.Add("status", itemMatches ? "VERIFIED" : "MISMATCH");
            matches = matches && itemMatches;
            primitiveEvidence.Add(itemEvidence);
        }
        evidence.Add("status", matches ? "VERIFIED" : "MISMATCH");
        evidence.Add("sketch_id", RequiredJournalId(sketch, "M3 sketch"));
        evidence.Add("sketch_geometry", primitiveEvidence);
        return matches;
    }

    private static Dictionary<string, object> M3SourceSketchProjection(Dictionary<string, object> sketchRecord)
    {
        string[] fields = new string[] {
            "journal_id", "feature_journal_id", "status", "degrees_of_freedom", "units",
            "origin", "normal", "x_axis", "y_axis", "geometry", "constraints"
        };
        Dictionary<string, object> projection = new Dictionary<string, object>(StringComparer.Ordinal);
        List<object> missing = new List<object>();
        foreach (string field in fields)
        {
            object value;
            if (sketchRecord != null && sketchRecord.TryGetValue(field, out value)) projection.Add(field, value);
            else missing.Add(field);
        }
        projection.Add("missing_fields", missing);
        return projection;
    }

    private static bool ProjectionComplete(Dictionary<string, object> projection)
    {
        object value;
        List<object> missing = projection != null && projection.TryGetValue("missing_fields", out value)
            ? value as List<object> : null;
        return missing != null && missing.Count == 0;
    }

    private bool SketchConstraintsMatch(M3PlanState plan, Sketch sketch, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        if (sketch == null)
        {
            evidence.Add("status", "MISMATCH");
            evidence.Add("reason", "Sketch is unavailable");
            return false;
        }
        if (plan.Kind == "create_extrude")
        {
            Dictionary<string, object> current = BuildSketchDetail(sketch.OwningPart as Part, sketch, sketch.OwningPart.PartUnits.ToString());
            List<object> expected = plan.SketchRecordBefore == null ? null : plan.SketchRecordBefore["constraints"] as List<object>;
            List<object> actual = current["constraints"] as List<object>;
            bool same = expected != null && actual != null && SemanticValuesEqual(expected, actual, "constraints");
            evidence.Add("status", same ? "VERIFIED" : "MISMATCH");
            evidence.Add("expected", expected ?? new List<object>());
            evidence.Add("actual", actual ?? new List<object>());
            return same;
        }
        int dof;
        Sketch.Status status = sketch.GetStatus(out dof);
        List<object> actualDimensions = ReadM3DrivingDimensions(plan.Primitives);
        bool dimensionsEqual = SemanticValuesEqual(plan.DrivingDimensions, actualDimensions, "value");
        bool fullyConstrained = status == Sketch.Status.WellConstrained && dof == 0;
        evidence.Add("status", dimensionsEqual && fullyConstrained ? "VERIFIED" : "MISMATCH");
        evidence.Add("sketch_status", status.ToString());
        evidence.Add("degrees_of_freedom", dof);
        evidence.Add("expected_driving_dimensions", plan.DrivingDimensions ?? new List<object>());
        evidence.Add("actual_driving_dimensions", actualDimensions);
        evidence.Add("fully_constrained", fullyConstrained);
        return dimensionsEqual && fullyConstrained;
    }

    private static Point3d SketchPoint(Sketch sketch, double x, double y)
    {
        Matrix3x3 axes = sketch.Orientation.Element;
        Point3d origin = sketch.Origin;
        return new Point3d(origin.X + axes.Xx * x + axes.Yx * y,
            origin.Y + axes.Xy * x + axes.Yy * y,
            origin.Z + axes.Xz * x + axes.Yz * y);
    }

    private static bool EndpointsMatch(Point3d a1, Point3d a2, Point3d b1, Point3d b2)
    {
        return (PointNearlyEqual(a1, b1) && PointNearlyEqual(a2, b2)) ||
            (PointNearlyEqual(a1, b2) && PointNearlyEqual(a2, b1));
    }

    private static bool PointNearlyEqual(Point3d left, Point3d right)
    {
        return NearlyEqual(left.X, right.X) && NearlyEqual(left.Y, right.Y) && NearlyEqual(left.Z, right.Z);
    }

    private static bool ValidateSingleExtrudeProfile(Sketch sketch, out double helpX, out double helpY)
    {
        helpX = 0.0;
        helpY = 0.0;
        if (sketch == null) throw new M2ModelException("SKETCH_NOT_FOUND", "sketch_id does not identify an NX Sketch");
        NXObject[] geometry = sketch.GetAllGeometry();
        if (geometry == null || geometry.Length == 0)
            throw new M2ModelException("UNSUPPORTED_PROFILE", "Extrude requires one closed rectangle or full-circle Sketch profile");
        if (geometry.Length == 1)
        {
            Arc circle = geometry[0] as Arc;
            if (circle != null && NearlyEqual(Math.Abs(circle.EndAngle - circle.StartAngle), 2.0 * Math.PI))
            {
                double normal;
                ProjectToSketchAxes(sketch.Origin, circle.CenterPoint, sketch.Orientation.Element, out helpX, out helpY, out normal);
                if (!NearlyEqual(normal, 0.0)) throw new M2ModelException("UNSUPPORTED_PROFILE", "circle profile center is outside its sketch plane");
                return true;
            }
        }
        if (geometry.Length != 4)
            throw new M2ModelException("UNSUPPORTED_PROFILE", "Extrude supports one four-line rectangle or one full circle per Sketch");
        Line[] lines = new Line[4];
        List<Point3d> localPoints = new List<Point3d>();
        int horizontalCount = 0;
        int verticalCount = 0;
        for (int i = 0; i < geometry.Length; i++)
        {
            lines[i] = geometry[i] as Line;
            if (lines[i] == null) throw new M2ModelException("UNSUPPORTED_PROFILE", "Extrude profile contains geometry other than lines");
            Point3d start = lines[i].StartPoint;
            Point3d end = lines[i].EndPoint;
            double x1, y1, n1, x2, y2, n2;
            ProjectToSketchAxes(sketch.Origin, start, sketch.Orientation.Element, out x1, out y1, out n1);
            ProjectToSketchAxes(sketch.Origin, end, sketch.Orientation.Element, out x2, out y2, out n2);
            if (!NearlyEqual(n1, 0.0) || !NearlyEqual(n2, 0.0))
                throw new M2ModelException("UNSUPPORTED_PROFILE", "rectangle line is outside its sketch plane");
            if (NearlyEqual(y1, y2) && !NearlyEqual(x1, x2)) horizontalCount++;
            else if (NearlyEqual(x1, x2) && !NearlyEqual(y1, y2)) verticalCount++;
            else throw new M2ModelException("UNSUPPORTED_PROFILE", "Extrude line profile must be one axis-aligned rectangle");
            localPoints.Add(new Point3d(x1, y1, 0));
            localPoints.Add(new Point3d(x2, y2, 0));
        }
        if (horizontalCount != 2 || verticalCount != 2)
            throw new M2ModelException("UNSUPPORTED_PROFILE", "Extrude line profile must contain two horizontal and two vertical sides");
        ValidateRectangleCorners(localPoints.ToArray(), out helpX, out helpY);
        return true;
    }

    private static void ValidateRectangleCorners(Point3d[] localPoints, out double helpX, out double helpY)
    {
        helpX = 0.0;
        helpY = 0.0;
        if (localPoints == null || localPoints.Length != 8)
            throw new M2ModelException("UNSUPPORTED_PROFILE", "Extrude rectangle requires four complete sides");
        double minX = Double.PositiveInfinity, minY = Double.PositiveInfinity;
        double maxX = Double.NegativeInfinity, maxY = Double.NegativeInfinity;
        foreach (Point3d point in localPoints)
        {
            minX = Math.Min(minX, point.X); minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X); maxY = Math.Max(maxY, point.Y);
        }
        if (maxX - minX <= 0.0 || maxY - minY <= 0.0)
            throw new M2ModelException("UNSUPPORTED_PROFILE", "Extrude rectangle must have positive width and height");
        bool[] edges = new bool[4];
        for (int i = 0; i < localPoints.Length; i += 2)
        {
            double x1 = localPoints[i].X, y1 = localPoints[i].Y;
            double x2 = localPoints[i + 1].X, y2 = localPoints[i + 1].Y;
            bool spansX = (NearlyEqual(x1, minX) && NearlyEqual(x2, maxX)) ||
                (NearlyEqual(x1, maxX) && NearlyEqual(x2, minX));
            bool spansY = (NearlyEqual(y1, minY) && NearlyEqual(y2, maxY)) ||
                (NearlyEqual(y1, maxY) && NearlyEqual(y2, minY));
            int edge;
            if (NearlyEqual(y1, minY) && NearlyEqual(y2, minY) && spansX) edge = 0;
            else if (NearlyEqual(x1, maxX) && NearlyEqual(x2, maxX) && spansY) edge = 1;
            else if (NearlyEqual(y1, maxY) && NearlyEqual(y2, maxY) && spansX) edge = 2;
            else if (NearlyEqual(x1, minX) && NearlyEqual(x2, minX) && spansY) edge = 3;
            else throw new M2ModelException("UNSUPPORTED_PROFILE", "Extrude rectangle side does not connect its two corners");
            if (edges[edge]) throw new M2ModelException("UNSUPPORTED_PROFILE", "Extrude rectangle has a duplicate side");
            edges[edge] = true;
        }
        for (int i = 0; i < edges.Length; i++) if (!edges[i]) throw new M2ModelException("UNSUPPORTED_PROFILE", "Extrude rectangle is open");
        helpX = (minX + maxX) / 2.0;
        helpY = (minY + maxY) / 2.0;
    }

    private static M3BodyMeasure ResolveAffectedBody(M3PlanState plan, List<M3BodyMeasure> after, Feature feature)
    {
        if (after == null || feature == null) throw new M2ModelException("M3_BODY_READ_FAILED", "NX returned no M3 feature or body inventory");
        Body[] featureBodies = feature.GetBodies();
        if (featureBodies == null || featureBodies.Length != 1 || featureBodies[0] == null)
            throw new M2ModelException("M3_BODY_READ_FAILED", "M3 Extrude feature must return exactly one affected solid body");
        Body candidate = featureBodies[0];
        foreach (M3BodyMeasure body in after)
            if (body.Body.Tag.Equals(candidate.Tag)) return body;
        throw new M2ModelException("M3_BODY_READ_FAILED", "feature affected body is absent from the Work Part body inventory");
    }

    private static bool M3BodyEffectMatches(M3PlanState plan, M3BodyMeasure affected,
        List<M3BodyMeasure> after, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        bool matches = affected != null && after != null;
        M3BodyMeasure beforeTarget = null;
        if (plan.TargetBodyId != null) beforeTarget = FindBodyMeasure(plan.BodiesBefore, plan.TargetBodyId);
        if (matches && !affected.IsSolid) matches = false;
        if (matches && !affected.VolumeMm3.HasValue) matches = false;
        bool nonTargetsEqual = false;
        Dictionary<string, object> nonTargetEvidence;
        string omitBefore = plan.TargetBodyId;
        string omitAfter = plan.Kind == "create_extrude" ? affected == null ? null : affected.Id : affected == null ? null : affected.Id;
        if (matches)
            nonTargetsEqual = BodyMeasurementRecordsEqual(BodyMeasureRecords(plan.BodiesBefore, omitBefore),
                BodyMeasureRecords(after, omitAfter), out nonTargetEvidence);
        else
            nonTargetEvidence = new Dictionary<string, object>(StringComparer.Ordinal) { { "status", "MISMATCH" } };

        double beforeVolume = beforeTarget == null || !beforeTarget.VolumeMm3.HasValue ? 0.0 : beforeTarget.VolumeMm3.Value;
        double afterVolume = affected == null || !affected.VolumeMm3.HasValue ? 0.0 : affected.VolumeMm3.Value;
        bool countMatches = plan.ExtrudeRequest != null &&
            (plan.ExtrudeRequest.Mode == "create" ? after.Count == plan.BodiesBefore.Count + 1 : after.Count == plan.BodiesBefore.Count);
        bool volumeChangedCorrectly = false;
        if (plan.ExtrudeRequest != null && affected != null && affected.VolumeMm3.HasValue)
        {
            if (plan.ExtrudeRequest.Mode == "create") volumeChangedCorrectly = afterVolume > 0.0;
            else if (plan.ExtrudeRequest.Mode == "unite") volumeChangedCorrectly = afterVolume > beforeVolume + ValueTolerance(afterVolume, beforeVolume);
            else volumeChangedCorrectly = afterVolume > 0.0 && afterVolume < beforeVolume - ValueTolerance(afterVolume, beforeVolume);
        }
        matches = matches && countMatches && volumeChangedCorrectly && nonTargetsEqual && affected.BboxMm != null;
        evidence.Add("status", matches ? "VERIFIED" : "MISMATCH");
        evidence.Add("mode", plan.ExtrudeRequest == null ? null : plan.ExtrudeRequest.Mode);
        evidence.Add("target_body_id_before", plan.TargetBodyId);
        evidence.Add("affected_body_id_after", affected == null ? null : affected.Id);
        evidence.Add("target_volume_before_mm3", beforeTarget == null ? null : (object)beforeTarget.VolumeMm3);
        evidence.Add("affected_volume_after_mm3", affected == null ? null : (object)affected.VolumeMm3);
        evidence.Add("volume_delta_mm3", affected == null || !affected.VolumeMm3.HasValue ? null : (object)(afterVolume - beforeVolume));
        evidence.Add("body_count_before", plan.BodiesBefore == null ? 0 : plan.BodiesBefore.Count);
        evidence.Add("body_count_after", after == null ? 0 : after.Count);
        evidence.Add("body_count_matches_mode", countMatches);
        evidence.Add("volume_changed_in_requested_direction", volumeChangedCorrectly);
        evidence.Add("non_target_bodies", nonTargetEvidence);
        evidence.Add("affected_body", affected == null ? null : affected.PublicRecord);
        return matches;
    }

    private M3ExecutionResult RestoreM3State(M2ModelBinding binding, BindingState state,
        M3PlanState plan, Session.UndoMarkId mark, Exception operationException)
    {
        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("operation_exception", operationException.GetType().Name);
        evidence.Add("operation_message", operationException.Message);
        if (plan.ExecutionFailureEvidence != null)
            evidence.Add("operation_diagnostics", plan.ExecutionFailureEvidence);
        bool undoCalled = false;
        int updateErrors = -1;
        try
        {
            Sketch active = state.Session.ActiveSketch;
            if (active != null && !Object.ReferenceEquals(active, plan.ActiveSketchBefore))
            {
                active.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
                int deactivateErrors = state.Session.UpdateManager.DoUpdate(mark);
                evidence.Add("active_sketch_deactivate_update_errors", deactivateErrors);
                if (deactivateErrors != 0) throw new InvalidOperationException("active Sketch deactivation update returned errors");
            }
        }
        catch (Exception ex)
        {
            evidence.Add("active_sketch_deactivate_error", ex.GetType().Name);
            return M3Execution("UNKNOWN", binding.RevisionId, evidence, "ROLLBACK_UNVERIFIED", "active Sketch could not be ended before undo");
        }
        try
        {
            state.Session.UndoToMarkWithStatus(mark, "M3 native sketch/extrude rollback");
            undoCalled = true;
            updateErrors = state.Session.UpdateManager.DoUpdate(mark);
            evidence.Add("undo_called", undoCalled);
            evidence.Add("undo_update_error_count", updateErrors);
            if (updateErrors != 0)
                return M3Execution("UNKNOWN", binding.RevisionId, evidence, "ROLLBACK_UNVERIFIED", "NX undo update returned errors");
        }
        catch (Exception ex)
        {
            evidence.Add("undo_called", undoCalled);
            evidence.Add("undo_exception", ex.GetType().Name);
            return M3Execution("UNKNOWN", binding.RevisionId, evidence, "ROLLBACK_UNVERIFIED", "NX UndoToMark could not establish the prior M3 model state");
        }
        try
        {
            SnapshotResult restored = Capture(binding, state);
            string semantic = CanonicalSemanticSnapshot(restored.Snapshot);
            string geometry = CaptureFullGeometry(state.WorkPart);
            List<object> history = HistoryProjection(restored.Snapshot);
            List<M3BodyMeasure> bodies = CaptureM3BodyMeasures(state.WorkPart);
            Dictionary<string, object> bodyEvidence;
            bool bodiesEqual = BodyMeasuresEqual(plan.BodiesBefore, bodies, out bodyEvidence);
            bool semanticEqual = String.Equals(plan.SemanticBefore, semantic, StringComparison.Ordinal);
            bool geometryEqual = String.Equals(plan.GeometryBefore, geometry, StringComparison.Ordinal);
            bool historyEqual = SemanticValuesEqual(plan.HistoryBefore, history, "history");
            evidence.Add("semantic_restored", semanticEqual);
            evidence.Add("geometry_restored", geometryEqual);
            evidence.Add("history_restored", historyEqual);
            evidence.Add("bodies_restored", bodiesEqual);
            evidence.Add("body_measurement_evidence", bodyEvidence);
            evidence.Add("feature_ids_after_undo", FeatureIds(restored.Snapshot));
            evidence.Add("body_inventory_after_undo", BodyMeasureRecords(bodies, null));
            bool restoredAll = semanticEqual && geometryEqual && historyEqual && bodiesEqual;
            if (!restoredAll)
                return M3Execution("UNKNOWN", binding.RevisionId, evidence, "ROLLBACK_UNVERIFIED", "NX Undo completed but the model did not match the captured M3 pre-operation state");
            return M3Execution("ROLLED_BACK", binding.RevisionId, evidence, "M3_OPERATION_ROLLED_BACK", operationException.Message);
        }
        catch (Exception ex)
        {
            evidence.Add("rollback_readback_error", ex.GetType().Name);
            return M3Execution("UNKNOWN", binding.RevisionId, evidence, "ROLLBACK_UNVERIFIED", "NX undo completed but the restored model state could not be measured");
        }
    }

    private static M3ExecutionResult M3Execution(string state, string revisionId,
        Dictionary<string, object> result, string errorCode, string errorMessage)
    {
        return new M3ExecutionResult
        {
            State = state,
            RevisionId = revisionId,
            Result = result ?? new Dictionary<string, object>(StringComparer.Ordinal),
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
    }

    private static M3PlanState CreateM3PlanState(M2ModelBinding binding, BindingState state,
        string kind, SnapshotResult capture)
    {
        return new M3PlanState
        {
            BindingState = state,
            Kind = kind,
            RevisionId = binding.RevisionId,
            SemanticBefore = CanonicalSemanticSnapshot(capture.Snapshot),
            GeometryBefore = CaptureFullGeometry(state.WorkPart),
            HistoryBefore = HistoryProjection(capture.Snapshot),
            BodiesBefore = CaptureM3BodyMeasures(state.WorkPart),
            Primitives = new List<M3PrimitiveState>(),
            ActiveSketchBefore = state.Session.ActiveSketch
        };
    }

    private static M3SketchRequest CloneM3SketchRequest(M3SketchRequest request)
    {
        M3SketchRequest clone = new M3SketchRequest();
        clone.RevisionId = request.RevisionId;
        clone.Plane = request.Plane;
        clone.OriginMm = request.OriginMm == null ? null : (double[])request.OriginMm.Clone();
        clone.SupportFaceSelector = request.SupportFaceSelector;
        clone.SupportBodyId = request.SupportBodyId;
        clone.FaceOriginOffsetMm = request.FaceOriginOffsetMm == null ? null : (double[])request.FaceOriginOffsetMm.Clone();
        clone.Geometries = new List<M3SketchGeometryRequest>();
        foreach (M3SketchGeometryRequest geometry in request.Geometries)
        {
            M3SketchGeometryRequest item = new M3SketchGeometryRequest();
            item.Id = geometry.Id; item.Type = geometry.Type;
            item.StartMm = geometry.StartMm == null ? null : (double[])geometry.StartMm.Clone();
            item.EndMm = geometry.EndMm == null ? null : (double[])geometry.EndMm.Clone();
            item.CornerMm = geometry.CornerMm == null ? null : (double[])geometry.CornerMm.Clone();
            item.CenterMm = geometry.CenterMm == null ? null : (double[])geometry.CenterMm.Clone();
            item.WidthMm = geometry.WidthMm; item.HeightMm = geometry.HeightMm; item.DiameterMm = geometry.DiameterMm;
            clone.Geometries.Add(item);
        }
        return clone;
    }

    private static M3ExtrudeRequest CloneM3ExtrudeRequest(M3ExtrudeRequest request)
    {
        return new M3ExtrudeRequest
        {
            RevisionId = request.RevisionId,
            SketchId = request.SketchId,
            Mode = request.Mode,
            Direction = request.Direction,
            Extent = request.Extent,
            DepthMm = request.DepthMm,
            TargetBodyId = request.TargetBodyId
        };
    }

    private static List<object> SketchGeometryPublicRecords(List<M3SketchGeometryRequest> geometries)
    {
        List<object> records = new List<object>();
        foreach (M3SketchGeometryRequest geometry in geometries)
        {
            Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
            record.Add("id", geometry.Id);
            record.Add("type", geometry.Type);
            if (geometry.Type == "line")
            {
                record.Add("start_mm", NumberList(geometry.StartMm));
                record.Add("end_mm", NumberList(geometry.EndMm));
            }
            else if (geometry.Type == "rectangle")
            {
                record.Add("corner_mm", NumberList(geometry.CornerMm));
                record.Add("width_mm", geometry.WidthMm);
                record.Add("height_mm", geometry.HeightMm);
            }
            else
            {
                record.Add("center_mm", NumberList(geometry.CenterMm));
                record.Add("diameter_mm", geometry.DiameterMm);
            }
            records.Add(record);
        }
        return records;
    }

    private static List<object> ExpectedDrivingDimensionRecords(List<M3SketchGeometryRequest> geometries)
    {
        List<object> records = new List<object>();
        foreach (M3SketchGeometryRequest geometry in geometries)
        {
            if (geometry.Type == "line")
            {
                double dx = geometry.EndMm[0] - geometry.StartMm[0];
                double dy = geometry.EndMm[1] - geometry.StartMm[1];
                if (geometry.StartMm[0] != 0.0 || geometry.StartMm[1] != 0.0)
                {
                    AddExpectedDimension(records, geometry.Id, "x", "HorizontalDim", Math.Abs(geometry.StartMm[0]), "sketch origin to line start x");
                    AddExpectedDimension(records, geometry.Id, "y", "VerticalDim", Math.Abs(geometry.StartMm[1]), "sketch origin to line start y");
                }
                if (Math.Abs(dy) <= 1e-9)
                    AddExpectedDimension(records, geometry.Id, "length", "HorizontalDim", Math.Abs(dx), "line endpoint projected x span");
                else if (Math.Abs(dx) <= 1e-9)
                    AddExpectedDimension(records, geometry.Id, "length", "VerticalDim", Math.Abs(dy), "line endpoint projected y span");
                else
                {
                    AddExpectedDimension(records, geometry.Id, "length_x", "HorizontalDim", Math.Abs(dx), "line endpoint projected x span");
                    AddExpectedDimension(records, geometry.Id, "length_y", "VerticalDim", Math.Abs(dy), "line endpoint projected y span");
                }
            }
            else if (geometry.Type == "rectangle")
            {
                if (geometry.CornerMm[0] != 0.0 || geometry.CornerMm[1] != 0.0)
                {
                    AddExpectedDimension(records, geometry.Id, "x", "HorizontalDim", Math.Abs(geometry.CornerMm[0]), "sketch origin to rectangle corner x");
                    AddExpectedDimension(records, geometry.Id, "y", "VerticalDim", Math.Abs(geometry.CornerMm[1]), "sketch origin to rectangle corner y");
                }
                AddExpectedDimension(records, geometry.Id, "width", "HorizontalDim", geometry.WidthMm, "rectangle width");
                AddExpectedDimension(records, geometry.Id, "height", "VerticalDim", geometry.HeightMm, "rectangle height");
            }
            else
            {
                if (geometry.CenterMm[0] != 0.0 || geometry.CenterMm[1] != 0.0)
                {
                    AddExpectedDimension(records, geometry.Id, "x", "HorizontalDim", Math.Abs(geometry.CenterMm[0]), "sketch origin to circle center x");
                    AddExpectedDimension(records, geometry.Id, "y", "VerticalDim", Math.Abs(geometry.CenterMm[1]), "sketch origin to circle center y");
                }
                AddExpectedDimension(records, geometry.Id, "diameter", "DiameterDim", geometry.DiameterMm, "circle diameter");
            }
        }
        return records;
    }

    private static void AddExpectedDimension(List<object> records, string geometryId, string role,
        string type, double value, string reference)
    {
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
        record.Add("geometry_id", geometryId);
        record.Add("role", role);
        record.Add("constraint_type", type);
        record.Add("value", value);
        record.Add("unit", "mm");
        record.Add("geometry_refs_by_role", reference);
        records.Add(record);
    }

    private static void ValidateExtrudeRequest(M3ExtrudeRequest request)
    {
        if (request.Mode != "create" && request.Mode != "unite" && request.Mode != "subtract")
            throw new M2ModelException("INVALID_REQUEST", "mode must be create, unite, or subtract");
        if (request.Direction != "positive" && request.Direction != "negative")
            throw new M2ModelException("INVALID_REQUEST", "direction must be positive or negative");
        if (request.Extent != "finite" && request.Extent != "through_all")
            throw new M2ModelException("INVALID_REQUEST", "extent must be finite or through_all");
        if (request.Mode == "create")
        {
            if (request.Extent != "finite" || !String.IsNullOrEmpty(request.TargetBodyId) ||
                Double.IsNaN(request.DepthMm) || Double.IsInfinity(request.DepthMm) || request.DepthMm <= 0.0)
                throw new M2ModelException("INVALID_REQUEST", "create requires finite positive depth_mm and no target_body_id");
        }
        else
        {
            if (String.IsNullOrWhiteSpace(request.TargetBodyId))
                throw new M2ModelException("INVALID_REQUEST", "unite and subtract require one explicit target_body_id");
            if (request.Extent == "finite" && (Double.IsNaN(request.DepthMm) || Double.IsInfinity(request.DepthMm) || request.DepthMm <= 0.0))
                throw new M2ModelException("INVALID_REQUEST", "finite Extrude requires positive finite depth_mm");
            if (request.Extent == "through_all" && request.DepthMm != 0.0)
                throw new M2ModelException("INVALID_REQUEST", "through_all does not accept depth_mm");
        }
    }

    private static Sketch FindSketch(Part part, string sketchId)
    {
        if (part == null || String.IsNullOrWhiteSpace(sketchId)) throw new M2ModelException("INVALID_REQUEST", "sketch_id is required");
        Sketch[] sketches = part.Sketches.ToArray();
        if (sketches == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketches.ToArray returned null");
        Sketch found = null;
        int matches = 0;
        foreach (Sketch sketch in sketches)
        {
            if (sketch != null && String.Equals(RequiredJournalId(sketch, "sketch"), sketchId, StringComparison.Ordinal))
            {
                found = sketch;
                matches++;
            }
        }
        if (matches != 1 || found == null)
            throw new M2ModelException(matches == 0 ? "SKETCH_NOT_FOUND" : "AMBIGUOUS_SKETCH", "sketch_id must identify exactly one Sketch in the Work Part");
        return found;
    }

    private static Body FindBody(Part part, string bodyId)
    {
        if (part == null || String.IsNullOrWhiteSpace(bodyId)) throw new M2ModelException("INVALID_REQUEST", "target_body_id is required");
        Body[] bodies = part.Bodies.ToArray();
        if (bodies == null) throw new M2ModelException("MODEL_READ_FAILED", "Bodies.ToArray returned null");
        Body found = null;
        int matches = 0;
        foreach (Body body in bodies)
        {
            if (body != null && String.Equals(RequiredJournalId(body, "body"), bodyId, StringComparison.Ordinal))
            {
                found = body;
                matches++;
            }
        }
        if (matches != 1 || found == null)
            throw new M2ModelException(matches == 0 ? "BODY_NOT_FOUND" : "AMBIGUOUS_BODY", "target_body_id must identify exactly one body in the Work Part");
        return found;
    }

    private static List<M3BodyMeasure> CaptureM3BodyMeasures(Part part)
    {
        Body[] bodies = part.Bodies.ToArray();
        if (bodies == null) throw new M2ModelException("MODEL_READ_FAILED", "Bodies.ToArray returned null during M3 measurement");
        List<M3BodyMeasure> result = new List<M3BodyMeasure>();
        foreach (Body body in bodies)
        {
            if (body == null) throw new M2ModelException("UNSUPPORTED_MODEL", "body collection contains null during M3 measurement");
            result.Add(MeasureBody(part, body));
        }
        result.Sort(delegate(M3BodyMeasure left, M3BodyMeasure right) { return String.CompareOrdinal(left.Id, right.Id); });
        return result;
    }

    private static List<object> BodyMeasureRecords(List<M3BodyMeasure> bodies, string omitBodyId)
    {
        List<object> records = new List<object>();
        if (bodies == null) return records;
        foreach (M3BodyMeasure body in bodies)
            if (omitBodyId == null || !String.Equals(body.Id, omitBodyId, StringComparison.Ordinal)) records.Add(Clone(body.PublicRecord));
        return records;
    }

    private static M3BodyMeasure FindBodyMeasure(List<M3BodyMeasure> bodies, string bodyId)
    {
        if (bodies == null) throw new M2ModelException("MODEL_READ_FAILED", "body measurements are unavailable");
        M3BodyMeasure found = null;
        int matches = 0;
        foreach (M3BodyMeasure body in bodies)
            if (String.Equals(body.Id, bodyId, StringComparison.Ordinal)) { found = body; matches++; }
        if (matches != 1 || found == null)
            throw new M2ModelException(matches == 0 ? "BODY_NOT_FOUND" : "AMBIGUOUS_BODY", "body measurement id is unavailable or ambiguous");
        return found;
    }

    private static M3BodyMeasure FindBodyMeasureByTag(List<M3BodyMeasure> bodies, string bodyId, Body bodyReference)
    {
        if (bodies == null) return null;
        foreach (M3BodyMeasure body in bodies)
        {
            if (bodyReference != null && body.Body.Tag.Equals(bodyReference.Tag)) return body;
            if (bodyReference == null && String.Equals(body.Id, bodyId, StringComparison.Ordinal)) return body;
        }
        return null;
    }

    private static bool BodyMeasuresEqual(List<M3BodyMeasure> before, List<M3BodyMeasure> after,
        out Dictionary<string, object> evidence)
    {
        return BodyMeasurementRecordsEqual(BodyMeasureRecords(before, null), BodyMeasureRecords(after, null), out evidence);
    }

    private static M3VerificationResult M3VerificationFailure(string revisionId, string reason)
    {
        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("reason", reason);
        Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal);
        string[] names = new string[] { "geometry", "constraints", "history", "bodies", "non_target_bodies" };
        foreach (string name in names) checks.Add(name, VerificationCheck("MISMATCH", Clone(evidence)));
        return new M3VerificationResult
        {
            Verified = false,
            RevisionId = revisionId,
            Checks = checks,
            Result = new Dictionary<string, object>(StringComparer.Ordinal),
            ErrorCode = "PART_VERIFICATION_FAILED",
            ErrorMessage = reason
        };
    }

    public M2SaveCopyResult SaveCopy(M2ModelBinding binding, string targetPath, bool reopen)
    {
        BindingState state = RequireState(binding);
        string sourcePath = state.WorkPart == null ? null : state.WorkPart.FullPath;
        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("source_path", sourcePath);
        evidence.Add("saved_path", targetPath);
        evidence.Add("save_attempted", false);
        evidence.Add("reopen_requested", reopen);

        if (String.Equals(binding.ArtifactScope, "current_work_part", StringComparison.Ordinal))
            return SaveCopyRejected("CURRENT_WORK_PART_SAVE_UNSUPPORTED", "nx_save_copy is unavailable for current_work_part because SaveAs/reopen cannot preserve the in-memory Work Part identity.", evidence);
        if (IsPartReadOnly(state.WorkPart))
            return SaveCopyRejected("READ_ONLY_PART", "saving is not allowed for a read-only Work Part.", evidence);
        if (!reopen)
            return SaveCopyRejected("REOPEN_REQUIRED", "reopen=true is required so the saved native copy can be checked after loading it again.", evidence);
        string bindingReason;
        if (!IsBindingCurrent(binding, out bindingReason))
            return SaveCopyRejected("STALE_BINDING", bindingReason ?? "NX model binding is no longer current.", evidence);
        string fullTarget;
        try { fullTarget = Path.GetFullPath(targetPath); }
        catch (Exception ex)
        {
            return SaveCopyRejected("INVALID_TARGET_PATH", "target_path cannot be canonicalized (" + ex.GetType().Name + ").", evidence);
        }
        string canonicalTarget = CanonicalPath(fullTarget);
        if (!String.Equals(Path.GetExtension(fullTarget), ".prt", StringComparison.OrdinalIgnoreCase))
            return SaveCopyRejected("INVALID_TARGET_PATH", "target_path must end in .prt.", evidence);
        if (String.Equals(state.CanonicalPath, canonicalTarget, StringComparison.OrdinalIgnoreCase))
            return SaveCopyRejected("TARGET_IS_SOURCE_PART", "target_path cannot overwrite the takeover source part.", evidence);
        if (File.Exists(fullTarget) || Directory.Exists(fullTarget))
            return SaveCopyRejected("TARGET_ALREADY_EXISTS", "target_path already exists; save copy never overwrites existing files.", evidence);
        if (!Directory.Exists(Path.GetDirectoryName(fullTarget)))
            return SaveCopyRejected("TARGET_DIRECTORY_NOT_FOUND", "target_path directory does not exist.", evidence);

        SnapshotResult before;
        List<object> geometryBefore;
        try
        {
            before = Capture(binding, state);
            geometryBefore = CaptureGeometryRecords(state.WorkPart, false);
        }
        catch (M2ModelException ex)
        {
            return SaveCopyRejected(ex.Code, "The native Work Part could not be read before SaveAs: " + ex.Message, evidence);
        }
        catch (Exception ex)
        {
            return SaveCopyRejected("MODEL_READ_FAILED", "The native Work Part could not be read before SaveAs (" + ex.GetType().Name + ").", evidence);
        }

        evidence["source_revision_id"] = binding.RevisionId;
        evidence["source_part_tag"] = state.PartTag;
        evidence["save_attempted"] = true;
        PartSaveStatus saveStatus = null;
        try
        {
            saveStatus = state.WorkPart.SaveAs(fullTarget);
            if (saveStatus == null)
                return SaveCopyUnknown("SAVE_RESULT_UNKNOWN", "NX SaveAs returned no status object.", evidence);
            if (saveStatus.NumberUnsavedParts != 0 || saveStatus.NumberUnsavedObjects != 0)
            {
                evidence["unsaved_part_count"] = saveStatus.NumberUnsavedParts;
                evidence["unsaved_object_count"] = saveStatus.NumberUnsavedObjects;
                return SaveCopyUnknown("SAVE_RESULT_UNKNOWN", "NX SaveAs reported unsaved parts or objects.", evidence);
            }
        }
        catch (Exception ex)
        {
            AddExceptionEvidence(evidence, "save", ex);
            return SaveCopyUnknown("SAVE_RESULT_UNKNOWN", "NX SaveAs did not return a verifiable status.", evidence);
        }
        finally
        {
            if (saveStatus != null) saveStatus.Dispose();
        }

        if (!File.Exists(fullTarget))
            return SaveCopyUnknown("SAVE_RESULT_UNKNOWN", "NX SaveAs returned success but the requested .prt file is absent.", evidence);
        evidence["target_file_exists_after_save"] = true;

        Part reopenedPart;
        try
        {
            reopenedPart = ReopenSavedCopy(state.Session, state.WorkPart, fullTarget);
            if (reopenedPart == null || !String.Equals(CanonicalPath(reopenedPart.FullPath), canonicalTarget, StringComparison.OrdinalIgnoreCase))
                return SaveCopyUnknown("SAVE_RESULT_UNKNOWN", "NX did not activate the requested saved .prt after reopen.", evidence);
            state.WorkPart = reopenedPart;
            state.PartPath = reopenedPart.FullPath;
            state.PartTag = reopenedPart.Tag.ToString();
            state.CanonicalPath = CanonicalPath(reopenedPart.FullPath);
            binding.PartPath = state.PartPath;
            binding.PartTag = state.PartTag;
            SnapshotResult after = Capture(binding, state);
            List<object> geometryAfter = CaptureGeometryRecords(reopenedPart, false);
            Dictionary<string, object> checks = BuildSaveCopyChecks(before.Snapshot, after.Snapshot,
                geometryBefore, geometryAfter);
            evidence["source_path"] = sourcePath;
            evidence["saved_path"] = state.PartPath;
            evidence["reopened"] = true;
            evidence["source_part_tag"] = before.Snapshot["identity"] is Dictionary<string, object>
                ? ((Dictionary<string, object>)before.Snapshot["identity"])["part_tag"] : null;
            evidence["reopened_part_tag"] = state.PartTag;
            evidence["round_trip_checks"] = checks;
            evidence["read_effects_after_reopen"] = after.ReadEffects;
            binding.Identity = BuildIdentity(binding, reopenedPart, after.ReadEffects);
            return new M2SaveCopyResult
            {
                State = "SAVED",
                PartPath = state.PartPath,
                PartTag = state.PartTag,
                RevisionId = binding.RevisionId,
                Identity = Clone(binding.Identity),
                Reopened = true,
                Checks = checks,
                Evidence = evidence
            };
        }
        catch (Exception ex)
        {
            AddExceptionEvidence(evidence, "reopen", ex);
            return SaveCopyUnknown("SAVE_RESULT_UNKNOWN", "NX saved the requested copy but its reopened model state could not be verified.", evidence);
        }
    }

    public void Release(M2ModelBinding binding)
    {
        if (binding != null) binding.AdapterState = null;
    }

    private static Dictionary<string, object> VerificationCheck(string status, Dictionary<string, object> evidence)
    {
        Dictionary<string, object> check = new Dictionary<string, object>(StringComparer.Ordinal);
        check.Add("status", status);
        check.Add("evidence", evidence);
        return check;
    }

    private static void AddExceptionEvidence(Dictionary<string, object> evidence, string prefix, Exception exception)
    {
        evidence[prefix + "_exception_type"] = exception.GetType().Name;
        evidence[prefix + "_exception_message"] = exception.Message;
        PropertyInfo codeProperty = exception.GetType().GetProperty("ErrorCode");
        if (codeProperty != null)
        {
            try { evidence[prefix + "_nx_error_code"] = Convert.ToString(codeProperty.GetValue(exception, null), CultureInfo.InvariantCulture); }
            catch (Exception) { }
        }
    }

    private static M2PartVerificationResult VerificationFailure(string revisionId, string reason)
    {
        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("reason", reason);
        Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal);
        checks.Add("expression", VerificationCheck("MISMATCH", Clone(evidence)));
        checks.Add("geometry", VerificationCheck("MISMATCH", Clone(evidence)));
        checks.Add("history", VerificationCheck("MISMATCH", Clone(evidence)));
        checks.Add("associations", VerificationCheck("MISMATCH", Clone(evidence)));
        return new M2PartVerificationResult
        {
            Verified = false,
            RevisionId = revisionId,
            Checks = checks,
            ErrorCode = "PART_VERIFICATION_FAILED",
            ErrorMessage = reason
        };
    }

    private static bool SelectedHistoryMatches(Part part, Sketch sketch, string expectedFeatureId,
        out Dictionary<string, object> evidence)
    {
        Feature feature = sketch == null ? null : sketch.Feature;
        Feature[] allFeatures = part.Features.GetFeatures();
        if (allFeatures == null) throw new M2ModelException("MODEL_READ_FAILED", "feature history returned null during verification");
        string featureId = feature == null ? null : RequiredJournalId(feature, "sketch feature");
        int matches = 0;
        foreach (Feature candidate in allFeatures)
        {
            if (candidate != null && String.Equals(RequiredJournalId(candidate, "feature history"), featureId, StringComparison.Ordinal))
                matches++;
        }
        List<string> parentIds = feature == null ? new List<string>() : GetJournalIds(feature.GetParents(), "history parent");
        List<string> childIds = feature == null ? new List<string>() : GetJournalIds(feature.GetChildren(), "history child");
        Expression[] expressions = feature == null ? new Expression[0] : feature.GetExpressions();
        if (expressions == null) throw new M2ModelException("MODEL_READ_FAILED", "sketch feature expressions returned null during verification");
        List<string> expressionNames = new List<string>();
        foreach (Expression expression in expressions)
        {
            if (expression == null) throw new M2ModelException("UNSUPPORTED_MODEL", "sketch feature history contains a null expression");
            expressionNames.Add(expression.Name);
        }
        expressionNames.Sort(StringComparer.Ordinal);

        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("sketch_id", sketch == null ? null : RequiredJournalId(sketch, "sketch"));
        evidence.Add("feature_id", featureId);
        evidence.Add("expected_feature_id", expectedFeatureId);
        evidence.Add("feature_type", feature == null ? null : feature.FeatureType);
        evidence.Add("parents", ObjectStrings(parentIds));
        evidence.Add("children", ObjectStrings(childIds));
        evidence.Add("expressions", ObjectStrings(expressionNames));
        evidence.Add("feature_count", allFeatures.Length);
        evidence.Add("feature_in_part_count", matches);
        return feature != null && matches == 1 && String.Equals(featureId, expectedFeatureId, StringComparison.Ordinal);
    }

    private static M2SaveCopyResult SaveCopyRejected(string code, string message,
        Dictionary<string, object> evidence)
    {
        M2SaveCopyResult result = new M2SaveCopyResult();
        result.State = "UNCHANGED";
        result.Reopened = false;
        result.Checks = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Evidence = evidence;
        result.ErrorCode = code;
        result.ErrorMessage = message;
        return result;
    }

    private static M2SaveCopyResult SaveCopyUnknown(string code, string message,
        Dictionary<string, object> evidence)
    {
        M2SaveCopyResult result = new M2SaveCopyResult();
        result.State = "UNKNOWN";
        result.Reopened = false;
        result.Checks = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Evidence = evidence;
        result.ErrorCode = code;
        result.ErrorMessage = message;
        return result;
    }

    private static Part ReopenSavedCopy(Session session, Part current, string targetPath)
    {
        Part active;
        if (String.Equals(CanonicalPath(current.FullPath), CanonicalPath(targetPath), StringComparison.OrdinalIgnoreCase))
        {
            PartReopenReport report = null;
            try
            {
                BasePart reloaded = current.Reopen(BasePart.CloseWholeTree.False,
                    BasePart.CloseModified.CloseModified, null, out report);
                if (reloaded == null || (report != null && report.CouldNotResetWorkPart))
                    throw new M2ModelException("SAVE_COPY_REOPEN_FAILED", "NX did not reset the saved copy as the Work Part.");
                active = reloaded as Part;
            }
            finally
            {
                if (report != null) report.Dispose();
            }
        }
        else
        {
            PartLoadStatus loadStatus = null;
            BasePart opened;
            try
            {
                opened = session.Parts.OpenBaseDisplay(targetPath, out loadStatus);
                if (opened == null || (loadStatus != null && loadStatus.NumberUnloadedParts != 0))
                    throw new M2ModelException("SAVE_COPY_REOPEN_FAILED", "NX did not fully open the saved copy.");
            }
            finally
            {
                if (loadStatus != null) loadStatus.Dispose();
            }

            PartLoadStatus displayStatus = null;
            try
            {
                object displayResult = session.Parts.SetActiveDisplay(opened, DisplayPartOption.ReplaceExisting,
                    PartDisplayPartWorkPartOption.SameAsDisplay, out displayStatus);
                if (displayResult == null || !String.Equals(displayResult.ToString(), "OK", StringComparison.Ordinal) ||
                    (displayStatus != null && displayStatus.NumberUnloadedParts != 0))
                    throw new M2ModelException("SAVE_COPY_REOPEN_FAILED", "NX could not make the saved copy the active Work Part.");
            }
            finally
            {
                if (displayStatus != null) displayStatus.Dispose();
            }
            active = session.Parts.Work;
        }
        if (active == null) active = session.Parts.Work;
        if (active == null || !String.Equals(CanonicalPath(active.FullPath), CanonicalPath(targetPath), StringComparison.OrdinalIgnoreCase))
            throw new M2ModelException("SAVE_COPY_REOPEN_FAILED", "the active NX Work Part path does not match the saved copy.");
        if (!Object.ReferenceEquals(session.Parts.Work, active)) session.Parts.SetWork(active);
        return active;
    }

    private static Dictionary<string, object> BuildSaveCopyChecks(Dictionary<string, object> before,
        Dictionary<string, object> after, List<object> geometryBefore, List<object> geometryAfter)
    {
        List<object> beforeExpressions = SnapshotList(before, "expressions");
        List<object> afterExpressions = SnapshotList(after, "expressions");
        List<object> beforeFeatures = HistoryProjection(before);
        List<object> afterFeatures = HistoryProjection(after);
        List<object> beforeAssociations = AssociationProjection(before);
        List<object> afterAssociations = AssociationProjection(after);
        List<object> beforeBodies = BodyMeasurementProjection(before);
        List<object> afterBodies = BodyMeasurementProjection(after);
        Dictionary<string, object> bodyEvidence;
        bool bodiesEqual = BodyMeasurementRecordsEqual(beforeBodies, afterBodies, out bodyEvidence);
        bool expressionEqual = ExpressionRecordsEqual(beforeExpressions, afterExpressions);
        bool reopenedBodiesConsistent = M7SnapshotBodiesConsistent(after);
        bool geometryEqual = SemanticValuesEqual(geometryBefore, geometryAfter, "geometry") && bodiesEqual && reopenedBodiesConsistent;
        bool historyEqual = SemanticValuesEqual(beforeFeatures, afterFeatures, "history");
        bool associationsEqual = SemanticValuesEqual(beforeAssociations, afterAssociations, "associations");

        Dictionary<string, object> expressionEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        expressionEvidence.Add("before_count", beforeExpressions.Count);
        expressionEvidence.Add("after_count", afterExpressions.Count);
        expressionEvidence.Add("before", ExpressionProjection(beforeExpressions));
        expressionEvidence.Add("after", ExpressionProjection(afterExpressions));
        Dictionary<string, object> geometryEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        geometryEvidence.Add("before_sketch_geometry", geometryBefore);
        geometryEvidence.Add("after_sketch_geometry", geometryAfter);
        geometryEvidence.Add("before_sketch_count", geometryBefore.Count);
        geometryEvidence.Add("after_sketch_count", geometryAfter.Count);
        geometryEvidence.Add("body_measurements", bodyEvidence);
        geometryEvidence.Add("reopened_bodies_consistent", reopenedBodiesConsistent);
        Dictionary<string, object> historyEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        historyEvidence.Add("before", beforeFeatures);
        historyEvidence.Add("after", afterFeatures);
        historyEvidence.Add("before_feature_count", SnapshotList(before, "features").Count);
        historyEvidence.Add("after_feature_count", SnapshotList(after, "features").Count);
        Dictionary<string, object> associationEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        associationEvidence.Add("before", beforeAssociations);
        associationEvidence.Add("after", afterAssociations);

        Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal);
        checks.Add("expression", VerificationCheck(expressionEqual ? "VERIFIED" : "MISMATCH", expressionEvidence));
        checks.Add("geometry", VerificationCheck(geometryEqual ? "VERIFIED" : "MISMATCH", geometryEvidence));
        checks.Add("history", VerificationCheck(historyEqual ? "VERIFIED" : "MISMATCH", historyEvidence));
        checks.Add("associations", VerificationCheck(associationsEqual ? "VERIFIED" : "MISMATCH", associationEvidence));
        return checks;
    }

    private static List<object> BodyMeasurementProjection(Dictionary<string, object> snapshot)
    {
        List<object> bodies = SnapshotList(snapshot, "bodies");
        List<object> result = new List<object>();
        foreach (object value in bodies)
        {
            Dictionary<string, object> source = value as Dictionary<string, object>;
            if (source == null) throw new M2ModelException("MODEL_READ_FAILED", "body measurement evidence contains an invalid record");
            Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
            record.Add("journal_id", source["journal_id"]);
            record.Add("is_solid", source["is_solid"]);
            record.Add("volume_mm3", source["volume_mm3"]);
            record.Add("bbox_mm", source["bbox_mm"]);
            record.Add("measurement_status", source["measurement_status"]);
            result.Add(record);
        }
        result.Sort(delegate(object leftValue, object rightValue)
        {
            Dictionary<string, object> left = leftValue as Dictionary<string, object>;
            Dictionary<string, object> right = rightValue as Dictionary<string, object>;
            return String.CompareOrdinal(Convert.ToString(left["journal_id"], CultureInfo.InvariantCulture),
                Convert.ToString(right["journal_id"], CultureInfo.InvariantCulture));
        });
        return result;
    }

    private static bool BodyMeasurementRecordsEqual(List<object> before, List<object> after,
        out Dictionary<string, object> evidence)
    {
        bool same = before != null && after != null && before.Count == after.Count;
        if (same)
        {
            for (int i = 0; i < before.Count; i++)
            {
                Dictionary<string, object> left = before[i] as Dictionary<string, object>;
                Dictionary<string, object> right = after[i] as Dictionary<string, object>;
                if (left == null || right == null ||
                    !String.Equals(Convert.ToString(left["journal_id"], CultureInfo.InvariantCulture), Convert.ToString(right["journal_id"], CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
                    !Object.Equals(left["is_solid"], right["is_solid"]) ||
                    !String.Equals(Convert.ToString(left["measurement_status"], CultureInfo.InvariantCulture), Convert.ToString(right["measurement_status"], CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
                    !NullableNumberEqual(left["volume_mm3"], right["volume_mm3"]) ||
                    !NumericListEqual(left["bbox_mm"] as List<object>, right["bbox_mm"] as List<object>))
                {
                    same = false;
                    break;
                }
            }
        }
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("status", same ? "VERIFIED" : "MISMATCH");
        evidence.Add("before", before ?? new List<object>());
        evidence.Add("after", after ?? new List<object>());
        evidence.Add("body_count_before", before == null ? 0 : before.Count);
        evidence.Add("body_count_after", after == null ? 0 : after.Count);
        evidence.Add("tolerance", "max(1e-7, max(|a|,|b|)*1e-9) in mm and mm^3");
        return same;
    }

    private static bool NullableNumberEqual(object left, object right)
    {
        if (left == null || right == null) return left == null && right == null;
        double a, b;
        return TryFiniteNumber(left, out a) && TryFiniteNumber(right, out b) && NearlyEqual(a, b);
    }

    private static bool NumericListEqual(List<object> left, List<object> right)
    {
        if (left == null || right == null) return left == null && right == null;
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
            if (!NullableNumberEqual(left[i], right[i])) return false;
        return true;
    }

    private static bool TryFiniteNumber(object value, out double number)
    {
        number = 0.0;
        if (value is double) { number = (double)value; return !Double.IsNaN(number) && !Double.IsInfinity(number); }
        if (value is float) { number = (float)value; return !Double.IsNaN(number) && !Double.IsInfinity(number); }
        return value is string && Double.TryParse((string)value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
            !Double.IsNaN(number) && !Double.IsInfinity(number);
    }

    private static List<object> ExpressionProjection(List<object> expressions)
    {
        List<object> projected = new List<object>();
        foreach (object item in expressions)
        {
            Dictionary<string, object> record = item as Dictionary<string, object>;
            if (record == null) throw new M2ModelException("MODEL_READ_FAILED", "expression evidence contains an invalid record");
            Dictionary<string, object> current = new Dictionary<string, object>(StringComparer.Ordinal);
            string[] keys = new string[] { "name", "right_hand_side", "status", "value", "unit" };
            foreach (string key in keys)
            {
                object value;
                if (!record.TryGetValue(key, out value)) throw new M2ModelException("MODEL_READ_FAILED", "expression evidence is missing " + key);
                current.Add(key, value);
            }
            projected.Add(current);
        }
        return projected;
    }

    private static List<object> HistoryProjection(Dictionary<string, object> snapshot)
    {
        List<object> features = SnapshotList(snapshot, "features");
        List<object> dependencies = SnapshotList(snapshot, "dependencies");
        List<object> sketches = SnapshotList(snapshot, "sketches");
        List<object> sketchFeatures = new List<object>();
        foreach (object value in sketches)
        {
            Dictionary<string, object> sketch = value as Dictionary<string, object>;
            if (sketch == null) throw new M2ModelException("MODEL_READ_FAILED", "sketch history contains an invalid record");
            Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
            record.Add("journal_id", sketch["journal_id"]);
            record.Add("feature_journal_id", sketch["feature_journal_id"]);
            sketchFeatures.Add(record);
        }
        List<object> result = new List<object>();
        result.Add(features);
        result.Add(dependencies);
        result.Add(sketchFeatures);
        return result;
    }

    private static List<object> AssociationProjection(Dictionary<string, object> snapshot)
    {
        List<object> sketches = SnapshotList(snapshot, "sketches");
        List<object> projected = new List<object>();
        foreach (object value in sketches)
        {
            Dictionary<string, object> sketch = value as Dictionary<string, object>;
            if (sketch == null) throw new M2ModelException("MODEL_READ_FAILED", "sketch association evidence contains an invalid record");
            List<object> constraints = sketch["constraints"] as List<object>;
            if (constraints == null) throw new M2ModelException("MODEL_READ_FAILED", "sketch constraints are unavailable");
            List<object> relations = new List<object>();
            foreach (object constraintValue in constraints)
            {
                Dictionary<string, object> constraint = constraintValue as Dictionary<string, object>;
                if (constraint == null) throw new M2ModelException("MODEL_READ_FAILED", "constraint association evidence contains an invalid record");
                Dictionary<string, object> relation = new Dictionary<string, object>(StringComparer.Ordinal);
                string[] keys = new string[] { "journal_id", "constraint_type", "class", "dimension_state", "is_driving", "expression_name", "geometry_refs" };
                foreach (string key in keys)
                {
                    object field;
                    if (constraint.TryGetValue(key, out field)) relation.Add(key, field);
                }
                relations.Add(relation);
            }
            Dictionary<string, object> sketchRelations = new Dictionary<string, object>(StringComparer.Ordinal);
            sketchRelations.Add("sketch_id", sketch["journal_id"]);
            sketchRelations.Add("constraints", relations);
            projected.Add(sketchRelations);
        }
        return projected;
    }

    private static List<object> SnapshotList(Dictionary<string, object> snapshot, string key)
    {
        object value;
        List<object> list = snapshot != null && snapshot.TryGetValue(key, out value) ? value as List<object> : null;
        if (list == null) throw new M2ModelException("MODEL_READ_FAILED", "semantic snapshot is missing " + key);
        return list;
    }

    private static bool ExpressionRecordsEqual(List<object> left, List<object> right)
    {
        List<object> a = ExpressionProjection(left);
        List<object> b = ExpressionProjection(right);
        if (a.Count != b.Count) return false;
        Dictionary<string, Dictionary<string, object>> byName = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        foreach (object item in b)
        {
            Dictionary<string, object> expression = item as Dictionary<string, object>;
            string name = expression["name"] as string;
            if (String.IsNullOrEmpty(name) || byName.ContainsKey(name)) return false;
            byName.Add(name, expression);
        }
        foreach (object item in a)
        {
            Dictionary<string, object> before = item as Dictionary<string, object>;
            string name = before["name"] as string;
            Dictionary<string, object> after;
            if (String.IsNullOrEmpty(name) || !byName.TryGetValue(name, out after)) return false;
            if (!String.Equals(before["status"] as string, after["status"] as string, StringComparison.Ordinal) ||
                !String.Equals(before["unit"] as string, after["unit"] as string, StringComparison.Ordinal) ||
                !NumericExpressionTextEqual(before["right_hand_side"] as string, after["right_hand_side"] as string) ||
                !NumericTextEqual(before["value"] as string, after["value"] as string)) return false;
        }
        return true;
    }

    private static bool NumericExpressionTextEqual(string left, string right)
    {
        double a;
        double b;
        if (Double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out a) &&
            Double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out b)) return NearlyEqual(a, b);
        return String.Equals(left, right, StringComparison.Ordinal);
    }

    private static bool NumericTextEqual(string left, string right)
    {
        double a;
        double b;
        return Double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out a) &&
            Double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out b) && NearlyEqual(a, b);
    }

    private static bool SemanticValuesEqual(object left, object right, string key)
    {
        if (left == null || right == null) return left == null && right == null;
        Dictionary<string, object> leftMap = left as Dictionary<string, object>;
        Dictionary<string, object> rightMap = right as Dictionary<string, object>;
        if (leftMap != null || rightMap != null)
        {
            if (leftMap == null || rightMap == null || leftMap.Count != rightMap.Count) return false;
            foreach (KeyValuePair<string, object> field in leftMap)
            {
                object other;
                if (!rightMap.TryGetValue(field.Key, out other) || !SemanticValuesEqual(field.Value, other, field.Key)) return false;
            }
            return true;
        }
        List<object> leftList = left as List<object>;
        List<object> rightList = right as List<object>;
        if (leftList != null || rightList != null)
        {
            if (leftList == null || rightList == null || leftList.Count != rightList.Count) return false;
            for (int i = 0; i < leftList.Count; i++)
                if (!SemanticValuesEqual(leftList[i], rightList[i], key)) return false;
            return true;
        }
        if (left is string && right is string && IsNumericGeometryField(key))
        {
            double a;
            double b;
            if (Double.TryParse((string)left, NumberStyles.Float, CultureInfo.InvariantCulture, out a) &&
                Double.TryParse((string)right, NumberStyles.Float, CultureInfo.InvariantCulture, out b)) return NearlyEqual(a, b);
        }
        if (left is string || right is string) return left is string && right is string && String.Equals(left as string, right as string, StringComparison.Ordinal);
        return Object.Equals(left, right);
    }

    private static bool IsNumericGeometryField(string key)
    {
        return String.Equals(key, "start", StringComparison.Ordinal) ||
            String.Equals(key, "end", StringComparison.Ordinal) ||
            String.Equals(key, "center", StringComparison.Ordinal) ||
            String.Equals(key, "coordinates", StringComparison.Ordinal) ||
            String.Equals(key, "origin", StringComparison.Ordinal) ||
            String.Equals(key, "normal", StringComparison.Ordinal) ||
            String.Equals(key, "x_axis", StringComparison.Ordinal) ||
            String.Equals(key, "y_axis", StringComparison.Ordinal) ||
            String.Equals(key, "radius", StringComparison.Ordinal) ||
            String.Equals(key, "start_angle", StringComparison.Ordinal) ||
            String.Equals(key, "end_angle", StringComparison.Ordinal) ||
            String.Equals(key, "value", StringComparison.Ordinal);
    }

    private static void RequireMillimeterPartAndUnit(Part part, string requestedUnit, string expectedSymbol)
    {
        if (part == null || part.PartUnits != BasePart.Units.Millimeters)
            throw new M2ModelException("UNSUPPORTED_PARAMETER", "parameter edits currently require a millimeter Work Part");
        if (!String.Equals(requestedUnit, "mm", StringComparison.Ordinal))
            throw new M2ModelException("UNIT_MISMATCH", "requested unit must be exactly mm");
        Unit baseLength = part.UnitCollection.GetBase("Length");
        if (baseLength == null || !String.Equals(baseLength.Symbol, "mm", StringComparison.Ordinal))
            throw new M2ModelException("UNIT_MISMATCH", "Work Part base length unit is not mm");
        if (expectedSymbol != null && !String.Equals(expectedSymbol, requestedUnit, StringComparison.Ordinal))
            throw new M2ModelException("UNIT_MISMATCH", "requested unit differs from the prepared expression unit");
    }

    private static void RequireWritableSimplePart(Part part)
    {
        if (part == null) throw new M2ModelException("UNSUPPORTED_PARAMETER", "Work Part is unavailable");
        if (IsPartReadOnly(part)) throw new M2ModelException("READ_ONLY_PART", "model modification is not allowed on a read-only Work Part");
        if (part.ComponentAssembly != null && part.ComponentAssembly.RootComponent != null)
            throw new M2ModelException("ASSEMBLY_UNSUPPORTED", "model modification does not support assembly Work Parts");
    }

    private static ParameterTarget ResolveParameterTarget(Part part, string sketchId, string constraintId)
    {
        if (part == null || String.IsNullOrWhiteSpace(sketchId) || String.IsNullOrWhiteSpace(constraintId))
            throw new M2ModelException("INVALID_REQUEST", "sketch_id and constraint_id are required");

        Sketch[] sketches = part.Sketches.ToArray();
        if (sketches == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketches.ToArray returned null");
        Sketch selectedSketch = null;
        int sketchMatches = 0;
        foreach (Sketch sketch in sketches)
        {
            if (sketch == null) throw new M2ModelException("UNSUPPORTED_MODEL", "sketch collection contains null");
            if (String.Equals(RequiredJournalId(sketch, "sketch"), sketchId, StringComparison.Ordinal))
            {
                selectedSketch = sketch;
                sketchMatches++;
            }
        }
        if (sketchMatches != 1 || selectedSketch == null)
            throw new M2ModelException(sketchMatches == 0 ? "SKETCH_NOT_FOUND" : "AMBIGUOUS_SKETCH", "sketch_id must identify exactly one Sketch in the bound Work Part");

        SketchDimensionalConstraint selectedDimension = null;
        int constraintMatches = 0;
        foreach (Sketch.ConstraintType type in Enum.GetValues(typeof(Sketch.ConstraintType)))
        {
            if (type == Sketch.ConstraintType.NoCon || type == Sketch.ConstraintType.LastConType) continue;
            SketchConstraint[] constraints = selectedSketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Any, type);
            if (constraints == null)
                throw new M2ModelException("MODEL_READ_FAILED", "constraint enumeration returned null for " + type);
            foreach (SketchConstraint constraint in constraints)
            {
                if (constraint == null) throw new M2ModelException("UNSUPPORTED_MODEL", "sketch constraint collection contains null");
                if (!String.Equals(RequiredJournalId(constraint, "sketch constraint"), constraintId, StringComparison.Ordinal)) continue;
                constraintMatches++;
                selectedDimension = constraint as SketchDimensionalConstraint;
            }
        }
        if (constraintMatches == 0 || selectedDimension == null)
            throw new M2ModelException("CONSTRAINT_NOT_FOUND", "constraint_id does not identify a dimensional constraint in the selected sketch");
        if (constraintMatches != 1)
            throw new M2ModelException("AMBIGUOUS_CONSTRAINT", "constraint_id identifies more than one constraint in the selected sketch");

        SketchDimensionalConstraint.DimensionStateType state = selectedDimension.DimensionState;
        if (state != SketchDimensionalConstraint.DimensionStateType.Driving)
            throw new M2ModelException("UNSUPPORTED_PARAMETER", "selected dimensional constraint is not Driving");
        Expression associated = selectedDimension.AssociatedExpression;
        if (associated == null)
            throw new M2ModelException("UNSUPPORTED_PARAMETER", "selected Driving dimension has no AssociatedExpression");

        Expression[] expressions = part.Expressions.ToArray();
        if (expressions == null) throw new M2ModelException("MODEL_READ_FAILED", "Expressions.ToArray returned null");
        int expressionMatches = 0;
        Expression partExpression = null;
        foreach (Expression expression in expressions)
        {
            if (expression == null) throw new M2ModelException("UNSUPPORTED_MODEL", "expression collection contains null");
            if (expression.Tag == associated.Tag)
            {
                expressionMatches++;
                partExpression = expression;
            }
        }
        if (expressionMatches != 1 || partExpression == null ||
            !String.Equals(partExpression.Name, associated.Name, StringComparison.Ordinal))
            throw new M2ModelException("UNSUPPORTED_PARAMETER", "AssociatedExpression is not a unique expression in the bound Work Part");
        if (partExpression.IsInterpartExpression)
            throw new M2ModelException("UNSUPPORTED_PARAMETER", "interpart expressions are outside the supported edit scope");
        Expression[] referencingExpressions = partExpression.GetReferencingExpressions();
        if (referencingExpressions == null || referencingExpressions.Length != 0)
            throw new M2ModelException("UNSUPPORTED_PARAMETER", "expressions that feed other expressions are outside the supported edit scope");
        Feature[] usingFeatures = partExpression.GetUsingFeatures();
        if (usingFeatures == null) throw new M2ModelException("MODEL_READ_FAILED", "associated expression feature users returned null");
        foreach (Feature usingFeature in usingFeatures)
        {
            if (usingFeature == null || usingFeature.OwningPart == null || usingFeature.OwningPart.Tag != part.Tag)
                throw new M2ModelException("UNSUPPORTED_PARAMETER", "associated expression is used by a feature outside the bound Work Part");
        }
        if (partExpression.Units == null || String.IsNullOrEmpty(partExpression.Units.Symbol))
            throw new M2ModelException("UNIT_MISMATCH", "associated expression has no explicit unit");

        int relationMatches = 0;
        SketchDimensionalConstraint relation = null;
        foreach (Sketch sketch in sketches)
        {
            foreach (Sketch.ConstraintType type in Enum.GetValues(typeof(Sketch.ConstraintType)))
            {
                if (type == Sketch.ConstraintType.NoCon || type == Sketch.ConstraintType.LastConType) continue;
                SketchConstraint[] constraints = sketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Any, type);
                if (constraints == null)
                    throw new M2ModelException("MODEL_READ_FAILED", "constraint enumeration returned null while validating expression uniqueness");
                foreach (SketchConstraint constraint in constraints)
                {
                    SketchDimensionalConstraint dimension = constraint as SketchDimensionalConstraint;
                    if (dimension == null) continue;
                    Expression expression = dimension.AssociatedExpression;
                    if (expression == null || expression.Tag != associated.Tag) continue;
                    relationMatches++;
                    relation = dimension;
                }
            }
        }
        if (relationMatches != 1 || relation == null || relation.Tag != selectedDimension.Tag)
            throw new M2ModelException("AMBIGUOUS_EXPRESSION_RELATION", "AssociatedExpression must belong to exactly the selected dimensional constraint");

        Sketch.DimensionGeometry[] operands = selectedDimension.GetDimensionGeometry();
        if (operands == null || operands.Length == 0)
            throw new M2ModelException("UNSUPPORTED_PARAMETER", "selected dimension has no measurable geometry operands");
        List<string> geometryRefs = new List<string>();
        List<string> signatureParts = new List<string>();
        foreach (Sketch.DimensionGeometry operand in operands)
        {
            if (operand.Geometry == null)
                throw new M2ModelException("UNSUPPORTED_PARAMETER", "selected dimension contains a null geometry operand");
            if (operand.Geometry.OwningPart == null || operand.Geometry.OwningPart.Tag != part.Tag)
                throw new M2ModelException("UNSUPPORTED_PARAMETER", "selected dimension references geometry outside the bound Work Part");
            string geometryId = RequiredJournalId(operand.Geometry, "dimension geometry");
            string objectType = operand.Geometry.GetType().Name;
            geometryRefs.Add(DimensionReference(operand));
            signatureParts.Add(geometryId + "|" + objectType + "|" + operand.Geometry.Tag.ToString() + "|" +
                operand.AssocType.ToString() + "|" + operand.AssocValue.ToString(CultureInfo.InvariantCulture));
        }
        geometryRefs.Sort(StringComparer.Ordinal);
        signatureParts.Sort(StringComparer.Ordinal);
        string geometryKind;
        double measured;
        Dictionary<string, object> geometryReadback;
        MeasureSupportedDimension(selectedSketch, selectedDimension, operands, out geometryKind, out measured, out geometryReadback);

        ParameterTarget target = new ParameterTarget();
        target.Sketch = selectedSketch;
        target.Dimension = selectedDimension;
        target.Expression = partExpression;
        target.Unit = partExpression.Units;
        target.SketchId = sketchId;
        target.ConstraintId = constraintId;
        target.ExpressionRightHandSide = partExpression.RightHandSide;
        target.UnitSymbol = partExpression.Units.Symbol;
        target.DimensionType = selectedDimension.ConstraintType.ToString();
        target.DimensionState = state.ToString();
        target.GeometrySignature = String.Join(";", signatureParts.ToArray());
        target.GeometryKind = geometryKind;
        target.GeometryRefs = geometryRefs;
        target.ExpressionValue = partExpression.Value;
        target.GeometryValue = measured;
        target.GeometryReadback = geometryReadback;
        EnsureFinite(target.ExpressionValue, "expression value");
        EnsureFinite(target.GeometryValue, "measured geometry value");
        return target;
    }

    private static void MeasureSupportedDimension(Sketch sketch, SketchDimensionalConstraint dimension,
        Sketch.DimensionGeometry[] operands, out string kind, out double value,
        out Dictionary<string, object> readback)
    {
        kind = null;
        value = 0.0;
        readback = new Dictionary<string, object>(StringComparer.Ordinal);
        string constraintType = dimension.ConstraintType.ToString();

        if (String.Equals(constraintType, "DiameterDim", StringComparison.Ordinal) && operands.Length == 1)
        {
            Arc arc = operands[0].Geometry as Arc;
            if (arc != null && operands[0].AssocValue == 0)
            {
                double sweep = Math.Abs(arc.EndAngle - arc.StartAngle);
                double radius = arc.Radius;
                if (NearlyEqual(sweep, 2.0 * Math.PI) && radius > 0.0)
                {
                    kind = "DIAMETER_ARC";
                    value = 2.0 * radius;
                    readback.Add("kind", kind);
                    readback.Add("geometry_id", RequiredJournalId(arc, "diameter arc"));
                    readback.Add("center", Point(arc.CenterPoint));
                    readback.Add("radius", NumberText(radius));
                    readback.Add("measured_value", NumberText(value));
                    return;
                }
            }
        }

        if (String.Equals(constraintType, "HorizontalDim", StringComparison.Ordinal) && operands.Length == 2)
        {
            Arc arc = null;
            NXOpen.Point point = null;
            foreach (Sketch.DimensionGeometry operand in operands)
            {
                Arc candidateArc = operand.Geometry as Arc;
                NXOpen.Point candidatePoint = operand.Geometry as NXOpen.Point;
                if (candidateArc != null && operand.AssocType == Sketch.AssocType.ArcCenter && arc == null)
                    arc = candidateArc;
                else if (candidatePoint != null && operand.AssocType == Sketch.AssocType.None && operand.AssocValue == 0 && point == null)
                    point = candidatePoint;
                else
                {
                    arc = null;
                    point = null;
                    break;
                }
            }
            if (arc != null && point != null)
            {
                Point3d center = arc.CenterPoint;
                Point3d reference = point.Coordinates;
                Matrix3x3 axes = sketch.Orientation.Element;
                double deltaX;
                double deltaY;
                double deltaNormal;
                ProjectToSketchAxes(center, reference, axes, out deltaX, out deltaY, out deltaNormal);
                if (NearlyEqual(deltaNormal, 0.0))
                {
                    kind = "HORIZONTAL_ARC_CENTER_TO_POINT";
                    value = Math.Abs(deltaX);
                    readback.Add("kind", kind);
                    readback.Add("arc_id", RequiredJournalId(arc, "dimension arc"));
                    readback.Add("point_id", RequiredJournalId(point, "dimension reference point"));
                    readback.Add("arc_center", Point(center));
                    readback.Add("reference_point", Point(reference));
                    readback.Add("sketch_x_axis", Vector(axes.Xx, axes.Xy, axes.Xz));
                    readback.Add("projected_y_delta", NumberText(deltaY));
                    readback.Add("measured_value", NumberText(value));
                    return;
                }
            }
        }

        if ((String.Equals(constraintType, "HorizontalDim", StringComparison.Ordinal) ||
            String.Equals(constraintType, "VerticalDim", StringComparison.Ordinal)) && operands.Length == 2)
        {
            Line line = operands[0].Geometry as Line;
            if (line != null && operands[1].Geometry is Line && line.Tag == operands[1].Geometry.Tag)
            {
                bool start = false;
                bool end = false;
                foreach (Sketch.DimensionGeometry operand in operands)
                {
                    if (operand.AssocValue != 0) { start = false; end = false; break; }
                    if (operand.AssocType == Sketch.AssocType.StartPoint && !start) start = true;
                    else if (operand.AssocType == Sketch.AssocType.EndPoint && !end) end = true;
                    else { start = false; end = false; break; }
                }
                if (start && end)
                {
                    Point3d a = line.StartPoint;
                    Point3d b = line.EndPoint;
                    Matrix3x3 axes = sketch.Orientation.Element;
                    double deltaX;
                    double deltaY;
                    double deltaNormal;
                    ProjectToSketchAxes(a, b, axes, out deltaX, out deltaY, out deltaNormal);
                    if (String.Equals(constraintType, "HorizontalDim", StringComparison.Ordinal) &&
                        NearlyEqual(deltaNormal, 0.0))
                    {
                        kind = "HORIZONTAL_LINE_ENDPOINTS";
                        value = Math.Abs(deltaX);
                    }
                    else if (String.Equals(constraintType, "VerticalDim", StringComparison.Ordinal) &&
                        NearlyEqual(deltaNormal, 0.0))
                    {
                        kind = "VERTICAL_LINE_ENDPOINTS";
                        value = Math.Abs(deltaY);
                    }
                    if (kind != null)
                    {
                        readback.Add("kind", kind);
                        readback.Add("line_id", RequiredJournalId(line, "dimension line"));
                        readback.Add("start", Point(a));
                        readback.Add("end", Point(b));
                        readback.Add("sketch_x_axis", Vector(axes.Xx, axes.Xy, axes.Xz));
                        readback.Add("sketch_y_axis", Vector(axes.Yx, axes.Yy, axes.Yz));
                        readback.Add("projected_delta_x", NumberText(deltaX));
                        readback.Add("projected_delta_y", NumberText(deltaY));
                        readback.Add("measurement_axis", String.Equals(constraintType, "HorizontalDim", StringComparison.Ordinal) ? "x" : "y");
                        readback.Add("measured_value", NumberText(value));
                        return;
                    }
                }
            }
        }

        throw new M2ModelException("UNSUPPORTED_PARAMETER", "selected dimension geometry does not match a supported measurable signature");
    }

    private static void RequireSameTarget(ParameterPlanState plan, ParameterTarget target)
    {
        if (target == null || target.Dimension.Tag != plan.Dimension.Tag || target.Expression.Tag != plan.Expression.Tag ||
            !String.Equals(target.SketchId, plan.SketchId, StringComparison.Ordinal) ||
            !String.Equals(target.ConstraintId, plan.ConstraintId, StringComparison.Ordinal) ||
            !String.Equals(target.Expression.Name, plan.ExpressionName, StringComparison.Ordinal) ||
            !String.Equals(target.DimensionType, plan.DimensionType, StringComparison.Ordinal) ||
            !String.Equals(target.DimensionState, "Driving", StringComparison.Ordinal) ||
            !String.Equals(target.UnitSymbol, plan.UnitSymbol, StringComparison.Ordinal) ||
            !String.Equals(target.GeometryKind, plan.GeometryKind, StringComparison.Ordinal) ||
            !String.Equals(target.GeometrySignature, plan.GeometrySignature, StringComparison.Ordinal) ||
            !StringListsEqual(target.GeometryRefs, plan.GeometryRefs))
            throw new M2ModelException("STALE_PARAMETER", "selected sketch constraint, expression relation, unit or geometry operands changed after preparation");
    }

    private static bool PlanFieldsMatch(M2ParameterPlanSnapshot prepared, ParameterPlanState plan)
    {
        return String.Equals(prepared.RevisionId, plan.RevisionId, StringComparison.Ordinal) &&
            String.Equals(prepared.SketchId, plan.SketchId, StringComparison.Ordinal) &&
            String.Equals(prepared.ConstraintId, plan.ConstraintId, StringComparison.Ordinal) &&
            String.Equals(prepared.SketchFeatureId, plan.SketchFeatureId, StringComparison.Ordinal) &&
            String.Equals(prepared.DimensionType, plan.DimensionType, StringComparison.Ordinal) &&
            String.Equals(prepared.DimensionState, "Driving", StringComparison.Ordinal) &&
            String.Equals(prepared.ExpressionName, plan.ExpressionName, StringComparison.Ordinal) &&
            String.Equals(prepared.ExpressionRightHandSide, plan.ExpressionRightHandSide, StringComparison.Ordinal) &&
            String.Equals(prepared.CurrentValue, NumberText(plan.CurrentExpressionValue), StringComparison.Ordinal) &&
            String.Equals(prepared.Unit, plan.UnitSymbol, StringComparison.Ordinal) &&
            StringListsEqual(prepared.GeometryRefs, plan.GeometryRefs);
    }

    private string TryRestoreParameter(M2ModelBinding binding, BindingState state, ParameterPlanState plan,
        Session.UndoMarkId mark, Dictionary<string, object> evidence)
    {
        try
        {
            SnapshotResult current = Capture(binding, state);
            string currentSemantic = CanonicalSemanticSnapshot(current.Snapshot);
            string currentGeometry = CaptureFullGeometry(state.WorkPart);
            ParameterTarget currentTarget = ResolveParameterTarget(state.WorkPart, plan.SketchId, plan.ConstraintId);
            RequireSameTarget(plan, currentTarget);
            bool same = String.Equals(currentSemantic, plan.SemanticSnapshot, StringComparison.Ordinal) &&
                String.Equals(currentGeometry, plan.FullGeometrySnapshot, StringComparison.Ordinal) &&
                String.Equals(currentTarget.ExpressionRightHandSide, plan.ExpressionRightHandSide, StringComparison.Ordinal) &&
                NearlyEqual(currentTarget.ExpressionValue, plan.CurrentExpressionValue) &&
                NearlyEqual(currentTarget.GeometryValue, plan.GeometryValue);
            if (same)
            {
                SetFinalReadbackEvidence(evidence, currentTarget);
                evidence["rollback_readback_verified"] = true;
                evidence["rollback_performed"] = false;
                return "UNCHANGED";
            }
        }
        catch (Exception ex) { evidence["pre_undo_state_check_error"] = ex.GetType().Name; }

        try
        {
            state.Session.UndoToMarkWithStatus(mark, "M2 selected sketch dimension edit");
            evidence["rollback_performed"] = true;
        }
        catch (Exception ex)
        {
            evidence["rollback_undo_error"] = ex.GetType().Name;
            return "UNKNOWN";
        }

        int rollbackErrors;
        try
        {
            rollbackErrors = state.Session.UpdateManager.DoUpdate(mark);
            evidence["rollback_update_error_count"] = rollbackErrors;
            if (rollbackErrors != 0) return "UNKNOWN";
        }
        catch (Exception ex)
        {
            evidence["rollback_update_error"] = ex.GetType().Name;
            return "UNKNOWN";
        }

        try
        {
            SnapshotResult restoredSnapshot = Capture(binding, state);
            string restoredSemantic = CanonicalSemanticSnapshot(restoredSnapshot.Snapshot);
            string restoredGeometry = CaptureFullGeometry(state.WorkPart);
            ParameterTarget restored = ResolveParameterTarget(state.WorkPart, plan.SketchId, plan.ConstraintId);
            RequireSameTarget(plan, restored);
            bool semanticRestored = String.Equals(restoredSemantic, plan.SemanticSnapshot, StringComparison.Ordinal);
            bool fullGeometryRestored = String.Equals(restoredGeometry, plan.FullGeometrySnapshot, StringComparison.Ordinal);
            bool rhsRestored = String.Equals(restored.ExpressionRightHandSide, plan.ExpressionRightHandSide, StringComparison.Ordinal);
            bool valueRestored = NearlyEqual(restored.ExpressionValue, plan.CurrentExpressionValue);
            bool geometryRestored = NearlyEqual(restored.GeometryValue, plan.GeometryValue);
            evidence["rollback_after_rhs"] = restored.ExpressionRightHandSide;
            evidence["rollback_after_value"] = NumberText(restored.ExpressionValue);
            evidence["rollback_after_unit"] = restored.UnitSymbol;
            evidence["geometry_after_rollback"] = restored.GeometryReadback;
            SetFinalReadbackEvidence(evidence, restored);
            evidence["rollback_rhs_verified"] = rhsRestored;
            evidence["rollback_expression_value_verified"] = valueRestored;
            evidence["rollback_geometry_verified"] = geometryRestored;
            evidence["rollback_semantic_snapshot_verified"] = semanticRestored;
            evidence["rollback_full_geometry_verified"] = fullGeometryRestored;
            bool verified = semanticRestored && fullGeometryRestored && rhsRestored &&
                valueRestored && geometryRestored;
            evidence["rollback_readback_verified"] = verified;
            return verified ? "ROLLED_BACK" : "UNKNOWN";
        }
        catch (Exception ex)
        {
            evidence["rollback_readback_error"] = ex.GetType().Name;
            evidence["rollback_readback_verified"] = false;
            return "UNKNOWN";
        }
    }

    private static void AddBeforeEvidence(Dictionary<string, object> evidence, ParameterPlanState plan)
    {
        evidence["before_rhs"] = plan.ExpressionRightHandSide;
        evidence["before_value"] = NumberText(plan.CurrentExpressionValue);
        evidence["before_unit"] = plan.UnitSymbol;
        evidence["geometry_before"] = Clone(plan.GeometryBefore);
    }

    private static void SetFinalReadbackEvidence(Dictionary<string, object> evidence, ParameterTarget target)
    {
        evidence["after_rhs"] = target.ExpressionRightHandSide;
        evidence["after_value"] = NumberText(target.ExpressionValue);
        evidence["after_unit"] = target.UnitSymbol;
        evidence["geometry_after"] = target.GeometryReadback;
    }

    private static bool IsNumericRhsEqual(string rightHandSide, double requestedValue)
    {
        double rightHandSideValue;
        return !String.IsNullOrWhiteSpace(rightHandSide) &&
            Double.TryParse(rightHandSide.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out rightHandSideValue) &&
            !Double.IsNaN(rightHandSideValue) && !Double.IsInfinity(rightHandSideValue) &&
            NearlyEqual(rightHandSideValue, requestedValue);
    }

    private static M2ParameterExecutionResult ParameterResult(string state, string revisionId,
        Dictionary<string, object> evidence, string errorCode, string errorMessage)
    {
        M2ParameterExecutionResult result = new M2ParameterExecutionResult();
        result.State = state;
        result.RevisionId = revisionId;
        result.Evidence = evidence;
        result.ErrorCode = errorCode;
        result.ErrorMessage = errorMessage;
        return result;
    }

    private static double ParsePositiveMillimeters(string text, string field)
    {
        double value;
        if (String.IsNullOrWhiteSpace(text) || !Double.TryParse(text.Trim(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out value) || Double.IsNaN(value) || Double.IsInfinity(value) || value <= 0.0)
            throw new M2ModelException("INVALID_VALUE", field + " must be a finite positive numeric millimeter value without a unit suffix");
        return value;
    }

    private static void EnsureFinite(double value, string field)
    {
        if (Double.IsNaN(value) || Double.IsInfinity(value))
            throw new M2ModelException("UNSUPPORTED_MODEL", "NX returned a non-finite " + field);
    }

    private static double ValueTolerance(double left, double right)
    {
        double scale = Math.Max(Math.Abs(left), Math.Abs(right));
        return Math.Max(1e-7, scale * 1e-9);
    }

    private static void ProjectToSketchAxes(Point3d start, Point3d end, Matrix3x3 axes,
        out double x, out double y, out double normal)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double dz = end.Z - start.Z;
        x = dx * axes.Xx + dy * axes.Xy + dz * axes.Xz;
        y = dx * axes.Yx + dy * axes.Yy + dz * axes.Yz;
        normal = dx * axes.Zx + dy * axes.Zy + dz * axes.Zz;
        EnsureFinite(x, "sketch X projection");
        EnsureFinite(y, "sketch Y projection");
        EnsureFinite(normal, "sketch normal projection");
    }

    private static bool NearlyEqual(double left, double right)
    {
        if (Double.IsNaN(left) || Double.IsInfinity(left) || Double.IsNaN(right) || Double.IsInfinity(right)) return false;
        return Math.Abs(left - right) <= ValueTolerance(left, right);
    }

    private static List<string> CopyStrings(List<string> values)
    {
        List<string> copy = new List<string>();
        if (values != null) foreach (string value in values) copy.Add(value);
        return copy;
    }

    private static bool StringListsEqual(List<string> left, List<string> right)
    {
        if (left == null || right == null || left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
            if (!String.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
        return true;
    }

    private static void TryDeleteUndoMark(Session session, Session.UndoMarkId mark)
    {
        try { session.DeleteUndoMark(mark, ""); }
        catch { }
    }

    private static string CanonicalSemanticSnapshot(Dictionary<string, object> snapshot)
    {
        Dictionary<string, object> copy = DeepCloneValue(snapshot) as Dictionary<string, object>;
        if (copy == null) throw new M2ModelException("MODEL_READ_FAILED", "semantic snapshot could not be normalized");
        NormalizeModifiedFlag(copy, "part");
        NormalizeModifiedFlag(copy, "identity");
        List<object> parts = copy.ContainsKey("session_parts") ? copy["session_parts"] as List<object> : null;
        if (parts != null)
        {
            foreach (object item in parts)
            {
                Dictionary<string, object> part = item as Dictionary<string, object>;
                if (part != null && part.ContainsKey("is_modified")) part["is_modified"] = false;
            }
        }
        List<object> bodies = copy.ContainsKey("bodies") ? copy["bodies"] as List<object> : null;
        if (bodies != null)
        {
            foreach (object item in bodies)
            {
                Dictionary<string, object> body = item as Dictionary<string, object>;
                if (body == null) continue;
                body.Remove("volume_mm3");
                body.Remove("bbox_mm");
                body.Remove("measurement_status");
            }
        }
        return M2Json.CanonicalText(copy);
    }

    private static void NormalizeModifiedFlag(Dictionary<string, object> snapshot, string key)
    {
        Dictionary<string, object> record = snapshot.ContainsKey(key) ? snapshot[key] as Dictionary<string, object> : null;
        if (record != null && record.ContainsKey("is_modified")) record["is_modified"] = false;
    }

    private static object DeepCloneValue(object value)
    {
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary != null)
        {
            Dictionary<string, object> copy = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> entry in dictionary) copy.Add(entry.Key, DeepCloneValue(entry.Value));
            return copy;
        }
        List<object> list = value as List<object>;
        if (list != null)
        {
            List<object> copy = new List<object>();
            foreach (object item in list) copy.Add(DeepCloneValue(item));
            return copy;
        }
        return value;
    }

    private static string CaptureFullGeometry(Part part)
    {
        return M2Json.CanonicalText(CaptureGeometryRecords(part, true));
    }

    private static List<object> CaptureGeometryRecords(Part part, bool includeTag)
    {
        Sketch[] sketches = part.Sketches.ToArray();
        if (sketches == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketches.ToArray returned null during geometry verification");
        List<Dictionary<string, object>> sketchRecords = new List<Dictionary<string, object>>();
        foreach (Sketch sketch in sketches)
        {
            if (sketch == null) throw new M2ModelException("UNSUPPORTED_MODEL", "sketch collection contains null during geometry verification");
            string sketchId = RequiredJournalId(sketch, "sketch");
            NXObject[] geometry = sketch.GetAllGeometry();
            if (geometry == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch.GetAllGeometry returned null during geometry verification");
            Dictionary<string, NXObject> allGeometry = new Dictionary<string, NXObject>(StringComparer.Ordinal);
            foreach (NXObject item in geometry) AddFullGeometryObject(allGeometry, item, part.Tag);
            foreach (Sketch.ConstraintType type in Enum.GetValues(typeof(Sketch.ConstraintType)))
            {
                if (type == Sketch.ConstraintType.NoCon || type == Sketch.ConstraintType.LastConType) continue;
                SketchConstraint[] constraints = sketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Any, type);
                if (constraints == null)
                    throw new M2ModelException("MODEL_READ_FAILED", "constraint enumeration returned null during geometry verification");
                foreach (SketchConstraint constraint in constraints)
                {
                    if (constraint == null) throw new M2ModelException("UNSUPPORTED_MODEL", "constraint collection contains null during geometry verification");
                    SketchDimensionalConstraint dimensional = constraint as SketchDimensionalConstraint;
                    if (dimensional != null)
                    {
                        Sketch.DimensionGeometry[] operands = dimensional.GetDimensionGeometry();
                        if (operands == null) throw new M2ModelException("MODEL_READ_FAILED", "dimension geometry returned null during geometry verification");
                        foreach (Sketch.DimensionGeometry operand in operands) AddFullGeometryObject(allGeometry, operand.Geometry, part.Tag);
                    }
                    else
                    {
                        SketchGeometricConstraint geometric = constraint as SketchGeometricConstraint;
                        if (geometric == null) throw new M2ModelException("UNSUPPORTED_MODEL", "constraint class cannot be included in complete geometry verification");
                        Sketch.ConstraintGeometry[] operands = geometric.GetGeometry();
                        if (operands == null) throw new M2ModelException("MODEL_READ_FAILED", "geometric constraint operands returned null during geometry verification");
                        foreach (Sketch.ConstraintGeometry operand in operands) AddFullGeometryObject(allGeometry, operand.Geometry, part.Tag);
                    }
                }
            }
            List<Dictionary<string, object>> geometryRecords = new List<Dictionary<string, object>>();
            foreach (NXObject item in allGeometry.Values)
            {
                string id = RequiredJournalId(item, "sketch geometry");
                Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
                record.Add("journal_id", id);
                if (includeTag) record.Add("tag", item.Tag.ToString());
                record.Add("object_type", item.GetType().Name);
                Line line = item as Line;
                Arc arc = item as Arc;
                NXOpen.Point point = item as NXOpen.Point;
                if (line != null)
                {
                    record.Add("start", Point(line.StartPoint));
                    record.Add("end", Point(line.EndPoint));
                }
                else if (arc != null)
                {
                    record.Add("center", Point(arc.CenterPoint));
                    record.Add("radius", NumberText(arc.Radius));
                    record.Add("start_angle", NumberText(arc.StartAngle));
                    record.Add("end_angle", NumberText(arc.EndAngle));
                }
                else if (point != null)
                {
                    record.Add("coordinates", Point(point.Coordinates));
                }
                else
                {
                    throw new M2ModelException("UNSUPPORTED_PARAMETER", "complete rollback geometry readback is unavailable for " + item.GetType().Name);
                }
                geometryRecords.Add(record);
            }
            SortRecords(geometryRecords, "journal_id", "sketch geometry");
            Dictionary<string, object> sketchRecord = new Dictionary<string, object>(StringComparer.Ordinal);
            sketchRecord.Add("journal_id", sketchId);
            sketchRecord.Add("geometry", ToObjectList(geometryRecords));
            sketchRecords.Add(sketchRecord);
        }
        SortRecords(sketchRecords, "journal_id", "sketch");
        List<object> completeRecords = ToObjectList(sketchRecords);
        completeRecords.AddRange(CaptureM7NativeDefinitions(part));
        return completeRecords;
    }

    private static void AddFullGeometryObject(Dictionary<string, NXObject> allGeometry, NXObject item, Tag partTag)
    {
        if (item == null) throw new M2ModelException("UNSUPPORTED_MODEL", "constraint geometry contains null during geometry verification");
        if (item.OwningPart == null || item.OwningPart.Tag != partTag)
            throw new M2ModelException("UNSUPPORTED_PARAMETER", "complete geometry verification does not support external geometry references");
        string key = item.Tag.ToString();
        if (String.IsNullOrEmpty(key)) throw new M2ModelException("UNSUPPORTED_MODEL", "geometry has no stable NX Tag during geometry verification");
        NXObject existing;
        if (allGeometry.TryGetValue(key, out existing))
        {
            if (!String.Equals(RequiredJournalId(existing, "sketch geometry"),
                RequiredJournalId(item, "sketch geometry"), StringComparison.Ordinal))
                throw new M2ModelException("UNSUPPORTED_MODEL", "geometry Tag resolves to inconsistent JournalIdentifiers");
            return;
        }
        allGeometry.Add(key, item);
    }

    private SnapshotResult Capture(M2ModelBinding binding, BindingState state)
    {
        bool modifiedBefore;
        try { modifiedBefore = state.WorkPart.IsModified; }
        catch (Exception ex) { throw new M2ModelException("MODEL_READ_FAILED", "could not read Work Part modified state (" + ex.GetType().Name + ")"); }

        Dictionary<string, object> snapshot;
        try { snapshot = BuildSemanticSnapshot(binding, state); }
        catch (M2ModelException) { throw; }
        catch (Exception ex)
        {
            throw new M2ModelException("MODEL_READ_FAILED", "semantic snapshot failed (" + ex.GetType().Name + ")");
        }

        bool modifiedAfter;
        try { modifiedAfter = state.WorkPart.IsModified; }
        catch (Exception ex) { throw new M2ModelException("MODEL_READ_FAILED", "could not verify Work Part modified state (" + ex.GetType().Name + ")"); }

        Dictionary<string, object> part = snapshot["part"] as Dictionary<string, object>;
        if (part == null) throw new M2ModelException("MODEL_READ_FAILED", "semantic snapshot has no part record");
        part["is_modified"] = modifiedAfter;
        Dictionary<string, object> identity = snapshot["identity"] as Dictionary<string, object>;
        if (identity == null) throw new M2ModelException("MODEL_READ_FAILED", "semantic snapshot has no identity record");
        identity["is_modified"] = modifiedAfter;
        List<object> inventory = snapshot["session_parts"] as List<object>;
        if (inventory == null) throw new M2ModelException("MODEL_READ_FAILED", "semantic snapshot has no session inventory");
        foreach (object inventoryItem in inventory)
        {
            Dictionary<string, object> partItem = inventoryItem as Dictionary<string, object>;
            if (partItem != null && String.Equals(Convert.ToString(partItem["tag"], CultureInfo.InvariantCulture),
                state.PartTag, StringComparison.Ordinal)) partItem["is_modified"] = modifiedAfter;
        }

        Dictionary<string, object> effects = new Dictionary<string, object>(StringComparer.Ordinal);
        effects.Add("is_modified_before_read", modifiedBefore);
        effects.Add("is_modified_after_read", modifiedAfter);
        effects.Add("dirty_changed_by_read", !modifiedBefore && modifiedAfter);
        effects.Add("read_may_mark_modified", true);
        effects.Add("note", "NXOpen getters can mark the Work Part modified; before/after state is reported without clearing dirty state.");

        string canonical = CanonicalSemanticSnapshot(snapshot);
        List<object> bodyMeasurements = BodyMeasurementProjection(snapshot);
        Dictionary<string, object> measurementComparison;
        bool measurementsChanged = state.CanonicalBodyMeasurements == null ||
            !BodyMeasurementRecordsEqual(state.CanonicalBodyMeasurements, bodyMeasurements, out measurementComparison);
        if (state.CanonicalBodyMeasurements == null) measurementComparison = new Dictionary<string, object>(StringComparer.Ordinal);
        bool semanticChanged = !String.Equals(state.CanonicalSnapshot, canonical, StringComparison.Ordinal);
        bool changed = semanticChanged || measurementsChanged;
        if (state.RevisionNumber == 0 || changed) state.RevisionNumber++;
        if (measurementsChanged || state.CanonicalBodyMeasurements == null)
            state.CanonicalBodyMeasurements = bodyMeasurements;
        state.CanonicalSnapshot = canonical;
        state.ReadEffects = effects;
        binding.CanonicalSnapshot = canonical;
        binding.RevisionId = state.RevisionNumber.ToString(CultureInfo.InvariantCulture);
        return new SnapshotResult { Snapshot = snapshot, ReadEffects = effects, SemanticChanged = changed };
    }

    private Dictionary<string, object> BuildSemanticSnapshot(M2ModelBinding binding, BindingState state)
    {
        Session session = state.Session;
        Part work = state.WorkPart;
        Dictionary<string, object> supportSummary = BuildArtifactSupportSummary(work, binding.ArtifactScope);
        string rejection = Convert.ToString(supportSummary["rejection_code"], CultureInfo.InvariantCulture);
        if (!String.IsNullOrEmpty(rejection))
            throw new M2ModelException(rejection, Convert.ToString(supportSummary["rejection_reason"], CultureInfo.InvariantCulture));
        state.SupportSummary = supportSummary;
        binding.ArtifactAssessment = supportSummary;
        binding.IsReadOnly = IsPartReadOnly(work);
        Dictionary<string, object> snapshot = new Dictionary<string, object>(StringComparer.Ordinal);
        snapshot.Add("schema", "m2-semantic-v1");
        snapshot.Add("identity", BuildIdentity(binding, work, null));

        Dictionary<string, object> part = new Dictionary<string, object>(StringComparer.Ordinal);
        part.Add("path", state.PartPath);
        part.Add("leaf", Path.GetFileName(state.PartPath));
        part.Add("tag", state.PartTag);
        part.Add("load_state", work.PartLoadState.ToString());
        part.Add("units", work.PartUnits.ToString());
        part.Add("is_read_only", IsPartReadOnly(work));
        part.Add("is_modified", work.IsModified);
        snapshot.Add("part", part);

        snapshot.Add("bodies", BuildBodies(work));
        snapshot.Add("features", BuildFeatures(work));
        snapshot.Add("native_helical_definitions", CaptureM7NativeDefinitions(work));
        snapshot.Add("curve_ids", ObjectStrings(GetJournalIds(work.Curves.ToArray(), "part curve")));
        snapshot.Add("sketches", BuildSketches(work));
        snapshot.Add("expressions", BuildExpressions(work));
        snapshot.Add("dependencies", BuildDependencies(work));
        snapshot.Add("support", BuildSupportSummary(work));
        snapshot.Add("session_parts", BuildPartInventory(session, state.CanonicalPath));
        snapshot.Add("support_summary", supportSummary);
        return snapshot;
    }

    private static List<object> BuildPartInventory(Session session, string workPath)
    {
        BasePart[] parts = session.Parts.ToArray();
        if (parts == null) throw new M2ModelException("MODEL_READ_FAILED", "Session.Parts inventory returned null");
        List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();
        bool foundWork = false;
        foreach (BasePart item in parts)
        {
            if (item == null) throw new M2ModelException("UNSUPPORTED_MODEL", "Session.Parts contains a null part");
            string path = item.FullPath ?? String.Empty;
            if (item.Tag == session.Parts.Work.Tag) foundWork = true;
            Dictionary<string, object> entry = new Dictionary<string, object>(StringComparer.Ordinal);
            entry.Add("path", path);
            entry.Add("tag", item.Tag.ToString());
            entry.Add("load_state", item.PartLoadState.ToString());
            entry.Add("is_read_only", IsPartReadOnly(item));
            entry.Add("is_modified", item.IsModified);
            records.Add(entry);
        }
        if (!foundWork) throw new M2ModelException("UNSUPPORTED_MODEL", "active Work Part is absent from Session.Parts inventory");
        records.Sort(delegate(Dictionary<string, object> left, Dictionary<string, object> right)
        {
            return String.CompareOrdinal((string)left["path"], (string)right["path"]);
        });
        return ToObjectList(records);
    }

    private static Dictionary<string, object> BuildArtifactSupportSummary(Part work, string artifactScope)
    {
        bool assembly = work.ComponentAssembly != null && work.ComponentAssembly.RootComponent != null;
        bool ugmanagerActive;
        UFSession.GetUFSession().UF.IsUgmanagerActive(out ugmanagerActive);
        bool teamcenter = ugmanagerActive || (work.FullPath ?? String.Empty).StartsWith("@DB", StringComparison.OrdinalIgnoreCase);
        Feature[] features = work.Features.GetFeatures();
        Sketch[] sketches = work.Sketches.ToArray();
        if (features == null || sketches == null)
            throw new M2ModelException("MODEL_READ_FAILED", "native history enumeration returned null");

        int waveOrLinkFeatures = 0;
        int externalFeatureParents = 0;
        int interpartExpressions = 0;
        int featureBackedSketches = 0;
        HashSet<string> externalSketchGeometry = new HashSet<string>(StringComparer.Ordinal);
        foreach (Feature feature in features)
        {
            if (feature == null) throw new M2ModelException("UNSUPPORTED_MODEL", "feature history contains null");
            string type = feature.FeatureType ?? String.Empty;
            string upperType = type.ToUpperInvariant();
            if (feature is WaveLink || upperType.Contains("WAVE") || upperType.Contains("LINKED") || upperType.Contains("INTERPART"))
                waveOrLinkFeatures++;
            if (feature.IsOccurrence) externalFeatureParents++;
            Feature[] parents = feature.GetParents();
            if (parents == null) throw new M2ModelException("MODEL_READ_FAILED", "feature parent enumeration returned null");
            foreach (Feature parent in parents)
            {
                if (parent == null || parent.IsOccurrence || parent.OwningPart == null || parent.OwningPart.Tag != work.Tag)
                    externalFeatureParents++;
            }
        }
        Expression[] expressions = work.Expressions.ToArray();
        if (expressions == null) throw new M2ModelException("MODEL_READ_FAILED", "expression history enumeration returned null");
        foreach (Expression expression in expressions)
        {
            if (expression == null) throw new M2ModelException("UNSUPPORTED_MODEL", "expression history contains null");
            if (expression.IsInterpartExpression) interpartExpressions++;
        }
        foreach (Sketch sketch in sketches)
        {
            if (sketch == null) throw new M2ModelException("UNSUPPORTED_MODEL", "sketch collection contains null");
            if (sketch.Feature != null && !sketch.Feature.IsOccurrence && sketch.Feature.OwningPart != null &&
                sketch.Feature.OwningPart.Tag == work.Tag) featureBackedSketches++;
            NXObject[] geometry = sketch.GetAllGeometry();
            if (geometry == null) throw new M2ModelException("MODEL_READ_FAILED", "sketch geometry enumeration returned null");
            foreach (NXObject item in geometry)
            {
                if (item == null) throw new M2ModelException("UNSUPPORTED_MODEL", "sketch geometry contains null");
                if (item.IsOccurrence || item.OwningPart == null || item.OwningPart.Tag != work.Tag)
                    externalSketchGeometry.Add(item.Tag.ToString());
            }
            foreach (Sketch.ConstraintType constraintType in Enum.GetValues(typeof(Sketch.ConstraintType)))
            {
                if (constraintType == Sketch.ConstraintType.NoCon || constraintType == Sketch.ConstraintType.LastConType) continue;
                SketchConstraint[] constraints = sketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Any, constraintType);
                if (constraints == null) throw new M2ModelException("MODEL_READ_FAILED", "sketch constraint enumeration returned null");
                foreach (SketchConstraint constraint in constraints)
                {
                    SketchGeometricConstraint geometric = constraint as SketchGeometricConstraint;
                    if (geometric != null)
                    {
                        Sketch.ConstraintGeometry[] operands = geometric.GetGeometry();
                        if (operands == null) throw new M2ModelException("MODEL_READ_FAILED", "sketch constraint geometry enumeration returned null");
                        foreach (Sketch.ConstraintGeometry operand in operands)
                        {
                            NXObject item = operand.Geometry;
                            if (item == null) throw new M2ModelException("UNSUPPORTED_MODEL", "sketch constraint geometry contains null");
                            if (item.IsOccurrence || item.OwningPart == null || item.OwningPart.Tag != work.Tag)
                                externalSketchGeometry.Add(item.Tag.ToString());
                        }
                    }
                    SketchDimensionalConstraint dimension = constraint as SketchDimensionalConstraint;
                    if (dimension != null)
                    {
                        Sketch.DimensionGeometry[] operands = dimension.GetDimensionGeometry();
                        if (operands == null) throw new M2ModelException("MODEL_READ_FAILED", "sketch dimension geometry enumeration returned null");
                        foreach (Sketch.DimensionGeometry operand in operands)
                        {
                            NXObject item = operand.Geometry;
                            if (item == null) throw new M2ModelException("UNSUPPORTED_MODEL", "sketch dimension geometry contains null");
                            if (item.IsOccurrence || item.OwningPart == null || item.OwningPart.Tag != work.Tag)
                                externalSketchGeometry.Add(item.Tag.ToString());
                        }
                    }
                }
            }
        }

        ExternalFileReference[] fileReferences = null;
        ExternalFileReferenceManager referenceManager = work.ExternalFileReferenceManager;
        if (referenceManager != null) referenceManager.GetExternalFileReferences(out fileReferences);
        int externalFileReferenceCount = fileReferences == null ? 0 : fileReferences.Length;

        string rejectionCode = null;
        string rejectionReason = null;
        if (assembly)
        {
            rejectionCode = "ASSEMBLY_UNSUPPORTED";
            rejectionReason = "M5 supports a standalone native part; assembly Work Parts are not supported";
        }
        else if (teamcenter)
        {
            rejectionCode = "TEAMCENTER_UNSUPPORTED";
            rejectionReason = "Teamcenter managed parts are outside the M5 independent-part scope";
        }
        else if (externalFileReferenceCount > 0 || interpartExpressions > 0 || externalFeatureParents > 0 ||
            externalSketchGeometry.Count > 0 || waveOrLinkFeatures > 0)
        {
            rejectionCode = "EXTERNAL_REFERENCE_UNSUPPORTED";
            rejectionReason = "M5 does not modify parts with external, interpart, occurrence, or WAVE/linked history references";
        }
        else if (!work.IsFullyLoaded)
        {
            rejectionCode = "PART_NOT_FULLY_LOADED";
            rejectionReason = "M5 requires the standalone Work Part to be fully loaded before inspection or modification";
        }

        Dictionary<string, object> dependencies = new Dictionary<string, object>(StringComparer.Ordinal);
        dependencies.Add("external_file_reference_count", externalFileReferenceCount);
        dependencies.Add("interpart_expression_count", interpartExpressions);
        dependencies.Add("external_feature_parent_count", externalFeatureParents);
        dependencies.Add("external_sketch_geometry_count", externalSketchGeometry.Count);
        dependencies.Add("wave_or_link_feature_count", waveOrLinkFeatures);
        Dictionary<string, object> featureSupport = BuildSupportSummary(work);
        List<object> unsupportedFeatures = featureSupport["unsupported"] as List<object>;
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("status", IsPartReadOnly(work) ? "READ_ONLY_INSPECTION" : "SUPPORTED_LIMITED");
        result.Add("artifact_scope", artifactScope);
        result.Add("history_mode", featureBackedSketches > 0 ? "NATIVE_SKETCH_HISTORY" : "APPEND_ONLY");
        result.Add("history_feature_count", features.Length);
        result.Add("sketch_count", sketches.Length);
        result.Add("feature_backed_sketch_count", featureBackedSketches);
        result.Add("driving_edit_scope", featureBackedSketches > 0
            ? "feature-backed Sketches with an actual Driving dimension and a supported M2 geometry signature"
            : "unsupported_without_feature_backed_sketch; appended M3 features remain available");
        result.Add("append_scope", "limited native sketch, extrusion, external detailed single-start 60-degree thread and three-guide trapezoidal helical groove operations");
        result.Add("unsupported_feature_parameter_count", unsupportedFeatures == null ? 0 : unsupportedFeatures.Count);
        result.Add("dependencies", dependencies);
        result.Add("is_read_only", IsPartReadOnly(work));
        result.Add("rejection_code", rejectionCode);
        result.Add("rejection_reason", rejectionReason);
        return result;
    }

    private static List<object> BuildBodies(Part work)
    {
        Body[] bodies = work.Bodies.ToArray();
        if (bodies == null) throw new M2ModelException("MODEL_READ_FAILED", "Bodies.ToArray returned null");
        List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();
        foreach (Body body in bodies)
        {
            if (body == null) throw new M2ModelException("UNSUPPORTED_MODEL", "body collection contains null");
            string id = RequiredJournalId(body, "body");
            Face[] faces = body.GetFaces();
            Edge[] edges = body.GetEdges();
            if (faces == null || edges == null) throw new M2ModelException("MODEL_READ_FAILED", "body face/edge enumeration returned null");
            Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
            item.Add("journal_id", id);
            item.Add("kind", body.IsSolidBody ? "SOLID_BODY" : "SHEET_BODY");
            item.Add("is_solid", body.IsSolidBody);
            item.Add("face_ids", ObjectStrings(GetJournalIds(faces, "face")));
            item.Add("edge_ids", ObjectStrings(GetJournalIds(edges, "edge")));
            M3BodyMeasure measured = MeasureBody(work, body);
            item.Add("volume_mm3", measured.VolumeMm3.HasValue ? (object)measured.VolumeMm3.Value : null);
            item.Add("bbox_mm", measured.BboxMm == null ? null : NumberList(measured.BboxMm));
            item.Add("measurement_status", measured.BboxMm == null ? "PART_UNITS_MIXED" : "MEASURED");
            int consistencyFaults; int[] consistencyTypes; Tag[] consistencyTags;
            UFSession.GetUFSession().Modl.AskBodyConsistency(body.Tag, out consistencyFaults, out consistencyTypes, out consistencyTags);
            item.Add("body_consistency_fault_count", consistencyFaults);
            records.Add(item);
        }
        SortRecords(records, "journal_id", "body");
        return ToObjectList(records);
    }

    private static M3BodyMeasure MeasureBody(Part work, Body body)
    {
        if (work == null || body == null) throw new M2ModelException("MODEL_READ_FAILED", "body measurement requires a Work Part and Body");
        M3BodyMeasure measured = new M3BodyMeasure();
        measured.Body = body;
        measured.Id = RequiredJournalId(body, "body");
        measured.IsSolid = body.IsSolidBody;
        if (body.IsSolidBody)
        {
            Unit[] units = new Unit[4];
            units[0] = work.UnitCollection.FindObject("Kilogram");
            units[1] = work.UnitCollection.FindObject("MilliMeter");
            units[2] = work.UnitCollection.FindObject("SquareMilliMeter");
            units[3] = work.UnitCollection.FindObject("CubicMilliMeter");
            if (units[0] == null || units[1] == null || units[2] == null || units[3] == null)
                throw new M2ModelException("MODEL_READ_FAILED", "NX millimeter mass-property units are unavailable");
            MeasureBodies mass = null;
            try
            {
                mass = work.MeasureManager.NewMassProperties(units, 0.99, new Body[] { body });
                if (mass == null || Double.IsNaN(mass.Volume) || Double.IsInfinity(mass.Volume))
                    throw new M2ModelException("MODEL_READ_FAILED", "NX returned no finite solid volume");
                measured.VolumeMm3 = mass.Volume;
            }
            finally { if (mass != null) mass.Dispose(); }
        }

        if (work.PartUnits == BasePart.Units.Mix)
        {
            measured.BboxMm = null;
        }
        else
        {
            double[] box = M7ExactBox(body.Tag);
            double scale = work.PartUnits == BasePart.Units.Inches ? 25.4 :
                (work.PartUnits == BasePart.Units.Meters ? 1000.0 :
                (work.PartUnits == BasePart.Units.Micrometers ? 0.001 : 1.0));
            for (int i = 0; i < box.Length; i++)
            {
                box[i] *= scale;
                if (Double.IsNaN(box[i]) || Double.IsInfinity(box[i]))
                    throw new M2ModelException("MODEL_READ_FAILED", "NX returned a non-finite body bounding box");
            }
            measured.BboxMm = box;
        }
        measured.PublicRecord = new Dictionary<string, object>(StringComparer.Ordinal);
        measured.PublicRecord.Add("journal_id", measured.Id);
        measured.PublicRecord.Add("is_solid", measured.IsSolid);
        measured.PublicRecord.Add("volume_mm3", measured.VolumeMm3.HasValue ? (object)measured.VolumeMm3.Value : null);
        measured.PublicRecord.Add("bbox_mm", measured.BboxMm == null ? null : NumberList(measured.BboxMm));
        measured.PublicRecord.Add("measurement_status", measured.BboxMm == null ? "PART_UNITS_MIXED" : "MEASURED");
        return measured;
    }

    private static List<object> NumberList(double[] values)
    {
        List<object> result = new List<object>();
        foreach (double value in values) result.Add(value);
        return result;
    }

    private static List<object> BuildFeatures(Part work)
    {
        Feature[] features = work.Features.GetFeatures();
        if (features == null) throw new M2ModelException("MODEL_READ_FAILED", "Features.GetFeatures returned null");
        List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();
        foreach (Feature feature in features)
        {
            if (feature == null) throw new M2ModelException("UNSUPPORTED_MODEL", "feature collection contains null");
            string id = RequiredJournalId(feature, "feature");
            string type = feature.FeatureType;
            if (String.IsNullOrEmpty(type)) throw new M2ModelException("UNSUPPORTED_MODEL", "feature " + id + " has no FeatureType");
            Feature[] parents = feature.GetParents();
            Feature[] children = feature.GetChildren();
            Body[] bodies = feature.GetBodies();
            Expression[] expressions = feature.GetExpressions();
            if (parents == null || children == null || bodies == null || expressions == null)
                throw new M2ModelException("MODEL_READ_FAILED", "feature relationship enumeration returned null for " + id);

            Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
            item.Add("journal_id", id);
            item.Add("feature_type", type);
            item.Add("suppressed", feature.Suppressed);
            item.Add("parent_ids", ObjectStrings(GetJournalIds(parents, "feature parent")));
            item.Add("child_ids", ObjectStrings(GetJournalIds(children, "feature child")));
            item.Add("body_ids", ObjectStrings(GetJournalIds(bodies, "feature body")));
            item.Add("expression_ids", ObjectStrings(GetExpressionIds(expressions)));
            item.Add("parameter_support", ParameterSupport(type));
            if (M7HasNativeDefinition(feature))
            {
                item.Add("native_definition", CaptureM7FeatureDefinition(work, feature));
                item["parameter_support"] = new Dictionary<string, object> {
                    { "status", "READ_ONLY_NATIVE_PARAMETERS" },
                    { "reason", "Native parameter and association readback is available; edits remain limited to supported Driving sketch dimensions." } };
            }
            records.Add(item);
        }
        SortRecords(records, "journal_id", "feature");
        return ToObjectList(records);
    }

    private static bool TryParseM4FaceSelector(string text, out M4FaceSelector selector)
    {
        selector = null;
        if (String.IsNullOrWhiteSpace(text) || text.Length > 256) return false;
        string value = text.Trim().ToLowerInvariant().Replace('\u3000', ' ');
        bool outermost = value.Contains("outermost") || value.Contains("most outer") ||
            value.Contains("最外侧") || value.Contains("最外面");
        Match coordinateMatch = Regex.Match(value,
            @"(?:\bat\s+)?([xyz])\s*=\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?)\s*(?:mm|毫米)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        double coordinate = 0.0;
        bool hasCoordinate = coordinateMatch.Success && Double.TryParse(coordinateMatch.Groups[2].Value,
            NumberStyles.Float, CultureInfo.InvariantCulture, out coordinate) &&
            !Double.IsNaN(coordinate) && !Double.IsInfinity(coordinate);
        if (coordinateMatch.Success && !hasCoordinate) return false;
        int axis = -1;
        int sign = 0;
        int directionTokens = 0;
        string[] axisNames = new string[] { "x", "y", "z" };
        for (int candidateAxis = 0; candidateAxis < 3; candidateAxis++)
        {
            string name = axisNames[candidateAxis];
            if (Regex.IsMatch(value, @"(?:\bpositive\s*[- ]?\s*" + name + @"\b|\b" + name + @"\s*\+|\+\s*" + name + @"\b)|正\s*" + name, RegexOptions.IgnoreCase))
            { axis = candidateAxis; sign = 1; directionTokens++; }
            if (Regex.IsMatch(value, @"(?:\bnegative\s*[- ]?\s*" + name + @"\b|\b" + name + @"\s*-|-\s*" + name + @"\b)|[负負]\s*" + name, RegexOptions.IgnoreCase))
            { axis = candidateAxis; sign = -1; directionTokens++; }
        }
        bool topAlias = Regex.IsMatch(value, @"\b(?:top|upper|topmost)\b|顶面|上表面|上面", RegexOptions.IgnoreCase);
        bool bottomAlias = Regex.IsMatch(value, @"\b(?:bottom|lower|bottommost)\b|底面|下表面|下面", RegexOptions.IgnoreCase);
        if (topAlias) { axis = 2; sign = 1; directionTokens++; }
        if (bottomAlias) { axis = 2; sign = -1; directionTokens++; }
        // One directional selector is required. A phrase containing two axes
        // cannot be reduced to whichever direction happened to be parsed last.
        if (directionTokens != 1) return false;
        if (coordinateMatch.Success && "xyz".IndexOf(Char.ToLowerInvariant(coordinateMatch.Groups[1].Value[0])) != axis)
            return false;
        if (coordinateMatch.Success && outermost) return false;
        if ((value.Contains("topmost") || value.Contains("bottommost")) && outermost) return false;

        string residue = value;
        if (coordinateMatch.Success) residue = residue.Remove(coordinateMatch.Index, coordinateMatch.Length);
        residue = Regex.Replace(residue, @"outermost|most\s+outer|最外侧|最外面", "", RegexOptions.IgnoreCase);
        residue = Regex.Replace(residue, @"\b(?:positive|negative)\s*[- ]?\s*[xyz]\b|\b[xyz]\s*[-+]|[-+]\s*[xyz]\b|正\s*[xyz]|[负負]\s*[xyz]|\b(?:top|upper|topmost|bottom|lower|bottommost)\b|顶面|上表面|上面|底面|下表面|下面", "", RegexOptions.IgnoreCase);
        residue = Regex.Replace(residue, @"\b(?:face|side|surface|planar|support|supporting|the|outer)\b|面|侧面|表面|支撑|外侧", "", RegexOptions.IgnoreCase);
        residue = Regex.Replace(residue, @"[\s,，:：()（）\[\]]", "");
        if (residue.Length != 0) return false;

        selector = new M4FaceSelector();
        selector.Axis = axis;
        selector.Sign = sign;
        selector.AxisName = axis == 0 ? "x" : (axis == 1 ? "y" : "z");
        selector.PositionRule = coordinateMatch.Success ? "at" : (outermost || value.Contains("topmost") || value.Contains("bottommost") ? "outermost" : "all");
        selector.CoordinateMm = coordinateMatch.Success ? (double?)coordinate : null;
        selector.Canonical = (sign > 0 ? "positive " : "negative ") + selector.AxisName + " side" +
            (selector.PositionRule == "outermost" ? " outermost" : (selector.PositionRule == "at" ? " at " + NumberText(coordinate) + " mm" : ""));
        return true;
    }

    private static List<object> M4SupportedSelectorExamples()
    {
        return new List<object> { "top", "bottom", "positive X side", "outermost positive X side",
            "positive X side at x=35 mm", "正X面", "最外侧正X面" };
    }

    private static Dictionary<string, object> M4UnsupportedSelectorResult(string selector, string revisionId)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("selection_status", "unsupported_selector");
        result.Add("selector", selector);
        result.Add("selector_normalized", null);
        result.Add("candidate_count", 0);
        result.Add("candidates", new List<object>());
        result.Add("clarification_required", true);
        result.Add("revision_id", revisionId);
        result.Add("supported_selector_examples", M4SupportedSelectorExamples());
        return result;
    }

    private static Dictionary<string, object> M4SelectorRecord(M4FaceSelector selector)
    {
        double[] normal = new double[3];
        normal[selector.Axis] = selector.Sign;
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
        record.Add("canonical", selector.Canonical);
        record.Add("axis", selector.AxisName);
        record.Add("side", selector.Sign > 0 ? "positive" : "negative");
        record.Add("normal_world", NumberList(normal));
        record.Add("position_rule", selector.PositionRule);
        if (selector.CoordinateMm.HasValue) record.Add("coordinate_mm", selector.CoordinateMm.Value);
        return record;
    }

    private List<M4FaceCandidate> ResolveM4Faces(Part work, M4FaceSelector selector, string bodyId,
        HashSet<string> excludedEdgeFeatureTags, Dictionary<string, string> featureJournalIdsByTag,
        List<string> excludedFeatureIds, Sketch supportSketch)
    {
        Body[] bodies = work.Bodies.ToArray();
        if (bodies == null) throw new M2ModelException("MODEL_READ_FAILED", "Bodies.ToArray returned null during M4 face query");
        List<Body> targets = new List<Body>();
        if (!String.IsNullOrWhiteSpace(bodyId))
        {
            foreach (Body body in bodies)
                if (body != null && String.Equals(RequiredJournalId(body, "body"), bodyId, StringComparison.Ordinal)) targets.Add(body);
            if (targets.Count == 0) throw new M2ModelException("M4_SUPPORT_BODY_NOT_FOUND", "body_id does not identify a Body in the current Work Part");
            if (targets.Count != 1) throw new M2ModelException("M4_SUPPORT_BODY_AMBIGUOUS", "body_id does not identify one Body");
            if (!targets[0].IsSolidBody) throw new M2ModelException("UNSUPPORTED_BODY", "face query body_id must identify a solid body");
        }
        else
        {
            foreach (Body body in bodies)
                if (body != null && body.IsSolidBody) targets.Add(body);
        }

        List<M4FaceCandidate> result = new List<M4FaceCandidate>();
        double[] requestedNormal = new double[3];
        requestedNormal[selector.Axis] = selector.Sign;
        foreach (Body body in targets)
        {
            Face[] faces = body.GetFaces();
            if (faces == null) throw new M2ModelException("MODEL_READ_FAILED", "Body.GetFaces returned null during M4 query");
            foreach (Face face in faces)
            {
                if (face == null) throw new M2ModelException("MODEL_READ_FAILED", "Body face collection contains null");
                int type;
                double[] point = new double[3];
                double[] direction = new double[3];
                double[] faceBox = new double[6];
                double radius, radiusData;
                int normalDirection;
                UFSession.GetUFSession().Modl.AskFaceData(face.Tag, out type, point, direction,
                    faceBox, out radius, out radiusData, out normalDirection);
                if (type != UFConstants.UF_MODL_PLANAR_FACE) continue;
                double directionLength = Length3(direction);
                if (directionLength <= 1e-12) continue;
                // For planar faces AskFaceData.dir is the face normal used by
                // the P04 NX probe. norm_dir relates that normal to the surface
                // parameterization and must not reverse candidate orientation.
                double[] normal = new double[] { direction[0] / directionLength,
                    direction[1] / directionLength, direction[2] / directionLength };
                if (Dot3(normal, requestedNormal) < M4FaceNormalDotTolerance) continue;
                M4FaceCandidate candidate = BuildM4FaceCandidate(work, body, face, point, normal,
                    excludedEdgeFeatureTags, featureJournalIdsByTag, excludedFeatureIds, supportSketch);
                candidate.PlaneCoordinate = point[selector.Axis];
                candidate.PlaneOffset = Dot3(candidate.Normal, candidate.Origin);
                result.Add(candidate);
            }
        }
        return result;
    }

    private M4FaceCandidate ResolveM4UniqueFace(Part work, string selectorText, string bodyId)
    {
        return ResolveM4UniqueFace(work, selectorText, bodyId, null);
    }

    private M4FaceCandidate ResolveM4UniqueFace(Part work, string selectorText, string bodyId,
        List<string> excludedFeatureIds)
    {
        return ResolveM4UniqueFace(work, selectorText, bodyId, excludedFeatureIds, null);
    }

    private M4FaceCandidate ResolveM4UniqueFace(Part work, string selectorText, string bodyId,
        List<string> excludedFeatureIds, Sketch supportSketch)
    {
        M4FaceSelector selector;
        if (!TryParseM4FaceSelector(selectorText, out selector))
            throw new M2ModelException("M4_UNSUPPORTED_SELECTOR", "selector is unsupported; use top/bottom, positive or negative X/Y/Z, optional outermost or an explicit mm coordinate");
        Dictionary<string, string> featureJournalIdsByTag;
        HashSet<string> excludedTags = M4FeatureTags(work, excludedFeatureIds, out featureJournalIdsByTag);
        List<M4FaceCandidate> candidates = ResolveM4Faces(work, selector, bodyId, excludedTags,
            featureJournalIdsByTag, excludedFeatureIds, supportSketch);
        if (selector.PositionRule == "outermost" && candidates.Count > 0)
        {
            double extreme = candidates[0].PlaneCoordinate;
            for (int i = 1; i < candidates.Count; i++)
                extreme = selector.Sign > 0 ? Math.Max(extreme, candidates[i].PlaneCoordinate) : Math.Min(extreme, candidates[i].PlaneCoordinate);
            candidates.RemoveAll(delegate(M4FaceCandidate item) { return Math.Abs(item.PlaneCoordinate - extreme) > 1e-6; });
        }
        else if (selector.PositionRule == "at" && candidates.Count > 0)
        {
            double coordinate = selector.CoordinateMm.Value;
            candidates.RemoveAll(delegate(M4FaceCandidate item) { return Math.Abs(item.PlaneCoordinate - coordinate) > 1e-5; });
        }
        if (candidates.Count == 0)
            throw new M2ModelException("M4_FACE_NOT_FOUND", "selector did not match a planar face on the requested body scope");
        if (candidates.Count != 1)
            throw new M2ModelException("M4_FACE_AMBIGUOUS", "selector matches " + candidates.Count.ToString(CultureInfo.InvariantCulture) + " planar faces; add an explicit outermost or at-axis-mm qualifier");
        return candidates[0];
    }

    private M4FaceCandidate BuildM4FaceCandidate(Part work, Body body, Face face, double[] facePoint,
        double[] normal, HashSet<string> excludedEdgeFeatureTags,
        Dictionary<string, string> featureJournalIdsByTag, List<string> excludedFeatureIds, Sketch supportSketch)
    {
        M4FaceCandidate item = new M4FaceCandidate();
        item.Face = face;
        item.Body = body;
        item.BodyId = RequiredJournalId(body, "body");
        item.FaceId = RequiredJournalId(face, "face");
        item.Normal = (double[])normal.Clone();
        item.FeatureJournalIdsByTag = featureJournalIdsByTag;
        item.ExcludedCutFeatures = StringListAsObjects(excludedFeatureIds ?? new List<string>());
        item.PlaneOffset = Dot3(normal, facePoint);
        item.Origin = new double[] { normal[0] * item.PlaneOffset, normal[1] * item.PlaneOffset, normal[2] * item.PlaneOffset };
        double[] reference = Math.Abs(normal[0]) < 0.999 ? new double[] { 1, 0, 0 } : new double[] { 0, 1, 0 };
        double alongNormal = Dot3(reference, normal);
        double[] x = new double[] { reference[0] - alongNormal * normal[0], reference[1] - alongNormal * normal[1], reference[2] - alongNormal * normal[2] };
        Normalize3(x);
        double[] y = Cross3(normal, x);
        Normalize3(y);
        item.XAxis = x;
        item.YAxis = y;
        item.Bbox = new double[6];
        UFSession.GetUFSession().Modl.AskBoundingBox(face.Tag, item.Bbox);
        MeasureFaces measurement = work.MeasureManager.NewFaceProperties(
            work.UnitCollection.FindObject("SquareMilliMeter"), work.UnitCollection.FindObject("MilliMeter"),
            0.999, new IParameterizedSurface[] { face });
        item.Area = measurement.Area;
        if (supportSketch != null)
        {
            Dictionary<string, object> profileEvidence;
            item.SupportProfiles = M4BuildSketchProfiles(supportSketch, item, out profileEvidence);
        }
        item.Loops = ReadM4FaceLoops(face, item, excludedEdgeFeatureTags, item.SupportProfiles,
            out item.BoundarySupported, out item.BoundaryReason, out item.BoundaryTypes);
        item.Centroid = M4FaceCentroid(item, facePoint);
        return item;
    }

    private static Dictionary<string, object> M4FaceCandidateRecord(M4FaceCandidate candidate)
    {
        Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
        item.Add("surface_type", "planar");
        item.Add("body_id", candidate.BodyId);
        item.Add("face_journal_id", candidate.FaceId);
        item.Add("normal_world", NumberList(candidate.Normal));
        item.Add("plane_origin_mm", NumberList(candidate.Origin));
        item.Add("plane_offset_mm", candidate.PlaneOffset);
        item.Add("centroid_mm", NumberList(candidate.Centroid));
        item.Add("area_mm2", candidate.Area);
        item.Add("bbox_mm", NumberList(candidate.Bbox));
        item.Add("loop_count", candidate.Loops == null ? 0 : candidate.Loops.Count);
        item.Add("boundary_types", candidate.BoundaryTypes == null ? new List<object>() : StringListAsObjects(candidate.BoundaryTypes));
        item.Add("boundary_supported", candidate.BoundarySupported);
        item.Add("boundary_chord_tolerance_mm", M4BoundaryChordToleranceMm);
        item.Add("frame", M4FrameRecord(candidate.Origin, candidate.XAxis, candidate.YAxis, candidate.Normal));
        return item;
    }

    private static Dictionary<string, object> M4FrameRecord(double[] origin, double[] xAxis, double[] yAxis, double[] normal)
    {
        Dictionary<string, object> frame = new Dictionary<string, object>(StringComparer.Ordinal);
        frame.Add("origin_mm", NumberList(origin));
        frame.Add("x_axis_world", NumberList(xAxis));
        frame.Add("y_axis_world", NumberList(yAxis));
        frame.Add("normal_world", NumberList(normal));
        return frame;
    }

    private static void SortM4FaceCandidates(List<M4FaceCandidate> candidates)
    {
        candidates.Sort(delegate(M4FaceCandidate left, M4FaceCandidate right)
        {
            int byBody = String.CompareOrdinal(left.BodyId, right.BodyId);
            if (byBody != 0) return byBody;
            int byOffset = left.PlaneCoordinate.CompareTo(right.PlaneCoordinate);
            if (byOffset != 0) return byOffset;
            return String.CompareOrdinal(left.FaceId, right.FaceId);
        });
    }

    private static double[] M4FaceCentroid(M4FaceCandidate candidate, double[] fallback)
    {
        if (candidate.Loops == null || candidate.Loops.Count == 0) return (double[])fallback.Clone();
        double weightedX = 0.0, weightedY = 0.0, total = 0.0;
        for (int i = 0; i < candidate.Loops.Count; i++)
        {
            double area, cx, cy;
            M4PolygonAreaCentroid(candidate.Loops[i].Points, out area, out cx, out cy);
            if (Math.Abs(area) <= 1e-10) continue;
            int depth = 0;
            M4Point2 probe = candidate.Loops[i].Points[0];
            for (int j = 0; j < candidate.Loops.Count; j++)
                if (i != j && M4PointInPolygon(probe, candidate.Loops[j].Points)) depth++;
            double signed = (depth % 2 == 0 ? 1.0 : -1.0) * Math.Abs(area);
            weightedX += signed * cx;
            weightedY += signed * cy;
            total += signed;
        }
        if (Math.Abs(total) <= 1e-10) return (double[])fallback.Clone();
        double localX = weightedX / total, localY = weightedY / total;
        return new double[] {
            candidate.Origin[0] + localX * candidate.XAxis[0] + localY * candidate.YAxis[0],
            candidate.Origin[1] + localX * candidate.XAxis[1] + localY * candidate.YAxis[1],
            candidate.Origin[2] + localX * candidate.XAxis[2] + localY * candidate.YAxis[2]
        };
    }

    private static List<object> StringListAsObjects(List<string> values)
    {
        List<object> result = new List<object>();
        foreach (string value in values) result.Add(value);
        return result;
    }

    private static List<M4FaceLoop> ReadM4FaceLoops(Face face, M4FaceCandidate frame,
        HashSet<string> excludedEdgeFeatureTags, List<M4Profile> supportProfiles,
        out bool supported, out string reason, out List<string> boundaryTypes)
    {
        supported = false;
        reason = null;
        boundaryTypes = new List<string>();
        List<M4FaceLoop> loops = new List<M4FaceLoop>();
        Edge[] edges;
        try { edges = face.GetEdges(); }
        catch (Exception ex) { reason = "Face.GetEdges failed (" + ex.GetType().Name + ")"; return loops; }
        if (edges == null || edges.Length == 0)
        {
            reason = "planar face has no enumerable boundary edges";
            return loops;
        }
        List<List<M4Point2>> edgePoints = new List<List<M4Point2>>();
        List<string> edgeTypes = new List<string>();
        List<bool> edgeOwnedByCut = new List<bool>();
        List<object> edgeFeatureEvidence = new List<object>();
        foreach (Edge edge in edges)
        {
            if (edge == null) { reason = "face boundary contains a null edge"; return loops; }
            bool ownedByExcludedFeature = false;
            Dictionary<string, object> edgeEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
            if (excludedEdgeFeatureTags != null && excludedEdgeFeatureTags.Count > 0)
            {
                try { ownedByExcludedFeature = M4EdgeUsesExcludedFeature(edge, excludedEdgeFeatureTags,
                    frame.FeatureJournalIdsByTag, out edgeEvidence); }
                catch (Exception ex)
                {
                    reason = "could not identify downstream cut-edge provenance (" + ex.GetType().Name + ")";
                    return new List<M4FaceLoop>();
                }
            }
            else
            {
                edgeEvidence.Add("edge_tag", edge.Tag.ToString());
                try { edgeEvidence.Add("edge_journal_id", edge.JournalIdentifier); }
                catch { edgeEvidence.Add("edge_journal_id", null); }
                edgeEvidence.Add("ask_edge_feature_tags", new List<object>());
                edgeEvidence.Add("has_own_cut_feature", false);
                edgeEvidence.Add("all_feature_tags_are_own_cut", false);
            }
            int type;
            try { UFSession.GetUFSession().Modl.AskEdgeType(edge.Tag, out type); }
            catch (Exception ex) { reason = "AskEdgeType failed (" + ex.GetType().Name + ")"; return loops; }
            string edgeType;
            List<double[]> worldPoints = new List<double[]>();
            if (type == UFConstants.UF_MODL_LINEAR_EDGE)
            {
                edgeType = "line";
                double[] first = new double[3], last = new double[3];
                int vertexCount;
                try { UFSession.GetUFSession().Modl.AskEdgeVerts(edge.Tag, first, last, out vertexCount); }
                catch (Exception ex) { reason = "AskEdgeVerts failed (" + ex.GetType().Name + ")"; return loops; }
                if (vertexCount != 2)
                {
                    reason = "linear face boundary did not have exactly two vertices";
                    return loops;
                }
                worldPoints.Add(first);
                worldPoints.Add(last);
            }
            else if (type == UFConstants.UF_MODL_CIRCULAR_EDGE)
            {
                edgeType = "arc";
                int count;
                double[] points;
                try
                {
                    UFSession.GetUFSession().Modl.AskCurvePoints(edge.Tag,
                        M4BoundaryChordToleranceMm, 0.01, 0.5, out count, out points);
                }
                catch (Exception ex) { reason = "AskCurvePoints failed (" + ex.GetType().Name + ")"; return loops; }
                if (count < 3 || count > 100000 || points == null || points.Length < count * 3)
                {
                    reason = "circular face boundary could not be sampled within the supported point limit";
                    return loops;
                }
                for (int i = 0; i < count; i++)
                    worldPoints.Add(new double[] { points[i * 3], points[i * 3 + 1], points[i * 3 + 2] });
            }
            else
            {
                edgeType = "unsupported";
                if (!boundaryTypes.Contains(edgeType)) boundaryTypes.Add(edgeType);
                reason = "face boundaries support only exact line edges and circular edges; found UF edge type " + type.ToString(CultureInfo.InvariantCulture);
                return loops;
            }
            if (!boundaryTypes.Contains(edgeType)) boundaryTypes.Add(edgeType);
            List<M4Point2> localPoints = new List<M4Point2>();
            foreach (double[] world in worldPoints)
                localPoints.Add(M4ProjectToFrame(frame, world));
            RemoveAdjacentDuplicatePoints(localPoints, 1e-8);
            if (localPoints.Count < 2)
            {
                reason = "face boundary edge returned fewer than two distinct points";
                return loops;
            }
            edgePoints.Add(localPoints);
            edgeTypes.Add(edgeType);
            edgeOwnedByCut.Add(ownedByExcludedFeature);
            edgeFeatureEvidence.Add(edgeEvidence);
            frame.EdgeFeatureProvenance.Add(edgeEvidence);
        }

        bool[] used = new bool[edgePoints.Count];
        for (int seed = 0; seed < edgePoints.Count; seed++)
        {
            if (used[seed]) continue;
            List<M4Point2> path = new List<M4Point2>(edgePoints[seed]);
            string loopType = edgeTypes[seed];
            bool loopOwnedByCut = edgeOwnedByCut[seed];
            List<int> loopEdgeIndexes = new List<int> { seed };
            used[seed] = true;
            if (M4PointsNear(path[0], path[path.Count - 1], 1e-6))
            {
                path[path.Count - 1] = path[0];
                Dictionary<string, object> profileMatchEvidence;
                bool profileMatches = M4OwnedCutLoopMatchesProfile(path, supportProfiles, out profileMatchEvidence);
                AddM4LoopProvenance(frame, path, loopEdgeIndexes, edgeFeatureEvidence,
                    loopType, loopOwnedByCut, profileMatchEvidence);
                if (loopOwnedByCut)
                {
                    if (!profileMatches)
                    { reason = "own-cut circular/rectangular boundary could not be proven isolated from other face loops"; return new List<M4FaceLoop>(); }
                }
                else loops.Add(new M4FaceLoop { Points = path, EdgeType = loopType });
                continue;
            }
            bool closed = false;
            for (int hop = 0; hop < edgePoints.Count; hop++)
            {
                M4Point2 end = path[path.Count - 1];
                int next = -1;
                bool reverse = false;
                for (int candidate = 0; candidate < edgePoints.Count; candidate++)
                {
                    if (used[candidate]) continue;
                    if (M4PointsNear(end, edgePoints[candidate][0], 1e-6)) { next = candidate; break; }
                    if (M4PointsNear(end, edgePoints[candidate][edgePoints[candidate].Count - 1], 1e-6))
                    { next = candidate; reverse = true; break; }
                }
                if (next < 0)
                {
                    reason = "face boundary edges did not form closed line/circular loops";
                    return new List<M4FaceLoop>();
                }
                List<M4Point2> nextPoints = edgePoints[next];
                used[next] = true;
                loopEdgeIndexes.Add(next);
                loopOwnedByCut = loopOwnedByCut && edgeOwnedByCut[next];
                if (loopType != edgeTypes[next]) loopType = "line+arc";
                if (reverse)
                {
                    for (int k = nextPoints.Count - 2; k >= 0; k--) path.Add(nextPoints[k]);
                }
                else
                {
                    for (int k = 1; k < nextPoints.Count; k++) path.Add(nextPoints[k]);
                }
                if (M4PointsNear(path[0], path[path.Count - 1], 1e-6))
                {
                    path[path.Count - 1] = path[0];
                    closed = true;
                    break;
                }
            }
            if (!closed)
            {
                reason = "face boundary loop exceeded the supported edge count";
                return new List<M4FaceLoop>();
            }
            Dictionary<string, object> completeLoopProfileEvidence;
            bool completeLoopMatchesProfile = M4OwnedCutLoopMatchesProfile(path, supportProfiles, out completeLoopProfileEvidence);
            AddM4LoopProvenance(frame, path, loopEdgeIndexes, edgeFeatureEvidence,
                loopType, loopOwnedByCut, completeLoopProfileEvidence);
            if (loopOwnedByCut)
            {
                if (!completeLoopMatchesProfile)
                { reason = "own-cut boundary did not match the live source Sketch profile"; return new List<M4FaceLoop>(); }
            }
            else loops.Add(new M4FaceLoop { Points = path, EdgeType = loopType });
        }
        supported = loops.Count > 0;
        reason = supported ? null : "planar face has no closed boundary loops";
        return loops;
    }

    private static void AddM4LoopProvenance(M4FaceCandidate frame, List<M4Point2> points,
        List<int> edgeIndexes, List<object> edgeFeatureEvidence, string loopType, bool ownedByOwnCut,
        Dictionary<string, object> profileMatchEvidence)
    {
        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("edge_type", loopType);
        evidence.Add("closed", points != null && points.Count >= 2 && M4PointsNear(points[0], points[points.Count - 1], 1e-6));
        evidence.Add("edge_indexes", edgeIndexes == null ? new List<object>() : new List<object>(edgeIndexes.ConvertAll<object>(delegate(int index) { return index; })));
        evidence.Add("all_edges_have_own_cut_influence", ownedByOwnCut);
        List<object> loopPoints = new List<object>();
        if (points != null)
            foreach (M4Point2 point in points) loopPoints.Add(new List<object> { point.X, point.Y });
        evidence.Add("local_loop_points_mm", loopPoints);
        List<object> edges = new List<object>();
        if (edgeIndexes != null && edgeFeatureEvidence != null)
            foreach (int index in edgeIndexes)
                if (index >= 0 && index < edgeFeatureEvidence.Count) edges.Add(edgeFeatureEvidence[index]);
        evidence.Add("edges", edges);
        evidence.Add("profile_match", profileMatchEvidence);
        frame.LoopProvenance.Add(evidence);
    }

    private static bool M4EdgeUsesExcludedFeature(Edge edge, HashSet<string> excludedFeatureTags,
        Dictionary<string, string> featureJournalIdsByTag, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("edge_tag", edge == null ? null : edge.Tag.ToString());
        try { evidence.Add("edge_journal_id", edge == null ? null : edge.JournalIdentifier); }
        catch { evidence.Add("edge_journal_id", null); }
        Tag[] featureTags;
        UFSession.GetUFSession().Modl.AskEdgeFeats(edge.Tag, out featureTags);
        List<object> featureRecords = new List<object>();
        bool allExcluded = featureTags != null && featureTags.Length > 0;
        bool hasOwnCut = false;
        if (featureTags != null)
        {
            foreach (Tag tag in featureTags)
            {
                string tagText = tag.ToString();
                string journalId = null;
                if (featureJournalIdsByTag != null) featureJournalIdsByTag.TryGetValue(tagText, out journalId);
                bool excluded = excludedFeatureTags != null && excludedFeatureTags.Contains(tagText);
                Dictionary<string, object> featureRecord = new Dictionary<string, object>(StringComparer.Ordinal);
                featureRecord.Add("tag", tagText);
                featureRecord.Add("journal_id", journalId);
                featureRecord.Add("own_cut_feature", excluded);
                featureRecords.Add(featureRecord);
                if (excluded) hasOwnCut = true;
                if (!excluded) allExcluded = false;
            }
        }
        evidence.Add("ask_edge_feature_tags", featureRecords);
        evidence.Add("has_own_cut_feature", hasOwnCut);
        evidence.Add("all_feature_tags_are_own_cut", allExcluded);
        return hasOwnCut;
    }

    private static bool M4OwnedCutLoopMatchesProfile(List<M4Point2> loop, List<M4Profile> profiles,
        out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("closed_loop_point_count", loop == null ? 0 : loop.Count);
        evidence.Add("profile_count", profiles == null ? 0 : profiles.Count);
        List<object> profileChecks = new List<object>();
        bool matched = false;
        if (loop == null || loop.Count < 4 || profiles == null || profiles.Count == 0)
        {
            evidence.Add("matched", false);
            evidence.Add("reason", "closed loop or supported source profile is unavailable");
            evidence.Add("profiles", profileChecks);
            return false;
        }
        foreach (M4Profile profile in profiles)
        {
            if (!profile.Closed || (profile.Kind != "circle" && profile.Kind != "rectangle" && profile.Kind != "polygon")) continue;
            bool profileMatches = false;
            Dictionary<string, object> profileEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
            profileEvidence.Add("geometry_ids", StringListAsObjects(profile.GeometryIds));
            profileEvidence.Add("type", profile.Kind);
            if (profile.Kind == "circle")
            {
                bool allOnCircle = true;
                double maxLoopPointRadialError = 0.0;
                for (int i = 0; i < loop.Count - 1; i++)
                {
                    double dx = loop[i].X - profile.Center.X, dy = loop[i].Y - profile.Center.Y;
                    double error = Math.Abs(Math.Sqrt(dx * dx + dy * dy) - profile.Radius);
                    maxLoopPointRadialError = Math.Max(maxLoopPointRadialError, error);
                    if (error > M4BoundaryChordToleranceMm * 4.0) allOnCircle = false;
                }
                List<M4Point2> fullCircleSamples = M4CirclePolygon(profile.Center, profile.Radius, M4BoundaryChordToleranceMm);
                double maxProfileSampleDistance = 0.0;
                foreach (M4Point2 sample in fullCircleSamples)
                    maxProfileSampleDistance = Math.Max(maxProfileSampleDistance, M4DistanceToPolyline(sample, loop));
                bool fullCircumferenceCovered = maxProfileSampleDistance <= M4BoundaryChordToleranceMm * 4.0;
                profileMatches = allOnCircle && fullCircumferenceCovered;
                profileEvidence.Add("maximum_loop_point_radial_error_mm", maxLoopPointRadialError);
                profileEvidence.Add("loop_points_match_circle", allOnCircle);
                profileEvidence.Add("profile_sample_count", fullCircleSamples.Count);
                profileEvidence.Add("maximum_profile_sample_distance_to_loop_mm", maxProfileSampleDistance);
                profileEvidence.Add("full_circumference_covered", fullCircumferenceCovered);
            }
            else
            {
                bool loopOnProfile = true;
                for (int i = 0; i < loop.Count - 1 && loopOnProfile; i++)
                    if (M4DistanceToPolyline(loop[i], profile.Points) > M4BoundaryChordToleranceMm * 4.0) loopOnProfile = false;
                bool profileOnLoop = true;
                for (int i = 0; i < profile.Points.Count - 1 && profileOnLoop; i++)
                    if (M4DistanceToPolyline(profile.Points[i], loop) > M4BoundaryChordToleranceMm * 4.0) profileOnLoop = false;
                profileMatches = loopOnProfile && profileOnLoop;
                profileEvidence.Add("loop_points_match_profile", loopOnProfile);
                profileEvidence.Add("profile_points_match_loop", profileOnLoop);
            }
            profileEvidence.Add("matches_complete_loop", profileMatches);
            profileChecks.Add(profileEvidence);
            if (profileMatches) matched = true;
        }
        evidence.Add("matched", matched);
        evidence.Add("profiles", profileChecks);
        return matched;
    }

    private static double M4DistanceToPolyline(M4Point2 point, List<M4Point2> path)
    {
        double distance = Double.PositiveInfinity;
        for (int i = 0; i < path.Count - 1; i++)
            distance = Math.Min(distance, M4PointSegmentDistance(point, path[i], path[i + 1]));
        return distance;
    }

    private static HashSet<string> M4FeatureTags(Part work, List<string> featureIds,
        out Dictionary<string, string> featureJournalIdsByTag)
    {
        HashSet<string> tags = new HashSet<string>(StringComparer.Ordinal);
        featureJournalIdsByTag = new Dictionary<string, string>(StringComparer.Ordinal);
        if (work == null) return tags;
        Feature[] features = work.Features.GetFeatures();
        if (features == null) return tags;
        HashSet<string> requested = new HashSet<string>(featureIds ?? new List<string>(), StringComparer.Ordinal);
        foreach (Feature feature in features)
        {
            if (feature == null) continue;
            string journalId = feature.JournalIdentifier;
            string tag = feature.Tag.ToString();
            featureJournalIdsByTag[tag] = journalId;
            if (requested.Contains(journalId)) tags.Add(tag);
        }
        return tags;
    }

    private static M4Point2 M4ProjectToFrame(M4FaceCandidate frame, double[] point)
    {
        double dx = point[0] - frame.Origin[0];
        double dy = point[1] - frame.Origin[1];
        double dz = point[2] - frame.Origin[2];
        return new M4Point2(dx * frame.XAxis[0] + dy * frame.XAxis[1] + dz * frame.XAxis[2],
            dx * frame.YAxis[0] + dy * frame.YAxis[1] + dz * frame.YAxis[2]);
    }

    private static void RemoveAdjacentDuplicatePoints(List<M4Point2> points, double tolerance)
    {
        for (int i = points.Count - 1; i > 0; i--)
            if (M4PointsNear(points[i], points[i - 1], tolerance)) points.RemoveAt(i);
    }

    private bool ValidateM4SketchProfile(M4FaceCandidate face, List<M3SketchGeometryRequest> geometries,
        double[] offset, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("body_id", face.BodyId);
        evidence.Add("face_journal_id", face.FaceId);
        evidence.Add("boundary_method", "exact linear edges plus circular edge samples from NX UF with chord tolerance 0.001 mm");
        evidence.Add("boundary_chord_tolerance_mm", M4BoundaryChordToleranceMm);
        evidence.Add("domain_clearance_mm", M4DomainClearanceMm);
        evidence.Add("loop_count", face.Loops == null ? 0 : face.Loops.Count);
        evidence.Add("boundary_types", face.BoundaryTypes == null ? new List<object>() : StringListAsObjects(face.BoundaryTypes));
        evidence.Add("own_cut_feature_journal_ids", face.ExcludedCutFeatures ?? new List<object>());
        evidence.Add("boundary_edge_feature_provenance", face.EdgeFeatureProvenance ?? new List<object>());
        evidence.Add("boundary_loop_provenance", face.LoopProvenance ?? new List<object>());
        if (!face.BoundarySupported || face.Loops == null || face.Loops.Count == 0)
        {
            evidence.Add("status", "UNSUPPORTED_BOUNDARY");
            evidence.Add("reason", face.BoundaryReason ?? "support face region is not enumerable");
            throw new M2ModelException("M4_PROFILE_OUTSIDE_FACE", "support face boundary could not be proven using line/circular-edge domain checks");
        }
        List<M4Profile> profiles = new List<M4Profile>();
        List<M4LineSegment> lineSegments = new List<M4LineSegment>();
        foreach (M3SketchGeometryRequest geometry in geometries)
        {
            M4Profile profile = new M4Profile();
            if (geometry.Type == "circle")
            {
                profile.Kind = "circle";
                profile.GeometryIds.Add(geometry.Id);
                profile.Center = new M4Point2(offset[0] + geometry.CenterMm[0], offset[1] + geometry.CenterMm[1]);
                profile.Radius = geometry.DiameterMm / 2.0;
                profile.Closed = true;
                profile.Points = M4CirclePolygon(profile.Center, profile.Radius, M4BoundaryChordToleranceMm);
            }
            else if (geometry.Type == "rectangle")
            {
                profile.Kind = "rectangle";
                profile.GeometryIds.Add(geometry.Id);
                double x = offset[0] + geometry.CornerMm[0];
                double y = offset[1] + geometry.CornerMm[1];
                profile.Closed = true;
                profile.Points = new List<M4Point2> {
                    new M4Point2(x,y), new M4Point2(x+geometry.WidthMm,y),
                    new M4Point2(x+geometry.WidthMm,y+geometry.HeightMm), new M4Point2(x,y+geometry.HeightMm), new M4Point2(x,y)
                };
                profile.Center = new M4Point2(x + geometry.WidthMm / 2.0, y + geometry.HeightMm / 2.0);
            }
            else
            {
                lineSegments.Add(new M4LineSegment {
                    First = new M4Point2(offset[0] + geometry.StartMm[0], offset[1] + geometry.StartMm[1]),
                    Last = new M4Point2(offset[0] + geometry.EndMm[0], offset[1] + geometry.EndMm[1]),
                    Id = geometry.Id
                });
                continue;
            }
            profiles.Add(profile);
        }

        profiles.AddRange(M4BuildLineProfiles(lineSegments));
        bool allInside = M4CheckProfilesInDomain(face, profiles, out evidence);
        if (!allInside)
            throw new M2ModelException("M4_PROFILE_OUTSIDE_FACE", "the sketch profile is outside, crosses, encloses a cutout in, or is too close to the selected face boundary");
        return true;
    }

    private static bool M4CheckProfilesInDomain(M4FaceCandidate face, List<M4Profile> profiles,
        out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("body_id", face.BodyId);
        evidence.Add("face_journal_id", face.FaceId);
        evidence.Add("boundary_method", "exact linear edges plus circular edge samples from NX UF with chord tolerance 0.001 mm");
        evidence.Add("boundary_chord_tolerance_mm", M4BoundaryChordToleranceMm);
        evidence.Add("domain_clearance_mm", M4DomainClearanceMm);
        evidence.Add("loop_count", face.Loops == null ? 0 : face.Loops.Count);
        evidence.Add("boundary_types", face.BoundaryTypes == null ? new List<object>() : StringListAsObjects(face.BoundaryTypes));
        evidence.Add("own_cut_feature_journal_ids", face.ExcludedCutFeatures ?? new List<object>());
        evidence.Add("boundary_edge_feature_provenance", face.EdgeFeatureProvenance ?? new List<object>());
        evidence.Add("boundary_loop_provenance", face.LoopProvenance ?? new List<object>());
        if (!face.BoundarySupported || face.Loops == null || face.Loops.Count == 0)
        {
            evidence.Add("status", "UNSUPPORTED_BOUNDARY");
            evidence.Add("reason", face.BoundaryReason ?? "support face region is not enumerable");
            return false;
        }

        bool allInside = true;
        List<object> geometryEvidence = new List<object>();
        foreach (M4Profile profile in profiles)
        {
            string failure = profile.Kind == "unsupported" ? "closed line profile is not a supported axis-aligned rectangle" : null;
            foreach (M4Point2 point in profile.Points)
            {
                if (failure != null) break;
                if (!M4PointInsideFaceDomain(point, face.Loops)) { failure = "profile boundary leaves the support face region"; break; }
                if (M4DistanceToFaceBoundary(point, face.Loops) <= M4DomainClearanceMm)
                { failure = "profile is within the face-boundary uncertainty margin"; break; }
            }
            if (failure == null && profile.Closed && !M4PointInsideFaceDomain(profile.Center, face.Loops))
                failure = "profile interior lies outside the support face region";
            if (failure == null)
            {
                foreach (M4FaceLoop loop in face.Loops)
                {
                    for (int i = 0; i < loop.Points.Count - 1; i++)
                    {
                        M4Point2 boundaryPoint = loop.Points[i];
                        if (M4PointInsideProfile(boundaryPoint, profile, M4DomainClearanceMm))
                        { failure = "a face cutout or inner loop lies inside the requested profile"; break; }
                        for (int j = 0; j < profile.Points.Count - 1; j++)
                        {
                            if (M4SegmentsWithinTolerance(boundaryPoint, loop.Points[i + 1],
                                profile.Points[j], profile.Points[j + 1], M4DomainClearanceMm))
                            { failure = "profile crosses or approaches a face boundary"; break; }
                        }
                        if (failure != null) break;
                    }
                    if (failure != null) break;
                }
            }
            Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
            item.Add("geometry_ids", StringListAsObjects(profile.GeometryIds));
            item.Add("type", profile.Kind);
            item.Add("inside", failure == null);
            item.Add("reason", failure);
            geometryEvidence.Add(item);
            if (failure != null) allInside = false;
        }
        evidence.Add("geometries", geometryEvidence);
        evidence.Add("status", allInside ? "VERIFIED" : "OUTSIDE_FACE");
        evidence.Add("in_face_domain", allInside);
        return allInside;
    }

    private static List<M4Profile> M4BuildLineProfiles(List<M4LineSegment> segments)
    {
        List<M4Profile> profiles = new List<M4Profile>();
        if (segments == null) return profiles;
        List<M4Point2> vertices = new List<M4Point2>();
        List<int> degree = new List<int>();
        foreach (M4LineSegment segment in segments)
        {
            IncrementM4VertexDegree(segment.First, vertices, degree);
            IncrementM4VertexDegree(segment.Last, vertices, degree);
        }
        for (int i = 0; i < degree.Count; i++)
        {
            if (degree[i] <= 2) continue;
            M4Profile branched = new M4Profile();
            branched.Kind = "unsupported";
            branched.Closed = false;
            branched.GeometryIds = new List<string>();
            branched.Points = new List<M4Point2>();
            foreach (M4LineSegment segment in segments)
            {
                if (!branched.GeometryIds.Contains(segment.Id)) branched.GeometryIds.Add(segment.Id);
                branched.Points.Add(segment.First);
                branched.Points.Add(segment.Last);
            }
            profiles.Add(branched);
            return profiles;
        }
        bool[] used = new bool[segments.Count];
        for (int seed = 0; seed < segments.Count; seed++)
        {
            if (used[seed]) continue;
            List<M4Point2> path = new List<M4Point2> { segments[seed].First, segments[seed].Last };
            List<string> ids = new List<string> { segments[seed].Id };
            used[seed] = true;
            bool closed = false;
            for (int hop = 0; hop < segments.Count; hop++)
            {
                M4Point2 end = path[path.Count - 1];
                int next = -1;
                bool reverse = false;
                for (int candidate = 0; candidate < segments.Count; candidate++)
                {
                    if (used[candidate]) continue;
                    if (M4PointsNear(end, segments[candidate].First, 1e-7)) { next = candidate; break; }
                    if (M4PointsNear(end, segments[candidate].Last, 1e-7)) { next = candidate; reverse = true; break; }
                }
                if (next < 0) break;
                used[next] = true;
                ids.Add(segments[next].Id);
                M4Point2 nextPoint = reverse ? segments[next].First : segments[next].Last;
                path.Add(nextPoint);
                if (M4PointsNear(path[0], path[path.Count - 1], 1e-7))
                {
                    path[path.Count - 1] = path[0];
                    closed = true;
                    break;
                }
            }
            M4Profile profile = new M4Profile();
            profile.GeometryIds = ids;
            profile.Closed = closed;
            if (closed)
            {
                bool rectangle = M4IsAxisAlignedRectangle(path);
                profile.Kind = rectangle ? "polygon" : "unsupported";
                profile.Points = path;
                double area, cx, cy;
                M4PolygonAreaCentroid(path, out area, out cx, out cy);
                profile.Center = new M4Point2(cx, cy);
            }
            else
            {
                profile.Kind = "line";
                profile.Points = path;
            }
            profiles.Add(profile);
        }
        return profiles;
    }

    private static void IncrementM4VertexDegree(M4Point2 point, List<M4Point2> vertices, List<int> degree)
    {
        for (int i = 0; i < vertices.Count; i++)
        {
            if (!M4PointsNear(point, vertices[i], 1e-7)) continue;
            degree[i]++;
            return;
        }
        vertices.Add(point);
        degree.Add(1);
    }

    private static List<M4Profile> M4BuildSketchProfiles(Sketch sketch, M4FaceCandidate face,
        out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("method", "live Sketch curves projected onto the selected face frame");
        NXObject[] geometry;
        try { geometry = sketch.GetAllGeometry(); }
        catch (Exception ex)
        {
            evidence.Add("status", "GEOMETRY_READ_FAILED");
            evidence.Add("reason", ex.GetType().Name);
            return null;
        }
        if (geometry == null || geometry.Length == 0)
        {
            evidence.Add("status", "EMPTY_SKETCH");
            return null;
        }
        List<M4Profile> profiles = new List<M4Profile>();
        List<M4LineSegment> lines = new List<M4LineSegment>();
        foreach (NXObject item in geometry)
        {
            if (item == null)
            {
                evidence.Add("status", "NULL_SKETCH_GEOMETRY");
                return null;
            }
            string id = RequiredJournalId(item, "M4 sketch geometry");
            Arc arc = item as Arc;
            if (arc != null)
            {
                double sweep = Math.Abs(arc.EndAngle - arc.StartAngle);
                if (sweep < (2.0 * Math.PI - 1e-4) || arc.Radius <= 0.0)
                {
                    evidence.Add("status", "UNSUPPORTED_ARC");
                    evidence.Add("geometry_id", id);
                    return null;
                }
                M4Point2 center = M4ProjectToFrame(face, new double[] { arc.CenterPoint.X, arc.CenterPoint.Y, arc.CenterPoint.Z });
                M4Profile circle = new M4Profile();
                circle.Kind = "circle";
                circle.GeometryIds.Add(id);
                circle.Center = center;
                circle.Radius = arc.Radius;
                circle.Closed = true;
                circle.Points = M4CirclePolygon(center, arc.Radius, M4BoundaryChordToleranceMm);
                profiles.Add(circle);
                continue;
            }
            Line line = item as Line;
            if (line != null)
            {
                M4Point2 first = M4ProjectToFrame(face, new double[] { line.StartPoint.X, line.StartPoint.Y, line.StartPoint.Z });
                M4Point2 last = M4ProjectToFrame(face, new double[] { line.EndPoint.X, line.EndPoint.Y, line.EndPoint.Z });
                lines.Add(new M4LineSegment { First = first, Last = last, Id = id });
                continue;
            }
            evidence.Add("status", "UNSUPPORTED_GEOMETRY");
            evidence.Add("geometry_id", id);
            evidence.Add("object_type", item.GetType().Name);
            return null;
        }
        profiles.AddRange(M4BuildLineProfiles(lines));
        if (profiles.Count == 0)
        {
            evidence.Add("status", "EMPTY_PROFILE");
            return null;
        }
        evidence["live_geometry_count"] = geometry.Length;
        return profiles;
    }

    private static bool M4SketchGeometryInsideFace(Sketch sketch, M4FaceCandidate face,
        out Dictionary<string, object> evidence)
    {
        List<M4Profile> profiles = M4BuildSketchProfiles(sketch, face, out evidence);
        if (profiles == null) return false;
        bool inside = M4CheckProfilesInDomain(face, profiles, out evidence);
        return inside;
    }

    private static bool M4IsAxisAlignedRectangle(List<M4Point2> path)
    {
        if (path == null || path.Count != 5 || !M4PointsNear(path[0], path[4], 1e-7)) return false;
        double minX = Double.PositiveInfinity, maxX = Double.NegativeInfinity;
        double minY = Double.PositiveInfinity, maxY = Double.NegativeInfinity;
        for (int i = 0; i < 4; i++)
        {
            minX = Math.Min(minX, path[i].X); maxX = Math.Max(maxX, path[i].X);
            minY = Math.Min(minY, path[i].Y); maxY = Math.Max(maxY, path[i].Y);
        }
        if (maxX - minX <= 1e-9 || maxY - minY <= 1e-9) return false;
        for (int i = 0; i < 4; i++)
        {
            bool horizontal = Math.Abs(path[i].Y - path[i + 1].Y) <= 1e-7 && Math.Abs(path[i].X - path[i + 1].X) > 1e-7;
            bool vertical = Math.Abs(path[i].X - path[i + 1].X) <= 1e-7 && Math.Abs(path[i].Y - path[i + 1].Y) > 1e-7;
            if (!horizontal && !vertical) return false;
        }
        return true;
    }

    private static List<M4Point2> M4CirclePolygon(M4Point2 center, double radius, double chordTolerance)
    {
        double maxAngle = radius <= chordTolerance ? Math.PI / 8.0 : 2.0 * Math.Acos(Math.Max(-1.0, Math.Min(1.0, 1.0 - chordTolerance / radius)));
        if (Double.IsNaN(maxAngle) || maxAngle <= 1e-6) maxAngle = Math.PI / 2048.0;
        int count = (int)Math.Ceiling(2.0 * Math.PI / maxAngle);
        if (count < 32) count = 32;
        if (count > 16384) throw new M2ModelException("M4_PROFILE_OUTSIDE_FACE", "circle is too large to prove inside the face within the configured chord tolerance");
        List<M4Point2> points = new List<M4Point2>();
        for (int i = 0; i <= count; i++)
        {
            double angle = 2.0 * Math.PI * i / count;
            points.Add(new M4Point2(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)));
        }
        return points;
    }

    private static bool M4PointInsideFaceDomain(M4Point2 point, List<M4FaceLoop> loops)
    {
        bool inside = false;
        foreach (M4FaceLoop loop in loops)
            if (M4PointInPolygon(point, loop.Points)) inside = !inside;
        return inside;
    }

    private static bool M4PointInPolygon(M4Point2 point, List<M4Point2> polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            M4Point2 a = polygon[i], b = polygon[j];
            if (((a.Y > point.Y) != (b.Y > point.Y)) &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static bool M4PointInsideProfile(M4Point2 point, M4Profile profile, double margin)
    {
        if (profile.Kind == "circle")
        {
            double dx = point.X - profile.Center.X, dy = point.Y - profile.Center.Y;
            return Math.Sqrt(dx * dx + dy * dy) < profile.Radius - margin;
        }
        if (profile.Kind == "rectangle" || profile.Kind == "polygon") return M4PointInPolygon(point, profile.Points);
        return false;
    }

    private static double M4DistanceToFaceBoundary(M4Point2 point, List<M4FaceLoop> loops)
    {
        double min = Double.PositiveInfinity;
        foreach (M4FaceLoop loop in loops)
            for (int i = 0; i < loop.Points.Count - 1; i++)
                min = Math.Min(min, M4PointSegmentDistance(point, loop.Points[i], loop.Points[i + 1]));
        return min;
    }

    private static bool M4SegmentsWithinTolerance(M4Point2 a, M4Point2 b, M4Point2 c, M4Point2 d, double tolerance)
    {
        if (M4SegmentsIntersect(a, b, c, d)) return true;
        return Math.Min(Math.Min(M4PointSegmentDistance(a, c, d), M4PointSegmentDistance(b, c, d)),
            Math.Min(M4PointSegmentDistance(c, a, b), M4PointSegmentDistance(d, a, b))) <= tolerance;
    }

    private static bool M4SegmentsIntersect(M4Point2 a, M4Point2 b, M4Point2 c, M4Point2 d)
    {
        double abC = M4Cross2(b.X - a.X, b.Y - a.Y, c.X - a.X, c.Y - a.Y);
        double abD = M4Cross2(b.X - a.X, b.Y - a.Y, d.X - a.X, d.Y - a.Y);
        double cdA = M4Cross2(d.X - c.X, d.Y - c.Y, a.X - c.X, a.Y - c.Y);
        double cdB = M4Cross2(d.X - c.X, d.Y - c.Y, b.X - c.X, b.Y - c.Y);
        const double eps = 1e-10;
        return ((abC > eps && abD < -eps) || (abC < -eps && abD > eps)) &&
            ((cdA > eps && cdB < -eps) || (cdA < -eps && cdB > eps));
    }

    private static double M4Cross2(double ax, double ay, double bx, double by) { return ax * by - ay * bx; }

    private static double M4PointSegmentDistance(M4Point2 point, M4Point2 first, M4Point2 last)
    {
        double dx = last.X - first.X, dy = last.Y - first.Y;
        double length2 = dx * dx + dy * dy;
        if (length2 <= 1e-24)
        {
            double px = point.X - first.X, py = point.Y - first.Y;
            return Math.Sqrt(px * px + py * py);
        }
        double t = ((point.X - first.X) * dx + (point.Y - first.Y) * dy) / length2;
        t = Math.Max(0.0, Math.Min(1.0, t));
        double ex = point.X - (first.X + t * dx), ey = point.Y - (first.Y + t * dy);
        return Math.Sqrt(ex * ex + ey * ey);
    }

    private static bool M4PointsNear(M4Point2 left, M4Point2 right, double tolerance)
    {
        double dx = left.X - right.X, dy = left.Y - right.Y;
        return dx * dx + dy * dy <= tolerance * tolerance;
    }

    private static double[] M4OffsetFrameOrigin(M4FaceCandidate face, double[] offset)
    {
        return new double[] {
            face.Origin[0] + offset[0] * face.XAxis[0] + offset[1] * face.YAxis[0],
            face.Origin[1] + offset[0] * face.XAxis[1] + offset[1] * face.YAxis[1],
            face.Origin[2] + offset[0] * face.XAxis[2] + offset[1] * face.YAxis[2]
        };
    }

    private static double Length3(double[] value) { return Math.Sqrt(value[0] * value[0] + value[1] * value[1] + value[2] * value[2]); }
    private static double Dot3(double[] left, double[] right) { return left[0] * right[0] + left[1] * right[1] + left[2] * right[2]; }
    private static void Normalize3(double[] value)
    {
        double length = Length3(value);
        if (length <= 1e-12) throw new M2ModelException("M4_FACE_UNSUPPORTED", "face frame contains a zero direction");
        value[0] /= length; value[1] /= length; value[2] /= length;
    }
    private static double[] Cross3(double[] left, double[] right)
    {
        return new double[] { left[1] * right[2] - left[2] * right[1],
            left[2] * right[0] - left[0] * right[2], left[0] * right[1] - left[1] * right[0] };
    }
    private static Vector3d Cross(Vector3d left, Vector3d right)
    {
        return new Vector3d(left.Y * right.Z - left.Z * right.Y,
            left.Z * right.X - left.X * right.Z, left.X * right.Y - left.Y * right.X);
    }
    private static Point3d ToPoint3d(double[] point) { return new Point3d(point[0], point[1], point[2]); }
    private static Vector3d ToVector3d(double[] vector) { return new Vector3d(vector[0], vector[1], vector[2]); }

    private static void M4PolygonAreaCentroid(List<M4Point2> points, out double area, out double cx, out double cy)
    {
        double twiceArea = 0.0, x = 0.0, y = 0.0;
        for (int i = 0; i < points.Count - 1; i++)
        {
            double cross = points[i].X * points[i + 1].Y - points[i + 1].X * points[i].Y;
            twiceArea += cross;
            x += (points[i].X + points[i + 1].X) * cross;
            y += (points[i].Y + points[i + 1].Y) * cross;
        }
        area = twiceArea / 2.0;
        if (Math.Abs(twiceArea) <= 1e-12) { cx = 0.0; cy = 0.0; }
        else { cx = x / (3.0 * twiceArea); cy = y / (3.0 * twiceArea); }
    }

    private static string ReadM4StringAttribute(NXObject target, string title)
    {
        if (target == null) return null;
        try { return target.GetStringUserAttribute(title, 0); }
        catch { return null; }
    }

    private static void PersistM4SupportMetadata(Sketch sketch, string selector, string bodyId, bool domainValidated)
    {
        if (sketch == null || sketch.Feature == null)
            throw new M2ModelException("M4_FACE_ASSOCIATION_INVALID", "on-face Sketch has no feature for persisted support metadata");
        NXObject target = sketch.Feature;
        target.SetUserAttribute(M4SelectorAttribute, 0, selector, Update.Option.Now);
        target.SetUserAttribute(M4BodyAttribute, 0, bodyId, Update.Option.Now);
        target.SetUserAttribute(M4DomainAttribute, 0, domainValidated ? "true" : "false", Update.Option.Now);
        target.SetUserAttribute(M4BoundaryMethodAttribute, 0,
            "linear-exact+circular-chord-0.001mm-clearance-0.002mm-v1", Update.Option.Now);
    }

    private static List<string> ReadM4CutFeatureIds(Sketch sketch)
    {
        List<string> ids = new List<string>();
        string text = ReadM4StringAttribute(sketch == null ? null : sketch.Feature, M4CutFeatureAttribute);
        if (String.IsNullOrWhiteSpace(text)) return ids;
        foreach (string value in text.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            if (!ids.Contains(value)) ids.Add(value);
        return ids;
    }

    private static void PersistM4CutFeatureIds(Sketch sketch, List<string> featureIds)
    {
        if (sketch == null || sketch.Feature == null) return;
        List<string> ids = featureIds == null ? new List<string>() : new List<string>(featureIds);
        ids.Sort(StringComparer.Ordinal);
        sketch.Feature.SetUserAttribute(M4CutFeatureAttribute, 0, String.Join("|", ids.ToArray()), Update.Option.Now);
    }

    private static Plane FindM4AttachedPlane(Part work, Sketch sketch)
    {
        // Rebuild the live sketch builder to read its actual PlaneReference.
        // This is direct dependency evidence; coincident origin/normal alone is
        // insufficient because unrelated planes can occupy the same location.
        SketchInPlaceBuilder attachedBuilder = null;
        try
        {
            attachedBuilder = work.Sketches.CreateSketchInPlaceBuilder2(sketch);
            Plane referenced = attachedBuilder.PlaneReference;
            if (referenced != null) return referenced;
        }
        catch { }
        finally
        {
            if (attachedBuilder != null) try { attachedBuilder.Destroy(); } catch { }
        }

        NXObject attached = null;
        try { attached = (object)sketch.AttachPlane as NXObject; }
        catch { attached = null; }
        string attachedId = null;
        Tag attachedTag = Tag.Null;
        if (attached != null)
        {
            try { attachedId = attached.JournalIdentifier; } catch { }
            try { attachedTag = attached.Tag; } catch { }
        }
        Plane[] planes = work.Planes.ToArray();
        if (planes == null) return null;
        foreach (Plane plane in planes)
        {
            if (plane == null) continue;
            try
            {
                if (!String.IsNullOrEmpty(attachedId) && String.Equals(plane.JournalIdentifier, attachedId, StringComparison.Ordinal))
                    return plane;
                if (attachedTag != Tag.Null && plane.Tag.Equals(attachedTag)) return plane;
            }
            catch { }
        }
        return null;
    }

    private static bool ReadM4NativeSupport(Part work, Sketch sketch, Plane plane, string expectedBodyId,
        out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        bool native = false;
        string geometryKind = null;
        string actualBodyId = null;
        string faceId = null;
        Face face = null;
        try
        {
            if (plane == null) plane = FindM4AttachedPlane(work, sketch);
            if (plane != null)
            {
                evidence.Add("plane_method", plane.Method.ToString());
                evidence.Add("plane_journal_id", plane.JournalIdentifier);
                evidence.Add("plane_origin_mm", Point(plane.Origin));
                evidence.Add("plane_normal_world", Vector(plane.Normal.X, plane.Normal.Y, plane.Normal.Z));
                NXObject[] geometry = plane.GetGeometry();
                evidence.Add("face_geometry_count", geometry == null ? 0 : geometry.Length);
                if (geometry != null && geometry.Length == 1)
                {
                    face = geometry[0] as Face;
                    if (face != null)
                    {
                        geometryKind = "Face";
                        faceId = RequiredJournalId(face, "M4 support face");
                        Body body = face.GetBody();
                        actualBodyId = RequiredJournalId(body, "M4 support body");
                        native = plane.Method == PlaneTypes.MethodType.Coincident;
                        if (!String.IsNullOrEmpty(expectedBodyId) && !String.Equals(expectedBodyId, actualBodyId, StringComparison.Ordinal))
                            native = false;
                        int type;
                        double[] point = new double[3], direction = new double[3], box = new double[6];
                        double radius, radiusData;
                        int normalDirection;
                        UFSession.GetUFSession().Modl.AskFaceData(face.Tag, out type, point, direction,
                            box, out radius, out radiusData, out normalDirection);
                        double length = Length3(direction);
                        double dot = length <= 1e-12 ? 0.0 :
                            Dot3(new double[] { direction[0] / length,
                                direction[1] / length, direction[2] / length },
                            new double[] { plane.Normal.X, plane.Normal.Y, plane.Normal.Z });
                        evidence.Add("face_type", type);
                        evidence.Add("face_normal_alignment", dot);
                        if (type != UFConstants.UF_MODL_PLANAR_FACE || dot < M4FaceNormalDotTolerance) native = false;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            evidence["readback_error"] = ex.GetType().Name;
            native = false;
        }
        evidence["native"] = native;
        evidence["geometry_kind"] = geometryKind;
        evidence["support_face_id"] = faceId;
        evidence["support_face_tag"] = face == null ? null : face.Tag.ToString();
        evidence["body_id"] = actualBodyId;
        evidence["expected_body_id"] = expectedBodyId;
        evidence["current_face_matches"] = false;
        return native;
    }

    private bool TryReadM4SketchSupport(Part work, Sketch sketch,
        out Dictionary<string, object> association, out Dictionary<string, object> frame)
    {
        association = null;
        frame = null;
        string selector = ReadM4StringAttribute(sketch.Feature, M4SelectorAttribute);
        string bodyId = ReadM4StringAttribute(sketch.Feature, M4BodyAttribute);
        string domain = ReadM4StringAttribute(sketch.Feature, M4DomainAttribute);
        if (String.IsNullOrWhiteSpace(selector) || String.IsNullOrWhiteSpace(bodyId)) return false;

        Plane plane = FindM4AttachedPlane(work, sketch);
        Dictionary<string, object> nativeEvidence;
        bool native = ReadM4NativeSupport(work, sketch, plane, bodyId, out nativeEvidence);
        bool selectorUnique = false;
        bool currentFaceMatches = false;
        M4FaceCandidate selected = null;
        string selectionError = null;
        try
        {
            List<string> ownCutFeatures = ReadM4CutFeatureIds(sketch);
            selected = ResolveM4UniqueFace(work, selector, bodyId, ownCutFeatures, sketch);
            selectorUnique = true;
            string supportFaceTag = Convert.ToString(nativeEvidence["support_face_tag"], CultureInfo.InvariantCulture);
            currentFaceMatches = String.Equals(selected.Face.Tag.ToString(), supportFaceTag, StringComparison.Ordinal);
        }
        catch (M2ModelException ex) { selectionError = ex.Code + ": " + ex.Message; }
        Dictionary<string, object> profileDomainEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        bool profileInDomain = selected != null && M4SketchGeometryInsideFace(sketch, selected, out profileDomainEvidence);
        if (selected == null) profileDomainEvidence["status"] = "UNRESOLVED_SUPPORT_FACE";

        Point3d origin = sketch.Origin;
        Matrix3x3 axes = sketch.Orientation.Element;
        double[] frameOrigin = new double[] { origin.X, origin.Y, origin.Z };
        double[] xAxis = new double[] { axes.Xx, axes.Xy, axes.Xz };
        double[] yAxis = new double[] { axes.Yx, axes.Yy, axes.Yz };
        double[] normal = new double[] { axes.Zx, axes.Zy, axes.Zz };
        frame = M4FrameRecord(frameOrigin, xAxis, yAxis, normal);

        association = new Dictionary<string, object>(StringComparer.Ordinal);
        association.Add("native", native);
        association.Add("geometry_kind", nativeEvidence["geometry_kind"] ?? "unknown");
        association.Add("plane_method", nativeEvidence.ContainsKey("plane_method") ? nativeEvidence["plane_method"] : null);
        association.Add("support_face_id", nativeEvidence["support_face_id"]);
        association.Add("body_id", bodyId);
        association.Add("selector", selector);
        association.Add("selector_unique", selectorUnique);
        association.Add("current_face_matches", currentFaceMatches);
        association.Add("in_face_domain", String.Equals(domain, "true", StringComparison.OrdinalIgnoreCase) && profileInDomain);
        association.Add("profile_domain_readback", profileDomainEvidence);
        association.Add("domain_method", ReadM4StringAttribute(sketch.Feature, M4BoundaryMethodAttribute));
        association.Add("selection_error", selectionError);
        if (selected != null)
        {
            association.Add("face_area_mm2", selected.Area);
            association.Add("loop_count", selected.Loops == null ? 0 : selected.Loops.Count);
            association.Add("boundary_types", StringListAsObjects(selected.BoundaryTypes));
            association.Add("boundary_chord_tolerance_mm", M4BoundaryChordToleranceMm);
        }
        association.Add("native_readback", nativeEvidence);
        return true;
    }

    private static void RefreshM4SupportTracksFromPart(Part work, BindingState state)
    {
        Sketch[] sketches = work.Sketches.ToArray();
        if (sketches == null) return;
        foreach (Sketch sketch in sketches)
        {
            if (sketch == null) continue;
            string selector = ReadM4StringAttribute(sketch.Feature, M4SelectorAttribute);
            string bodyId = ReadM4StringAttribute(sketch.Feature, M4BodyAttribute);
            if (String.IsNullOrWhiteSpace(selector) || String.IsNullOrWhiteSpace(bodyId)) continue;
            Plane plane = FindM4AttachedPlane(work, sketch);
            Dictionary<string, object> evidence;
            bool native = ReadM4NativeSupport(work, sketch, plane, bodyId, out evidence);
            string sketchId = RequiredJournalId(sketch, "M4 on-face sketch");
            M4SupportTrack track;
            if (!state.M4SupportTracks.TryGetValue(sketchId, out track))
            {
                track = new M4SupportTrack();
                track.SketchId = sketchId;
                state.M4SupportTracks.Add(sketchId, track);
            }
            track.BodyId = bodyId;
            track.Selector = selector;
            if (String.IsNullOrEmpty(track.FaceId) && evidence["support_face_tag"] != null)
                track.FaceId = Convert.ToString(evidence["support_face_tag"], CultureInfo.InvariantCulture);
            track.FeatureId = sketch.Feature == null ? null : RequiredJournalId(sketch.Feature, "M4 sketch feature");
            track.Plane = plane;
            track.DomainValidated = String.Equals(ReadM4StringAttribute(sketch.Feature, M4DomainAttribute), "true", StringComparison.OrdinalIgnoreCase);
            track.Native = native;
            track.GeometryKind = Convert.ToString(evidence["geometry_kind"], CultureInfo.InvariantCulture);
            track.CutFeatureIds = ReadM4CutFeatureIds(sketch);
            Point3d origin = sketch.Origin;
            track.SupportOrigin = new double[] { origin.X, origin.Y, origin.Z };
        }
    }

    private bool ValidateM4SupportTracks(BindingState state, out List<object> records, HashSet<string> refreshFaceIds)
    {
        records = new List<object>();
        RefreshM4SupportTracksFromPart(state.WorkPart, state);
        bool allValid = true;
        foreach (M4SupportTrack track in state.M4SupportTracks.Values)
        {
            Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
            item.Add("sketch_id", track.SketchId);
            item.Add("body_id", track.BodyId);
            item.Add("selector", track.Selector);
            Sketch sketch = FindSketch(state.WorkPart, track.SketchId);
            Plane plane = FindM4AttachedPlane(state.WorkPart, sketch);
            Dictionary<string, object> nativeEvidence;
            bool native = ReadM4NativeSupport(state.WorkPart, sketch, plane, track.BodyId, out nativeEvidence);
            bool unique = false, matches = false, topologyMatches = false;
            string error = null;
            M4FaceCandidate selected = null;
            try
            {
                selected = ResolveM4UniqueFace(state.WorkPart, track.Selector, track.BodyId, track.CutFeatureIds, sketch);
                unique = true;
                string planeFaceTag = Convert.ToString(nativeEvidence["support_face_tag"], CultureInfo.InvariantCulture);
                string selectedFaceTag = selected.Face.Tag.ToString();
                matches = String.Equals(selectedFaceTag, planeFaceTag, StringComparison.Ordinal);
                topologyMatches = String.Equals(track.FaceId, selectedFaceTag, StringComparison.Ordinal);
            }
            catch (M2ModelException ex) { error = ex.Code + ": " + ex.Message; }
            Dictionary<string, object> profileDomainEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
            bool profileInDomain = selected != null && M4SketchGeometryInsideFace(sketch, selected, out profileDomainEvidence);
            bool domainValidated = track.DomainValidated && String.Equals(
                ReadM4StringAttribute(sketch.Feature, M4DomainAttribute), "true", StringComparison.OrdinalIgnoreCase) && profileInDomain;
            bool refreshThis = refreshFaceIds != null && refreshFaceIds.Contains(track.SketchId);
            bool valid = native && unique && matches && domainValidated && (refreshThis || topologyMatches);
            item.Add("native", native);
            item.Add("geometry_kind", nativeEvidence["geometry_kind"] ?? "unknown");
            item.Add("native_support_face_journal_id", nativeEvidence["support_face_id"]);
            item.Add("native_support_face_tag", nativeEvidence["support_face_tag"]);
            item.Add("selector_face_journal_id", selected == null ? null : selected.FaceId);
            item.Add("selector_face_tag", selected == null || selected.Face == null ? null : selected.Face.Tag.ToString());
            item.Add("selector_unique", unique);
            item.Add("current_face_matches", matches);
            item.Add("topology_matches", topologyMatches);
            item.Add("in_face_domain", domainValidated);
            item.Add("support_frame", M4SketchFrameRecord(sketch));
            item.Add("profile_domain_readback", profileDomainEvidence);
            item.Add("native_readback", nativeEvidence);
            item.Add("selection_error", error);
            item.Add("status", valid ? "VERIFIED" : "MISMATCH");
            records.Add(item);
            if (!valid) allValid = false;
            if (valid && refreshThis)
            {
                track.FaceId = selected.Face.Tag.ToString();
                track.Plane = plane;
            }
        }
        return allValid;
    }

    private static List<object> M4SupportSnapshot(BindingState state)
    {
        List<string> ids = new List<string>(state.M4SupportTracks.Keys);
        ids.Sort(StringComparer.Ordinal);
        List<object> result = new List<object>();
        foreach (string id in ids)
        {
            M4SupportTrack track = state.M4SupportTracks[id];
            Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
            item.Add("sketch_id", track.SketchId);
            item.Add("body_id", track.BodyId);
            item.Add("selector", track.Selector);
            item.Add("support_face_tag", track.FaceId);
            Sketch sketch = FindSketch(state.WorkPart, track.SketchId);
            item.Add("support_frame", M4SketchFrameRecord(sketch));
            result.Add(item);
        }
        return result;
    }

    private static void RequireM4SubtractContourEditSupported(Part work, BindingState state, string editedSketchId)
    {
        if (state.M4SupportTracks.Count == 0) return;
        Sketch editedSketch = FindSketch(work, editedSketchId);
        if (editedSketch.Feature == null) return;
        HashSet<string> supportBodies = new HashSet<string>(StringComparer.Ordinal);
        foreach (M4SupportTrack track in state.M4SupportTracks.Values) supportBodies.Add(track.BodyId);
        Feature[] features = work.Features.GetFeatures();
        if (features == null) throw new M2ModelException("MODEL_READ_FAILED", "feature history returned null while checking M4 contour edits");
        foreach (Feature feature in features)
        {
            if (feature == null || !String.Equals(feature.FeatureType, "EXTRUDE", StringComparison.Ordinal)) continue;
            ExtrudeBuilder builder = null;
            try
            {
                builder = work.Features.CreateExtrudeBuilder(feature);
                if (builder.BooleanOperation == null || builder.BooleanOperation.Type != NXOpen.GeometricUtilities.BooleanOperation.BooleanType.Subtract)
                    continue;
                Body[] targets = builder.BooleanOperation.GetTargetBodies();
                if (targets == null || targets.Length == 0) continue;
                bool sameSupportBody = false;
                foreach (Body target in targets)
                    if (target != null && supportBodies.Contains(RequiredJournalId(target, "M4 subtract target body"))) sameSupportBody = true;
                if (!sameSupportBody) continue;
                Feature[] parents = feature.GetParents();
                if (parents == null) throw new M2ModelException("M4_SUBTRACT_CONTOUR_EDIT_UNSUPPORTED", "an existing support-body Subtract has unreadable sketch parents");
                foreach (Feature parent in parents)
                    if (parent != null && parent.Tag.Equals(editedSketch.Feature.Tag))
                        throw new M2ModelException("M4_SUBTRACT_CONTOUR_EDIT_UNSUPPORTED",
                            "M4 permits upstream create/unite support changes, but refuses edits to an existing Subtract profile on the supported body because merged face-domain changes are not verified");
            }
            catch (M2ModelException) { throw; }
            catch (Exception ex)
            {
                throw new M2ModelException("M4_SUBTRACT_CONTOUR_EDIT_UNSUPPORTED",
                    "M4 cannot safely inspect an existing support-body Subtract contour (" + ex.GetType().Name + ")");
            }
            finally
            {
                if (builder != null) try { builder.Destroy(); } catch { }
            }
        }
    }

    private static Dictionary<string, object> M4SketchFrameRecord(Sketch sketch)
    {
        Point3d origin = sketch.Origin;
        Matrix3x3 axes = sketch.Orientation.Element;
        return M4FrameRecord(new double[] { origin.X, origin.Y, origin.Z },
            new double[] { axes.Xx, axes.Xy, axes.Xz }, new double[] { axes.Yx, axes.Yy, axes.Yz },
            new double[] { axes.Zx, axes.Zy, axes.Zz });
    }

    private List<object> BuildSketches(Part work)
    {
        Sketch[] sketches = work.Sketches.ToArray();
        if (sketches == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketches.ToArray returned null");
        List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();
        foreach (Sketch sketch in sketches)
        {
            if (sketch == null) throw new M2ModelException("UNSUPPORTED_MODEL", "sketch collection contains null");
            records.Add(BuildSketchDetail(work, sketch, work.PartUnits.ToString()));
        }
        SortRecords(records, "journal_id", "sketch");
        return ToObjectList(records);
    }

    private Dictionary<string, object> BuildSketchDetail(Part work, Sketch sketch, string units)
    {
        string id = RequiredJournalId(sketch, "sketch");
        int degreesOfFreedom;
        Sketch.Status status = sketch.GetStatus(out degreesOfFreedom);
        Point3d origin = sketch.Origin;
        Matrix3x3 axes = sketch.Orientation.Element;
        NXObject[] geometry = sketch.GetAllGeometry();
        if (geometry == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch.GetAllGeometry returned null for " + id);

        Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
        item.Add("journal_id", id);
        item.Add("feature_journal_id", sketch.Feature == null ? null : RequiredJournalId(sketch.Feature, "sketch feature"));
        item.Add("status", status.ToString());
        item.Add("degrees_of_freedom", degreesOfFreedom.ToString(CultureInfo.InvariantCulture));
        item.Add("units", units);
        item.Add("origin", Point(origin));
        item.Add("normal", Vector(axes.Zx, axes.Zy, axes.Zz));
        item.Add("x_axis", Vector(axes.Xx, axes.Xy, axes.Xz));
        item.Add("y_axis", Vector(axes.Yx, axes.Yy, axes.Yz));
        item.Add("geometry", BuildSketchGeometry(geometry, units));
        item.Add("constraints", BuildSketchConstraints(sketch));
        bool hasNativeFeature = sketch.Feature != null && !sketch.Feature.IsOccurrence &&
            sketch.Feature.OwningPart != null && sketch.Feature.OwningPart.Tag == work.Tag;
        item.Add("history_support", SupportStatus(hasNativeFeature ? "NATIVE_FEATURE_HISTORY" : "APPEND_ONLY_NO_NATIVE_FEATURE",
            hasNativeFeature
                ? "This Sketch has an NX feature; only a verified Driving dimension with a supported M2 geometry signature is editable."
                : "This Sketch has no native feature history in the Work Part and is not editable; supported operations may append new features."));
        item.Add("history_feature_available", hasNativeFeature);
        Dictionary<string, object> supportAssociation;
        Dictionary<string, object> supportFrame;
        if (TryReadM4SketchSupport(work, sketch, out supportAssociation, out supportFrame))
        {
            item.Add("support_association", supportAssociation);
            item.Add("support_frame", supportFrame);
        }
        item.Add("support", SupportStatus("SUPPORTED_METADATA", "Sketch frame, degrees of freedom, geometry identities and enumerated constraints were read."));
        return item;
    }

    private static List<object> BuildSketchGeometry(NXObject[] geometry, string units)
    {
        List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();
        foreach (NXObject obj in geometry)
        {
            if (obj == null) throw new M2ModelException("UNSUPPORTED_MODEL", "sketch geometry contains null");
            string id = RequiredJournalId(obj, "sketch geometry");
            Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
            item.Add("journal_id", id);
            item.Add("object_type", obj.GetType().Name);
            item.Add("units", units);
            Line line = obj as Line;
            if (line != null)
            {
                item.Add("detail_status", "SUPPORTED");
                item.Add("kind", "LINE");
                item.Add("start", Point(line.StartPoint));
                item.Add("end", Point(line.EndPoint));
            }
            else if (obj is Arc)
            {
                Arc arc = (Arc)obj;
                Vector3d arcX, arcY;
                Point3d arcCenter;
                arc.GetOrientation(out arcCenter, out arcX, out arcY);
                Vector3d arcNormal = Cross(arcX, arcY);
                item.Add("detail_status", "SUPPORTED");
                item.Add("kind", "ARC");
                item.Add("center", Point(arcCenter));
                item.Add("radius_mm", arc.Radius);
                item.Add("start_angle_rad", arc.StartAngle);
                item.Add("end_angle_rad", arc.EndAngle);
                item.Add("normal", Vector(arcNormal.X, arcNormal.Y, arcNormal.Z));
            }
            else
            {
                item.Add("detail_status", "UNSUPPORTED");
                item.Add("reason", "Geometry identity is available, but detailed parameters for " + obj.GetType().Name + " are not implemented.");
            }
            records.Add(item);
        }
        SortRecords(records, "journal_id", "sketch geometry");
        return ToObjectList(records);
    }

    private static List<object> BuildSketchConstraints(Sketch sketch)
    {
        List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();
        foreach (Sketch.ConstraintType type in Enum.GetValues(typeof(Sketch.ConstraintType)))
        {
            if (type == Sketch.ConstraintType.NoCon || type == Sketch.ConstraintType.LastConType) continue;
            SketchConstraint[] constraints = sketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Any, type);
            if (constraints == null)
                throw new M2ModelException("MODEL_READ_FAILED", "constraint enumeration returned null for " + type);
            foreach (SketchConstraint constraint in constraints)
            {
                if (constraint == null) throw new M2ModelException("UNSUPPORTED_MODEL", "sketch constraint collection contains null");
                Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
                item.Add("constraint_type", type.ToString());
                SketchDimensionalConstraint dimension = constraint as SketchDimensionalConstraint;
                SketchGeometricConstraint geometric = constraint as SketchGeometricConstraint;
                if (dimension != null)
                {
                    Sketch.DimensionGeometry[] operands = dimension.GetDimensionGeometry();
                    if (operands == null) throw new M2ModelException("MODEL_READ_FAILED", "dimension geometry returned null for " + type);
                    List<string> references = new List<string>();
                    foreach (Sketch.DimensionGeometry operand in operands) references.Add(DimensionReference(operand));
                    references.Sort(StringComparer.Ordinal);
                    Expression associated = dimension.AssociatedExpression;
                    bool driving = dimension.DimensionState == SketchDimensionalConstraint.DimensionStateType.Driving;
                    string expressionName = associated == null ? null : associated.Name;
                    string dimensionValue = associated == null ? null : NumberText(associated.Value);
                    item.Add("class", "DIMENSIONAL");
                    item.Add("geometry_refs", ObjectStrings(references));
                    item.Add("is_driving", driving);
                    item.Add("dimension_state", dimension.DimensionState.ToString());
                    item.Add("expression_name", expressionName);
                    item.Add("value", dimensionValue);
                    item.Add("journal_id", RequiredJournalId(constraint, "sketch constraint"));
                    item.Add("semantic_key", type.ToString() + "|" + JoinStrings(item["geometry_refs"] as List<object>) +
                        "|" + (expressionName ?? String.Empty) + "|" + (dimensionValue ?? String.Empty));
                }
                else if (geometric != null)
                {
                    Sketch.ConstraintGeometry[] operands = geometric.GetGeometry();
                    if (operands == null) throw new M2ModelException("MODEL_READ_FAILED", "geometric constraint operands returned null for " + type);
                    List<string> references = new List<string>();
                    foreach (Sketch.ConstraintGeometry operand in operands) references.Add(GeometryReference(operand));
                    references.Sort(StringComparer.Ordinal);
                    item.Add("class", "GEOMETRIC");
                    item.Add("geometry_refs", ObjectStrings(references));
                    item.Add("is_driving", false);
                    item.Add("expression_name", null);
                    item.Add("value", null);
                    item.Add("journal_id", RequiredJournalId(constraint, "sketch constraint"));
                    item.Add("semantic_key", type.ToString() + "|" + JoinStrings(item["geometry_refs"] as List<object>));
                }
                else
                {
                    throw new M2ModelException("UNSUPPORTED_MODEL", "unsupported sketch constraint class " + constraint.GetType().Name + " for " + type);
                }
                records.Add(item);
            }
        }
        SortRecords(records, "journal_id", "sketch constraint");
        return ToObjectList(records);
    }

    private static List<object> BuildExpressions(Part work)
    {
        Expression[] expressions = work.Expressions.ToArray();
        if (expressions == null) throw new M2ModelException("MODEL_READ_FAILED", "Expressions.ToArray returned null");
        List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();
        foreach (Expression expression in expressions)
        {
            if (expression == null) throw new M2ModelException("UNSUPPORTED_MODEL", "expression collection contains null");
            string name = expression.Name;
            if (String.IsNullOrEmpty(name)) throw new M2ModelException("UNSUPPORTED_MODEL", "expression has no name");
            Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
            item.Add("name", name.Normalize(System.Text.NormalizationForm.FormC));
            item.Add("right_hand_side", expression.RightHandSide);
            item.Add("status", expression.Status.ToString());
            item.Add("value", NumberText(expression.Value));
            item.Add("unit", expression.Units == null ? null : expression.Units.Symbol);
            item.Add("journal_id", expression.JournalIdentifier);
            records.Add(item);
        }
        SortRecords(records, "name", "expression");
        return ToObjectList(records);
    }

    private static List<object> BuildDependencies(Part work)
    {
        Feature[] features = work.Features.GetFeatures();
        if (features == null) throw new M2ModelException("MODEL_READ_FAILED", "feature dependency enumeration returned null");
        List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();
        foreach (Feature feature in features)
        {
            string child = RequiredJournalId(feature, "dependency feature");
            Feature[] parents = feature.GetParents();
            Expression[] expressions = feature.GetExpressions();
            if (parents == null || expressions == null) throw new M2ModelException("MODEL_READ_FAILED", "dependency enumeration returned null for " + child);
            foreach (Feature parent in parents)
            {
                Dictionary<string, object> edge = new Dictionary<string, object>(StringComparer.Ordinal);
                edge.Add("kind", "FEATURE_PARENT");
                edge.Add("source_id", RequiredJournalId(parent, "parent feature"));
                edge.Add("target_id", child);
                records.Add(edge);
            }
            foreach (Expression expression in expressions)
            {
                Dictionary<string, object> edge = new Dictionary<string, object>(StringComparer.Ordinal);
                edge.Add("kind", "FEATURE_EXPRESSION");
                edge.Add("source_id", child);
                edge.Add("target_id", expression == null ? null : expression.Name);
                if (expression == null) throw new M2ModelException("UNSUPPORTED_MODEL", "feature expression reference is null for " + child);
                records.Add(edge);
            }
        }
        records.Sort(delegate(Dictionary<string, object> left, Dictionary<string, object> right)
        {
            int kind = String.CompareOrdinal((string)left["kind"], (string)right["kind"]);
            if (kind != 0) return kind;
            int source = String.CompareOrdinal((string)left["source_id"], (string)right["source_id"]);
            return source != 0 ? source : String.CompareOrdinal((string)left["target_id"], (string)right["target_id"]);
        });
        return ToObjectList(records);
    }

    private static Dictionary<string, object> BuildSupportSummary(Part work)
    {
        List<object> reasons = new List<object>();
        Feature[] features = work.Features.GetFeatures();
        if (features == null) throw new M2ModelException("MODEL_READ_FAILED", "support feature enumeration returned null");
        foreach (Feature feature in features)
        {
            if (feature == null) throw new M2ModelException("UNSUPPORTED_MODEL", "support feature collection contains null");
            string type = feature.FeatureType ?? String.Empty;
            if (!String.Equals(type, "SKETCH", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(type, "DATUM_CSYS", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(type, "EXTRUDE", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, object> reason = new Dictionary<string, object>(StringComparer.Ordinal);
                reason.Add("feature_id", RequiredJournalId(feature, "feature"));
                reason.Add("feature_type", type);
                reason.Add("code", "FEATURE_PARAMETERS_UNSUPPORTED");
                reason.Add("message", M7HasNativeDefinition(feature)
                    ? "Native helical definition readback is available; parameter editing remains limited to supported Driving sketch dimensions."
                    : "Generic feature metadata and dependency links are available; feature-specific parameter semantics are not implemented for this feature type.");
                reasons.Add(reason);
            }
        }
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("status", reasons.Count == 0 ? "SUPPORTED" : "PARTIALLY_SUPPORTED");
        result.Add("unsupported", reasons);
        return result;
    }

    private static Dictionary<string, object> ParameterSupport(string featureType)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        bool generic = String.Equals(featureType, "SKETCH", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(featureType, "DATUM_CSYS", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(featureType, "EXTRUDE", StringComparison.OrdinalIgnoreCase);
        result.Add("status", generic ? "SUPPORTED_METADATA" : "UNSUPPORTED_PARAMETERS");
        result.Add("reason", generic
            ? "Feature identity, status, parent/child links, created bodies and created expressions are available; generic parameter access is not declared."
            : "Feature identity and dependency metadata are available; feature-specific parameters are not implemented for this type.");
        return result;
    }

    private static Dictionary<string, object> BuildIdentity(M2ModelBinding binding, Part work,
        Dictionary<string, object> readEffects)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("nx_build", binding.NxBuild ?? "UNKNOWN");
        result.Add("nx_build_source", binding.NxBuildSource);
        result.Add("nxopen_assembly_version", typeof(Session).Assembly.GetName().Version.ToString());
        result.Add("nxopen_file_version", GetNxOpenFileVersion());
        result.Add("process_id", Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
        result.Add("session_epoch", binding.SessionEpoch);
        result.Add("load_instance_id", binding.LoadInstanceId);
        result.Add("binding_epoch", binding.BindingEpoch);
        result.Add("part_path", work.FullPath);
        result.Add("part_tag", work.Tag.ToString());
        result.Add("part_units", work.PartUnits.ToString());
        result.Add("part_load_state", work.PartLoadState.ToString());
        result.Add("is_read_only", IsPartReadOnly(work));
        result.Add("is_modified", work.IsModified);
        result.Add("read_may_mark_modified", true);
        result.Add("artifact_scope", binding.ArtifactScope);
        result.Add("artifact_kind", binding.ArtifactKind);
        result.Add("support_summary", Clone(binding.ArtifactAssessment));
        if (readEffects != null)
        {
            result.Add("is_modified_before_read", readEffects["is_modified_before_read"]);
            result.Add("is_modified_after_read", readEffects["is_modified_after_read"]);
            result.Add("dirty_changed_by_read", readEffects["dirty_changed_by_read"]);
        }
        return result;
    }

    private static Dictionary<string, object> SupportStatus(string status, string reason)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("status", status);
        result.Add("reason", reason);
        return result;
    }

    private static BindingState RequireState(M2ModelBinding binding)
    {
        BindingState state = binding == null ? null : binding.AdapterState as BindingState;
        if (state == null) throw new M2ModelException("STALE_BINDING", "NX model binding has been released");
        return state;
    }

    private static string GetNxBuild(Session session)
    {
        try { return session.GetEnvironmentVariableValue("NX_FULL_VERSION"); }
        catch { return null; }
    }

    private static string GetNxOpenFileVersion()
    {
        try { return FileVersionInfo.GetVersionInfo(typeof(Session).Assembly.Location).ProductVersion ?? String.Empty; }
        catch { return String.Empty; }
    }

    private static string CanonicalPath(string path)
    {
        if (String.IsNullOrWhiteSpace(path)) throw new M2ModelException("UNSUPPORTED_ARTIFACT_SCOPE", "part path is empty");
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception ex) { throw new M2ModelException("UNSUPPORTED_ARTIFACT_SCOPE", "part path cannot be canonicalized (" + ex.GetType().Name + ")"); }
    }

    private static string CanonicalPartIdentityPath(string path)
    {
        if (String.IsNullOrEmpty(path)) return String.Empty;
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path.Trim(); }
    }

    private static bool PartIdentityPathMatches(string actualPath, string expectedPath)
    {
        return String.Equals(CanonicalPartIdentityPath(actualPath), CanonicalPartIdentityPath(expectedPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPartReadOnly(BasePart part)
    {
        return part == null || part.IsReadOnly;
    }

    private static string RequiredJournalId(NXObject value, string subject)
    {
        if (value == null) throw new M2ModelException("UNSUPPORTED_MODEL", subject + " is null");
        string id = value.JournalIdentifier;
        if (String.IsNullOrEmpty(id)) throw new M2ModelException("UNSUPPORTED_MODEL", subject + " has no JournalIdentifier");
        return id;
    }

    private static List<string> GetJournalIds(NXObject[] values, string subject)
    {
        if (values == null) throw new M2ModelException("MODEL_READ_FAILED", subject + " enumeration returned null");
        List<string> ids = new List<string>();
        foreach (NXObject value in values) ids.Add(RequiredJournalId(value, subject));
        ids.Sort(StringComparer.Ordinal);
        RejectDuplicates(ids, subject);
        return ids;
    }

    private static List<string> GetJournalIds(Feature[] values, string subject)
    {
        if (values == null) throw new M2ModelException("MODEL_READ_FAILED", subject + " enumeration returned null");
        List<string> ids = new List<string>();
        foreach (Feature value in values) ids.Add(RequiredJournalId(value, subject));
        ids.Sort(StringComparer.Ordinal);
        RejectDuplicates(ids, subject);
        return ids;
    }

    private static List<string> GetJournalIds(Body[] values, string subject)
    {
        if (values == null) throw new M2ModelException("MODEL_READ_FAILED", subject + " enumeration returned null");
        List<string> ids = new List<string>();
        foreach (Body value in values) ids.Add(RequiredJournalId(value, subject));
        ids.Sort(StringComparer.Ordinal);
        RejectDuplicates(ids, subject);
        return ids;
    }

    private static List<string> GetJournalIds(Face[] values, string subject)
    {
        if (values == null) throw new M2ModelException("MODEL_READ_FAILED", subject + " enumeration returned null");
        List<string> ids = new List<string>();
        foreach (Face value in values) ids.Add(RequiredJournalId(value, subject));
        ids.Sort(StringComparer.Ordinal);
        RejectDuplicates(ids, subject);
        return ids;
    }

    private static List<string> GetJournalIds(Edge[] values, string subject)
    {
        if (values == null) throw new M2ModelException("MODEL_READ_FAILED", subject + " enumeration returned null");
        List<string> ids = new List<string>();
        foreach (Edge value in values) ids.Add(RequiredJournalId(value, subject));
        ids.Sort(StringComparer.Ordinal);
        RejectDuplicates(ids, subject);
        return ids;
    }

    private static List<string> GetExpressionIds(Expression[] values)
    {
        if (values == null) throw new M2ModelException("MODEL_READ_FAILED", "feature expression enumeration returned null");
        List<string> ids = new List<string>();
        foreach (Expression value in values)
        {
            if (value == null || String.IsNullOrEmpty(value.Name)) throw new M2ModelException("UNSUPPORTED_MODEL", "feature has an expression without a stable name");
            ids.Add(value.Name);
        }
        ids.Sort(StringComparer.Ordinal);
        RejectDuplicates(ids, "feature expression");
        return ids;
    }

    private static void RejectDuplicates(List<string> values, string subject)
    {
        for (int i = 1; i < values.Count; i++)
            if (String.Equals(values[i - 1], values[i], StringComparison.Ordinal))
                throw new M2ModelException("UNSUPPORTED_MODEL", "duplicate " + subject + " identity " + values[i]);
    }

    private static List<object> ObjectStrings(List<string> values)
    {
        List<object> result = new List<object>();
        foreach (string value in values) result.Add(value);
        return result;
    }

    private static List<object> ToObjectList(List<Dictionary<string, object>> values)
    {
        List<object> result = new List<object>();
        foreach (Dictionary<string, object> value in values) result.Add(value);
        return result;
    }

    private static void SortRecords(List<Dictionary<string, object>> records, string key, string subject)
    {
        records.Sort(delegate(Dictionary<string, object> left, Dictionary<string, object> right)
        {
            return String.CompareOrdinal(Convert.ToString(left[key], CultureInfo.InvariantCulture),
                Convert.ToString(right[key], CultureInfo.InvariantCulture));
        });
        for (int i = 1; i < records.Count; i++)
            if (String.Equals(Convert.ToString(records[i - 1][key], CultureInfo.InvariantCulture),
                Convert.ToString(records[i][key], CultureInfo.InvariantCulture), StringComparison.Ordinal))
                throw new M2ModelException("UNSUPPORTED_MODEL", "duplicate " + subject + " identity " + Convert.ToString(records[i][key], CultureInfo.InvariantCulture));
    }

    private static string JoinStrings(List<object> values)
    {
        if (values == null || values.Count == 0) return String.Empty;
        List<string> strings = new List<string>();
        foreach (object value in values) strings.Add(Convert.ToString(value, CultureInfo.InvariantCulture));
        return String.Join("|", strings.ToArray());
    }

    private static string GeometryReference(Sketch.ConstraintGeometry geometry)
    {
        if (geometry.Geometry == null) throw new M2ModelException("UNSUPPORTED_MODEL", "constraint geometry operand is null");
        return RequiredJournalId(geometry.Geometry, "constraint geometry") + ":" + geometry.PointType.ToString();
    }

    private static string DimensionReference(Sketch.DimensionGeometry geometry)
    {
        if (geometry.Geometry == null) throw new M2ModelException("UNSUPPORTED_MODEL", "dimension geometry operand is null");
        return RequiredJournalId(geometry.Geometry, "dimension geometry") + ":" + geometry.AssocType.ToString();
    }

    private static List<object> Point(Point3d point)
    {
        List<object> result = new List<object>();
        result.Add(NumberText(point.X));
        result.Add(NumberText(point.Y));
        result.Add(NumberText(point.Z));
        return result;
    }

    private static List<object> Vector(double x, double y, double z)
    {
        List<object> result = new List<object>();
        result.Add(NumberText(x));
        result.Add(NumberText(y));
        result.Add(NumberText(z));
        return result;
    }

    private static string NumberText(double value)
    {
        if (Double.IsNaN(value) || Double.IsInfinity(value))
            throw new M2ModelException("UNSUPPORTED_MODEL", "NX returned a non-finite numeric value");
        if (value == 0.0) return "0";
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, object> Clone(Dictionary<string, object> source)
    {
        Dictionary<string, object> copy = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object> entry in source) copy.Add(entry.Key, entry.Value);
        return copy;
    }
}
