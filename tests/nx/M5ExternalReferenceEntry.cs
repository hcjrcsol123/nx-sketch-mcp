using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using NXOpen;

// Standalone fixture entry that creates one real NX interpart expression link.
public static class M5ExternalReferenceEntry
{
    private sealed class Config
    {
        internal string OutputDirectory;
        internal string Mode;
        internal string SourcePartPath;
        internal string ReportPath;
        internal string ConfigPath;
    }

    public static void Main(string[] args)
    {
        Config config = null;
        Dictionary<string, object> report = new Dictionary<string, object>();
        try
        {
            config = ReadConfig();
            Directory.CreateDirectory(config.OutputDirectory);
            report["status"] = "RUNNING";
            report["mode"] = config.Mode;
            report["started_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            report["config_path"] = config.ConfigPath;
            report["output_dir"] = config.OutputDirectory;
            report["report_path"] = config.ReportPath;
            report["evidence_level"] = "NXOpen native interpart expression fixture; actual NX execution required for runtime evidence";

            Session session = Session.GetSession();
            if (config.Mode == "select_part")
            {
                Part selected = OpenSourcePart(session, config.SourcePartPath);
                report["requested_path"] = config.SourcePartPath;
                report["current_part"] = new Dictionary<string, object>
                {
                    { "path", selected.FullPath },
                    { "tag", selected.Tag.ToString() },
                    { "is_modified", selected.IsModified },
                    { "is_read_only", selected.IsReadOnly }
                };
                report["status"] = "OK";
                report["finished_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                WriteReport(config.ReportPath, report);
                return;
            }

            Part sourcePart = OpenSourcePart(session, config.SourcePartPath);
            Expression sourceExpression = sourcePart.Expressions.FindObject("M5_RECT_WIDTH");
            report["source"] = new Dictionary<string, object>
            {
                { "path", sourcePart.FullPath },
                { "tag", sourcePart.Tag.ToString() },
                { "is_modified", sourcePart.IsModified },
                { "expression_tag", sourceExpression.Tag.ToString() },
                { "expression_name", sourceExpression.Name },
                { "expression_rhs", sourceExpression.RightHandSide },
                { "expression_value", sourceExpression.Value },
                { "expression_unit", sourceExpression.Units == null ? null : sourceExpression.Units.Symbol },
                { "source_is_interpart", sourceExpression.IsInterpartExpression }
            };

            string suffix = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture);
            string targetPath = Path.Combine(config.OutputDirectory, "m5_external_reference_" + suffix + ".prt");
            if (File.Exists(targetPath)) throw new IOException("Refusing to overwrite an existing fixture: " + targetPath);
            Part target = session.Parts.NewDisplay("M5_EXTERNAL_REFERENCE_" + suffix, Part.Units.Millimeters);
            InterpartExpressionsBuilder builder = target.Expressions.CreateInterpartExpressionsBuilder();
            NXObject[] committed;
            try
            {
                builder.SetExpressions(new Expression[] { sourceExpression }, new string[] { "M5_EXTERNAL_WIDTH" });
                builder.Commit();
                committed = builder.GetCommittedObjects();
            }
            finally { builder.Destroy(); }

            List<object> linked = new List<object>();
            foreach (Expression expression in target.Expressions.ToArray())
            {
                if (!expression.IsInterpartExpression) continue;
                string sourcePartName;
                string sourceExpressionName;
                expression.GetInterpartExpressionNames(out sourcePartName, out sourceExpressionName);
                linked.Add(new Dictionary<string, object>
                {
                    { "tag", expression.Tag.ToString() },
                    { "name", expression.Name },
                    { "right_hand_side", expression.RightHandSide },
                    { "value", expression.Value },
                    { "unit", expression.Units == null ? null : expression.Units.Symbol },
                    { "is_interpart_expression", expression.IsInterpartExpression },
                    { "source_part_name", sourcePartName },
                    { "source_expression_name", sourceExpressionName }
                });
            }
            string[] interpartReferences = target.Expressions.GetInterpartReferences();
            List<object> committedObjects = new List<object>();
            if (committed != null)
                foreach (NXObject item in committed)
                {
                    Expression expression = item as Expression;
                    committedObjects.Add(new Dictionary<string, object>
                    {
                        { "tag", item.Tag.ToString() },
                        { "journal_id", item.JournalIdentifier },
                        { "type", item.GetType().Name },
                        { "expression_name", expression == null ? null : expression.Name },
                        { "is_interpart_expression", expression == null ? (object)null : expression.IsInterpartExpression }
                    });
                }
            report["target_before_save"] = new Dictionary<string, object>
            {
                { "path", target.FullPath },
                { "tag", target.Tag.ToString() },
                { "is_modified", target.IsModified },
                { "is_read_only", target.IsReadOnly },
                { "committed_objects", committedObjects },
                { "linked_expressions", linked },
                { "interpart_references", interpartReferences },
                { "interpart_reference_count", interpartReferences == null ? 0 : interpartReferences.Length }
            };
            if (linked.Count != 1 || interpartReferences == null || interpartReferences.Length == 0)
                throw new InvalidOperationException("NX did not report the expected interpart expression reference.");

            using (PartSaveStatus save = target.SaveAs(targetPath))
            {
                if (save.NumberUnsavedObjects != 0)
                    throw new InvalidOperationException("NX SaveAs reported unsaved objects: " + save.NumberUnsavedObjects.ToString(CultureInfo.InvariantCulture));
            }
            FileInfo file = new FileInfo(targetPath);
            report["target"] = new Dictionary<string, object>
            {
                { "path", target.FullPath },
                { "saved_path", targetPath },
                { "tag", target.Tag.ToString() },
                { "is_modified", target.IsModified },
                { "is_read_only", target.IsReadOnly },
                { "file_bytes", file.Length },
                { "linked_expressions", linked },
                { "interpart_references", interpartReferences },
                { "interpart_reference_count", interpartReferences.Length }
            };
            report["current_part"] = new Dictionary<string, object>
            {
                { "path", target.FullPath },
                { "tag", target.Tag.ToString() },
                { "is_modified", target.IsModified },
                { "is_read_only", target.IsReadOnly },
                { "units", target.PartUnits.ToString() }
            };
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
        JavaScriptSerializer serializer = new JavaScriptSerializer();
        Dictionary<string, object> fields = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(configPath));
        string mode = StringValue(fields, "mode");
        if (String.IsNullOrWhiteSpace(mode) || (mode != "external_reference" && mode != "select_part"))
            throw new InvalidOperationException("config.mode must be external_reference or select_part.");
        Config config = new Config();
        config.ConfigPath = configPath;
        config.Mode = mode;
        config.OutputDirectory = Path.GetFullPath(StringValue(fields, "output_dir"));
        config.SourcePartPath = Path.GetFullPath(StringValue(fields, "part_path"));
        config.ReportPath = StringValue(fields, "report_path");
        if (String.IsNullOrWhiteSpace(config.OutputDirectory)) throw new InvalidOperationException("config.output_dir is required.");
        if (String.IsNullOrWhiteSpace(config.SourcePartPath)) throw new InvalidOperationException("config.part_path is required.");
        if (String.IsNullOrWhiteSpace(config.ReportPath))
            config.ReportPath = Path.Combine(config.OutputDirectory, "m5-external-reference.json");
        config.ReportPath = Path.GetFullPath(config.ReportPath);
        return config;
    }

    private static string StringValue(Dictionary<string, object> fields, string name)
    {
        object value;
        return fields != null && fields.TryGetValue(name, out value) && value != null
            ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
    }

    private static Part OpenSourcePart(Session session, string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Source fixture part does not exist.", path);
        string target = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (BasePart existing in session.Parts.ToArray())
        {
            string existingPath = existing.FullPath;
            if (String.IsNullOrWhiteSpace(existingPath)) continue;
            string full = Path.GetFullPath(existingPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!String.Equals(full, target, StringComparison.OrdinalIgnoreCase)) continue;
            PartLoadStatus displayStatus;
            session.Parts.SetDisplay(existing, false, true, out displayStatus);
            if (displayStatus != null) displayStatus.Dispose();
            if (session.Parts.Work == null || session.Parts.Work.Tag != existing.Tag) session.Parts.SetWork(existing);
            Part loadedPart = existing as Part;
            if (loadedPart == null) throw new InvalidOperationException("The source fixture is not an NX Part.");
            return loadedPart;
        }
        PartLoadStatus loadStatus;
        Part part = session.Parts.OpenDisplay(path, out loadStatus);
        if (loadStatus != null) loadStatus.Dispose();
        if (session.Parts.Work == null || session.Parts.Work.Tag != part.Tag) session.Parts.SetWork(part);
        return part;
    }

    private static void WriteReport(string path, Dictionary<string, object> report)
    {
        string parent = Path.GetDirectoryName(path);
        if (!String.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
        File.WriteAllText(path, serializer.Serialize(report) + Environment.NewLine, new System.Text.UTF8Encoding(false));
    }
}
