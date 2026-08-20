using System.ComponentModel;

namespace SiteLink.Queue;

public sealed class Translations
{
    [Description("Returned by {queue_count} when the server queue is empty.")]
    public string QueueCountEmpty { get; set; } = "";

    [Description("Placeholders: {queue_length}. Returned by {queue_count} when players are waiting.")]
    public string QueueCount { get; set; } = "+{queue_length} in queue";

    [Description("Placeholders: {server}, {server_name}, {queue_channel}, {queue_position}")]
    public string AddedToQueueLog { get; set; } =
        "Added (f=cyan){user_id}(f=white) to the (f=yellow){server_name}(f=white) {queue_channel} queue at position (f=green){queue_position}(f=white).";

    [Description("Shown when no queue channel is available to the player.")]
    public string NoEligibleQueueChannel { get; set; } =
        "You do not have permission to join any configured queue channel.";

    [Description("Shown when the queue service cannot resolve the player's database ID.")]
    public string QueueLookupFailed { get; set; } =
        "The queue service could not look up your player account. Please try again.";

    [Description("No placeholders.")]
    public string NullSessionLog { get; set; } =
        "(f=red)Failed to remove a player from the queue because the session was null.(f=white)";
}
