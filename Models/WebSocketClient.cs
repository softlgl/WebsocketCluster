using System.Net.WebSockets;

namespace WebsocketCluster.Models
{
    public class WebSocketClient
    {
        public string ClientId { get; set; }
        public WebSocket WSocket { get; set; }
    }
}