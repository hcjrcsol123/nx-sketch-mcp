using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using NXOpen;
using NXOpen.Features;
using NXOpen.Assemblies;
using NXOpen.UF;
using NXOpen.Utilities;

// Native NXOpen fixtures for M5 external-history and current-work-part checks.
// This is a standalone test entry: it does not load the MCP bridge or call an M3 factory.
public static class M5FixtureEntry
{
    private const string SentinelName = "M5_USER_MEMORY_SENTINEL";
    private static readonly HashSet<string> OwnedTags = new HashSet<string>(StringComparer.Ordinal);
    private static readonly HashSet<string> OwnedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static string _ownedOutputDirectory;

    private sealed class Config
    {
        internal string OutputDirectory;
        internal string ReportPath;
        internal string Mode;
        internal string PartPath;
        internal string ConfigPath;
    }

    private sealed class FixtureSample
    {
        internal string Name;
        internal string PartPath;
        internal Part Part;
        internal Sketch Sketch;
        internal Feature Extrude;
        internal SketchDimensionalConstraint EditConstraint;
        internal Expression EditExpression;
        internal string EditExpressionName;
        internal string DimensionType;
        internal double CurrentEditValue;
        internal double TargetEditValue;
        internal double ExpectedVolumeAfterEdit;
        internal double InitialVolume;
        internal double Depth;
        internal string SourceFeatureIdBeforeRemoveParameters;
        internal string SourceFeatureTagBeforeRemoveParameters;
        internal string SourceFeatureTypeBeforeRemoveParameters;
        internal double SourceVolumeBeforeRemoveParameters;
        internal List<string> SourceExpressionNamesBeforeRemoveParameters;
        internal bool SourceFeatureRetainedAfterRemoveParameters;
        internal int RemainingSketchCountAfterRemoveParameters;
        internal List<string> ExpressionNamesAfterRemoveParameters;
        internal string BodyTagAfterRemoveParameters;
        internal string BodyJournalIdAfterRemoveParameters;
        internal Dictionary<string, object> Inspection;
        internal string SketchStatus;
        internal int SketchDof;
    }

    public static void Main(string[] args)
    {
        Config config = null;
        Dictionary<string, object> report = new Dictionary<string, object>();
        try
        {
            config = ReadConfig();
            _ownedOutputDirectory = CanonicalDirectory(config.OutputDirectory);
            Directory.CreateDirectory(_ownedOutputDirectory);
            report["status"] = "RUNNING";
            report["mode"] = config.Mode;
            report["started_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            report["output_dir"] = _ownedOutputDirectory;
            report["report_path"] = config.ReportPath;
            report["config_path"] = config.ConfigPath;
            report["evidence_level"] = "NXOpen native fixture entry; actual NX execution required for runtime evidence";

            Session session = Session.GetSession();
            if (config.Mode == "create") CreateHistoryFixtures(session, config, report);
            else if (config.Mode == "no_history")
            {
                FixtureSample noHistory = CreateNoHistory(session,
                    Path.Combine(config.OutputDirectory, "m5_no_history_" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture) + ".prt"),
                    report);
                report["current_part"] = CurrentPartReport(noHistory.Part);
            }
            else if (config.Mode == "open") OpenFixture(session, config, report);
            else if (config.Mode == "saved_dirty") CreateSavedDirtyState(session, config, report);
            else if (config.Mode == "unsaved") CreateUnsavedState(session, config, report);
            else if (config.Mode == "readonly") OpenReadOnlyCopy(session, config, report);
            else if (config.Mode == "inspect") InspectCurrent(session, report);
            else if (config.Mode == "assembly") CreateAssemblyFixture(session, config, report);
            else if (config.Mode == "close_test_parts") CloseTestParts(session, report);
            else throw new InvalidOperationException("Unsupported M5 fixture mode: " + config.Mode);

            report["status"] = "OK";
            report["finished_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            WriteReport(config.ReportPath, report);
        }
        catch (Exception ex)
        {
            report["status"] = "FAIL";
            report["error"] = ex.ToString();
            report["finished_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            if (config != null)
            {
                report["mode"] = config.Mode;
                report["output_dir"] = config.OutputDirectory;
                report["report_path"] = config.ReportPath;
                try { WriteReport(config.ReportPath, report); }
                catch { }
            }
            throw;
        }
    }

    public static int GetUnloadOption(string dummy)
    {
        return (int)Session.LibraryUnloadOption.Explicitly;
    }

    public static void UnloadLibrary(string dummy)
    {
    }

    private static Config ReadConfig()
    {
        string configPath = Environment.GetEnvironmentVariable("NX_SKETCH_MCP_M5_FIXTURE_CONFIG");
        if (String.IsNullOrWhiteSpace(configPath))
            throw new InvalidOperationException("NX_SKETCH_MCP_M5_FIXTURE_CONFIG must contain the path to a JSON config file.");
        configPath = Path.GetFullPath(configPath);
        string json = File.ReadAllText(configPath);
        JavaScriptSerializer serializer = new JavaScriptSerializer();
        Dictionary<string, object> values = serializer.Deserialize<Dictionary<string, object>>(json);
        Config result = new Config();
        result.ConfigPath = configPath;
        result.OutputDirectory = StringValue(values, "output_dir");
        string mode = StringValue(values, "mode");
        if (String.IsNullOrWhiteSpace(mode)) throw new InvalidOperationException("config.mode is required");
        result.Mode = mode.Trim().ToLowerInvariant();
        result.PartPath = OptionalString(values, "part_path");
        result.ReportPath = OptionalString(values, "report_path");
        if (String.IsNullOrWhiteSpace(result.OutputDirectory)) throw new InvalidOperationException("config.output_dir is required");
        if (String.IsNullOrWhiteSpace(result.ReportPath))
            result.ReportPath = Path.Combine(result.OutputDirectory, "m5-fixture-" + result.Mode + ".json");
        result.ReportPath = Path.GetFullPath(result.ReportPath);
        return result;
    }

    private static string StringValue(Dictionary<string, object> values, string name)
    {
        object value;
        return values != null && values.TryGetValue(name, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
    }

    private static string OptionalString(Dictionary<string, object> values, string name)
    {
        return StringValue(values, name);
    }

    private static void CreateHistoryFixtures(Session session, Config config, Dictionary<string, object> report)
    {
        string suffix = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture);
        List<object> samples = new List<object>();
        report["samples"] = samples;
        report["sample_count"] = 0;
        FixtureSample rectangle = CreateRectangle(session, Path.Combine(config.OutputDirectory, "m5_rect_" + suffix + ".prt"));
        samples.Add(SampleReport(rectangle));
        report["sample_count"] = samples.Count;
        FixtureSample cylinder = CreateCylinder(session, Path.Combine(config.OutputDirectory, "m5_cylinder_" + suffix + ".prt"));
        samples.Add(SampleReport(cylinder));
        report["sample_count"] = samples.Count;
        FixtureSample bracket = CreateLBracket(session, Path.Combine(config.OutputDirectory, "m5_l_bracket_" + suffix + ".prt"));
        samples.Add(SampleReport(bracket));
        report["sample_count"] = samples.Count;
        CreateNoHistory(session, Path.Combine(config.OutputDirectory, "m5_no_history_" + suffix + ".prt"), report);
        report["current_part"] = CurrentPartReport(session.Parts.Work);
    }

    private static FixtureSample CreateRectangle(Session session, string path)
    {
        EnsureNewOutput(path);
        Part part = NewPart(session, "M5_RECT");
        Track(part, path);
        Sketch sketch = CreateSketch(part);
        Line bottom = part.Curves.CreateLine(new Point3d(0, 0, 0), new Point3d(20, 0, 0));
        Line right = part.Curves.CreateLine(new Point3d(20, 0, 0), new Point3d(20, 12, 0));
        Line top = part.Curves.CreateLine(new Point3d(20, 12, 0), new Point3d(0, 12, 0));
        Line left = part.Curves.CreateLine(new Point3d(0, 12, 0), new Point3d(0, 0, 0));
        Line[] lines = new Line[] { bottom, right, top, left };
        AddLines(sketch, lines);
        JoinLines(sketch, lines);
        sketch.CreateHorizontalConstraint(LineConstraint(bottom));
        sketch.CreateHorizontalConstraint(LineConstraint(top));
        sketch.CreateVerticalConstraint(LineConstraint(right));
        sketch.CreateVerticalConstraint(LineConstraint(left));
        sketch.CreateCoincidentConstraint(Vertex(bottom, Sketch.ConstraintPointType.StartVertex),
            new Sketch.ConstraintGeometry(sketch.OriginPoint, Sketch.ConstraintPointType.None, 0));

        SketchDimensionalConstraint width = CreateSpan(part, sketch, bottom, Sketch.ConstraintType.HorizontalDim,
            20.0, new Point3d(10, -4, 0), "M5_RECT_WIDTH");
        SketchDimensionalConstraint height = CreateSpan(part, sketch, left, Sketch.ConstraintType.VerticalDim,
            12.0, new Point3d(-4, 6, 0), "M5_RECT_HEIGHT");
        string status;
        int dof;
        CheckSketch(sketch, out status, out dof);
        Feature extrude = Extrude(part, sketch, bottom, new Point3d(6, 4, 0), 5.0, "M5_RECT_DEPTH");
        Session.GetSession().UpdateManager.DoUpdate(Session.GetSession().NewestVisibleUndoMark);
        double initialVolume = BodyVolume(part, SingleBody(part));
        Dictionary<string, object> inspection = InspectPart(part);
        SaveNewPart(part, path);
        return new FixtureSample
        {
            Name = "rect_extrude",
            PartPath = path,
            Part = part,
            Sketch = sketch,
            Extrude = extrude,
            EditConstraint = width,
            EditExpression = FindExpression(part, "M5_RECT_WIDTH"),
            EditExpressionName = "M5_RECT_WIDTH",
            DimensionType = Sketch.ConstraintType.HorizontalDim.ToString(),
            CurrentEditValue = 20.0,
            TargetEditValue = 24.0,
            ExpectedVolumeAfterEdit = 24.0 * 12.0 * 5.0,
            InitialVolume = initialVolume,
            Depth = 5.0,
            Inspection = inspection,
            SketchStatus = status,
            SketchDof = dof
        };
    }

    private static FixtureSample CreateNoHistory(Session session, string path, Dictionary<string, object> report)
    {
        EnsureNewOutput(path);
        Part part = NewPart(session, "M5_NO_HISTORY");
        Track(part, path);
        Tag blockTag;
        UFSession.GetUFSession().Modl.CreateBlock1(FeatureSigns.Nullsign,
            new double[] { 0.0, 0.0, 0.0 }, new string[] { "50", "30", "12" }, out blockTag);
        Feature blockFeature = (Feature)NXObjectManager.Get(blockTag);
        string sourceFeatureId = blockFeature.JournalIdentifier;
        string sourceFeatureType = blockFeature.FeatureType;
        string sourceFeatureTag = blockFeature.Tag.ToString();
        Body sourceBody = SingleBody(part);
        double sourceVolume = BodyVolume(part, sourceBody);
        List<string> expressionsBefore = ExpressionNames(part);
        report["no_history_conversion_before"] = new Dictionary<string, object>
        {
            { "part_path", path },
            { "source_feature_id", sourceFeatureId },
            { "source_feature_tag", sourceFeatureTag },
            { "source_feature_type", sourceFeatureType },
            { "source_body_tag", sourceBody.Tag.ToString() },
            { "source_body_journal_id", sourceBody.JournalIdentifier },
            { "source_volume_mm3", sourceVolume },
            { "source_sketch_count", part.Sketches.ToArray().Length },
            { "expressions_before", expressionsBefore }
        };

        RemoveParametersBuilder builder = part.Features.CreateRemoveParametersBuilder();
        try
        {
            if (!builder.Objects.Add(sourceBody))
                throw new InvalidOperationException("NX RemoveParametersBuilder did not accept the UF block Body.");
            builder.Commit();
        }
        finally { builder.Destroy(); }

        session.UpdateManager.DoUpdate(session.NewestVisibleUndoMark);
        Body body = SingleBody(part);
        double volume = BodyVolume(part, body);
        int remainingSketches = part.Sketches.ToArray().Length;
        Feature[] featuresAfter = part.Features.ToArray();
        bool sourceFeatureRetained = featuresAfter.Any(delegate(Feature candidate)
        {
            return candidate.Tag.ToString() == sourceFeatureTag || candidate.JournalIdentifier == sourceFeatureId;
        });
        List<string> featureIdsAfter = new List<string>();
        List<string> featureTypesAfter = new List<string>();
        foreach (Feature feature in featuresAfter)
        {
            featureIdsAfter.Add(feature.JournalIdentifier);
            featureTypesAfter.Add(feature.FeatureType);
        }
        List<string> expressionsAfter = ExpressionNames(part);
        report["no_history_conversion_after"] = new Dictionary<string, object>
        {
            { "part_path", path },
            { "source_feature_id", sourceFeatureId },
            { "source_feature_tag", sourceFeatureTag },
            { "source_feature_retained", sourceFeatureRetained },
            { "feature_count_after", featuresAfter.Length },
            { "feature_ids_after", featureIdsAfter },
            { "feature_types_after", featureTypesAfter },
            { "body_tag_after", body.Tag.ToString() },
            { "body_journal_id_after", body.JournalIdentifier },
            { "remaining_sketch_count", remainingSketches },
            { "expressions_after", expressionsAfter },
            { "volume_after_mm3", volume }
        };
        if (sourceFeatureRetained)
            throw new InvalidOperationException("RemoveParametersBuilder retained the original UF BLOCK feature; no-history conversion did not complete.");
        if (remainingSketches != 0)
            throw new InvalidOperationException("RemoveParametersBuilder left an editable Sketch feature; count=" + remainingSketches.ToString(CultureInfo.InvariantCulture));

        Dictionary<string, object> inspection = InspectPart(part);
        SaveNewPart(part, path);
        FixtureSample sample = new FixtureSample
        {
            Name = "uf_block_without_parameters",
            PartPath = path,
            Part = part,
            SourceFeatureIdBeforeRemoveParameters = sourceFeatureId,
            SourceFeatureTagBeforeRemoveParameters = sourceFeatureTag,
            SourceFeatureTypeBeforeRemoveParameters = sourceFeatureType,
            SourceVolumeBeforeRemoveParameters = sourceVolume,
            SourceExpressionNamesBeforeRemoveParameters = expressionsBefore,
            SourceFeatureRetainedAfterRemoveParameters = sourceFeatureRetained,
            RemainingSketchCountAfterRemoveParameters = remainingSketches,
            ExpressionNamesAfterRemoveParameters = expressionsAfter,
            BodyTagAfterRemoveParameters = body.Tag.ToString(),
            BodyJournalIdAfterRemoveParameters = body.JournalIdentifier,
            InitialVolume = volume,
            ExpectedVolumeAfterEdit = volume,
            Depth = 12.0,
            SketchStatus = "NO_SKETCH",
            SketchDof = -1,
            Inspection = inspection
        };
        report["no_history_sample"] = NoHistorySampleReport(sample);
        return sample;
    }

    private static Dictionary<string, object> NoHistorySampleReport(FixtureSample sample)
    {
        return new Dictionary<string, object>
        {
            { "fixture_id", sample.Name },
            { "kind", "UF_block_with_native_RemoveParameters" },
            { "part_path", sample.PartPath },
            { "current_part", CurrentPartReport(sample.Part) },
            { "editable_original_sketch_count", 0 },
            { "source_feature_id_before_remove_parameters", sample.SourceFeatureIdBeforeRemoveParameters },
            { "source_feature_tag_before_remove_parameters", sample.SourceFeatureTagBeforeRemoveParameters },
            { "source_feature_type_before_remove_parameters", sample.SourceFeatureTypeBeforeRemoveParameters },
            { "source_volume_before_remove_parameters_mm3", sample.SourceVolumeBeforeRemoveParameters },
            { "source_feature_retained_after_remove_parameters", sample.SourceFeatureRetainedAfterRemoveParameters },
            { "remaining_sketch_count_after_remove_parameters", sample.RemainingSketchCountAfterRemoveParameters },
            { "body_tag_after_remove_parameters", sample.BodyTagAfterRemoveParameters },
            { "body_journal_id_after_remove_parameters", sample.BodyJournalIdAfterRemoveParameters },
            { "expression_names_before_remove_parameters", sample.SourceExpressionNamesBeforeRemoveParameters },
            { "expression_names_after_remove_parameters", sample.ExpressionNamesAfterRemoveParameters },
            { "conversion_method", "NXOpen.Features.RemoveParametersBuilder.Objects.Add(Body).Commit()" },
            { "remaining_feature_count", ((List<object>)sample.Inspection["features"]).Count },
            { "expected_volume_mm3", sample.InitialVolume },
            { "edit", null },
            { "inspection", sample.Inspection }
        };
    }

    private static FixtureSample CreateCylinder(Session session, string path)
    {
        EnsureNewOutput(path);
        Part part = NewPart(session, "M5_CYL");
        Track(part, path);
        Sketch sketch = CreateSketch(part);
        Arc circle = part.Curves.CreateArc(new Point3d(0, 0, 0), new Vector3d(1, 0, 0),
            new Vector3d(0, 1, 0), 15.0, 0.0, 2.0 * Math.PI);
        sketch.AddGeometry(circle, Sketch.InferConstraintsOption.InferNoConstraints);
        sketch.CreateCoincidentConstraint(new Sketch.ConstraintGeometry(circle,
            Sketch.ConstraintPointType.ArcCenter, 0),
            new Sketch.ConstraintGeometry(sketch.OriginPoint, Sketch.ConstraintPointType.None, 0));
        SketchDimensionalConstraint diameter = sketch.CreateDiameterDimension(
            new Sketch.DimensionGeometry { Geometry = circle, AssocType = Sketch.AssocType.None, AssocValue = 0 },
            new Point3d(25, 25, 0), null);
        SetDimension(part, diameter, 30.0, "M5_CYL_DIAMETER");
        string status;
        int dof;
        CheckSketch(sketch, out status, out dof);
        Feature extrude = Extrude(part, sketch, circle, new Point3d(3, 2, 0), 60.0, "M5_CYL_DEPTH");
        Session.GetSession().UpdateManager.DoUpdate(Session.GetSession().NewestVisibleUndoMark);
        double initialVolume = BodyVolume(part, SingleBody(part));
        Dictionary<string, object> inspection = InspectPart(part);
        SaveNewPart(part, path);
        return new FixtureSample
        {
            Name = "circle_extrude",
            PartPath = path,
            Part = part,
            Sketch = sketch,
            Extrude = extrude,
            EditConstraint = diameter,
            EditExpression = diameter.AssociatedExpression,
            EditExpressionName = "M5_CYL_DIAMETER",
            DimensionType = Sketch.ConstraintType.DiameterDim.ToString(),
            CurrentEditValue = 30.0,
            TargetEditValue = 18.0,
            ExpectedVolumeAfterEdit = Math.PI * 9.0 * 9.0 * 60.0,
            InitialVolume = initialVolume,
            Depth = 60.0,
            Inspection = inspection,
            SketchStatus = status,
            SketchDof = dof
        };
    }

    private static FixtureSample CreateLBracket(Session session, string path)
    {
        EnsureNewOutput(path);
        Part part = NewPart(session, "M5_L_BRACKET");
        Track(part, path);
        Sketch sketch = CreateSketch(part);
        Point3d[] points = new Point3d[]
        {
            new Point3d(0, 0, 0), new Point3d(60, 0, 0), new Point3d(60, 10, 0),
            new Point3d(10, 10, 0), new Point3d(10, 40, 0), new Point3d(0, 40, 0)
        };
        Line[] lines = new Line[6];
        for (int i = 0; i < points.Length; i++)
            lines[i] = part.Curves.CreateLine(points[i], points[(i + 1) % points.Length]);
        AddLines(sketch, lines);
        JoinLines(sketch, lines);
        sketch.CreateHorizontalConstraint(LineConstraint(lines[0]));
        sketch.CreateVerticalConstraint(LineConstraint(lines[1]));
        sketch.CreateHorizontalConstraint(LineConstraint(lines[2]));
        sketch.CreateVerticalConstraint(LineConstraint(lines[3]));
        sketch.CreateHorizontalConstraint(LineConstraint(lines[4]));
        sketch.CreateVerticalConstraint(LineConstraint(lines[5]));
        sketch.CreateCoincidentConstraint(Vertex(lines[0], Sketch.ConstraintPointType.StartVertex),
            new Sketch.ConstraintGeometry(sketch.OriginPoint, Sketch.ConstraintPointType.None, 0));
        SketchDimensionalConstraint outerLength = CreateSpan(part, sketch, lines[0], Sketch.ConstraintType.HorizontalDim,
            60.0, new Point3d(30, -5, 0), "M5_L_OUTER_LENGTH");
        CreateSpan(part, sketch, lines[1], Sketch.ConstraintType.VerticalDim,
            10.0, new Point3d(65, 5, 0), "M5_L_THICKNESS");
        CreateSpan(part, sketch, lines[2], Sketch.ConstraintType.HorizontalDim,
            50.0, new Point3d(35, 15, 0), "M5_L_INNER_WIDTH", true);
        CreateSpan(part, sketch, lines[3], Sketch.ConstraintType.VerticalDim,
            30.0, new Point3d(15, 25, 0), "M5_L_INNER_HEIGHT");
        string status;
        int dof;
        CheckSketch(sketch, out status, out dof);
        Feature extrude = Extrude(part, sketch, lines[0], new Point3d(30, 5, 0), 8.0, "M5_L_DEPTH");
        Session.GetSession().UpdateManager.DoUpdate(Session.GetSession().NewestVisibleUndoMark);
        double initialVolume = BodyVolume(part, SingleBody(part));
        Dictionary<string, object> inspection = InspectPart(part);
        SaveNewPart(part, path);
        double expectedAreaAfterEdit = 70.0 * 40.0 - 50.0 * 30.0;
        return new FixtureSample
        {
            Name = "l_bracket_extrude",
            PartPath = path,
            Part = part,
            Sketch = sketch,
            Extrude = extrude,
            EditConstraint = outerLength,
            EditExpression = outerLength.AssociatedExpression,
            EditExpressionName = "M5_L_OUTER_LENGTH",
            DimensionType = Sketch.ConstraintType.HorizontalDim.ToString(),
            CurrentEditValue = 60.0,
            TargetEditValue = 70.0,
            ExpectedVolumeAfterEdit = expectedAreaAfterEdit * 8.0,
            InitialVolume = initialVolume,
            Depth = 8.0,
            Inspection = inspection,
            SketchStatus = status,
            SketchDof = dof
        };
    }

    private static Dictionary<string, object> SampleReport(FixtureSample sample)
    {
        bool dirtyAfterSave = sample.Part.IsModified;
        return new Dictionary<string, object>
        {
            { "fixture_id", sample.Name },
            { "kind", "native_sketch_extrude" },
            { "part_path", sample.PartPath },
            { "current_part", CurrentPartReport(sample.Part) },
            { "sketch_id", sample.Sketch.JournalIdentifier },
            { "sketch_tag", sample.Sketch.Tag.ToString() },
            { "sketch_status", sample.SketchStatus },
            { "sketch_dof", sample.SketchDof },
            { "extrude_id", sample.Extrude.JournalIdentifier },
            { "extrude_tag", sample.Extrude.Tag.ToString() },
            { "edit", EditReport(sample.Sketch.JournalIdentifier, sample.EditConstraint, sample.EditExpressionName,
                sample.CurrentEditValue, sample.TargetEditValue, sample.DimensionType) },
            { "initial_volume_mm3", sample.InitialVolume },
            { "expected_volume_mm3", sample.ExpectedVolumeAfterEdit },
            { "depth_mm", sample.Depth },
            { "is_modified_after_save", dirtyAfterSave },
            { "inspection", sample.Inspection }
        };
    }

    private static void CreateSavedDirtyState(Session session, Config config, Dictionary<string, object> report)
    {
        string path = RequirePartPath(config);
        if (!IsWithinOutputDirectory(path, config.OutputDirectory))
            throw new InvalidOperationException("saved_dirty only edits fixture files beneath output_dir.");
        Part part = OpenPart(session, path);
        Track(part, path);
        FileInfo savedFileBefore = new FileInfo(path);
        long savedLengthBefore = savedFileBefore.Length;
        DateTime savedWriteBeforeUtc = savedFileBefore.LastWriteTimeUtc;
        bool dirtyBeforeWrites = part.IsModified;
        Expression widthExpression = part.Expressions.FindObject("M5_RECT_WIDTH");
        double oldWidth = widthExpression.Value;
        Sketch ignoredSketch;
        Sketch mcpSketch;
        SketchDimensionalConstraint memoryDimension = FindDrivingDimension(part, "M5_RECT_WIDTH", out ignoredSketch);
        SketchDimensionalConstraint mcpDimension = FindDrivingDimension(part, "M5_RECT_HEIGHT", out mcpSketch);
        double currentHeight = mcpDimension.AssociatedExpression.Value;
        widthExpression.RightHandSide = "23";
        EnsureSentinel(part);
        session.UpdateManager.DoUpdate(session.NewestVisibleUndoMark);
        bool dirtyAfterWrites = part.IsModified;
        Dictionary<string, object> inspect = InspectPart(part);
        bool dirtyAfterInspection = part.IsModified;
        FileInfo savedFileAfter = new FileInfo(path);
        savedFileAfter.Refresh();
        long savedLengthAfter = savedFileAfter.Length;
        DateTime savedWriteAfterUtc = savedFileAfter.LastWriteTimeUtc;
        report["current_part"] = CurrentPartReport(part);
        report["memory_edit"] = new Dictionary<string, object>
        {
            { "expression_name", "M5_RECT_WIDTH" },
            { "constraint_id", memoryDimension.JournalIdentifier },
            { "sketch_id", ignoredSketch.JournalIdentifier },
            { "value", widthExpression.Value },
            { "disk_value", oldWidth },
            { "unit", "mm" }
        };
        report["dirty_evidence"] = new Dictionary<string, object>
        {
            { "is_modified_before_writes", dirtyBeforeWrites },
            { "is_modified_after_writes", dirtyAfterWrites },
            { "is_modified_after_inspection", dirtyAfterInspection },
            { "saved_file_length_before", savedLengthBefore },
            { "saved_file_length_after", savedLengthAfter },
            { "saved_file_last_write_utc_before", savedWriteBeforeUtc.ToString("o", CultureInfo.InvariantCulture) },
            { "saved_file_last_write_utc_after", savedWriteAfterUtc.ToString("o", CultureInfo.InvariantCulture) },
            { "saved_file_unchanged_by_length_and_write_time", savedLengthBefore == savedLengthAfter && savedWriteBeforeUtc == savedWriteAfterUtc },
            { "sentinel_name", SentinelName },
            { "sentinel_value", 123.456 },
            { "width_expression", "M5_RECT_WIDTH" },
            { "width_before_mm", oldWidth },
            { "width_after_mm", widthExpression.Value },
            { "inspection", inspect }
        };
        report["edit"] = EditReport(mcpSketch.JournalIdentifier, mcpDimension, "M5_RECT_HEIGHT", currentHeight, 15.0,
            Sketch.ConstraintType.VerticalDim.ToString());
        report["expected_volume_mm3"] = 23.0 * 15.0 * 5.0;
        report["source_part_path"] = path;
    }

    private static void CreateUnsavedState(Session session, Config config, Dictionary<string, object> report)
    {
        Part part = NewPart(session, "M5_UNSAVED_" + DateTime.UtcNow.ToString("HHmmssfff", CultureInfo.InvariantCulture));
        Track(part, null);
        Sketch sketch = CreateSketch(part);
        Line line = part.Curves.CreateLine(new Point3d(0, 0, 0), new Point3d(25, 0, 0));
        sketch.AddGeometry(line, Sketch.InferConstraintsOption.InferNoConstraints);
        sketch.CreateHorizontalConstraint(LineConstraint(line));
        sketch.CreateCoincidentConstraint(Vertex(line, Sketch.ConstraintPointType.StartVertex),
            new Sketch.ConstraintGeometry(sketch.OriginPoint, Sketch.ConstraintPointType.None, 0));
        SketchDimensionalConstraint length = CreateSpan(part, sketch, line, Sketch.ConstraintType.HorizontalDim,
            25.0, new Point3d(12.5, -4, 0), "M5_UNSAVED_LINE_LENGTH");
        string status;
        int dof;
        CheckSketch(sketch, out status, out dof);
        EnsureSentinel(part);
        bool dirtyBeforeInspect = part.IsModified;
        Dictionary<string, object> inspected = InspectPart(part);
        bool dirtyAfterInspect = part.IsModified;
        report["current_part"] = CurrentPartReport(part);
        report["unsaved_evidence"] = new Dictionary<string, object>
        {
            { "part_was_not_saved", true },
            { "is_modified_before_inspection", dirtyBeforeInspect },
            { "is_modified_after_inspection", dirtyAfterInspect },
            { "sketch_id", sketch.JournalIdentifier },
            { "sketch_status", status },
            { "sketch_dof", dof },
            { "edit", EditReport(sketch.JournalIdentifier, length, "M5_UNSAVED_LINE_LENGTH", 25.0, 25.0,
                Sketch.ConstraintType.HorizontalDim.ToString()) },
            { "sentinel_name", SentinelName },
            { "sentinel_value", 123.456 },
            { "inspection", inspected }
        };
        report["edit"] = EditReport(sketch.JournalIdentifier, length, "M5_UNSAVED_LINE_LENGTH", 25.0, 30.0,
            Sketch.ConstraintType.HorizontalDim.ToString());
    }

    private static void OpenReadOnlyCopy(Session session, Config config, Dictionary<string, object> report)
    {
        string sourcePath = RequirePartPath(config);
        string targetPath = Path.Combine(config.OutputDirectory, "m5_readonly.prt");
        EnsureNewOutput(targetPath);
        File.Copy(sourcePath, targetPath, false);
        File.SetAttributes(targetPath, File.GetAttributes(targetPath) | FileAttributes.ReadOnly);
        bool fileReadOnlyBeforeOpen = (File.GetAttributes(targetPath) & FileAttributes.ReadOnly) != 0;
        Part part = OpenPart(session, targetPath);
        Track(part, targetPath);
        bool dirtyBeforeInspect = part.IsModified;
        Dictionary<string, object> inspected = InspectPart(part);
        bool dirtyAfterInspect = part.IsModified;
        report["current_part"] = CurrentPartReport(part);
        report["readonly_evidence"] = new Dictionary<string, object>
        {
            { "source_path", sourcePath },
            { "readonly_copy_path", targetPath },
            { "file_attribute_readonly_before_open", fileReadOnlyBeforeOpen },
            { "nx_is_read_only", part.IsReadOnly },
            { "is_modified_before_inspection", dirtyBeforeInspect },
            { "is_modified_after_inspection", dirtyAfterInspect },
            { "inspection", inspected }
        };
        double? expectedVolume;
        report["edit"] = NativeEditInfo(part, out expectedVolume);
        if (expectedVolume.HasValue) report["expected_volume_mm3"] = expectedVolume.Value;
    }

    private static void OpenFixture(Session session, Config config, Dictionary<string, object> report)
    {
        string path = RequirePartPath(config);
        Part part = OpenPart(session, path);
        if (IsWithinOutputDirectory(path, config.OutputDirectory)) Track(part, path);
        bool dirtyBeforeNativeEditRead = part.IsModified;
        double? expectedVolume;
        report["edit"] = NativeEditInfo(part, out expectedVolume);
        if (expectedVolume.HasValue) report["expected_volume_mm3"] = expectedVolume.Value;
        bool dirtyAfterNativeEditRead = part.IsModified;
        report["current_part"] = CurrentPartReport(part);
        report["read_transition"] = new Dictionary<string, object>
        {
            { "is_modified_before_native_edit_read", dirtyBeforeNativeEditRead },
            { "is_modified_after_native_edit_read", dirtyAfterNativeEditRead },
            { "getter_changed_modified_state", dirtyBeforeNativeEditRead != dirtyAfterNativeEditRead }
        };
        report["inspection_scope"] = "part identity and native edit candidate only; use mode inspect for full readback";
    }

    private static void InspectCurrent(Session session, Dictionary<string, object> report)
    {
        Part part = session.Parts.Work;
        if (part == null) throw new InvalidOperationException("There is no current Work Part to inspect.");
        bool dirtyBefore = part.IsModified;
        Dictionary<string, object> inspected = InspectPart(part);
        bool dirtyAfter = part.IsModified;
        report["current_part"] = CurrentPartReport(part);
        report["inspection"] = inspected;
        double? expectedVolume;
        report["edit"] = NativeEditInfo(part, out expectedVolume);
        if (expectedVolume.HasValue) report["expected_volume_mm3"] = expectedVolume.Value;
        report["dirty_transition"] = new Dictionary<string, object>
        {
            { "is_modified_before", dirtyBefore },
            { "is_modified_after", dirtyAfter },
            { "getter_changed_modified_state", dirtyBefore != dirtyAfter }
        };
    }

    private static void CreateAssemblyFixture(Session session, Config config, Dictionary<string, object> report)
    {
        string componentPath = RequirePartPath(config);
        if (!IsWithinOutputDirectory(componentPath, config.OutputDirectory))
            throw new InvalidOperationException("assembly mode requires a saved component beneath output_dir.");
        string assemblyPath = Path.Combine(config.OutputDirectory, "m5_assembly.prt");
        EnsureNewOutput(assemblyPath);
        Part assembly = NewPart(session, "M5_ASSEMBLY_" + DateTime.UtcNow.ToString("HHmmssfff", CultureInfo.InvariantCulture));
        Track(assembly, assemblyPath);
        Matrix3x3 identity = new Matrix3x3
        {
            Xx = 1.0, Xy = 0.0, Xz = 0.0,
            Yx = 0.0, Yy = 1.0, Yz = 0.0,
            Zx = 0.0, Zy = 0.0, Zz = 1.0
        };
        PartLoadStatus loadStatus;
        Component component = assembly.ComponentAssembly.AddComponent(componentPath, "Entire Part", "M5_fixture_component",
            new Point3d(0, 0, 0), identity, 1, out loadStatus);
        if (loadStatus != null) loadStatus.Dispose();
        if (assembly.ComponentAssembly.RootComponent == null)
            throw new InvalidOperationException("Native AddComponent did not create a root component tree.");
        SaveNewPart(assembly, assemblyPath);
        report["current_part"] = CurrentPartReport(assembly);
        report["assembly"] = new Dictionary<string, object>
        {
            { "part_path", assemblyPath },
            { "root_component_tag", assembly.ComponentAssembly.RootComponent.Tag.ToString() },
            { "component_tag", component == null ? null : component.Tag.ToString() },
            { "component_count", assembly.ComponentAssembly.RootComponent.GetChildren().Length },
            { "component_part_path", componentPath },
            { "is_modified_after_save", assembly.IsModified }
        };
    }

    private static void CloseTestParts(Session session, Dictionary<string, object> report)
    {
        List<object> closed = new List<object>();
        report["closed_parts"] = closed;
        report["closed_count"] = 0;
        BasePart[] parts = session.Parts.ToArray();
        foreach (BasePart basePart in parts)
        {
            Part part = basePart as Part;
            if (part == null || !IsFixtureOwned(part)) continue;
            Sketch activeSketch = session.ActiveSketch;
            if (activeSketch != null && activeSketch.OwningPart != null && activeSketch.OwningPart.Tag == part.Tag)
                activeSketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
            string path = part.FullPath;
            string tag = part.Tag.ToString();
            part.Close(BasePart.CloseWholeTree.False, BasePart.CloseModified.CloseModified, null);
            closed.Add(new Dictionary<string, object> { { "path", path }, { "tag", tag } });
            report["closed_count"] = closed.Count;
            OwnedTags.Remove(tag);
            if (!String.IsNullOrWhiteSpace(path)) OwnedPaths.Remove(CanonicalPath(path));
        }
        report["closed_parts"] = closed;
        report["closed_count"] = closed.Count;
    }

    private static bool IsFixtureOwned(Part part)
    {
        string tag = part.Tag.ToString();
        if (OwnedTags.Contains(tag)) return true;
        string path = part.FullPath;
        return !String.IsNullOrWhiteSpace(path) && (OwnedPaths.Contains(CanonicalPath(path)) ||
            IsWithinOutputDirectory(path, _ownedOutputDirectory));
    }

    private static Dictionary<string, object> InspectPart(Part part)
    {
        bool dirtyBefore = part.IsModified;
        Dictionary<string, object> result = new Dictionary<string, object>();
        result["current_part"] = CurrentPartReport(part);
        List<object> expressions = new List<object>();
        foreach (Expression expression in part.Expressions.ToArray())
        {
            Dictionary<string, object> item = new Dictionary<string, object>
            {
                { "tag", expression.Tag.ToString() },
                { "name", expression.Name },
                { "right_hand_side", expression.RightHandSide },
                { "unit", expression.Units == null ? null : expression.Units.Symbol }
            };
            try { item["value"] = expression.Value; }
            catch (Exception ex) { item["value_error"] = ex.Message; }
            expressions.Add(item);
        }
        result["expressions"] = expressions;

        List<object> features = new List<object>();
        foreach (Feature feature in part.Features.ToArray())
        {
            features.Add(new Dictionary<string, object>
            {
                { "tag", feature.Tag.ToString() },
                { "journal_id", feature.JournalIdentifier },
                { "feature_type", feature.FeatureType },
                { "suppressed", feature.Suppressed }
            });
        }
        result["features"] = features;

        List<object> sketches = new List<object>();
        foreach (Sketch sketch in part.Sketches.ToArray())
        {
            int dof;
            Sketch.Status status = sketch.GetStatus(out dof);
            List<object> dimensions = new List<object>();
            Sketch.ConstraintType[] dimensionTypes = new Sketch.ConstraintType[]
            {
                Sketch.ConstraintType.HorizontalDim,
                Sketch.ConstraintType.VerticalDim,
                Sketch.ConstraintType.DiameterDim
            };
            foreach (Sketch.ConstraintType dimensionType in dimensionTypes)
            foreach (SketchConstraint constraint in sketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Dimension, dimensionType))
            {
                SketchDimensionalConstraint dimensional = constraint as SketchDimensionalConstraint;
                if (dimensional == null) continue;
                Expression expression = dimensional.AssociatedExpression;
                dimensions.Add(new Dictionary<string, object>
                {
                    { "tag", dimensional.Tag.ToString() },
                    { "constraint_id", dimensional.JournalIdentifier },
                    { "dimension_type", dimensional.ConstraintType.ToString() },
                    { "dimension_state", dimensional.DimensionState.ToString() },
                    { "expression_name", expression == null ? null : expression.Name },
                    { "value", expression == null ? (object)null : expression.Value }
                });
            }
            sketches.Add(new Dictionary<string, object>
            {
                { "tag", sketch.Tag.ToString() },
                { "sketch_id", sketch.JournalIdentifier },
                { "feature_id", sketch.Feature == null ? null : sketch.Feature.JournalIdentifier },
                { "status", status.ToString() },
                { "degrees_of_freedom", dof },
                { "geometry_count", sketch.GetAllGeometry().Length },
                { "driving_dimensions", dimensions }
            });
        }
        result["sketches"] = sketches;

        List<object> bodies = new List<object>();
        foreach (Body body in part.Bodies.ToArray())
        {
            Dictionary<string, object> item = new Dictionary<string, object>
            {
                { "tag", body.Tag.ToString() },
                { "journal_id", body.JournalIdentifier },
                { "is_solid", body.IsSolidBody }
            };
            if (body.IsSolidBody) item["volume_mm3"] = BodyVolume(part, body);
            bodies.Add(item);
        }
        result["bodies"] = bodies;
        result["is_modified_before_getters"] = dirtyBefore;
        result["is_modified_after_getters"] = part.IsModified;
        return result;
    }

    private static Dictionary<string, object> CurrentPartReport(BasePart part)
    {
        if (part == null) return null;
        return new Dictionary<string, object>
        {
            { "path", part.FullPath },
            { "leaf", part.Leaf },
            { "tag", part.Tag.ToString() },
            { "is_modified", part.IsModified },
            { "is_read_only", part.IsReadOnly },
            { "units", part.PartUnits.ToString() }
        };
    }

    private static Dictionary<string, object> EditReport(string sketchId, SketchDimensionalConstraint constraint,
        string expressionName, double currentValue, double targetValue, string dimensionType)
    {
        Expression expression = constraint.AssociatedExpression;
        return new Dictionary<string, object>
        {
            { "sketch_id", sketchId },
            { "constraint_id", constraint.JournalIdentifier },
            { "constraint_tag", constraint.Tag.ToString() },
            { "expression_name", expressionName },
            { "current_value", currentValue },
            { "value", targetValue },
            { "unit", "mm" },
            { "dimension_type", dimensionType },
            { "dimension_state", constraint.DimensionState.ToString() },
            { "associated_expression_tag", expression == null ? null : expression.Tag.ToString() }
        };
    }

    private static Dictionary<string, object> NativeEditInfo(Part part, out double? expectedVolume)
    {
        expectedVolume = null;
        string expressionName = null;
        double target = 0.0;
        string type = null;
        if (FindExpression(part, "M5_RECT_WIDTH") != null)
        {
            expressionName = "M5_RECT_WIDTH";
            target = 24.0;
            type = Sketch.ConstraintType.HorizontalDim.ToString();
            expectedVolume = 24.0 * 12.0 * 5.0;
        }
        else if (FindExpression(part, "M5_CYL_DIAMETER") != null)
        {
            expressionName = "M5_CYL_DIAMETER";
            target = 18.0;
            type = Sketch.ConstraintType.DiameterDim.ToString();
            expectedVolume = Math.PI * 9.0 * 9.0 * 60.0;
        }
        else if (FindExpression(part, "M5_L_OUTER_LENGTH") != null)
        {
            expressionName = "M5_L_OUTER_LENGTH";
            target = 70.0;
            type = Sketch.ConstraintType.HorizontalDim.ToString();
            expectedVolume = (70.0 * 40.0 - 50.0 * 30.0) * 8.0;
        }
        else if (FindExpression(part, "M5_UNSAVED_LINE_LENGTH") != null)
        {
            expressionName = "M5_UNSAVED_LINE_LENGTH";
            target = 30.0;
            type = Sketch.ConstraintType.HorizontalDim.ToString();
        }
        if (expressionName == null) return null;
        Sketch ownerSketch;
        SketchDimensionalConstraint constraint = FindDrivingDimension(part, expressionName, out ownerSketch);
        return EditReport(ownerSketch.JournalIdentifier, constraint, expressionName,
            constraint.AssociatedExpression.Value, target, type);
    }

    private static Part NewPart(Session session, string name)
    {
        string uniqueName = name + "_" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture);
        return session.Parts.NewDisplay(uniqueName, Part.Units.Millimeters);
    }

    private static Part OpenPart(Session session, string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("M5 fixture part path does not exist", path);
        string target = CanonicalPath(path);
        foreach (BasePart existing in session.Parts.ToArray())
        {
            string existingPath = existing.FullPath;
            bool samePath = false;
            if (!String.IsNullOrWhiteSpace(existingPath))
            {
                samePath = String.Equals(CanonicalPath(existingPath), target, StringComparison.OrdinalIgnoreCase);
                if (!samePath && !Path.IsPathRooted(existingPath))
                    samePath = String.Equals(Path.GetFileName(existingPath), Path.GetFileName(target), StringComparison.OrdinalIgnoreCase);
            }
            if (!samePath) continue;
            PartLoadStatus displayStatus;
            session.Parts.SetDisplay(existing, false, true, out displayStatus);
            if (displayStatus != null) displayStatus.Dispose();
            if (session.Parts.Work == null || session.Parts.Work.Tag != existing.Tag) session.Parts.SetWork(existing);
            return existing as Part;
        }
        PartLoadStatus status;
        Part part = session.Parts.OpenDisplay(path, out status);
        if (status != null) status.Dispose();
        return part;
    }

    private static Sketch CreateSketch(Part part)
    {
        Point3d origin = new Point3d(0, 0, 0);
        Plane plane = part.Planes.CreatePlane(origin, new Vector3d(0, 0, 1), SmartObject.UpdateOption.WithinModeling);
        Direction axis = part.Directions.CreateDirection(origin, new Vector3d(1, 0, 0), SmartObject.UpdateOption.WithinModeling);
        Point point = part.Points.CreatePoint(origin);
        SketchInPlaceBuilder builder = part.Sketches.CreateSketchInPlaceBuilder2(null);
        try
        {
            builder.PlaneReference = plane;
            builder.AxisReference = axis;
            builder.SketchOrigin = point;
            builder.PlaneOption = Sketch.PlaneOption.ExistingPlane;
            builder.OriginOption = OriginMethod.SpecifyPoint;
            Sketch sketch = (Sketch)builder.Commit();
            sketch.Activate(Sketch.ViewReorient.False);
            return sketch;
        }
        finally { builder.Destroy(); }
    }

    private static void AddLines(Sketch sketch, Line[] lines)
    {
        foreach (Line line in lines) sketch.AddGeometry(line, Sketch.InferConstraintsOption.InferNoConstraints);
    }

    private static void JoinLines(Sketch sketch, Line[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
            sketch.CreateCoincidentConstraint(Vertex(lines[i], Sketch.ConstraintPointType.EndVertex),
                Vertex(lines[(i + 1) % lines.Length], Sketch.ConstraintPointType.StartVertex));
    }

    private static Sketch.ConstraintGeometry LineConstraint(Line line)
    {
        return new Sketch.ConstraintGeometry(line, Sketch.ConstraintPointType.None, 0);
    }

    private static Sketch.ConstraintGeometry Vertex(Line line, Sketch.ConstraintPointType point)
    {
        return new Sketch.ConstraintGeometry(line, point, 0);
    }

    private static Sketch.DimensionGeometry Dimension(Line line, Sketch.AssocType point)
    {
        return new Sketch.DimensionGeometry { Geometry = line, AssocType = point, AssocValue = 0 };
    }

    private static SketchDimensionalConstraint CreateSpan(Part part, Sketch sketch, Line line,
        Sketch.ConstraintType dimensionType, double value, Point3d labelPoint, string expressionName, bool reverse = false)
    {
        Sketch.DimensionGeometry start = Dimension(line, reverse ? Sketch.AssocType.EndPoint : Sketch.AssocType.StartPoint);
        Sketch.DimensionGeometry end = Dimension(line, reverse ? Sketch.AssocType.StartPoint : Sketch.AssocType.EndPoint);
        SketchDimensionalConstraint constraint = sketch.CreateDimension(dimensionType, start, end,
            labelPoint, null, Sketch.DimensionOption.CreateAsDriving);
        SetDimension(part, constraint, value, expressionName);
        return constraint;
    }

    private static void SetDimension(Part part, SketchDimensionalConstraint constraint, double value, string expressionName)
    {
        if (constraint == null || constraint.AssociatedExpression == null)
            throw new InvalidOperationException("NX did not create an AssociatedExpression for a Driving dimension.");
        constraint.AssociatedExpression.RightHandSide = NumberText(value);
        part.Expressions.Rename(constraint.AssociatedExpression, expressionName);
        if (constraint.DimensionState != SketchDimensionalConstraint.DimensionStateType.Driving)
            throw new InvalidOperationException("NX did not retain the requested dimension as Driving: " + expressionName);
    }

    private static void CheckSketch(Sketch sketch, out string status, out int dof)
    {
        sketch.CalculateStatus();
        Sketch.Status value = sketch.GetStatus(out dof);
        status = value.ToString();
        if (value != Sketch.Status.WellConstrained || dof != 0)
            throw new InvalidOperationException("Native fixture sketch is not fully constrained: " + status + ", dof=" + dof.ToString(CultureInfo.InvariantCulture));
        sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
    }

    private static Feature Extrude(Part part, Sketch sketch, Curve profileCurve, Point3d helpPoint,
        double depth, string depthExpressionName)
    {
        Section section = part.Sections.CreateSection(0.0095, 0.01, 0.5);
        CurveFeatureRule rule = part.ScRuleFactory.CreateRuleCurveFeature(new Feature[] { sketch.Feature });
        section.AddToSection(new SelectionIntentRule[] { rule }, profileCurve, null, null, helpPoint, Section.Mode.Create);
        Direction direction = part.Directions.CreateDirection(new Point3d(0, 0, 0), new Vector3d(0, 0, 1), SmartObject.UpdateOption.WithinModeling);
        ExtrudeBuilder builder = part.Features.CreateExtrudeBuilder(null);
        try
        {
            builder.Section = section;
            builder.Direction = direction;
            builder.Limits.StartExtend.Value.RightHandSide = "0";
            builder.Limits.EndExtend.Value.RightHandSide = NumberText(depth);
            Expression depthExpression = builder.Limits.EndExtend.Value;
            Feature feature = builder.CommitFeature();
            if (depthExpression != null) part.Expressions.Rename(depthExpression, depthExpressionName);
            return feature;
        }
        finally { builder.Destroy(); }
    }

    private static Body SingleBody(Part part)
    {
        Body[] bodies = part.Bodies.ToArray();
        if (bodies.Length != 1 || !bodies[0].IsSolidBody)
            throw new InvalidOperationException("Fixture expected one solid body, found " + bodies.Length.ToString(CultureInfo.InvariantCulture));
        return bodies[0];
    }

    private static double BodyVolume(Part part, Body body)
    {
        Unit[] units = new Unit[]
        {
            part.UnitCollection.FindObject("Kilogram"),
            part.UnitCollection.FindObject("MilliMeter"),
            part.UnitCollection.FindObject("SquareMilliMeter"),
            part.UnitCollection.FindObject("CubicMilliMeter")
        };
        MeasureBodies measurement = part.MeasureManager.NewMassProperties(units, 0.99, new Body[] { body });
        return measurement.Volume;
    }

    private static void SaveNewPart(Part part, string path)
    {
        using (PartSaveStatus status = part.SaveAs(path))
        {
            if (status.NumberUnsavedObjects != 0)
                throw new InvalidOperationException("NX SaveAs reported unsaved objects: " + status.NumberUnsavedObjects.ToString(CultureInfo.InvariantCulture));
        }
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new IOException("NX did not create the requested fixture part: " + path);
        Track(part, path);
    }

    private static void EnsureSentinel(Part part)
    {
        Expression sentinel = FindExpression(part, SentinelName);
        if (sentinel == null)
        {
            sentinel = part.Expressions.CreateExpression("Number", "123.456");
            part.Expressions.Rename(sentinel, SentinelName);
        }
    }

    private static Expression FindExpression(Part part, string name)
    {
        foreach (Expression expression in part.Expressions.ToArray())
            if (String.Equals(expression.Name, name, StringComparison.Ordinal)) return expression;
        return null;
    }

    private static List<string> ExpressionNames(Part part)
    {
        List<string> names = new List<string>();
        foreach (Expression expression in part.Expressions.ToArray()) names.Add(expression.Name);
        return names;
    }

    private static SketchDimensionalConstraint FindDrivingDimension(Part part, string expressionName, out Sketch ownerSketch)
    {
        foreach (Sketch sketch in part.Sketches.ToArray())
        foreach (Sketch.ConstraintType dimensionType in new Sketch.ConstraintType[]
            { Sketch.ConstraintType.HorizontalDim, Sketch.ConstraintType.VerticalDim, Sketch.ConstraintType.DiameterDim })
        foreach (SketchConstraint candidate in sketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Dimension, dimensionType))
        {
            SketchDimensionalConstraint dimensional = candidate as SketchDimensionalConstraint;
            if (dimensional != null && dimensional.AssociatedExpression != null &&
                String.Equals(dimensional.AssociatedExpression.Name, expressionName, StringComparison.Ordinal))
            {
                ownerSketch = sketch;
                return dimensional;
            }
        }
        ownerSketch = null;
        throw new InvalidOperationException("Driving dimension was not found for expression " + expressionName);
    }

    private static void Track(Part part, string path)
    {
        if (part != null) OwnedTags.Add(part.Tag.ToString());
        if (!String.IsNullOrWhiteSpace(path)) OwnedPaths.Add(CanonicalPath(path));
    }

    private static void EnsureNewOutput(string path)
    {
        if (File.Exists(path)) throw new IOException("Refusing to overwrite an existing fixture file: " + path);
    }

    private static string RequirePartPath(Config config)
    {
        if (String.IsNullOrWhiteSpace(config.PartPath)) throw new InvalidOperationException("config.part_path is required for mode " + config.Mode);
        return Path.GetFullPath(config.PartPath);
    }

    private static string CanonicalPath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string CanonicalDirectory(string path)
    {
        return CanonicalPath(path) + Path.DirectorySeparatorChar;
    }

    private static bool IsWithinOutputDirectory(string path, string outputDirectory)
    {
        string directory = CanonicalDirectory(outputDirectory);
        string fullPath = CanonicalPath(path);
        return fullPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase);
    }

    private static string NumberText(double value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static void WriteReport(string path, Dictionary<string, object> report)
    {
        string parent = Path.GetDirectoryName(path);
        if (!String.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
        File.WriteAllText(path, serializer.Serialize(report) + Environment.NewLine, new System.Text.UTF8Encoding(false));
    }
}
