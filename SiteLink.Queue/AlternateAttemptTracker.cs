namespace SiteLink.Queue;

internal sealed class AlternateAttemptTracker<T>
    where T : class
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Attempt> _attempts = new();

    internal Attempt Begin(string userId, T[] candidates)
    {
        if (string.IsNullOrEmpty(userId) || candidates == null || candidates.Length == 0)
            return null;

        Attempt attempt = new(candidates);
        lock (_sync)
            _attempts[userId] = attempt;

        return attempt;
    }

    internal bool TryTake(string userId, T finalCandidate, out T[] candidates)
    {
        candidates = Array.Empty<T>();
        if (string.IsNullOrEmpty(userId) || finalCandidate == null)
            return false;

        lock (_sync)
        {
            if (!_attempts.TryGetValue(userId, out Attempt attempt) ||
                attempt.Candidates.Length == 0 ||
                !ReferenceEquals(attempt.Candidates[^1], finalCandidate))
            {
                return false;
            }

            _attempts.Remove(userId);
            candidates = attempt.Candidates;
            return true;
        }
    }

    internal bool Cancel(string userId, T finalCandidate)
    {
        if (string.IsNullOrEmpty(userId) || finalCandidate == null)
            return false;

        lock (_sync)
        {
            if (!_attempts.TryGetValue(userId, out Attempt attempt) ||
                attempt.Candidates.Length == 0 ||
                !ReferenceEquals(attempt.Candidates[^1], finalCandidate))
            {
                return false;
            }

            _attempts.Remove(userId);
            return true;
        }
    }

    internal void Remove(string userId, Attempt expected = null)
    {
        if (string.IsNullOrEmpty(userId))
            return;

        lock (_sync)
        {
            if (expected != null &&
                (!_attempts.TryGetValue(userId, out Attempt current) ||
                 !ReferenceEquals(current, expected)))
            {
                return;
            }

            _attempts.Remove(userId);
        }
    }

    internal sealed record Attempt(T[] Candidates);
}
