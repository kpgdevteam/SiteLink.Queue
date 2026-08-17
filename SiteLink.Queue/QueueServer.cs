using SiteLink.API.Core;
using SiteLink.API.Models;
using SiteLink.API.Networking;

namespace SiteLink.Queue
{
    public sealed class QueueServer : Server
    {
        public static QueueServer Instance { get; private set; }

        public QueueServer()
            : base(
                "sitelink_queue",
                new ServerSettings
                {
                    Name = "sitelink_queue",
                    DisplayName = "SiteLink Queue",
                    Address = "127.0.0.1",
                    Port = 0,
                    MaxClients = int.MaxValue
                },
                isSimulated: true)
        {
            Instance = this;
        }

        public override bool OnSessionConnecting(Session session)
        {
            return true;
        }

        public override void OnSessionReady(Session session)
        {
            if (!MainClass.TryTakeQueueTarget(
                    session.UserId,
                    out Server target))
            {
                session.Disconnect("Queue destination was not found.");
                return;
            }

            session.World = new QueueWorld(target);
        }
    }
}