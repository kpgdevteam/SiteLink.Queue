using System.ComponentModel;
using System.Globalization;

namespace SiteLink.Queue;

public class Config
{
    public string[] ServersWithQueue { get; set; } = new[] { "default" };

    [Description("Ordered alternate destinations for each queued server. A single scalar server name is also accepted for backward compatibility.")]
    public AlternateServerDictionary AltConnectServers { get; set; } = new()
    {
        ["default"] = ["community"]
    };

    public string GrpcUrl { get; set; } = "https://kingsplayground.fun:50051/";

    public string GrpcKey { get; set; } = string.Empty;

    public int ServerId { get; set; }

    [Description("Seconds to wait before reconnecting to the proxy-server gRPC service.")]
    public double ConnectionRetryCooldown { get; set; } = 5.0;

    [Description("Queue channels are checked by descending weight. A null permission is available to every player.")]
    public List<QueueChannelConfig> QueueChannels { get; set; } =
    [
        new()
        {
            DisplayName = "Reserved Slot",
            Weight = 100,
            Permission = 61
        },
        new()
        {
            DisplayName = "Default",
            Weight = 0,
            Permission = null
        }
    ];

    public float HintDuration { get; set; } = 1.2f;
}

public sealed class AlternateServerDictionary : Dictionary<string, ServerNameList>
{
    public AlternateServerDictionary() : base(StringComparer.OrdinalIgnoreCase)
    {
    }
}

[TypeConverter(typeof(ServerNameListConverter))]
public sealed class ServerNameList : List<string>
{
}

public sealed class ServerNameListConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override object ConvertFrom(
        ITypeDescriptorContext context,
        CultureInfo culture,
        object value)
    {
        if (value is string serverName)
            return new ServerNameList { serverName };

        return base.ConvertFrom(context, culture, value);
    }
}

public sealed class QueueChannelConfig
{
    internal const string LegacyAltConnectPrompt =
        "<color=white>Press </color><color=orange>[ALT]</color><color=white> to connect</color>";

    internal const string LegacyPrimaryConnectPrompt =
        "<color=white>Press </color><color=orange>[Q]</color><color=white> to connect</color>";

    internal const string PttHoldConnectPrompt =
        "<color=white>Hold your PTT key for </color><color=orange>{alt_hold_remaining}</color><color=white> to connect</color>";

    public string DisplayName { get; set; } = "Default";

    public int Weight { get; set; }

    public int? Permission { get; set; }

    [Description("Placeholders: {tag}, {queue_channel}, {queue_server}, {queue_server_name}, {queue_position}, {queue_position_ordinal}, {queue_length}, {alt_server}, {alt_server_name}, {alt_online}, {alt_max}, {alt_hold_remaining}")]
    public string QueueText { get; set; } =
        "<color=orange>{queue_server}</color> <color=white>is full</color>\n" +
        "<color=white>You are </color><color=orange>{queue_position_ordinal}</color><color=white> in the {queue_channel} queue</color>\n\n" +
        "<color=orange>{alt_server}</color> <color=white>has </color><color=orange>{alt_online}/{alt_max}</color><color=white> players online</color>\n" +
        PttHoldConnectPrompt;

    [Description("Used when the queued server has no resolvable alternate server. Uses queue_text when omitted. Placeholders: {tag}, {queue_channel}, {queue_server}, {queue_server_name}, {queue_position}, {queue_position_ordinal}, {queue_length}.")]
    public string QueueTextWithoutAltServer { get; set; }

    internal bool MigrateLegacyConnectPrompt()
    {
        if (string.IsNullOrEmpty(QueueText))
            return false;

        string migrated = QueueText
            .Replace(LegacyAltConnectPrompt, PttHoldConnectPrompt, StringComparison.Ordinal)
            .Replace(LegacyPrimaryConnectPrompt, PttHoldConnectPrompt, StringComparison.Ordinal);

        if (string.Equals(migrated, QueueText, StringComparison.Ordinal))
            return false;

        QueueText = migrated;
        return true;
    }

    internal QueueChannel Snapshot()
    {
        string queueText = QueueText ?? string.Empty;
        return new QueueChannel(
            string.IsNullOrWhiteSpace(DisplayName) ? "Queue" : DisplayName,
            Weight,
            Permission,
            queueText,
            QueueTextWithoutAltServer ?? queueText);
    }
}

internal sealed record QueueChannel(
    string DisplayName,
    int Weight,
    int? Permission,
    string QueueText,
    string QueueTextWithoutAltServer);
