using System.ComponentModel;

namespace SiteLink.Queue;

public class Config
{
    public string[] ServersWithQueue { get; set; } = new[] { "default" };

    public Dictionary<string, string> AltConnectServers { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["default"] = "community"
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

public sealed class QueueChannelConfig
{
    public string DisplayName { get; set; } = "Default";

    public int Weight { get; set; }

    public int? Permission { get; set; }

    [Description("Placeholders: {tag}, {queue_channel}, {queue_server}, {queue_server_name}, {queue_position}, {queue_position_ordinal}, {queue_length}, {alt_server}, {alt_server_name}, {alt_online}, {alt_max}")]
    public string QueueText { get; set; } =
        "<color=orange>{queue_server}</color> <color=white>is full</color>\n" +
        "<color=white>You are </color><color=orange>{queue_position_ordinal}</color><color=white> in the {queue_channel} queue</color>\n\n" +
        "<color=orange>{alt_server}</color> <color=white>has </color><color=orange>{alt_online}/{alt_max}</color><color=white> players online</color>\n" +
        "<color=white>Press </color><color=orange>[ALT]</color><color=white> to connect</color>";

    internal QueueChannel Snapshot() => new(
        string.IsNullOrWhiteSpace(DisplayName) ? "Queue" : DisplayName,
        Weight,
        Permission,
        QueueText ?? string.Empty);
}

internal sealed record QueueChannel(
    string DisplayName,
    int Weight,
    int? Permission,
    string QueueText);
