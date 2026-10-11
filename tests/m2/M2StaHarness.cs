// NX-free STA WinForms host for the production M2 bridge assembly.
// It starts only the local IPC server and remains alive until stdin receives "stop".
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using NxSketchMcp.M2;

internal static class M2StaHarness
{
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static object bridgeServer;
    private static MethodInfo stopMethod;
    private static bool bridgeStarted;
    private static bool stopInvoked;
    private static int exitCode;
    private static FakeModelAdapter fakeModelAdapter;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("usage: M2StaHarness.exe <bridge-assembly> <pipe-name> <token-file>");
            return 64;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var form = new Form())
        {
            form.Text = "M2 IPC Test Harness";
            form.ShowInTaskbar = false;
            form.WindowState = FormWindowState.Minimized;
            form.Shown += delegate
            {
                try
                {
                    if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                        throw new InvalidOperationException("WinForms message loop is not on an STA thread");

                    var assemblyPath = System.IO.Path.GetFullPath(args[0]);
                    var bridgeAssembly = typeof(M2BridgeServer).Assembly;

                    var startMethod = typeof(M2BridgeServer).GetMethod("Start", BindingFlags.Public | BindingFlags.Instance,
                        null, new[] { typeof(string), typeof(string) }, null);
                    stopMethod = typeof(M2BridgeServer).GetMethod("Stop", BindingFlags.Public | BindingFlags.Instance,
                        null, Type.EmptyTypes, null);
                    if (startMethod == null || stopMethod == null)
                        throw new InvalidOperationException("expected public instance Start(string,string) and Stop() methods");

                    fakeModelAdapter = new FakeModelAdapter();
                    bridgeServer = new M2BridgeServer(fakeModelAdapter);
                    startMethod.Invoke(bridgeServer, new object[] { args[1], System.IO.Path.GetFullPath(args[2]) });
                    bridgeStarted = true;
                    bool referencesNxOpen = bridgeAssembly.GetReferencedAssemblies()
                        .Any(name => name.Name.StartsWith("NXOpen", StringComparison.OrdinalIgnoreCase));
                    WriteJson("{\"event\":\"ready\",\"pid\":" +
                        System.Diagnostics.Process.GetCurrentProcess().Id +
                        ",\"main_thread_id\":" + GetCurrentThreadId() +
                        ",\"apartment\":\"STA\",\"assembly_path\":" + JsonString(assemblyPath) +
                        ",\"assembly_version\":" + JsonString(bridgeAssembly.GetName().Version.ToString()) +
                        ",\"references_nxopen\":" + (referencesNxOpen ? "true" : "false") +
                        ",\"fake_model_adapter\":true" +
                        ",\"pipe_name\":" + JsonString(args[1]) + "}");
                }
                catch (Exception ex)
                {
                    exitCode = 2;
                    Console.Error.WriteLine("harness_start_error: " + Unwrap(ex).GetType().Name + ": " + Unwrap(ex).Message);
                    form.Close();
                }
            };

            form.FormClosing += delegate
            {
                if (!bridgeStarted || stopInvoked)
                    return;
                stopInvoked = true;
                try
                {
                    stopMethod.Invoke(bridgeServer, null);
                    WriteJson("{\"event\":\"stopped\",\"pid\":" +
                        System.Diagnostics.Process.GetCurrentProcess().Id +
                        ",\"main_thread_id\":" + GetCurrentThreadId() + "}");
                }
                catch (Exception ex)
                {
                    exitCode = 3;
                    Console.Error.WriteLine("harness_stop_error: " + Unwrap(ex).GetType().Name + ": " + Unwrap(ex).Message);
                }
            };

            var control = new Thread(delegate()
            {
                string line;
                while ((line = Console.ReadLine()) != null)
                {
                    string command = line.Trim();
                    if (String.Equals(command, "wait-inspect-start", StringComparison.Ordinal))
                    {
                        bool signaled = fakeModelAdapter != null && fakeModelAdapter.WaitForInspectStart(5000);
                        WriteJson("{\"event\":\"inspect-start\",\"observed\":" + (signaled ? "true" : "false") + "}");
                        continue;
                    }
                    if (String.Equals(command, "block-inspect", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.BlockInspection();
                        WriteJson("{\"event\":\"inspect-blocked\"}");
                        continue;
                    }
                    if (String.Equals(command, "release-inspect", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.ReleaseInspection();
                        WriteJson("{\"event\":\"inspect-released\"}");
                        continue;
                    }
                    if (String.Equals(command, "change-identity", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.ChangeIdentity();
                        WriteJson("{\"event\":\"identity-changed\"}");
                        continue;
                    }
                    if (String.Equals(command, "block-operation", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.BlockOperation();
                        WriteJson("{\"event\":\"operation-blocked\"}");
                        continue;
                    }
                    if (String.Equals(command, "release-operation", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.ReleaseOperation();
                        WriteJson("{\"event\":\"operation-released\"}");
                        continue;
                    }
                    if (String.Equals(command, "age-running-m3-operation", StringComparison.Ordinal))
                    {
                        bool aged = AgeRunningM3Operation();
                        WriteJson("{\"event\":\"m3-operation-aged\",\"observed\":" + (aged ? "true" : "false") + "}");
                        continue;
                    }
                    if (String.Equals(command, "wait-operation-start", StringComparison.Ordinal))
                    {
                        bool signaled = fakeModelAdapter != null && fakeModelAdapter.WaitForOperationStart(5000);
                        WriteJson("{\"event\":\"operation-start\",\"observed\":" + (signaled ? "true" : "false") + "}");
                        continue;
                    }
                    if (String.Equals(command, "change-revision", StringComparison.Ordinal))
                    {
                        string revision = fakeModelAdapter == null ? "" : fakeModelAdapter.ChangeRevision();
                        WriteJson("{\"event\":\"revision-changed\",\"revision_id\":" + JsonString(revision) + "}");
                        continue;
                    }
                    if (String.Equals(command, "unknown-after-mutation", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.ReturnUnknownAfterMutation();
                        WriteJson("{\"event\":\"unknown-after-mutation\"}");
                        continue;
                    }
                    if (String.Equals(command, "rollback-after-write", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.ReturnRolledBackAfterWrite();
                        WriteJson("{\"event\":\"rollback-after-write\"}");
                        continue;
                    }
                    if (String.Equals(command, "rollback-unverified-after-write", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.ReturnRollbackUnverifiedAfterWrite();
                        WriteJson("{\"event\":\"rollback-unverified-after-write\"}");
                        continue;
                    }
                    if (String.Equals(command, "model-drift", StringComparison.Ordinal))
                    {
                        string revision = fakeModelAdapter == null ? "" : fakeModelAdapter.DriftModel();
                        WriteJson("{\"event\":\"model-drift\",\"revision_id\":" + JsonString(revision) + "}");
                        continue;
                    }
                    if (String.Equals(command, "unknown-next-save", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.ReturnUnknownOnNextSave();
                        WriteJson("{\"event\":\"unknown-next-save\"}");
                        continue;
                    }
                    if (String.Equals(command, "block-save", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.BlockSave();
                        WriteJson("{\"event\":\"save-blocked\"}");
                        continue;
                    }
                    if (String.Equals(command, "wait-save-start", StringComparison.Ordinal))
                    {
                        bool signaled = fakeModelAdapter != null && fakeModelAdapter.WaitForSaveStart(5000);
                        WriteJson("{\"event\":\"save-start\",\"observed\":" + (signaled ? "true" : "false") + "}");
                        continue;
                    }
                    if (String.Equals(command, "release-save", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.ReleaseSave();
                        WriteJson("{\"event\":\"save-released\"}");
                        continue;
                    }
                    if (String.Equals(command, "fake-counts", StringComparison.Ordinal))
                    {
                        WriteJson("{\"event\":\"fake-counts\",\"takeover_calls\":" +
                            (fakeModelAdapter == null ? 0 : fakeModelAdapter.TakeoverCalls) +
                            ",\"inspection_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.InspectionCalls) +
                            ",\"prepare_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.PrepareCalls) +
                            ",\"execution_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.ExecutionCalls) +
                            ",\"verification_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.VerificationCalls) +
                            ",\"save_copy_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.SaveCopyCalls) +
                            ",\"saved_copy_count\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.SavedCopyCount) +
                            ",\"write_attempt_count\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.WriteAttemptCount) +
                            ",\"rollback_count\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.RollbackCount) +
                            ",\"mutation_count\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.MutationCount) +
                            ",\"m3_sketch_prepare_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3SketchPrepareCalls) +
                            ",\"m4_face_query_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M4FaceQueryCalls) +
                            ",\"m4_last_selector\":" + JsonString(fakeModelAdapter == null ? "" : fakeModelAdapter.M4LastSelector) +
                            ",\"m4_last_body_id\":" + JsonString(fakeModelAdapter == null ? "" : fakeModelAdapter.M4LastBodyId) +
                            ",\"m4_face_query_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M4FaceQueryCalls) +
                            ",\"m4_last_selector\":" + JsonString(fakeModelAdapter == null ? "" : fakeModelAdapter.M4LastSelector) +
                            ",\"m4_last_body_id\":" + JsonString(fakeModelAdapter == null ? "" : fakeModelAdapter.M4LastBodyId) +
                            ",\"m3_extrude_prepare_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3ExtrudePrepareCalls) +
                            ",\"m3_execution_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3ExecutionCalls) +
                            ",\"m3_verification_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3VerificationCalls) +
                            ",\"m3_mutation_count\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3MutationCount) +
                            ",\"m3_sketch_count\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3SketchCount) +
                            ",\"m3_constraint_count\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3ConstraintCount) +
                            ",\"m3_body_count\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3BodyCount) +
                            ",\"revision_id\":" + JsonString(fakeModelAdapter == null ? "" : fakeModelAdapter.RevisionId) +
                            ",\"current_value\":" + JsonString(fakeModelAdapter == null ? "" : fakeModelAdapter.CurrentValue) + "}");
                        continue;
                    }
                    if (!String.Equals(command, "stop", StringComparison.Ordinal)) continue;
                    try
                    {
                        form.BeginInvoke((MethodInvoker)delegate { form.Close(); });
                    }
                    catch (InvalidOperationException)
                    {
                        // The form already closed during startup failure or process cleanup.
                    }
                    return;
                }
            });
            control.IsBackground = true;
            control.Name = "M2 harness control reader";
            control.Start();

            Application.Run(form);
        }
        return exitCode;
    }

    private static Exception Unwrap(Exception ex)
    {
        var invocation = ex as TargetInvocationException;
        return invocation != null && invocation.InnerException != null ? invocation.InnerException : ex;
    }

    private static bool AgeRunningM3Operation()
    {
        if (bridgeServer == null) return false;
        Type serverType = bridgeServer.GetType();
        FieldInfo operationsField = serverType.GetField("_operations", BindingFlags.Instance | BindingFlags.NonPublic);
        IDictionary operations = operationsField == null ? null : operationsField.GetValue(bridgeServer) as IDictionary;
        if (operations == null) return false;
        foreach (DictionaryEntry entry in operations)
        {
            object operation = entry.Value;
            if (operation == null) continue;
            Type operationType = operation.GetType();
            FieldInfo kindField = operationType.GetField("Kind", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo stateField = operationType.GetField("State", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo timestampField = operationType.GetField("StartedTimestamp", BindingFlags.Instance | BindingFlags.NonPublic);
            string kind = kindField == null ? null : kindField.GetValue(operation) as string;
            string state = stateField == null ? null : stateField.GetValue(operation) as string;
            if (kind == null || !kind.StartsWith("execute_create_", StringComparison.Ordinal) ||
                !String.Equals(state, "RUNNING", StringComparison.Ordinal) || timestampField == null) continue;
            long ageTicks = (long)(121000.0 * Stopwatch.Frequency / 1000.0);
            timestampField.SetValue(operation, Stopwatch.GetTimestamp() - ageTicks);
            return true;
        }
        return false;
    }

    private static void WriteJson(string line)
    {
        Console.Out.WriteLine(line);
        Console.Out.Flush();
    }

    private static string JsonString(string value)
    {
        return "\"" + (value ?? String.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
    }

    private sealed class FakeModelAdapter : IM2ModelAdapter, IM2OperationAdapter, IM2M27ModelAdapter, IM3OperationAdapter, IM4FaceQueryAdapter, IM7HelicalOperationAdapter
    {
        private sealed class FakeM3Geometry
        {
            internal string Id;
            internal string JournalId;
            internal string Type;
            internal double[] Start;
            internal double[] End;
            internal double[] Corner;
            internal double[] Center;
            internal double Width;
            internal double Height;
            internal double Diameter;
        }

        private sealed class FakeM3Constraint
        {
            internal string Id;
            internal string GeometryId;
            internal string Role;
            internal string Type;
            internal string ExpressionName;
            internal double Value;
            internal bool IsDriving;
            internal FakeM3Sketch Sketch;
            internal FakeM3Geometry Geometry;
            internal List<string> GeometryRefs;
        }

        private sealed class FakeM3Sketch
        {
            internal string Id;
            internal string FeatureId;
            internal string Plane;
            internal double[] Origin;
            internal string SupportFaceSelector;
            internal string SupportBodyId;
            internal double[] FaceOriginOffsetMm;
            internal List<FakeM3Geometry> Geometries = new List<FakeM3Geometry>();
            internal List<FakeM3Constraint> Constraints = new List<FakeM3Constraint>();
        }

        private sealed class FakeM3Body
        {
            internal string Id;
            internal bool IsSolid;
            internal double Volume;
            internal double[] Bbox;
        }

        private sealed class FakeM3Feature
        {
            internal string Id;
            internal string Kind;
            internal string SketchId;
            internal string BodyId;
        }

        private sealed class FakeM3PlanState
        {
            internal string Kind;
            internal string FeatureId;
            internal string BodyId;
            internal FakeM3Sketch Sketch;
            internal M3ExtrudeRequest Extrude;
            internal string TargetBodyId;
            internal Dictionary<string, object> TargetBefore;
            internal List<Dictionary<string, object>> NonTargetBefore = new List<Dictionary<string, object>>();
            internal bool Executed;
            internal bool ExpectedUnknown;
            internal Dictionary<string, object> NativeDefinition;
        }

        private readonly System.Threading.ManualResetEvent inspectStarted = new System.Threading.ManualResetEvent(false);
        private readonly System.Threading.ManualResetEvent releaseInspect = new System.Threading.ManualResetEvent(false);
        private readonly System.Threading.ManualResetEvent operationStarted = new System.Threading.ManualResetEvent(false);
        private readonly System.Threading.ManualResetEvent releaseOperation = new System.Threading.ManualResetEvent(false);
        private readonly System.Threading.ManualResetEvent saveStarted = new System.Threading.ManualResetEvent(false);
        private readonly System.Threading.ManualResetEvent releaseSave = new System.Threading.ManualResetEvent(false);
        private int identityChanged;
        private int takeoverCalls;
        private int inspectionCalls;
        private int prepareCalls;
        private int executionCalls;
        private int verificationCalls;
        private int saveCopyCalls;
        private int writeAttemptCount;
        private int rollbackCount;
        private int mutationCount;
        private int blockExecution;
        private int blockInspection;
        private int blockSave;
        private int unknownAfterMutation;
        private int rolledBackAfterWrite;
        private int rollbackUnverifiedAfterWrite;
        private int unknownNextSave;
        private int revisionNumber = 1;
        private int savedCopyNumber;
        private int m3SketchPrepareCalls;
        private int m3ExtrudePrepareCalls;
        private int m3ExecutionCalls;
        private int m3VerificationCalls;
        private int m3MutationCount;
        private int m4FaceQueryCalls;
        private string m4LastSelector;
        private string m4LastBodyId;
        private int m3SketchNumber;
        private int m3FeatureNumber;
        private int m3ConstraintNumber;
        private int m3BodyNumber;
        private string currentValue = "10";
        private M2ModelBinding activeBinding;
        private readonly Dictionary<M2ParameterPlanSnapshot, string> expectedValues =
            new Dictionary<M2ParameterPlanSnapshot, string>();
        private readonly HashSet<string> savedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FakeM3Sketch> m3Sketches = new Dictionary<string, FakeM3Sketch>(StringComparer.Ordinal);
        private readonly Dictionary<string, FakeM3Constraint> m3Constraints = new Dictionary<string, FakeM3Constraint>(StringComparer.Ordinal);
        private readonly Dictionary<string, FakeM3Body> m3Bodies = new Dictionary<string, FakeM3Body>(StringComparer.Ordinal);
        private readonly List<FakeM3Feature> m3Features = new List<FakeM3Feature>();

        internal int TakeoverCalls { get { return System.Threading.Interlocked.CompareExchange(ref takeoverCalls, 0, 0); } }
        internal int InspectionCalls { get { return System.Threading.Interlocked.CompareExchange(ref inspectionCalls, 0, 0); } }
        internal int PrepareCalls { get { return System.Threading.Interlocked.CompareExchange(ref prepareCalls, 0, 0); } }
        internal int ExecutionCalls { get { return System.Threading.Interlocked.CompareExchange(ref executionCalls, 0, 0); } }
        internal int VerificationCalls { get { return System.Threading.Interlocked.CompareExchange(ref verificationCalls, 0, 0); } }
        internal int SaveCopyCalls { get { return System.Threading.Interlocked.CompareExchange(ref saveCopyCalls, 0, 0); } }
        internal int SavedCopyCount { get { lock (savedTargets) return savedTargets.Count; } }
        internal int WriteAttemptCount { get { return System.Threading.Interlocked.CompareExchange(ref writeAttemptCount, 0, 0); } }
        internal int RollbackCount { get { return System.Threading.Interlocked.CompareExchange(ref rollbackCount, 0, 0); } }
        internal int MutationCount { get { return System.Threading.Interlocked.CompareExchange(ref mutationCount, 0, 0); } }
        internal int M3SketchPrepareCalls { get { return System.Threading.Interlocked.CompareExchange(ref m3SketchPrepareCalls, 0, 0); } }
        internal int M3ExtrudePrepareCalls { get { return System.Threading.Interlocked.CompareExchange(ref m3ExtrudePrepareCalls, 0, 0); } }
        internal int M3ExecutionCalls { get { return System.Threading.Interlocked.CompareExchange(ref m3ExecutionCalls, 0, 0); } }
        internal int M3VerificationCalls { get { return System.Threading.Interlocked.CompareExchange(ref m3VerificationCalls, 0, 0); } }
        internal int M3MutationCount { get { return System.Threading.Interlocked.CompareExchange(ref m3MutationCount, 0, 0); } }
        internal int M4FaceQueryCalls { get { return System.Threading.Interlocked.CompareExchange(ref m4FaceQueryCalls, 0, 0); } }
        internal string M4LastSelector { get { return System.Threading.Volatile.Read(ref m4LastSelector); } }
        internal string M4LastBodyId { get { return System.Threading.Volatile.Read(ref m4LastBodyId); } }
        internal int M3SketchCount { get { return m3Sketches.Count; } }
        internal int M3BodyCount { get { return m3Bodies.Count; } }
        internal int M3ConstraintCount { get { return m3Constraints.Count; } }
        internal string RevisionId { get { return System.Threading.Interlocked.CompareExchange(ref revisionNumber, 0, 0).ToString(); } }
        internal string CurrentValue { get { return System.Threading.Volatile.Read(ref currentValue); } }
        internal bool WaitForInspectStart(int milliseconds) { return inspectStarted.WaitOne(milliseconds); }
        internal bool WaitForOperationStart(int milliseconds) { return operationStarted.WaitOne(milliseconds); }
        internal bool WaitForSaveStart(int milliseconds) { return saveStarted.WaitOne(milliseconds); }
        internal void ChangeIdentity() { System.Threading.Interlocked.Exchange(ref identityChanged, 1); }
        internal void BlockInspection()
        {
            inspectStarted.Reset();
            releaseInspect.Reset();
            System.Threading.Interlocked.Exchange(ref blockInspection, 1);
        }
        internal void ReleaseInspection()
        {
            System.Threading.Interlocked.Exchange(ref blockInspection, 0);
            releaseInspect.Set();
        }
        internal void BlockOperation()
        {
            operationStarted.Reset();
            releaseOperation.Reset();
            System.Threading.Interlocked.Exchange(ref unknownAfterMutation, 0);
            System.Threading.Interlocked.Exchange(ref rolledBackAfterWrite, 0);
            System.Threading.Interlocked.Exchange(ref rollbackUnverifiedAfterWrite, 0);
            System.Threading.Interlocked.Exchange(ref blockExecution, 1);
        }
        internal void ReleaseOperation() { releaseOperation.Set(); }
        internal void BlockSave()
        {
            saveStarted.Reset();
            releaseSave.Reset();
            System.Threading.Interlocked.Exchange(ref blockSave, 1);
        }
        internal void ReleaseSave()
        {
            System.Threading.Interlocked.Exchange(ref blockSave, 0);
            releaseSave.Set();
        }
        internal string ChangeRevision() { return System.Threading.Interlocked.Increment(ref revisionNumber).ToString(); }
        internal string DriftModel()
        {
            System.Threading.Volatile.Write(ref currentValue, "99");
            string revision = System.Threading.Interlocked.Increment(ref revisionNumber).ToString();
            if (activeBinding != null) activeBinding.RevisionId = revision;
            return revision;
        }
        internal void ReturnUnknownOnNextSave() { System.Threading.Interlocked.Exchange(ref unknownNextSave, 1); }
        internal void ReturnUnknownAfterMutation()
        {
            System.Threading.Interlocked.Exchange(ref blockExecution, 0);
            System.Threading.Interlocked.Exchange(ref unknownAfterMutation, 1);
            System.Threading.Interlocked.Exchange(ref rolledBackAfterWrite, 0);
            System.Threading.Interlocked.Exchange(ref rollbackUnverifiedAfterWrite, 0);
        }
        internal void ReturnRolledBackAfterWrite()
        {
            System.Threading.Interlocked.Exchange(ref blockExecution, 0);
            System.Threading.Interlocked.Exchange(ref unknownAfterMutation, 0);
            System.Threading.Interlocked.Exchange(ref rolledBackAfterWrite, 1);
            System.Threading.Interlocked.Exchange(ref rollbackUnverifiedAfterWrite, 0);
        }
        internal void ReturnRollbackUnverifiedAfterWrite()
        {
            System.Threading.Interlocked.Exchange(ref blockExecution, 0);
            System.Threading.Interlocked.Exchange(ref unknownAfterMutation, 0);
            System.Threading.Interlocked.Exchange(ref rolledBackAfterWrite, 0);
            System.Threading.Interlocked.Exchange(ref rollbackUnverifiedAfterWrite, 1);
        }

        public M2ModelBinding Takeover(string expectedPartPath, string artifactScope, string sessionEpoch, string loadInstanceId, int bindingEpoch)
        {
            System.Threading.Interlocked.Increment(ref takeoverCalls);
            if (!String.IsNullOrWhiteSpace(expectedPartPath) &&
                !String.Equals(expectedPartPath.Replace('/', '\\'), "C:\\M2Fake\\model.prt", StringComparison.OrdinalIgnoreCase))
                throw new M2ModelException("SESSION_MISMATCH", "fixture Work Part path mismatch");
            M2ModelBinding binding = new M2ModelBinding();
            binding.ArtifactScope = artifactScope;
            binding.ArtifactKind = "standalone_part";
            binding.ArtifactAssessment = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "status", "SUPPORTED_LIMITED" }, { "history_mode", "NATIVE_SKETCH_HISTORY" },
                { "history_feature_count", 1 }, { "sketch_count", 1 }, { "feature_backed_sketch_count", 1 },
                { "driving_edit_scope", "fixture supported Driving constraints" },
                { "append_scope", "limited M3 native sketch and extrusion operations" },
                { "unsupported_feature_parameter_count", 0 },
                { "dependencies", new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "external_file_reference_count", 0 }, { "interpart_expression_count", 0 },
                    { "external_feature_parent_count", 0 }, { "external_sketch_geometry_count", 0 },
                    { "wave_or_link_feature_count", 0 } } }
            };
            binding.IsReadOnly = false;
            binding.SessionEpoch = sessionEpoch;
            binding.LoadInstanceId = loadInstanceId;
            binding.BindingEpoch = bindingEpoch;
            binding.PartPath = "C:\\M2Fake\\model.prt";
            binding.PartTag = "fake-part-tag-1";
            binding.NxBuild = "NXOpen fixture build 1";
            binding.RevisionId = "1";
            binding.CanonicalSnapshot = "{\"schema\":\"m2-semantic-v1\"}";
            binding.AdapterState = this;
            binding.Identity = new Dictionary<string, object>(StringComparer.Ordinal);
            binding.Identity.Add("nx_build", binding.NxBuild);
            binding.Identity.Add("process_id", System.Diagnostics.Process.GetCurrentProcess().Id);
            binding.Identity.Add("part_path", binding.PartPath);
            binding.Identity.Add("part_tag", binding.PartTag);
            binding.Identity.Add("part_units", "Millimeters");
            binding.Identity.Add("is_modified_before_read", false);
            binding.Identity.Add("is_modified_after_read", false);
            binding.Identity.Add("dirty_changed_by_read", false);
            binding.Identity.Add("read_may_mark_modified", true);
            binding.Identity.Add("artifact_scope", binding.ArtifactScope);
            binding.Identity.Add("artifact_kind", binding.ArtifactKind);
            binding.Identity.Add("support_summary", binding.ArtifactAssessment);
            activeBinding = binding;
            return binding;
        }

        public bool IsBindingCurrent(M2ModelBinding binding, out string reason)
        {
            if (System.Threading.Interlocked.CompareExchange(ref identityChanged, 0, 0) == 0 &&
                binding != null && Object.ReferenceEquals(binding.AdapterState, this))
            {
                reason = null;
                return true;
            }
            reason = "fixture Work Part identity changed";
            return false;
        }

        public Dictionary<string, object> GetSession(M2ModelBinding binding)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result.Add("nx_build", binding.NxBuild);
            result.Add("part_path", binding.PartPath);
            result.Add("part_tag", binding.PartTag);
            result.Add("part_units", "Millimeters");
            result.Add("revision_id", binding.RevisionId);
            return result;
        }

        public Dictionary<string, object> InspectPart(M2ModelBinding binding)
        {
            System.Threading.Interlocked.Increment(ref inspectionCalls);
            inspectStarted.Set();
            if (System.Threading.Interlocked.CompareExchange(ref blockInspection, 0, 0) != 0)
            {
                if (!releaseInspect.WaitOne(15000))
                    throw new M2ModelException("FIXTURE_GATE_TIMEOUT", "fixture inspection release gate timed out");
            }
            else
                System.Threading.Thread.Sleep(1100);
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result.Add("schema", "m2-semantic-v1");
            result.Add("part_path", binding.PartPath);
            result.Add("part_tag", binding.PartTag);
            result.Add("part_units", "Millimeters");
            result.Add("features", new List<object> { new Dictionary<string, object> {
                { "journal_id", "feature-1" }, { "feature_type", "EXTRUDE" }, { "parameter_support", "SUPPORTED_METADATA" }
            }});
            result.Add("expressions", new List<object> { new Dictionary<string, object> {
                { "name", "p11" }, { "right_hand_side", CurrentValue }, { "value", CurrentValue }, { "unit", "mm" }
            }});
            result.Add("dependencies", new List<object>());
            result.Add("history", new List<object> { new Dictionary<string, object> {
                { "journal_id", "feature-1" }, { "feature_type", "EXTRUDE" }, { "expression_name", "p11" }
            }});
            result.Add("associations", new List<object> { new Dictionary<string, object> {
                { "sketch_id", "sketch-1" }, { "constraint_id", "constraint-1" },
                { "dimension_state", "Driving" }, { "expression_name", "p11" },
                { "geometry_refs", new List<string> { "line-1", "line-2" } }
            }});
            result.Add("bodies", M3BodyInventory());
            return result;
        }

        public Dictionary<string, object> InspectSketch(M2ModelBinding binding, string sketchId)
        {
            System.Threading.Interlocked.Increment(ref inspectionCalls);
            if (String.Equals(sketchId, "raise", StringComparison.Ordinal))
                throw new M2ModelException("MODEL_READ_FAILED", "fixture inspection failure");
            if (!String.Equals(sketchId, "sketch-1", StringComparison.Ordinal))
            {
                FakeM3Sketch m3Sketch;
                if (!m3Sketches.TryGetValue(sketchId, out m3Sketch))
                    throw new M2ModelException("SKETCH_NOT_FOUND", "fixture sketch was not found");
                Dictionary<string, object> m3SketchView = new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "journal_id", m3Sketch.Id }, { "status", "FullyConstrained" },
                    { "degrees_of_freedom", "0" }, { "constraints", M3ConstraintViews(m3Sketch) },
                    { "geometry", M3GeometryViews(m3Sketch) }
                };
                return new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "sketch", m3SketchView }, { "part_path", binding.PartPath }
                };
            }
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result.Add("sketch", new Dictionary<string, object> {
                { "journal_id", "sketch-1" }, { "status", "FullyConstrained" }, { "degrees_of_freedom", "0" }
            });
            result.Add("part_path", binding.PartPath);
            return result;
        }

        public Dictionary<string, object> QueryFaces(M2ModelBinding binding, string revisionId,
            string selector, string bodyId)
        {
            System.Threading.Interlocked.Increment(ref m4FaceQueryCalls);
            if (!String.Equals(revisionId, RevisionId, StringComparison.Ordinal))
                throw new M2ModelException("STALE_REVISION", "fixture face query revision changed");
            System.Threading.Volatile.Write(ref m4LastSelector, selector);
            System.Threading.Volatile.Write(ref m4LastBodyId, bodyId);
            bool ambiguous = String.Equals(selector, "ambiguous", StringComparison.Ordinal);
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "selection_status", ambiguous ? "ambiguous" : "unique" },
                { "selector_normalized", selector }, { "candidate_count", ambiguous ? 2 : 1 },
                { "candidates", new List<object>() }, { "clarification_required", ambiguous },
                { "revision_id", revisionId }, { "body_id", bodyId }
            };
        }

        public M2ParameterPlanSnapshot PrepareParameterEdit(M2ModelBinding binding, M2ParameterEditRequest request)
        {
            System.Threading.Interlocked.Increment(ref prepareCalls);
            string currentRevision = RevisionId;
            if (!String.Equals(request.RevisionId, currentRevision, StringComparison.Ordinal))
                throw new M2ModelException("REVISION_MISMATCH", "fixture revision changed before plan preparation");
            if (!String.Equals(request.Unit, "mm", StringComparison.Ordinal))
                throw new M2ModelException("UNIT_MISMATCH", "fixture driving dimension is measured in mm");
            if (String.Equals(request.ConstraintId, "non-driving", StringComparison.Ordinal))
                throw new M2ModelException("NON_DRIVING_DIMENSION", "fixture constraint is reference-only");
            if (String.Equals(request.ConstraintId, "unsupported", StringComparison.Ordinal))
                throw new M2ModelException("UNSUPPORTED_DIMENSION_TYPE", "fixture dimension type is not supported for editing");
            if (String.Equals(request.ConstraintId, "ambiguous", StringComparison.Ordinal))
                throw new M2ModelException("AMBIGUOUS_CONSTRAINT", "fixture constraint id resolves to more than one dimension");
            FakeM3Constraint m3Constraint;
            if (m3Constraints.TryGetValue(request.ConstraintId, out m3Constraint))
            {
                if (!String.Equals(request.SketchId, m3Constraint.Sketch.Id, StringComparison.Ordinal))
                    throw new M2ModelException("CONSTRAINT_NOT_FOUND", "fixture constraint is not in the identified M3 sketch");
                if (!m3Constraint.IsDriving)
                    throw new M2ModelException("NON_DRIVING_DIMENSION", "fixture M3 constraint is not Driving");
                string dimensionValue = M3NumberText(m3Constraint.Value);
                return new M2ParameterPlanSnapshot {
                    RevisionId = currentRevision,
                    SketchId = request.SketchId,
                    ConstraintId = request.ConstraintId,
                    DimensionType = m3Constraint.Type,
                    DimensionState = "Driving",
                    ExpressionName = m3Constraint.ExpressionName,
                    ExpressionRightHandSide = dimensionValue,
                    CurrentValue = dimensionValue,
                    Unit = "mm",
                    GeometryRefs = new List<string>(m3Constraint.GeometryRefs),
                    AdapterState = m3Constraint
                };
            }
            if (!String.Equals(request.ConstraintId, "constraint-1", StringComparison.Ordinal))
                throw new M2ModelException("CONSTRAINT_NOT_FOUND", "fixture constraint was not found in the identified sketch");

            string value = CurrentValue;
            return new M2ParameterPlanSnapshot {
                RevisionId = currentRevision,
                SketchId = request.SketchId,
                ConstraintId = request.ConstraintId,
                DimensionType = "HorizontalDim",
                DimensionState = "Driving",
                ExpressionName = "p11",
                ExpressionRightHandSide = value,
                CurrentValue = value,
                Unit = "mm",
                GeometryRefs = new List<string> { "line-1", "line-2" },
                AdapterState = this
            };
        }

        public M2ParameterExecutionResult ExecuteParameterEdit(M2ModelBinding binding,
            M2ParameterPlanSnapshot prepared, string requestedValue, string requestedUnit)
        {
            System.Threading.Interlocked.Increment(ref executionCalls);
            operationStarted.Set();
            if (System.Threading.Interlocked.CompareExchange(ref blockExecution, 0, 0) != 0 && !releaseOperation.WaitOne(15000))
                throw new M2ModelException("FIXTURE_GATE_TIMEOUT", "fixture operation release gate timed out");

            FakeM3Constraint m3Constraint = prepared.AdapterState as FakeM3Constraint;
            if (m3Constraint != null)
                return ExecuteM3DimensionEdit(binding, prepared, m3Constraint, requestedValue, requestedUnit);

            string before = CurrentValue;
            string currentRevision = RevisionId;
            if (!String.Equals(currentRevision, prepared.RevisionId, StringComparison.Ordinal) ||
                !String.Equals(before, prepared.CurrentValue, StringComparison.Ordinal))
            {
                if (!String.Equals(currentRevision, prepared.RevisionId, StringComparison.Ordinal))
                    binding.RevisionId = currentRevision;
                return new M2ParameterExecutionResult {
                    State = "UNCHANGED",
                    RevisionId = currentRevision,
                    ErrorCode = "REVISION_MISMATCH",
                    ErrorMessage = "fixture revision or parameter value changed after plan preparation",
                    Evidence = new Dictionary<string, object> {
                        { "changed", false },
                        { "before", ParameterEvidence(before) }, { "after", ParameterEvidence(before) },
                        { "before_rhs", before }, { "before_value", before }, { "before_unit", "mm" },
                        { "after_rhs", before }, { "after_value", before }, { "after_unit", "mm" },
                        { "geometry_before", new List<string> { "line-1:end-x=" + before + "mm", "line-2:origin-fixed" } },
                        { "geometry_after", new List<string> { "line-1:end-x=" + before + "mm", "line-2:origin-fixed" } },
                        { "geometry_changed", false }, { "readback_verified", false }, { "write_attempted", false }
                    }
                };
            }

            if (System.Threading.Interlocked.CompareExchange(ref rolledBackAfterWrite, 0, 0) != 0)
            {
                System.Threading.Interlocked.Exchange(ref rolledBackAfterWrite, 0);
                System.Threading.Interlocked.Increment(ref writeAttemptCount);
                System.Threading.Volatile.Write(ref currentValue, requestedValue);
                System.Threading.Volatile.Write(ref currentValue, before);
                System.Threading.Interlocked.Increment(ref rollbackCount);
                List<string> geometry = new List<string> { "line-1:end-x=" + before + "mm", "line-2:origin-fixed" };
                return new M2ParameterExecutionResult {
                    State = "ROLLED_BACK",
                    RevisionId = currentRevision,
                    ErrorCode = "EDIT_NOT_VERIFIED",
                    ErrorMessage = "fixture restored the prior parameter and geometry after update failure",
                    Evidence = new Dictionary<string, object> {
                        { "before", ParameterEvidence(before) }, { "after", ParameterEvidence(before) },
                        { "before_rhs", before }, { "before_value", before }, { "before_unit", "mm" },
                        { "after_rhs", before }, { "after_value", before }, { "after_unit", "mm" },
                        { "geometry_before", geometry }, { "geometry_after", new List<string>(geometry) },
                        { "geometry_changed", false }, { "write_attempted", true },
                        { "update_error_count", 1 }, { "readback_verified", false },
                        { "rollback_readback_verified", true }
                    }
                };
            }

            if (System.Threading.Interlocked.Exchange(ref rollbackUnverifiedAfterWrite, 0) != 0)
            {
                System.Threading.Interlocked.Increment(ref writeAttemptCount);
                System.Threading.Volatile.Write(ref currentValue, requestedValue);
                expectedValues[prepared] = requestedValue;
                System.Threading.Interlocked.Increment(ref mutationCount);
                System.Threading.Interlocked.Increment(ref rollbackCount);
                currentRevision = System.Threading.Interlocked.Increment(ref revisionNumber).ToString(CultureInfo.InvariantCulture);
                binding.RevisionId = currentRevision;
                List<string> geometry = new List<string> {
                    "line-1:end-x=" + requestedValue + "mm", "line-2:origin-fixed"
                };
                return new M2ParameterExecutionResult {
                    State = "UNKNOWN", RevisionId = currentRevision,
                    ErrorCode = "ROLLBACK_UNVERIFIED",
                    ErrorMessage = "fixture applied the edit but could not verify restoration after the update failure",
                    Evidence = new Dictionary<string, object> {
                        { "changed", null },
                        { "before", ParameterEvidence(before) }, { "after", ParameterEvidence(requestedValue) },
                        { "before_rhs", before }, { "before_value", before }, { "before_unit", "mm" },
                        { "after_rhs", requestedValue }, { "after_value", requestedValue }, { "after_unit", "mm" },
                        { "geometry_before", new List<string> { "line-1:end-x=" + before + "mm", "line-2:origin-fixed" } },
                        { "geometry_after", geometry }, { "geometry_changed", true },
                        { "write_attempted", true }, { "update_error_count", 1 },
                        { "rollback_attempted", true }, { "rollback_readback_verified", false },
                        { "rollback_error", "FIXTURE_UNDO_UNVERIFIED" }, { "readback_verified", false },
                        { "revision_id", currentRevision }
                    }
                };
            }

            System.Threading.Interlocked.Increment(ref writeAttemptCount);
            System.Threading.Volatile.Write(ref currentValue, requestedValue);
            expectedValues[prepared] = requestedValue;
            System.Threading.Interlocked.Increment(ref mutationCount);
            currentRevision = System.Threading.Interlocked.Increment(ref revisionNumber).ToString();
            bool unknown = System.Threading.Interlocked.CompareExchange(ref unknownAfterMutation, 0, 0) != 0;
            string geometryBefore = "line-1:end-x=" + before + "mm";
            string geometryAfter = "line-1:end-x=" + requestedValue + "mm";
            return new M2ParameterExecutionResult {
                State = unknown ? "UNKNOWN" : "APPLIED",
                RevisionId = currentRevision,
                ErrorCode = unknown ? "UNKNOWN_RESULT" : null,
                ErrorMessage = unknown ? "fixture applied the edit but could not verify its result" : null,
                Evidence = new Dictionary<string, object> {
                    { "changed", unknown ? (object)null : true },
                    { "before", ParameterEvidence(before) }, { "after", ParameterEvidence(requestedValue) },
                    { "before_rhs", before }, { "before_value", before }, { "before_unit", "mm" },
                    { "after_rhs", requestedValue }, { "after_value", requestedValue }, { "after_unit", "mm" },
                    { "geometry_before", new List<string> { geometryBefore, "line-2:origin-fixed" } },
                    { "geometry_after", new List<string> { geometryAfter, "line-2:origin-fixed" } },
                    { "geometry_changed", true }, { "write_attempted", true },
                    { "update_error_count", unknown ? 1 : 0 },
                    { "readback_verified", !unknown },
                    { "revision_id", currentRevision }
                }
            };
        }

        public M2PartVerificationResult VerifyParameterEdit(M2ModelBinding binding,
            M2ParameterPlanSnapshot prepared)
        {
            System.Threading.Interlocked.Increment(ref verificationCalls);
            FakeM3Constraint m3Constraint = prepared.AdapterState as FakeM3Constraint;
            if (m3Constraint != null)
                return VerifyM3DimensionEdit(binding, prepared, m3Constraint);
            string expected;
            if (!expectedValues.TryGetValue(prepared, out expected)) expected = prepared.CurrentValue;
            string actual = CurrentValue;
            string sketchId = prepared.SketchId;
            string constraintId = prepared.ConstraintId;
            List<string> geometryRefs = new List<string>(prepared.GeometryRefs ?? new List<string>());
            bool expressionMatches = String.Equals(actual, expected, StringComparison.Ordinal);
            bool geometryMatches = String.Equals(actual, expected, StringComparison.Ordinal);

            Dictionary<string, object> expressionEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "sketch_id", sketchId }, { "constraint_id", constraintId },
                { "expression_name", prepared.ExpressionName }, { "expected_value", expected },
                { "actual_rhs", actual }, { "actual_value", actual }, { "unit", prepared.Unit }
            };
            Dictionary<string, object> geometryEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "sketch_id", sketchId }, { "constraint_id", constraintId },
                { "geometry_refs", geometryRefs }, { "expected_value", expected },
                { "actual_value", actual }, { "geometry", "line-1:end-x=" + actual + "mm" }
            };
            Dictionary<string, object> historyEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "sketch_id", sketchId }, { "feature_id", "feature-1" }, { "feature_type", "EXTRUDE" },
                { "parents", new List<string>() }, { "expressions", new List<string> { prepared.ExpressionName } },
                { "feature_count", 1 }
            };
            Dictionary<string, object> associationEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "sketch_id", sketchId }, { "constraint_id", constraintId },
                { "dimension_state", prepared.DimensionState }, { "expression_name", prepared.ExpressionName },
                { "geometry_refs", geometryRefs }
            };
            Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "expression", VerificationCheck(expressionMatches, expressionEvidence) },
                { "geometry", VerificationCheck(geometryMatches, geometryEvidence) },
                { "history", VerificationCheck(true, historyEvidence) },
                { "associations", VerificationCheck(true, associationEvidence) }
            };
            bool verified = expressionMatches && geometryMatches;
            return new M2PartVerificationResult {
                Verified = verified,
                RevisionId = binding.RevisionId,
                Checks = checks,
                ErrorCode = verified ? null : "PART_VERIFICATION_FAILED",
                ErrorMessage = verified ? null : "fixture model expression or geometry differs from the completed operation"
            };
        }

        public M3PlanSnapshot PrepareSketch(M2ModelBinding binding, M3SketchRequest request)
        {
            System.Threading.Interlocked.Increment(ref m3SketchPrepareCalls);
            if (!String.Equals(request.RevisionId, RevisionId, StringComparison.Ordinal))
                throw new M2ModelException("STALE_REVISION", "fixture revision changed before sketch planning");

            int number = System.Threading.Interlocked.Increment(ref m3SketchNumber);
            FakeM3Sketch sketch = new FakeM3Sketch();
            sketch.Id = "m3-sketch-" + number.ToString(CultureInfo.InvariantCulture);
            sketch.FeatureId = "m3-sketch-feature-" + number.ToString(CultureInfo.InvariantCulture);
            sketch.Plane = request.Plane;
            sketch.Origin = request.OriginMm == null ? null : (double[])request.OriginMm.Clone();
            sketch.SupportFaceSelector = request.SupportFaceSelector;
            sketch.SupportBodyId = request.SupportBodyId;
            sketch.FaceOriginOffsetMm = request.FaceOriginOffsetMm == null ? null : (double[])request.FaceOriginOffsetMm.Clone();
            List<object> publicGeometries = new List<object>();
            List<object> drivingDimensions = new List<object>();
            foreach (M3SketchGeometryRequest geometryRequest in request.Geometries)
            {
                FakeM3Geometry geometry = CopyM3Geometry(sketch.Id, geometryRequest);
                sketch.Geometries.Add(geometry);
                publicGeometries.Add(M3GeometryView(geometry));
                if (String.Equals(geometry.Type, "line", StringComparison.Ordinal))
                {
                    double dx = geometry.End[0] - geometry.Start[0];
                    double dy = geometry.End[1] - geometry.Start[1];
                    if (Math.Abs(dy) < 1e-9)
                        AddM3Constraint(sketch, geometry, "length_x", "HorizontalDim", Math.Abs(dx),
                            new List<string> { geometry.JournalId + "|StartPoint|0", geometry.JournalId + "|EndPoint|0" }, drivingDimensions);
                    else if (Math.Abs(dx) < 1e-9)
                        AddM3Constraint(sketch, geometry, "length_y", "VerticalDim", Math.Abs(dy),
                            new List<string> { geometry.JournalId + "|StartPoint|0", geometry.JournalId + "|EndPoint|0" }, drivingDimensions);
                    else
                    {
                        AddM3Constraint(sketch, geometry, "length_x", "HorizontalDim", Math.Abs(dx),
                            new List<string> { geometry.JournalId + "|StartPoint|0", geometry.JournalId + "|EndPoint|0" }, drivingDimensions);
                        AddM3Constraint(sketch, geometry, "length_y", "VerticalDim", Math.Abs(dy),
                            new List<string> { geometry.JournalId + "|StartPoint|0", geometry.JournalId + "|EndPoint|0" }, drivingDimensions);
                    }
                }
                else if (String.Equals(geometry.Type, "rectangle", StringComparison.Ordinal))
                {
                    AddM3Constraint(sketch, geometry, "width", "HorizontalDim", geometry.Width,
                        new List<string> { geometry.JournalId + "-top|StartPoint|0", geometry.JournalId + "-top|EndPoint|0" }, drivingDimensions);
                    AddM3Constraint(sketch, geometry, "height", "VerticalDim", geometry.Height,
                        new List<string> { geometry.JournalId + "-side|StartPoint|0", geometry.JournalId + "-side|EndPoint|0" }, drivingDimensions);
                }
                else if (String.Equals(geometry.Type, "circle", StringComparison.Ordinal))
                {
                    AddM3Constraint(sketch, geometry, "diameter", "DiameterDim", geometry.Diameter,
                        new List<string> { geometry.JournalId + "|None|0" }, drivingDimensions);
                    AddM3Constraint(sketch, geometry, "center_x", "HorizontalDim", Math.Abs(geometry.Center[0]),
                        new List<string> { geometry.JournalId + "|ArcCenter|0", sketch.Id + "-origin|None|0" }, drivingDimensions);
                    AddM3Constraint(sketch, geometry, "center_y", "VerticalDim", Math.Abs(geometry.Center[1]),
                        new List<string> { geometry.JournalId + "|ArcCenter|0", sketch.Id + "-origin|None|0" }, drivingDimensions);
                }
            }

            Dictionary<string, object> publicView = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "kind", "create_sketch" }, { "sketch_id", sketch.Id }, { "feature_id", sketch.FeatureId },
                { "geometries", publicGeometries }, { "driving_dimensions", drivingDimensions },
                { "sketch_status", "FullyConstrained" }, { "dof", 0 }, { "degrees_of_freedom", 0 }
            };
            if (!String.IsNullOrWhiteSpace(sketch.SupportFaceSelector))
            {
                publicView.Add("support_face_selector", sketch.SupportFaceSelector);
                publicView.Add("support_body_id", sketch.SupportBodyId);
                publicView.Add("face_origin_offset_mm", new List<double>(sketch.FaceOriginOffsetMm));
            }
            else
            {
                publicView.Add("plane", sketch.Plane);
                publicView.Add("origin_mm", new List<double>(sketch.Origin));
            }
            FakeM3PlanState state = new FakeM3PlanState {
                Kind = "create_sketch", FeatureId = sketch.FeatureId, Sketch = sketch
            };
            foreach (FakeM3Body body in SortedM3Bodies()) state.NonTargetBefore.Add(M3BodyView(body));
            return new M3PlanSnapshot {
                Kind = state.Kind, RevisionId = RevisionId, SketchId = sketch.Id,
                FeatureId = sketch.FeatureId, PublicView = publicView, AdapterState = state
            };
        }

        private FakeM3Geometry CopyM3Geometry(string sketchId, M3SketchGeometryRequest request)
        {
            FakeM3Geometry geometry = new FakeM3Geometry {
                Id = request.Id,
                JournalId = "m3-geometry-" + sketchId + "-" + request.Id,
                Type = request.Type,
                Start = request.StartMm == null ? null : (double[])request.StartMm.Clone(),
                End = request.EndMm == null ? null : (double[])request.EndMm.Clone(),
                Corner = request.CornerMm == null ? null : (double[])request.CornerMm.Clone(),
                Center = request.CenterMm == null ? null : (double[])request.CenterMm.Clone(),
                Width = request.WidthMm,
                Height = request.HeightMm,
                Diameter = request.DiameterMm
            };
            return geometry;
        }

        private void AddM3Constraint(FakeM3Sketch sketch, FakeM3Geometry geometry, string role,
            string type, double value, List<string> geometryRefs, List<object> publicDimensions)
        {
            int number = System.Threading.Interlocked.Increment(ref m3ConstraintNumber);
            FakeM3Constraint constraint = new FakeM3Constraint {
                Id = "m3-constraint-" + number.ToString(CultureInfo.InvariantCulture),
                GeometryId = geometry.Id,
                Role = role,
                Type = type,
                ExpressionName = "m3_expr_" + number.ToString(CultureInfo.InvariantCulture),
                Value = value,
                IsDriving = true,
                Sketch = sketch,
                Geometry = geometry,
                GeometryRefs = new List<string>(geometryRefs)
            };
            sketch.Constraints.Add(constraint);
            publicDimensions.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                { "geometry_id", geometry.Id }, { "role", role }, { "constraint_type", type },
                { "value", value }, { "unit", "mm" },
                { "geometry_refs_by_role", new List<string>(geometryRefs) }
            });
        }

        public M3PlanSnapshot PrepareThread(M2ModelBinding binding, M7ThreadRequest request)
        {
            return PrepareM7Fixture(binding, request.RevisionId, request.TargetBodyId, "create_thread",
                new Dictionary<string, object> { { "pitch_mm", request.PitchMm }, { "major_diameter_mm", request.MajorDiameterMm },
                    { "minor_diameter_mm", request.MinorDiameterMm }, { "handedness", request.Handedness }, { "direction", request.Direction },
                    { "axis", request.Axis }, { "axis_origin_mm", new List<double>(request.AxisOriginMm) },
                    { "start_mm", request.StartMm }, { "length_mm", request.LengthMm }, { "profile", "external_single_start_60deg_detailed" } });
        }

        public M3PlanSnapshot PrepareHelicalGroove(M2ModelBinding binding, M7HelicalGrooveRequest request)
        {
            return PrepareM7Fixture(binding, request.RevisionId, request.TargetBodyId, "create_helical_groove",
                new Dictionary<string, object> { { "lead_mm", request.LeadMm }, { "root_diameter_mm", request.RootDiameterMm },
                    { "reference_diameter_mm", request.ReferenceDiameterMm }, { "outer_diameter_mm", request.OuterDiameterMm },
                    { "space_width_mm", request.SpaceWidthMm }, { "flank_angle_deg", request.FlankAngleDeg }, { "phase_deg", request.PhaseDeg },
                    { "handedness", request.Handedness }, { "axis", request.Axis }, { "axis_origin_mm", new List<double>(request.AxisOriginMm) },
                    { "start_mm", request.StartMm }, { "length_mm", request.LengthMm } });
        }

        private M3PlanSnapshot PrepareM7Fixture(M2ModelBinding binding, string revision, string bodyId, string kind,
            Dictionary<string, object> definition)
        {
            if (revision != RevisionId) throw new M2ModelException("STALE_REVISION", "fixture revision changed");
            FakeM3Body target;
            if (!m3Bodies.TryGetValue(bodyId, out target)) throw new M2ModelException("TARGET_BODY_NOT_FOUND", "fixture helical body not found");
            string feature = "m7-feature-" + System.Threading.Interlocked.Increment(ref m3FeatureNumber).ToString(CultureInfo.InvariantCulture);
            FakeM3PlanState state = new FakeM3PlanState { Kind = kind, FeatureId = feature, BodyId = bodyId,
                TargetBodyId = bodyId, TargetBefore = M3BodyView(target), NativeDefinition = definition };
            foreach (FakeM3Body body in SortedM3Bodies())
                if (body.Id != bodyId) state.NonTargetBefore.Add(M3BodyView(body));
            return new M3PlanSnapshot { Kind = kind, RevisionId = revision, BodyId = bodyId, FeatureId = feature,
                PublicView = new Dictionary<string, object> { { "kind", kind }, { "body_id", bodyId }, { "parameters", definition } }, AdapterState = state };
        }

        public M3PlanSnapshot PrepareExtrude(M2ModelBinding binding, M3ExtrudeRequest request)
        {
            System.Threading.Interlocked.Increment(ref m3ExtrudePrepareCalls);
            if (!String.Equals(request.RevisionId, RevisionId, StringComparison.Ordinal))
                throw new M2ModelException("STALE_REVISION", "fixture revision changed before extrusion planning");
            FakeM3Sketch sketch;
            if (!m3Sketches.TryGetValue(request.SketchId, out sketch))
                throw new M2ModelException("SKETCH_NOT_FOUND", "fixture extrusion sketch was not found");

            FakeM3Body target = null;
            if (!String.Equals(request.Mode, "create", StringComparison.Ordinal))
            {
                if (String.IsNullOrEmpty(request.TargetBodyId) || !m3Bodies.TryGetValue(request.TargetBodyId, out target))
                    throw new M2ModelException("TARGET_BODY_NOT_FOUND", "fixture boolean target body was not found");
            }

            int number = System.Threading.Interlocked.Increment(ref m3FeatureNumber);
            string featureId = "m3-extrude-feature-" + number.ToString(CultureInfo.InvariantCulture);
            string bodyId = String.Equals(request.Mode, "create", StringComparison.Ordinal)
                ? "m3-body-" + System.Threading.Interlocked.Increment(ref m3BodyNumber).ToString(CultureInfo.InvariantCulture)
                : request.TargetBodyId;
            Dictionary<string, object> targetBefore = target == null ? null : M3BodyView(target);
            List<Dictionary<string, object>> nonTargetBefore = new List<Dictionary<string, object>>();
            foreach (FakeM3Body body in SortedM3Bodies())
                if (target == null || !String.Equals(body.Id, target.Id, StringComparison.Ordinal))
                    nonTargetBefore.Add(M3BodyView(body));

            FakeM3PlanState state = new FakeM3PlanState {
                Kind = "create_extrude", FeatureId = featureId, BodyId = bodyId, Sketch = sketch,
                Extrude = request, TargetBodyId = target == null ? null : target.Id,
                TargetBefore = targetBefore, NonTargetBefore = nonTargetBefore
            };
            List<object> nonTargetView = new List<object>();
            foreach (Dictionary<string, object> body in nonTargetBefore) nonTargetView.Add(body);
            Dictionary<string, object> publicView = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "kind", state.Kind }, { "sketch_id", sketch.Id }, { "feature_id", featureId },
                { "body_id", bodyId }, { "mode", request.Mode }, { "direction", request.Direction },
                { "extent", request.Extent }, { "depth_mm", request.Extent == "finite" ? (object)request.DepthMm : null },
                { "target_body_id", request.TargetBodyId }, { "target_body_before", targetBefore },
                { "non_target_bodies_before", nonTargetView },
                { "profile_area_mm2", M3ProfileArea(sketch) }
            };
            return new M3PlanSnapshot {
                Kind = state.Kind, RevisionId = RevisionId, SketchId = sketch.Id,
                FeatureId = featureId, BodyId = bodyId, PublicView = publicView, AdapterState = state
            };
        }

        public M3ExecutionResult ExecutePlan(M2ModelBinding binding, M3PlanSnapshot prepared)
        {
            System.Threading.Interlocked.Increment(ref m3ExecutionCalls);
            operationStarted.Set();
            if (System.Threading.Interlocked.CompareExchange(ref blockExecution, 0, 0) != 0 && !releaseOperation.WaitOne(15000))
                throw new M2ModelException("FIXTURE_GATE_TIMEOUT", "fixture M3 operation release gate timed out");
            FakeM3PlanState state = prepared == null ? null : prepared.AdapterState as FakeM3PlanState;
            if (state == null) throw new M2ModelException("INVALID_PLAN", "fixture M3 plan state is unavailable");
            if (state.Executed) throw new M2ModelException("PLAN_ALREADY_USED", "fixture M3 plan was already executed");
            state.Executed = true;

            bool rollback = System.Threading.Interlocked.Exchange(ref rolledBackAfterWrite, 0) != 0;
            bool unknown = !rollback && System.Threading.Interlocked.Exchange(ref unknownAfterMutation, 0) != 0;
            if (rollback)
            {
                System.Threading.Interlocked.Increment(ref rollbackCount);
                Dictionary<string, object> rejected = M3ExecutionView(state, prepared, "ROLLED_BACK", RevisionId);
                rejected["changed"] = false;
                rejected["mutation_state"] = "ROLLED_BACK";
                return new M3ExecutionResult {
                    State = "ROLLED_BACK", RevisionId = RevisionId, Result = rejected,
                    ErrorCode = "EDIT_NOT_VERIFIED", ErrorMessage = "fixture restored the prior body and sketch inventory"
                };
            }

            if (String.Equals(state.Kind, "create_sketch", StringComparison.Ordinal))
                ApplyM3Sketch(state.Sketch);
            else if (state.NativeDefinition != null)
            {
                m3Bodies[state.BodyId].Volume *= 0.98;
                m3Features.Add(new FakeM3Feature { Id = state.FeatureId, Kind = state.Kind == "create_thread" ? "THREADS" : "SKIN", BodyId = state.BodyId });
            }
            else
                ApplyM3Extrude(state);
            state.ExpectedUnknown = unknown;
            string revision = System.Threading.Interlocked.Increment(ref revisionNumber).ToString(CultureInfo.InvariantCulture);
            binding.RevisionId = revision;
            System.Threading.Interlocked.Increment(ref m3MutationCount);
            Dictionary<string, object> result = M3ExecutionView(state, prepared, unknown ? "UNKNOWN" : "APPLIED", revision);
            result["changed"] = unknown ? (object)null : true;
            result["mutation_state"] = unknown ? "UNKNOWN" : "APPLIED";
            return new M3ExecutionResult {
                State = unknown ? "UNKNOWN" : "APPLIED", RevisionId = revision, Result = result,
                ErrorCode = unknown ? "UNKNOWN_RESULT" : null,
                ErrorMessage = unknown ? "fixture applied the M3 operation but could not confirm its result" : null
            };
        }

        private void ApplyM3Sketch(FakeM3Sketch sketch)
        {
            m3Sketches.Add(sketch.Id, sketch);
            foreach (FakeM3Constraint constraint in sketch.Constraints) m3Constraints.Add(constraint.Id, constraint);
            m3Features.Add(new FakeM3Feature { Id = sketch.FeatureId, Kind = "SKETCH", SketchId = sketch.Id });
        }

        private void ApplyM3Extrude(FakeM3PlanState state)
        {
            double depth = state.Extrude.Extent == "through_all" ? 100.0 : state.Extrude.DepthMm;
            double volume = M3ProfileArea(state.Sketch) * depth;
            double[] bbox = M3ExtrudedBounds(state.Sketch, depth, state.Extrude.Direction);
            if (String.Equals(state.Extrude.Mode, "create", StringComparison.Ordinal))
            {
                m3Bodies.Add(state.BodyId, new FakeM3Body {
                    Id = state.BodyId, IsSolid = true, Volume = volume, Bbox = bbox
                });
            }
            else
            {
                FakeM3Body target;
                if (!m3Bodies.TryGetValue(state.TargetBodyId, out target))
                    throw new M2ModelException("TARGET_BODY_NOT_FOUND", "fixture boolean target disappeared before execution");
                if (String.Equals(state.Extrude.Mode, "unite", StringComparison.Ordinal))
                {
                    target.Volume += volume;
                    target.Bbox = UnionBounds(target.Bbox, bbox);
                }
                else
                    target.Volume = Math.Max(1.0, target.Volume - volume);
            }
            m3Features.Add(new FakeM3Feature {
                Id = state.FeatureId, Kind = "EXTRUDE", SketchId = state.Sketch.Id, BodyId = state.BodyId
            });
        }

        private Dictionary<string, object> M3ExecutionView(FakeM3PlanState state, M3PlanSnapshot prepared,
            string mutationState, string revision)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "kind", state.Kind }, { "revision_id", revision }, { "feature_id", state.FeatureId },
                { "sketch_id", state.Sketch == null ? null : state.Sketch.Id },
                { "body_id", state.BodyId }, { "constraint_ids", M3ConstraintIds(state.Sketch) },
                { "driving_dimensions", M3DrivingDimensionViews(state.Sketch) },
                { "body_inventory", M3BodyInventory() }, { "sketch_status", "FullyConstrained" },
                { "dof", 0 }, { "degrees_of_freedom", 0 }, { "mutation_state", mutationState }
            };
            return result;
        }

        private static List<string> M3ConstraintIds(FakeM3Sketch sketch)
        {
            List<string> result = new List<string>();
            if (sketch != null)
                foreach (FakeM3Constraint constraint in sketch.Constraints) result.Add(constraint.Id);
            return result;
        }

        private static List<object> M3DrivingDimensionViews(FakeM3Sketch sketch)
        {
            List<object> result = new List<object>();
            if (sketch == null) return result;
            foreach (FakeM3Constraint constraint in sketch.Constraints)
                result.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "constraint_id", constraint.Id }, { "geometry_id", constraint.GeometryId },
                    { "role", constraint.Role }, { "constraint_type", constraint.Type },
                    { "value", constraint.Value }, { "unit", "mm" },
                    { "expression_name", constraint.ExpressionName },
                    { "geometry_refs", new List<string>(constraint.GeometryRefs) },
                    { "is_driving", constraint.IsDriving }
                });
            return result;
        }

        public M3VerificationResult VerifyPlan(M2ModelBinding binding, M3PlanSnapshot prepared)
        {
            System.Threading.Interlocked.Increment(ref m3VerificationCalls);
            FakeM3PlanState state = prepared == null ? null : prepared.AdapterState as FakeM3PlanState;
            if (state == null || !state.Executed || state.ExpectedUnknown)
                throw new M2ModelException("VERIFY_REQUIRES_EXECUTED_PLAN", "fixture M3 plan has no known completed execution");
            bool geometryMatch = true;
            bool constraintMatch = true;
            bool historyMatch = false;
            bool bodyMatch = true;
            if (String.Equals(state.Kind, "create_sketch", StringComparison.Ordinal))
            {
                FakeM3Sketch actual;
                geometryMatch = m3Sketches.TryGetValue(state.Sketch.Id, out actual) &&
                    M3GeometryViews(actual).Count == M3GeometryViews(state.Sketch).Count;
                constraintMatch = geometryMatch && state.Sketch.Constraints.Count > 0 &&
                    state.Sketch.Constraints.TrueForAll(delegate(FakeM3Constraint c) { return c.IsDriving; });
                historyMatch = m3Features.Exists(delegate(FakeM3Feature f) { return f.Id == state.FeatureId; });
                bodyMatch = M3NonTargetsUnchanged(state);
            }
            else
            {
                FakeM3Body actual;
                bodyMatch = m3Bodies.TryGetValue(state.BodyId, out actual) && actual.IsSolid && actual.Volume > 0.0;
                geometryMatch = bodyMatch;
                constraintMatch = state.NativeDefinition != null ? state.NativeDefinition.Count > 0 : state.Sketch.Constraints.Count > 0;
                historyMatch = m3Features.Exists(delegate(FakeM3Feature f) { return f.Id == state.FeatureId; });
            }
            bool nonTargetsMatch = M3NonTargetsUnchanged(state);
            Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "geometry", VerificationCheck(geometryMatch, new Dictionary<string, object> {
                    { "sketch_id", state.Sketch == null ? null : state.Sketch.Id },
                    { "feature_id", state.FeatureId }, { "body_inventory", M3BodyInventory() } }) },
                { "constraints", VerificationCheck(constraintMatch, new Dictionary<string, object> {
                    { "driving_constraint_ids", M3ConstraintIds(state.Sketch) },
                    { "driving_dimensions", M3DrivingDimensionViews(state.Sketch) } }) },
                { "history", VerificationCheck(historyMatch, new Dictionary<string, object> {
                    { "feature_id", state.FeatureId }, { "feature_count", m3Features.Count } }) },
                { "bodies", VerificationCheck(bodyMatch, new Dictionary<string, object> {
                    { "body_inventory", M3BodyInventory() }, { "body_count", m3Bodies.Count } }) },
                { "non_target_bodies", VerificationCheck(nonTargetsMatch, new Dictionary<string, object> {
                    { "before", state.NonTargetBefore }, { "after", M3CurrentNonTargets(state) } }) }
            };
            bool verified = geometryMatch && constraintMatch && historyMatch && bodyMatch && nonTargetsMatch;
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "verified", verified }, { "revision_id", binding.RevisionId }, { "checks", checks }
            };
            return new M3VerificationResult {
                Verified = verified, RevisionId = binding.RevisionId, Checks = checks,
                Result = result,
                ErrorCode = verified ? null : "PART_VERIFICATION_FAILED",
                ErrorMessage = verified ? null : "fixture M3 geometry, constraint, history, or body observation mismatched"
            };
        }

        private List<FakeM3Body> SortedM3Bodies()
        {
            List<FakeM3Body> result = new List<FakeM3Body>(m3Bodies.Values);
            result.Sort(delegate(FakeM3Body a, FakeM3Body b) { return StringComparer.Ordinal.Compare(a.Id, b.Id); });
            return result;
        }

        private List<object> M3BodyInventory()
        {
            List<object> result = new List<object>();
            foreach (FakeM3Body body in SortedM3Bodies()) result.Add(M3BodyView(body));
            return result;
        }

        private static Dictionary<string, object> M3BodyView(FakeM3Body body)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "journal_id", body.Id }, { "is_solid", body.IsSolid },
                { "volume_mm3", body.Volume }, { "bbox_mm", new List<double>(body.Bbox) }
            };
        }

        private List<Dictionary<string, object>> M3CurrentNonTargets(FakeM3PlanState state)
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            foreach (FakeM3Body body in SortedM3Bodies())
            {
                if (!String.IsNullOrEmpty(state.TargetBodyId) && String.Equals(body.Id, state.TargetBodyId, StringComparison.Ordinal))
                    continue;
                if (String.Equals(state.Kind, "create_extrude", StringComparison.Ordinal) &&
                    String.Equals(state.Extrude.Mode, "create", StringComparison.Ordinal) &&
                    String.Equals(body.Id, state.BodyId, StringComparison.Ordinal))
                    continue;
                result.Add(M3BodyView(body));
            }
            return result;
        }

        private bool M3NonTargetsUnchanged(FakeM3PlanState state)
        {
            List<Dictionary<string, object>> actual = M3CurrentNonTargets(state);
            if (actual.Count != state.NonTargetBefore.Count) return false;
            for (int i = 0; i < actual.Count; i++)
            {
                Dictionary<string, object> expected = state.NonTargetBefore[i];
                Dictionary<string, object> found = actual[i];
                if (!Object.Equals(expected["journal_id"], found["journal_id"]) ||
                    !Object.Equals(expected["is_solid"], found["is_solid"]) ||
                    !Object.Equals(expected["volume_mm3"], found["volume_mm3"])) return false;
                List<double> expectedBounds = expected["bbox_mm"] as List<double>;
                List<double> actualBounds = found["bbox_mm"] as List<double>;
                if (expectedBounds == null || actualBounds == null || expectedBounds.Count != actualBounds.Count) return false;
                for (int j = 0; j < expectedBounds.Count; j++)
                    if (!expectedBounds[j].Equals(actualBounds[j])) return false;
            }
            return true;
        }

        private static List<object> M3ConstraintViews(FakeM3Sketch sketch)
        {
            List<object> result = new List<object>();
            foreach (FakeM3Constraint constraint in sketch.Constraints)
            {
                result.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "constraint_type", constraint.Type }, { "class", "DIMENSIONAL" },
                    { "geometry_refs", new List<string>(constraint.GeometryRefs) },
                    { "is_driving", constraint.IsDriving }, { "dimension_state", "Driving" },
                    { "expression_name", constraint.ExpressionName },
                    { "value", M3NumberText(constraint.Value) }, { "journal_id", constraint.Id },
                    { "semantic_key", constraint.Type + "|" + String.Join("|", constraint.GeometryRefs.ToArray()) +
                        "|" + constraint.ExpressionName + "|" + M3NumberText(constraint.Value) }
                });
            }
            return result;
        }

        private static List<object> M3GeometryViews(FakeM3Sketch sketch)
        {
            List<object> result = new List<object>();
            foreach (FakeM3Geometry geometry in sketch.Geometries) result.Add(M3GeometryView(geometry));
            return result;
        }

        private static Dictionary<string, object> M3GeometryView(FakeM3Geometry geometry)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "id", geometry.Id }, { "journal_id", geometry.JournalId }, { "type", geometry.Type }
            };
            if (geometry.Start != null) result.Add("start_mm", new List<double>(geometry.Start));
            if (geometry.End != null) result.Add("end_mm", new List<double>(geometry.End));
            if (geometry.Corner != null) result.Add("corner_mm", new List<double>(geometry.Corner));
            if (geometry.Center != null) result.Add("center_mm", new List<double>(geometry.Center));
            if (geometry.Type == "rectangle")
            {
                result.Add("width_mm", geometry.Width);
                result.Add("height_mm", geometry.Height);
            }
            if (geometry.Type == "circle") result.Add("diameter_mm", geometry.Diameter);
            return result;
        }

        private static double M3ProfileArea(FakeM3Sketch sketch)
        {
            double area = 0.0;
            foreach (FakeM3Geometry geometry in sketch.Geometries)
            {
                if (geometry.Type == "rectangle") area += geometry.Width * geometry.Height;
                else if (geometry.Type == "circle") area += Math.PI * geometry.Diameter * geometry.Diameter / 4.0;
            }
            return area;
        }

        private static double[] M3ExtrudedBounds(FakeM3Sketch sketch, double depth, string direction)
        {
            double minU = Double.PositiveInfinity, minV = Double.PositiveInfinity;
            double maxU = Double.NegativeInfinity, maxV = Double.NegativeInfinity;
            foreach (FakeM3Geometry geometry in sketch.Geometries)
            {
                if (geometry.Type == "line")
                {
                    minU = Math.Min(minU, Math.Min(geometry.Start[0], geometry.End[0]));
                    minV = Math.Min(minV, Math.Min(geometry.Start[1], geometry.End[1]));
                    maxU = Math.Max(maxU, Math.Max(geometry.Start[0], geometry.End[0]));
                    maxV = Math.Max(maxV, Math.Max(geometry.Start[1], geometry.End[1]));
                }
                else if (geometry.Type == "rectangle")
                {
                    minU = Math.Min(minU, geometry.Corner[0]);
                    minV = Math.Min(minV, geometry.Corner[1]);
                    maxU = Math.Max(maxU, geometry.Corner[0] + geometry.Width);
                    maxV = Math.Max(maxV, geometry.Corner[1] + geometry.Height);
                }
                else if (geometry.Type == "circle")
                {
                    double radius = geometry.Diameter / 2.0;
                    minU = Math.Min(minU, geometry.Center[0] - radius);
                    minV = Math.Min(minV, geometry.Center[1] - radius);
                    maxU = Math.Max(maxU, geometry.Center[0] + radius);
                    maxV = Math.Max(maxV, geometry.Center[1] + radius);
                }
            }
            if (Double.IsInfinity(minU)) { minU = 0.0; maxU = 0.0; minV = 0.0; maxV = 0.0; }
            double[] bounds = new double[6];
            if (sketch.Plane == "XY")
            {
                bounds[0] = sketch.Origin[0] + minU; bounds[1] = sketch.Origin[1] + minV;
                bounds[3] = sketch.Origin[0] + maxU; bounds[4] = sketch.Origin[1] + maxV;
                bounds[2] = sketch.Origin[2]; bounds[5] = sketch.Origin[2] + (direction == "negative" ? -depth : depth);
                if (bounds[2] > bounds[5]) Swap(ref bounds[2], ref bounds[5]);
            }
            else if (sketch.Plane == "XZ")
            {
                bounds[0] = sketch.Origin[0] + minU; bounds[2] = sketch.Origin[2] + minV;
                bounds[3] = sketch.Origin[0] + maxU; bounds[5] = sketch.Origin[2] + maxV;
                bounds[1] = sketch.Origin[1]; bounds[4] = sketch.Origin[1] + (direction == "negative" ? depth : -depth);
                if (bounds[1] > bounds[4]) Swap(ref bounds[1], ref bounds[4]);
            }
            else
            {
                bounds[1] = sketch.Origin[1] + minU; bounds[2] = sketch.Origin[2] + minV;
                bounds[4] = sketch.Origin[1] + maxU; bounds[5] = sketch.Origin[2] + maxV;
                bounds[0] = sketch.Origin[0]; bounds[3] = sketch.Origin[0] + (direction == "negative" ? -depth : depth);
                if (bounds[0] > bounds[3]) Swap(ref bounds[0], ref bounds[3]);
            }
            return bounds;
        }

        private static void Swap(ref double left, ref double right)
        {
            double temp = left; left = right; right = temp;
        }

        private static double[] UnionBounds(double[] left, double[] right)
        {
            return new double[] {
                Math.Min(left[0], right[0]), Math.Min(left[1], right[1]), Math.Min(left[2], right[2]),
                Math.Max(left[3], right[3]), Math.Max(left[4], right[4]), Math.Max(left[5], right[5])
            };
        }

        private M2ParameterExecutionResult ExecuteM3DimensionEdit(M2ModelBinding binding,
            M2ParameterPlanSnapshot prepared, FakeM3Constraint constraint, string requestedValue, string requestedUnit)
        {
            string before = M3NumberText(constraint.Value);
            List<string> geometryBefore = M3GeometrySummary(constraint.Sketch);
            if (!String.Equals(requestedUnit, "mm", StringComparison.Ordinal) ||
                !String.Equals(binding.RevisionId, prepared.RevisionId, StringComparison.Ordinal) ||
                !String.Equals(before, prepared.CurrentValue, StringComparison.Ordinal))
            {
                return new M2ParameterExecutionResult {
                    State = "UNCHANGED", RevisionId = binding.RevisionId,
                    ErrorCode = !String.Equals(requestedUnit, "mm", StringComparison.Ordinal) ? "UNIT_MISMATCH" : "REVISION_MISMATCH",
                    ErrorMessage = "fixture M3 dimension or sketch revision changed after plan preparation",
                    Evidence = new Dictionary<string, object> {
                        { "changed", false }, { "before", ParameterEvidence(before) }, { "after", ParameterEvidence(before) },
                        { "before_rhs", before }, { "before_value", before }, { "before_unit", "mm" },
                        { "after_rhs", before }, { "after_value", before }, { "after_unit", "mm" },
                        { "geometry_before", geometryBefore }, { "geometry_after", new List<string>(geometryBefore) },
                        { "geometry_changed", false }, { "write_attempted", false }, { "readback_verified", false }
                    }
                };
            }
            double parsed;
            if (!Double.TryParse(requestedValue, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ||
                Double.IsNaN(parsed) || Double.IsInfinity(parsed) || parsed <= 0.0)
                throw new M2ModelException("INVALID_REQUEST", "fixture M3 Driving dimension value must be positive and finite");

            System.Threading.Interlocked.Increment(ref writeAttemptCount);
            constraint.Value = parsed;
            ApplyM3Dimension(constraint, parsed);
            System.Threading.Interlocked.Increment(ref mutationCount);
            System.Threading.Interlocked.Increment(ref m3MutationCount);
            string revision = System.Threading.Interlocked.Increment(ref revisionNumber).ToString(CultureInfo.InvariantCulture);
            binding.RevisionId = revision;
            string after = M3NumberText(constraint.Value);
            List<string> geometryAfter = M3GeometrySummary(constraint.Sketch);
            expectedValues[prepared] = after;
            Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "changed", true }, { "before", ParameterEvidence(before) }, { "after", ParameterEvidence(after) },
                { "before_rhs", before }, { "before_value", before }, { "before_unit", "mm" },
                { "after_rhs", after }, { "after_value", after }, { "after_unit", "mm" },
                { "geometry_before", geometryBefore }, { "geometry_after", geometryAfter },
                { "geometry_changed", true }, { "write_attempted", true },
                { "update_error_count", 0 }, { "readback_verified", true }, { "revision_id", revision }
            };
            return new M2ParameterExecutionResult { State = "APPLIED", RevisionId = revision, Evidence = evidence };
        }

        private M2PartVerificationResult VerifyM3DimensionEdit(M2ModelBinding binding,
            M2ParameterPlanSnapshot prepared, FakeM3Constraint constraint)
        {
            string expected;
            if (!expectedValues.TryGetValue(prepared, out expected)) expected = prepared.CurrentValue;
            string actual = M3NumberText(constraint.Value);
            bool matches = String.Equals(expected, actual, StringComparison.Ordinal);
            List<string> geometryRefs = new List<string>(constraint.GeometryRefs);
            Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "expression", VerificationCheck(matches, new Dictionary<string, object> {
                    { "sketch_id", constraint.Sketch.Id }, { "constraint_id", constraint.Id },
                    { "expression_name", constraint.ExpressionName }, { "expected_value", expected },
                    { "actual_rhs", actual }, { "actual_value", actual }, { "unit", "mm" } }) },
                { "geometry", VerificationCheck(matches, new Dictionary<string, object> {
                    { "sketch_id", constraint.Sketch.Id }, { "constraint_id", constraint.Id },
                    { "geometry_refs", geometryRefs }, { "expected_value", expected },
                    { "actual_value", actual }, { "geometry", M3GeometryViews(constraint.Sketch) } }) },
                { "history", VerificationCheck(true, new Dictionary<string, object> {
                    { "sketch_id", constraint.Sketch.Id }, { "feature_id", constraint.Sketch.FeatureId },
                    { "feature_type", "SKETCH" }, { "feature_count", m3Features.Count } }) },
                { "associations", VerificationCheck(true, new Dictionary<string, object> {
                    { "sketch_id", constraint.Sketch.Id }, { "constraint_id", constraint.Id },
                    { "dimension_state", "Driving" }, { "expression_name", constraint.ExpressionName },
                    { "geometry_refs", geometryRefs } }) }
            };
            return new M2PartVerificationResult {
                Verified = matches, RevisionId = binding.RevisionId, Checks = checks,
                ErrorCode = matches ? null : "PART_VERIFICATION_FAILED",
                ErrorMessage = matches ? null : "fixture M3 Driving value differs from its post-edit geometry"
            };
        }

        private static List<string> M3GeometrySummary(FakeM3Sketch sketch)
        {
            List<string> result = new List<string>();
            foreach (FakeM3Geometry geometry in sketch.Geometries)
                result.Add(geometry.Id + ":" + M2Json.CanonicalText(M3GeometryView(geometry)));
            return result;
        }

        private static void ApplyM3Dimension(FakeM3Constraint constraint, double value)
        {
            FakeM3Geometry geometry = constraint.Geometry;
            if (constraint.Role == "diameter") geometry.Diameter = value;
            else if (constraint.Role == "width") geometry.Width = value;
            else if (constraint.Role == "height") geometry.Height = value;
            else if (constraint.Role == "length_x")
            {
                double sign = geometry.End[0] >= geometry.Start[0] ? 1.0 : -1.0;
                geometry.End[0] = geometry.Start[0] + sign * value;
            }
            else if (constraint.Role == "length_y")
            {
                double sign = geometry.End[1] >= geometry.Start[1] ? 1.0 : -1.0;
                geometry.End[1] = geometry.Start[1] + sign * value;
            }
            else if (constraint.Role == "center_x") geometry.Center[0] = geometry.Center[0] < 0.0 ? -value : value;
            else if (constraint.Role == "center_y") geometry.Center[1] = geometry.Center[1] < 0.0 ? -value : value;
        }

        private static string M3NumberText(double value)
        {
            return value.ToString("G17", CultureInfo.InvariantCulture);
        }

        public M2SaveCopyResult SaveCopy(M2ModelBinding binding, string targetPath, bool reopen)
        {
            System.Threading.Interlocked.Increment(ref saveCopyCalls);
            saveStarted.Set();
            if (System.Threading.Interlocked.CompareExchange(ref blockSave, 0, 0) != 0 && !releaseSave.WaitOne(15000))
                throw new M2ModelException("FIXTURE_GATE_TIMEOUT", "fixture save release gate timed out");
            string sourcePath = binding.PartPath;
            string normalizedTarget;
            try { normalizedTarget = System.IO.Path.GetFullPath(targetPath); }
            catch (Exception) { throw new M2ModelException("INVALID_SAVE_TARGET", "fixture target path is not an absolute path"); }
            if (!System.IO.Path.IsPathRooted(normalizedTarget) ||
                !String.Equals(System.IO.Path.GetExtension(normalizedTarget), ".prt", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(normalizedTarget, sourcePath, StringComparison.OrdinalIgnoreCase))
                throw new M2ModelException("INVALID_SAVE_TARGET", "fixture save target must be a distinct absolute .prt path");
            lock (savedTargets)
            {
                if (savedTargets.Contains(normalizedTarget))
                    throw new M2ModelException("TARGET_EXISTS", "fixture save target already exists and cannot be overwritten");
                savedTargets.Add(normalizedTarget);
            }

            int copyNumber = System.Threading.Interlocked.Increment(ref savedCopyNumber);
            string revision = RevisionId;
            string partTag = "fake-saved-part-tag-" + copyNumber.ToString();
            string value = CurrentValue;
            Dictionary<string, object> checks = BuildSaveChecks(binding, value);
            Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "source_path", sourcePath }, { "saved_path", normalizedTarget }, { "reopened", reopen },
                { "checks", checks },
                { "source_identity", SemanticIdentity(binding.PartPath, binding.PartTag, binding.RevisionId) },
                { "reopened_identity", SemanticIdentity(normalizedTarget, partTag, revision) }
            };

            bool unknown = System.Threading.Interlocked.Exchange(ref unknownNextSave, 0) != 0;
            if (unknown)
            {
                return new M2SaveCopyResult {
                    State = "UNKNOWN", PartPath = normalizedTarget, PartTag = partTag,
                    RevisionId = revision, Reopened = false, Checks = checks, Evidence = evidence,
                    ErrorCode = "SAVE_RESULT_UNKNOWN",
                    ErrorMessage = "fixture persisted the copy but could not confirm the result"
                };
            }

            if (reopen)
            {
                binding.PartPath = normalizedTarget;
                binding.PartTag = partTag;
                binding.RevisionId = revision;
                SetIdentity(binding.Identity, "part_path", normalizedTarget);
                SetIdentity(binding.Identity, "part_tag", partTag);
                SetIdentity(binding.Identity, "is_modified_before_read", false);
                SetIdentity(binding.Identity, "is_modified_after_read", false);
                SetIdentity(binding.Identity, "dirty_changed_by_read", false);
                activeBinding = binding;
            }
            return new M2SaveCopyResult {
                State = "SAVED", PartPath = normalizedTarget, PartTag = partTag,
                RevisionId = revision, Reopened = reopen, Checks = checks, Evidence = evidence
            };
        }

        private Dictionary<string, object> BuildSaveChecks(M2ModelBinding binding, string value)
        {
            Dictionary<string, object> expressionEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "sketch_id", "sketch-1" }, { "constraint_id", "constraint-1" },
                { "expression_name", "p11" }, { "expected_value", value },
                { "actual_rhs", value }, { "actual_value", value }, { "unit", "mm" }
            };
            Dictionary<string, object> geometryEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "sketch_id", "sketch-1" }, { "constraint_id", "constraint-1" },
                { "geometry_refs", new List<string> { "line-1", "line-2" } },
                { "expected_value", value }, { "actual_value", value },
                { "geometry", "line-1:end-x=" + value + "mm" }
            };
            Dictionary<string, object> historyEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "sketch_id", "sketch-1" }, { "feature_id", "feature-1" }, { "feature_type", "EXTRUDE" },
                { "parents", new List<string>() }, { "expressions", new List<string> { "p11" } }, { "feature_count", 1 }
            };
            Dictionary<string, object> associationEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "sketch_id", "sketch-1" }, { "constraint_id", "constraint-1" },
                { "dimension_state", "Driving" }, { "expression_name", "p11" },
                { "geometry_refs", new List<string> { "line-1", "line-2" } }
            };
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "expression", VerificationCheck(true, expressionEvidence) },
                { "geometry", VerificationCheck(true, geometryEvidence) },
                { "history", VerificationCheck(true, historyEvidence) },
                { "associations", VerificationCheck(true, associationEvidence) }
            };
        }

        private static Dictionary<string, object> VerificationCheck(bool verified, Dictionary<string, object> evidence)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "status", verified ? "VERIFIED" : "MISMATCH" }, { "evidence", evidence }
            };
        }

        private static Dictionary<string, object> SemanticIdentity(string partPath, string partTag, string revisionId)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "part_path", partPath }, { "part_tag", partTag }, { "revision_id", revisionId }
            };
        }

        private static void SetIdentity(Dictionary<string, object> identity, string key, object value)
        {
            if (identity == null) return;
            identity[key] = value;
        }

        private static Dictionary<string, object> ParameterEvidence(string value)
        {
            return new Dictionary<string, object> {
                { "expression_rhs", value }, { "value", value }, { "unit", "mm" }
            };
        }

        public void Release(M2ModelBinding binding)
        {
            if (binding != null) binding.AdapterState = null;
        }
    }
}
