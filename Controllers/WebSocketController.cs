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
    public class WebSocketController : ControllerBase
    {
        private static ConcurrentDictionary<string,WebSocket> _users = new ConcurrentDictionary<string,WebSocket>();
        private static ConcurrentBag<WebSocketClient> _clients = new ConcurrentBag<WebSocketClient>();

        private readonly ILogger<WebSocketController> _logger;
        private readonly WebSocketHandler _socketHandler;

        public WebSocketController(ILogger<WebSocketController> logger, WebSocketHandler socketHandler)
        {
            _logger = logger;
            _socketHandler = socketHandler;
        }

        [HttpGet("/ws")]
        public async Task Get()
        {
            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
                await Echo(webSocket);
            }
            else
            {
                HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
        }

        private static async Task Echo(WebSocket webSocket)
        {
            var buffer = new byte[1024 * 4];
            var receiveResult = await webSocket.ReceiveAsync(
                new ArraySegment<byte>(buffer), CancellationToken.None);

            while (!receiveResult.CloseStatus.HasValue)
            {
                await webSocket.SendAsync(
                    new ArraySegment<byte>(buffer, 0, receiveResult.Count),
                    receiveResult.MessageType,
                    receiveResult.EndOfMessage,
                    CancellationToken.None);

                receiveResult = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), CancellationToken.None);
            }

            await webSocket.CloseAsync(
                receiveResult.CloseStatus.Value,
                receiveResult.CloseStatusDescription,
                CancellationToken.None);
        }

        [HttpGet("/chat")]
        public async Task Chat(string id)
        {
            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
                _clients.Add(new WebSocketClient { ClientId = id, WSocket = socket });

                var buffer = new byte[1024 * 4];
                var receiveResult = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                while (!receiveResult.CloseStatus.HasValue)
                {
                    string msg = Encoding.UTF8.GetString(buffer[0..receiveResult.Count]).TrimEnd('\0');
                    MsgBody msgBody = JsonConvert.DeserializeObject<MsgBody>(msg);
                    byte[] sendMsg = Encoding.UTF8.GetBytes($"user {id} send:{msgBody.Msg}");

                    var tasks = _clients.Where(i=>i.ClientId == msgBody.Id || i.ClientId==id)
                        .Select(client => Task.Run(async()=> await client.WSocket.SendAsync(new ArraySegment<byte>(sendMsg, 0, sendMsg.Length), receiveResult.MessageType, true, CancellationToken.None)));
                    await Task.WhenAll(tasks);

                    receiveResult = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                }

                await socket.CloseAsync(receiveResult.CloseStatus.Value,receiveResult.CloseStatusDescription,CancellationToken.None);
            }
            else
            {
                HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
        }

        [HttpGet("/chatroom")]
        public async Task ChatRoom(string id)
        {
            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                var webSocket = _users.GetOrAdd(id, await HttpContext.WebSockets.AcceptWebSocketAsync());
                var shared = ArrayPool<byte>.Shared;
                byte[] buffer = shared.Rent(1024 * 4);
                var receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                while (webSocket.State == WebSocketState.Open)
                {
                    try
                    {
                        string msg = Encoding.UTF8.GetString(buffer[..receiveResult.Count]).TrimEnd('\0');
                        MsgBody msgBody = JsonConvert.DeserializeObject<MsgBody>(msg);
                        byte[] sendByte = Encoding.UTF8.GetBytes($"user {id} send:{msgBody.Msg}");

                        if (_users.TryGetValue(msgBody.Id, out var targetSocket))
                        {
                            await targetSocket.SendAsync(new ArraySegment<byte>(sendByte, 0, sendByte.Length), receiveResult.MessageType, true, CancellationToken.None);
                        }

                        receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, ex.Message);
                        break;
                    }
                    finally
                    {
                        //shared.Return(buffer);
                    }
                }     
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Socket closed", CancellationToken.None);
                _ = _users.TryRemove(id, out _);
                shared.Return(buffer, true);
            }
            else
            {
                HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
        }

        [HttpGet("/chat/user/{id}")]
        public async Task ChatUser(string id)
        {
            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                _logger.LogInformation($"user:{id}-{Request.HttpContext.Connection.RemoteIpAddress}:{Request.HttpContext.Connection.RemotePort} join");

                var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
                await _socketHandler.Handle(id, webSocket);
            }
            else
            {
                HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
        }

        [HttpGet("/chat/group/{groupId}/{userId}")]
        public async Task ChatGroup(string groupId, string userId)
        {
            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                _logger.LogInformation($"group:{groupId} user:{userId}-{Request.HttpContext.Connection.RemoteIpAddress}:{Request.HttpContext.Connection.RemotePort} join");

                var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
                await _socketHandler.HandleGroup(groupId, userId, webSocket);
            }
            else
            {
                HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
        }

        [HttpGet("/chat/all/{id}")]
        public async Task ChatAll(string id)
        {
            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                _logger.LogInformation($"all user:{id}-{Request.HttpContext.Connection.RemoteIpAddress}:{Request.HttpContext.Connection.RemotePort} join");

                var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
                await _socketHandler.HandleAll(id, webSocket);
            }
            else
            {
                HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
        }
    }
}
