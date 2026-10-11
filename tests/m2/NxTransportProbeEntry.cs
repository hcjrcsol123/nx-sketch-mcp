using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Channels;
using System.Web.Script.Serialization;
using NXOpen;

// NX-loadable transport inventory only. This fixture never accesses Session or model data.
public static class NxTransportProbeEntry
{
    public static void Main(string[] args)
    {
        string reportPath = Environment.GetEnvironmentVariable("NX_SKETCH_MCP_M2_TRANSPORT_REPORT");
        if (String.IsNullOrWhiteSpace(reportPath) || !Path.IsPathRooted(reportPath))
            throw new InvalidOperationException("NX_SKETCH_MCP_M2_TRANSPORT_REPORT must be an absolute report path");

        List<object> channels = new List<object>();
        IChannel[] registered = ChannelServices.RegisteredChannels;
        foreach (IChannel channel in registered)
        {
            Dictionary<string, object> row = new Dictionary<string, object>(StringComparer.Ordinal);
            row.Add("name", channel == null ? null : channel.ChannelName);
            row.Add("type", channel == null ? null : channel.GetType().FullName);
            row.Add("assembly", channel == null ? null : channel.GetType().Assembly.GetName().Name);

            IChannelReceiver receiver = channel as IChannelReceiver;
            if (receiver == null)
            {
                row.Add("is_receiver", false);
                row.Add("urls_for_m2bridge", new string[0]);
                row.Add("channel_uris", new string[0]);
            }
            else
            {
                row.Add("is_receiver", true);
                try
                {
                    string[] urls = receiver.GetUrlsForUri("M2Bridge");
                    row.Add("urls_for_m2bridge", urls ?? new string[0]);
                }
                catch (Exception ex)
                {
                    row.Add("urls_for_m2bridge", new string[0]);
                    row.Add("urls_error_type", ex.GetType().FullName);
                }

                try
                {
                    object channelData = receiver.ChannelData;
                    IChannelDataStore data = channelData as IChannelDataStore;
                    row.Add("channel_data_type", channelData == null ? null : channelData.GetType().FullName);
                    row.Add("channel_uris", data == null || data.ChannelUris == null ? new string[0] : data.ChannelUris);
                }
                catch (Exception ex)
                {
                    row.Add("channel_data_type", null);
                    row.Add("channel_uris", new string[0]);
                    row.Add("channel_data_error_type", ex.GetType().FullName);
                }
            }
            channels.Add(row);
        }

        Dictionary<string, object> report = new Dictionary<string, object>(StringComparer.Ordinal);
        report.Add("captured_utc", DateTime.UtcNow.ToString("o"));
        report.Add("process_id", System.Diagnostics.Process.GetCurrentProcess().Id);
        report.Add("runtime_version", Environment.Version.ToString());
        report.Add("app_domain_id", AppDomain.CurrentDomain.Id);
        report.Add("app_domain_name", AppDomain.CurrentDomain.FriendlyName);
        Type exportedType = RemotingServices.GetServerTypeForUri("M2Bridge");
        report.Add("exported_m2bridge_type", exportedType == null ? null : exportedType.FullName);
        report.Add("registered_channel_count", registered.Length);
        report.Add("channels", channels);

        string fullPath = Path.GetFullPath(reportPath);
        string parent = Path.GetDirectoryName(fullPath);
        if (!String.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        // A follow-up inventory must preserve the first observation.
        if (File.Exists(fullPath))
            fullPath = Path.Combine(parent, Path.GetFileNameWithoutExtension(fullPath) + "-" +
                DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ") + Path.GetExtension(fullPath));
        string json = new JavaScriptSerializer().Serialize(report);
        File.WriteAllText(fullPath, json + "\n", new System.Text.UTF8Encoding(false));
    }

    public static int GetUnloadOption(string dummy)
    {
        return (int)Session.LibraryUnloadOption.Explicitly;
    }
}
