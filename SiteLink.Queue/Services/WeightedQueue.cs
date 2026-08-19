namespace SiteLink.Queue.Services;

internal sealed class WeightedQueue
{
    private readonly object _syncRoot = new();
    private readonly List<WeightedQueueEntry> _entries = [];

    public int Count
    {
        get
        {
            lock (_syncRoot)
                return _entries.Count;
        }
    }

    public bool Add(string userId, QueueChannel channel, long sequence)
    {
        lock (_syncRoot)
        {
            if (_entries.Any(entry => entry.UserId == userId))
                return false;

            _entries.Add(new WeightedQueueEntry(userId, channel, sequence));
            return true;
        }
    }

    public void Remove(string userId)
    {
        lock (_syncRoot)
            _entries.RemoveAll(entry => entry.UserId == userId);
    }

    public int GetPosition(string userId)
    {
        lock (_syncRoot)
        {
            WeightedQueueEntry entry = _entries.FirstOrDefault(candidate => candidate.UserId == userId);
            if (entry == null)
                return -1;

            return _entries.Count(candidate =>
                       candidate.Channel.Weight > entry.Channel.Weight ||
                       candidate.Channel.Weight == entry.Channel.Weight && candidate.Sequence < entry.Sequence) + 1;
        }
    }

    public WeightedQueueEntry Peek()
    {
        lock (_syncRoot)
        {
            return _entries
                .OrderByDescending(candidate => candidate.Channel.Weight)
                .ThenBy(candidate => candidate.Sequence)
                .FirstOrDefault();
        }
    }
}

internal sealed record WeightedQueueEntry(string UserId, QueueChannel Channel, long Sequence);
