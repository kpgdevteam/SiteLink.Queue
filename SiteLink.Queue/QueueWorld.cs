using PlayerRoles;
using SiteLink.API.Core;
using SiteLink.API.Misc;
using SiteLink.API.Models;
using SiteLink.API.Networking;
using SiteLink.API.Networking.Objects;
using SiteLink.API.Translations;
using SiteLink.Queue.Services;
using UnityEngine;

namespace SiteLink.Queue;

public class QueueWorld : World
{
    public WaypointToyObject Waypoint;
    public TextToyObject TextToy;

    public Server ConnectingTo;

    internal QueueChannel Channel { get; }
    internal int PlayerId { get; }

    public DateTime Delay;

    internal QueueWorld(Server server, QueueChannel channel, int playerId) : base("Queue")
    {
        DestroyOnEmpty = true;

        ConnectingTo = server;
        Channel = channel;
        PlayerId = playerId;

        AddWaypoint(new Vector3(0f, -300f, 0f));
    }

    DateTime _delay;
    private readonly PttHoldTracker _pttHoldTracker = new();
    private readonly QueueDepartureGate _departureGate = new();
    private readonly AlternateAttemptTracker<Server> _alternateAttempts = new();

    public override void Update()
    {
        if (_delay > DateTime.Now)
            return;

        foreach (var client in GetClientsSnapshot())
            TrySendQueueHint(client);

        _delay = DateTime.Now.AddSeconds(1);
    }

    public void RecordPttActivity(Session session)
    {
        if (session?.Connection == null)
            return;

        HoldUpdate update = _pttHoldTracker.RecordActivity(session.UserId);
        if (!update.ShouldConnect)
            return;

        if (!TryGetAltServers(out Server[] altServers))
        {
            _pttHoldTracker.Reset(session.UserId);
            return;
        }

        if (!TryTransferToAlternates(session, altServers))
            _pttHoldTracker.Reset(session.UserId);
    }

    internal bool TryTransferTo(Session session, Server target)
    {
        if (session?.Connection == null || target == null)
            return false;

        return _departureGate.TryBeginTransfer(
            session.UserId,
            () =>
            {
                var connection = session.Connection;
                if (connection == null || !ReferenceEquals(connection.Session, session))
                    return false;

                return connection.Connect(target, true);
            });
    }

    public void ChangeTarget(Session session, Server target)
    {
        if (session == null || target == null)
            return;

        _alternateAttempts.Remove(session.UserId);
        _departureGate.Reset(session.UserId);
        _pttHoldTracker.Reset(session.UserId);

        if (ReferenceEquals(ConnectingTo, target))
            return;

        QueueService.RemoveFromQueue(session.UserId, ConnectingTo);
        ConnectingTo = target;
        QueueService.AddToQueue(session, ConnectingTo, Channel, PlayerId);
    }

    private string BuildQueueText(Session session)
    {
        int position = QueueService.GetPositionInQueue(session, ConnectingTo);
        int queueLength = QueueService.GetQueueLength(ConnectingTo);

        bool hasAltServer = TryGetAltServers(out Server[] altServers);
        Server altServer = hasAltServer ? altServers[0] : null;
        string queueText = hasAltServer
            ? Channel.QueueText
            : Channel.QueueTextWithoutAltServer;

        return TranslationManager.Format(
            queueText,
            TranslationContext.For(session, ConnectingTo, MainClass.Instance)
                .With("queue_channel", Channel.DisplayName)
                .With("queue_server", ConnectingTo.DisplayName)
                .With("queue_server_name", ConnectingTo.Name)
                .With("queue_position", position)
                .With("queue_position_ordinal", ToOrdinal(position))
                .With("queue_length", queueLength)
                .With("alt_server", altServer?.DisplayName ?? string.Empty)
                .With("alt_server_name", altServer?.Name ?? string.Empty)
                .With("alt_online", altServer?.SessionsCount ?? 0)
                .With("alt_max", altServer?.MaxSessions ?? 0)
                .With("alt_hold_remaining", FormatHoldRemaining(session)))
            .Format();
    }

    private void TrySendQueueHint(Session session)
    {
        _departureGate.TrySendHint(
            session.UserId,
            () =>
            {
                var connection = session.Connection;
                if (connection == null ||
                    connection.IsSwitchingServers ||
                    !ReferenceEquals(connection.Session, session) ||
                    !SessionManager.Singleton.Slots.TryGetValue(session.UserId, out SessionSlot slot))
                {
                    return QueueSessionState.Inactive;
                }

                lock (slot)
                {
                    if (!ReferenceEquals(slot.Active, session))
                        return QueueSessionState.Inactive;

                    if (slot.Pending != null)
                        return QueueSessionState.Pending;

                    connection = session.Connection;
                    if (connection == null ||
                        connection.IsSwitchingServers ||
                        !ReferenceEquals(connection.Session, session))
                    {
                        return QueueSessionState.Inactive;
                    }

                    connection.AsServer.Hint(
                        BuildQueueText(session),
                        MainClass.Instance.Config.HintDuration);
                    return QueueSessionState.Active;
                }
            });
    }

    private string FormatHoldRemaining(Session session)
    {
        TimeSpan remaining = _pttHoldTracker.GetRemaining(session.UserId);
        int seconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        return seconds == 1 ? "1 second" : $"{seconds} seconds";
    }

    internal bool TrySelectAllFullFallback(Session session, Server finalServer, out Server fallback)
    {
        fallback = null;
        if (session == null || finalServer == null)
            return false;

        if (!_alternateAttempts.TryTake(session.UserId, finalServer, out Server[] candidates))
            return false;

        fallback = AlternateServerBalancer.SelectShortestQueue(candidates);
        return fallback != null;
    }

    internal bool CancelAlternateAttempt(Session session, Server finalServer)
    {
        if (session == null || finalServer == null)
            return false;

        if (!_alternateAttempts.Cancel(session.UserId, finalServer))
            return false;

        _pttHoldTracker.Reset(session.UserId);
        return true;
    }

    private bool TryTransferToAlternates(Session session, Server[] targets)
    {
        if (session?.Connection == null || targets == null || targets.Length == 0)
            return false;

        AlternateAttemptTracker<Server>.Attempt attempt =
            _alternateAttempts.Begin(session.UserId, targets);
        if (attempt == null)
            return false;

        bool started = false;
        try
        {
            started = _departureGate.TryBeginTransfer(
                session.UserId,
                () =>
                {
                    var connection = session.Connection;
                    if (connection == null || !ReferenceEquals(connection.Session, session))
                        return false;

                    return SessionManager.Singleton.CreateOrSwitchSession(
                        connection,
                        targets,
                        silent: true) != null;
                });

            return started;
        }
        finally
        {
            if (!started)
                _alternateAttempts.Remove(session.UserId, attempt);
        }
    }

    private bool TryGetAltServers(out Server[] servers)
    {
        servers = Array.Empty<Server>();

        if (MainClass.Instance?.Config?.AltConnectServers == null ||
            !MainClass.Instance.Config.AltConnectServers.TryGetValue(
                ConnectingTo.Name,
                out ServerNameList configuredNames))
        {
            return false;
        }

        servers = AlternateServerBalancer.ResolveCandidates(configuredNames);
        return servers.Length > 0;
    }

    private static string ToOrdinal(int number)
    {
        int lastTwoDigits = Math.Abs(number) % 100;
        if (lastTwoDigits is >= 11 and <= 13)
            return $"{number}th";

        return (Math.Abs(number) % 10) switch
        {
            1 => $"{number}st",
            2 => $"{number}nd",
            3 => $"{number}rd",
            _ => $"{number}th"
        };
    }

    public override void OnLoad(Session session)
    {
        QueueService.AddToQueue(session, ConnectingTo, Channel, PlayerId);
        session.SpawnPlayer(new Vector3(0f, -299f, 0f));
    }

    public override void OnUnload(Session session)
    {
        _alternateAttempts.Remove(session.UserId);
        _departureGate.Reset(session.UserId);
        _pttHoldTracker.Reset(session.UserId);
        QueueService.RemoveFromQueue(session, ConnectingTo);
    }

    public override void OnObjectsSpawned(Session session)
    {
        session.Connection.AsServer.Role(session.NetworkId, RoleTypeId.Tutorial);
        session.Connection.AsServer.Health(session.NetworkId, 100f);
        session.Connection.AsServer.Seed(350);
    }

}
