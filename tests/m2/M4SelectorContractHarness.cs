using System;
using System.IO;
using System.Reflection;

internal static class M4SelectorContractHarness
{
    private static int failures;

    private static int Main(string[] args)
    {
        if (args.Length != 2) return 64;
        string entryPath = Path.GetFullPath(args[0]);
        string nxManaged = Path.GetFullPath(args[1]);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs eventArgs)
        {
            string file = new AssemblyName(eventArgs.Name).Name + ".dll";
            string[] roots = new string[] { Path.GetDirectoryName(entryPath), nxManaged };
            foreach (string root in roots)
            {
                string candidate = Path.Combine(root, file);
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            }
            return null;
        };

        Assembly entry = Assembly.LoadFrom(entryPath);
        Type adapter = entry.GetType("M2OpenModelAdapter", true);
        MethodInfo parse = adapter.GetMethod("TryParseM4FaceSelector", BindingFlags.NonPublic | BindingFlags.Static);
        if (parse == null) throw new InvalidOperationException("M4 selector parser is missing");
        Check(parse, "top", true, 2, 1, "all", null);
        Check(parse, "bottom", true, 2, -1, "all", null);
        Check(parse, "positive Z side", true, 2, 1, "all", null);
        Check(parse, "negative Z side", true, 2, -1, "all", null);
        Check(parse, "正Z面", true, 2, 1, "all", null);
        Check(parse, "负Z面", true, 2, -1, "all", null);
        Check(parse, "負X面", true, 0, -1, "all", null);
        Check(parse, "outermost positive X side", true, 0, 1, "outermost", null);
        Check(parse, "positive X side at x=35 mm", true, 0, 1, "at", 35.0);
        Check(parse, "the nicest face", false, -1, 0, null, null);
        Check(parse, "positive X side positive Z side", false, -1, 0, null, null);
        return failures == 0 ? 0 : 1;
    }

    private static void Check(MethodInfo parser, string text, bool expected, int axis,
        int sign, string positionRule, double? coordinate)
    {
        object[] args = new object[] { text, null };
        bool actual = (bool)parser.Invoke(null, args);
        if (actual != expected)
        {
            Fail(text + " parsed=" + actual + " expected=" + expected);
            return;
        }
        if (!expected) return;
        object selector = args[1];
        Type type = selector.GetType();
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        int actualAxis = (int)type.GetField("Axis", flags).GetValue(selector);
        int actualSign = (int)type.GetField("Sign", flags).GetValue(selector);
        string actualRule = (string)type.GetField("PositionRule", flags).GetValue(selector);
        object actualCoordinate = type.GetField("CoordinateMm", flags).GetValue(selector);
        if (actualAxis != axis || actualSign != sign || !String.Equals(actualRule, positionRule, StringComparison.Ordinal))
            Fail(text + " normalized to the wrong axis, direction, or position rule");
        if (!Nullable.Equals((double?)actualCoordinate, coordinate))
            Fail(text + " normalized to the wrong coordinate");
    }

    private static void Fail(string message)
    {
        failures++;
        Console.Error.WriteLine("FAIL " + message);
    }
}
