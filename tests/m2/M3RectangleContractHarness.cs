using System;
using System.IO;
using System.Reflection;

internal static class M3RectangleContractHarness
{
    private static MethodInfo validate;
    private static Type pointType;
    private static int failures;
    private static int checks;

    private static int Main(string[] args)
    {
        if (args.Length != 2) return 64;
        string entryPath = Path.GetFullPath(args[0]);
        string nxManaged = Path.GetFullPath(args[1]);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs eventArgs)
        {
            string file = new AssemblyName(eventArgs.Name).Name + ".dll";
            foreach (string root in new string[] { Path.GetDirectoryName(entryPath), nxManaged })
            {
                string candidate = Path.Combine(root, file);
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            }
            return null;
        };
        Assembly entry = Assembly.LoadFrom(entryPath);
        validate = entry.GetType("M2OpenModelAdapter", true).GetMethod(
            "ValidateRectangleCorners", BindingFlags.NonPublic | BindingFlags.Static);
        if (validate == null) throw new InvalidOperationException("production rectangle validator is missing");
        pointType = validate.GetParameters()[0].ParameterType.GetElementType();

        double[][] rectangle = new double[][] {
            new double[] { 0, 0, 2, 0 }, new double[] { 2, 0, 2, 2 },
            new double[] { 2, 2, 0, 2 }, new double[] { 0, 2, 0, 0 }
        };
        Check("complete", rectangle, true);
        double[][] reversed = Clone(rectangle);
        for (int i = 0; i < reversed.Length; i++)
            reversed[i] = new double[] { reversed[i][2], reversed[i][3], reversed[i][0], reversed[i][1] };
        Check("reversed", reversed, true);
        Check("unordered", new double[][] { reversed[2], rectangle[0], reversed[3], rectangle[1] }, true);
        Check("reported-open-contour", new double[][] {
            new double[] { 0, 0, 1, 0 }, new double[] { 0, 2, 1, 2 },
            new double[] { 0, 0, 0, 2 }, new double[] { 2, 0, 2, 2 }
        }, false);
        for (int side = 0; side < 4; side++)
        {
            double[][] partial = Clone(rectangle);
            int coordinate = side % 2 == 0 ? 2 : 3;
            partial[side][coordinate] = 1;
            Check("partial-side-" + side, partial, false);
        }
        double[][] duplicate = Clone(rectangle);
        duplicate[2] = (double[])rectangle[0].Clone();
        Check("duplicate-and-missing-side", duplicate, false);
        double[][] diagonal = Clone(rectangle);
        diagonal[0][3] = 1;
        Check("diagonal", diagonal, false);
        double[][] nearCorner = Clone(rectangle);
        nearCorner[0][2] = 2 - 5e-8;
        Check("within-existing-tolerance", nearCorner, true);
        double[][] outsideTolerance = Clone(rectangle);
        outsideTolerance[0][2] = 2 - 1e-5;
        Check("gap-outside-tolerance", outsideTolerance, false);
        foreach (double nonFinite in new double[] { Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity })
        {
            double[][] invalid = Clone(rectangle);
            invalid[0][0] = nonFinite;
            Check("non-finite-" + nonFinite, invalid, false);
        }
        Check("missing-side", new double[][] { rectangle[0], rectangle[1], rectangle[2] }, false);
        Console.WriteLine("rectangle_contract_checks=" + checks + " failures=" + failures);
        return failures == 0 ? 0 : 1;
    }

    private static double[][] Clone(double[][] lines)
    {
        double[][] result = new double[lines.Length][];
        for (int i = 0; i < lines.Length; i++) result[i] = (double[])lines[i].Clone();
        return result;
    }

    private static void Check(string label, double[][] lines, bool expected)
    {
        checks++;
        Array points = Array.CreateInstance(pointType, lines.Length * 2);
        for (int i = 0; i < lines.Length; i++)
        {
            points.SetValue(Activator.CreateInstance(pointType, lines[i][0], lines[i][1], 0.0), i * 2);
            points.SetValue(Activator.CreateInstance(pointType, lines[i][2], lines[i][3], 0.0), i * 2 + 1);
        }
        object[] invocation = new object[] { points, 0.0, 0.0 };
        try
        {
            validate.Invoke(null, invocation);
            if (!expected) Fail(label + " unexpectedly accepted");
            else if (Math.Abs((double)invocation[1] - 1) > 1e-7 || Math.Abs((double)invocation[2] - 1) > 1e-7)
                Fail(label + " returned the wrong section help point");
        }
        catch (TargetInvocationException exception)
        {
            Exception inner = exception.InnerException;
            FieldInfo codeField = inner.GetType().GetField("Code");
            string code = codeField == null ? null : (string)codeField.GetValue(inner);
            if (expected || code != "UNSUPPORTED_PROFILE")
                Fail(label + " returned " + inner.GetType().Name + ": " + code);
        }
    }

    private static void Fail(string message)
    {
        failures++;
        Console.Error.WriteLine("FAIL " + message);
    }
}
