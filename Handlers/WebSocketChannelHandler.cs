using FreeRedis;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using WebsocketCluster.Models;

namespace WebsocketCluster.Handlers
{
    public class WebSocketChannelHandler : IDisposable
    {
        private readonly UserConnection UserConnection = new();
        private readonly ConcurrentDictionary<string, HashSet<string>> GroupUser = new ConcurrentDictionary<string, HashSet<string>>();
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly ConcurrentDictionary<string, IDisposable> _disposables = new();

        private readonly string userPrefix = "user:";
        private readonly string groupPrefix = "group:";
        private readonly string all = "all";

        private readonly ILogger<WebSocketHandler> _logger;
        private readonly RedisClient _redisClient;

        public WebSocketChannelHandler(ILogger<WebSocketHandler> logger, RedisClient redisClient)
        {
            _logger = logger;
            _redisClient = redisClient;
        }

        public async Task HandleChannel(string id, WebSocket webSocket)
        {
            await _lock.WaitAsync();

            _ = UserConnection.GetOrAdd(id, webSocket);

            await SubMsg($"{userPrefix}{id}");
            if (UserConnection.Count == 1)
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
                    ChannelData channelData = JsonConvert.DeserializeObject<ChannelData>(msg);

                    switch (channelData.Method)
                    {
                        case "One":
                            await HandleOne(id, channelData.MsgBody, receiveResult);
                            break;
                        case "UserGroup":
                            await AddUserGroup(id, channelData.Group, webSocket);
                            break;
                        case "Group":
                            await HandleGroup(channelData.Group, id, webSocket, channelData.MsgBody);
                            break;
                        default:
                            await HandleAll(id, channelData.MsgBody);
                            break;
                    }

                    receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, ex.Message);
                    break;
                }
            }

            await webSocket.CloseAsync(receiveResult.CloseStatus.Value, receiveResult.CloseStatusDescription, CancellationToken.None);

            //在群组中移除当前用户
            foreach (var users in GroupUser.Values)
            {
                lock (users)
                {
                    users.Remove(id);
                }
            }

            _ = UserConnection.TryRemove(id, out _);
            _disposables.Remove($"{userPrefix}{id}", out var sub);
            sub?.Dispose();
        }

        private async Task HandleOne(string id, object msg, WebSocketReceiveResult receiveResult)
        {
            MsgBody msgBody = JsonConvert.DeserializeObject<MsgBody>(JsonConvert.SerializeObject(msg));
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
        }

        private async Task AddUserGroup(string user, string group, WebSocket webSocket)
        {
            var currentGroup = GroupUser.GetOrAdd(group, new HashSet<string>());

            lock (currentGroup)
            {
                _ = currentGroup.Add(user);
            }

            if (currentGroup.Count == 1)
            {
                await SubGroupMsg($"{groupPrefix}{group}");
            }

            string addMsg = $"user 【{user}】 add  to group 【{group}】";
            byte[] sendByte = Encoding.UTF8.GetBytes(addMsg);
            await webSocket.SendAsync(new ArraySegment<byte>(sendByte, 0, sendByte.Length), WebSocketMessageType.Text, true, CancellationToken.None);

            ChannelMsgBody channelMsgBody = new ChannelMsgBody { FromId = user, ToId = group, Msg = addMsg };
            _redisClient.Publish($"{groupPrefix}{group}", JsonConvert.SerializeObject(channelMsgBody));
        }

        private async Task HandleGroup(string groupId, string userId, WebSocket webSocket, object msgBody)
        {

            var hasValue = GroupUser.TryGetValue(groupId, out var users);
            if (!hasValue)
            {
                byte[] sendByte = Encoding.UTF8.GetBytes($"group【{groupId}】 not exists");
                await webSocket.SendAsync(new ArraySegment<byte>(sendByte, 0, sendByte.Length), WebSocketMessageType.Text, true, CancellationToken.None);
                return;
            }

            if (!users.Contains(userId))
            {
                byte[] sendByte = Encoding.UTF8.GetBytes($"user 【{userId}】 not in 【{groupId}】");
                await webSocket.SendAsync(new ArraySegment<byte>(sendByte, 0, sendByte.Length), WebSocketMessageType.Text, true, CancellationToken.None);
                return;
            }

            _logger.LogInformation($"group 【{groupId}】 user 【{userId}】 send:{msgBody}");

            ChannelMsgBody channelMsgBody = new ChannelMsgBody { FromId = userId, ToId = groupId, Msg = msgBody.ToString() };
            _redisClient.Publish($"{groupPrefix}{groupId}", JsonConvert.SerializeObject(channelMsgBody));
        }

        private async Task HandleAll(string id, object msgBody)
        {
            _logger.LogInformation($"user {id} send:{msgBody}");

            ChannelMsgBody channelMsgBody = new ChannelMsgBody { FromId = id, Msg = msgBody.ToString() };
            _redisClient.Publish(all, JsonConvert.SerializeObject(channelMsgBody));
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
                    else
                    {
                        _ = UserConnection.TryRemove(msgBody.FromId, out _);
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

                GroupUser.TryGetValue(msgBody.ToId, out var currentGroup);

                foreach (var user in currentGroup)
                {
                    if (user == msgBody.FromId)
                    {
                        continue;
                    }

                    if (UserConnection.TryGetValue(user, out var targetSocket) && targetSocket.State == WebSocketState.Open)
                    {
                        await targetSocket.SendAsync(new ArraySegment<byte>(sendByte, 0, sendByte.Length), WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    else
                    {
                        currentGroup.Remove(user);
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

                foreach (var user in UserConnection)
                {
                    if (user.Key == msgBody.FromId)
                    {
                        continue;
                    }

                    if (user.Value.State == WebSocketState.Open)
                    {
                        await user.Value.SendAsync(new ArraySegment<byte>(sendByte, 0, sendByte.Length), WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    else
                    {
                        _ = UserConnection.TryRemove(user.Key, out _);
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
