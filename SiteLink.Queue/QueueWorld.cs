using PlayerRoles;
using SiteLink.API.Core;
using SiteLink.API.Misc;
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

    public DateTime Delay;

    public QueueWorld(Server server) : base("Queue")
    {
        DestroyOnEmpty = true;

        ConnectingTo = server;

        AddWaypoint(new Vector3(0f, -300f, 0f));
    }

    DateTime _delay;
    private readonly Dictionary<string, DateTime> _altConnectDelays = new();

    public override void Update()
    {
        if (_delay > DateTime.Now)
            return;

        foreach (var client in GetClientsSnapshot())
        {
            client.Connection?.AsServer.Hint(BuildQueueText(client), MainClass.Instance.Config.HintDuration);
        }

        _delay = DateTime.Now.AddSeconds(1);
    }

    public void TryConnectToAltServer(Session session)
    {
        if (session?.Connection == null)
            return;

        if (_altConnectDelays.TryGetValue(session.UserId, out DateTime delay) && delay > DateTime.UtcNow)
            return;

        _altConnectDelays[session.UserId] = DateTime.UtcNow.AddSeconds(1);

        if (!TryGetAltServer(out Server altServer))
            return;

        session.Connection.Connect(altServer, true);
    }

    private string BuildQueueText(Session session)
    {
        int position = QueueService.GetPositionInQueue(session, ConnectingTo);
        int queueLength = QueueService.GetQueueLength(ConnectingTo);

        TryGetAltServer(out Server altServer);

        return MainClass.Instance.Translate(
            session,
            translations => translations.QueueText,
            TranslationContext.For(session, ConnectingTo, MainClass.Instance)
                .With("queue_server", ConnectingTo.DisplayName)
                .With("queue_server_name", ConnectingTo.Name)
                .With("queue_position", position)
                .With("queue_position_ordinal", ToOrdinal(position))
                .With("queue_length", queueLength)
                .With("alt_server", altServer?.DisplayName ?? string.Empty)
                .With("alt_server_name", altServer?.Name ?? string.Empty)
                .With("alt_online", altServer?.SessionsCount ?? 0)
                .With("alt_max", altServer?.MaxSessions ?? 0));
    }

    private bool TryGetAltServer(out Server server)
    {
        server = null;

        if (!MainClass.Instance.Config.AltConnectServers.TryGetValue(ConnectingTo.Name, out string serverName))
            return false;

        return !string.IsNullOrWhiteSpace(serverName) && Server.TryGetByName(serverName, out server);
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
        QueueService.AddToQueue(session, ConnectingTo);
        session.SpawnPlayer(new Vector3(0f, -299f, 0f));
    }

    public override void OnUnload(Session session)
    {
        _altConnectDelays.Remove(session.UserId);
        QueueService.RemoveFromQueue(session, ConnectingTo);
    }

    public override void OnObjectsSpawned(Session session)
    {
        session.Connection.AsServer.Role(session.NetworkId, RoleTypeId.Tutorial);
        session.Connection.AsServer.Health(session.NetworkId, 100f);
        session.Connection.AsServer.Seed(350);
    }
}
