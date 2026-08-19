using System.Diagnostics;

namespace SiteLink.Queue;

internal sealed class PttHoldTracker
{
    internal static readonly TimeSpan HoldDuration = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan ActivityGracePeriod = TimeSpan.FromMilliseconds(250);

    private readonly object _sync = new();
    private readonly Dictionary<string, HoldState> _states = new();
    private readonly Func<long> _getTimestamp;
    private readonly long _timestampFrequency;

    internal PttHoldTracker(Func<long> getTimestamp = null, long timestampFrequency = 0)
    {
        _getTimestamp = getTimestamp ?? Stopwatch.GetTimestamp;
        _timestampFrequency = timestampFrequency > 0
            ? timestampFrequency
            : Stopwatch.Frequency;
    }

    internal HoldUpdate RecordActivity(string userId)
    {
        if (string.IsNullOrEmpty(userId))
            return new HoldUpdate(false, HoldDuration);

        long now = _getTimestamp();

        lock (_sync)
        {
            if (!_states.TryGetValue(userId, out HoldState state) ||
                GetElapsedTime(state.LastActivity, now) > ActivityGracePeriod)
            {
                _states[userId] = new HoldState(now);
                return new HoldUpdate(false, HoldDuration);
            }

            state.LastActivity = now;
            TimeSpan remaining = HoldDuration - GetElapsedTime(state.StartedAt, now);

            if (remaining > TimeSpan.Zero || state.ConnectionTriggered)
                return new HoldUpdate(false, Max(remaining, TimeSpan.Zero));

            state.ConnectionTriggered = true;
            return new HoldUpdate(true, TimeSpan.Zero);
        }
    }

    internal TimeSpan GetRemaining(string userId)
    {
        if (string.IsNullOrEmpty(userId))
            return HoldDuration;

        long now = _getTimestamp();

        lock (_sync)
        {
            if (!_states.TryGetValue(userId, out HoldState state))
                return HoldDuration;

            if (GetElapsedTime(state.LastActivity, now) > ActivityGracePeriod)
            {
                _states.Remove(userId);
                return HoldDuration;
            }

            return Max(HoldDuration - GetElapsedTime(state.StartedAt, now), TimeSpan.Zero);
        }
    }

    internal void Reset(string userId)
    {
        if (string.IsNullOrEmpty(userId))
            return;

        lock (_sync)
            _states.Remove(userId);
    }

    private TimeSpan GetElapsedTime(long start, long end) =>
        TimeSpan.FromSeconds((end - start) / (double)_timestampFrequency);

    private static TimeSpan Max(TimeSpan left, TimeSpan right) =>
        left >= right ? left : right;

    private sealed class HoldState
    {
        internal HoldState(long timestamp)
        {
            StartedAt = timestamp;
            LastActivity = timestamp;
        }

        internal long StartedAt { get; }
        internal long LastActivity { get; set; }
        internal bool ConnectionTriggered { get; set; }
    }
}

internal readonly record struct HoldUpdate(bool ShouldConnect, TimeSpan Remaining);
