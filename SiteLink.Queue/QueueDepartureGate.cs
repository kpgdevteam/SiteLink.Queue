namespace SiteLink.Queue;

internal sealed class QueueDepartureGate
{
    private readonly object _sync = new();
    private readonly HashSet<string> _departingUsers = new();

    internal bool TryBeginTransfer(string userId, Func<bool> beginTransfer)
    {
        if (string.IsNullOrEmpty(userId) || beginTransfer == null)
            return false;

        lock (_sync)
        {
            if (!_departingUsers.Add(userId))
                return false;

            try
            {
                if (beginTransfer())
                    return true;
            }
            catch
            {
                _departingUsers.Remove(userId);
                throw;
            }

            _departingUsers.Remove(userId);
            return false;
        }
    }

    internal bool TrySendHint(string userId, Func<QueueSessionState> inspectAndSend)
    {
        if (string.IsNullOrEmpty(userId) || inspectAndSend == null)
            return false;

        lock (_sync)
        {
            QueueSessionState state = inspectAndSend();
            if (state != QueueSessionState.Active)
                return false;

            _departingUsers.Remove(userId);
            return true;
        }
    }

    internal void Reset(string userId)
    {
        if (string.IsNullOrEmpty(userId))
            return;

        lock (_sync)
            _departingUsers.Remove(userId);
    }
}

internal enum QueueSessionState
{
    Active,
    Pending,
    Inactive
}
