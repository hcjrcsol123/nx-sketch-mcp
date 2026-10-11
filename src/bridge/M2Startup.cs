using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using NXOpen;

// NX automatically invokes Startup on an interactive startup DLL. The dedicated
// child process supplies the bridge location; no model is selected or inspected.
public static class M2Startup
{
    private static bool _started;

    public static int Startup()
    {
        if (_started) return 0;
        string entryValue = Environment.GetEnvironmentVariable("NX_SKETCH_MCP_M2_ENTRY_DLL");
        string tokenValue = Environment.GetEnvironmentVariable("NX_SKETCH_MCP_M2_TOKEN_FILE");
        string pipeValue = Environment.GetEnvironmentVariable("NX_SKETCH_MCP_M2_PIPE");
        // A manually launched NX outside the dedicated profile must stay untouched.
        if (String.IsNullOrEmpty(entryValue) || String.IsNullOrEmpty(tokenValue) || String.IsNullOrEmpty(pipeValue)) return 0;
        try
        {
            if (!Path.IsPathRooted(entryValue) || !Path.IsPathRooted(tokenValue))
                throw new InvalidOperationException("Dedicated bridge paths must be absolute");
            string entryPath = Path.GetFullPath(entryValue);
            if (!String.Equals(Path.GetFileName(entryPath), "M2Entry.dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Dedicated startup requires M2Entry.dll");
            string bridgePath = Path.Combine(Path.GetDirectoryName(entryPath), "M2Bridge.dll");
            if (!File.Exists(bridgePath) || !File.Exists(entryPath))
                throw new FileNotFoundException("Dedicated bridge assemblies are missing");
            Assembly.LoadFrom(bridgePath);
            Assembly entry = Assembly.LoadFrom(entryPath);
            MethodInfo main = entry.GetType("M2Entry", true).GetMethod("Main", BindingFlags.Public | BindingFlags.Static,
                null, new Type[] { typeof(string[]) }, null);
            if (main == null) throw new MissingMethodException("M2Entry.Main(string[]) is missing");
            main.Invoke(null, new object[] { new string[0] });
            _started = true;
            return 0;
        }
        catch (Exception exception)
        {
            Exception cause = exception is TargetInvocationException && exception.InnerException != null
                ? exception.InnerException : exception;
            try
            {
                // One replaceable runtime diagnostic, not an accumulating run log.
                string errorPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(tokenValue)), "startup.last_error.txt");
                File.WriteAllText(errorPath, Process.GetCurrentProcess().Id + Environment.NewLine + cause.ToString(), new UTF8Encoding(false));
            }
            catch { }
            return 1;
        }
    }

    public static int GetUnloadOption(string dummy)
    {
        return (int)Session.LibraryUnloadOption.Explicitly;
    }
}
