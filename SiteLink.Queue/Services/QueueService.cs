using Microsoft.Extensions.Hosting;
using SiteLink.API.Core;
using SiteLink.API.Misc;
using SiteLink.API.Models;
using SiteLink.API.Networking;
using SiteLink.API.Networking.Connections;
using SiteLink.API.Translations;
using System.Collections.Concurrent;

namespace SiteLink.Queue.Services;

public class QueueService : BackgroundService
{
    private static readonly ConcurrentDictionary<Server, WeightedQueue> ServerQueues = new();
    private static readonly ConcurrentDictionary<(Server Server, string UserId), DateTime> NextAttempts = new();
    private static long _nextSequence;

    public static int GetPositionInQueue(Session session, Server server)
    {
        if (session == null || !ServerQueues.TryGetValue(server, out WeightedQueue queue))
            return -1;

        return queue.GetPosition(session.UserId);
    }

    public static int GetQueueLength(Server server)
    {
        if (!ServerQueues.TryGetValue(server, out WeightedQueue queue))
            return 0;

        return queue.Count;
    }

    internal static void AddToQueue(Session session, Server server, QueueChannel channel)
    {
        if (session == null || server == null || channel == null)
            return;

        WeightedQueue queue = ServerQueues.GetOrAdd(server, _ => new WeightedQueue());
        if (!queue.Add(session.UserId, channel, Interlocked.Increment(ref _nextSequence)))
            return;

        int position = GetPositionInQueue(session, server);
        SiteLinkLogger.Info(
            MainClass.Instance.Translate(
                session,
                translations => translations.AddedToQueueLog,
                TranslationContext.For(session, server, MainClass.Instance)
                    .With("queue_channel", channel.DisplayName)
                    .With("queue_position", position)),
            "Queue");
    }

    public static void RemoveFromQueue(Session session, Server server)
    {
        if (session == null)
        {
            SiteLinkLogger.Error(
                MainClass.Instance?.Translation.NullSessionLog ??
                "Failed to remove a player from the queue because the session was null.",
                "Queue");
            return;
        }

        RemoveFromQueue(session.UserId, server);
    }

    internal static void RemoveFromQueue(string userId, Server server)
    {
        if (string.IsNullOrEmpty(userId) || server == null || !ServerQueues.TryGetValue(server, out WeightedQueue queue))
            return;

        queue.Remove(userId);

        NextAttempts.TryRemove((server, userId), out _);
    }

    internal static void Clear()
    {
        ServerQueues.Clear();
        NextAttempts.Clear();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach ((Server server, WeightedQueue queue) in ServerQueues)
                    TryAdvance(server, queue);

                await Task.Delay(500, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SiteLinkLogger.Error(ex, "SiteLink.Queue");
            }
        }
    }

    private static void TryAdvance(Server server, WeightedQueue queue)
    {
        if (server.SessionsCount >= server.MaxSessions)
            return;

        while (queue.Peek() is { } next)
        {
            if (!RemoteConnection.TryGet(next.UserId, out RemoteConnection client))
            {
                RemoveFromQueue(next.UserId, server);
                continue;
            }

            if (SessionManager.Singleton.Slots.TryGetValue(next.UserId, out SessionSlot slot))
            {
                lock (slot)
                {
                    if (slot.Pending != null)
                        return;
                }
            }

            DateTime now = DateTime.UtcNow;
            var attemptKey = (server, next.UserId);
            if (NextAttempts.TryGetValue(attemptKey, out DateTime nextAttempt) && nextAttempt > now)
                return;

            NextAttempts[attemptKey] = now.AddSeconds(3);

            Session activeSession = client.Session;
            if (activeSession?.World is QueueWorld queueWorld)
                queueWorld.TryTransferTo(activeSession, server);
            else
                client.Connect(server, true);

            return;
        }
    }

}
