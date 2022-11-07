using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using WebsocketCluster.Handlers;
using WebsocketCluster.Models;

namespace WebsocketCluster.Controllers
{
    public class WebSocketChannelController : ControllerBase
    {
        private readonly ILogger<WebSocketController> _logger;
        private readonly WebSocketChannelHandler _webSocketChannelHandler;

        public WebSocketChannelController(ILogger<WebSocketController> logger, WebSocketChannelHandler webSocketChannelHandler)
        {
            _logger = logger;
            _webSocketChannelHandler = webSocketChannelHandler;
        }

        [HttpGet("/chat/channel/{id}")]
        public async Task Channel(string id)
        {
            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                _logger.LogInformation($"user:{id}-{Request.HttpContext.Connection.RemoteIpAddress}:{Request.HttpContext.Connection.RemotePort} join");

                var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
                await _webSocketChannelHandler.HandleChannel(id, webSocket);
            }
            else
            {
                HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
        }
    }
}
