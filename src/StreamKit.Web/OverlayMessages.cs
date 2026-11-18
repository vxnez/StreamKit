using System.Text.Json;
using StreamKit.Core.Alerts;
using StreamKit.Core.Events;

namespace StreamKit.Web;

/// <summary>Contrato JSON con los overlays. v=1 para poder evolucionar sin romper OBS.</summary>
public static class OverlayMessages
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Hello(OverlayChannel channel, int queueDepth) => Serialize(new
    {
        v = 1,
        type = "hello",
        channel = ChannelName(channel),
        queueDepth,
    });

    public static string Alert(AlertItem item) => Serialize(new
    {
        v = 1,
        type = "alert",
        id = item.Id,
        kind = item.Kind.ToString().ToLowerInvariant(),
        durationMs = item.DurationMs,
        count = item.Count,
        text = item.Text,
        ts = item.CreatedAt.ToUnixTimeMilliseconds(),
        payload = item.Data,
    });

    public static string Chat(ChatEvent chat) => Serialize(new
    {
        v = 1,
        type = "chat",
        payload = new
        {
            user = chat.Viewer.UniqueId,
            nickname = chat.Viewer.DisplayName,
            text = chat.Message,
            isModerator = chat.IsModerator,
            isOwner = chat.IsOwner,
            isBot = false,
            isError = false,
        },
    });

    public static string BotMessage(string text, bool isError = false) => Serialize(new
    {
        v = 1,
        type = "chat",
        payload = new
        {
            user = "streamkit",
            nickname = "StreamKit",
            text,
            isModerator = false,
            isOwner = false,
            isBot = true,
            isError,
        },
    });

    public static string Stats(object payload) => Serialize(new
    {
        v = 1,
        type = "stats",
        payload,
    });

    public static string Ping(long timestamp) => Serialize(new
    {
        v = 1,
        type = "ping",
        t = timestamp,
    });

    public static string ChannelName(OverlayChannel channel) => channel switch
    {
        OverlayChannel.Alerts => "alerts",
        OverlayChannel.Chat => "chat",
        OverlayChannel.Stats => "stats",
        _ => "alerts",
    };

    public static OverlayChannel? ParseChannel(string? value) => value?.ToLowerInvariant() switch
    {
        "alerts" => OverlayChannel.Alerts,
        "chat" => OverlayChannel.Chat,
        "stats" => OverlayChannel.Stats,
        _ => null,
    };

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
