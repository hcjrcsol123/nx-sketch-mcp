// Invoke explicitly inside the user's idle NX GUI. Writes only unique isolated test parts.
// The production adapter is exercised directly; protocol/queue tests live in tests/m2.
using System;
using System.IO;
using System.Collections.Generic;
using NXOpen;
using NXOpen.UF;
using NxSketchMcp.M2;

public static class M7Acceptance
{
    private static readonly string Root = @"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp";
    public static void Main(string[] args)
    {
        Session session = Session.GetSession(); Part original = session.Parts.Work;
        if ((original != null && original.IsModified) || session.IsInTaskEnvironment)
            throw new InvalidOperationException("Acceptance requires a saved idle Work Part; preserve it before testing.");
        string folder = Path.Combine(Root, "workspace", "parts"); Directory.CreateDirectory(folder);
        string id = Guid.NewGuid().ToString("N");
        string source = Path.Combine(folder, "m7-acceptance-" + id + ".prt");
        string saved = Path.Combine(folder, "m7-acceptance-" + id + "-saved.prt");
        string report = Path.Combine(Root, "workspace", "build", "current", "M2", "m7-nx-acceptance.json");
        Dictionary<string, object> evidence = new Dictionary<string, object> { { "source", source }, { "saved_copy", saved },
            { "original_part", original == null ? null : original.FullPath }, { "test_layer", "real_NX2506_CSharp_production_adapter" }, { "passed", false } };
        List<object> cases = new List<object>(); evidence.Add("cases", cases);
        M2OpenModelAdapter adapter = new M2OpenModelAdapter(); M2ModelBinding binding = null;
        try
        {
            Part part = session.Parts.NewDisplay(source, Part.Units.Millimeters);
            Body threadBody = Cylinder(new double[] { 0, 0, 0 }, new double[] { 0, 0, 1 }, 40, 100);
            Body rightBody = Cylinder(new double[] { 0, 200, 0 }, new double[] { 1, 0, 0 }, 96, 184);
            Body leftBody = Cylinder(new double[] { 300, 0, 0 }, new double[] { 0, 1, 0 }, 96, 184);
            PartSaveStatus saveStatus = part.Save(BasePart.SaveComponents.True, BasePart.CloseAfterSave.False); saveStatus.Dispose();
            binding = adapter.Takeover(source, "saved_independent_copy", "m7-acceptance-session", "m7-acceptance-load", 1);
            M7ThreadRequest thread = new M7ThreadRequest { RevisionId = binding.RevisionId, TargetBodyId = threadBody.JournalIdentifier,
                Axis = "Z", AxisOriginMm = new double[] { 0, 0, 0 }, StartMm = 100, LengthMm = 30,
                Handedness = "right", Direction = "negative", MajorDiameterMm = 40, MinorDiameterMm = 37.546262, PitchMm = 2 };
            Run(adapter, binding, adapter.PrepareThread(binding, thread), cases, "external_thread_right_negative_Z");
            M7HelicalGrooveRequest right = Groove(binding, rightBody, "X", new double[] { 0, 200, 0 }, "right", 0);
            Run(adapter, binding, adapter.PrepareHelicalGroove(binding, right), cases, "three_guide_groove_right_X");
            M7HelicalGrooveRequest left = Groove(binding, leftBody, "Y", new double[] { 300, 0, 0 }, "left", 15);
            Run(adapter, binding, adapter.PrepareHelicalGroove(binding, left), cases, "three_guide_groove_left_Y_phase15");
            M2SaveCopyResult roundtrip = adapter.SaveCopy(binding, saved, true);
            evidence.Add("roundtrip", new Dictionary<string, object> { { "state", roundtrip.State }, { "reopened", roundtrip.Reopened },
                { "checks", roundtrip.Checks }, { "evidence", roundtrip.Evidence }, { "error_code", roundtrip.ErrorCode }, { "error_message", roundtrip.ErrorMessage } });
            if (roundtrip.State != "SAVED" || !roundtrip.Reopened || !ChecksPass(roundtrip.Checks))
                throw new InvalidOperationException("Native save/reopen verification failed: " + roundtrip.ErrorCode + ": " + roundtrip.ErrorMessage);
            evidence["passed"] = true;
        }
        catch (Exception ex)
        {
            evidence["error"] = ex.ToString();
            M2ModelException model = ex as M2ModelException;
            if (model != null) evidence["error_code"] = model.Code;
        }
        finally
        {
            try { if (binding != null) adapter.Release(binding); } catch (Exception ex) { evidence["release_error"] = ex.Message; }
            try
            {
                if (original != null)
                {
                    PartLoadStatus status; session.Parts.SetDisplay(original, false, false, out status);
                    if (status != null) status.Dispose(); session.Parts.SetWork(original);
                    evidence["original_part_restored"] = session.Parts.Work.Tag == original.Tag && !original.IsModified;
                }
                else
                {
                    List<Part> ownedTests = new List<Part>();
                    foreach (BasePart loaded in session.Parts)
                        if (loaded is Part && (loaded.FullPath == source || loaded.FullPath == saved)) ownedTests.Add((Part)loaded);
                    foreach (Part owned in ownedTests) owned.Close(BasePart.CloseWholeTree.False, BasePart.CloseModified.CloseModified, null);
                    evidence["empty_original_session_restored"] = session.Parts.Work == null;
                }
            }
            catch (Exception ex) { evidence["restore_error"] = ex.Message; }
            File.WriteAllText(report, M2Json.CanonicalText(evidence), new System.Text.UTF8Encoding(false));
        }
    }

    private static Body Cylinder(double[] origin, double[] direction, double diameter, double length)
    {
        Tag feature, body;
        UFSession.GetUFSession().Modl.CreateCylinder(FeatureSigns.Nullsign, Tag.Null, origin,
            length.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            diameter.ToString("R", System.Globalization.CultureInfo.InvariantCulture), direction, out feature);
        UFSession.GetUFSession().Modl.AskFeatBody(feature, out body);
        return (Body)NXOpen.Utilities.NXObjectManager.Get(body);
    }
    private static M7HelicalGrooveRequest Groove(M2ModelBinding binding, Body body, string axis, double[] origin, string hand, double phase)
    {
        return new M7HelicalGrooveRequest { RevisionId = binding.RevisionId, TargetBodyId = body.JournalIdentifier,
            Axis = axis, AxisOriginMm = origin, StartMm = 35, LengthMm = 2 * Math.PI * 8,
            Handedness = hand, LeadMm = Math.PI * 8, RootDiameterMm = 60.8, ReferenceDiameterMm = 80,
            OuterDiameterMm = 110, SpaceWidthMm = Math.PI * 8 / 2, FlankAngleDeg = 20, PhaseDeg = phase };
    }
    private static void Run(M2OpenModelAdapter adapter, M2ModelBinding binding, M3PlanSnapshot plan, List<object> cases, string name)
    {
        M3ExecutionResult execution = adapter.ExecutePlan(binding, plan);
        Dictionary<string, object> record = new Dictionary<string, object> { { "name", name }, { "plan", plan.PublicView },
            { "execution_state", execution.State }, { "execution", execution.Result }, { "error_code", execution.ErrorCode }, { "error_message", execution.ErrorMessage } };
        cases.Add(record);
        if (execution.State != "APPLIED") throw new InvalidOperationException(name + " execution: " + execution.State + " " + execution.ErrorCode + ": " + execution.ErrorMessage);
        M3VerificationResult verification = adapter.VerifyPlan(binding, plan);
        record.Add("verified", verification.Verified); record.Add("verification_checks", verification.Checks);
        if (!verification.Verified || !ChecksPass(verification.Checks)) throw new InvalidOperationException(name + " verification failed: " + verification.ErrorCode + ": " + verification.ErrorMessage);
    }
    private static bool ChecksPass(Dictionary<string, object> checks)
    {
        if (checks == null || checks.Count == 0) return false;
        foreach (object value in checks.Values)
        {
            Dictionary<string, object> record = value as Dictionary<string, object>;
            if (record == null || !record.ContainsKey("status") || (string)record["status"] != "VERIFIED") return false;
        }
        return true;
    }
    public static int GetUnloadOption(string dummy) { return (int)Session.LibraryUnloadOption.Explicitly; }
}
