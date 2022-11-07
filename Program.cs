using FreeRedis;
using WebsocketCluster.Handlers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(provider => {
    var logger = provider.GetService<ILogger<WebSocketChannelHandler>>();
    RedisClient cli = new RedisClient(builder.Configuration["Redis:ConnectionString"]);
    cli.Notice += (s, e) => logger?.LogInformation(e.Log);
    return cli;
});
builder.Services.AddSingleton<WebSocketHandler>().AddSingleton<WebSocketChannelHandler>();
builder.Services.AddControllers();

var app = builder.Build();

var webSocketOptions = new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromMinutes(2)
};

app.UseWebSockets(webSocketOptions);

app.MapGet("/", () => "Hello World!");
app.MapControllers();

app.Run();
