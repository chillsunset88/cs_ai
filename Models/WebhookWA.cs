public class WebhookWA
{
    public string Event { get; set; }
    public string DeviceId { get; set; }
    public string MsgId { get; set; }
    public string From { get; set; }
    public string Name { get; set; }
    public string Message { get; set; }
    public bool IsFromGroup { get; set; }
    public bool IsFromMe { get; set; }
    public string MessageType { get; set; }
    public string MediaUrl { get; set; }
}
