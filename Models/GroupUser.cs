using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace WebsocketCluster.Models
{
    public class GroupUser
    {
        public ConcurrentDictionary<string, UserConnection> Groups = new ConcurrentDictionary<string, UserConnection>();
    }
}
