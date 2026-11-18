using StreamKit.Core.Events;

namespace StreamKit.TikTok;

/// <summary>
/// Traducción pura de datos de TikTok al dominio. Sin red, sin estado y sin dependencias de la
/// librería: todo lo que entra se puede construir en un test.
/// </summary>
public static class TikTokEventMapper
{
    private const int MaxCount = 100_000;
    private const int MaxMonths = 60;
    private const int MaxViewers = 10_000_000;

    public static ChatEvent? MapChat(TikTokChatMessage message, DateTimeOffset at)
    {
        var viewer = message.User.ToViewer();
        if (viewer.IsEmpty || string.IsNullOrWhiteSpace(message.Message))
            return null;

        return new ChatEvent(at, viewer, message.Message.Trim());
    }

    public static GiftEvent? MapGift(TikTokGiftMessage message, DateTimeOffset at)
    {
        var viewer = message.User.ToViewer();
        if (viewer.IsEmpty)
            return null;

        var count = (int)Math.Clamp(message.ComboCount, 1, MaxCount);
        var diamonds = Math.Max(0, message.UnitDiamonds);
        var name = string.IsNullOrWhiteSpace(message.GiftName) ? "Regalo" : message.GiftName.Trim();
        var giftId = message.GiftId > 0
            ? message.GiftId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : name;

        return new GiftEvent(at, viewer, giftId, name, count, diamonds, message.StreakFinished);
    }

    public static LikeEvent? MapLike(TikTokLikeMessage message, DateTimeOffset at)
    {
        var viewer = message.User.ToViewer();
        if (viewer.IsEmpty)
            return null;

        var count = (int)Math.Clamp(message.Count, 1, MaxCount);
        return new LikeEvent(at, viewer, count);
    }

    public static FollowEvent? MapFollow(TikTokFollowMessage message, DateTimeOffset at)
    {
        var viewer = message.User.ToViewer();
        return viewer.IsEmpty ? null : new FollowEvent(at, viewer);
    }

    public static ShareEvent? MapShare(TikTokShareMessage message, DateTimeOffset at)
    {
        var viewer = message.User.ToViewer();
        return viewer.IsEmpty ? null : new ShareEvent(at, viewer);
    }

    public static JoinEvent? MapJoin(TikTokJoinMessage message, DateTimeOffset at)
    {
        var viewer = message.User.ToViewer();
        return viewer.IsEmpty ? null : new JoinEvent(at, viewer);
    }

    public static SubscribeEvent? MapSubscribe(TikTokSubscribeMessage message, DateTimeOffset at)
    {
        var viewer = message.User.ToViewer();
        if (viewer.IsEmpty)
            return null;

        var months = Math.Clamp(message.Months, 1, MaxMonths);
        return new SubscribeEvent(at, viewer, months);
    }

    public static ViewerCountEvent MapViewers(TikTokViewerCountMessage message, DateTimeOffset at)
        => new(at, (int)Math.Clamp(message.Count, 0, MaxViewers));

    public static StreamEndedEvent MapStreamEnded(DateTimeOffset at) => new(at);
}
