using System;
using System.IO;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Ipc;
using System.Text;
using NxSketchMcp.M2;

// JSONL stdin/stdout adapter for the current-user M2 Remoting IPC channel.
// Credentials are read from a private file and are never written to argv/output/logs.
internal static class M2JsonlHelper
{
    private const int MaxLineChars = 16384;

    private sealed class LineRead
    {
        internal string Text;
        internal bool TooLong;
    }

    private static int Main(string[] args)
    {
        string pipeName;
        string tokenPath;
        if (!TryArguments(args, out pipeName, out tokenPath))
        {
            Console.Error.WriteLine("usage: M2JsonlHelper.exe --pipe-name <name> --token-file <absolute-path>");
            return 2;
        }

        string token;
        try { token = ReadToken(tokenPath); }
        catch (Exception)
        {
            Console.Error.WriteLine("could not read bridge credential file");
            return 2;
        }

        IpcClientChannel channel = null;
        try
        {
            Console.InputEncoding = new UTF8Encoding(false, true);
            Console.OutputEncoding = new UTF8Encoding(false);
            channel = new IpcClientChannel("m2-jsonl-" + Guid.NewGuid().ToString("N"), null);
            ChannelServices.RegisterChannel(channel, true);
            IM2Bridge bridge = (IM2Bridge)Activator.GetObject(typeof(IM2Bridge), "ipc://" + pipeName + "/M2Bridge");

            while (true)
            {
                LineRead input = ReadLine(Console.In, MaxLineChars);
                if (input == null) break;

                string response;
                if (input.TooLong)
                    response = M2Json.ErrorEnvelope(null, "INVALID_JSON", "request size is invalid");
                else
                {
                    try { response = bridge.Invoke(token, input.Text); }
                    catch (Exception)
                    {
                        response = M2Json.TransportError(input.Text);
                        Console.Error.WriteLine("bridge IPC call failed");
                    }
                }

                if (String.IsNullOrEmpty(response)) response = M2Json.TransportError(input.Text);
                Console.Out.WriteLine(response);
                Console.Out.Flush();
            }
            return 0;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("bridge IPC connection failed");
            return 3;
        }
        finally
        {
            if (channel != null)
            {
                try { ChannelServices.UnregisterChannel(channel); } catch { }
            }
            token = null;
        }
    }

    private static bool TryArguments(string[] args, out string pipeName, out string tokenPath)
    {
        pipeName = null;
        tokenPath = null;
        if (args == null || args.Length != 4) return false;
        for (int i = 0; i < args.Length; i += 2)
        {
            if (String.Equals(args[i], "--pipe-name", StringComparison.Ordinal) && pipeName == null)
                pipeName = args[i + 1];
            else if (String.Equals(args[i], "--token-file", StringComparison.Ordinal) && tokenPath == null)
                tokenPath = args[i + 1];
            else return false;
        }
        return !String.IsNullOrEmpty(pipeName) && !String.IsNullOrEmpty(tokenPath) && Path.IsPathRooted(tokenPath);
    }

    private static string ReadToken(string path)
    {
        FileInfo info = new FileInfo(path);
        if (!info.Exists || info.Length < 64 || info.Length > 66) throw new InvalidDataException();
        string value = File.ReadAllText(path, new UTF8Encoding(false, true));
        value = value.TrimEnd('\r', '\n');
        if (value.Length != 64) throw new InvalidDataException();
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) throw new InvalidDataException();
        }
        return value;
    }

    private static LineRead ReadLine(TextReader reader, int maximum)
    {
        StringBuilder line = new StringBuilder(Math.Min(maximum, 1024));
        bool tooLong = false;
        bool sawAny = false;
        while (true)
        {
            int next = reader.Read();
            if (next < 0)
            {
                if (!sawAny) return null;
                break;
            }
            sawAny = true;
            char c = (char)next;
            if (c == '\n') break;
            if (c == '\r') continue;
            if (line.Length < maximum) line.Append(c);
            else tooLong = true;
        }
        return new LineRead { Text = line.ToString(), TooLong = tooLong };
    }
}
