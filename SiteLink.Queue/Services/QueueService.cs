using Microsoft.Extensions.Hosting;
using SiteLink.API.Core;
using SiteLink.API.Misc;
using SiteLink.API.Models;
using SiteLink.API.Networking;
using SiteLink.API.Networking.Connections;
using System.Collections.Concurrent;

namespace SiteLink.Queue.Services;

public class QueueService : BackgroundService
{
    public static ConcurrentDictionary<Server, List<string>> ServerQueues = new ConcurrentDictionary<Server, List<string>>();
    private static readonly ConcurrentDictionary<string, DateTime> NextAttempts = new();

    public static int GetPositionInQueue(Session session, Server server)
    {
        if (!ServerQueues.TryGetValue(server, out List<string> queues))
            return -1;

        return queues.IndexOf(session.UserId) + 1;
    }

    public static int GetQueueLength(Server server)
    {
        if (!ServerQueues.TryGetValue(server, out List<string> queues))
            return 0;

        return queues.Count;
    }

    public static void AddToQueue(Session session, Server server)
    {
        if (!ServerQueues.TryGetValue(server, out List<string> queues))
        {
            queues = new List<string>();
            ServerQueues.TryAdd(server, queues);
        }

        if (queues.Contains(session.UserId))
            return;

        queues.Add(session.UserId);
    }

    public static void RemoveFromQueue(Session session, Server server)
    {
        if (!ServerQueues.TryGetValue(server, out List<string> queues))
            return;

        if (session == null)
        {
            SiteLinkLogger.Error(
                MainClass.Instance?.Translation.NullSessionLog ??
                "Failed to remove a player from the queue because the session was null.",
                "Queue");
            return;
        }

        queues.Remove(session.UserId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var queue in ServerQueues)
                {
                    // If server is still full dont do anything and skip.
                    if (queue.Key.SessionsCount >= queue.Key.MaxSessions)
                        continue;

                    // If theres no one in queue then skip.
                    if (queue.Value.Count == 0)
                        continue;

                    string nextPlayer = queue.Value[0];

                    if (!RemoteConnection.TryGet(nextPlayer, out RemoteConnection client))
                    {
                        queue.Value.RemoveAt(0);
                        continue;
                    }

                    //
                    // Don't create another pending backend session while
                    // we're already attempting to move this player.
                    //
                    if (SessionManager.Singleton.Slots.TryGetValue(nextPlayer, out SessionSlot slot))
                    {
                        lock (slot)
                        {
                            if (slot.Pending != null)
                                continue;
                        }
                    }

                    string attemptKey = $"{queue.Key.Name}:{nextPlayer}";

                    DateTime now = DateTime.UtcNow;

                    if (NextAttempts.TryGetValue(attemptKey, out DateTime nextAttempt) && nextAttempt > now)
                    {
                        continue;
                    }

                    NextAttempts[attemptKey] = now.AddSeconds(3);

                    client.Connect(queue.Key, true);
                }

                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                SiteLinkLogger.Error(ex, "SiteLink.Queue");
            }
        }
    }
}
