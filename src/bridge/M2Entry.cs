using System;
using NXOpen;
using NxSketchMcp.M2;

// NX assembly entry point. Its only configuration inputs are the dedicated M2
// pipe name and private token-file path. Construction is inert; M2OpenModelAdapter
// first calls NXOpen only after an explicit ownership.takeover command.
public static class M2Entry
{
    private static M2BridgeServer _server;

    public static void Main(string[] args)
    {
        string pipeName = Environment.GetEnvironmentVariable("NX_SKETCH_MCP_M2_PIPE");
        string tokenFile = Environment.GetEnvironmentVariable("NX_SKETCH_MCP_M2_TOKEN_FILE");
        if (String.IsNullOrEmpty(pipeName) || String.IsNullOrEmpty(tokenFile))
            throw new InvalidOperationException("NX_SKETCH_MCP_M2_PIPE and NX_SKETCH_MCP_M2_TOKEN_FILE are required");

        M2BridgeServer server = new M2BridgeServer(new M2OpenModelAdapter());
        server.Start(pipeName, tokenFile);
        _server = server;
    }

    public static int GetUnloadOption(string dummy)
    {
        return (int)Session.LibraryUnloadOption.Explicitly;
    }

    public static void UnloadLibrary(string dummy)
    {
        M2BridgeServer server = _server;
        _server = null;
        if (server != null) server.Stop();
    }
}
