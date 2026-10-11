using System.Collections.Generic;

namespace NxSketchMcp.M2
{
    // Optional native helical operations; keep the existing M3 implementers compatible.
    public interface IM7HelicalOperationAdapter
    {
        M3PlanSnapshot PrepareThread(M2ModelBinding binding, M7ThreadRequest request);
        M3PlanSnapshot PrepareHelicalGroove(M2ModelBinding binding, M7HelicalGrooveRequest request);
    }

    public sealed class M7ThreadRequest
    {
        public string RevisionId;
        public string TargetBodyId;
        public string Axis;
        public double[] AxisOriginMm;
        public double StartMm;
        public double LengthMm;
        public string Handedness;
        public string Direction;
        public double MajorDiameterMm;
        public double MinorDiameterMm;
        public double PitchMm;
    }

    public sealed class M7HelicalGrooveRequest
    {
        public string RevisionId;
        public string TargetBodyId;
        public string Axis;
        public double[] AxisOriginMm;
        public double StartMm;
        public double LengthMm;
        public string Handedness;
        public double LeadMm;
        public double RootDiameterMm;
        public double ReferenceDiameterMm;
        public double OuterDiameterMm;
        public double SpaceWidthMm;
        public double FlankAngleDeg;
        public double PhaseDeg;
    }

    // M3 is an optional extension of the frozen M2 model adapter. The M2 bridge
    // keeps ownership, binding, revision, operation and cancellation behavior;
    // this contract describes only the additional native sketch/Extrude work.
    public interface IM3OperationAdapter
    {
        M3PlanSnapshot PrepareSketch(M2ModelBinding binding, M3SketchRequest request);
        M3PlanSnapshot PrepareExtrude(M2ModelBinding binding, M3ExtrudeRequest request);
        M3ExecutionResult ExecutePlan(M2ModelBinding binding, M3PlanSnapshot prepared);
        M3VerificationResult VerifyPlan(M2ModelBinding binding, M3PlanSnapshot prepared);
    }

    // M4 adds read-only planar face discovery. Face choices remain inside the
    // NX adapter; callers supply a constrained selector, never a face tag/index.
    public interface IM4FaceQueryAdapter
    {
        Dictionary<string, object> QueryFaces(M2ModelBinding binding, string revisionId,
            string selector, string bodyId);
    }

    public sealed class M3SketchRequest
    {
        public string RevisionId;
        public string Plane;
        public double[] OriginMm;
        public string SupportFaceSelector;
        public string SupportBodyId;
        public double[] FaceOriginOffsetMm;
        public List<M3SketchGeometryRequest> Geometries;
    }

    public sealed class M3SketchGeometryRequest
    {
        public string Id;
        public string Type;
        public double[] StartMm;
        public double[] EndMm;
        public double[] CornerMm;
        public double[] CenterMm;
        public double WidthMm;
        public double HeightMm;
        public double DiameterMm;
    }

    public sealed class M3ExtrudeRequest
    {
        public string RevisionId;
        public string SketchId;
        public string Mode;
        public string Direction;
        public string Extent;
        public double DepthMm;
        public string TargetBodyId;
    }

    public sealed class M3PlanSnapshot
    {
        public string Kind;
        public string RevisionId;
        public string SketchId;
        public string FeatureId;
        public string BodyId;
        public Dictionary<string, object> PublicView;
        public object AdapterState;
    }

    public sealed class M3ExecutionResult
    {
        // APPLIED, ROLLED_BACK, UNCHANGED, or UNKNOWN. The bridge freezes future
        // model writes for UNKNOWN and never automatically retries the plan.
        public string State;
        public string RevisionId;
        public Dictionary<string, object> Result;
        public string ErrorCode;
        public string ErrorMessage;
    }

    public sealed class M3VerificationResult
    {
        public bool Verified;
        public string RevisionId;
        public Dictionary<string, object> Checks;
        public Dictionary<string, object> Result;
        public string ErrorCode;
        public string ErrorMessage;
    }
}
