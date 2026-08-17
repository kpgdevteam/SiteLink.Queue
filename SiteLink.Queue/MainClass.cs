using Microsoft.Extensions.DependencyInjection;
using Mirror;
using SiteLink.API;
using SiteLink.API.Core;
using SiteLink.API.Events;
using SiteLink.API.Events.Args;
using SiteLink.API.Networking;
using SiteLink.API.Plugins;
using SiteLink.API.Structs;
using SiteLink.API.Translations;
using SiteLink.Queue.Services;
using System.Collections.Concurrent;

namespace SiteLink.Queue;

public class MainClass : Plugin<Config, Translations>
{
    private static readonly ConcurrentDictionary<string, Server> PendingQueueTargets = new();

    public static MainClass Instance { get; private set; }

    public QueueServer QueueServer { get; private set; }

    internal static bool TryTakeQueueTarget(string userId, out Server server)
    {
        return PendingQueueTargets.TryRemove(
            userId,
            out server);
    }

    public override string Name { get; } = "Queue";

    public override string Description { get; } = "Adds queue system for servers.";

    public override string Author { get; } = "Killers0992";

    public override Version Version { get; } = new Version(1, 1, 0);

    public override Version ApiVersion { get; } = new Version(SiteLinkAPI.ApiVersionText);
    public override string Repository => "Killers0992/SiteLink.Queue";

    public override void OnLoad(IServiceCollection collection)
    {
        Instance = this;

        QueueServer = new QueueServer();
        Server.Register(QueueServer);

        PlaceholderRegistry.Register("queue_count", context =>
        {
            if (context.Server == null)
                return string.Empty;

            int count = QueueService.GetQueueLength(context.Server);
            Translations translations = Instance.GetTranslation(context.Session);
            string template = count == 0
                ? translations.QueueCountEmpty
                : translations.QueueCount;

            return TranslationManager.Format(template, context)
                .Add("queue_length", count)
                .Format();
        });

        collection.AddHostedService<QueueService>();

        EventManager.Client.ConnectionResponse += OnConnectionResponse;
        EventManager.Client.JoinedServer += OnJoinedServer;
        EventManager.Listener.ListenerRegistered += OnListenerRegistered;

        foreach (Listener listener in Listener.List)
            RegisterVoiceHandler(listener);
    }

    public override void OnUnload()
    {
        if (QueueServer != null)
        {
            Server.Unregister(QueueServer.Name);
            QueueServer = null;
        }

        EventManager.Client.ConnectionResponse -= OnConnectionResponse;
        EventManager.Client.JoinedServer -= OnJoinedServer;
        EventManager.Listener.ListenerRegistered -= OnListenerRegistered;
        PlaceholderRegistry.Unregister("queue_count");

        Instance = null;
        base.OnUnload();
    }

    private void OnListenerRegistered(ListenerRegisteredEvent ev) => RegisterVoiceHandler(ev.Listener);

    private static void RegisterVoiceHandler(Listener listener) =>
        listener.ClientToServer.Register(NetworkMessages.VoiceMessage, OnVoiceMessage);

    private static InterceptResult OnVoiceMessage(ushort id, NetworkReader reader, ArraySegment<byte> original, Session session)
    {
        if (session.World is not QueueWorld world)
            return InterceptResult.Pass();

        world.TryConnectToAltServer(session);
        return InterceptResult.Drop();
    }

    private void OnJoinedServer(SessionJoinedServerEvent ev)
    {
        if (!Config.ServersWithQueue.Contains(ev.Server.Name))
            return;

        if (!QueueService.ServerQueues.TryGetValue(ev.Server, out List<string> queues))
            return;

        if (!queues.Contains(ev.Session.UserId))
            return;

        queues.Remove(ev.Session.UserId);
    }

    private void OnConnectionResponse(ClientConnectionResponseEvent ev)
    {
        if (ev.Response is not ServerIsFullResponse)
            return;

        if (!Config.ServersWithQueue.Contains(ev.Server.Name))
            return;

        ev.IsCancelled = true;

        PendingQueueTargets[ev.Connection.PreAuth.UserId] = ev.Server;

        ev.Connection.Connect(QueueServer.Instance, silent: true);
    }
}