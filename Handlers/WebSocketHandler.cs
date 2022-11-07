using FreeRedis;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using WebsocketCluster.Models;

namespace WebsocketCluster.Handlers
{
    public class WebSocketHandler : IDisposable
    {
        private readonly UserConnection UserConnection = new();
        private readonly UserConnection AllConnection = new();
        private readonly GroupUser GroupUser = new();
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly ConcurrentDictionary<string, IDisposable> _disposables = new();

        private readonly string userPrefix = "user:";
        private readonly string groupPrefix = "group:";
        private readonly string all = "all";

        private readonly ILogger<WebSocketHandler> _logger;
        private readonly RedisClient _redisClient;

        public WebSocketHandler(ILogger<WebSocketHandler> logger, RedisClient redisClient)
        {
            _logger = logger;
            _redisClient = redisClient;
        }

        public async Task Handle(string id, WebSocket webSocket)
        {
            _ = UserConnection.GetOrAdd(id, webSocket);
            await SubMsg($"{userPrefix}{id}");

            var buffer = new byte[1024 * 4];
            var receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            while (webSocket.State == WebSocketState.Open)
            {
                try
                {
                    string msg = Encoding.UTF8.GetString(buffer[..receiveResult.Count]).TrimEnd('\0');
                    MsgBody msgBody = JsonConvert.DeserializeObject<MsgBody>(msg);
                    byte[] sendByte = Encoding.UTF8.GetBytes($"user {id} send:{msgBody.Msg}");
                    _logger.LogInformation($"user {id} send:{msgBody.Msg}");

                    if (UserConnection.TryGetValue(msgBody.Id, out var targetSocket))
                    {
                        if (targetSocket.State == WebSocketState.Open)
                        {
                            await targetSocket.SendAsync(new ArraySegment<byte>(sendByte, 0, sendByte.Length), receiveResult.MessageType, true, CancellationToken.None);
                        }
                    }
                    else
                    {
                        ChannelMsgBody channelMsgBody = new ChannelMsgBody { FromId = id, ToId = msgBody.Id, Msg = msgBody.Msg };
                        _redisClient.Publish($"{userPrefix}{msgBody.Id}", JsonConvert.SerializeObject(channelMsgBody));
                    }

                    receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, ex.Message);
                    break;
                }
            }

            _ = UserConnection.TryRemove(id, out _);
            await webSocket.CloseAsync(receiveResult.CloseStatus.Value, receiveResult.CloseStatusDescription, CancellationToken.None);
            _disposables.TryRemove($"{userPrefix}{id}", out var disposable);
            disposable.Dispose();
        }

        public async Task HandleGroup(string groupId, string userId, WebSocket webSocket)
        {
            await _lock.WaitAsync();

            var currentGroup = GroupUser.Groups.GetOrAdd(groupId, new UserConnection { });
            _ = currentGroup.GetOrAdd(userId, webSocket);
            if (currentGroup.Count == 1)
            {
                await SubGroupMsg($"{groupPrefix}{groupId}");
            }

            _lock.Release();

            var buffer = new byte[1024 * 4];
            var receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            while (webSocket.State == WebSocketState.Open)
            {
                try
                {
                    string msg = Encoding.UTF8.GetString(buffer[..receiveResult.Count]).TrimEnd('\0');
                    _logger.LogInformation($"group 【{groupId}】 user 【{userId}】 send:{msg}");

                    ChannelMsgBody channelMsgBody = new ChannelMsgBody { FromId = userId, ToId = groupId, Msg = msg };
                    _redisClient.Publish($"{groupPrefix}{groupId}", JsonConvert.SerializeObject(channelMsgBody));

                    receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, ex.Message);
                    break;
                }
            }

            _ = currentGroup.TryRemove(userId, out _);
            await webSocket.CloseAsync(receiveResult.CloseStatus.Value, receiveResult.CloseStatusDescription, CancellationToken.None);
        }

        public async Task HandleAll(string id, WebSocket webSocket)
        {
            await _lock.WaitAsync();

            _ = AllConnection.GetOrAdd(id, webSocket);

            if (AllConnection.Count == 1)
            {
                await SubAllMsg(all);
            }

            _lock.Release();

            var buffer = new byte[1024 * 4];
            var receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            while (webSocket.State == WebSocketState.Open)
            {
                try
                {
                    string msg = Encoding.UTF8.GetString(buffer[..receiveResult.Count]).TrimEnd('\0');
                    _logger.LogInformation($"user {id} send:{msg}");

                    ChannelMsgBody channelMsgBody = new ChannelMsgBody { FromId = id, Msg = msg };
                    _redisClient.Publish(all, JsonConvert.SerializeObject(channelMsgBody));

                    receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, ex.Message);
                    break;
                }
            }

            _ = UserConnection.TryRemove(id, out _);
            await webSocket.CloseAsync(receiveResult.CloseStatus.Value, receiveResult.CloseStatusDescription, CancellationToken.None);
            //_disposables.TryRemove($"{userPrefix}{id}", out var disposable);
            //disposable.Dispose();
        }

        private async Task SubMsg(string channel)
        {
            var sub = _redisClient.Subscribe(channel, async (channel, data) =>
            {
                ChannelMsgBody msgBody = JsonConvert.DeserializeObject<ChannelMsgBody>(data.ToString());
                byte[] sendByte = Encoding.UTF8.GetBytes($"user {msgBody.FromId} send:{msgBody.Msg}");
                if (UserConnection.TryGetValue(msgBody.ToId, out var targetSocket))
                {
                    if (targetSocket.State == WebSocketState.Open)
                    {
                        await targetSocket.SendAsync(new ArraySegment<byte>(sendByte, 0, sendByte.Length), WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                }
            });
            _disposables.TryAdd(channel, sub);
        }

        private async Task SubGroupMsg(string channel)
        {
            var sub = _redisClient.Subscribe(channel, async (channel, data) =>
            {
                ChannelMsgBody msgBody = JsonConvert.DeserializeObject<ChannelMsgBody>(data.ToString());
                byte[] sendByte = Encoding.UTF8.GetBytes($"group 【{msgBody.ToId}】 user 【{msgBody.FromId}】 send:{msgBody.Msg}");

                GroupUser.Groups.TryGetValue(msgBody.ToId, out var currentGroup);
                foreach (var user in currentGroup)
                {
                    if (user.Key == msgBody.FromId)
                    {
                        continue;
                    }

                    if (user.Value.State == WebSocketState.Open)
                    {
                        await user.Value.SendAsync(new ArraySegment<byte>(sendByte, 0, sendByte.Length), WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                }
            });
            _disposables.TryAdd(channel, sub);
        }

        private async Task SubAllMsg(string channel)
        {
            var sub = _redisClient.Subscribe(channel, async (channel, data) =>
            {
                ChannelMsgBody msgBody = JsonConvert.DeserializeObject<ChannelMsgBody>(data.ToString());
                byte[] sendByte = Encoding.UTF8.GetBytes($"user 【{msgBody.FromId}】 send all:{msgBody.Msg}");

                foreach (var user in AllConnection)
                {
                    if (user.Key == msgBody.FromId)
                    {
                        continue;
                    }

                    if (user.Value.State == WebSocketState.Open)
                    {
                        await user.Value.SendAsync(new ArraySegment<byte>(sendByte, 0, sendByte.Length), WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                }
            });
            _disposables.TryAdd(channel, sub);
        }


        public void Dispose()
        {
            foreach (var disposable in _disposables)
            {
                disposable.Value.Dispose();
            }

            _disposables.Clear();
        }
    }
}

namespace WebsocketCluster.Models
{
}

namespace WebsocketCluster.Models
{
}