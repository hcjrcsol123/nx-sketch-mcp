using System;
using System.Collections.Generic;

namespace NxSketchMcp.M2
{
    // NXOpen is deliberately kept out of M2Bridge.dll. The loaded NX entry assembly
    // supplies this adapter, and every adapter call is made on the dispatcher STA.
    public interface IM2ModelAdapter
    {
        M2ModelBinding Takeover(string expectedPartPath, string artifactScope, string sessionEpoch,
            string loadInstanceId, int bindingEpoch);
        bool IsBindingCurrent(M2ModelBinding binding, out string reason);
        Dictionary<string, object> GetSession(M2ModelBinding binding);
        Dictionary<string, object> InspectPart(M2ModelBinding binding);
        Dictionary<string, object> InspectSketch(M2ModelBinding binding, string sketchId);
        void Release(M2ModelBinding binding);
    }

    // Parameter editing is an optional, narrowly scoped extension. The bridge
    // creates and retains its own immutable plan after this adapter has resolved
    // the real NX constraint, expression, unit, and geometry relationship.
    public interface IM2OperationAdapter
    {
        M2ParameterPlanSnapshot PrepareParameterEdit(M2ModelBinding binding, M2ParameterEditRequest request);
        M2ParameterExecutionResult ExecuteParameterEdit(M2ModelBinding binding,
            M2ParameterPlanSnapshot prepared, string requestedValue, string requestedUnit);
    }

    // M2-7 adds explicit verification and saved-copy operations without making
    // these methods mandatory for older fixture adapters.
    public interface IM2M27ModelAdapter
    {
        M2PartVerificationResult VerifyParameterEdit(M2ModelBinding binding,
            M2ParameterPlanSnapshot prepared);
        M2SaveCopyResult SaveCopy(M2ModelBinding binding, string targetPath, bool reopen);
    }

    public sealed class M2PartVerificationResult
    {
        public bool Verified;
        public string RevisionId;
        public Dictionary<string, object> Checks;
        public string ErrorCode;
        public string ErrorMessage;
    }

    public sealed class M2SaveCopyResult
    {
        // SAVED means SaveAs completed. UNKNOWN means the adapter cannot prove
        // whether the output was saved or reopened; the bridge must freeze writes.
        public string State;
        public string PartPath;
        public string PartTag;
        public string RevisionId;
        public Dictionary<string, object> Identity;
        public bool Reopened;
        public Dictionary<string, object> Checks;
        public Dictionary<string, object> Evidence;
        public string ErrorCode;
        public string ErrorMessage;
    }

    public sealed class M2ParameterEditRequest
    {
        public string RevisionId;
        public string SketchId;
        public string ConstraintId;
        public string ValueText;
        public string Unit;
    }

    public sealed class M2ParameterPlanSnapshot
    {
        public string RevisionId;
        public string SketchId;
        public string ConstraintId;
        public string DimensionType;
        public string DimensionState;
        public string SketchFeatureId;
        public string ExpressionName;
        public string ExpressionRightHandSide;
        public string CurrentValue;
        public string Unit;
        public List<string> GeometryRefs;
        public object AdapterState;
    }

    public sealed class M2ParameterExecutionResult
    {
        // APPLIED, ROLLED_BACK, UNCHANGED, or UNKNOWN.
        public string State;
        public string RevisionId;
        public Dictionary<string, object> Evidence;
        public string ErrorCode;
        public string ErrorMessage;
    }

    public sealed class M2ModelBinding
    {
        // The bridge passes ArtifactScope to the NX adapter. ArtifactKind and
        // ArtifactAssessment are returned from native structure checks.
        public string ArtifactScope;
        public string ArtifactKind;
        public Dictionary<string, object> ArtifactAssessment;
        public bool IsReadOnly;
        public string SessionEpoch;
        public string LoadInstanceId;
        public int BindingEpoch;
        public string PartPath;
        public string PartTag;
        public string NxBuild;
        public string NxBuildSource;
        public string RevisionId;
        public string CanonicalSnapshot;
        public Dictionary<string, object> Identity;
        public object AdapterState;
    }

    public sealed class M2ModelException : Exception
    {
        public readonly string Code;

        public M2ModelException(string code, string message) : base(message)
        {
            Code = String.IsNullOrEmpty(code) ? "MODEL_READ_FAILED" : code;
        }
    }
}
