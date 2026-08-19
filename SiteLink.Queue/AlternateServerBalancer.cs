using SiteLink.API.Core;
using SiteLink.Queue.Services;

namespace SiteLink.Queue;

internal static class AlternateServerBalancer
{
    internal static Server[] ResolveCandidates(IEnumerable<string> configuredNames) =>
        ResolveCandidates(
                configuredNames,
                name => Server.TryGetByName(name, out Server server) ? server : null,
                server => server.Name)
            .ToArray();

    internal static IReadOnlyList<T> ResolveCandidates<T>(
        IEnumerable<string> configuredNames,
        Func<string, T> resolve,
        Func<T, string> getName)
        where T : class
    {
        if (configuredNames == null || resolve == null || getName == null)
            return Array.Empty<T>();

        List<T> candidates = new();
        HashSet<string> seenNames = new(StringComparer.OrdinalIgnoreCase);

        foreach (string configuredName in configuredNames)
        {
            if (string.IsNullOrWhiteSpace(configuredName))
                continue;

            T candidate = resolve(configuredName.Trim());
            if (candidate == null)
                continue;

            string resolvedName = getName(candidate);
            if (string.IsNullOrWhiteSpace(resolvedName) || !seenNames.Add(resolvedName))
                continue;

            candidates.Add(candidate);
        }

        return candidates;
    }

    internal static Server SelectShortestQueue(IReadOnlyList<Server> candidates) =>
        SelectShortestQueue(candidates, QueueService.GetQueueLength);

    internal static T SelectShortestQueue<T>(
        IReadOnlyList<T> candidates,
        Func<T, int> getQueueLength)
        where T : class
    {
        if (candidates == null || candidates.Count == 0 || getQueueLength == null)
            return null;

        T selected = null;
        int shortestLength = int.MaxValue;

        foreach (T candidate in candidates)
        {
            if (candidate == null)
                continue;

            int queueLength = getQueueLength(candidate);
            if (selected != null && queueLength >= shortestLength)
                continue;

            selected = candidate;
            shortestLength = queueLength;
        }

        return selected;
    }
}
