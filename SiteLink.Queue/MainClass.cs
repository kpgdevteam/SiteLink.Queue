using Microsoft.Extensions.DependencyInjection;
using Mirror;
using SiteLink.API;
using SiteLink.API.Core;
using SiteLink.API.Events;
using SiteLink.API.Events.Args;
using SiteLink.API.Misc;
using SiteLink.API.Networking;
using SiteLink.API.Plugins;
using SiteLink.API.Structs;
using SiteLink.API.Translations;
using SiteLink.Queue.Services;
using System.Collections.Concurrent;
using KingsPlayground.Shared.Protobuf;

namespace SiteLink.Queue;

public class MainClass : Plugin<Config, Translations>
{
    private static readonly ConcurrentDictionary<string, QueueAdmission> PendingQueueAdmissions = new();

    private readonly ConcurrentDictionary<string, byte> _admissionsInProgress = new();
    private readonly CancellationTokenSource _admissionCancellation = new();
    private PlayerPermissionService _permissionService;

    public static MainClass Instance { get; private set; }

    public QueueServer QueueServer { get; private set; }

    internal static bool TryTakeQueueAdmission(string userId, out QueueAdmission admission)
    {
        return PendingQueueAdmissions.TryRemove(
            userId,
            out admission);
    }

    internal void PublishQueueStatus(QueueStatusUpdate update) =>
        _permissionService?.PublishQueueStatus(update);

    public override string Name { get; } = "Queue";

    public override string Description { get; } = "Adds queue system for servers.";

    public override string Author { get; } = "Killers0992";

    public override Version Version { get; } = new Version(1, 2, 0);

    public override Version ApiVersion { get; } = new Version(SiteLinkAPI.ApiVersionText);
    public override string Repository => "Killers0992/SiteLink.Queue";

    public override void LoadConfig()
    {
        base.LoadConfig();

        if (Config.QueueChannels?.Any(channel => channel?.MigrateLegacyConnectPrompt() == true) == true)
            SaveConfig();
    }

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

        _permissionService = new PlayerPermissionService(() => Config);
        collection.AddSingleton(_permissionService);
        collection.AddHostedService(_ => _permissionService);
        collection.AddHostedService<QueueService>();

        EventManager.Client.ConnectionResponse += OnConnectionResponse;
        EventManager.Client.JoinedServer += OnJoinedServer;
        EventManager.Listener.ListenerRegistered += OnListenerRegistered;

        foreach (Listener listener in Listener.List)
            RegisterVoiceHandler(listener);
    }

    public override void OnUnload()
    {
        _admissionCancellation.Cancel();
        PendingQueueAdmissions.Clear();
        _admissionsInProgress.Clear();

        if (_permissionService != null)
        {
            try
            {
                _permissionService.ShutdownAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                SiteLinkLogger.Error(ex, "Queue");
            }

            _permissionService = null;
        }

        QueueService.Clear();

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

        if (session.Player == null ||
            reader.Remaining < sizeof(byte) + sizeof(byte) + sizeof(ushort))
        {
            return InterceptResult.Drop();
        }

        int speakerId = reader.ReadRecyclablePlayerId().Value;
        reader.ReadByte();
        ushort dataLength = reader.ReadUShort();

        if (speakerId == 0 ||
            speakerId != session.Player.ReferenceHub.PlayerId.Value ||
            dataLength == 0 ||
            dataLength > reader.Remaining)
        {
            return InterceptResult.Drop();
        }

        world.RecordPttActivity(session);
        return InterceptResult.Drop();
    }

    private void OnJoinedServer(SessionJoinedServerEvent ev)
    {
        if (!Config.ServersWithQueue.Contains(ev.Server.Name))
            return;

        QueueService.RemoveFromQueue(ev.Session.UserId, ev.Server);
    }

    private void OnConnectionResponse(ClientConnectionResponseEvent ev)
    {
        Session activeSession = ev.Connection.Session;
        QueueWorld activeQueueWorld = activeSession?.World as QueueWorld;

        if (ev.Response is ServerIsOfflineResponse)
        {
            activeQueueWorld?.CancelAlternateAttempt(activeSession, ev.Server);
            return;
        }

        if (ev.Response is not ServerIsFullResponse)
            return;

        if (activeQueueWorld != null &&
            activeQueueWorld.TrySelectAllFullFallback(activeSession, ev.Server, out Server fallback))
        {
            ev.IsCancelled = true;
            activeQueueWorld.ChangeTarget(activeSession, fallback);

            SiteLinkLogger.Info(
                $"{ev.Connection.Tag} All alternate servers are full; " +
                $"remaining in the shortest queue for (f=yellow){fallback.Name}(f=white).",
                "Queue");

            return;
        }

        if (!Config.ServersWithQueue.Contains(ev.Server.Name))
            return;

        ev.IsCancelled = true;

        if (activeSession?.Server == QueueServer.Instance)
        {
            if (activeQueueWorld != null)
                activeQueueWorld.ChangeTarget(activeSession, ev.Server);

            SiteLinkLogger.Info(
                $"{ev.Connection.Tag} Server " +
                $"(f=yellow){ev.Server.Name}(f=white) is still full; " +
                $"remaining in queue.",
                "Queue");

            return;
        }

        _ = AdmitToQueueAsync(ev.Connection, ev.Server, _admissionCancellation.Token);
    }

    internal static QueueChannel SelectChannel(
        IEnumerable<QueueChannelConfig> channels,
        IReadOnlySet<int> permissions)
    {
        return channels?
            .Where(channel => channel != null &&
                              (!channel.Permission.HasValue || permissions.Contains(channel.Permission.Value)))
            .Select((channel, index) => new { Channel = channel, Index = index })
            .OrderByDescending(candidate => candidate.Channel.Weight)
            .ThenBy(candidate => candidate.Index)
            .Select(candidate => candidate.Channel.Snapshot())
            .FirstOrDefault();
    }

    private async Task AdmitToQueueAsync(
        SiteLink.API.Networking.Connections.RemoteConnection connection,
        Server target,
        CancellationToken cancellationToken)
    {
        string userId = connection.PreAuth.UserId;
        if (!_admissionsInProgress.TryAdd(userId, 0))
            return;

        try
        {
            PlayerLookupResult lookup;
            try
            {
                lookup = await _permissionService.LookupAsync(userId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                SiteLinkLogger.Error(
                    $"Player lookup failed for (f=cyan){userId}(f=white); queue admission was rejected. {ex}",
                    "Queue");

                if (SiteLink.API.Networking.Connections.RemoteConnection.TryGet(userId, out var failedLookupConnection) &&
                    ReferenceEquals(failedLookupConnection, connection) &&
                    !connection.IsDisposed)
                {
                    string message = connection.Session == null
                        ? Translation.QueueLookupFailed
                        : GetTranslation(connection.Session).QueueLookupFailed;
                    connection.Disconnect(message);
                }

                return;
            }

            QueueChannel channel = SelectChannel(Config.QueueChannels, lookup.Permissions);
            if (channel == null)
            {
                string message = connection.Session == null
                    ? Translation.NoEligibleQueueChannel
                    : GetTranslation(connection.Session).NoEligibleQueueChannel;
                connection.Disconnect(message);
                return;
            }

            if (!SiteLink.API.Networking.Connections.RemoteConnection.TryGet(userId, out var current) ||
                !ReferenceEquals(current, connection) ||
                connection.IsDisposed)
            {
                return;
            }

            PendingQueueAdmissions[userId] = new QueueAdmission(target, channel, lookup.PlayerId);
            try
            {
                connection.Connect(QueueServer.Instance, silent: true);
            }
            catch
            {
                PendingQueueAdmissions.TryRemove(userId, out _);
                throw;
            }
        }
        catch (Exception ex)
        {
            SiteLinkLogger.Error(ex, "Queue");
        }
        finally
        {
            _admissionsInProgress.TryRemove(userId, out _);
        }
    }
}

internal sealed record QueueAdmission(Server Target, QueueChannel Channel, int PlayerId);
