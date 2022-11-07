using Newtonsoft.Json.Linq;
using System.Collections;
using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace WebsocketCluster.Models
{
    public class UserConnection : IEnumerable<KeyValuePair<string, WebSocket>>
    {
        private ConcurrentDictionary<string, WebSocket> _users = new ConcurrentDictionary<string, WebSocket>();

        public int Count => _users.Count;

        public WebSocket GetOrAdd(string userId, WebSocket webSocket)
        {
            return _users.GetOrAdd(userId, webSocket);
        }

        public bool TryGetValue(string userId, out WebSocket webSocket)
        {
            return _users.TryGetValue(userId, out webSocket);
        }

        public bool TryRemove(string userId, out WebSocket webSocket)
        {
            return _users.TryRemove(userId, out webSocket);
        }

        public void Clear()
        {
            _users.Clear();
        }

        public IEnumerator<KeyValuePair<string, WebSocket>> GetEnumerator()
        {
            return _users.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return _users.GetEnumerator();
        }
    }
}
