namespace WebsocketCluster.Models
{
    public class ChannelData
    {
        public string Method { get; set; }
        public string Group { get; set; }
        public object MsgBody { get; set; }
    }
}
